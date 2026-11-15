using System.IO;
using System.Windows;
using System.Windows.Threading;
using BlackPearl.Core;

namespace BlackPearl.Desktop;

// A room has no host. One member at a time holds the streaming role: their app plays its own file and keeps the room's
// playback state, and everyone else follows it. RoomClient calls that member the host (Identity.Host). The role moves to
// whoever starts a video for the room, and passes to someone else when its holder leaves.
public partial class MainWindow
{
    // Whoever is playing the video, or holds the role while nothing plays.
    private string StreamerName => room?.Snapshot?.People.FirstOrDefault(person => person.IsHost)?.Name ?? "Someone";
    private string StreamCaption(RoomClient client) => client.Identity?.Host == true ? "You're streaming" : "Streaming from " + StreamerName;
    // The member whose playback state the player follows. A different one starts its revisions over.
    private string? followedStreamer;

    // Starting a video replaces whatever someone else is playing for the room, so ask first.
    private bool ConfirmTakeOver()
    {
        if (room is not { Identity.Host: false } client || client.Snapshot is not { Media: { } playing } snapshot || snapshot.Playback?.Stop?.Reason == "ended") return true;
        return Dialogs.Confirm(this, "Play yours for the room?", $"{StreamerName} is playing {playing.Title}. Starting your video replaces it for everyone.", "Play mine", "Keep watching");
    }
    // Library and queue pages: playing something of your own in a room where someone else holds the streaming role takes
    // it over. Everything after this runs as the streamer.
    private async Task<bool> BecomeStreamer()
    {
        if (room is not { Identity.Host: false } client) return true;
        if (!client.IsConnected) { SetStatus("You're disconnected from the room. Rejoin it to play something there."); return false; }
        if (!ConfirmTakeOver()) return false;
        try { await client.BecomeHostAsync(lifetime.Token); }
        catch (IOException ex) { SetStatus(ex.Message); return false; }
        return room == client && client.Identity?.Host == true;
    }
    // Plays one of this library's videos for the room. Anyone in the room can; someone who isn't streaming takes the
    // streaming role with it.
    private async Task StartHostedVideo(MediaItem item)
    {
        var client = room!;
        var prepared = RoomClient.PrepareHostedFile(item.Path, PlaybackTitle(item));
        var previous = playingItem;
        // Set before the room answers: its new state can arrive first, and loading the video reads this.
        playingItem = item;
        try { await client.StartStreamAsync(prepared, true, ct: lifetime.Token); }
        catch { if (room == client && playingItem == item) playingItem = previous; throw; }
        // Keep the computer awake while it plays for other people.
        if (room == client) SetThreadExecutionState(0x80000003);
    }

    // The streaming role moved to this app, or away from it.
    private void OnRoleChanged(RoomClient client, bool mine)
    {
        ResetRoomPlaybackTracking();
        // Queues shared by, or with, the previous arrangement no longer apply.
        ClearHostQueues();
        if (mine)
        {
            // Nobody else's video is loaded any more; this app's own queue and last session are the room's now.
            if (ConnectedSavedRoom is { } saved) PublishRoomSession(client, saved);
            ShareQueues();
            try { client.Send(new("queue", Data: Wire.Serialize(queue.Select(PlaybackTitle).Take(150).ToArray()))); } catch (IOException) { }
        }
        else
        {
            // Someone else's video replaces this app's. Keep where its own stopped.
            SavePlaybackPosition(handingOver: true);
            if (upNextChoice is not null) EndUpNext(false);
            playingItem = null; queuePosition = -1;
            SetThreadExecutionState(0x80000000);
        }
        // Another member's library is no longer the one being browsed.
        LeaveStreamerLibrary();
        RefreshPeople(); UpdateSessionControls(); RefreshSavedRooms(); RefreshVoice(); UpdateRoomPlaybackStatus();
    }
    private void ResetRoomPlaybackTracking()
    {
        target = null; appliedRevision = -1; appliedGeneration = readyGeneration = -1;
        playbackPause.Clear(); playbackIntent.Clear(); playbackRecovery.Clear();
    }

