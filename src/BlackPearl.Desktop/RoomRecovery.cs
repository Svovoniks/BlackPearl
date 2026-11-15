using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BlackPearl.Core;

namespace BlackPearl.Desktop;

public partial class MainWindow
{
    private CancellationTokenSource? roomReconnect;
    private Action? disconnectToast;
    private readonly Dictionary<string, CancellationTokenSource> peerNotices = [];
    private readonly PlaybackRecovery playbackRecovery = new();

    private void BeginBufferedRecovery()
    {
        var continuing = ready && player?.IsPlaying == true && target?.Playing == true &&
            !playbackPause.Pending && !seeking && pendingSeek is null && !buffering.IsActive;
        if (playbackRecovery.Begin(continuing)) player?.SetPause(true);
        else ApplyPlaybackRate(1);
        appliedRevision = readyGeneration = -1;
    }

    private long? readAheadMediaBytes;
    // The room's video as it downloads. It outlives the bridge, so reopening the same video after a reconnect keeps
    // what was buffered.
    private StreamCache? streamCache;
    private static string StreamCacheDirectory => Path.Combine(Path.GetTempPath(), "BlackPearl", "stream-cache");
    private StreamCache? StreamCacheFor(SharedMedia media)
    {
        if (streamCache?.Matches(media) == true) return streamCache;
        CloseStreamCache();
        return streamCache = StreamCache.TryCreate(StreamCacheDirectory, media);
    }
    private void CloseStreamCache() { streamCache?.Dispose(); streamCache = null; }
    // A reconnect keeps the buffered video for the room's video to continue from. When the room it returns to plays
    // something else or nothing, no video load or video end comes along to close it, so it is closed here, once whoever
    // is streaming has said what the room is playing. The room service's first list of members says nothing about that.
    private async Task CloseUnusedStreamCache(RoomClient client)
    {
        try { await client.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(30), lifetime.Token); }
        catch (TimeoutException) { }
        // The connection failed again before anyone said what the room plays: the next reconnect decides.
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { return; }
        if (room == client && streamCache is { } kept && (client.Identity?.Host == true || client.Snapshot?.Media is not { } media || !kept.Matches(media))) CloseStreamCache();
    }
    // Begin read-ahead once the first frames are playing, so it never competes with startup for the streamer's
    // bandwidth. From then on the rest of the video downloads continuously, also while paused.
    // How far the room's video is held ahead of the position, drawn on the timeline in a lighter grey. A video played
    // from this PC has nothing to buffer and draws none.
    private void RefreshBufferedBar()
    {
        var length = bridge is not null && player is not null ? player.Length : 0;
        var buffered = length > 0 ? Math.Clamp((double)bridge!.BufferedUntilMs(player!.Time, length) / length, 0, 1) : 0;
        if (Math.Abs(SliderBuffer.GetFraction(Timeline) - buffered) > 0.0005 || buffered is 0 or 1) SliderBuffer.SetFraction(Timeline, buffered);
    }

    private void StartReadAhead()
    {
        if (bridge is null || readAheadMediaBytes is not { } mediaBytes || !ready || player is not { } native ||
            native.State == LibVLCSharp.Shared.VLCState.Buffering || buffering.IsActive) return;
        // The window only bounds the buffer when it has to be held in memory, and that needs the video's duration.
        var bytes = StreamingBufferOptions.ReadAheadBytes(mediaBytes, native.Length);
        if (bytes == 0 && bridge.BuffersWholeVideo) bytes = StreamingBufferOptions.MaximumReadAheadBytes;
        if (bytes == 0) return;
        readAheadMediaBytes = null;
        bridge.SetReadAhead(bytes);
        PlaybackDiagnostics.Record("read-ahead", new { wholeVideo = bridge.BuffersWholeVideo, memoryWindowBytes = bytes });
    }

    private void PollBufferedRecovery()
    {
        if (room?.IsPeerRecovering != true || player is null) return;
        if (playbackRecovery.ShouldPause(target?.Playing == true && !playbackPause.Pending,
            player.State is LibVLCSharp.Shared.VLCState.Buffering or LibVLCSharp.Shared.VLCState.Error or LibVLCSharp.Shared.VLCState.Ended)) player.SetPause(true);
    }

    private void CancelPeerNotice(string? id)
    {
        if (id is not null && peerNotices.Remove(id, out var pending)) pending.Cancel();
    }
    private void CancelPeerNotices()
    {
        var pendingNotices = peerNotices.Values.ToArray();
        peerNotices.Clear();
        foreach (var pending in pendingNotices) pending.Cancel();
    }
    private async Task NotifyPeerDeparture(RoomClient client, WireMessage message)
    {
        var id = message.Sender ?? "participant";
        CancelPeerNotice(id);
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        peerNotices[id] = pending;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), pending.Token);
            if (room == client && !closing) NotifyDisconnected(message.Text ?? "A participant");
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { }
        finally { if (peerNotices.GetValueOrDefault(id) == pending) peerNotices.Remove(id); }
    }

    private void NotifyDisconnected(string name)
    {
        System.Media.SystemSounds.Exclamation.Play();
        disconnectToast?.Invoke();
        disconnectToast = InAppDialog.Toast(this, name + " disconnected", TimeSpan.FromSeconds(7));
    }

    private async Task ReconnectRoomAsync(RoomClient previous)
    {
        if (roomReconnect is not null || closing || previous.Identity is not { } identity || previous.ServerAddress is not { } server) return;
        // Re-entering uses the member's saved key for the room.
        using var retry = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        roomReconnect = retry;
        var name = SavedDisplayName;
        var roomName = previous.Snapshot?.Name;
        var source = previous.HostedMedia as ILocalMediaSource;
        // Disconnect clears the playing video; restore it so watch progress and
        // the episode controls keep working after the room reconnects.
        var resumedItem = playingItem;
        var playback = previous.Snapshot?.Playback;
        var position = playback is null ? 0 : SyncMath.TargetPosition(playback, previous.ServerNowMs);
        var attempt = 0;
        SetStatus("Connection interrupted · Reconnecting…");
        try
        {
            while (!retry.IsCancellationRequested)
            {
                RoomSubtitle.Text = "Connection interrupted · Reconnecting…";
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 * Math.Pow(2, Math.Min(attempt++, 4)))), retry.Token);
                await Disconnect(reconnecting: true);
                retry.Token.ThrowIfCancellationRequested();
                var candidate = NewRoom();
                try
                {
                    await candidate.ConnectAsync(server, name, identity.Room, identity.HostKey, roomName: identity.Owner ? roomName : null);
                    if (retry.IsCancellationRequested || room != candidate) { await candidate.DisposeAsync(); return; }
                    RememberCurrentRoom();
                    if (ConnectedSavedRoom is { } saved) PublishRoomSession(candidate, saved);
                    RoomSubtitle.Text = "Reconnected";
                    _ = CloseUnusedStreamCache(candidate);
                    // This app was playing for the room: carry on from where it stopped, unless someone else started a
                    // video meanwhile. Theirs stays.
                    if (identity.Host && source is not null && File.Exists(source.LocalPath))
                    {
                        try { await candidate.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(8), retry.Token); }
                        catch (Exception ex) when (ex is TimeoutException or IOException) { }
                        if (room == candidate && candidate.Snapshot?.Media is null)
                        {
                            if (resumedItem is not null) playingItem = items.FirstOrDefault(item => item.Id == resumedItem.Id) ?? resumedItem;
                            await candidate.StartStreamAsync(RoomClient.PrepareHostedFile(source.LocalPath, source.Media.Title), playback?.Playing == true, position, retry.Token);
                        }
                    }
                    SetStatus("Room connection restored."); UpdateSessionControls(); return;
                }
                // Removed from the room, or the room is gone: trying again won't change it.
                catch (RoomRejectedException ex)
                {
                    if (room == candidate) await Disconnect(reconnecting: true); else await candidate.DisposeAsync();
                    await Disconnect();
                    SetStatus(ex.Message); RoomSubtitle.Text = ex.Message; RefreshSavedRooms(); return;
                }
                // A health-check or connect timeout is another failed attempt, not a reason to stop retrying.
                catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or System.Net.WebSockets.WebSocketException ||
                    ex is OperationCanceledException && !retry.IsCancellationRequested)
                {
                    if (room == candidate) await Disconnect(reconnecting: true); else await candidate.DisposeAsync();
                    if (!retry.IsCancellationRequested)
                        SetStatus("Reconnecting… " + (ex is OperationCanceledException ? "The room service did not respond in time." : ex.Message));
                }
            }
        }
        catch (OperationCanceledException) when (retry.IsCancellationRequested) { }
        finally { if (roomReconnect == retry) roomReconnect = null; }
    }
}
