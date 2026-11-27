using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using BlackPearl.Core;

// The room service's log (src/BlackPearl.Server/ServerLog.cs) on a real service: every connection's life is in it with
// why it ended, a room that is only watching writes nothing, and nothing that identifies people or lets anyone in ever
// appears. The service runs with its console log as JSON, one entry per line, so each check reads the event and its named
// values; separate runs check the format a journal and a plain console get.
static class ServerLogFixture
{
    sealed record Entry(int EventId, string Level, string Category, string Message, JsonElement State)
    {
        public string Value(string name) => State.TryGetProperty(name, out var value) ? value.ToString() : "";
    }

    // A room service whose console output is collected line by line while it runs.
    sealed class Service : IAsyncDisposable
    {
        readonly Process process;
        public readonly ConcurrentQueue<string> Lines = new();
        public string Url { get; }
        Service(Process process, string url) { this.process = process; Url = url; }
        public static async Task<Service> Start(string sdk, string serverDll, string data, Dictionary<string, string> environment)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var url = "http://localhost:" + ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var start = new ProcessStartInfo(sdk) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["BLACKPEARL_DATA"] = data;
            // Systemd sets this for a service whose output goes to the journal; only the run that stands in for one keeps it.
            start.Environment.Remove("JOURNAL_STREAM");
            foreach (var (name, value) in environment) start.Environment[name] = value;
            start.ArgumentList.Add(serverDll); start.ArgumentList.Add("--urls"); start.ArgumentList.Add(url);
            var service = new Service(Process.Start(start)!, url);
            service.process.OutputDataReceived += (_, e) => { if (e.Data is not null) service.Lines.Enqueue(e.Data); };
            service.process.ErrorDataReceived += (_, e) => { if (e.Data is not null) service.Lines.Enqueue(e.Data); };
            service.process.BeginOutputReadLine(); service.process.BeginErrorReadLine();
            using var http = new HttpClient();
            for (var i = 0; i < 100; i++) { try { if ((await http.GetAsync(url + "/health")).IsSuccessStatusCode) break; } catch (HttpRequestException) { } await Task.Delay(200); }
            return service;
        }
        public Entry[] Entries() => Lines.Select(line =>
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                return new Entry(root.GetProperty("EventId").GetInt32(), root.GetProperty("LogLevel").GetString()!, root.GetProperty("Category").GetString()!,
                    root.GetProperty("Message").GetString()!, root.TryGetProperty("State", out var state) ? state.Clone() : default);
            }
            catch (JsonException) { return new Entry(-1, "", "", line, default); }
        }).ToArray();
        public async Task<Entry?> Next(Func<Entry, bool> match, int milliseconds = 10000)
        {
            for (var waited = 0; waited <= milliseconds; waited += 50)
            {
                if (Entries().FirstOrDefault(match) is { } found) return found;
                await Task.Delay(50);
            }
            return null;
        }
        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync();
            process.Dispose();
        }
    }

    sealed class MemoryStore : IPairedPhoneStore
    {
        public List<PairedPhone> Saved = [];
        public IReadOnlyList<PairedPhone> Load() => Saved.ToArray();
        public void Save(IReadOnlyList<PairedPhone> phones) => Saved = phones.ToList();
    }

    public static async Task Run(string sdk, string serverDll, string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        // Everything private the checks hand the service; none of it may reach the log.
        var secrets = new ConcurrentBag<string>();
        await using (var service = await Service.Start(sdk, serverDll, Path.Combine(directory, "events"), new()
        {
            ["Logging__Console__FormatterName"] = "json",
            // Debug too, so the per-message lines are checked for private values as well.
            ["Logging__LogLevel__BlackPearl"] = "Debug",
            ["BLACKPEARL_EMPTY_ROOM_SECONDS"] = "1", ["BLACKPEARL_SWEEP_SECONDS"] = "1",
        }))
        {
            try
            {
                // The service closes a socket whose app stops answering its pings within 50 seconds; that runs beside the rest.
                var silent = Silent(service, secrets, check);
                await Rooms(service, secrets, check);
                await OlderApps(service, secrets, check);
                await Remote(service, secrets, check);
                await RateLimit(service, secrets, check);
                await silent;
                var entries = service.Entries();
                check(entries.All(e => e.EventId >= 0), "every log entry is one line");
                check(!entries.Any(e => e.Category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) && e.Level is "Information" or "Debug" or "Trace"),
                    "ASP.NET's per-request lines stay out of the log");
                var text = string.Join('\n', service.Lines);
                var leaked = secrets.Where(secret => secret.Length > 0 && text.Contains(secret, StringComparison.OrdinalIgnoreCase)).ToArray();
                check(secrets.Count > 20 && leaked.Length == 0, $"no display or room name, invitation, key, member, phone or PC id, signal or client address reaches the log ({secrets.Count} values checked" +
                    (leaked.Length == 0 ? ")" : $"; found {string.Join(", ", leaked.Take(3))})"));
            }
            finally { await File.WriteAllLinesAsync(Path.Combine(directory, "events.log"), service.Lines); }
        }
        await Restarted(sdk, serverDll, directory, check);
        await Journal(sdk, serverDll, directory, check);
        await Session(sdk, serverDll, directory, check);
    }

    // A saved room after the service restarts: its first opening counts from the start, not from when it was last left.
    // The restart also brings back ASP.NET's request lines the way deploy/README.md says, with a setting systemd accepts.
    static async Task Restarted(string sdk, string serverDll, string directory, Action<bool, string> check)
    {
        var data = Path.Combine(directory, "restarted");
        var json = new Dictionary<string, string> { ["Logging__Console__FormatterName"] = "json" };
        string code, key;
        await using (var before = await Service.Start(sdk, serverDll, data, json))
        {
            var (socket, reply) = await Open(before.Url, "/room", new("enter", Text: "Ann Logcheck", Data: Wire.Serialize(new EnterRequest(RoomProtocol.Version, null, null, null, Temporary: false))));
            var welcome = Wire.Read<Welcome>(reply.Data!); code = welcome.Room; key = welcome.GuestKey!;
            await Close(socket);
            check(await before.Next(e => e.EventId == 10 && e.Value("Kind") == "saved") is not null, "creating a saved room is logged as saved");
        }
        await using var after = await Service.Start(sdk, serverDll, data, new(json) { ["Logging__LogLevel__Microsoft"] = "Information" });
        try
        {
            check(await after.Next(e => e.EventId == 1) is { } starting && starting.Value("Saved") == "1", "the service says how many saved rooms it loaded");
            await Task.Delay(1200);
            var (socket, _) = await Open(after.Url, "/room", new("enter", Text: "Ann Logcheck", Data: Wire.Serialize(new EnterRequest(RoomProtocol.Version, code, key, null, Temporary: false))));
            var first = await after.Next(e => e.EventId is 11 or 23 && e.Value("Room") == ServerLog(code));
            check(first is { EventId: 23, Level: "Information" } && long.Parse(first.Value("StartedSeconds")) >= 1,
                $"a saved room's first opening after a restart says it counts from the start ({first?.Message})");
            await Close(socket);
            await after.Next(e => e.EventId == 12 && e.Value("Room") == ServerLog(code));
            var (again, _) = await Open(after.Url, "/room", new("enter", Text: "Ann Logcheck", Data: Wire.Serialize(new EnterRequest(RoomProtocol.Version, code, key, null, Temporary: false))));
            check(await after.Next(e => e.EventId == 11 && e.Value("Room") == ServerLog(code)) is not null, "after that it is logged opened after how long it stood empty");
            await Close(again);
            check(await after.Next(e => e.Category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) && e.Level == "Information") is not null,
                "Logging__LogLevel__Microsoft=Information brings back ASP.NET's request lines");
            var text = string.Join('\n', after.Lines);
            check(!text.Contains(code, StringComparison.OrdinalIgnoreCase) && !text.Contains(key, StringComparison.OrdinalIgnoreCase), "the restarted service's log holds neither the invitation nor the key");
        }
        finally { await File.WriteAllLinesAsync(Path.Combine(directory, "restarted.log"), after.Lines); }
    }

    static ClientWebSocket Socket(string? address = null)
    {
        var socket = new ClientWebSocket();
        // The app never misses the service's pings in these checks, except where one is made to.
        if (address is not null) socket.Options.SetRequestHeader("X-Forwarded-For", address);
        return socket;
    }
    static async Task<(ClientWebSocket Socket, WireMessage Reply)> Open(string url, string path, WireMessage first, string? address = null)
    {
        var socket = Socket(address);
        await socket.ConnectAsync(new Uri(url.Replace("http", "ws") + path), default);
        await SocketMessages.SendAsync(socket, first, default);
        return (socket, await Read(socket));
    }
    static async Task<WireMessage> Read(ClientWebSocket socket, string? type = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (true)
            {
                var message = await SocketMessages.ReadAsync(socket, timeout.Token) ?? new("closed");
                if (type is null || message.Type == type || message.Type is "closed" or "error") return message;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { return new("closed"); }
    }
    static async Task Close(ClientWebSocket socket)
    {
        try { using var closing = new CancellationTokenSource(2000); await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, closing.Token); } catch (Exception) { socket.Abort(); }
        socket.Dispose();
    }
    static WireMessage Enter(string name, string? room = null, string? key = null, string? roomName = null) =>
        new("enter", Text: name, Data: Wire.Serialize(new EnterRequest(RoomProtocol.Version, room, key, roomName, Temporary: true)));

    // Rooms without a host: entering, signals, removal and letting back in, a second window, refusals and lost sockets.
    static async Task Rooms(Service service, ConcurrentBag<string> secrets, Action<bool, string> check)
    {
        var url = service.Url;
        var annKey = PersistentRooms.NewKey(); var benKey = PersistentRooms.NewKey();
        foreach (var value in new[] { "Ann Logcheck", "Ben Logcheck", "Evening Logcheck Room", annKey, benKey, PersistentRooms.Hash(annKey), PersistentRooms.Hash(benKey), "198.51.100.77" }) secrets.Add(value);
        var (ann, annWelcome) = await Open(url, "/room", Enter("Ann Logcheck", key: annKey, roomName: "Evening Logcheck Room"), "198.51.100.77");
        var identity = Wire.Read<Welcome>(annWelcome.Data!);
        // A room's creator gets a key of the service's making.
        var creatorKey = identity.GuestKey!;
        secrets.Add(identity.Room); secrets.Add(identity.Peer); secrets.Add(creatorKey); secrets.Add(PersistentRooms.Hash(creatorKey));
        var tag = ServerLog(identity.Room);
        var created = await service.Next(e => e.EventId == 10 && e.Value("Room") == tag);
        var annEntered = await service.Next(e => e.EventId == 14 && e.Value("Room") == tag);
        var annId = annEntered?.Value("Connection") ?? "?";
        check(created is { Level: "Information" } && created.Value("Kind") == "temporary" && created.Value("Connection") == annId &&
            annEntered is { Level: "Information" } && annEntered.Value("Who") == ServerLog(PersistentRooms.Hash(creatorKey)) && annEntered.Value("Present") == "1" &&
            annEntered.Value("Role") == "its creator, holding the streaming role" && annId.Length == 8 && annEntered.Message.StartsWith($"[{annId}] ", StringComparison.Ordinal),
            $"creating a room logs the room by a short hash of its invitation, and its creator by connection id and a short hash of their key ({annEntered?.Message})");

        var (ben, benWelcome) = await Open(url, "/room", Enter("Ben Logcheck", identity.Room, benKey));
        var benPeer = Wire.Read<Welcome>(benWelcome.Data!).Peer; secrets.Add(benPeer);
        var benEntered = await service.Next(e => e.EventId == 14 && e.Value("Room") == tag && e.Value("Connection") != annId);
        var benId = benEntered?.Value("Connection") ?? "?";
        check(benEntered?.Value("Present") == "2" && benEntered.Value("Role") == "a member", "someone entering is logged with how many are present");

        const string Payload = "LOGCHECK-SIGNAL-PAYLOAD-7f3a";
        secrets.Add(Payload);
        await SocketMessages.SendAsync(ann, new("signal", Target: benPeer, Data: Payload), default);
        check((await Read(ben, "signal")).Data == Payload, "a signal still reaches the member it is for");
        await SocketMessages.SendAsync(ann, new("signal", Target: "0123456789abcdef0123456789abcdef", Data: Payload), default);
        check(await service.Next(e => e.EventId == 22 && e.Value("Connection") == annId) is { Level: "Debug" } dropped && dropped.Value("Reason") == "no one else in the room has that id",
            "a signal for no one is dropped with a reason, at Debug");

        // Pings and the like write nothing.
        var before = service.Lines.Count;
        for (var i = 0; i < 50; i++) await SocketMessages.SendAsync(ann, new("ping", Number: i), default);
        for (var i = 0; i < 50; i++) await Read(ann, "pong");
        await Task.Delay(300);
        check(service.Lines.Count == before, $"pings write nothing to the log ({service.Lines.Count - before} lines for 50)");

        // The creator removes Ben, who is turned away until let back in.
        await SocketMessages.SendAsync(ann, new("remove", Target: benPeer), default);
        var removed = await service.Next(e => e.EventId == 18 && e.Value("Room") == tag);
        var benLeft = await service.Next(e => e.EventId == 15 && e.Value("Connection") == benId);
        check(removed?.Value("Connection") == annId && removed.Value("Who") == ServerLog(PersistentRooms.Hash(benKey)) && removed.Value("Presence") == "was present" &&
            benLeft is { Level: "Information" } && benLeft.Value("Reason") == $"removed by the room's creator (connection {annId})" && benLeft.Value("Present") == "1",
            $"a removal names who removed whom, and the removed member's line says why they left ({benLeft?.Message})");
        await Close(ben);
        var (barred, refusal) = await Open(url, "/room", Enter("Ben Logcheck", identity.Room, benKey));
        var turnedAway = await service.Next(e => e.EventId == 16 && e.Value("Room") == tag);
        check(refusal.Type == "error" && turnedAway is { Level: "Warning" } && turnedAway.Value("Reason") == "refused: " + refusal.Text,
            $"someone turned away at a room's door is a warning with the reason the app was given ({turnedAway?.Message})");
        await Close(barred);
        await SocketMessages.SendAsync(ann, new("restore", Target: PersistentRooms.Hash(benKey)), default);
        check(await service.Next(e => e.EventId == 19 && e.Value("Room") == tag) is { } restored && restored.Value("Who") == ServerLog(PersistentRooms.Hash(benKey)),
            "letting someone back in is logged");

        // Ben enters from two windows; the second replaces the first.
        var (first, _) = await Open(url, "/room", Enter("Ben Logcheck", identity.Room, benKey));
        var firstEntered = await service.Next(e => e.EventId == 14 && e.Value("Room") == tag && e.Value("Connection") is var c && c != annId && c != benId);
        var firstId = firstEntered?.Value("Connection") ?? "?";
        var (second, _) = await Open(url, "/room", Enter("Ben Logcheck", identity.Room, benKey));
        var replaced = await service.Next(e => e.EventId == 17 && e.Value("Earlier") == firstId);
        var firstLeft = await service.Next(e => e.EventId == 15 && e.Value("Connection") == firstId);
        check(replaced is { Level: "Information" } && firstLeft?.Value("Reason") == $"replaced by connection {replaced.Value("Connection")}, which entered as the same person",
            $"a second window is logged as replacing the first, and the first says so when it ends ({firstLeft?.Message})");
        await Close(first);
        var secondId = replaced?.Value("Connection") ?? "?";
        // The second window cuts its connection, as BlackPearl does when it leaves.
        second.Abort(); second.Dispose();
        var lost = await service.Next(e => e.EventId == 15 && e.Value("Connection") == secondId);
        // BlackPearl itself leaves this way, so it is no warning.
        check(lost is { Level: "Information" } && lost.Value("Reason") == "the app cut the connection without a WebSocket close" && double.Parse(lost.Value("Seconds"), System.Globalization.CultureInfo.InvariantCulture) >= 0,
            $"a socket cut without a WebSocket close says so ({lost?.Message})");

        // Text that isn't a message, and an invitation that doesn't exist.
        var junk = Socket(); await junk.ConnectAsync(new Uri(url.Replace("http", "ws") + "/room"), default);
        await junk.SendAsync("{\"type\": LOGCHECK-NOT-JSON"u8.ToArray(), WebSocketMessageType.Text, true, default);
        secrets.Add("LOGCHECK-NOT-JSON");
        var junkLine = await service.Next(e => e.EventId == 30 && e.Value("Reason").Contains("JsonException", StringComparison.Ordinal));
        check(junkLine is { Level: "Warning" } && junkLine.Value("Reason") == "refused: malformed message (JsonException)" && junkLine.Value("Path") == "/room",
            $"a message that can't be read is a warning that names the kind of error, not what was sent ({junkLine?.Message})");
        await Close(junk);
        var wrongCode = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)); secrets.Add(wrongCode);
        var (wrong, _) = await Open(url, "/room", Enter("Ben Logcheck", wrongCode, benKey));
        check(await service.Next(e => e.EventId == 30 && e.Value("Reason") == "refused: Invitation is invalid or expired") is { Level: "Warning" }, "a wrong invitation is a warning");
        await Close(wrong);

        // Ann floods the service and is cut off, then the room empties and closes.
        // Ann reads what the service sends meanwhile, so it is the flood that ends her connection and not a backlog of replies.
        var draining = Task.Run(async () => { try { while (await SocketMessages.ReadAsync(ann, default) is not null) { } } catch (Exception) { } });
        for (var i = 0; i < 1200; i++) { try { await SocketMessages.SendAsync(ann, new("ping", Number: i), default); } catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException) { break; } }
        await draining.WaitAsync(TimeSpan.FromSeconds(10));
        var flooded = await service.Next(e => e.EventId == 15 && e.Value("Connection") == annId);
        check(flooded is { Level: "Warning" } && flooded.Value("Reason") == "refused: Message rate limit exceeded" && int.Parse(flooded.Value("Messages")) > 400 && flooded.Value("Relayed") == "1" && flooded.Value("Dropped") == "1",
            $"a member cut off for flooding is a warning with their message and signal counts ({flooded?.Message})");
        ann.Abort(); ann.Dispose();
        var empty = await service.Next(e => e.EventId == 12 && e.Value("Room") == tag);
        var closed = await service.Next(e => e.EventId == 13 && e.Value("Room") == tag);
        check(empty is not null && closed?.Value("Reason").StartsWith("empty for", StringComparison.Ordinal) == true, $"a room that empties and is swept is logged closed ({closed?.Message})");

        // Someone who comes back to a room that emptied: the room opens again.
        var (owner, ownerWelcome) = await Open(url, "/room", Enter("Ann Logcheck", key: annKey));
        var again = Wire.Read<Welcome>(ownerWelcome.Data!); secrets.Add(again.Room); secrets.Add(again.Peer); secrets.Add(again.GuestKey!);
        await Close(owner);
        await service.Next(e => e.EventId == 12 && e.Value("Room") == ServerLog(again.Room));
        var (back, _) = await Open(url, "/room", Enter("Ann Logcheck", again.Room, again.GuestKey));
        check(await service.Next(e => e.EventId == 11 && e.Value("Room") == ServerLog(again.Room)) is { Level: "Information" }, "a room someone returns to is logged open again");
        await Close(back);
        var closedByApp = await service.Next(e => e.EventId == 15 && e.Value("Room") == ServerLog(again.Room) && e.Value("Reason") == "closed by the app");
        check(closedByApp is { Level: "Information" }, "leaving properly is logged as closed by the app, at Information");

        // Connecting and saying nothing.
        var mute = Socket(); await mute.ConnectAsync(new Uri(url.Replace("http", "ws") + "/room"), default);
        check(await service.Next(e => e.EventId == 30 && e.Value("Reason") == "sent nothing within 10 s of connecting", 15000) is { Level: "Warning" }, "a socket that never says what it wants is a warning after 10 seconds");
        mute.Abort(); mute.Dispose();
    }

    // An app that stops answering the service's pings: the service closes it after 50 seconds and says why.
    static async Task Silent(Service service, ConcurrentBag<string> secrets, Action<bool, string> check)
    {
        var key = PersistentRooms.NewKey(); secrets.Add(key); secrets.Add(PersistentRooms.Hash(key)); secrets.Add("Quiet Logcheck");
        var socket = new ClientWebSocket();
        // A client answers pings only while it reads; this one reads its welcome and then stops.
        socket.Options.KeepAliveInterval = TimeSpan.Zero;
        await socket.ConnectAsync(new Uri(service.Url.Replace("http", "ws") + "/room"), default);
        await SocketMessages.SendAsync(socket, Enter("Quiet Logcheck", key: key), default);
        var welcome = await Read(socket, "welcome");
        if (welcome.Data is not null) { var identity = Wire.Read<Welcome>(welcome.Data); secrets.Add(identity.Room); secrets.Add(identity.Peer); secrets.Add(identity.GuestKey!); secrets.Add(PersistentRooms.Hash(identity.GuestKey!)); }
        var tag = welcome.Data is null ? "?" : ServerLog(Wire.Read<Welcome>(welcome.Data).Room);
        var left = await service.Next(e => e.EventId == 15 && e.Value("Room") == tag, 75000);
        check(left is { Level: "Warning" } && left.Value("Reason") == "no answer to the service's keep-alive pings for 30 s", $"an app that stops answering pings is closed with that reason ({left?.Message})");
        socket.Abort(); socket.Dispose();
    }

    // Apps on protocol 1: a host, a guest waiting, admitted, and removed.
    static async Task OlderApps(Service service, ConcurrentBag<string> secrets, Action<bool, string> check)
    {
        var url = service.Url;
        var guestKey = PersistentRooms.NewKey();
        foreach (var value in new[] { "Old Host Logcheck", "Old Guest Logcheck", guestKey, PersistentRooms.Hash(guestKey) }) secrets.Add(value);
        var (host, hostWelcome) = await Open(url, "/room", new("create", Text: "Old Host Logcheck"));
        var room = Wire.Read<Welcome>(hostWelcome.Data!); secrets.Add(room.Room); secrets.Add(room.Peer);
        var tag = ServerLog(room.Room);
        var (guest, guestWelcome) = await Open(url, "/room", new("join", Text: "Old Guest Logcheck", Data: room.Room, Sender: guestKey));
        var guestPeer = Wire.Read<Welcome>(guestWelcome.Data!).Peer; secrets.Add(guestPeer);
        var waiting = await service.Next(e => e.EventId == 14 && e.Value("Room") == tag && e.Value("Role") == "a guest waiting for the host");
        check(await service.Next(e => e.EventId == 10 && e.Value("Room") == tag) is { } created && created.Value("Protocol") == "1" &&
            await service.Next(e => e.EventId == 14 && e.Value("Room") == tag && e.Value("Role") == "host") is not null && waiting?.Value("Who") == ServerLog(PersistentRooms.Hash(guestKey)),
            "rooms for apps on protocol 1 log their host and a waiting guest");
        await SocketMessages.SendAsync(host, new("admit", Target: guestPeer), default);
        check(await service.Next(e => e.EventId == 20 && e.Value("Room") == tag) is { } admitted && admitted.Value("Admitted") == "1", "a guest the host admits is logged");
        await SocketMessages.SendAsync(host, new("remove", Target: guestPeer), default);
        var guestLeft = await service.Next(e => e.EventId == 15 && e.Value("Connection") == waiting?.Value("Connection"));
        check(await service.Next(e => e.EventId == 18 && e.Value("Room") == tag) is not null && guestLeft?.Value("Reason").StartsWith("removed by the host", StringComparison.Ordinal) == true,
            $"a guest the host removes is logged, and their line says why they left ({guestLeft?.Message})");
        await Close(guest); await Close(host);
    }

    // Phone remotes: a phone waiting for its PC, the PC coming online, signals each way, the PC connecting again, and refusals.
    static async Task Remote(Service service, ConcurrentBag<string> secrets, Action<bool, string> check)
    {
        var url = service.Url;
        var pcKey = PersistentRooms.NewKey(); var pcId = PhoneRemoteProtocol.PcId(pcKey);
        secrets.Add(pcKey); secrets.Add(pcId);
        var tag = ServerLog(pcId);
        var (phone, phoneWelcome) = await Open(url, "/remote", new("remote-phone", Target: pcId, Number: PhoneRemoteProtocol.Version));
        var phoneId = phoneWelcome.Target ?? ""; secrets.Add(phoneId);
        var asked = await service.Next(e => e.EventId == 43 && e.Value("Pc") == tag);
        check(asked is { Level: "Information" } && asked.Value("State") == "offline" && asked.Value("Phones") == "1", $"a phone asking for an offline PC is logged ({asked?.Message})");
        const string PhoneSignal = "LOGCHECK-PHONE-SIGNAL-91c2", PcSignal = "LOGCHECK-PC-SIGNAL-44be";
        secrets.Add(PhoneSignal); secrets.Add(PcSignal);
        await SocketMessages.SendAsync(phone, new("signal", Data: PhoneSignal), default);
        check(await service.Next(e => e.EventId == 46 && e.Value("Pc") == tag) is { Level: "Debug" } offline && offline.Value("Reason") == "the PC is offline", "a phone's signal while its PC is offline is dropped with a reason, at Debug");

        var (pc, _) = await Open(url, "/remote", new("remote-pc", Data: pcKey, Number: PhoneRemoteProtocol.Version));
        var online = await service.Next(e => e.EventId == 40 && e.Value("Pc") == tag);
        check(online?.Value("Phones") == "1" && (await Read(phone, "remote-online")).Type == "remote-online", "a PC coming online is logged with how many phones wait for it");
        await SocketMessages.SendAsync(phone, new("signal", Data: PhoneSignal), default);
        check((await Read(pc, "signal")).Data == PhoneSignal, "the phone's signal reaches the PC");
        await SocketMessages.SendAsync(pc, new("signal", Target: phoneId, Data: PcSignal), default);
        check((await Read(phone, "signal")).Data == PcSignal, "the PC's signal reaches the phone");
        check(await service.Next(e => e.EventId == 45 && e.Value("Pc") == tag) is { Level: "Debug" }, "relayed signals are logged at Debug");

        var (again, _) = await Open(url, "/remote", new("remote-pc", Data: pcKey, Number: PhoneRemoteProtocol.Version));
        var replaced = await service.Next(e => e.EventId == 41 && e.Value("Pc") == tag);
        var firstLeft = await service.Next(e => e.EventId == 42 && e.Value("Connection") == online?.Value("Connection"));
        check(replaced?.Value("Earlier") == online?.Value("Connection") && firstLeft?.Value("Reason") == $"replaced by connection {replaced?.Value("Connection")} from the same PC" &&
            firstLeft.Value("Phones") == "0" && firstLeft.Value("Relayed") == "1", $"a PC connecting again replaces its earlier connection, which logs why it ended ({firstLeft?.Message})");
        await Close(pc);
        await Close(again);
        var pcLeft = await service.Next(e => e.EventId == 42 && e.Value("Connection") == replaced?.Value("Connection"));
        check(pcLeft is { Level: "Information" } && pcLeft.Value("Reason") == "closed by the app" && pcLeft.Value("Phones") == "1", $"a PC going offline is logged with the phones it told ({pcLeft?.Message})");
        await Close(phone);
        var phoneLeft = await service.Next(e => e.EventId == 44 && e.Value("Pc") == tag);
        check(phoneLeft is { Level: "Information" } && phoneLeft.Value("Relayed") == "1" && phoneLeft.Value("Dropped") == "1" && phoneLeft.Value("Reason") == "closed by the app",
            $"a phone leaving is logged with its signals relayed and dropped ({phoneLeft?.Message})");

        var (older, olderReply) = await Open(url, "/remote", new("remote-phone", Target: pcId, Number: 0));
        check(olderReply.Type == "error" && await service.Next(e => e.EventId == 30 && e.Value("Path") == "/remote" && e.Value("Reason") == "refused: " + olderReply.Text) is { Level: "Warning" },
            "a phone the service turns away is a warning with the error it was sent");
        await Close(older);
    }

    // Requests over the per-address limit are one warning a minute, whatever their number.
    static async Task RateLimit(Service service, ConcurrentBag<string> secrets, Action<bool, string> check)
    {
        secrets.Add("203.0.113.99");
        using var http = new HttpClient();
        for (var i = 0; i < 40; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, service.Url + "/health"); request.Headers.Add("X-Forwarded-For", "203.0.113.99");
            using var response = await http.SendAsync(request);
        }
        await Task.Delay(300);
        var limited = service.Entries().Where(e => e.EventId == 2).ToArray();
        check(limited is [{ Level: "Warning" } only] && only.Value("Count") == "1" && only.Value("Path") == "/health", $"requests over the limit log one warning, not one each ({limited.Length} lines)");
    }

    // Under systemd each line goes to the journal as one entry with its level, and the journal adds the time.
    static async Task Journal(string sdk, string serverDll, string directory, Action<bool, string> check)
    {
        await using (var journal = await Service.Start(sdk, serverDll, Path.Combine(directory, "journal"), new() { ["JOURNAL_STREAM"] = "8:12345" }))
        {
            var (socket, welcome) = await Open(journal.Url, "/room", Enter("Journal Logcheck"));
            var (wrong, _) = await Open(journal.Url, "/room", Enter("Journal Logcheck", "000000000000000000000000"));
            await Close(socket); await Close(wrong);
            await Task.Delay(1000);
            var lines = journal.Lines.ToArray();
            await File.WriteAllLinesAsync(Path.Combine(directory, "journal.log"), lines);
            check(lines.Length > 0 && lines.All(line => System.Text.RegularExpressions.Regex.IsMatch(line, @"^<\d>\S+\[\d+\] ")) &&
                lines.Any(line => line.StartsWith("<6>BlackPearl.Rooms[14] ", StringComparison.Ordinal)) && lines.Any(line => line.StartsWith("<4>BlackPearl.Rooms[30] ", StringComparison.Ordinal)) &&
                !lines.Any(line => line.Contains(Wire.Read<Welcome>(welcome.Data!).Room, StringComparison.OrdinalIgnoreCase)),
                $"under systemd every entry is one line that starts with its journal priority ({string.Join(" | ", lines.Take(2))})");
        }
        await using (var console = await Service.Start(sdk, serverDll, Path.Combine(directory, "console"), new()))
        {
            var started = await Task.Run(async () => { for (var i = 0; i < 50 && !console.Lines.Any(l => l.Contains("BlackPearl.Server[1]")); i++) await Task.Delay(100); return console.Lines.FirstOrDefault(l => l.Contains("BlackPearl.Server[1]")); });
            check(started is not null && System.Text.RegularExpressions.Regex.IsMatch(started, @"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} info: BlackPearl\.Server\[1\] BlackPearl room server "),
                $"elsewhere each entry is one line that starts with its UTC time and level ({started})");
        }
    }

    // A typical evening: two people enter a room, one streams a video the other watches, both leave, and a phone pairs
    // with a PC and controls it. Returns how many lines the service logged while the room only watched.
    static async Task<int> TypicalSession(Service service, string directory, int watchSeconds)
    {
        var video = Path.Combine(directory, "session-video.mkv");
        var bytes = new byte[400000]; new Random(5).NextBytes(bytes); await File.WriteAllBytesAsync(video, bytes);
        int watching;
        {
            await using var host = new RoomClient(); await using var guest = new RoomClient();
            await host.ConnectAsync(service.Url, "Host Logcheck");
            await guest.ConnectAsync(service.Url, "Guest Logcheck", host.Identity!.Room);
            await guest.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(25));
            await host.StartStreamAsync(RoomClient.PrepareHostedFile(video, "Logcheck film"), true);
            for (var i = 0; i < 200 && guest.Snapshot?.Media is null; i++) await Task.Delay(50);
            var source = await guest.GetSourceAsync(guest.Snapshot!.Media!, CancellationToken.None);
            await source.ReadAsync(0, 16384, CancellationToken.None);
            // Watching: the guest keeps reading the video while both apps ping the service as they do.
            await Task.Delay(1000);
            var before = service.Lines.Count;
            var until = Stopwatch.StartNew();
            long offset = 0;
            while (until.Elapsed.TotalSeconds < watchSeconds) { await source.ReadAsync(offset, 16384, CancellationToken.None); offset = (offset + 16384) % (bytes.Length - 16384); await Task.Delay(250); }
            watching = service.Lines.Count - before;
        }
        await Task.Delay(1500);
        var hostKey = PersistentRooms.NewKey(); var pcId = PhoneRemoteProtocol.PcId(hostKey);
        using (var pc = new PhoneRemoteHost(new PhoneRemoteOptions { Server = service.Url, HostKey = hostKey, PcName = "Logcheck PC", AppVersion = "0.0.0-test", Store = new MemoryStore() }))
        {
            pc.CurrentState = () => new RemoteState(true, "Logcheck film", Playing: true, PositionMs: 1000, DurationMs: 60000);
            pc.Start();
            for (var i = 0; i < 200 && !pc.ServerReady; i++) await Task.Delay(50);
            var code = System.Web.HttpUtility.ParseQueryString(new Uri(pc.BeginPairing()).Query)["code"]!;
            await using (var phone = await FakePhone.Connect(service.Url, pcId, "pair", code))
            {
                if (!await phone.Opened()) throw new TimeoutException("The phone did not pair through the service");
                phone.Send(new { type = "hello", protocol = 1, app = "test", device = "Logcheck phone" });
                await phone.Next("welcome");
                phone.Send(new { type = "command", action = "toggle" });
                await Task.Delay(2000);
            }
            await Task.Delay(1500);
        }
        await Task.Delay(1500);
        return watching;
    }

    static int WatchSeconds() => int.TryParse(Environment.GetEnvironmentVariable("BLACKPEARL_LOG_SESSION_SECONDS"), out var configured) ? configured : 25;

    // The typical session logs a few lines, all of them Information, and none while the room only watches.
    static async Task Session(string sdk, string serverDll, string directory, Action<bool, string> check)
    {
        var watchSeconds = WatchSeconds();
        await using var service = await Service.Start(sdk, serverDll, Path.Combine(directory, "session"), new() { ["Logging__Console__FormatterName"] = "json" });
        var clock = Stopwatch.StartNew();
        int watching;
        try { watching = await TypicalSession(service, directory, watchSeconds); }
        finally { await File.WriteAllLinesAsync(Path.Combine(directory, "session.log"), service.Lines); }
        check(watching == 0, $"a room that is only watching logs nothing ({watching} lines in {watchSeconds} s)");
        var logged = service.Entries().Where(e => e.Level is not ("Debug" or "Trace")).ToArray();
        var minutes = clock.Elapsed.TotalMinutes;
        var summary = string.Join(", ", logged.GroupBy(e => e.EventId).OrderBy(g => g.Key).Select(g => $"{g.Key}×{g.Count()}"));
        check(logged.Length is > 0 and <= 30 && logged.All(e => e.Level == "Information") && logged.Any(e => e.EventId == 15) && logged.Any(e => e.EventId == 44),
            $"a typical session logs a few lines, all Information: {logged.Length} lines in {minutes:0.0} min, {logged.Length / minutes:0.0} a minute (events {summary})");
    }

    // How many journal lines the typical session writes on any build of the service, this one or an older one: the service
    // runs as under systemd, and every line it writes counts, startup included. --server-log-volume [server dll].
    public static async Task Volume(string sdk, string serverDll, string directory)
    {
        Directory.CreateDirectory(directory);
        var watchSeconds = WatchSeconds();
        await using var service = await Service.Start(sdk, serverDll, Path.Combine(directory, "volume"), new() { ["JOURNAL_STREAM"] = "8:12345" });
        var startup = service.Lines.Count;
        var clock = Stopwatch.StartNew();
        int watching;
        try { watching = await TypicalSession(service, directory, watchSeconds); }
        finally { await File.WriteAllLinesAsync(Path.Combine(directory, "volume.log"), service.Lines); }
        var minutes = clock.Elapsed.TotalMinutes; var lines = service.Lines.Count;
        Console.WriteLine($"{serverDll}: {lines} journal lines ({startup} at startup, {lines - startup} in the session) over {minutes:0.0} min of session, " +
            $"{(lines - startup) / minutes:0.0} a minute; {watching} while watching for {watchSeconds} s. Log: {Path.Combine(directory, "volume.log")}");
    }
    static string ServerLog(string value) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..6];
}
