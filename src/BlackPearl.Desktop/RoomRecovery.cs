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
    // The room's video as it downloads. It outlives the bridge, so reopening the same video keeps what was buffered:
    // after this app reconnects to the room, and after whoever plays the video reconnects and starts it again.
    private StreamCache? streamCache;
    private static string StreamCacheDirectory => Path.Combine(Path.GetTempPath(), "BlackPearl", "stream-cache");
    // The same for a converted stream: its pieces as they download, and the stream they belong to. A stream's pieces
    // never change, so they are good for as long as the room offers that stream.
    private (string StreamId, SegmentStore Store)? streamPieces;
    private StreamCache? StreamCacheFor(SharedMedia media)
    {
        if (streamCache?.Matches(media) == true) return streamCache;
        CloseFileCache();
        return streamCache = StreamCache.TryCreate(StreamCacheDirectory, media);
    }
    private SegmentStore? StreamPiecesFor(RoomStream stream)
    {
        if (streamPieces is { } kept && kept.StreamId == stream.Id && kept.Store.Segments == stream.Segments) return kept.Store;
        CloseStreamPieces();
        var store = SegmentStore.TryCreate(StreamCacheDirectory, stream.Segments);
        if (store is not null) streamPieces = (stream.Id, store);
        return store;
    }
    private void CloseFileCache() { streamCache?.Dispose(); streamCache = null; }
    // Closing the file deletes it, which takes long enough for gigabytes that it happens on another thread.
    private void CloseStreamPieces() { if (streamPieces is { } kept) { streamPieces = null; _ = Task.Run(kept.Store.Dispose); } }
    private void CloseStreamCache() { CloseFileCache(); CloseStreamPieces(); }
    // A buffer the player is not reading from waits this long for its video to come back, and is then closed. Whoever
    // plays the video takes a few seconds to return from a lost connection, and up to a minute or two when the first
    // attempts fail; the room's video is gone meanwhile, and a streamer who starts it again offers the same stream.
    private int bufferGraceMs = 120000;
    private long fileCacheIdleSince, streamPiecesIdleSince;
    // Runs with every playback tick. The time this app spends reconnecting to the room itself does not count: what it
    // returns to decides, as it would have before the connection was lost.
    private void CloseIdleBuffers()
    {
        if (roomReconnect is not null) { fileCacheIdleSince = streamPiecesIdleSince = 0; return; }
        var now = Environment.TickCount64;
        // Null while nothing from the room is open.
        var playsStream = bridge?.PlaysConvertedStream;
        if (streamCache is null || playsStream == false) fileCacheIdleSince = 0;
        else if (fileCacheIdleSince == 0) fileCacheIdleSince = now;
        else if (now - fileCacheIdleSince >= bufferGraceMs) { fileCacheIdleSince = 0; CloseFileCache(); }
        if (streamPieces is null || playsStream == true) streamPiecesIdleSince = 0;
        else if (streamPiecesIdleSince == 0) streamPiecesIdleSince = now;
        else if (now - streamPiecesIdleSince >= bufferGraceMs) { streamPiecesIdleSince = 0; CloseStreamPieces(); }
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
                    // This app was playing for the room: carry on from where it stopped, unless someone else started a
                    // video meanwhile. Theirs stays.
                    if (identity.Host && source is not null && File.Exists(source.LocalPath))
                    {
                        try { await candidate.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(8), retry.Token); }
                        catch (Exception ex) when (ex is TimeoutException or IOException) { }
                        if (room == candidate && candidate.Snapshot?.Media is null)
                        {
                            if (resumedItem is not null) playingItem = items.FirstOrDefault(item => item.Id == resumedItem.Id) ?? resumedItem;
                            // The same video under the same id: a conversion kept through the reconnect is still its stream.
                            await candidate.StartStreamAsync(RoomClient.PrepareHostedFile(source.LocalPath, source.Media.Title, source.Media.Id), playback?.Playing == true, position, retry.Token);
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
