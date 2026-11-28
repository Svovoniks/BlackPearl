using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using BlackPearl.Core;

// The room service: who is in each room, who streams, and WebRTC signaling between members. A room has no host.
// Anyone holding its invitation enters, every member connects to every other, and one member at a time holds the
// streaming role, which keeps the room's playback state. Video, voice and controls never pass through here.
// Apps from before that speak protocol 1, with a host who admits guests; ServeHosted still serves them.
var serverVersion = typeof(RoomProtocol).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
int[] roomProtocols = [1, RoomProtocol.Version];
// The updater (deploy/blackpearl-update.sh) asks a downloaded build what it is before it switches to it, which also
// shows that the build starts on this machine.
if (args.Contains("--describe"))
{
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { version = serverVersion, protocols = roomProtocols, remote = PhoneRemoteProtocol.Version }));
    return;
}
var builder = WebApplication.CreateBuilder(args);
// One line per event. Under systemd the journal timestamps each line and reads its level from the <N> prefix the systemd
// format starts it with, so `journalctl -p warning` works; elsewhere each line carries its own UTC time. ASP.NET's own
// per-request lines are left out: two to five for every request, with the server's domain in each. These are only
// defaults: Logging__* settings in the service's environment replace them (deploy/README.md, "Logs"). They are set on
// Microsoft rather than Microsoft.AspNetCore because systemd ignores an environment variable whose name has a dot, so
// Logging__LogLevel__Microsoft is the one that can bring the request lines back.
var journal = WritesToJournal();
builder.Configuration.Sources.Insert(0, new Microsoft.Extensions.Configuration.Memory.MemoryConfigurationSource
{
    InitialData = new Dictionary<string, string?>
    {
        ["Logging:LogLevel:Default"] = "Information",
        ["Logging:LogLevel:Microsoft"] = "Warning",
        ["Logging:LogLevel:Microsoft.Hosting.Lifetime"] = "Information",
        ["Logging:Console:FormatterName"] = journal ? "systemd" : "simple",
        ["Logging:Console:FormatterOptions:SingleLine"] = "true",
        ["Logging:Console:FormatterOptions:UseUtcTimestamp"] = "true",
        ["Logging:Console:FormatterOptions:TimestampFormat"] = journal ? null : "yyyy-MM-dd HH:mm:ss.fff ",
    },
});
// systemd sets JOURNAL_STREAM to the device and inode of the journal socket it gave the service as stdout or stderr.
// Processes started from such a service, such as a terminal in a desktop session, inherit the variable, so the journal
// format is used only while this process's own output still is that socket (systemd.exec(5)). Where /proc can't say,
// the variable decides.
static bool WritesToJournal()
{
    var stream = Environment.GetEnvironmentVariable("JOURNAL_STREAM");
    if (string.IsNullOrEmpty(stream)) return false;
    if (!Directory.Exists("/proc/self/fd")) return true;
    var socket = "socket:[" + stream[(stream.IndexOf(':') + 1)..] + "]";
    foreach (var output in new[] { "/proc/self/fd/1", "/proc/self/fd/2" })
        try { if (new FileInfo(output).LinkTarget == socket) return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    return false;
}
// Errors go to Sentry when the server sets BLACKPEARL_SENTRY_DSN; without it nothing is sent. CrashReportPrivacy (Core)
// decides what reports leave out: addresses, request details beyond the path, breadcrumbs, and paths and ids in error text.
if (builder.Configuration[AppEnvironment.Prefix + "SENTRY_DSN"] is { Length: > 0 } sentryDsn)
    builder.WebHost.UseSentry(options =>
    {
        options.Dsn = sentryDsn;
        options.Release = "blackpearl-server@" + serverVersion;
        options.Environment = builder.Configuration[AppEnvironment.Prefix + "SENTRY_ENVIRONMENT"] ?? "production";
        CrashReportPrivacy.Apply(options);
        options.MaxRequestBodySize = Sentry.Extensibility.RequestSize.None;
        options.TracesSampleRate = 0;
        options.SetBeforeSend(evt => CrashReportPrivacy.Scrub(evt));
    });
// Requests over the limit are counted, and logged at most once a minute, so a flood can't flood the log as well.
long rateLimited = 0, rateLimitLogged = 0;
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.OnRejected = (rejected, _) =>
    {
        Interlocked.Increment(ref rateLimited);
        var now = Environment.TickCount64; var last = Volatile.Read(ref rateLimitLogged);
        if ((last == 0 || now - last >= 60000) && Interlocked.CompareExchange(ref rateLimitLogged, now, last) == last)
            ServerLog.RateLimited(rejected.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("BlackPearl.Server"),
                Interlocked.Exchange(ref rateLimited, 0), rejected.HttpContext.Request.Path.Value ?? "");
        return ValueTask.CompletedTask;
    };
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
// A task that failed with nobody awaiting it is a bug; say so in the service's log as well as in any error report.
var unobservedLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("UnobservedTask");
TaskScheduler.UnobservedTaskException += (_, e) => unobservedLog.LogError(e.Exception, "Unobserved task exception");
var roomLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("BlackPearl.Rooms");
var remoteLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("BlackPearl.Remote");
// Behind the bundled reverse proxy, rate limits apply to the client address it reports; only loopback proxies are trusted.
app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto });
app.UseRateLimiter();
// Half-open sockets close after a missed pong, so a member who left is not shown in the room for minutes.
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20), KeepAliveTimeout = TimeSpan.FromSeconds(30) });
var rooms = new ConcurrentDictionary<string, Room>();
// Settings are BLACKPEARL_*; servers configured while the app was called Watchroom keep their WATCHROOM_* names.
string? Config(string name) => builder.Configuration[AppEnvironment.Prefix + name] is { Length: > 0 } value ? value : builder.Configuration[AppEnvironment.FormerPrefix + name];
int Setting(string? text, int fallback) => int.TryParse(text, out var value) && value > 0 ? value : fallback;
// Temporary rooms are not saved, so an empty one only waits briefly for its members to return.
var emptyRoomGraceMs = Setting(Config("EMPTY_ROOM_SECONDS"), 300) * 1000L;
var persistentPerHour = Setting(Config("PERSISTENT_ROOMS_PER_HOUR"), 20);
const int TemporaryRoomLimit = 1000, PersistentRoomLimit = 10000;
var persistentCreations = new ConcurrentDictionary<string, Queue<long>>();
bool AllowPersistent(string address, long now)
{
    var times = persistentCreations.GetOrAdd(address, _ => new());
    lock (times)
    {
        while (times.Count > 0 && now - times.Peek() > 3600000) times.Dequeue();
        if (times.Count >= persistentPerHour) return false;
        times.Enqueue(now); return true;
    }
}
void SweepRooms(long now)
{
    foreach (var pair in rooms)
        if (!pair.Value.Persistent)
            lock (pair.Value.Gate)
                if ((pair.Value.Expires < now || !pair.Value.Occupied && now - pair.Value.EmptySince >= emptyRoomGraceMs) && rooms.TryRemove(pair))
                    ServerLog.RoomClosed(roomLog, pair.Value.Tag, pair.Value.Expires < now ? "6 hours after it was created" : $"empty for {(now - pair.Value.EmptySince) / 1000} s");
    foreach (var pair in persistentCreations)
        lock (pair.Value) if (pair.Value.All(at => now - at > 3600000)) persistentCreations.TryRemove(pair);
}
_ = Task.Run(async () =>
{
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Setting(Config("SWEEP_SECONDS"), 60)));
    try { while (await timer.WaitForNextTickAsync(app.Lifetime.ApplicationStopping)) SweepRooms(Wire.Now); }
    catch (OperationCanceledException) { }
});
var dataDirectory = Config("DATA") ?? Path.Combine(AppContext.BaseDirectory, "data");
var persistentRooms = new PersistentRooms(dataDirectory);
// Sockets that have connected to /room and not yet ended: the people a restart would disconnect from their rooms.
// Phone remote links are left out, since a PC with the remote turned on keeps one open all the time.
var roomConnections = 0;
// status.json tells the updater which version runs and whether anyone is in a room. It is written every 10 seconds,
// so a stale one means the service has stopped.
var statusPath = Path.Combine(dataDirectory, "status.json");
void WriteStatus()
{
    try
    {
        Directory.CreateDirectory(dataDirectory);
        var temporary = statusPath + ".tmp";
        File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(new
        {
            version = serverVersion, protocols = roomProtocols, roomConnections = Volatile.Read(ref roomConnections), written = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        }));
        File.Move(temporary, statusPath, true);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { app.Logger.LogWarning("Could not write {Path}: {Error}", statusPath, ex.Message); }
}
WriteStatus();
_ = Task.Run(async () =>
{
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Setting(Config("STATUS_SECONDS"), 10)));
    try { while (await timer.WaitForNextTickAsync(app.Lifetime.ApplicationStopping)) WriteStatus(); }
    catch (OperationCanceledException) { }
});
foreach (var saved in persistentRooms.All())
{
    var restored = new Room(saved.Code, true) { Name = saved.Name, OwnerHash = saved.HostKeyHash, SharedControls = saved.SharedControls, Unopened = true };
    foreach (var guest in saved.Guests ?? []) restored.Members[guest.Id] = guest.Name;
    // Rooms saved before members had names list only their ids.
    foreach (var id in saved.ApprovedGuests ?? []) restored.Members.TryAdd(id, "Guest");
    foreach (var removed in saved.Banned ?? []) { restored.Banned[removed.Id] = removed.Name; restored.Members.Remove(removed.Id); }
    rooms[saved.Code] = restored;
}
ServerLog.Starting(app.Logger, serverVersion, string.Join(", ", roomProtocols), PhoneRemoteProtocol.Version, rooms.Count,
    rooms.Values.Sum(room => room.Members.Count), rooms.Values.Sum(room => room.Banned.Count));