    // The room's video is gone: its streamer left, or handed the role over without a video. A handover is followed by
    // the next video within a moment, so the picture is held briefly before the player empties.
    private readonly DispatcherTimer streamEndTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private void InitializeRoomStreaming() => streamEndTimer.Tick += (_, _) => { streamEndTimer.Stop(); if (room is { } client && client.Snapshot?.Media is null && loadedMedia is null) _ = Guard(() => EmptyRoomPlayer(client)); };
    private void RoomVideoEnded()
    {
        if (loadedMedia is null && !ready) return;
        ResetRoomPlaybackTracking();
        player?.SetPause(true);
        loadedMedia = null; ready = false; ++loadGeneration;
        mediaLoadCancellation?.Cancel(); bridge?.CancelPendingReads();
        streamEndTimer.Stop(); streamEndTimer.Start();
        UpdateSessionControls(); UpdateRoomPlaybackStatus();
    }
    private async Task EmptyRoomPlayer(RoomClient client)
    {
        await loading.WaitAsync();
        try
        {
            if (room != client || client.Snapshot?.Media is not null || loadedMedia is not null) return;
            var oldBridge = bridge; bridge = null;
            oldBridge?.CancelPendingReads();
            await Task.WhenAll(StopNativePlayback(), oldBridge is null ? Task.CompletedTask : oldBridge.DisposeAsync().AsTask());
            if (room != client || client.Snapshot?.Media is not null) return;
            ResetRoomTracks();
            if (pictureInPicture) SetPictureInPicture(false, openPlayer: false);
            SetAudioPlayback(false);
            PlayerEmpty.Visibility = Visibility.Visible;
            RefreshPlayerEmpty();
            RoomHeading.Text = client.Snapshot?.Name ?? RoomHeading.Text;
            UpdateSystemMediaControls(); UpdateSessionControls(); UpdateRoomPlaybackStatus();
        }
        finally { loading.Release(); }
    }
    // What the empty player says: in a room, anyone can start something for everyone.
    private void RefreshPlayerEmpty()
    {
        if (PlayerEmptyTitle is null) return;
        var inRoom = room?.IsConnected == true;
        PlayerEmptyTitle.Text = inRoom ? "Nothing is playing in this room" : "Choose a video to play";
        PlayerEmptyHint.Text = inRoom ? "Pick a video from your library and choose Watch together. Everyone here watches it with you." : "";
        PlayerEmptyHint.Visibility = inRoom ? Visibility.Visible : Visibility.Collapsed;
    }

    // In a room, Library, Home and Queues show this app's own unless the user chose to look through the streamer's,
    // which that person allows per room.
    private bool browsingStreamerLibrary;
    private bool CanBrowseStreamerLibrary => room is { IsConnected: true, Identity.Host: false } client && client.DirectControlsReady.IsCompletedSuccessfully && client.Snapshot?.LibraryBrowsing == true;
    private void BrowseStreamerLibrary(object sender, RoutedEventArgs e)
    {
        if (browsingStreamerLibrary) { LeaveStreamerLibrary(); return; }
        if (!CanBrowseStreamerLibrary) return;
        browsingStreamerLibrary = true;
        selectedCards.Clear();
        guestBrowse = new(); guestLibraryNotice = null; guestCurrentBrowse = null; guestLibraryCards.Clear(); guestLibraryCache.Clear();
        if (currentPage is not ("Library" or "Home")) ShowPage("Library");
        RefreshSharedLibrary(); RefreshSavedItems();
        if (currentPage == "Library") FilterHostLibrary(); else RefreshHome();
    }
    private void LeaveStreamerLibrary()
    {
        if (!browsingStreamerLibrary) { RefreshSharedLibrary(); return; }
        ReleaseHostLibrary();
        browsingStreamerLibrary = false;
        guestLibraryCache.Clear(); guestCurrentBrowse = null; guestLibraryNotice = null;
        // The streamer's title has no page in this app's own library.
        if (currentPage == "Details") ShowPage("Library");
        RefreshSharedLibrary(); RefreshSavedItems();
        if (currentPage == "Library") FilterLibrary(); else if (currentPage == "Home") RefreshHome();
    }
}
