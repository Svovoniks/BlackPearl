using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using BlackPearl.Core;
using DataChannelDotnet;
using DataChannelDotnet.Data;
using DataChannelDotnet.Impl;

// The phone remote (PHONE-REMOTE.md) end to end through a local room server: pairing, a paired phone reconnecting,
// commands and state, refused signatures, a removed phone, and a phone waiting for its PC. The phone here is a
// stand-in written against the protocol document, using the same WebRTC library as the PC.
static class PhoneRemoteFixture
{
    sealed class MemoryStore : IPairedPhoneStore
    {
        public List<PairedPhone> Saved = [];
        public IReadOnlyList<PairedPhone> Load() => Saved.ToArray();
        public void Save(IReadOnlyList<PairedPhone> phones) => Saved = phones.ToList();
    }

    public static async Task Run(string sdk, string serverDll, string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        check(PhoneRemoteProtocol.Sign("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f", "phone", "a1b2c3d4e5f60718a1b2c3d4e5f60718",
            "0123456789abcdef0123456789abcdef", "sdp", "{\"type\":\"offer\",\"sdp\":\"v=0\"}") == "AHCUPPyOcWPNmRtEBJ6D5PQUJwF9CV5-GHlhUpfTkX8", "phone remote signatures match the protocol's first test vector");
        check(PhoneRemoteProtocol.Sign("ffeeddccbbaa99887766554433221100", "pc", "pair", "0123456789abcdef0123456789abcdef", "ice",
            "{\"candidate\":\"candidate:1 1 UDP 2122317823 192.168.1.20 50000 typ host\",\"mid\":\"0\"}") == "bIGb4K6s3ztFQAOUL2Q3Oof397tsk3TiGq_W2ZB3I_I", "phone remote signatures match the protocol's second test vector");
        // A phone's progress saves as the PC's own playback does.
        check(PhoneProgress.From(754000, 6300000, false, false) == new PhoneProgress(754000, true, false), "a phone's progress saves its position and counts as played");
        check(PhoneProgress.From(5000, 6300000, false, false) == new PhoneProgress(5000, false, false), "a few seconds on the phone don't count as played");
        check(PhoneProgress.From(6299000, 6300000, false, false) == new PhoneProgress(0, true, true) && PhoneProgress.From(100, 0, true, false).Ended,
            "the end of a video, or the phone saying it ended, marks it watched and clears its resume point");
        check(PhoneProgress.From(600000, 6300000, true, false) == new PhoneProgress(600000, true, false) && PhoneProgress.From(6270000, 6300000, true, false).Ended,
            "a phone that says it ended in the middle of a video, as when its stream stopped, doesn't mark it watched");
        check(PhoneProgress.From(900000, 6300000, false, true).ResumeMs == 0, "a title watched before keeps no resume point while it is watched again, as on the PC");
        // An episode of 2696 seconds left with Next 3 minutes before its end, in the credits.
        check(PhoneProgress.From(2516000, 2696000, false, false, final: true) == new PhoneProgress(0, true, true),
            "leaving a video on the phone in its last tenth, as with Next in the credits, marks it watched");
        check(PhoneProgress.From(2516000, 2696000, false, false) == new PhoneProgress(2516000, true, false) &&
            PhoneProgress.From(2400000, 2696000, false, false, final: true) == new PhoneProgress(2400000, true, false) && !PhoneProgress.From(100, 0, false, false, final: true).Ended,
            "a pause in the credits, or leaving a video before its last tenth, keeps its resume point");
        await HostlessRoomFixture.WithServer(sdk, serverDll, directory, "phone-remote", async url => { await Exercise(url, check); await PhoneHostFixture.Run(url, directory, check); });
        await OtherServers(check);
    }

