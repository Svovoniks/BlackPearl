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

        // Someone who saw a phone's signals on the home network (its socket is plain ws://) sends them again. None of them
        // is taken: each connection has its own session id, a signal of a session that isn't live proves nothing, and the
        // phone's link stays up.
        await using (var real = await FakePhone.Connect(lan, pcId, phone.Id, phone.Secret))
        {
            check(await real.Opened() && real.Offer is not null, "a phone links up on the home network for the replay checks");
            await using var replayer = await RawSocket.Open(preferred);
            await replayer.Send(new("remote-phone", Target: pcId, Number: 1));
            var handed = await replayer.Next("remote-welcome") is { } w ? JsonDocument.Parse(w.Data!).RootElement.GetProperty("session").GetString() : null;
            check(handed is { Length: 32 } && handed != real.Session && PhoneRemoteProtocol.IsLanSession(hostKey, handed) && !PhoneRemoteProtocol.IsLanSession(PersistentRooms.NewKey(), handed),
                "each home-network connection hands its phone a session id of its own, which only this PC's key makes");
            await replayer.Send(new("signal", Data: real.Offer));
            var ice = Wire.Serialize(new RemoteIce("candidate:9 1 UDP 1 192.168.1.9 9 typ host", "0"));
            await replayer.Send(new("signal", Data: Wire.Serialize(new RemoteSignal(phone.Id, real.Session, "ice", ice, PhoneRemoteProtocol.Sign(phone.Secret, "phone", phone.Id, real.Session, "ice", ice)))));
            var old = PhoneRemoteProtocol.NewHex(16);
            await replayer.Send(new("signal", Data: Wire.Serialize(new RemoteSignal(phone.Id, old, "ice", ice, PhoneRemoteProtocol.Sign(phone.Secret, "phone", phone.Id, old, "ice", ice)))));
            check(await replayer.Next("signal", 1500) is null && !await real.Closed(500), "a phone's offer and candidates sent again on another connection are ignored, and its link stays up");
            check(await replayer.ClosedWithin(5000), "a connection that only repeats someone's signals proves nothing and is closed after a while");
            // The same phone connecting again: its newer connection is the one kept.
            await using var again = await FakePhone.Connect(lan, pcId, phone.Id, phone.Secret);
            check(await again.Opened() && await Until(() => host.LanConnections == 1, 3000), "a phone connecting again keeps one connection on the listener, the newest");
        }
        await Until(() => host.LanConnections == 0, 5000);

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

    // Offers sent again through the room server, which anyone who knows the PC's id can reach. An offer seen before is
    // refused, and so is one made on the home network (its session id says so, also after the PC restarted), so the
    // phone's live link stays up.
    public static async Task Replays(string url, Action<bool, string> check)
    {
        var hostKey = PersistentRooms.NewKey();
        var pcId = PhoneRemoteProtocol.PcId(hostKey);
        var phone = new PairedPhone(PhoneRemoteProtocol.NewHex(16), "Replay phone", PhoneRemoteProtocol.NewHex(32), Wire.Now, Wire.Now);
        var port = FreePort();
        PhoneRemoteHost Host() => new(new PhoneRemoteOptions
        {
            Server = url, HostKey = hostKey, PcName = "Replay PC", AppVersion = "0.0.0-test", Store = new MemoryStore(phone),
            Lan = new PhoneLanOptions { LoopbackOnly = true, Port = port },
        });
        string lanOffer;
        using (var host = Host())
        {
            host.Start();
            check(await Until(() => host.ServerReady && host.Lan is { State: "listening" }, 10000), "a PC reaches its room server and listens on the home network for the replay checks");
            string firstOffer;
            await using (var first = await FakePhone.Connect(url, pcId, phone.Id, phone.Secret))
            {
                check(await first.Opened(), "a phone links up through the room server");
                firstOffer = first.Offer!;
            }
            await using (var onLan = await FakePhone.Connect($"http://127.0.0.1:{port}", pcId, phone.Id, phone.Secret))
            {
                check(await onLan.Opened(), "the phone links up on the home network");
                lanOffer = onLan.Offer!;
            }
            await using var live = await FakePhone.Connect(url, pcId, phone.Id, phone.Secret);
            check(await live.Opened(), "the phone links up through the room server again");
            await using var replayer = await PhoneServerSocket.Open(url, pcId);
            await replayer.Send(new("signal", Data: firstOffer));
            await replayer.Send(new("signal", Data: lanOffer));
            check(!await live.Closed(2000), "an earlier offer, or one from the home network, sent again through the room server doesn't replace the phone's link");

            // A device answering at the PC's home address, which the phone tries on any Wi-Fi, hands out a session id of
            // its choosing and collects the phone's offer. That offer is signed for the home network only: sent through
            // the room server it is refused, and the phone's link stays up.
            var impostorPort = FreePort();
            using var impostor = new System.Net.HttpListener(); impostor.Prefixes.Add($"http://localhost:{impostorPort}/"); impostor.Start();
            var chosen = PhoneRemoteProtocol.NewHex(16);
            var served = Task.Run(async () =>
            {
                var context = await impostor.GetContextAsync();
                var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
                await SocketMessages.ReadAsync(socket, CancellationToken.None);
                await SocketMessages.SendAsync(socket, new("remote-welcome", Data: Wire.Serialize(new { iceServers = Array.Empty<string>(), forceRelay = false, session = chosen })), CancellationToken.None);
                await SocketMessages.SendAsync(socket, new("remote-online"), CancellationToken.None);
                try { while (await SocketMessages.ReadAsync(socket, CancellationToken.None) is not null) { } } catch (Exception) { }
            });
            string? harvested;
            await using (var fooled = await FakePhone.Connect($"http://localhost:{impostorPort}", pcId, phone.Id, phone.Secret))
            {
                await Until(() => fooled.Offer is not null, 5000);
                harvested = fooled.Offer;
            }
            check(harvested is not null && Wire.Read<RemoteSignal>(harvested).Session == chosen, "a device at the PC's home address gets the phone's offer under the session id it chose");
            await replayer.Send(new("signal", Data: harvested!));
            check(!await live.Closed(2000), "that offer, sent through the room server, is refused: it is signed for the home network");
            impostor.Stop();

            // The other way round: an offer signed for the room server is refused on the home network, even under the
            // session id that connection handed out.
            await using var raw = await RawSocket.Open(port);
            await raw.Send(new("remote-phone", Target: pcId, Number: 1));
            var handed = JsonDocument.Parse((await raw.Next("remote-welcome"))!.Data!).RootElement.GetProperty("session").GetString()!;
            var payload = Wire.Serialize(new RemoteSdp("offer", "v=0"));
            await raw.Send(new("signal", Data: Wire.Serialize(new RemoteSignal(phone.Id, handed, "sdp", payload, PhoneRemoteProtocol.Sign(phone.Secret, PhoneRemoteProtocol.FromPhone, phone.Id, handed, "sdp", payload)))));
            check(!await live.Closed(2000) && await raw.Next("signal", 1000) is null, "an offer signed for the room server is refused on the home network, under its handed-out session id too");
        }
        // After a restart the PC remembers no offers, and still knows a home-network one by its session id.
        using (var restarted = Host())
        {
            restarted.Start();
            check(await Until(() => restarted.ServerReady, 10000), "the restarted PC reaches its room server");
            await using var live = await FakePhone.Connect(url, pcId, phone.Id, phone.Secret);
            check(await live.Opened(), "the phone links up to the restarted PC");
            await using var replayer = await PhoneServerSocket.Open(url, pcId);
            await replayer.Send(new("signal", Data: lanOffer));
            check(!await live.Closed(2000), "after a restart, an offer seen on the home network still can't replace the phone's link through the room server");
        }
    }

    // Offers arriving at the same moment on several home-network connections are taken one at a time, and every session
    // they open is closed again: none is left running unaccounted for.
    public static async Task Concurrent(Action<bool, string> check)
    {
        var hostKey = PersistentRooms.NewKey();
        var pcId = PhoneRemoteProtocol.PcId(hostKey);
        var phone = new PairedPhone(PhoneRemoteProtocol.NewHex(16), "Burst phone", PhoneRemoteProtocol.NewHex(32), Wire.Now, Wire.Now);
        var port = FreePort();
        var host = new PhoneRemoteHost(new PhoneRemoteOptions
        {
            Server = $"http://127.0.0.1:{FreePort()}", HostKey = hostKey, PcName = "Burst PC", AppVersion = "0.0.0-test", Store = new MemoryStore(phone),
            Lan = new PhoneLanOptions { LoopbackOnly = true, Port = port },
        });
        host.Start();
        await Until(() => host.Lan is { State: "listening" }, 5000);
        // One address can hold only a few connections that haven't proved anything.
        {
            var held = new List<RawSocket>();
            for (var i = 0; i < 4; i++) { var s = await RawSocket.Open(port); await s.Send(new("remote-phone", Target: pcId, Number: 1)); await s.Next("remote-online"); held.Add(s); }
            // Refused as it connects: its WebSocket never opens.
            bool refused;
            try { await using var fifth = await RawSocket.Open(port); refused = await fifth.ClosedWithin(2000); }
            catch (WebSocketException) { refused = true; }
            check(refused && !await held[0].ClosedWithin(200) && host.LanConnections == 4,
                "a fifth unproven connection from one address is refused while the listener still has room for others");
            foreach (var s in held) await s.DisposeAsync();
        }
        await Until(() => host.LanConnections == 0, 5000);
        const int Pairs = 25;
        for (var round = 0; round < Pairs; round++)
        {
            var sockets = new List<(RawSocket Socket, string Session)>();
            for (var i = 0; i < 2; i++)
            {
                var socket = await RawSocket.Open(port);
                await socket.Send(new("remote-phone", Target: pcId, Number: 1));
                var welcome = await socket.Next("remote-welcome");
                sockets.Add((socket, JsonDocument.Parse(welcome!.Data!).RootElement.GetProperty("session").GetString()!));
            }
            // Signed by the phone, each under its connection's own session id, sent together.
            var payload = Wire.Serialize(new RemoteSdp("offer", "v=0"));
            await Task.WhenAll(sockets.Select(s => s.Socket.Send(new("signal",
                Data: Wire.Serialize(new RemoteSignal(phone.Id, s.Session, "sdp", payload, PhoneRemoteProtocol.Sign(phone.Secret, PhoneRemoteProtocol.FromPhoneLan, phone.Id, s.Session, "sdp", payload)))))));
            await Task.Delay(30);
            foreach (var (socket, _) in sockets) await socket.DisposeAsync();
        }
        await Task.Delay(500);
        host.Dispose();
        await Task.Delay(1500);
        await PlaybackDiagnostics.FlushAsync();
        var records = Records().Where(r => r.Kind is "remote-session-opened" or "remote-session-closed" && Field(r.Data, "phone") == phone.Id[..8]).ToArray();
        var opened = records.Where(r => r.Kind == "remote-session-opened").Select(r => Field(r.Data, "session")).ToHashSet();
        var closed = records.Where(r => r.Kind == "remote-session-closed").Select(r => Field(r.Data, "session")).ToHashSet();
        check(opened.Count >= Pairs && opened.SetEquals(closed), $"offers arriving together each open a session that is closed again ({opened.Count} opened, {closed.Count} closed)");
    }

    // The room server names who sent a signal. One naming a home-network connection is ignored: the PC's answers would
    // otherwise go to that connection rather than back through the server.
    public static async Task SenderGuard(Action<bool, string> check)
    {
        var hostKey = PersistentRooms.NewKey();
        var phone = new PairedPhone(PhoneRemoteProtocol.NewHex(16), "Guard phone", PhoneRemoteProtocol.NewHex(32), Wire.Now, Wire.Now);
        var port = FreePort();
        using var http = new System.Net.HttpListener(); http.Prefixes.Add($"http://localhost:{port}/"); http.Start();
        string Offer(string session)
        {
            var payload = Wire.Serialize(new RemoteSdp("offer", "v=0"));
            return Wire.Serialize(new RemoteSignal(phone.Id, session, "sdp", payload, PhoneRemoteProtocol.Sign(phone.Secret, "phone", phone.Id, session, "sdp", payload)));
        }
        string named = PhoneRemoteProtocol.NewHex(16), plain = PhoneRemoteProtocol.NewHex(16);
        var served = Task.Run(async () =>
        {
            var context = await http.GetContextAsync();
            var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            await SocketMessages.ReadAsync(socket, CancellationToken.None);
            await SocketMessages.SendAsync(socket, new("remote-welcome", Data: Wire.Serialize(new RemoteWelcome([], false))), CancellationToken.None);
            await SocketMessages.SendAsync(socket, new("signal", Sender: "lan:0011223344556677", Data: Offer(named)), CancellationToken.None);
            await SocketMessages.SendAsync(socket, new("signal", Sender: "4f0c0e2e9d0b4b0c8a3f0c1d2e3f4a5b", Data: Offer(plain)), CancellationToken.None);
            await Task.Delay(2000);
        });
        using (var host = new PhoneRemoteHost(new PhoneRemoteOptions { Server = $"http://localhost:{port}", HostKey = hostKey, PcName = "Guard PC", AppVersion = "0.0.0-test", Store = new MemoryStore(phone) }))
        {
            host.Start();
            await served.WaitAsync(TimeSpan.FromSeconds(20));
        }
        await PlaybackDiagnostics.FlushAsync();
        var opened = Records().Where(r => r.Kind == "remote-session-opened").Select(r => Field(r.Data, "session")).ToHashSet();
        check(opened.Contains(plain[..8]) && !opened.Contains(named[..8]), "a signal the room server says came from a home-network connection is ignored; others are taken");
        http.Stop();
    }

    static IEnumerable<(string Kind, JsonElement Data)> Records()
    {
        string text;
        using (var stream = new FileStream(Path.Combine(PlaybackDiagnostics.DirectoryPath!, "diagnostics.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream)) text = reader.ReadToEnd();
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line).RootElement)
            .Select(r => (r.GetProperty("kind").GetString()!, r.GetProperty("data"))).ToArray();
    }
    static string Field(JsonElement record, string name) => record.TryGetProperty(name, out var value) ? value.ToString() : "";

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