void SaveRoom(Room room) { if (room.Persistent) persistentRooms.Update(room.Code, room.MemberList(), room.BannedList(), room.Name, room.SharedControls); }
static bool ValidRoomName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 100 && !name.Any(char.IsControl);
var cloudflare = CloudflareTurn.Create(builder.Configuration["CLOUDFLARE_TURN_KEY_ID"], builder.Configuration["CLOUDFLARE_TURN_API_TOKEN"],
    builder.Configuration["CLOUDFLARE_TURN_API"] ?? "https://rtc.live.cloudflare.com/v1",
    TimeSpan.FromSeconds(Setting(builder.Configuration["CLOUDFLARE_TURN_TTL_SECONDS"], 86400)), TimeSpan.FromSeconds(Setting(builder.Configuration["CLOUDFLARE_TURN_RETRY_SECONDS"], 60)), app.Logger);
if (cloudflare is not null) _ = cloudflare.RunAsync(app.Lifetime.ApplicationStopping);
string[] IceServers(string peer)
{
    var urls = new List<string>();
    var stun = Config("STUN");
    if (!string.IsNullOrWhiteSpace(stun)) urls.Add(stun);
    if (cloudflare is not null) urls.AddRange(cloudflare.Current());
    var turn = Config("TURN");
    var secret = Config("TURN_SECRET");
    if (!string.IsNullOrWhiteSpace(turn) && !string.IsNullOrWhiteSpace(secret))
    {
        var username = $"{DateTimeOffset.UtcNow.AddHours(8).ToUnixTimeSeconds()}:{peer}";
        var credential = Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(username)));
        foreach (var address in turn.Split(';', StringSplitOptions.RemoveEmptyEntries)) urls.Add(CloudflareTurn.WithCredentials(address, username, credential));
    }
    return urls.ToArray();
}
bool forceRelay = Config("FORCE_RELAY") == "true";
var remoteLinks = new RemoteLinks(IceServers, forceRelay, remoteLog);
// protocol is the newest the service speaks, which newer apps require; protocols lists every one it still serves.
// remote is the phone remote protocol (PHONE-REMOTE.md) this service introduces phones and PCs with.
app.MapGet("/health", () => Results.Ok(new { status = "ok", protocol = RoomProtocol.Version, protocols = roomProtocols, persistentRooms = true, remote = PhoneRemoteProtocol.Version,
    relayConfigured = cloudflare?.HasRelay == true || !string.IsNullOrEmpty(Config("TURN")),
    stunConfigured = cloudflare is not null || !string.IsNullOrEmpty(Config("STUN")) }));
