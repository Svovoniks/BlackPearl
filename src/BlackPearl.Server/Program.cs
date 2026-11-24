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
var builder = WebApplication.CreateBuilder(args);
// Errors go to Sentry when the server sets BLACKPEARL_SENTRY_DSN; without it nothing is sent. CrashReportPrivacy (Core)
// decides what reports leave out: addresses, request details beyond the path, breadcrumbs, and paths and ids in error text.
if (builder.Configuration[AppEnvironment.Prefix + "SENTRY_DSN"] is { Length: > 0 } sentryDsn)
    builder.WebHost.UseSentry(options =>
    {
        options.Dsn = sentryDsn;
        options.Release = "blackpearl-server@" + typeof(RoomProtocol).Assembly.GetName().Version?.ToString(3);
        options.Environment = builder.Configuration[AppEnvironment.Prefix + "SENTRY_ENVIRONMENT"] ?? "production";
        CrashReportPrivacy.Apply(options);
        options.MaxRequestBodySize = Sentry.Extensibility.RequestSize.None;
        options.TracesSampleRate = 0;
        options.SetBeforeSend(evt => CrashReportPrivacy.Scrub(evt));
    });
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
// A task that failed with nobody awaiting it is a bug; say so in the service's log as well as in any error report.
var unobservedLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("UnobservedTask");
TaskScheduler.UnobservedTaskException += (_, e) => unobservedLog.LogError(e.Exception, "Unobserved task exception");
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
                if (pair.Value.Expires < now || !pair.Value.Occupied && now - pair.Value.EmptySince >= emptyRoomGraceMs) rooms.TryRemove(pair);
    foreach (var pair in persistentCreations)
        lock (pair.Value) if (pair.Value.All(at => now - at > 3600000)) persistentCreations.TryRemove(pair);
}
_ = Task.Run(async () =>
{
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Setting(Config("SWEEP_SECONDS"), 60)));
    try { while (await timer.WaitForNextTickAsync(app.Lifetime.ApplicationStopping)) SweepRooms(Wire.Now); }
    catch (OperationCanceledException) { }
});
var persistentRooms = new PersistentRooms(Config("DATA") ?? Path.Combine(AppContext.BaseDirectory, "data"));
foreach (var saved in persistentRooms.All())
{
    var restored = new Room(saved.Code, true) { Name = saved.Name, OwnerHash = saved.HostKeyHash, SharedControls = saved.SharedControls };
    foreach (var guest in saved.Guests ?? []) restored.Members[guest.Id] = guest.Name;
    // Rooms saved before members had names list only their ids.
    foreach (var id in saved.ApprovedGuests ?? []) restored.Members.TryAdd(id, "Guest");
    foreach (var removed in saved.Banned ?? []) { restored.Banned[removed.Id] = removed.Name; restored.Members.Remove(removed.Id); }
    rooms[saved.Code] = restored;
}
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
var remoteLinks = new RemoteLinks(IceServers, forceRelay);
// protocol is the newest the service speaks, which newer apps require; protocols lists every one it still serves.
// remote is the phone remote protocol (PHONE-REMOTE.md) this service introduces phones and PCs with.
app.MapGet("/health", () => Results.Ok(new { status = "ok", protocol = RoomProtocol.Version, protocols = new[] { 1, RoomProtocol.Version }, persistentRooms = true, remote = PhoneRemoteProtocol.Version,
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
async Task ServeHosted(HttpContext context, WebSocket socket, CancellationTokenSource lifetime, WireMessage first, Action<Connection, Action> entered)
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
    }
    else if (!rooms.TryGetValue((first.Data ?? "").Trim().ToUpperInvariant(), out room) || room.Expires < Wire.Now) throw new InvalidDataException("Invitation is invalid or expired");
    if (first.Type == "resume")
    {
        if (!room.Persistent || room.OwnerHash is null || first.Target is not { Length: 64 } supplied ||
            !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(room.OwnerHash), SHA256.HashData(Encoding.UTF8.GetBytes(supplied)))) throw new InvalidDataException("Host key is invalid");
        hostKey = first.Target;
    }
    var guestKey = host ? null : first.Sender is { Length: 64 } key && key.All(Uri.IsHexDigit) ? key : PersistentRooms.NewKey();
    var member = new HostedMember(Guid.NewGuid().ToString("N"), name, host, socket, lifetime, guestKey);
    entered(member, () => { lock (room.Gate) room.LeaveHosted(member); });
    var sender = member.SendLoop();
    lock (room.Gate)
    {
        // The sweep removes rooms under this lock; never admit a member into a removed room.
        if (!rooms.TryGetValue(room.Code, out var live) || live != room) throw new InvalidDataException("Invitation is invalid or expired");
        if (room.People.Count > 0) throw new InvalidDataException("The people in this room use a newer BlackPearl. Update the app to join them.");
        var session = room.Hosted ??= new();
        if (session.People.Count >= 9) throw new InvalidDataException("Room is full");
        if (host && session.People.Values.Any(x => x.Host)) throw new InvalidDataException("The host is already connected to this room");
        // Someone who belongs to the room, admitted by its host or entered from a newer app, needs no fresh approval.
        if (!host && member.GuestHash != room.OwnerHash && room.Members.ContainsKey(member.GuestHash!) && session.People.Values.Count(x => x.Approved) < 5 &&
            !session.People.Values.Any(x => x.GuestHash == member.GuestHash)) member.Approved = true;
        session.People.Add(member.Id, member);
        member.Send(new(member.Approved && !host ? "admitted" : "welcome", Data: Wire.Serialize(new Welcome(room.Code, member.Id, host, member.Approved ? IceServers(member.Id) : [], forceRelay, hostKey, guestKey))));
        room.HostedSnapshot();
        if (!host && member.Approved) session.People.Values.FirstOrDefault(x => x.Host)?.Send(new("connect", Target: member.Id));
        if (host) foreach (var guest in session.People.Values.Where(x => !x.Host && x.Approved)) member.Send(new("connect", Target: guest.Id));
    }
    // Token bucket: the host's burst covers resume signaling for every guest; sustained floods still disconnect.
    var (burst, perSecond) = host ? (400.0, 160.0) : (120.0, 80.0);
    var tokens = burst; var refilled = Wire.Now;
    while (!lifetime.IsCancellationRequested)
    {
        var message = await SocketMessages.ReadAsync(socket, lifetime.Token); if (message is null) break;
        var now = Wire.Now; tokens = Math.Min(burst, tokens + (now - refilled) * perSecond / 1000); refilled = now;
        if (--tokens < 0) throw new InvalidDataException("Message rate limit exceeded");
        lock (room.Gate)
        {
            if (message.Type == "ping") { member.Send(new("pong", Number: message.Number, Data: Wire.Now.ToString())); continue; }
            if (!member.Approved || room.Hosted is not { } session || !session.People.ContainsKey(member.Id)) continue;
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
                    }
                    break;
                case "remove" when member.Host:
                    var removed = session.People.Values.FirstOrDefault(x => !x.Host && (x.Id == message.Target || x.GuestHash == message.Target));
                    if (removed is not null)
                    { room.Members.Remove(removed.GuestHash!); SaveRoom(room); removed.Approved = false; removed.End(); session.Broadcast(new("peer-left", Sender: removed.Id)); room.HostedSnapshot(); }
                    else if (message.Target != room.OwnerHash && room.Members.Remove(message.Target ?? ""))
                    { SaveRoom(room); room.HostedSnapshot(); }
                    break;
                case "signal":
                    if (session.People.TryGetValue(message.Target ?? "", out var target) && target.Approved && (target.Host || member.Host)) target.Send(message with { Sender = member.Id });
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
app.Map("/room", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    // Relay credentials are handed out when a member enters and last at least twice this long, so a connection ends
    // before they can; the app enters again by itself.
    lifetime.CancelAfter(TimeSpan.FromHours(6));
    // Whoever this connection is once it has entered a room, and how it leaves.
    Connection? connection = null; Action? leave = null;
    try
    {
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); handshake.CancelAfter(TimeSpan.FromSeconds(10));
        var first = await SocketMessages.ReadAsync(socket, handshake.Token);
        // Apps from before rooms lost their host say create, join or resume, and get the rooms they know.
        if (first?.Type is "create" or "join" or "resume")
        {
            await ServeHosted(context, socket, lifetime, first, (entered, leaving) => { connection = entered; leave = leaving; });
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
        Room? room;
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
            rooms[room.Code] = room;
        }
        else if (!rooms.TryGetValue(request.Room.Trim().ToUpperInvariant(), out room) || room.Expires < Wire.Now) throw new InvalidDataException("Invitation is invalid or expired");
        key ??= PersistentRooms.NewKey();
        var member = new Member(Guid.NewGuid().ToString("N"), name, socket, lifetime, key);
        connection = member; leave = () => { lock (room.Gate) room.Leave(member); };
        var sender = member.SendLoop();
        lock (room.Gate)
        {
            // The sweep removes rooms under this lock; never let a member into a removed room.
            if (!rooms.TryGetValue(room.Code, out var live) || live != room) throw new InvalidDataException("Invitation is invalid or expired");
            if (room.Hosted is { People.Count: > 0 }) throw new InvalidDataException("The people in this room use an older BlackPearl. Once they update, you can all join together.");
            if (room.Banned.ContainsKey(member.Hash)) throw new InvalidDataException("You were removed from this room. Its creator can let you back in.");
            member.Owner = member.Hash == room.OwnerHash;
            // Someone entering again replaces their earlier connection: usually one the network dropped before the
            // service noticed. Refusing instead would lock them out until that connection timed out.
            foreach (var earlier in room.People.Values.Where(x => x.Hash == member.Hash).ToArray()) { room.Leave(earlier); earlier.Close("You entered this room from another BlackPearl window."); }
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
        }
        // Token bucket: the burst covers signaling with every other member at once; sustained floods still disconnect.
        const double Burst = 400, PerSecond = 160;
        var tokens = Burst; var refilled = Wire.Now;
        while (!lifetime.IsCancellationRequested)
        {
            var message = await SocketMessages.ReadAsync(socket, lifetime.Token); if (message is null) break;
            var now = Wire.Now; tokens = Math.Min(Burst, tokens + (now - refilled) * PerSecond / 1000); refilled = now;
            if (--tokens < 0) throw new InvalidDataException("Message rate limit exceeded");
            lock (room.Gate)
            {
                if (message.Type == "ping") { member.Send(new("pong", Number: message.Number, Data: Wire.Now.ToString())); continue; }
                // A connection that was replaced or removed keeps its socket for a moment; it no longer acts in the room.
                if (!room.People.ContainsKey(member.Id)) continue;
                switch (message.Type)
                {
                    case "signal":
                        if (room.People.TryGetValue(message.Target ?? "", out var target) && target != member) target.Send(message with { Sender = member.Id });
                        break;
                    // Asks another member to start a new direct connection to this one.
                    case "reconnect" when member.AllowBroadcast():
                        if (room.People.TryGetValue(message.Target ?? "", out var peer) && peer != member) peer.Send(new("connect", Target: member.Id));
                        break;
                    // Starting a video takes the streaming role from whoever holds it.
                    case "claim" when room.HostId != member.Id && member.AllowBroadcast():
                        room.HostId = member.Id; room.Epoch++; room.Snapshot(); break;
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
                        if (present is not null) { room.Leave(present, snapshot: false); present.Close("You were removed from this room."); }
                        SaveRoom(room); room.Snapshot(); break;
                    case "restore" when member.Owner:
                        if (room.Banned.Remove(message.Target ?? "")) { SaveRoom(room); room.Snapshot(); }
                        break;
                }
            }
        }
    }
    catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or FormatException)
    {
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
    catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException) { }
    finally
    {
        leave?.Invoke();
        lifetime.Cancel(); socket.Abort();
    }
});
// Phones running BlackPearl Mobile and the PCs they control find each other here (RemoteLinks).
app.Map("/remote", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    // As with rooms, a connection ends before the relay credentials it was given can; the app connects again.
    lifetime.CancelAfter(TimeSpan.FromHours(6));
    Connection? connection = null; Action? leave = null;
    try
    {
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); handshake.CancelAfter(TimeSpan.FromSeconds(10));
        var first = await SocketMessages.ReadAsync(socket, handshake.Token);
        if (first?.Type is not ("remote-pc" or "remote-phone")) throw new InvalidDataException("Expected remote-pc or remote-phone");
        await remoteLinks.ServeAsync(socket, lifetime, first, (entered, leaving) => { connection = entered; leave = leaving; });
    }
    catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or FormatException)
    {
        if (connection is not null) await connection.RefuseAsync(ex.Message);
        else if (socket.State == WebSocketState.Open)
        {
            await SocketMessages.SendAsync(socket, new("error", Text: ex.Message), CancellationToken.None);
            try { using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, null, closing.Token); }
            catch (Exception) { }
        }
    }
    catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException) { }
    finally
    {
        leave?.Invoke();
        lifetime.Cancel(); socket.Abort();
    }
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
    public void Send(WireMessage message) { if (!outgoing.Writer.TryWrite((message, null, false))) End(); }
    // Turns the app away with the reason, for its handler once nothing else reads the socket. As in Close, the reason is
    // the last thing sent and a proper close follows; this waits up to two seconds for the app to close its side, since
    // the connection reset that follows cutting it can discard the reason before the app has read it.
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
    public void Close(string reason)
    {
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
        catch (Exception) { End(); }
    }
}
// Someone in a room without a host. Hash identifies the person across connections without storing their key.
sealed class Member(string id, string name, WebSocket socket, CancellationTokenSource lifetime, string key) : Connection(socket, lifetime)
{
    public string Hash { get; } = PersistentRooms.Hash(key);
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
    public bool Persistent { get; } = persistent;
    public string? Name;
    // The creator, who can rename the room and remove people.
    public string? OwnerHash;
    // The member holding the streaming role, and how many times it has changed hands.
    public string? HostId; public long Epoch;
    public long Expires { get; } = persistent ? long.MaxValue : Wire.Now + 6 * 3600000;
    public long EmptySince = Wire.Now, Arrivals;
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
    public void Leave(Member member, bool snapshot = true)
    {
        if (!People.Remove(member.Id)) return;
        Broadcast(new("peer-left", Sender: member.Id));
        if (HostId == member.Id)
        {
            // Their video ends with them. The role passes to whoever has been here longest; in an empty room, the next
            // person to enter takes it.
            HostId = People.Values.OrderBy(x => x.Arrived).FirstOrDefault()?.Id; Epoch++;
        }
        if (snapshot) Snapshot();
        if (!Occupied) EmptySince = Wire.Now;
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
    public void LeaveHosted(HostedMember member)
    {
        if (Hosted is not { } session || !session.People.Remove(member.Id)) return;
        // The host's video and queue end with its connection; guests wait in the room for it to return.
        if (member.Host) { session.Media = null; session.Playback = null; session.Queue = []; session.ResumeWhenReady = false; }
        session.Broadcast(new("peer-left", Sender: member.Id));
        if (!member.Host) session.TryResume();
        HostedSnapshot();
        if (session.People.Count == 0) { Hosted = null; EmptySince = Wire.Now; }
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
