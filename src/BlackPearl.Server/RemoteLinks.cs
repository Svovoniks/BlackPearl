using System.Net.WebSockets;
using BlackPearl.Core;

// Introduces phones running BlackPearl Mobile to the PC they were paired with (PHONE-REMOTE.md). A PC stays connected
// under the id its host key gives it; a phone asks for that id and the two exchange signed WebRTC signaling through
// here. Only the phone and the PC can sign, so this service can pass signals along but can't answer in the PC's place.
// Commands and playback state travel on the direct link they set up, never through here.
sealed class RemoteLinks(Func<string, string[]> iceServers, bool forceRelay, ILogger log)
{
    public const int MaxPhonesPerPc = 8, MaxPhones = 20000;
    readonly object gate = new();
    readonly Dictionary<string, RemotePc> pcs = new();
    // Every phone connection, online PC or not, by the PC it asked for.
    readonly Dictionary<string, Dictionary<string, RemotePhone>> phones = new();
    int phoneCount;

    // Tag is the PC in the log, the same for the PC and every phone asking for it; the PC id itself finds the PC.
    sealed class RemotePc(string id, WebSocket socket, CancellationTokenSource lifetime) : Connection(socket, lifetime) { public string Id => id; public string Tag { get; } = ServerLog.Tag(id); }
    sealed class RemotePhone(string id, string pc, WebSocket socket, CancellationTokenSource lifetime) : Connection(socket, lifetime)
    { public string Id => id; public string Pc => pc; public string Tag { get; } = ServerLog.Tag(pc); }

    static bool Hex(string? text, int length) => text is not null && text.Length == length && text.All(Uri.IsHexDigit);
    string Welcome(string target) => Wire.Serialize(new RemoteWelcome(iceServers(target), forceRelay));

    // Returns once the connection ends. Errors that should reach the app are thrown as InvalidDataException before the
    // connection is registered, or sent by the caller's error path after. connectionId names the socket in the log.
    public async Task ServeAsync(WebSocket socket, CancellationTokenSource lifetime, WireMessage first, string connectionId, Action<Connection, Action<Ending>> entered)
    {
        if (first.Number != PhoneRemoteProtocol.Version)
            throw new InvalidDataException(first.Number > PhoneRemoteProtocol.Version ? "This room server is older than your BlackPearl. Whoever runs it needs to update it."
                : "This phone app is older than the room server. Update BlackPearl Mobile.");
        if (first.Type == "remote-pc") await ServePc(socket, lifetime, first, connectionId, entered);
        else await ServePhone(socket, lifetime, first, connectionId, entered);
    }

    async Task ServePc(WebSocket socket, CancellationTokenSource lifetime, WireMessage first, string connectionId, Action<Connection, Action<Ending>> entered)
    {
        if (!Hex(first.Data, 64)) throw new InvalidDataException("Invalid PC key");
        var pc = new RemotePc(PhoneRemoteProtocol.PcId(first.Data!), socket, lifetime) { LogId = connectionId };
        entered(pc, ending => Leave(pc, ending));
        _ = pc.SendLoop();
        string? replaced = null; int waitingPhones;
        lock (gate)
        {
            // A PC connecting again replaces its earlier connection, usually one the network dropped unnoticed.
            if (pcs.Remove(pc.Id, out var earlier))
            {
                earlier.Close("BlackPearl connected again from this PC.", new($"replaced by connection {pc.LogId} from the same PC", LogLevel.Information));
                replaced = earlier.LogId;
            }
            pcs[pc.Id] = pc;
            pc.Send(new("remote-welcome", Target: pc.Id, Data: Welcome(pc.Id)));
            if (phones.TryGetValue(pc.Id, out var waiting)) foreach (var phone in waiting.Values) phone.Send(new("remote-online"));
            waitingPhones = waiting?.Count ?? 0;
        }
        if (replaced is not null) ServerLog.PcReplaced(log, pc.LogId, pc.Tag, replaced);
        ServerLog.PcOnline(log, pc.LogId, pc.Tag, waitingPhones);
        await Read(socket, lifetime, 400, 160, message =>
        {
            if (message.Type != "signal") return;
            string? dropped = null;
            lock (gate)
            {
                if (pcs.GetValueOrDefault(pc.Id) != pc) dropped = "this PC connection was replaced";
                else if (!phones.TryGetValue(pc.Id, out var list) || !list.TryGetValue(message.Target ?? "", out var phone)) dropped = "no phone with that id is connecting";
                else phone.Send(new("signal", Sender: pc.Id, Data: message.Data));
            }
            Count(pc, "to a phone", dropped);
        }, pc);
    }