// Who is in each room right now, for people who hold its invitation. The app shows it before they enter.
app.MapPost("/presence", ([FromBody] string[] codes) =>
{
    if (codes.Length > 32) return Results.BadRequest();
    var found = new Dictionary<string, RoomPresenceInfo>();
    foreach (var code in codes.Select(code => (code ?? "").Trim().ToUpperInvariant()).Distinct())
        if (rooms.TryGetValue(code, out var room) && room.Expires >= Wire.Now)
            lock (room.Gate) found[code] = new(room.Name, room.Hosted is { } hosted ? hosted.People.Values.Where(p => p.Approved).Select(p => p.Name).ToArray()
                : room.People.Values.OrderBy(p => p.Arrived).Select(p => p.Name).ToArray());
    return Results.Ok(found);
});
// Rooms as apps on protocol 1 know them: one host, who admits each guest, and guests connect only to the
// host. Those apps say create, join or resume. A room serves one kind of app at a time: while people on older apps are
// in it, newer apps are turned away with the reason, and the other way round. Its invitation, creator, name and the
// people who belong to it are the same either way, so a room carries over as its people update.
async Task ServeHosted(HttpContext context, WebSocket socket, CancellationTokenSource lifetime, WireMessage first, string connectionId, Action<Connection, Action<Ending>> entered)
{
    var name = (first.Text ?? "Guest").Trim();
    if (name.Length is < 1 or > 40 || name.Any(char.IsControl)) throw new InvalidDataException("Invalid display name");
    var host = first.Type is "create" or "resume";
    string? hostKey = null; Room? room;
    if (first.Type == "create")
    {
        // Only live temporary rooms count toward the temporary cap; saved rooms have their own limits.
        if (first.Target == "persistent" ? persistentRooms.Count >= PersistentRoomLimit || !AllowPersistent(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", Wire.Now)
            : rooms.Values.Count(x => !x.Persistent && x.Expires >= Wire.Now) >= TemporaryRoomLimit)
            throw new InvalidDataException(first.Target == "persistent" ? "Persistent room limit reached. Try again later." : "Server room limit reached");
        room = new Room(Convert.ToHexString(RandomNumberGenerator.GetBytes(12)), first.Target == "persistent");
        if (room.Persistent) { hostKey = persistentRooms.Create(room.Code); room.OwnerHash = PersistentRooms.Hash(hostKey); }
        rooms[room.Code] = room;
        ServerLog.RoomCreated(roomLog, connectionId, room.Kind, room.Tag, 1);
    }
    else if (!rooms.TryGetValue((first.Data ?? "").Trim().ToUpperInvariant(), out room) || room.Expires < Wire.Now) throw new InvalidDataException("Invitation is invalid or expired");
    if (first.Type == "resume")
    {
        if (!room.Persistent || room.OwnerHash is null || first.Target is not { Length: 64 } supplied ||
            !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(room.OwnerHash), SHA256.HashData(Encoding.UTF8.GetBytes(supplied)))) throw new InvalidDataException("Host key is invalid");
        hostKey = first.Target;
    }
    var guestKey = host ? null : first.Sender is { Length: 64 } key && key.All(Uri.IsHexDigit) ? key : PersistentRooms.NewKey();
    var member = new HostedMember(Guid.NewGuid().ToString("N"), name, host, socket, lifetime, guestKey) { LogId = connectionId };
    var inRoom = false;
    entered(member, ending =>
    {
        bool left, empty; int present;
        lock (room.Gate) { left = room.LeaveHosted(member); present = room.Hosted?.People.Count ?? 0; empty = left && !room.Occupied; }
        if (!inRoom) ServerLog.TurnedAway(roomLog, member.LogId, room.Tag, ending.Reason);
        else ServerLog.Left(roomLog, ending.Level, member.LogId, member.Who, room.Tag, ServerLog.Seconds(member.Opened), ending.Reason, present, member.Messages, member.Relayed, member.Dropped);
        if (empty) ServerLog.RoomEmpty(roomLog, room.Tag, room.Kind);
    });
    var sender = member.SendLoop();
    string role; int entering; long emptyFor = -1; var unopened = false;
    lock (room.Gate)
    {
        // The sweep removes rooms under this lock; never admit a member into a removed room.
        if (!rooms.TryGetValue(room.Code, out var live) || live != room) throw new InvalidDataException("Invitation is invalid or expired");
        if (room.People.Count > 0) throw new InvalidDataException("The people in this room use a newer BlackPearl. Update the app to join them.");
        if (!room.Occupied && first.Type != "create") { emptyFor = (Wire.Now - room.EmptySince) / 1000; unopened = room.Unopened; room.Unopened = false; }
        var session = room.Hosted ??= new();
        if (session.People.Count >= 9) throw new InvalidDataException("Room is full");
        if (host && session.People.Values.Any(x => x.Host)) throw new InvalidDataException("The host is already connected to this room");
        // Someone who belongs to the room, admitted by its host or entered from a newer app, needs no fresh approval.
        if (!host && member.GuestHash != room.OwnerHash && room.Members.ContainsKey(member.GuestHash!) && session.People.Values.Count(x => x.Approved) < 5 &&
            !session.People.Values.Any(x => x.GuestHash == member.GuestHash)) member.Approved = true;
        session.People.Add(member.Id, member); inRoom = true;
        member.Send(new(member.Approved && !host ? "admitted" : "welcome", Data: Wire.Serialize(new Welcome(room.Code, member.Id, host, member.Approved ? IceServers(member.Id) : [], forceRelay, hostKey, guestKey))));
        room.HostedSnapshot();
        if (!host && member.Approved) session.People.Values.FirstOrDefault(x => x.Host)?.Send(new("connect", Target: member.Id));
        if (host) foreach (var guest in session.People.Values.Where(x => !x.Host && x.Approved)) member.Send(new("connect", Target: guest.Id));
        role = host ? "host" : member.Approved ? "a guest who belongs to the room" : "a guest waiting for the host"; entering = session.People.Count;
    }
    if (unopened) ServerLog.RoomOpenedSinceStart(roomLog, member.LogId, room.Tag, emptyFor);
    else if (emptyFor >= 0) ServerLog.RoomOpened(roomLog, member.LogId, room.Tag, emptyFor);
    ServerLog.Entered(roomLog, member.LogId, member.Who, room.Tag, role, 1, entering);
    // Token bucket: the host's burst covers resume signaling for every guest; sustained floods still disconnect.
    var (burst, perSecond) = host ? (400.0, 160.0) : (120.0, 80.0);
    var tokens = burst; var refilled = Wire.Now;
    while (!lifetime.IsCancellationRequested)
    {
        var message = await SocketMessages.ReadAsync(socket, lifetime.Token); if (message is null) break;
        member.Messages++;
        var now = Wire.Now; tokens = Math.Min(burst, tokens + (now - refilled) * perSecond / 1000); refilled = now;
        if (--tokens < 0) throw new InvalidDataException("Message rate limit exceeded");
        if (message.Type == "ping") { await member.ReplyAsync(new("pong", Number: message.Number, Data: Wire.Now.ToString())); continue; }
        lock (room.Gate)
        {
            if (!member.Approved || room.Hosted is not { } session || !session.People.ContainsKey(member.Id))
            {
                if (message.Type == "signal") { member.Dropped++; ServerLog.RoomSignalDropped(roomLog, member.LogId, room.Tag, member.Approved ? "the sender is no longer in the room" : "the sender is waiting for the host"); }
                continue;
            }
            switch (message.Type)
            {
                case "reconnect" when !member.Host && member.AllowBroadcast():
                    room.HostedSnapshot();
                    session.People.Values.FirstOrDefault(x => x.Host)?.Send(new("connect", Target: member.Id));
                    break;
                case "admit" when member.Host:
                    if (session.People.TryGetValue(message.Target ?? "", out var guest) && !guest.Approved && session.People.Values.Count(x => x.Approved) < 5)
                    {
                        guest.Approved = true;
                        room.Banned.Remove(guest.GuestHash!); room.Members[guest.GuestHash!] = guest.Name; SaveRoom(room);
                        guest.Send(new("admitted", Data: Wire.Serialize(new Welcome(room.Code, guest.Id, false, IceServers(guest.Id), forceRelay, GuestKey: guest.GuestKey))));
                        member.Send(new("connect", Target: guest.Id)); room.HostedSnapshot();
                        ServerLog.Admitted(roomLog, member.LogId, guest.Who, room.Tag, session.People.Values.Count(x => x.Approved && !x.Host));
                    }
                    break;
                case "remove" when member.Host:
                    var removed = session.People.Values.FirstOrDefault(x => !x.Host && (x.Id == message.Target || x.GuestHash == message.Target));
                    if (removed is not null)
                    {
                        room.Members.Remove(removed.GuestHash!); SaveRoom(room); removed.Approved = false;
                        removed.End(new($"removed by the host (connection {member.LogId})", LogLevel.Information));
                        session.Broadcast(new("peer-left", Sender: removed.Id)); room.HostedSnapshot();
                        ServerLog.Removed(roomLog, member.LogId, removed.Who, room.Tag, "was present");
                    }
                    else if (message.Target != room.OwnerHash && room.Members.Remove(message.Target ?? ""))
                    { SaveRoom(room); room.HostedSnapshot(); ServerLog.Removed(roomLog, member.LogId, ServerLog.Tag(message.Target!), room.Tag, "was not present"); }
                    break;
                case "signal":
                    if (session.People.TryGetValue(message.Target ?? "", out var target) && target.Approved && (target.Host || member.Host)) { target.Send(message with { Sender = member.Id }); member.Relayed++; }
                    else { member.Dropped++; ServerLog.RoomSignalDropped(roomLog, member.LogId, room.Tag, target is null ? "no one in the room has that id" : "only the host and its admitted guests signal each other"); }
                    break;
                case "media" when member.Host:
                    var media = Wire.Read<SharedMedia>(message.Data!);
                    if (media.Length <= 0 || media.Title.Length > 300 || media.Id.Length > 128 || media.Extension.Length > 16) throw new InvalidDataException("Invalid media");
                    session.Media = media; session.ResumeWhenReady = false;
                    foreach (var p in session.People.Values) p.Ready = false;
                    session.Playback = new(++session.Revision, media.Id, false, 0, Wire.Now); room.HostedSnapshot();
                    break;
                case "settings" when member.Host:
                case "controls" when member.Host:
                    if (message.Text is not null)
                    {
                        if (!ValidRoomName(message.Text)) throw new InvalidDataException("Invalid room name");
                        room.Name = message.Text.Trim();
                    }
                    room.SharedControls = message.Number == 1; SaveRoom(room); room.HostedSnapshot(); break;
                case "queue" when member.Host:
                    var queue = Wire.Read<string[]>(message.Data!);
                    if (queue.Length > 1000 || queue.Any(x => x is null || x.Length > 300)) throw new InvalidDataException("Invalid queue");
                    session.Queue = queue; room.HostedSnapshot(); break;
                case "playback" when (member.Host || room.SharedControls) && member.AllowBroadcast():
                    var desired = Wire.Read<PlaybackState>(message.Data!);
                    if (session.Media is null || desired.MediaId != session.Media.Id || desired.PositionMs < 0 || desired.PositionMs > 7 * 86400000L) break;
                    session.ResumeWhenReady = desired.Playing && session.People.Values.Any(x => x.Approved && !x.Ready);
                    session.Playback = desired with { Revision = ++session.Revision, Playing = desired.Playing && !session.ResumeWhenReady, AtUnixMs = Wire.Now + (desired.Playing && !session.ResumeWhenReady ? 200 : 0) };
                    session.Broadcast(new("playback", Data: Wire.Serialize(session.Playback))); break;
                // Readiness broadcasts reach every member; ignore unchanged state and bound each member's rate.
                case "buffering" when (member.Ready || session.Playback is { Playing: true }) && member.AllowBroadcast():
                    member.Ready = false;
                    if (session.Playback is { Playing: true } current)
                    {
                        session.ResumeWhenReady = true;
                        session.Playback = current with { Revision = ++session.Revision, Playing = false, PositionMs = SyncMath.TargetPosition(current, Wire.Now), AtUnixMs = Wire.Now };
                        session.Broadcast(new("playback", Data: Wire.Serialize(session.Playback)));
                        session.Broadcast(new("notice", Text: $"{member.Name} is buffering. Playback resumes when everyone is ready."));
                    }
                    room.HostedSnapshot();
                    break;
                case "ready" when !member.Ready && member.AllowBroadcast():
                    member.Ready = true; session.TryResume(); room.HostedSnapshot(); break;
            }
        }
    }
}
// Rooms as apps on protocol 2 know them: no host, anyone with the invitation enters, and one member at a time streams.
async Task ServeRoom(HttpContext context, WebSocket socket, CancellationTokenSource lifetime, WireMessage? first, string connectionId, Action<Connection, Action<Ending>> entered)
{
    // Apps from before rooms lost their host say create, join or resume, and get the rooms they know.
    if (first?.Type is "create" or "join" or "resume")
    {
        await ServeHosted(context, socket, lifetime, first, connectionId, entered);
        return;
    }
    if (first is null || first.Type != "enter") throw new InvalidDataException("Expected create, join, resume or enter");
    var request = Wire.Read<EnterRequest>(first.Data ?? "{}");
    if (request.Protocol != RoomProtocol.Version)
        throw new InvalidDataException(request.Protocol > RoomProtocol.Version ? "This room server is older than your BlackPearl. Whoever runs it needs to update it."
            : "Rooms changed in a newer BlackPearl. Update the app to use rooms.");
    var name = (first.Text ?? "Guest").Trim();
    if (name.Length is < 1 or > 40 || name.Any(char.IsControl)) throw new InvalidDataException("Invalid display name");
    if (request.RoomName is not null && !ValidRoomName(request.RoomName)) throw new InvalidDataException("Invalid room name");
    var key = request.Key is { Length: 64 } supplied && supplied.All(Uri.IsHexDigit) ? supplied : null;
    Room? room; var created = false;
    if (request.Room is null)
    {
        // Only live temporary rooms count toward the temporary cap; saved rooms have their own limits.
        if (!request.Temporary ? persistentRooms.Count >= PersistentRoomLimit || !AllowPersistent(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", Wire.Now)
            : rooms.Values.Count(x => !x.Persistent && x.Expires >= Wire.Now) >= TemporaryRoomLimit)
            throw new InvalidDataException(request.Temporary ? "Server room limit reached" : "Persistent room limit reached. Try again later.");
        room = new Room(Convert.ToHexString(RandomNumberGenerator.GetBytes(12)), !request.Temporary) { Name = request.RoomName?.Trim() };
        // Whoever creates a room gets the key that marks them as its creator.
        key = room.Persistent ? persistentRooms.Create(room.Code, room.Name) : PersistentRooms.NewKey();
        room.OwnerHash = PersistentRooms.Hash(key);
        rooms[room.Code] = room; created = true;
        ServerLog.RoomCreated(roomLog, connectionId, room.Kind, room.Tag, RoomProtocol.Version);
    }
    else if (!rooms.TryGetValue(request.Room.Trim().ToUpperInvariant(), out room) || room.Expires < Wire.Now) throw new InvalidDataException("Invitation is invalid or expired");
    key ??= PersistentRooms.NewKey();
    var member = new Member(Guid.NewGuid().ToString("N"), name, socket, lifetime, key) { LogId = connectionId };
    entered(member, ending =>
    {
        bool left, empty; int remaining;
        lock (room.Gate) { left = room.Leave(member); remaining = room.People.Count; empty = left && !room.Occupied; }
        // Arrived is set once the member is in the room; before that, they were turned away at the door.
        if (member.Arrived == 0) ServerLog.TurnedAway(roomLog, member.LogId, room.Tag, ending.Reason);
        else ServerLog.Left(roomLog, ending.Level, member.LogId, member.Who, room.Tag, ServerLog.Seconds(member.Opened), ending.Reason, remaining, member.Messages, member.Relayed, member.Dropped);
        if (empty) ServerLog.RoomEmpty(roomLog, room.Tag, room.Kind);
    });
    var sender = member.SendLoop();
    var replaced = new List<string>(); long emptyFor = -1; var unopened = false; int count; string role;
    lock (room.Gate)
    {
        // The sweep removes rooms under this lock; never let a member into a removed room.
        if (!rooms.TryGetValue(room.Code, out var live) || live != room) throw new InvalidDataException("Invitation is invalid or expired");
        if (room.Hosted is { People.Count: > 0 }) throw new InvalidDataException("The people in this room use an older BlackPearl. Once they update, you can all join together.");
        if (room.Banned.ContainsKey(member.Hash)) throw new InvalidDataException("You were removed from this room. Its creator can let you back in.");
        member.Owner = member.Hash == room.OwnerHash;
        if (!created && !room.Occupied) { emptyFor = (Wire.Now - room.EmptySince) / 1000; unopened = room.Unopened; room.Unopened = false; }
        // Someone entering again replaces their earlier connection: usually one the network dropped before the
        // service noticed. Refusing instead would lock them out until that connection timed out.
        foreach (var earlier in room.People.Values.Where(x => x.Hash == member.Hash).ToArray())
        {
            room.Leave(earlier);
            earlier.Close("You entered this room from another BlackPearl window.", new($"replaced by connection {member.LogId}, which entered as the same person", LogLevel.Information));
            replaced.Add(earlier.LogId);
        }
        if (room.People.Count >= RoomProtocol.MaxPresent) throw new InvalidDataException($"Room is full ({RoomProtocol.MaxPresent} people)");
        if (member.Owner && request.RoomName is not null) room.Name = request.RoomName.Trim();
        room.Name ??= "Room " + room.Code[..6];
        room.Remember(member);
        room.People.Add(member.Id, member); member.Arrived = ++room.Arrivals;
        // With nobody streaming, the role rests with whoever has been here longest.
        if (room.HostId is null) { room.HostId = member.Id; room.Epoch++; }
        SaveRoom(room);
        member.Send(new("welcome", Data: Wire.Serialize(new Welcome(room.Code, member.Id, room.HostId == member.Id, IceServers(member.Id), forceRelay, member.Owner ? key : null, key, member.Owner))));
        room.Snapshot();
        role = (member.Owner ? "its creator" : "a member") + (room.HostId == member.Id ? ", holding the streaming role" : ""); count = room.People.Count;
    }
    if (unopened) ServerLog.RoomOpenedSinceStart(roomLog, member.LogId, room.Tag, emptyFor);
    else if (emptyFor >= 0) ServerLog.RoomOpened(roomLog, member.LogId, room.Tag, emptyFor);
    foreach (var earlier in replaced) ServerLog.Replaced(roomLog, member.LogId, member.Who, room.Tag, earlier);
    ServerLog.Entered(roomLog, member.LogId, member.Who, room.Tag, role, RoomProtocol.Version, count);
    // Token bucket: the burst covers signaling with every other member at once; sustained floods still disconnect.
    const double Burst = 400, PerSecond = 160;
    var tokens = Burst; var refilled = Wire.Now;
    while (!lifetime.IsCancellationRequested)
    {
        var message = await SocketMessages.ReadAsync(socket, lifetime.Token); if (message is null) break;
        member.Messages++;
        var now = Wire.Now; tokens = Math.Min(Burst, tokens + (now - refilled) * PerSecond / 1000); refilled = now;
        if (--tokens < 0) throw new InvalidDataException("Message rate limit exceeded");
        if (message.Type == "ping") { await member.ReplyAsync(new("pong", Number: message.Number, Data: Wire.Now.ToString())); continue; }
        lock (room.Gate)
        {
            // A connection that was replaced or removed keeps its socket for a moment; it no longer acts in the room.
            if (!room.People.ContainsKey(member.Id))
            {
                if (message.Type == "signal") { member.Dropped++; ServerLog.RoomSignalDropped(roomLog, member.LogId, room.Tag, "the sender is no longer in the room"); }
                continue;
            }
            switch (message.Type)
            {
                case "signal":
                    if (room.People.TryGetValue(message.Target ?? "", out var target) && target != member) { target.Send(message with { Sender = member.Id }); member.Relayed++; }
                    else { member.Dropped++; ServerLog.RoomSignalDropped(roomLog, member.LogId, room.Tag, "no one else in the room has that id"); }
                    break;
                // Asks another member to start a new direct connection to this one.
                case "reconnect" when member.AllowBroadcast():
                    if (room.People.TryGetValue(message.Target ?? "", out var peer) && peer != member) peer.Send(new("connect", Target: member.Id));
                    break;
                // Starting a video takes the streaming role from whoever holds it.
                case "claim" when room.HostId != member.Id && member.AllowBroadcast():
                    room.HostId = member.Id; room.Epoch++; room.Snapshot();
                    ServerLog.Claimed(roomLog, member.LogId, room.Tag, room.Epoch); break;
                case "rename" when member.Owner && member.AllowBroadcast():
                    if (!ValidRoomName(message.Text)) throw new InvalidDataException("Invalid room name");
                    room.Name = message.Text!.Trim(); SaveRoom(room); room.Snapshot(); break;
                // The creator removes someone by their connection or by their member id. A removed member can't
                // enter again until the creator lets them back in.
                case "remove" when member.Owner:
                    var present = room.People.Values.FirstOrDefault(x => x != member && (x.Id == message.Target || x.Hash == message.Target));
                    var removedId = present?.Hash ?? message.Target ?? "";
                    if (removedId == room.OwnerHash || !room.Members.Remove(removedId, out var removedName)) break;
                    room.Banned[removedId] = removedName;
                    if (present is not null)
                    {
                        room.Leave(present, snapshot: false);
                        present.Close("You were removed from this room.", new($"removed by the room's creator (connection {member.LogId})", LogLevel.Information));
                    }
                    SaveRoom(room); room.Snapshot();
                    ServerLog.Removed(roomLog, member.LogId, ServerLog.Tag(removedId), room.Tag, present is null ? "was not present" : "was present");
                    break;
                case "restore" when member.Owner:
                    if (room.Banned.Remove(message.Target ?? "")) { SaveRoom(room); room.Snapshot(); ServerLog.LetBackIn(roomLog, member.LogId, ServerLog.Tag(message.Target!), room.Tag); }
                    break;
            }
        }
    }
}
// Why a socket's connection was cancelled. A receive that is cancelled aborts the socket, which also cancels the request,
// so the service's own time limits are asked first. A send that failed first ends the connection by cancelling it.
Ending Cancelled(Exception? error, bool silent, CancellationTokenSource limit, Connection? connection, WebSocket socket) =>
    app.Lifetime.ApplicationStopping.IsCancellationRequested ? new("the server is stopping", LogLevel.Information)
    : silent ? new("sent nothing within 10 s of connecting", LogLevel.Warning)
    : limit.IsCancellationRequested ? new("reached the 6-hour connection limit; the app connects again", LogLevel.Information)
    : MissedKeepAlive(error, socket) ? Ending.MissedKeepAlive
    : connection?.SendFailed ?? Ending.Cut;
// A missed keep-alive aborts the socket under its pending receive, which then throws Kestrel's ConnectionAbortedException
// or, as often, the plain cancellation of the request it also aborts: whichever reaches it first. The socket remembers why
// it was aborted, though: using it again throws with the keep-alive timeout, a Faulted WebSocketException, inside. An
// aborted socket fails a receive before it starts, so this never waits.
static bool MissedKeepAlive(Exception? error, WebSocket socket)
{
    if (error is Microsoft.AspNetCore.Connections.ConnectionAbortedException) return true;
    if (socket.State != WebSocketState.Aborted) return false;
    Exception? failure;
    try
    {
        var probe = socket.ReceiveAsync(new ArraySegment<byte>(new byte[1]), CancellationToken.None);
        if (!probe.IsCompleted) return false;
        failure = probe.Exception;
    }
    catch (Exception ex) { failure = ex; }
    for (var e = failure; e is not null; e = e is AggregateException { InnerExceptions.Count: 1 } single ? single.InnerExceptions[0] : e.InnerException)
        if (e is WebSocketException { WebSocketErrorCode: WebSocketError.Faulted }) return true;
    return false;
}
// Both kinds of socket: read the first message within 10 seconds, hand the socket to serve, and once it ends, tell an app
// that was turned away why, take the connection out of wherever it entered, and log how it ended.
async Task ServeSocket(HttpContext context, ILogger log, Func<HttpContext, WebSocket, CancellationTokenSource, WireMessage?, string, Action<Connection, Action<Ending>>, Task> serve)
{
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    // Relay credentials are handed out when a member enters and last at least twice this long, so a connection ends
    // before they can; the app enters again by itself.
    using var limit = new CancellationTokenSource(TimeSpan.FromHours(6));
    using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, limit.Token);
    using var handshake = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var id = ServerLog.NewConnectionId(); var opened = System.Diagnostics.Stopwatch.GetTimestamp();
    // Whoever this connection is once it has entered, how it leaves, and why it ended.
    Connection? connection = null; Action<Ending>? leave = null; Ending? ending = null; var waiting = true;
    try
    {
        WireMessage? first;
        using (var reading = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, handshake.Token))
            first = await SocketMessages.ReadAsync(socket, reading.Token);
        waiting = false;
        await serve(context, socket, lifetime, first, id, (entered, leaving) => { connection = entered; leave = leaving; });
        ending = lifetime.IsCancellationRequested ? Cancelled(null, false, limit, connection, socket) : Ending.ClosedByApp;
    }
    catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or FormatException)
    {
        ending = Ending.Refused(ex);
        if (connection is not null) await connection.RefuseAsync(ex.Message);
        else if (socket.State == WebSocketState.Open)
        {
            await SocketMessages.SendAsync(socket, new("error", Text: ex.Message), CancellationToken.None);
            // Close properly so the client reads why it was turned away; the connection reset that follows an unclosed
            // socket can discard the unread error on the client's side.
            try { using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, null, closing.Token); }
            catch (Exception) { }
        }
    }
    catch (OperationCanceledException ex) { ending = Cancelled(ex, waiting && handshake.IsCancellationRequested, limit, connection, socket); }
    catch (Exception ex) when (ex is WebSocketException or IOException) { ending = MissedKeepAlive(ex, socket) ? Ending.MissedKeepAlive : Ending.Lost(ex); }
    finally
    {
        // The service's own reason, such as a replacement or a removal, explains what the socket did next.
        var end = connection?.Ended ?? ending ?? new("an unexpected error, logged with the request", LogLevel.Warning);
        if (leave is not null) leave(end);
        else ServerLog.EndedBeforeEntering(log, end.Level, id, context.Request.Path.Value ?? "", ServerLog.Seconds(opened), end.Reason);
        lifetime.Cancel(); socket.Abort();
    }
}
app.Map("/room", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    Interlocked.Increment(ref roomConnections);
    try { await ServeSocket(context, roomLog, ServeRoom); }
    finally { Interlocked.Decrement(ref roomConnections); }
});
// Phones running BlackPearl Mobile and the PCs they control find each other here (RemoteLinks).
app.Map("/remote", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    await ServeSocket(context, remoteLog, async (_, socket, lifetime, first, id, entered) =>
    {
        if (first?.Type is not ("remote-pc" or "remote-phone")) throw new InvalidDataException("Expected remote-pc or remote-phone");
        await remoteLinks.ServeAsync(socket, lifetime, first, id, entered);
    });
});
await app.RunAsync();

