using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using BlackPearl.Core;

// The phone remote on the home network (PHONE-REMOTE.md, "On the home network"): the PC's own listener, which speaks the
// room server's /remote protocol for this PC only. Its room server is out of reach throughout, as when the PC has lost
// its internet. A paired phone connects with its signed signals; strangers, forgeries, other PCs' ids, oversized and
// rapid messages, idle and silent connections are turned away; the connection cap keeps proven phones; and a second
// BlackPearl process of the library waits for the port rather than moving to another. Loopback only: no firewall prompt.
static class PhoneLanFixture
{
    sealed class MemoryStore(params PairedPhone[] phones) : IPairedPhoneStore
    {
        public List<PairedPhone> Saved = phones.ToList();
        public IReadOnlyList<PairedPhone> Load() => Saved.ToArray();
        public void Save(IReadOnlyList<PairedPhone> list) => Saved = list.ToList();
    }

    static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    public static async Task Run(Action<bool, string> check)
    {
        var hostKey = PersistentRooms.NewKey();
        var pcId = PhoneRemoteProtocol.PcId(hostKey);
        var phone = new PairedPhone(PhoneRemoteProtocol.NewHex(16), "Lan phone", PhoneRemoteProtocol.NewHex(32), Wire.Now, Wire.Now);
        // Nothing listens on the room server's port: the server is unreachable for the whole run.
        var offline = $"http://127.0.0.1:{FreePort()}";
        var preferred = FreePort();
        var chosen = 0;
        using var host = new PhoneRemoteHost(new PhoneRemoteOptions
        {
            Server = offline, HostKey = hostKey, PcName = "Lan PC", AppVersion = "0.0.0-test", Store = new MemoryStore(phone), Features = ["library"],
            Lan = new PhoneLanOptions { LoopbackOnly = true, Port = preferred, MaxConnections = 3, Unproven = TimeSpan.FromSeconds(3), Handshake = TimeSpan.FromSeconds(2), PortChosen = p => chosen = p },
        });
        host.CurrentState = () => new RemoteState(false, Volume: 30);
        check(host.Lan is { State: "starting" }, "a PC that listens on the home network says so before it has started");
        host.Start();
        check(await Until(() => host.Lan is { State: "listening" }, 5000) && host.Lan!.Port == preferred && chosen == preferred && host.Lan.LoopbackOnly,
            "the PC listens on the home network on the port it used last, and reports it to be kept");
        var lan = $"http://127.0.0.1:{preferred}";

        // A paired phone reaches the PC with the room server down.
        await using (var link = await FakePhone.Connect(lan, pcId, phone.Id, phone.Secret))
        {
            check(await link.Opened(), "a paired phone opens a direct link over the home network while the room server is out of reach");
            link.Send(new { type = "hello", protocol = 1, app = "test", device = "Lan phone" });
            var welcome = await link.Welcome.WaitAsync(TimeSpan.FromSeconds(5));
            check(welcome.TryGetProperty("lan", out var where) && where.GetProperty("port").GetInt32() == preferred &&
                where.GetProperty("addresses").EnumerateArray().Select(a => a.GetString()).SequenceEqual(["127.0.0.1"]),
                "the PC's welcome tells the phone where it listens on the home network");
            check(await link.Next("state") is { } state && state.GetProperty("volume").GetInt32() == 30, "the phone connected over the home network gets the PC's state");
            check(host.Connected.Count == 1 && host.LanConnections == 1, "the PC counts the phone and its home-network connection");
        }

        // A phone the PC doesn't know gets the same unsigned hint as through the room server, and nothing else.
        await using (var stranger = await FakePhone.Connect(lan, pcId, PhoneRemoteProtocol.NewHex(16), PhoneRemoteProtocol.NewHex(32)))
            check(await stranger.Rejection() == "unknown-phone" && !await stranger.Opened(1500), "a phone the PC doesn't know is told so on the home network, and gets no link");
        // A forged signature for a known phone: no answer, no link, and its connection is closed once it has proved nothing.
        await using (var forged = await RawSocket.Open(preferred))
        {
            await forged.Send(new("remote-phone", Target: pcId, Number: 1));
            check(await forged.Next("remote-welcome") is { } w && Wire.Read<RemoteWelcome>(w.Data!).IceServers.Length == 0 && await forged.Next("remote-online") is not null,
                "the home-network listener greets a phone as the room server would, with no relay servers");
            var session = PhoneRemoteProtocol.NewHex(16);
            var payload = Wire.Serialize(new RemoteSdp("offer", "v=0"));
            await forged.Send(new("signal", Data: Wire.Serialize(new RemoteSignal(phone.Id, session, "sdp", payload, PhoneRemoteProtocol.Sign(PhoneRemoteProtocol.NewHex(32), "phone", phone.Id, session, "sdp", payload)))));
            await forged.Send(new("signal", Data: Wire.Serialize(new RemoteSignal(phone.Id, session, "sdp", payload, ""))));
            check(await forged.Next("signal", 1500) is null, "forged and unsigned signals on the home network get no answer");
            check(await forged.ClosedWithin(5000), "a connection that sent no valid signature is closed after a while");
        }

        // Another PC's id: refused at once with a reason, so the phone tries its next address.
        await using (var other = await RawSocket.Open(preferred))
        {
            await other.Send(new("remote-phone", Target: PhoneRemoteProtocol.PcId(PersistentRooms.NewKey()), Number: 1));
            check(await other.Next("error") is { } refused && refused.Text == "This is another PC." && await other.ClosedWithin(3000), "a phone asking for another PC is told so and disconnected");
        }
        await using (var older = await RawSocket.Open(preferred))
        {
            await older.Send(new("remote-phone", Target: pcId, Number: 2));
            check(await older.Next("error") is { } refused && refused.Text!.Contains("Update BlackPearl on the PC", StringComparison.Ordinal), "a phone speaking a newer protocol is told to update the PC");
        }
        // Not a WebSocket at all: a browser, or something scanning the network.
        using (var plain = new TcpClient())
        {
            await plain.ConnectAsync(IPAddress.Loopback, preferred);
            var stream = plain.GetStream();
            await stream.WriteAsync("GET / HTTP/1.1\r\nHost: pc\r\n\r\n"u8.ToArray());
            var answer = new byte[256]; var read = await stream.ReadAsync(answer).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            check(Encoding.ASCII.GetString(answer, 0, read).StartsWith("HTTP/1.1 404", StringComparison.Ordinal), "a plain web request to the listener is answered Not Found");
        }
        using (var silent = new TcpClient())
        {
            await silent.ConnectAsync(IPAddress.Loopback, preferred);
            var read = await silent.GetStream().ReadAsync(new byte[16]).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            check(read == 0, "a connection that never sends its request is closed after the handshake time");
        }
        using (var flood = new TcpClient())
        {
            await flood.ConnectAsync(IPAddress.Loopback, preferred);
            var stream = flood.GetStream();
            var header = Encoding.ASCII.GetBytes("GET /remote HTTP/1.1\r\nX-Padding: " + new string('a', 9000));
            var closed = false;
            try { await stream.WriteAsync(header); await stream.ReadAsync(new byte[16]).AsTask().WaitAsync(TimeSpan.FromSeconds(3)); closed = true; }
            catch (IOException) { closed = true; } catch (TimeoutException) { }
            check(closed && await Until(() => host.LanConnections == 0, 3000), "a request longer than 8 KiB is refused");
        }
        await using (var large = await RawSocket.Open(preferred))
        {
            await large.Send(new("remote-phone", Target: pcId, Number: 1));
            await large.Next("remote-online");
            await large.SendText("{\"type\":\"signal\",\"data\":\"" + new string('x', 70000) + "\"}");
            check(await large.ClosedWithin(3000), "a message over 64 KiB ends the connection");
        }
        await using (var rapid = await RawSocket.Open(preferred))
        {
            await rapid.Send(new("remote-phone", Target: pcId, Number: 1));
            await rapid.Next("remote-online");
            try { for (var i = 0; i < 400; i++) await rapid.Send(new("ping", Number: i)); } catch (Exception ex) when (ex is WebSocketException or IOException) { }
            check(await rapid.Next("error", 3000) is { } limited && limited.Text!.Contains("rate", StringComparison.OrdinalIgnoreCase) && await rapid.ClosedWithin(3000),
                "a connection sending faster than the room server allows is cut off");
        }

        // The cap: three connections. A proven phone keeps its place; the oldest connection that proved nothing makes room.
        await Until(() => host.LanConnections == 0, 5000);
        await using (var kept = await FakePhone.Connect(lan, pcId, phone.Id, phone.Secret))
        {
            check(await kept.Opened(), "a phone connects for the cap check");
            await using var first = await RawSocket.Open(preferred);
            await first.Send(new("remote-phone", Target: pcId, Number: 1)); await first.Next("remote-online");
            await using var second = await RawSocket.Open(preferred);
            await second.Send(new("remote-phone", Target: pcId, Number: 1)); await second.Next("remote-online");
            await using var third = await RawSocket.Open(preferred);
            await third.Send(new("remote-phone", Target: pcId, Number: 1));
            check(await third.Next("remote-online") is not null && await first.ClosedWithin(2000) && !await second.ClosedWithin(300) && !await kept.Closed(300) && host.LanConnections == 3,
                "with the listener full, the oldest connection that hasn't proved itself makes room; the phone keeps its link");
        }

        // The other BlackPearl process of this library still has the port: a new listener waits for it instead of moving,
        // then takes the same port once it is free. Without one, a taken port means the next of the ten.
        var siblingRunning = true;
        using (var successor = new PhoneRemoteHost(new PhoneRemoteOptions
        {
            Server = offline, HostKey = hostKey, PcName = "Lan PC", AppVersion = "0.0.0-test", Store = new MemoryStore(phone),
            Lan = new PhoneLanOptions { LoopbackOnly = true, Port = preferred, SiblingRunning = () => siblingRunning },
        }))
        {
            successor.Start();
            check(await Until(() => successor.Lan is { State: "waiting" }, 3000) && successor.Lan!.Port == preferred, "a BlackPearl taking over from the other process waits for its port");
            await Task.Delay(1000);
            check(successor.Lan is { State: "waiting" }, "it keeps waiting while the other process runs");
            host.Dispose(); siblingRunning = false;
            check(await Until(() => successor.Lan is { State: "listening" }, 3000) && successor.Lan!.Port == preferred, "it takes the same port the moment the other process lets go");
            await using var reached = await FakePhone.Connect(lan, pcId, phone.Id, phone.Secret);
            check(await reached.Opened(), "phones reach the new process at the address they knew");
            using var stranger = new PhoneRemoteHost(new PhoneRemoteOptions
            {
                Server = offline, HostKey = PersistentRooms.NewKey(), PcName = "Other", AppVersion = "0.0.0-test", Store = new MemoryStore(),
                Lan = new PhoneLanOptions { LoopbackOnly = true, Port = preferred },
            });
            stranger.Start();
            check(await Until(() => stranger.Lan is { State: "listening" }, 3000) && stranger.Lan!.Port is >= PhoneRemoteProtocol.LanPort and < PhoneRemoteProtocol.LanPort + PhoneRemoteProtocol.LanPorts,
                "a listener whose port another program holds moves to the next free one of the ten");
        }
        using (var plainHost = new PhoneRemoteHost(new PhoneRemoteOptions { Server = offline, HostKey = hostKey, PcName = "x", AppVersion = "0", Store = new MemoryStore() }))
            check(plainHost.Lan is null,
            "a PC without the home-network option doesn't listen");

        await PlaybackDiagnostics.FlushAsync();
        string text;
        using (var stream = new FileStream(Path.Combine(PlaybackDiagnostics.DirectoryPath!, "diagnostics.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream)) text = await reader.ReadToEndAsync();
        var records = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line).RootElement)
            .Where(r => r.GetProperty("kind").GetString() == "remote-lan").Select(r => r.GetProperty("data")).ToArray();
        string Field(JsonElement record, string name) => record.TryGetProperty(name, out var value) ? value.ToString() : "";
        check(records.Any(r => Field(r, "state") == "started" && Field(r, "port") == preferred.ToString() && Field(r, "bind") == "loopback") &&
            records.Any(r => Field(r, "state") == "waiting") && records.Any(r => Field(r, "state") == "stopped"),
            "the log records the listener starting, waiting for the other process, and stopping");
        check(new[] { "wrong-pc", "protocol", "bad-request", "too-large", "rate", "unproven", "too-many-connections" }.All(reason => records.Any(r => Field(r, "state") == "refused" && Field(r, "reason") == reason)),
            "the log records each kind of connection the listener turned away");
        check(!new[] { hostKey, phone.Secret, phone.Id, pcId }.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase)), "the listener's log holds no keys, secrets or whole ids");
    }

    // A bare WebSocket to the listener, for what a phone wouldn't send.
    sealed class RawSocket : IAsyncDisposable
    {
        readonly ClientWebSocket socket = new();
        readonly System.Threading.Channels.Channel<WireMessage> received = System.Threading.Channels.Channel.CreateUnbounded<WireMessage>();
        readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public static async Task<RawSocket> Open(int port)
        {
            var result = new RawSocket();
            await result.socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/remote"), CancellationToken.None);
            _ = Task.Run(async () =>
            {
                try { while (await SocketMessages.ReadAsync(result.socket, CancellationToken.None) is { } message) result.received.Writer.TryWrite(message); }
                catch (Exception) { }
                result.received.Writer.TryComplete(); result.closed.TrySetResult();
            });
            return result;
        }
        public Task Send(WireMessage message) => SocketMessages.SendAsync(socket, message, CancellationToken.None);
        public Task SendText(string text) => socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
        public async Task<WireMessage?> Next(string type, int milliseconds = 3000)
        {
            using var wait = new CancellationTokenSource(milliseconds);
            try { while (true) { var message = await received.Reader.ReadAsync(wait.Token); if (message.Type == type) return message; } }
            catch (Exception ex) when (ex is OperationCanceledException or System.Threading.Channels.ChannelClosedException) { return null; }
        }
        public async Task<bool> ClosedWithin(int milliseconds) => await Task.WhenAny(closed.Task, Task.Delay(milliseconds)) == closed.Task;
        public async ValueTask DisposeAsync()
        {
            try { using var closing = new CancellationTokenSource(1000); await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, closing.Token); } catch { }
            socket.Dispose();
        }
    }

    static async Task<bool> Until(Func<bool> condition, int milliseconds)
    {
        for (var waited = 0; waited < milliseconds; waited += 50) { if (condition()) return true; await Task.Delay(50); }
        return condition();
    }
}