    async Task ServePhone(WebSocket socket, CancellationTokenSource lifetime, WireMessage first, string connectionId, Action<Connection, Action<Ending>> entered)
    {
        var target = (first.Target ?? "").ToUpperInvariant();
        if (!Hex(target, 32)) throw new InvalidDataException("Invalid PC id. Pair the phone again.");
        var phone = new RemotePhone(Guid.NewGuid().ToString("N"), target, socket, lifetime) { LogId = connectionId };
        int connecting;
        lock (gate)
        {
            if (phoneCount >= MaxPhones) throw new InvalidDataException("The room server is busy. Try again later.");
            var list = phones.TryGetValue(target, out var existing) ? existing : phones[target] = new();
            if (list.Count >= MaxPhonesPerPc) throw new InvalidDataException("Too many phones are connecting to this PC right now.");
            list[phone.Id] = phone; phoneCount++; connecting = list.Count;
        }
        entered(phone, ending => Leave(phone, ending));
        _ = phone.SendLoop();
        bool online;
        lock (gate)
        {
            online = pcs.ContainsKey(target);
            phone.Send(new("remote-welcome", Target: phone.Id, Data: Welcome(phone.Id)));
            phone.Send(new(online ? "remote-online" : "remote-offline"));
        }
        ServerLog.PhoneAsked(log, phone.LogId, phone.Tag, online ? "online" : "offline", connecting);
        await Read(socket, lifetime, 120, 60, message =>
        {
            if (message.Type != "signal") return;
            string? dropped = null;
            if (message.Data is null) dropped = "it was empty";
            else lock (gate)
                {
                    if (pcs.TryGetValue(target, out var pc)) pc.Send(new("signal", Sender: phone.Id, Data: message.Data));
                    else dropped = "the PC is offline";
                }
            Count(phone, "to the PC", dropped);
        }, phone);
    }

    // Counts a signal for the line logged when the connection ends; each one is logged only at Debug.
    void Count(Connection from, string direction, string? dropped)
    {
        var pc = from is RemotePc p ? p.Tag : ((RemotePhone)from).Tag;
        if (dropped is null) { from.Relayed++; ServerLog.RemoteSignalRelayed(log, from.LogId, direction, pc); }
        else { from.Dropped++; ServerLog.RemoteSignalDropped(log, from.LogId, direction, pc, dropped); }
    }

    static async Task Read(WebSocket socket, CancellationTokenSource lifetime, double burst, double perSecond, Action<WireMessage> handle, Connection connection)
    {
        var tokens = burst; var refilled = Wire.Now;
        while (!lifetime.IsCancellationRequested)
        {
            var message = await SocketMessages.ReadAsync(socket, lifetime.Token); if (message is null) break;
            connection.Messages++;
            var now = Wire.Now; tokens = Math.Min(burst, tokens + (now - refilled) * perSecond / 1000); refilled = now;
            if (--tokens < 0) throw new InvalidDataException("Message rate limit exceeded");
            if (message.Type == "ping") { connection.Send(new("pong", Number: message.Number, Data: Wire.Now.ToString())); continue; }
            handle(message);
        }
    }

    void Leave(RemotePc pc, Ending ending)
    {
        var told = 0;
        lock (gate)
        {
            if (pcs.GetValueOrDefault(pc.Id) == pc)
            {
                pcs.Remove(pc.Id);
                if (phones.TryGetValue(pc.Id, out var waiting)) { foreach (var phone in waiting.Values) phone.Send(new("remote-offline")); told = waiting.Count; }
            }
        }
        ServerLog.PcLeft(log, ending.Level, pc.LogId, pc.Tag, ServerLog.Seconds(pc.Opened), ending.Reason, told, pc.Messages, pc.Relayed, pc.Dropped);
    }

    void Leave(RemotePhone phone, Ending ending)
    {
        lock (gate)
        {
            if (phones.TryGetValue(phone.Pc, out var list) && list.Remove(phone.Id))
            {
                phoneCount--;
                if (list.Count == 0) phones.Remove(phone.Pc);
                if (pcs.TryGetValue(phone.Pc, out var pc)) pc.Send(new("peer-left", Sender: phone.Id));
            }
        }
        ServerLog.PhoneLeft(log, ending.Level, phone.LogId, phone.Tag, ServerLog.Seconds(phone.Opened), ending.Reason, phone.Messages, phone.Relayed, phone.Dropped);
    }

    public (int Pcs, int Phones) Counts() { lock (gate) return (pcs.Count, phoneCount); }
}