// Cloudflare issues shared short-lived TURN credentials through an authenticated API. A background loop keeps them
// fresh even while no rooms are open, so startup and entry never wait on Cloudflare and every credential handed out
// stays valid for at least half its lifetime (12 hours by default).
sealed class CloudflareTurn(string keyId, string token, string api, TimeSpan lifetime, TimeSpan retryInterval, ILogger logger)
{
    static readonly string[] StunOnly = ["stun:stun.cloudflare.com:3478"];
    sealed record Credentials(string[] Urls, DateTimeOffset Expires);
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
    Credentials? credentials;
    Credentials? Valid(DateTimeOffset now) => Volatile.Read(ref credentials) is { } current && now < current.Expires ? current : null;
    public bool HasRelay => Valid(DateTimeOffset.UtcNow)?.Urls.Any(x => x.StartsWith("turn", StringComparison.Ordinal)) == true;
    public static CloudflareTurn? Create(string? keyId, string? token, string api, TimeSpan lifetime, TimeSpan retryInterval, ILogger logger) =>
        string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(token) ? null : new(keyId.Trim(), token.Trim(), api.TrimEnd('/'), lifetime, retryInterval, logger);
    // Without valid relay credentials, allocations would only fail, so participants get STUN alone.
    public string[] Current() => Valid(DateTimeOffset.UtcNow)?.Urls ?? StunOnly;
    public async Task RunAsync(CancellationToken stopping)
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                var refreshed = await RefreshAsync(stopping);
                var wait = refreshed ? Volatile.Read(ref credentials)!.Expires - lifetime / 2 - DateTimeOffset.UtcNow : retryInterval;
                await Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, stopping);
            }
        }
        catch (Exception) when (stopping.IsCancellationRequested) { }
    }
    async Task<bool> RefreshAsync(CancellationToken stopping)
    {
        try
        {
            // Expiry counts from the request, so a slow reply cannot make handed-out credentials outlive Cloudflare's.
            var requested = DateTimeOffset.UtcNow;
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{api}/turn/keys/{Uri.EscapeDataString(keyId)}/credentials/generate-ice-servers")
            { Content = JsonContent.Create(new { ttl = (int)lifetime.TotalSeconds }) };
            request.Headers.Authorization = new("Bearer", token);
            using var response = await http.SendAsync(request, stopping);
            response.EnsureSuccessStatusCode();
            using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(stopping));
            var parsed = Parse(Items(json.RootElement.GetProperty("iceServers")));
            if (parsed.Length == 0) throw new InvalidDataException("Cloudflare returned no ICE servers");
            Volatile.Write(ref credentials, new Credentials(parsed, requested + lifetime));
            logger.LogInformation("Cloudflare TURN credentials refreshed ({Count} servers)", parsed.Length);
            return true;
        }
        catch (Exception ex) when (!stopping.IsCancellationRequested)
        {
            logger.LogWarning("Cloudflare TURN credentials unavailable: {Error}", ex.Message);
            return false;
        }
    }
    static IEnumerable<System.Text.Json.JsonElement> Items(System.Text.Json.JsonElement value) =>
        value.ValueKind == System.Text.Json.JsonValueKind.Array ? value.EnumerateArray() : [value];
    static string[] Parse(IEnumerable<System.Text.Json.JsonElement> servers)
    {
        var result = new List<string>();
        foreach (var server in servers)
        {
            var username = server.TryGetProperty("username", out var u) ? u.GetString() : null;
            var credential = server.TryGetProperty("credential", out var c) ? c.GetString() : null;
            if (!server.TryGetProperty("urls", out var list)) continue;
            foreach (var entry in Items(list))
            {
                // Port 53 duplicates the standard ports and is commonly intercepted by local DNS resolvers.
                if (entry.GetString() is not { Length: > 0 } url || System.Text.RegularExpressions.Regex.IsMatch(url, @":53(\?|$)")) continue;
                if (url.StartsWith("stun:", StringComparison.Ordinal)) result.Add(url);
                else if (url.StartsWith("turn", StringComparison.Ordinal) && username is not null && credential is not null) result.Add(WithCredentials(url, username, credential));
            }
        }
        return result.ToArray();
    }
    // libdatachannel uses TURN URLs with escaped userinfo.
    public static string WithCredentials(string address, string username, string credential)
    {
        var separator = address.IndexOf(':');
        return address[..(separator + 1)] + "//" + Uri.EscapeDataString(username) + ":" + Uri.EscapeDataString(credential) + "@" + address[(separator + 1)..].TrimStart('/');
    }
}

