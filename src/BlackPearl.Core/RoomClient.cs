using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;

namespace BlackPearl.Core;

// The room service turned the entry down for a reason that trying again won't change.
public sealed class RoomRejectedException(string message) : IOException(message);

// One member's side of a room. The room service says who is in the room and which member holds the streaming role.
// Every member keeps a direct connection to every other: voice goes straight to each listener, and room state, controls
// and video go to and from whoever holds the streaming role. That member is called the host in this code. Starting a
// video takes the role, and it passes to someone else when its holder leaves.
public sealed partial class RoomClient : IAsyncDisposable
{
    private readonly LibraryStore? settings;
    private readonly string diagnosticId = Guid.NewGuid().ToString("N")[..8];
    public RoomClient(LibraryStore? settings = null) => this.settings = settings;
    private string RoomSetting(string code, string field) => "room:" + ServerAddress + ":" + code.ToUpperInvariant() + ":" + field;
    private IRoomConnection? connection;
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationTokenSource discoveryLifetime = new();
    private readonly Channel<WireMessage> outgoing = Channel.CreateBounded<WireMessage>(128);
    private sealed record RoomEvent(string Kind, string? Peer = null, WireMessage? Message = null, PeerTransport? Transport = null, HostedFileSelection? Selection = null, bool Play = false, IMediaSource? Subtitle = null, string? MediaId = null, long PositionMs = 0, PeerInbox? Inbox = null, Action? Then = null, TaskCompletionSource? Completion = null);
    private readonly Channel<RoomEvent> events = Channel.CreateBounded<RoomEvent>(256);
    // Bounds each peer's share of the control queue before its messages are enqueued,
    // so one peer cannot overflow the queue or starve local and discovery events.
    // The streamer sends bursts of state, library pages and artwork, so its messages are queued without a rate budget.
    private sealed class PeerInbox(Func<bool> streamer)
    {
        private const int Capacity = 200, RefillPerSecond = 100, FloodDrops = 200;
        private double tokens = Capacity;
        private long refilled = Environment.TickCount64;
        private int queued, dropped;
        public bool Admit(out int drops, out bool budgeted)
        {
            budgeted = !streamer();
            lock (this)
            {
                var now = Environment.TickCount64;
                tokens = Math.Min(Capacity, tokens + (now - refilled) * RefillPerSecond / 1000.0); refilled = now;
                if (queued < (budgeted ? 48 : 160) && (!budgeted || tokens >= 1)) { tokens--; queued++; drops = dropped = 0; return true; }
                drops = ++dropped; return false;
            }
        }
        public static bool Flooding(int drops, bool budgeted) => budgeted && drops == FloodDrops;
        public void Release() { lock (this) queued--; }
    }
    private readonly ConcurrentQueue<(string Peer, PeerTransport Transport)> flooded = new();
    private Task offloaded = Task.CompletedTask;
    private int offloadedCount;
    private readonly ConcurrentDictionary<string, PeerTransport> peers = new();
    private readonly ConcurrentDictionary<string, byte> negotiated = new();
    private TaskCompletionSource directConnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Set while this member holds the streaming role.
    private HostRoomCoordinator? coordinator;
    // Everyone in the room, from the room service.
    private volatile Participant[] roster = [];
    // The member holding the streaming role, and the room service's count of how often it has changed hands.
    private volatile string? hostPeer;
    private long epoch = -1;
    // One direct connection per other member. The member whose id sorts first makes the offer; each attempt has a
    // number, so signals from an abandoned attempt are ignored.
    private sealed class PeerLink { public long Generation, RetryAt, HeardAt, CreatedAt; }
    private readonly Dictionary<string, PeerLink> links = [];
    private long linkGeneration = Wire.Now;
    private string displayName = "";
    private string? roomName;
    private long stateSequence, receivedSequence;
    private int discoveryFailed;
    private int peerRecovering;
    private Task? readTask, sendTask, controlTask, pingTask;
    private int disposed;
    private readonly TaskCompletionSource<Welcome> connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsConnected => Identity is not null && !lifetime.IsCancellationRequested;
    public string? ServerAddress { get; private set; }
    // Identity.Host follows the streaming role: true while this member holds it.
    public Welcome? Identity { get; private set; }
    public RoomSnapshot? Snapshot { get; private set; }
    // Completes once the room's state is known: at once for the streamer, and for the others when its state arrives.
    public Task DirectControlsReady => directConnected.Task;
    public bool DiscoveryOnline => Volatile.Read(ref discoveryFailed) == 0 && connection is not null;
    public bool IsPeerRecovering => Volatile.Read(ref peerRecovering) != 0;
    // Everyone who has entered this room, here or not, and for its creator the people it removed.
    public AdmittedGuest[] Members { get; private set; } = [];
    public AdmittedGuest[] Removed { get; private set; } = [];
    // Everyone in the room right now as the room service lists them, with their member ids.
    public Participant[] Roster => roster;
    // Why the room service ended this connection for good: removed from the room, or entered from another window.
    public string? ClosedReason { get; private set; }
    public IMediaSource? HostedMedia { get; set; }
    // The converted copy of the hosted video that viewers are offered instead of the file, if any.
    public StreamConverter? HostedStream { get; set; }
    // What this app accepts from viewers while it plays the room's video, told to them with the room's state.
    private const int ReceiveRateFeature = 1, HostFeatures = ReceiveRateFeature;
    private int hostFeatures;
    private readonly ConcurrentDictionary<string, (int Kbps, long At)> receiveRates = new();
    // How fast the room's video reaches this viewer, measured while the app was asking for more than arrived.
    public (int Kbps, long AgeMs)? ReceiveRate => Identity?.Host == false && hostPeer is { } playing && peers.TryGetValue(playing, out var source) ? source.Received.Rate : null;
    // For whoever plays the video: the slowest viewer's connection among those measured within maximumAgeMs, by the
    // viewers' own reports, and whether anyone else was measured too.
    public (string Name, int Kbps, bool Others)? SlowestReceiveRate(long maximumAgeMs)
    {
        if (Identity?.Host != true || Snapshot is not { } snapshot) return null;
        var now = Environment.TickCount64;
        var measured = receiveRates.Where(rate => now - rate.Value.At <= maximumAgeMs && snapshot.People.Any(person => person.Id == rate.Key && person.Approved)).OrderBy(rate => rate.Value.Kbps).ToArray();
        return measured.Length == 0 ? null : (snapshot.People.First(person => person.Id == measured[0].Key).Name, measured[0].Value.Kbps, measured.Length > 1);
    }
    // Reports a speed as if it had been measured; the checks use it in place of a slow connection.
    internal void ReportReceiveRate(int kbps) => Post(new("receive-rate", Message: new("receive-rate", Number: kbps)));
    // The file behind a subtitle shared with the room, for drawing it into a converted stream.
    public string? HostedAssetPath(string id) => (assets.GetValueOrDefault(id) as ILocalMediaSource)?.LocalPath;
    public void ShareSubtitle(string path)
    {
        if (!IsConnected || Identity?.Host != true || Snapshot?.Media is not { } media) throw new IOException("Only whoever is streaming can load room subtitles.");
        var asset = new FileMediaSource(path, Path.GetFileName(path));
        if (asset.Media.Length is <= 0 or > 8388608 || !new[] { ".srt", ".ass", ".ssa", ".sub" }.Contains(asset.Media.Extension.ToLowerInvariant()))
            throw new IOException("Choose a subtitle file up to 8 MiB.");
        if ((media.Subtitles ?? []).Count(s => s.Id != asset.Media.Id) >= 12) throw new IOException("A video can share up to 12 subtitle files.");
        if (!events.Writer.TryWrite(new("subtitle-selection", Subtitle: asset, MediaId: media.Id))) throw new IOException("Room control queue is full.");
    }
    private MediaItem[] hostedLibrary = [];
    public Func<string?, string?>? LibraryThumbnail { get; set; }
    public Func<string?, string?>? LibraryArtwork { get; set; }
    // Encodes artwork to fit 1920x1080 for guests' large views.
    public Func<string?, string?>? FullHdArtwork { get; set; }
    // The only artwork guests may request without library browsing: the room's current or last video.
    private SessionArtwork? sessionArtwork;
    public void SetSessionArtwork(string? title, string? path) =>
        Volatile.Write(ref sessionArtwork, title is null ? null : new(title, path));
    private sealed record SessionArtwork(string Title, string? Path);
    public void SetHostedLibrary(IEnumerable<MediaItem> items) => Volatile.Write(ref hostedLibrary, items.ToArray());
    private readonly ConcurrentDictionary<string, IMediaSource> assets = new();
    public void SetHostedFile(string path, string title)
    {
        ApplyHostedFile(PrepareHostedFile(path, title));
    }
    public static HostedFileSelection PrepareHostedFile(string path, string title)
    {
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { }
        var preparedAssets = new Dictionary<string, IMediaSource>();
        var file = new FileMediaSource(path, title);
        if (file.Media.Length <= 0) throw new InvalidDataException("Video file is empty");
        var stem = Path.GetFileNameWithoutExtension(path);
        foreach (var sidecar in Directory.EnumerateFiles(Path.GetDirectoryName(Path.GetFullPath(path))!))
        {
            var name = Path.GetFileNameWithoutExtension(sidecar);
            if (!(name.Equals(stem, StringComparison.OrdinalIgnoreCase) || name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase))) continue;
            if (!new[] { ".srt", ".ass", ".ssa" }.Contains(Path.GetExtension(sidecar).ToLowerInvariant()) || new FileInfo(sidecar).Length is <= 0 or > 8388608 || preparedAssets.Count >= 12) continue;
            var asset = new FileMediaSource(sidecar, Path.GetFileName(sidecar)); preparedAssets[asset.Media.Id] = asset;
        }
        return new(new MediaWithSubtitles(file, preparedAssets.Values.Select(x => x.Media).ToArray()), preparedAssets);
    }
    public void ApplyHostedFile(HostedFileSelection selection)
    {
        assets.Clear(); foreach (var pair in selection.Assets) assets[pair.Key] = pair.Value;
        HostedMedia = selection.Media;
    }
    public void PublishHostedFile(HostedFileSelection selection, bool play, long positionMs = 0)
    {
        if (!IsConnected || Identity?.Host != true || !events.Writer.TryWrite(new("selection", Selection: selection, Play: play, PositionMs: Math.Max(0, positionMs))))
            throw new IOException("Room connection is busy or closed");
    }
    // Starts a video for the room from any member. Someone who isn't streaming takes the streaming role first, and the
    // video is the first thing the room hears from them. Completes once the room has it.
    public async Task StartStreamAsync(HostedFileSelection selection, bool play, long positionMs = 0, CancellationToken ct = default)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!IsConnected || !events.Writer.TryWrite(new("stream", Selection: selection, Play: play, PositionMs: Math.Max(0, positionMs), Completion: started)))
            throw new IOException("Room connection is busy or closed");
        await WaitForRole(started, ct);
    }
    // Takes the streaming role without starting a video, for what only the streamer does: reopening the last video, or
    // sharing its queues and library.
    public async Task BecomeHostAsync(CancellationToken ct = default)
    {
        if (Identity?.Host == true) return;
        var taken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!IsConnected || !events.Writer.TryWrite(new("stream", Completion: taken))) throw new IOException("Room connection is busy or closed");
        await WaitForRole(taken, ct);
    }
    private async Task WaitForRole(TaskCompletionSource pending, CancellationToken ct)
    {
        try { await pending.Task.WaitAsync(TimeSpan.FromSeconds(10), ct); }
        catch (TimeoutException ex)
        {
            Post(new("stream-timeout", Completion: pending));
            throw new IOException("The room service didn't answer. Check the connection and try again.", ex);
        }
    }
    // Waiting to take the streaming role, with the video to start once it is held.
    private readonly List<(TaskCompletionSource Completion, HostedFileSelection? Selection, bool Play, long PositionMs)> pendingStreams = [];
    private readonly ServerClock clock = new();
    private long pingId;
    private string? clockHost;
    public long ServerNowMs => clock.Now;
    public long ServerOffsetMs => clock.OffsetMs;
    public long RecentRttMs => clock.RecentRttMs;
    public event Action<WireMessage>? Message;
    public event Action<string>? Status;
    public event Action<string>? LibraryPeerReady;
    public event Action<string, WireMessage>? LibraryMessage;
    public void SendLibrary(string peerId, WireMessage message)
    {
        if (!IsConnected || Snapshot?.People.Any(p => p.Id == peerId && p.Approved) != true ||
            !negotiated.ContainsKey(peerId) || !peers.TryGetValue(peerId, out var peer)) throw new IOException("They are no longer connected");
        peer.SendControl(message);
    }
    // Who is in each of these rooms right now, by room code, without entering them. Rooms the service doesn't know are left out.
    public static async Task<IReadOnlyDictionary<string, RoomPresenceInfo>> PresenceAsync(string server, IEnumerable<string> codes, CancellationToken ct)
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
        using var content = new StringContent(Wire.Serialize(codes.Select(code => code.ToUpperInvariant()).Distinct().Take(32).ToArray()), System.Text.Encoding.UTF8, "application/json");
        using var reply = await http.PostAsync(RoomAddress.ValidateServer(server) + "/presence", content, ct);
        if (!reply.IsSuccessStatusCode || reply.Content.Headers.ContentType?.MediaType != "application/json") return new Dictionary<string, RoomPresenceInfo>();
        return Wire.Read<Dictionary<string, RoomPresenceInfo>>(await reply.Content.ReadAsStringAsync(ct));
    }
    // Enters the room an invitation names, or creates one when there is none. key is this member's saved key for the
    // room; without one, the key saved in settings is used, and on a first visit the service issues one. A room created
    // with persistent off is forgotten once it stays empty.
    public async Task ConnectAsync(string server, string name, string? invitation = null, string? key = null, bool persistent = false, string? roomName = null)
    {
        server = RoomAddress.ValidateServer(server);
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 40 || name.Any(char.IsControl))
            throw new ArgumentException("Enter a display name between 1 and 40 characters in Settings.");
        ServerAddress = server;
        displayName = name.Trim();
        if (roomName is not null && !HostRoomCoordinator.ValidName(roomName))
            throw new ArgumentException("Enter a room name between 1 and 100 characters without control characters.");
        this.roomName = roomName?.Trim();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var uri = new Uri(server.TrimEnd('/') + "/room");
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) throw new InvalidOperationException("Use an HTTPS server address. HTTP is allowed only on localhost for development.");
        var builder = new UriBuilder(uri) { Scheme = uri.Scheme == "https" ? "wss" : "ws" };
        key ??= invitation is null ? null : settings?.Setting(RoomSetting(invitation, "guestKey"));
        var first = new WireMessage("enter", Text: name, Data: Wire.Serialize(new EnterRequest(RoomProtocol.Version, invitation?.Trim().ToUpperInvariant(), key, this.roomName, invitation is null && !persistent)));
        using var probe = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
        using var health = await probe.GetAsync(server.TrimEnd('/') + "/health", deadline.Token);
        if ((int)health.StatusCode is >= 300 and < 400 || health.Content.Headers.ContentType?.MediaType == "text/html")
            throw new IOException("This address is a web page, not a room server. Check the room server address in Settings.");
        var transport = health.IsSuccessStatusCode && health.Content.Headers.ContentType?.MediaType == "application/json"
            ? await health.Content.ReadAsStringAsync(lifetime.Token) : "";
        using (var capabilities = System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(transport) ? "{}" : transport))
        {
            bool Flag(string name) => capabilities.RootElement.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.True;
            var protocol = capabilities.RootElement.TryGetProperty("protocol", out var version) && version.TryGetInt32(out var number) ? number : 0;
            PlaybackDiagnostics.Record("room-connect", new { room = diagnosticId, action = invitation is null ? "create" : "enter", protocol,
                httpStatus = (int)health.StatusCode, relayConfigured = Flag("relayConfigured"), stunConfigured = Flag("stunConfigured") });
            // A service from before rooms lost their host answers with an older protocol and would only turn this app away.
            if (protocol is > 0 and < RoomProtocol.Version)
                throw new RoomRejectedException("This room server hasn't been updated for the new rooms yet. Whoever runs it needs to update it, or choose another server in Settings.");
            if (protocol > RoomProtocol.Version) throw new RoomRejectedException("This room server needs a newer BlackPearl. Update the app to use rooms.");
        }
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        try { await socket.ConnectAsync(builder.Uri, deadline.Token); }
        catch { socket.Dispose(); throw; }
        connection = new WebSocketRoomConnection(socket);
        await connection.SendAsync(first, lifetime.Token);
        controlTask = ControlLoop(); sendTask = SendLoop(); readTask = ReadLoop();
        pingTask = PingLoop();
        try { await connected.Task.WaitAsync(deadline.Token); }
        catch (OperationCanceledException ex)
        {
            await DisposeAsync();
            // A rejection also cancels the room lifetime. Preserve its specific
            // error rather than reporting the racing deadline cancellation.
            if (connected.Task.IsFaulted) await connected.Task;
            throw new IOException("Room connection timed out. Rejoin to reconnect.", ex);
        }
        catch { await DisposeAsync(); throw; }
    }
    public void Send(WireMessage message)
    {
        if (!IsConnected) throw new IOException("Room connection is closed. Rejoin the room to reconnect.");
        // The room's name and who may be in it belong to the room service; its creator changes them there.
        if (message.Type is "remove" or "restore" or "room-name")
        {
            if (Identity?.Owner != true) throw new IOException("Only the room's creator can do this.");
            if (!DiscoveryOnline) throw new IOException("The room service is offline. Try again once it reconnects.");
            QueueDiscovery(message.Type == "room-name" ? new("rename", Text: message.Text) : message);
            return;
        }
        if (message.Type is "tracks" or "catalog-follow" or "catalog-pointer" or "catalog-artwork" or "catalog-watch" or "catalog-view" or "catalog-browse" or "catalog-release" or "catalog-renew" or "catalog-permission" or "media" or "stream" or "controls" or "last-session" or "session-artwork" or "artwork-ack" or "queue" or "playback" or "ready" or "buffering")
        {
            if (Identity?.Host != true && !directConnected.Task.IsCompletedSuccessfully)
                throw new IOException("Still connecting to whoever is streaming. Try again in a moment.");
            if (!events.Writer.TryWrite(new("local", Message: message))) throw new IOException("Room control queue is full");
        }
        else QueueDiscovery(message);
        if (PlaybackDiagnostics.Enabled && message.Type == "playback") PlaybackDiagnostics.Record("control-queued", new { message.Data, transport = "peer" });
    }
    private void QueueDiscovery(WireMessage message)
    {
        if (!DiscoveryOnline || !outgoing.Writer.TryWrite(message)) throw new IOException("The room service is offline. Nobody new can enter until it reconnects.");
    }
    // Existing peer playback continues while discovery is offline or congested.
    private bool TryQueueDiscovery(WireMessage message)
    {
        try { QueueDiscovery(message); return true; } catch (IOException) { return false; }
    }
    private async Task SendLoop()
    {
        try
        {
            await foreach (var msg in outgoing.Reader.ReadAllAsync(discoveryLifetime.Token))
            {
                await connection!.SendAsync(msg, discoveryLifetime.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or HttpRequestException)
        { if (!lifetime.IsCancellationRequested) DiscoveryLost(ex.Message); }
    }
    private async Task PingLoop()
    {
        try
        {
            var tick = 0;
            while (!lifetime.IsCancellationRequested)
            {
                if (Identity?.Host == true) Post(new("library-expire"));
                if (tick++ % 5 == 0 && DiscoveryOnline)
                    try { QueueDiscovery(new("ping", Number: tick)); } catch (IOException ex) { DiscoveryLost(ex.Message); }
                Post(new("links"));
                Post(new("peer-health"));
                Post(new("clock", Message: new("clock-ping", Number: Interlocked.Increment(ref pingId))));
                Post(new("receive-rate"));
                await Task.Delay(2000, lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { FailRoom("Room controls failed: " + ex.Message); }
    }
    private async Task ReadLoop()
    {
        try
        {
            while (!discoveryLifetime.IsCancellationRequested)
            {
                var message = await connection!.ReadAsync(discoveryLifetime.Token); if (message is null) break;
                await events.Writer.WriteAsync(new("discovery", Message: message), lifetime.Token);
            }
        }
        catch (Exception ex) when (!lifetime.IsCancellationRequested) { DiscoveryLost(ex.Message); }
        finally { if (!lifetime.IsCancellationRequested) DiscoveryLost("Room discovery connection closed"); }
    }
    private void Post(RoomEvent item)
    {
        if (!lifetime.IsCancellationRequested && !events.Writer.TryWrite(item)) FailRoom("Room control queue overflow. Rejoin the room.");
    }
    private void PostPeer(PeerInbox inbox, RoomEvent item)
    {
        if (lifetime.IsCancellationRequested) return;
        if (inbox.Admit(out var drops, out var budgeted))
        {
            if (events.Writer.TryWrite(item with { Inbox = inbox })) return;
            inbox.Release(); drops = 1;
        }
        // Peer traffic is dropped, never allowed to fail the room. A sustained flood disconnects that peer.
        var flood = PeerInbox.Flooding(drops, budgeted);
        if (flood) { flooded.Enqueue((item.Peer!, item.Transport!)); events.Writer.TryWrite(new("flood")); }
        if (drops == 1 || flood)
            PlaybackDiagnostics.Record("peer-message-dropped", new { room = diagnosticId, transport = item.Transport?.DiagnosticId, command = item.Message?.Type, flood });
    }
    // Runs blocking library work off the control loop; results return to it in request order.
    private bool Offload<T>(Func<T> work, Action<T> then, Action<Exception> failed)
    {
        if (Interlocked.Increment(ref offloadedCount) > 32) { Interlocked.Decrement(ref offloadedCount); return false; }
        offloaded = offloaded.ContinueWith(async _ =>
        {
            Action result;
            try { var value = work(); result = () => then(value); }
            catch (Exception ex) { result = () => failed(ex); }
            finally { Interlocked.Decrement(ref offloadedCount); }
            try { await events.Writer.WriteAsync(new("completed", Then: () => { try { result(); } catch (Exception ex) when (ex is not OperationCanceledException) { failed(ex); } }), lifetime.Token); }
            catch (Exception) { /* The room closed while the work ran. */ }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        return true;
    }
    private void DiscoveryLost(string reason)
    {
        if (Interlocked.Exchange(ref discoveryFailed, 1) != 0) return;
        discoveryLifetime.Cancel(); connection?.Abort();
        Post(new("discovery-lost", Message: new("notice", Text: reason)));
    }
    // final: the room service ended this connection on purpose, so the app must not come back on its own.
    private void FailRoom(string reason, bool final = false)
    {
        PlaybackDiagnostics.Record("room-failed", new { room = diagnosticId, reason = DiagnosticReason(reason), final });
        if (final) ClosedReason ??= reason;
        var error = final ? new RoomRejectedException(reason) : new IOException(reason);
        connected.TrySetException(error); directConnected.TrySetException(error);
        lock (pendingStreams) { foreach (var pending in pendingStreams) pending.Completion.TrySetException(new IOException(reason)); pendingStreams.Clear(); }
        lifetime.Cancel(); discoveryLifetime.Cancel(); connection?.Abort();
        Status?.Invoke((final ? "Closed — " : "Disconnected — ") + reason);
    }
    private async Task ControlLoop()
    {
        try
        {
            await foreach (var item in events.Reader.ReadAllAsync(lifetime.Token))
            {
                item.Inbox?.Release();
                try
                {
                    while (flooded.TryDequeue(out var flood))
                        if (peers.TryGetValue(flood.Peer, out var flooding) && flooding == flood.Transport) DepartPeer(flood.Peer, "Peer exceeded the room message rate");
                    if (item.Transport is not null && (!peers.TryGetValue(item.Peer!, out var current) || current != item.Transport)) continue;
                    switch (item.Kind)
                    {
                        case "completed": item.Then!(); break;
                        case "library-expire": if (coordinator?.ExpireLibrary(clock.Now) == true) PublishHostState(); break;
                        case "links": ReconcileLinks(); break;
                        case "peer-health":
                            // Every member answers clock pings on the independent control connection. Replace a silent
                            // connection before its native timeout does: the streamer's quickly, because VLC's input
                            // depends on it, and the others, which only carry voice, with more patience.
                            if (DiscoveryOnline)
                                foreach (var id in negotiated.Keys.ToArray())
                                {
                                    var silent = Environment.TickCount64 - Link(id).HeardAt;
                                    if (silent < (id == hostPeer && directConnected.Task.IsCompletedSuccessfully ? 6000 : 12000)) continue;
                                    PlaybackDiagnostics.Record("peer-unresponsive", new { room = diagnosticId, silentMs = silent, streamer = id == hostPeer });
                                    DepartPeer(id, "Control replies stopped arriving");
                                }
                            ReconcileVoice();
                            break;
                        case "discovery": HandleDiscovery(item.Message!); break;
                        case "discovery-lost":
                            if (Identity is null || Identity.Host == false && !directConnected.Task.IsCompletedSuccessfully)
                                FailRoom(item.Message?.Text ?? "Room service disconnected");
                            else Status?.Invoke("Room service is offline. The video and voice continue; nobody new can enter.");
                            break;
                        case "local": HandleLocal(item.Message!); break;
                        case "voice-local": ApplyLocalVoice(); break;
                        case "stream":
                            if (Identity?.Host == true)
                            {
                                if (item.Selection is not null) ApplySelection(item.Selection, item.Play, item.PositionMs);
                                item.Completion!.TrySetResult();
                            }
                            else
                            {
                                // The room service answers with a snapshot naming this member.
                                QueueDiscovery(new("claim"));
                                lock (pendingStreams) pendingStreams.Add((item.Completion!, item.Selection, item.Play, item.PositionMs));
                            }
                            break;
                        case "stream-timeout": lock (pendingStreams) pendingStreams.RemoveAll(pending => pending.Completion == item.Completion); break;
                        case "subtitle-selection":
                            if (coordinator?.Media?.Id != item.MediaId || item.Subtitle is null) break;
                            assets[item.Subtitle.Media.Id] = item.Subtitle;
                            ApplyHostCommand(Identity!.Peer, new("subtitle", Data: Wire.Serialize(new SubtitleAttachment(item.MediaId!, item.Subtitle.Media))));
                            if (HostedMedia is ILocalMediaSource local && coordinator?.Media is { } updated)
                                HostedMedia = new UpdatedHostedMedia(local, updated);
                            break;
                        case "selection":
                            // Someone else took the streaming role while this was queued; their video stays.
                            if (Identity?.Host != true) throw new IOException("Someone else started a video. Play yours again to switch to it.");
                            ApplySelection(item.Selection!, item.Play, item.PositionMs);
                            break;
                        case "library-opened": LibraryPeerReady?.Invoke(item.Peer!); break;
                        case "opened": SendPeer(item.Peer!, new("control-hello", Number: 3)); break;
                        case "peer": HandlePeer(item.Peer!, item.Message!); break;
                        case "closed": DepartPeer(item.Peer!); break;
                        case "timeout":
                            if (!negotiated.ContainsKey(item.Peer!))
                            {
                                PlaybackDiagnostics.Record("peer-handshake-timeout", new { room = diagnosticId, transport = item.Transport?.DiagnosticId });
                                DepartPeer(item.Peer!, "Direct connection did not open");
                            }
                            break;
                        case "receive-rate":
                            // A viewer tells whoever plays the video how fast it arrives, as measured while this app was
                            // asking for more. Apps from before this would answer the unknown command with an error.
                            if (Identity?.Host == false && (hostFeatures & ReceiveRateFeature) != 0 && hostPeer is { } playing && negotiated.ContainsKey(playing) &&
                                (item.Message?.Number ?? (peers.TryGetValue(playing, out var source) && source.Received.Rate is { AgeMs: < 3000 } measured ? measured.Kbps : 0)) is > 0 and var kbps)
                                SendPeer(playing, new("receive-rate", Number: kbps));
                            break;
                        case "clock":
                            // The streamer's replies set the room clock; everyone's show that their connection is alive.
                            foreach (var id in negotiated.Keys.ToArray())
                            {
                                if (id == hostPeer && Identity?.Host == false) clock.Sent(item.Message!.Number);
                                SendPeer(id, item.Message!);
                            }
                            break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !lifetime.IsCancellationRequested)
                {
                    PlaybackDiagnostics.Record("room-command-error", new { room = diagnosticId, phase = item.Kind, command = item.Message?.Type, error = ex.GetType().Name });
                    // Another member's command or a local action can fail without ending the room.
                    if (item.Kind == "peer")
                    { if (Identity?.Host == true && negotiated.ContainsKey(item.Peer!)) SendPeer(item.Peer!, new("error", Text: "Invalid or unauthorized room command")); }
                    else if (item.Kind == "stream") item.Completion!.TrySetException(ex is IOException or InvalidDataException ? ex : new IOException(ex.Message, ex));
                    else if (item.Kind is "local" or "selection" or "subtitle-selection" or "completed") Message?.Invoke(new("error", Text: ex.Message));
                    else FailRoom(ex is InvalidDataException or System.Text.Json.JsonException or IOException or ArgumentException ? ex.Message : "Room controls failed: " + ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { FailRoom("Room controls failed: " + ex.Message); }
    }
    // Validates before replacing the shared file, so a rejected selection keeps the current video.
    private void ApplySelection(HostedFileSelection selection, bool play, long positionMs)
    {
        var selected = selection.Media.Media;
        if (!HostRoomCoordinator.ValidMedia(selected)) throw new InvalidDataException("This video cannot be shared.");
        var (previousMedia, previousAssets) = (HostedMedia, assets.ToArray());
        ApplyHostedFile(selection);
        try { ApplyHostCommand(Identity!.Peer, new("media", Data: Wire.Serialize(selected))); }
        finally
        {
            if (coordinator?.Media?.Id != selected.Id)
            { assets.Clear(); foreach (var pair in previousAssets) assets[pair.Key] = pair.Value; HostedMedia = previousMedia; }
        }
        if (coordinator?.Media?.Id == selected.Id)
            ApplyHostCommand(Identity.Peer, new("playback", Data: Wire.Serialize(new PlaybackState(0, selected.Id, play, positionMs, 0))));
    }
    private PeerLink Link(string id) => links.TryGetValue(id, out var link) ? link : links[id] = new() { HeardAt = Environment.TickCount64 };
    // Of two members, the one whose id sorts first offers the connection, so both never offer at once.
    private bool Offers(string id) => string.CompareOrdinal(Identity!.Peer, id) < 0;
    // Keeps one direct connection to everyone the room service lists, and none to anyone else.
    private void ReconcileLinks()
    {
        if (Identity is not { } identity) return;
        var people = roster;
        foreach (var id in peers.Keys.Concat(links.Keys).Distinct().Where(id => people.All(p => p.Id != id)).ToArray()) DepartPeer(id, "No longer in the room", left: true);
        var now = Environment.TickCount64;
        foreach (var person in people)
        {
            if (person.Id == identity.Peer || peers.ContainsKey(person.Id)) continue;
            var known = links.ContainsKey(person.Id);
            var link = Link(person.Id);
            if (now < link.RetryAt) continue;
            link.RetryAt = now + 10000;
            if (Offers(person.Id)) CreatePeer(person.Id, true);
            // The other member offers when it sees this one; ask only when that has not happened, or the connection was lost.
            else if (known && TryQueueDiscovery(new("reconnect", Target: person.Id))) PlaybackDiagnostics.Record("peer-reconnect-request", new { room = diagnosticId });
        }
    }
    // The room service names who holds the streaming role. epoch counts its changes.
    private void ChangeHost(string? streamer, long newEpoch)
    {
        var identity = Identity!;
        epoch = newEpoch; hostPeer = streamer; hostFeatures = 0;
        var mine = streamer == identity.Peer;
        PlaybackDiagnostics.Record("room-streamer", new { room = diagnosticId, mine, epoch = newEpoch });
        if (mine && !identity.Host) Promote();
        else if (!mine && identity.Host) Demote();
        else if (!mine) ResetHostLink();
        // Republish under the new count.
        else publishedSnapshot = null;
        // Its state may have arrived before the room service named it; ask again.
        if (!mine && streamer is not null && negotiated.ContainsKey(streamer)) SendPeer(streamer, new("state-request"));
    }
    private void ResetHostLink()
    {
        receivedSequence = 0; clockHost = null; Interlocked.Exchange(ref peerRecovering, 0);
        if (directConnected.Task.IsCompleted) directConnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    // This member now holds the streaming role and keeps the room's state.
    private void Promote()
    {
        var identity = Identity! with { Host = true };
        var previous = Snapshot;
        Identity = identity; hostPeer = identity.Peer;
        coordinator = new(identity.Peer);
        var now = clock.Now;
        coordinator.Discover(roster.Length > 0 ? roster : [new(identity.Peer, displayName, true, true, Owner: identity.Owner)], now);
        if (previous is not null) coordinator.Inherit(previous);
        var name = HostRoomCoordinator.ValidName(roomName) ? roomName : "Room " + identity.Room[..6];
        coordinator.Apply(identity.Peer, new("room-name", Text: name), now, out _);
        // Whoever streams decides whether the others may play, pause and seek. They may, unless this person turned that off for the room.
        coordinator.Apply(identity.Peer, new("controls", Number: settings?.Setting(RoomSetting(identity.Room, "sharedControls")) == "false" ? 0 : 1), now, out _);
        coordinator.Apply(identity.Peer, new("catalog-permission", Number: settings?.Setting(RoomSetting(identity.Room, "libraryBrowsing")) == "true" ? 1 : 0), now, out _);
        coordinator.Apply(identity.Peer, new("voice", Number: Volatile.Read(ref voiceDesired)), now, out _);
        foreach (var id in negotiated.Keys) coordinator.SetConnected(id, true, now);
        ResetHostLink(); directConnected.TrySetResult();
        publishedSnapshot = null;
        // Told before any state this member publishes, so whoever listens starts from a clean slate and then follows it.
        Message?.Invoke(new("role", Number: 1));
        (TaskCompletionSource Completion, HostedFileSelection? Selection, bool Play, long PositionMs)[] waiting;
        lock (pendingStreams) { waiting = pendingStreams.ToArray(); pendingStreams.Clear(); }
        // A video that was waiting for the role is the first state the room receives from this member.
        foreach (var pending in waiting)
            try
            {
                if (pending.Selection is not null) ApplySelection(pending.Selection, pending.Play, pending.PositionMs);
                pending.Completion.TrySetResult();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { pending.Completion.TrySetException(ex); }
        PublishHostState();
    }
    // Someone else took the streaming role: this member's video ends, and it follows theirs.
    private void Demote()
    {
        coordinator = null; HostedMedia = null; assets.Clear(); artworkTransfers.Clear(); receiveRates.Clear();
        Identity = Identity! with { Host = false };
        publishedSnapshot = publishedRecipients = null;
        ResetHostLink();
        Message?.Invoke(new("role", Number: 0));
    }
    private void HandleDiscovery(WireMessage message)
    {
        switch (message.Type)
        {
            case "welcome":
                var welcome = Wire.Read<Welcome>(message.Data!);
                PlaybackDiagnostics.Record("room-identity", new { room = diagnosticId, streamer = welcome.Host, owner = welcome.Owner, iceServers = welcome.IceServers.Length, welcome.ForceRelay });
                // The creator's key is kept with the saved room; everyone else's is kept here.
                if (welcome.GuestKey is { Length: 64 } key && welcome.HostKey is null) settings?.Setting(RoomSetting(welcome.Room, "guestKey"), key);
                Identity = welcome with { Host = false };
                // The first one into an empty room holds the streaming role until someone starts a video.
                if (welcome.Host) Promote();
                connected.TrySetResult(Identity); Message?.Invoke(message); break;
            case "snapshot":
                var discovery = Wire.Read<RoomSnapshot>(message.Data!);
                roster = discovery.People; Members = discovery.AdmittedGuests ?? []; Removed = discovery.Removed ?? [];
                if (HostRoomCoordinator.ValidName(discovery.Name)) roomName = discovery.Name!.Trim();
                var streamer = discovery.People.FirstOrDefault(p => p.IsHost)?.Id;
                if (discovery.Epoch != epoch || streamer != hostPeer) ChangeHost(streamer, discovery.Epoch);
                ReconcileLinks();
                if (Identity!.Host)
                {
                    if (roomName is not null && coordinator!.Name != roomName) coordinator.Apply(Identity.Peer, new("room-name", Text: roomName), clock.Now, out _);
                    coordinator!.Discover(discovery.People, clock.Now); PublishHostState();
                }
                // Until the streamer's state arrives, the room service's list shows who is here.
                else if (!directConnected.Task.IsCompletedSuccessfully && !IsPeerRecovering)
                {
                    Snapshot = (Snapshot ?? new([], null, null, false, [])) with { People = discovery.People.Select(p => p with { GuestId = null }).ToArray(), Name = roomName ?? Snapshot?.Name };
                    Message?.Invoke(new("snapshot", Data: Wire.Serialize(Snapshot)));
                }
                Message?.Invoke(new("members"));
                break;
            // Another member lost its connection to this one and asks for a new one.
            case "connect":
                if (message.Target is not { } asking || roster.All(p => p.Id != asking) || !Offers(asking)) break;
                // Both sides can notice a lost connection at once; an attempt this member just started already answers it.
                if (peers.ContainsKey(asking) && !negotiated.ContainsKey(asking) && Environment.TickCount64 - Link(asking).CreatedAt < 5000) break;
                if (peers.ContainsKey(asking)) DepartPeer(asking, "The other side asked for a new connection");
                CreatePeer(asking, true);
                break;
            case "signal":
                if (message.Sender is not { } from || roster.All(p => p.Id != from)) return;
                var link = Link(from);
                if (Offers(from))
                {
                    // Answers count only for the attempt this member is making now.
                    if (message.Number != link.Generation || !peers.TryGetValue(from, out var offered)) return;
                    offered.ReceiveSignal(message.Text!, message.Data!);
                }
                else
                {
                    if (message.Number < link.Generation) return;
                    // A higher number is a new attempt from the other member: it replaces the connection this one holds.
                    if (message.Number > link.Generation)
                    {
                        if (peers.ContainsKey(from)) DepartPeer(from, "Replaced by a new connection");
                        link.Generation = message.Number; CreatePeer(from, false);
                    }
                    if (peers.TryGetValue(from, out var answering)) answering.ReceiveSignal(message.Text!, message.Data!);
                }
                break;
            case "peer-left": if (message.Sender is not null) DepartPeer(message.Sender, "Room service reported that they left", left: true); break;
            // The room service ended this connection on purpose: removed by the room's creator, or entered from another window.
            case "closed": FailRoom(message.Text ?? "The room service closed this connection.", final: true); break;
            case "error":
                if (Identity is null) FailRoom(message.Text ?? "Could not enter this room", final: true);
                else Message?.Invoke(message);
                break;
        }
    }
    private void HandleLocal(WireMessage message)
    {
        if (Identity?.Host == true) ApplyHostCommand(Identity.Peer, message);
        else if (hostPeer is { } streamer && negotiated.ContainsKey(streamer)) SendPeer(streamer, message);
        else throw new IOException("Still connecting to whoever is streaming. Try again in a moment.");
    }
    private void HandlePeer(string sender, WireMessage message)
    {
        if (roster.All(p => p.Id != sender)) return;
        Link(sender).HeardAt = Environment.TickCount64;
        if (message.Type == "control-hello")
        {
            PlaybackDiagnostics.Record("peer-handshake", new { room = diagnosticId, protocol = message.Number, streamer = Identity?.Host == true });
            if (message.Number != 3) { DepartPeer(sender, "The other member runs a different BlackPearl version"); return; }
            negotiated.TryAdd(sender, 0);
            if (Identity?.Host == true) { coordinator!.SetConnected(sender, true, clock.Now); PublishHostState(); }
            // The streamer sends its state when it hears this member's hello; ask in case that already happened.
            else if (sender == hostPeer) SendPeer(sender, new("state-request"));
            Message?.Invoke(new("peer-connected", Sender: sender));
            return;
        }
        if (!negotiated.ContainsKey(sender)) return;
        // Every member answers, so a silent connection can be told from a quiet one.
        if (message.Type == "clock-ping") { SendPeer(sender, new("clock-pong", Number: message.Number, Data: clock.Now.ToString())); return; }
        if (message.Type == "clock-pong") { if (sender == hostPeer && Identity?.Host == false && long.TryParse(message.Data, out var now)) clock.Receive(message.Number, now); return; }
        // Said on the way out, so nobody starts rebuilding a connection to someone who left; the room service confirms it.
        if (message.Type == "bye") { DepartPeer(sender, "They left the room", left: true); return; }
        if (message.Type.StartsWith("library-", StringComparison.Ordinal))
        {
            LibraryMessage?.Invoke(sender, message with { Sender = sender }); return;
        }
        if (Identity?.Host == true)
        {
            if (message.Type == "state-request") { SendHostState(sender); return; }
            // How fast the video reaches this viewer, by their own measure. It changes nothing in the room.
            if (message.Type == "receive-rate") { if (message.Number is > 0 and <= 10_000_000) receiveRates[sender] = ((int)message.Number, Environment.TickCount64); return; }
            // Acknowledgements pace a transfer the host started; they bypass the command budget, and each releases at most one chunk.
            if (message.Type == "artwork-ack") { if (coordinator!.Approved(sender)) ContinueArtwork(sender, message.Number); return; }
            if (message.Type == "catalog-pointer") { ReceivePointer(sender, message); return; }
            ApplyHostCommand(sender, message);
        }
        // Only the member holding the streaming role may set this one's room state.
        else if (sender == hostPeer)
        {
            switch (message.Type)
            {
                case "host-state":
                    var state = Wire.Read<HostRoomState>(message.Data!);
                    // State from before the role last changed hands, or from a member the room service has not named yet.
                    if (state.Epoch != epoch || state.Sequence <= receivedSequence) return;
                    if (state.HostNowMs <= 0 || !state.Snapshot.People.Any(p => p.Id == sender && p.IsHost && p.Approved)) throw new InvalidDataException("Invalid host room state");
                    // Recovery to the same host keeps its RTT-compensated samples instead of an uncompensated reset.
                    if (clockHost != sender) { clock.Initialize(state.HostNowMs); clockHost = sender; }
                    receivedSequence = state.Sequence; hostFeatures = state.Features; ApplySnapshot(state.Snapshot);
                    directConnected.TrySetResult();
                    ReconcileVoice();
                    if (Interlocked.Exchange(ref peerRecovering, 0) != 0)
                    {
                        PlaybackDiagnostics.Record("peer-recovered", new { room = diagnosticId, transport = peers.GetValueOrDefault(sender)?.DiagnosticId });
                        Message?.Invoke(new("peer-recovered", Sender: sender));
                        Status?.Invoke("Connection to the stream restored");
                    }
                    break;
                case "catalog-artwork-chunk": case "session-artwork-chunk": case "catalog-started": case "catalog-page": case "catalog-error": case "notice": case "error": Message?.Invoke(message); break;
            }
        }
    }
    private void SendHostState(string id)
    {
        var snapshot = coordinator!.Snapshot;
        SendPeer(id, new("host-state", Data: Wire.Serialize(new HostRoomState(++stateSequence, clock.Now, PublicSnapshot(snapshot), epoch, HostFeatures))));
    }
    private static RoomSnapshot PublicSnapshot(RoomSnapshot snapshot) =>
        snapshot with { People = snapshot.People.Where(p => p.Approved).Select(p => p with { GuestId = null }).ToArray(), AdmittedGuests = null };
    private void ApplyHostCommand(string sender, WireMessage message)
    {
        var now = clock.Now;
        if (message.Type is "playback" or "ready" or "buffering")
            PlaybackDiagnostics.Record("host-command-received", new { sender, message.Type, message.Data, serverMs = now });
        // Publish lease expiry even when the following command is rejected.
        if (coordinator!.ExpireLibrary(now)) PublishHostState();
        LibraryBrowseRequest? browse = null;
        if (message.Type == "catalog-browse")
        {
            try
            {
                browse = Wire.Read<LibraryBrowseRequest>(message.Data ?? "{}");
                if (browse.Query is null || browse.Query.Length > 100 || browse.Category is < 0 or > 3 || browse.Page is < 0 or > 100000 || browse.ParentId?.Length > 128)
                    throw new InvalidDataException("Invalid library search");
            }
            catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
            { SendPeer(sender, new("catalog-error", Number: message.Number, Text: "Invalid library search.")); return; }
        }
        if (!coordinator.Apply(sender, message, now, out var announcement))
        {
            var error = new WireMessage(browse is null ? "error" : "catalog-error", Number: message.Number,
                Text: browse is null ? "Whoever is playing the video controls this, or the command is no longer valid." : !coordinator.LibraryBrowsing ? "They have turned off library browsing." : "Someone else is browsing. Try again when they finish.");
            if (sender == Identity!.Peer) Message?.Invoke(error); else SendPeer(sender, error);
            return;
        }
        if (HandleLibraryInteraction(sender, message)) return;
        if (browse is not null)
        {
            PublishHostState();
            var (source, thumbnail, store) = (Volatile.Read(ref hostedLibrary), LibraryThumbnail, settings);
            // Thumbnails and watch progress read files and SQLite; build the page off the control loop.
            if (!Offload(() => SharedLibrary.Browse(source, browse, thumbnail, browse.Home ? store?.PlaybackSettings() : null), page =>
            {
                if (!negotiated.ContainsKey(sender)) return;
                if (!coordinator.LibraryBrowsing || coordinator.LibraryActivity?.Peer != sender)
                { SendPeer(sender, new("catalog-error", Number: message.Number, Text: "Library browsing ended. Try again.")); return; }
                coordinator.DescribeLibrary(page.Location, browse.Query, clock.Now);
                PublishHostState();
                SendPeer(sender, new("catalog-page", Number: message.Number, Data: Wire.Serialize(page)));
            }, ex =>
            {
                PlaybackDiagnostics.Record("library-interaction-error", new { room = diagnosticId, command = message.Type, error = ex.GetType().Name });
                if (negotiated.ContainsKey(sender))
                    SendPeer(sender, new("catalog-error", Number: message.Number, Text: ex is InvalidDataException ? ex.Message : "Their library could not be loaded."));
            }))
                SendPeer(sender, new("catalog-error", Number: message.Number, Text: "Their app is busy. Try again shortly."));
            return;
        }
        if (message.Type == "catalog-view")
        {
            if (message.Data is not null)
            {
                try
                {
                    var location = Wire.Read<LibraryBrowseRequest>(message.Data);
                    var cached = SharedLibrary.Browse(Volatile.Read(ref hostedLibrary), location);
                    coordinator.DescribeLibrary(cached.Location, location.Query, clock.Now);
                    PublishHostState();
                }
                catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException) { }
                return;
            }
            var entry = Volatile.Read(ref hostedLibrary).FirstOrDefault(x => x.Id == message.Target && x.Available && !x.IsVirtual && !x.IsExtra);
            if (entry is not null)
            {
                var title = entry.Series is null ? entry.Title : entry.Caption + " · " + entry.EpisodeDisplayTitle;
                coordinator.DescribeLibrary(title[..Math.Min(240, title.Length)], "", clock.Now);
            }
        }
        if (sender == Identity!.Peer && message.Type == "catalog-permission")
            settings?.Setting(RoomSetting(Identity.Room, "libraryBrowsing"), coordinator.LibraryBrowsing ? "true" : "false");
        // Remembered for the next time this person streams in the room.
        if (sender == Identity!.Peer && message.Type == "controls")
            settings?.Setting(RoomSetting(Identity.Room, "sharedControls"), coordinator.SharedControls ? "true" : "false");
        PublishHostState();
        if (announcement is not null) { Message?.Invoke(announcement); Broadcast(announcement); }
        if (message.Type is "playback" or "ready" or "buffering")
            PlaybackDiagnostics.Record("host-command-accepted", new { sender, message.Type, playback = coordinator.Playback,
                waiting = coordinator.Snapshot.People.Where(p => p.Approved && !p.Ready && coordinator.IsConnected(p.Id)).Select(p => p.Id).ToArray() });
    }
    private void ApplySnapshot(RoomSnapshot snapshot, string? json = null)
    {
        var previous = Snapshot; Snapshot = snapshot;
        UpdateVoiceRoutes(snapshot);
        // A snapshot applied outside PublishHostState replaces what the host last published locally.
        if (json is null) publishedSnapshot = null;
        if (snapshot.Playback is { } playback && previous?.Playback is { } before && before.MediaId == playback.MediaId && before.Revision != playback.Revision)
            Message?.Invoke(new("playback", Data: Wire.Serialize(playback)));
        Message?.Invoke(new("snapshot", Data: json ?? Wire.Serialize(snapshot)));
    }
    private string? publishedSnapshot, publishedRecipients;
    private void PublishHostState()
    {
        var snapshot = coordinator!.Snapshot;
        // Library renewals and other commands often leave the room unchanged; skip republishing
        // unless the state or the set of peers receiving it changed.
        var json = Wire.Serialize(snapshot);
        // Include each peer's transport so a reconnected peer always receives the current state.
        var recipients = epoch + "|" + string.Join(",", negotiated.Keys.Where(coordinator.Approved).Order(StringComparer.Ordinal)
            .Select(id => id + ":" + peers.GetValueOrDefault(id)?.DiagnosticId));
        if (json == publishedSnapshot && recipients == publishedRecipients) return;
        publishedSnapshot = json; publishedRecipients = recipients;
        ApplySnapshot(snapshot, json);
        Broadcast(new("host-state", Data: Wire.Serialize(new HostRoomState(++stateSequence, clock.Now, PublicSnapshot(snapshot), epoch, HostFeatures))));
    }
    private void Broadcast(WireMessage message)
    {
        // Serialize once for every peer.
        var text = Wire.Serialize(message);
        foreach (var id in negotiated.Keys.ToArray())
            if (coordinator!.Approved(id)) SendPeer(id, message, text);
    }
    private void SendPeer(string id, WireMessage message, string? serialized = null)
    {
        if (!peers.TryGetValue(id, out var peer) || !peer.IsControlOpen) { DepartPeer(id); return; }
        try
        {
            peer.SendControl(message, serialized);
            if (message.Type is "playback" or "ready" or "buffering")
                PlaybackDiagnostics.Record("control-transmitted", new { room = diagnosticId, transport = peer.DiagnosticId, message.Type, message.Data });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            PlaybackDiagnostics.Record("control-send-failed", new { room = diagnosticId, transport = peer.DiagnosticId, command = message.Type, error = ex.GetType().Name });
            DepartPeer(id, "Direct control send failed");
        }
    }
    // Drops the direct connection to a member. left: the room service says they are gone; otherwise only the
    // connection failed, they are still in the room, and ReconcileLinks makes a new one.
    private void DepartPeer(string id, string? reason = null, bool left = false)
    {
        artworkTransfers.Remove(id); voiceBudgets.TryRemove(id, out _); receiveRates.TryRemove(id, out _);
        var name = roster.FirstOrDefault(p => p.Id == id)?.Name ?? Snapshot?.People.FirstOrDefault(p => p.Id == id)?.Name;
        var wasNegotiated = negotiated.TryRemove(id, out _);
        if (peers.TryRemove(id, out var peer) || wasNegotiated || left && name is not null)
            PlaybackDiagnostics.Record("peer-departed", new { room = diagnosticId, transport = peer?.DiagnosticId, streamer = id == hostPeer, left,
                reason = DiagnosticReason(reason ?? "Direct control channel closed or unavailable"), negotiated = wasNegotiated });
        peer?.Dispose();
        if (left) { roster = roster.Where(p => p.Id != id).ToArray(); links.Remove(id); }
        // A connection that worked is retried at once; one that never opened waits.
        else if (links.TryGetValue(id, out var link)) link.RetryAt = Environment.TickCount64 + (wasNegotiated ? 1000 : 10000);
        var departure = left && name is not null ? new WireMessage("peer-left", Sender: id, Text: name) : null;
        if (Identity?.Host == true)
        {
            // Someone whose connection failed stays in the room, but no longer holds up playback for the others.
            if (left) coordinator!.Depart(id, clock.Now); else coordinator!.SetConnected(id, false, clock.Now);
            PublishHostState();
            if (departure is not null) Message?.Invoke(departure);
        }
        else if (id == hostPeer)
        {
            var wasRecovering = IsPeerRecovering;
            ResetHostLink();
            if (!left && DiscoveryOnline)
            {
                // Keep the video, timeline and cache while the connection to the streamer is replaced.
                if (Interlocked.Exchange(ref peerRecovering, 1) == 0 && !wasRecovering)
                {
                    Message?.Invoke(new("peer-recovering", Sender: id));
                    Status?.Invoke("Reconnecting to the stream…");
                }
                Link(id).RetryAt = 0; ReconcileLinks();
                return;
            }
            // Their video ends with them. The room service names who holds the streaming role next.
            if (left) hostPeer = null;
            if (Snapshot is { } snapshot)
                ApplySnapshot(snapshot with { People = snapshot.People.Where(p => !left || p.Id != id).ToArray(), Media = null, Playback = null, Queue = [], Tracks = null, LibraryActivity = null, LibraryBrowsing = false });
            if (departure is not null) Message?.Invoke(departure);
            Message?.Invoke(new("stream-ended", Sender: id, Text: name));
        }
        else if (departure is not null)
        {
            if (Snapshot is { } snapshot) ApplySnapshot(snapshot with { People = snapshot.People.Where(p => p.Id != id).ToArray() });
            Message?.Invoke(departure);
        }
    }
    private PeerTransport CreatePeer(string id, bool offer)
    {
        if (Identity is null) throw new InvalidOperationException("Missing room identity");
        if (peers.TryRemove(id, out var previous)) previous.Dispose();
        negotiated.TryRemove(id, out _);
        if (Identity.Host && coordinator?.SetConnected(id, false, clock.Now) == true) PublishHostState();
        var link = Link(id);
        link.CreatedAt = link.HeardAt = Environment.TickCount64;
        if (offer) link.Generation = ++linkGeneration;
        var generation = link.Generation;
        var peer = new PeerTransport(Identity.IceServers, Identity.ForceRelay);
        PlaybackDiagnostics.Record("peer-created", new { room = diagnosticId, transport = peer.DiagnosticId, offer });
        // Only whoever is streaming serves its video, and only to people in the room.
        peer.ResolveMedia = media => Identity?.Host == true && negotiated.ContainsKey(id) && roster.Any(p => p.Id == id)
            ? media.StartsWith("stream/", StringComparison.Ordinal) ? HostedStream?.Resolve(media)
            : HostedMedia?.Media.Id == media ? HostedMedia : assets.GetValueOrDefault(media) : null;
        peer.Signal += (type, data) => TryQueueDiscovery(new("signal", Target: id, Text: type, Data: data, Number: generation));
        // With a connection to everyone, only the streamer's says how the video arrives.
        peer.Status += text => { if (Identity?.Host == true || id == hostPeer) Status?.Invoke(text); };
        var inbox = new PeerInbox(() => id == hostPeer);
        peer.ControlMessage += message => PostPeer(inbox, new("peer", id, message, peer));
        peer.ControlClosed += () => Post(new("closed", id, Transport: peer));
        peer.VoiceFrame += (transport, frame) => ReceiveVoice(id, transport, frame);
        peers[id] = peer;
        if (offer) peer.StartOffer();
        _ = MonitorLibraryPeer(id, peer);
        _ = MonitorPeer(id, peer);
        return peer;
    }
    private async Task MonitorLibraryPeer(string id, PeerTransport peer)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); deadline.CancelAfter(TimeSpan.FromSeconds(35));
            await peer.LibraryReady.WaitAsync(deadline.Token);
            while (!negotiated.ContainsKey(id)) await Task.Delay(20, deadline.Token);
            Post(new("library-opened", id, Transport: peer));
        }
        catch (Exception) { /* The connection closed first; its replacement reports when it opens. */ }
    }
    private async Task MonitorPeer(string id, PeerTransport peer)
    {
        try
        {
            await peer.ControlReady.WaitAsync(TimeSpan.FromSeconds(20), lifetime.Token);
            Post(new("opened", id, Transport: peer));
            await Task.Delay(15000, lifetime.Token);
            Post(new("timeout", id, Transport: peer));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { PlaybackDiagnostics.Record("peer-connect-timeout", new { room = diagnosticId, transport = peer.DiagnosticId, error = ex.GetType().Name }); Post(new("timeout", id, Transport: peer)); }
    }
    public async Task<IMediaSource> GetSourceAsync(SharedMedia media, CancellationToken ct)
    {
        PlaybackDiagnostics.Record("media-source-request", new { room = diagnosticId, streamer = Identity?.Host == true, media.Extension, bytes = media.Length,
            directReady = directConnected.Task.IsCompletedSuccessfully });
        if (Identity?.Host == true) return HostedMedia?.Media.Id == media.Id ? HostedMedia : assets.GetValueOrDefault(media.Id) ?? throw new InvalidOperationException("No shared media selected");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token); deadline.CancelAfter(TimeSpan.FromSeconds(35));
        await directConnected.Task.WaitAsync(deadline.Token);
        var peer = hostPeer is not null && peers.TryGetValue(hostPeer, out var host) ? host : throw new IOException("The connection to whoever is playing the video is unavailable");
        await peer.Ready.WaitAsync(deadline.Token);
        PlaybackDiagnostics.Record("media-source-ready", new { room = diagnosticId, transport = peer.DiagnosticId });
        return new RemoteMediaSource(media, (offset, count, token) => ReadRemoteRangeAsync(media, offset, count, token));
    }
    private async Task<byte[]> ReadRemoteRangeAsync(SharedMedia media, long offset, int count, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        PeerTransport? failed = null;
        var busy = 0;
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                await directConnected.Task.WaitAsync(deadline.Token);
                if (Snapshot?.Media is not { } selected || selected.Id != media.Id && selected.Subtitles?.Any(s => s.Id == media.Id) != true)
                    throw new IOException("This video is no longer playing in the room.");
                var hostId = hostPeer;
                if (hostId is null || !peers.TryGetValue(hostId, out var peer) || peer == failed || !negotiated.ContainsKey(hostId))
                { await Task.Delay(50, deadline.Token); continue; }
                try
                {
                    await peer.Ready.WaitAsync(deadline.Token);
                    return await peer.ReadAsync(media.Id, offset, count, deadline.Token);
                }
                catch (PeerBusyException) when (!deadline.IsCancellationRequested && !peer.IsInterrupted && ++busy <= 40)
                {
                    // The host's range queue is full; back off briefly instead of aborting VLC's input.
                    if (busy == 1) PlaybackDiagnostics.Record("media-range-busy", new { room = diagnosticId, transport = peer.DiagnosticId });
                    await Task.Delay(Math.Min(500, 25 * busy), deadline.Token);
                }
                catch (Exception ex) when (!deadline.IsCancellationRequested && (peer.IsInterrupted || ex is TimeoutException))
                {
                    PlaybackDiagnostics.Record("media-range-retry", new { room = diagnosticId, transport = peer.DiagnosticId, error = ex.GetType().Name });
                    failed = peer;
                    Post(new("closed", hostId, Transport: peer));
                }
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && !lifetime.IsCancellationRequested)
        { throw new IOException("The connection to whoever is playing the video did not recover within 45 seconds.", ex); }
    }
    // Reads part of the room's converted stream from whoever converts it. A piece that is not converted yet is asked
    // for again until it is, or until the caller gives up.
    public async Task<byte[]> ReadStreamAsync(RoomStream stream, string resource, long offset, int count, CancellationToken ct)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        PeerTransport? failed = null;
        var interruptedAt = 0L;
        while (true)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (Snapshot?.Stream?.Id != stream.Id) throw new IOException("This stream is no longer playing in the room.");
            var hostId = hostPeer;
            if (hostId is null || !directConnected.Task.IsCompletedSuccessfully || !peers.TryGetValue(hostId, out var peer) || peer == failed || !negotiated.ContainsKey(hostId))
            {
                if (interruptedAt == 0) interruptedAt = Environment.TickCount64;
                if (Environment.TickCount64 - interruptedAt > 45000) throw new IOException("The connection to whoever is playing the video did not recover within 45 seconds.");
                await Task.Delay(50, cancellation.Token); continue;
            }
            interruptedAt = 0;
            try
            {
                await peer.Ready.WaitAsync(cancellation.Token);
                return await peer.ReadAsync(resource, offset, count, cancellation.Token);
            }
            catch (PeerBusyException) when (!cancellation.IsCancellationRequested) { await Task.Delay(100, cancellation.Token); }
            catch (Exception ex) when (!cancellation.IsCancellationRequested && (peer.IsInterrupted || ex is TimeoutException))
            {
                PlaybackDiagnostics.Record("media-range-retry", new { room = diagnosticId, transport = peer.DiagnosticId, error = ex.GetType().Name });
                failed = peer;
                Post(new("closed", hostId, Transport: peer));
            }
        }
    }
    private static string DiagnosticReason(string reason)
    {
        var safe = System.Text.RegularExpressions.Regex.Replace(reason, @"https?://\S+|[a-fA-F0-9]{24,}", "[redacted]");
        return safe[..Math.Min(safe.Length, 300)];
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        foreach (var id in negotiated.Keys)
            if (peers.TryGetValue(id, out var open) && open.IsControlOpen) try { open.SendControl(new("bye")); } catch { }
        lock (pendingStreams) { foreach (var pending in pendingStreams) pending.Completion.TrySetException(new IOException("Room connection closed")); pendingStreams.Clear(); }
        lifetime.Cancel(); discoveryLifetime.Cancel(); outgoing.Writer.TryComplete(); events.Writer.TryComplete(); connection?.Abort();
        if (readTask is not null) { try { await readTask; } catch { } }
        if (sendTask is not null) { try { await sendTask; } catch { } }
        if (controlTask is not null) { try { await controlTask; } catch { } }
        if (pingTask is not null) { try { await pingTask; } catch { } }
        foreach (var peer in peers.Values) peer.Dispose(); peers.Clear();
        if (connection is not null) await connection.DisposeAsync();
        directConnected.TrySetCanceled();
    }
}

public record HostedFileSelection(IMediaSource Media, IReadOnlyDictionary<string, IMediaSource> Assets);

internal sealed class MediaWithSubtitles(FileMediaSource source, SharedMedia[] subtitles) : ILocalMediaSource
{
    public string LocalPath => source.LocalPath;
    public SharedMedia Media { get; } = source.Media with { Subtitles = subtitles };
    public Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct) => source.ReadAsync(offset, count, ct);
}
internal sealed class UpdatedHostedMedia(ILocalMediaSource source, SharedMedia media) : ILocalMediaSource
{
    public string LocalPath => source.LocalPath;
    public SharedMedia Media => media;
    public Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct) => source.ReadAsync(offset, count, ct);
}
