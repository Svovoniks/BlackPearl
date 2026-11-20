using System.Net.WebSockets;
using BlackPearl.Core;

// Introduces phones running BlackPearl Mobile to the PC they were paired with (PHONE-REMOTE.md). A PC stays connected
// under the id its host key gives it; a phone asks for that id and the two exchange signed WebRTC signaling through
// here. Only the phone and the PC can sign, so this service can pass signals along but can't answer in the PC's place.
// Commands and playback state travel on the direct link they set up, never through here.
sealed class RemoteLinks(Func<string, string[]> iceServers, bool forceRelay)
{
    public const int MaxPhonesPerPc = 8, MaxPhones = 20000;
    readonly object gate = new();
    readonly Dictionary<string, RemotePc> pcs = new();
    // Every phone connection, online PC or not, by the PC it asked for.
    readonly Dictionary<string, Dictionary<string, RemotePhone>> phones = new();
    int phoneCount;

    sealed class RemotePc(string id, WebSocket socket, CancellationTokenSource lifetime) : Connection(socket, lifetime) { public string Id => id; }
    sealed class RemotePhone(string id, string pc, WebSocket socket, CancellationTokenSource lifetime) : Connection(socket, lifetime) { public string Id => id; public string Pc => pc; }

    static bool Hex(string? text, int length) => text is not null && text.Length == length && text.All(Uri.IsHexDigit);
    string Welcome(string target) => Wire.Serialize(new RemoteWelcome(iceServers(target), forceRelay));

    // Returns once the connection ends. Errors that should reach the app are thrown as InvalidDataException before the
    // connection is registered, or sent by the caller's error path after.
    public async Task ServeAsync(WebSocket socket, CancellationTokenSource lifetime, WireMessage first, Action<Connection, Action> entered)
    {
        if (first.Number != PhoneRemoteProtocol.Version)
            throw new InvalidDataException(first.Number > PhoneRemoteProtocol.Version ? "This room server is older than your BlackPearl. Whoever runs it needs to update it."
                : "This phone app is older than the room server. Update BlackPearl Mobile.");
        if (first.Type == "remote-pc") await ServePc(socket, lifetime, first, entered);
        else await ServePhone(socket, lifetime, first, entered);
    }

    async Task ServePc(WebSocket socket, CancellationTokenSource lifetime, WireMessage first, Action<Connection, Action> entered)
    {
        if (!Hex(first.Data, 64)) throw new InvalidDataException("Invalid PC key");
        var pc = new RemotePc(PhoneRemoteProtocol.PcId(first.Data!), socket, lifetime);
        entered(pc, () => Leave(pc));
        _ = pc.SendLoop();
        lock (gate)
        {
            // A PC connecting again replaces its earlier connection, usually one the network dropped unnoticed.
            if (pcs.Remove(pc.Id, out var earlier)) earlier.Close("BlackPearl connected again from this PC.");
            pcs[pc.Id] = pc;
            pc.Send(new("remote-welcome", Target: pc.Id, Data: Welcome(pc.Id)));
            if (phones.TryGetValue(pc.Id, out var waiting)) foreach (var phone in waiting.Values) phone.Send(new("remote-online"));
        }
        await Read(socket, lifetime, 400, 160, message =>
        {
            if (message.Type != "signal") return;
            lock (gate)
                if (pcs.GetValueOrDefault(pc.Id) == pc && phones.TryGetValue(pc.Id, out var list) && list.TryGetValue(message.Target ?? "", out var phone))
                    phone.Send(new("signal", Sender: pc.Id, Data: message.Data));
        }, pc);
    }

    async Task ServePhone(WebSocket socket, CancellationTokenSource lifetime, WireMessage first, Action<Connection, Action> entered)
    {
        var target = (first.Target ?? "").ToUpperInvariant();
        if (!Hex(target, 32)) throw new InvalidDataException("Invalid PC id. Pair the phone again.");
        var phone = new RemotePhone(Guid.NewGuid().ToString("N"), target, socket, lifetime);
        lock (gate)
        {
            if (phoneCount >= MaxPhones) throw new InvalidDataException("The room server is busy. Try again later.");
            var list = phones.TryGetValue(target, out var existing) ? existing : phones[target] = new();
            if (list.Count >= MaxPhonesPerPc) throw new InvalidDataException("Too many phones are connecting to this PC right now.");
            list[phone.Id] = phone; phoneCount++;
        }
        entered(phone, () => Leave(phone));
        _ = phone.SendLoop();
        lock (gate)
        {
            phone.Send(new("remote-welcome", Target: phone.Id, Data: Welcome(phone.Id)));
            phone.Send(new(pcs.ContainsKey(target) ? "remote-online" : "remote-offline"));
        }
        await Read(socket, lifetime, 120, 60, message =>
        {
            if (message.Type != "signal" || message.Data is null) return;
            lock (gate) if (pcs.TryGetValue(target, out var pc)) pc.Send(new("signal", Sender: phone.Id, Data: message.Data));
        }, phone);
    }

    static async Task Read(WebSocket socket, CancellationTokenSource lifetime, double burst, double perSecond, Action<WireMessage> handle, Connection connection)
    {
        var tokens = burst; var refilled = Wire.Now;
        while (!lifetime.IsCancellationRequested)
        {
            var message = await SocketMessages.ReadAsync(socket, lifetime.Token); if (message is null) break;
            var now = Wire.Now; tokens = Math.Min(burst, tokens + (now - refilled) * perSecond / 1000); refilled = now;
            if (--tokens < 0) throw new InvalidDataException("Message rate limit exceeded");
            if (message.Type == "ping") { connection.Send(new("pong", Number: message.Number, Data: Wire.Now.ToString())); continue; }
            handle(message);
        }
    }

    void Leave(RemotePc pc)
    {
        lock (gate)
        {
            if (pcs.GetValueOrDefault(pc.Id) != pc) return;
            pcs.Remove(pc.Id);
            if (phones.TryGetValue(pc.Id, out var waiting)) foreach (var phone in waiting.Values) phone.Send(new("remote-offline"));
        }
    }

    void Leave(RemotePhone phone)
    {
        lock (gate)
        {
            if (!phones.TryGetValue(phone.Pc, out var list) || !list.Remove(phone.Id)) return;
            phoneCount--;
            if (list.Count == 0) phones.Remove(phone.Pc);
            if (pcs.TryGetValue(phone.Pc, out var pc)) pc.Send(new("peer-left", Sender: phone.Id));
        }
    }

    public (int Pcs, int Phones) Counts() { lock (gate) return (pcs.Count, phoneCount); }
}