// One socket that has entered a room, with its outgoing queue.
class Connection(WebSocket socket, CancellationTokenSource lifetime)
{
    // The socket's connection id, which every log line about it carries.
    public required string LogId { get; init; }
    public long Opened { get; } = System.Diagnostics.Stopwatch.GetTimestamp();
    // Counted by the connection's own read loop, for the line logged when it ends.
    public int Messages, Relayed, Dropped;
    // Set when the service itself ends the connection; the first reason given is the one logged.
    private Ending? ended;
    public Ending? Ended => Volatile.Read(ref ended);
    public void Mark(Ending why) => Interlocked.CompareExchange(ref ended, why, null);
    private Ending? sendFailed;
    public Ending? SendFailed => Volatile.Read(ref sendFailed);
    public void End(Ending why) { Mark(why); End(); }
    // Ends the connection. Its handler disposes the token once it returns, and tasks such as the send loop and other
    // members' messages can still reach this connection after that, when there is nothing left to end.
    public void End() { try { lifetime.Cancel(); } catch (ObjectDisposedException) { } }
    private double broadcastTokens = 20; private long broadcastRefilled = Wire.Now;
    // Called under the room lock for member messages that reach other members.
    public bool AllowBroadcast()
    {
        var now = Wire.Now; broadcastTokens = Math.Min(20, broadcastTokens + (now - broadcastRefilled) * 4 / 1000.0); broadcastRefilled = now;
        if (broadcastTokens < 1) return false;
        broadcastTokens--; return true;
    }
    private readonly Channel<(WireMessage Message, TaskCompletionSource? Sent, bool Last)> outgoing = Channel.CreateBounded<(WireMessage, TaskCompletionSource?, bool)>(128);
    private static readonly Ending Backlog = new("the app stopped reading: 128 messages waited to be sent", LogLevel.Warning);
    public void Send(WireMessage message) { if (!outgoing.Writer.TryWrite((message, null, false))) End(Backlog); }
    // Answers a message the app sent, such as a ping, from the connection's own read loop. A full queue here does not mean
    // the app stopped reading: the read loop handles messages that already arrived without waiting, and on a busy server
    // it answered 128 pings in a burst before the send loop got to run and send any. So the answer waits for room, and
    // the loop reads nothing more meanwhile; only a queue that stays full for ReplyWait means the app stopped reading.
    // Ten seconds is far beyond any wait for a thread, and well within the keep-alive's 50 s, which would end a connection
    // that reads nothing anyway, with a reason that says less.
    public async ValueTask ReplyAsync(WireMessage message)
    {
        if (outgoing.Writer.TryWrite((message, null, false))) return;
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); waiting.CancelAfter(ReplyWait);
        try { await outgoing.Writer.WriteAsync((message, null, false), waiting.Token); }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { End(Backlog); }
    }
    private static readonly TimeSpan ReplyWait = TimeSpan.FromSeconds(10);
    // Turns the app away with the reason, for its handler once nothing else reads the socket. As in Close, the reason is
    // the last thing sent and a proper close follows; this waits up to two seconds for the app to close its side, since
    // the connection reset that follows cutting it can discard the reason before the app has read it. A refused member
    // who had entered leaves the room only after this, when the handler ends: one round trip later with BlackPearl,
    // which closes as soon as it is told.
    public async Task RefuseAsync(string reason)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!outgoing.Writer.TryWrite((new("error", Text: reason), closed, true))) return;
        try
        {
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); waiting.CancelAfter(TimeSpan.FromSeconds(2));
            await closed.Task.WaitAsync(waiting.Token);
            while (await SocketMessages.ReadAsync(socket, waiting.Token) is not null) { }
        }
        catch (Exception) { }
    }
    // Tells the app why its connection ends, then ends it. The app does not come back on its own after this.
    // The reason is the last thing sent, followed by a proper close: cutting the connection straight after sending can
    // discard what the app has not read yet. The connection is cut only if the app does not close its side in time.
    public void Close(string reason, Ending why)
    {
        Mark(why);
        if (!outgoing.Writer.TryWrite((new("closed", Text: reason), null, true))) { End(); return; }
        _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => End(), TaskScheduler.Default);
    }
    public async Task SendLoop()
    {
        try
        {
            await foreach (var msg in outgoing.Reader.ReadAllAsync(lifetime.Token))
            {
                await SocketMessages.SendAsync(socket, msg.Message, lifetime.Token);
                if (!msg.Last) { msg.Sent?.TrySetResult(); continue; }
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, lifetime.Token);
                msg.Sent?.TrySetResult();
                return;
            }
        }
        catch (Exception ex)
        {
            // Once the connection is ending for another reason, its sends fail too; only a send that failed first can say
            // why. Even then a socket that died under both reading and sending is better explained by the read, so this
            // is only the reason when the read was merely cancelled by it (Cancelled).
            if (!lifetime.IsCancellationRequested) Volatile.Write(ref sendFailed, new($"sending failed ({ex.GetType().Name})", LogLevel.Warning));
            End();
        }
    }
}
// Someone in a room without a host. Hash identifies the person across connections without storing their key.
sealed class Member(string id, string name, WebSocket socket, CancellationTokenSource lifetime, string key) : Connection(socket, lifetime)
{
    public string Hash { get; } = PersistentRooms.Hash(key);
    // Who this is in the log: the same person on every connection, never their member id.
    public string Who => ServerLog.Tag(Hash);
    public string Id => id; public string Name => name;
    public bool Owner;
    // Order of arrival in the room.
    public long Arrived;
}
// Someone in a room as apps on protocol 1 know it: its host, or a guest the host admits.
sealed class HostedMember(string id, string name, bool host, WebSocket socket, CancellationTokenSource lifetime, string? guestKey) : Connection(socket, lifetime)
{
    public string? GuestKey => guestKey;
    public string? GuestHash { get; } = guestKey is null ? null : PersistentRooms.Hash(guestKey);
    public string Who => GuestHash is null ? "the host" : ServerLog.Tag(GuestHash);
    public string Id => id; public string Name => name; public bool Host { get; } = host;
    public bool Approved = host;
    public bool Ready;
}
// What a room holds while people on protocol 1 apps are in it: the playback state those apps keep on the service.
sealed class HostedSession
{
    public Dictionary<string, HostedMember> People { get; } = new();
    public SharedMedia? Media; public PlaybackState? Playback; public bool ResumeWhenReady; public string[] Queue = []; public long Revision;
    public void TryResume()
    {
        if (!ResumeWhenReady || Playback is null || People.Values.Any(x => x.Approved && !x.Ready)) return;
        ResumeWhenReady = false;
        Playback = Playback with { Revision = ++Revision, Playing = true, AtUnixMs = Wire.Now + 200 };
        Broadcast(new("playback", Data: Wire.Serialize(Playback)));
    }
    public void Broadcast(WireMessage message) { foreach (var p in People.Values.Where(x => x.Approved)) p.Send(message); }
}
sealed class Room(string code, bool persistent = false)
{
    public object Gate { get; } = new(); public string Code => code;
    // The room in the log, which never holds its invitation code.
    public string Tag { get; } = ServerLog.Tag(code);
    public bool Persistent { get; } = persistent;
    public string Kind => Persistent ? "saved" : "temporary";
    public string? Name;
    // The creator, who can rename the room and remove people.
    public string? OwnerHash;
    // The member holding the streaming role, and how many times it has changed hands.
    public string? HostId; public long Epoch;
    public long Expires { get; } = persistent ? long.MaxValue : Wire.Now + 6 * 3600000;
    public long EmptySince = Wire.Now, Arrivals;
    // A saved room loaded at startup that nobody has entered since: EmptySince is when the service started, not when the
    // room was last left.
    public bool Unopened;
    public Dictionary<string, Member> People { get; } = new();
    // Set while people on protocol 1 apps are in the room; People is empty then.
    public HostedSession? Hosted;
    // Whether guests may control playback, for those apps. Newer ones leave that to whoever is playing the video.
    public bool SharedControls;
    public bool Occupied => People.Count > 0 || Hosted is { People.Count: > 0 };
    // Everyone who has entered or been admitted, by member id, with the name they last used.
    public Dictionary<string, string> Members { get; } = [];
    public Dictionary<string, string> Banned { get; } = [];
    public AdmittedGuest[] MemberList() => Members.Select(pair => new AdmittedGuest(pair.Key, pair.Value)).ToArray();
    public AdmittedGuest[] BannedList() => Banned.Select(pair => new AdmittedGuest(pair.Key, pair.Value)).ToArray();
    public void Remember(Member member)
    {
        // A full list forgets someone who is not here; they are remembered again when they next enter.
        if (!Members.ContainsKey(member.Hash) && Members.Count >= RoomProtocol.MaxMembers &&
            Members.Keys.FirstOrDefault(id => id != OwnerHash && People.Values.All(p => p.Hash != id)) is { } forgotten) Members.Remove(forgotten);
        Members[member.Hash] = member.Name;
    }
    // Whether the member was still here: one replaced or removed has left already.
    public bool Leave(Member member, bool snapshot = true)
    {
        if (!People.Remove(member.Id)) return false;
        Broadcast(new("peer-left", Sender: member.Id));
        if (HostId == member.Id)
        {
            // Their video ends with them. The role passes to whoever has been here longest; in an empty room, the next
            // person to enter takes it.
            HostId = People.Values.OrderBy(x => x.Arrived).FirstOrDefault()?.Id; Epoch++;
        }
        if (snapshot) Snapshot();
        if (!Occupied) EmptySince = Wire.Now;
        return true;
    }
    public void Broadcast(WireMessage message) { foreach (var p in People.Values) p.Send(message); }
    public void Snapshot()
    {
        var snapshot = new RoomSnapshot(People.Values.OrderBy(x => x.Arrived).Select(x => new Participant(x.Id, x.Name, x.Id == HostId, true, false, x.Hash, x.Owner)).ToArray(),
            null, null, false, [], MemberList(), Name, Epoch: Epoch);
        var message = new WireMessage("snapshot", Data: Wire.Serialize(snapshot));
        WireMessage? forOwner = null;
        foreach (var p in People.Values)
            p.Send(p.Owner ? forOwner ??= new("snapshot", Data: Wire.Serialize(snapshot with { Removed = BannedList() })) : message);
    }
    public bool LeaveHosted(HostedMember member)
    {
        if (Hosted is not { } session || !session.People.Remove(member.Id)) return false;
        // The host's video and queue end with its connection; guests wait in the room for it to return.
        if (member.Host) { session.Media = null; session.Playback = null; session.Queue = []; session.ResumeWhenReady = false; }
        session.Broadcast(new("peer-left", Sender: member.Id));
        if (!member.Host) session.TryResume();
        HostedSnapshot();
        if (session.People.Count == 0) { Hosted = null; EmptySince = Wire.Now; }
        return true;
    }
    // The guests those apps list as admitted: everyone who belongs to the room except its creator, who is their host.
    private AdmittedGuest[] HostedGuests() => Members.Where(pair => pair.Key != OwnerHash).Select(pair => new AdmittedGuest(pair.Key, pair.Value)).ToArray();
    public void HostedSnapshot()
    {
        if (Hosted is not { } session) return;
        foreach (var p in session.People.Values)
        {
            var visible = session.People.Values.Where(x => p.Host || x.Approved || x.Id == p.Id).Select(x => new Participant(x.Id, x.Name, x.Host, x.Approved, x.Ready, p.Host ? x.GuestHash : null)).ToArray();
            p.Send(new("snapshot", Data: Wire.Serialize(new RoomSnapshot(visible, p.Approved ? session.Media : null, p.Approved ? session.Playback : null, SharedControls, p.Approved ? session.Queue : [], p.Host ? HostedGuests() : null, Name))));
        }
    }
}