    // Servers that aren't BlackPearl room servers with the phone remote: the PC keeps trying and says why.
    static async Task OtherServers(Action<bool, string> check)
    {
        foreach (var (body, expected, name) in new[]
        {
            ("\"ok\"", "Can't reach the room server", "a server whose health check isn't a room server's"),
            ("{\"status\":\"ok\",\"protocol\":2}", "doesn't support the phone remote", "a room server from before the phone remote"),
        })
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            using var http = new System.Net.HttpListener(); http.Prefixes.Add($"http://localhost:{port}/"); http.Start();
            _ = Task.Run(async () =>
            {
                while (http.IsListening)
                {
                    try
                    {
                        var context = await http.GetContextAsync();
                        var health = context.Request.Url!.AbsolutePath == "/health";
                        context.Response.StatusCode = health ? 200 : 404;
                        if (health) { var bytes = System.Text.Encoding.UTF8.GetBytes(body); context.Response.ContentType = "application/json"; await context.Response.OutputStream.WriteAsync(bytes); }
                        context.Response.Close();
                    }
                    catch (Exception) { return; }
                }
            });
            using var host = new PhoneRemoteHost(new PhoneRemoteOptions { Server = $"http://localhost:{port}", HostKey = PersistentRooms.NewKey(), PcName = "Test PC", AppVersion = "0.0.0-test", Store = new MemoryStore() });
            host.Start();
            check(await Until(() => host.Status.Contains(expected, StringComparison.Ordinal), 10000), $"the phone remote says why it can't use {name}");
            http.Stop();
        }
    }

    static async Task Exercise(string url, Action<bool, string> check)
    {
        using var http = new HttpClient();
        check((await http.GetStringAsync(url + "/health")).Contains("\"remote\":1"), "the room service reports the phone remote protocol");

        var hostKey = PersistentRooms.NewKey();
        var pcId = PhoneRemoteProtocol.PcId(hostKey);
        // A phone that opens the app before the PC is online waits, and hears when it arrives.
        await using var early = await PhoneServerSocket.Open(url, pcId);
        check(await early.Next("remote-welcome") is not null && await early.Next("remote-offline") is not null, "a phone hears that its PC is offline");

        var store = new MemoryStore();
        using var host = new PhoneRemoteHost(new PhoneRemoteOptions { Server = url, HostKey = hostKey, PcName = "Test PC", AppVersion = "0.0.0-test", Store = store,
            ReplacedWait = TimeSpan.FromSeconds(8), Features = ["library", "watch"] });
        // A made-up video of 600,000 bytes, and a resource that is never ready.
        var video = Enumerable.Range(0, 600000).Select(i => (byte)(i * 31 % 251)).ToArray();
        host.ResolveMedia = (_, resource, _) => Task.FromResult(resource switch
        {
            "file/test" => new PhoneResource(video.Length, (offset, count, _) => Task.FromResult(video.AsSpan((int)offset, count).ToArray())),
            "stream/busy/0" => throw new PhoneResourceBusyException("Still converting."),
            _ => null,
        });
        var requests = Channel.CreateUnbounded<(string Type, JsonElement Message)>();
        host.Request += (session, type, message) => { requests.Writer.TryWrite((type, message)); host.SendMessage(session, new { type = "page", request = message.GetProperty("request").GetInt64(), entries = Array.Empty<object>() }); };
        var disconnected = 0;
        host.Disconnected += _ => Interlocked.Increment(ref disconnected);
        var commands = Channel.CreateUnbounded<RemoteCommand>();
        host.Command += (_, command) => commands.Writer.TryWrite(command);
        host.CurrentState = () => new RemoteState(true, "Night Signal", "Season 2 · Episode 3", Playing: false, PositionMs: 1000, DurationMs: 60000, Volume: 40);
        host.Start();
        check(await Until(() => host.ServerReady, 10000), "the PC connects to the room server for phones");
        check(await early.Next("remote-online") is not null, "a waiting phone hears when its PC comes online");

        var link = host.BeginPairing();
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(link).Query);
        check(link.StartsWith("blackpearl://pair?v=1&", StringComparison.Ordinal) && query["pc"] == pcId && query["server"] == url && query["name"] == "Test PC", "the pairing link names the PC, its server and its name");
        var code = query["code"]!;

        // Anyone who knows the PC's id can send it signals. Missing fields, text that isn't JSON, and offers signed with the
        // live pairing code that the native library can't read must not stop the PC from serving phones.
        await using (var junk = await PhoneServerSocket.Open(url, pcId))
        {
            var session = PhoneRemoteProtocol.NewHex(16);
            await junk.Send(new("signal", Data: $"{{\"session\":\"{session}\",\"kind\":\"sdp\"}}"));
            await junk.Send(new("signal", Data: "{}"));
            await junk.Send(new("signal", Data: "not json"));
            await junk.Send(new("signal", Data: $"{{\"phone\":\"pair\",\"session\":\"{session}\",\"kind\":\"sdp\",\"payload\":null,\"auth\":null}}"));
            foreach (var payload in new[] { "{\"type\":\"offer\",\"sdp\":\"not an sdp\"}", "{\"type\":\"offer\"}", "{\"mid\":\"0\"}", "[]" })
            {
                var signed = PhoneRemoteProtocol.NewHex(16);
                foreach (var kind in new[] { "sdp", "ice" })
                    await junk.Send(new("signal", Data: Wire.Serialize(new RemoteSignal("pair", signed, kind, payload, PhoneRemoteProtocol.Sign(code, "phone", "pair", signed, kind, payload)))));
            }
            await Task.Delay(1500);
        }
        // Status alone can't tell: a PC whose reading stopped still shows Ready. A phone it doesn't know must get its answer.
        await using (var stranger = await FakePhone.Connect(url, pcId, PhoneRemoteProtocol.NewHex(16), PhoneRemoteProtocol.NewHex(32)))
            check(host.ServerReady && host.PairingOpen && await stranger.Rejection() == "unknown-phone", "malformed and unreadable signals leave the PC answering phones and the pairing code unused");

        string phoneId, secret;
        await using (var pairing = await FakePhone.Connect(url, pcId, "pair", code))
        {
            check(await pairing.Opened(), "a phone holding the pairing code opens a direct link to the PC");
            pairing.Send(new { type = "hello", protocol = 1, app = "test", device = "Test phone" });
            var welcome = await pairing.Next("welcome");
            check(welcome?.GetProperty("pc").GetString() == "Test PC" && welcome?.GetProperty("pcId").GetString() == pcId, "the PC welcomes the phone with its name and id");
            pairing.Send(new { type = "pair", name = "Pixel test" });
            var paired = await pairing.Next("paired");
            phoneId = paired?.GetProperty("phoneId").GetString() ?? ""; secret = paired?.GetProperty("secret").GetString() ?? "";
            check(phoneId.Length == 32 && secret.Length == 64 && store.Saved.Count == 1 && store.Saved[0].Name == "Pixel test" && store.Saved[0].Secret == secret,
                "pairing gives the phone its own id and secret, and the PC keeps them");
            check(await pairing.Next("state") is { } first && first.GetProperty("title").GetString() == "Night Signal", "a newly paired phone gets the player's state at once");
            check(!host.PairingOpen, "a pairing code works once");
        }

        await using (var reuse = await FakePhone.Connect(url, pcId, "pair", code))
            check(await reuse.Rejection() == "pairing-expired", "a used pairing code is refused with a reason");

        await using (var forged = await FakePhone.Connect(url, pcId, phoneId, PhoneRemoteProtocol.NewHex(32)))
            check(!await forged.Opened(3000) && await forged.Rejection(500) is null, "a phone that signs with the wrong secret gets no answer");

        await using (var phone = await FakePhone.Connect(url, pcId, phoneId, secret))
        {
            check(await phone.Opened(), "a paired phone connects with its own secret");
            phone.Send(new { type = "hello", protocol = 1, app = "test", device = "Test phone" });
            check(await phone.Next("welcome") is not null && await phone.Next("state") is { } state && state.GetProperty("volume").GetInt32() == 40, "a paired phone is welcomed and sent the state");
            check(await Until(() => host.Connected.Count == 1 && host.Connected[0].PhoneId == phoneId, 5000), "the PC lists the connected phone");
            phone.Send(new { type = "command", action = "seek-by", value = -10000 });
            phone.Send(new { type = "command", action = "toggle" });
            using var wait = new CancellationTokenSource(5000);
            var seek = await commands.Reader.ReadAsync(wait.Token); var toggle = await commands.Reader.ReadAsync(wait.Token);
            check(seek.Action == "seek-by" && seek.Number == -10000 && toggle.Action == "toggle" && toggle.Value is null, "the phone's commands reach the PC with their values");
            host.Publish(new RemoteState(true, "Night Signal", Playing: true, PositionMs: 5000, DurationMs: 60000));
            check(await phone.Next("state") is { } playing && playing.GetProperty("playing").GetBoolean(), "the PC's state reaches the phone");
            // Sound on the phone: the PC answers where its player is at once, less the time its picture runs behind.
            var clock = new PlaybackClock { Source = "film" };
            clock.Report(90000); clock.SetRunning(true);
            host.Clock = clock;
            phone.Send(new { type = "clock", request = 41 });
            var timed = await phone.Next("clock");
            check(timed is { } answer && answer.GetProperty("request").GetInt64() == 41 && answer.GetProperty("source").GetString() == "film" && answer.GetProperty("playing").GetBoolean() &&
                answer.GetProperty("positionMs").GetInt64() is >= 90000 - PlaybackClock.PictureLagMs and < 92000 && clock.LastHeard(phoneId) is not null,
                "a phone asking where the player is gets the position of the picture at once, and the PC notes it asked");
            check(await phone.MediaOpened(), "the phone's media channel opens beside the remote's");
            var read = await phone.Read("file/test", 100000, 262144);
            check(read.Length == 600000 && read.Bytes.AsSpan().SequenceEqual(video.AsSpan(100000, 262144)), "the phone reads a range of a video in frames, and learns the whole video's size");
            var tail = await phone.Read("file/test", 599990, 262144);
            check(tail.Bytes.AsSpan().SequenceEqual(video.AsSpan(599990)) && (await phone.Read("file/test", 600000, 100)).Bytes.Length == 0, "a read past the end of the video is cut short, and one at its end is empty");
            check((await phone.Read("file/other", 0, 100)).Error == "gone" && (await phone.Read("stream/busy/0", 0, 100)).Error == "busy", "a resource the PC doesn't offer is refused, and one still being prepared asks the phone to try again");
            var welcomed = await phone.Welcome;
            check(welcomed.GetProperty("features").EnumerateArray().Select(f => f.GetString()).SequenceEqual(["library", "watch"]), "the PC's welcome lists the library and watch features");
            phone.Send(new { type = "browse", request = 7, home = true });
            using (var waitRequest = new CancellationTokenSource(5000))
            {
                var browse = await requests.Reader.ReadAsync(waitRequest.Token);
                check(browse.Type == "browse" && browse.Message.GetProperty("home").GetBoolean() && await phone.Next("page") is { } page && page.GetProperty("request").GetInt64() == 7,
                    "a phone's library request reaches the PC with its fields, and the answer reaches the phone");
            }
            phone.Send(new { type = "pair", name = "again" });
            check(await phone.Next("error") is { } refused && refused.GetProperty("code").GetString() == "pairing" && store.Saved.Count == 1, "a paired phone can't pair again on its link");
            // The pairing link closing counted too; this counts only the paired phone's.
            Interlocked.Exchange(ref disconnected, 0);
            host.Forget(phoneId);
            check(await phone.Closed() && await Until(() => disconnected == 1, 5000), "removing a phone closes its link and tells the PC it went");
        }
        // A copy of this library running elsewhere, with the same host key, takes the phone remote over. The first one
        // says why and waits, rather than the two replacing each other every second.
        using (var twin = new PhoneRemoteHost(new PhoneRemoteOptions { Server = url, HostKey = hostKey, PcName = "Copy", AppVersion = "0.0.0-test", Store = new MemoryStore() }))
        {
            twin.Start();
            check(await Until(() => twin.ServerReady, 10000) && await Until(() => host.Status.StartsWith("Another BlackPearl", StringComparison.Ordinal), 5000),
                "a second BlackPearl with the same library takes the phone remote, and the first says so");
            await Task.Delay(4000);
            check(twin.ServerReady && !host.ServerReady, "the two copies don't keep replacing each other");
        }
        check(await Until(() => host.ServerReady, 15000), "the first copy takes the phone remote back once the other has gone");

        await using (var removed = await FakePhone.Connect(url, pcId, phoneId, secret))
            check(await removed.Rejection() == "unknown-phone", "a removed phone is told it was removed");
        await using (var newer = await FakePhone.Connect(url, pcId, "pair", "00000000000000000000000000000000"))
            check(await newer.Rejection() == "pairing-expired", "a phone without a live pairing code is turned away");
    }

    // Drives a running BlackPearl as a paired phone would, for trying the desktop app by hand: each step is a command
    // name with an optional value ("toggle", "seek-by=-10000", "volume=30", "wait=2000"), a message to send as it is
    // ("send={...}"), a read on the media channel ("read=<resource>,<offset>,<count>"), or a timed run of reads
    // ("fetch=<resource>,<bytes>"). Every message the PC sends is printed, long ones shortened.
    public static async Task Try(string url, string pcId, string phoneId, string secret, string[] steps)
    {
        await using var phone = await FakePhone.Connect(url, pcId, phoneId, secret);
        if (await phone.Rejection(3000) is { } reason) throw new Exception("Refused: " + reason);
        if (!await phone.Opened()) throw new Exception("No link to the PC");
        phone.Send(new { type = "hello", protocol = 1, app = "smoke", device = "Smoke phone" });
        Console.WriteLine("welcome: " + await phone.Next("welcome"));
        _ = Task.Run(async () =>
        {
            while (await phone.Next("", 600000) is { } message)
            {
                var text = message.ToString();
                Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {(text.Length > 1500 ? text[..1500] + $"… ({text.Length} characters)" : text)}");
            }
        });
        await phone.MediaOpened();
        await Task.Delay(1500);
        foreach (var step in steps)
        {
            var parts = step.Split('=', 2);
            if (parts[0] == "wait") { await Task.Delay(int.Parse(parts[1])); continue; }
            if (parts[0] == "send") { Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} > {parts[1]}"); phone.SendText(parts[1]); await Task.Delay(1500); continue; }
            if (parts[0] is "read" or "fetch")
            {
                var fields = parts[1].Split(',');
                if (parts[0] == "read")
                {
                    var started = System.Diagnostics.Stopwatch.StartNew();
                    var result = await phone.Read(fields[0], long.Parse(fields[1]), int.Parse(fields[2]));
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} read {fields[0]} @{fields[1]}: {result.Bytes.Length} bytes of {result.Length}, error {result.Error ?? "none"}, {started.ElapsedMilliseconds} ms, first bytes {Convert.ToHexString(result.Bytes.AsSpan(0, Math.Min(8, result.Bytes.Length)))}");
                    continue;
                }
                // Eight reads of 256 KiB in flight, as the phone's proxy keeps them.
                var total = long.Parse(fields[1]); long received = 0, next = 0; var clock = System.Diagnostics.Stopwatch.StartNew();
                var inFlight = new Queue<Task<(byte[] Bytes, long Length, string? Error)>>();
                while (received < total)
                {
                    while (inFlight.Count < 8 && next < total) { inFlight.Enqueue(phone.Read(fields[0], next, 262144)); next += 262144; }
                    var reply = await inFlight.Dequeue();
                    if (reply.Error is not null || reply.Bytes.Length == 0) { Console.WriteLine("fetch stopped: " + (reply.Error ?? "end")); break; }
                    received += reply.Bytes.Length;
                }
                Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} fetched {received:N0} bytes in {clock.ElapsedMilliseconds} ms: {received * 8 / 1000.0 / Math.Max(1, clock.ElapsedMilliseconds):0.0} Mbps");
                continue;
            }
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} > {step}");
            if (parts.Length == 1) phone.Send(new { type = "command", action = parts[0] });
            else phone.Send(new { type = "command", action = parts[0], value = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) });
            await Task.Delay(1200);
        }
    }

    static async Task<bool> Until(Func<bool> condition, int milliseconds)
    {
        for (var waited = 0; waited < milliseconds; waited += 50) { if (condition()) return true; await Task.Delay(50); }
        return condition();
    }
}
