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
        // The full suite has started the log already; on its own (--phone-remote) this run keeps one beside its files.
        PlaybackDiagnostics.Initialize(Path.Combine(directory, "logs"));
        check(PhoneRemoteProtocol.Sign("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f", "phone", "a1b2c3d4e5f60718a1b2c3d4e5f60718",
            "0123456789abcdef0123456789abcdef", "sdp", "{\"type\":\"offer\",\"sdp\":\"v=0\"}") == "AHCUPPyOcWPNmRtEBJ6D5PQUJwF9CV5-GHlhUpfTkX8", "phone remote signatures match the protocol's first test vector");
        check(PhoneRemoteProtocol.Sign("ffeeddccbbaa99887766554433221100", "pc", "pair", "0123456789abcdef0123456789abcdef", "ice",
            "{\"candidate\":\"candidate:1 1 UDP 2122317823 192.168.1.20 50000 typ host\",\"mid\":\"0\"}") == "bIGb4K6s3ztFQAOUL2Q3Oof397tsk3TiGq_W2ZB3I_I", "phone remote signatures match the protocol's second test vector");
        check(PhoneRemoteProtocol.Sign("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f", PhoneRemoteProtocol.FromPhoneLan, "a1b2c3d4e5f60718a1b2c3d4e5f60718",
            "0011223344556677d1e4a9c03b5f7e28", "sdp", "{\"type\":\"offer\",\"sdp\":\"v=0\"}") == "GdkJyJCxBUGJpiHHL2urH73n_M2RuZIlVaNTYTYwyhU" &&
            PhoneRemoteProtocol.Sign("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f", PhoneRemoteProtocol.FromPcLan, "a1b2c3d4e5f60718a1b2c3d4e5f60718",
            "0011223344556677d1e4a9c03b5f7e28", "sdp", "{\"type\":\"answer\",\"sdp\":\"v=0\"}") == "8PfaZ2C-qbq-HCuNntwQO9rtwXUkaGyZa_cDuwxK3EI",
            "home-network signatures match the protocol's third and fourth test vectors");
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
        LinkStats(check);
        await HostlessRoomFixture.WithServer(sdk, serverDll, directory, "phone-remote", async url => { await Exercise(url, check); await PhoneHostFixture.Run(url, directory, check); await PhoneLanFixture.Replays(url, check); });
        await OtherServers(check);
        await PhoneLanFixture.Run(check);
        await PhoneLanFixture.Concurrent(check);
        await PhoneLanFixture.SenderGuard(check);
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
        // Link reports (PHONE-REMOTE.md, "Measuring the link"), with pings and windows quicker than the app's.
        var connects = new ConcurrentQueue<PhoneConnectReport>();
        var windows = new ConcurrentQueue<PhoneLinkWindow>();
        var (pingInterval, windowLength) = (PhoneLinkReports.PingInterval, PhoneLinkReports.Window);
        PhoneLinkReports.PingInterval = TimeSpan.FromMilliseconds(250);
        PhoneLinkReports.Window = TimeSpan.FromSeconds(2);
        PhoneLinkReports.Connecting = () => new ConcurrentQueue<string>();
        PhoneLinkReports.ConnectStage = (trace, stage) => ((ConcurrentQueue<string>)trace).Enqueue(stage);
        PhoneLinkReports.Connects = connects.Enqueue;
        PhoneLinkReports.WindowStarting = () => new object();
        PhoneLinkReports.Windows = windows.Enqueue;
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

        // A code that isn't the live one (a newer code replaced it, or a mistyped one): the phone hears so, rather than
        // nothing, and the live code still works.
        await using (var replaced = await FakePhone.Connect(url, pcId, "pair", PhoneRemoteProtocol.NewHex(16)))
            check(await replaced.Rejection() == "pairing-expired" && host.PairingOpen, "a pairing code that isn't the live one is refused with a reason, and the live one stays open");

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
            // Its hello didn't say it answers pings, as BlackPearl Mobile's before them didn't: the PC never pings it.
            await Task.Delay(800);
            check(pairing.PcPings == 0, "the PC doesn't ping a phone whose hello doesn't say it answers");
        }

        await using (var reuse = await FakePhone.Connect(url, pcId, "pair", code))
            check(await reuse.Rejection() == "pairing-expired", "a used pairing code is refused with a reason");

        await using (var forged = await FakePhone.Connect(url, pcId, phoneId, PhoneRemoteProtocol.NewHex(32)))
            check(!await forged.Opened(3000) && await forged.Rejection(500) is null, "a phone that signs with the wrong secret gets no answer");

        await using (var phone = await FakePhone.Connect(url, pcId, phoneId, secret))
        {
            check(await phone.Opened(), "a paired phone connects with its own secret");
            phone.Send(new { type = "hello", protocol = 1, app = "test", device = "Test phone", features = new[] { "ping" } });
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
            check(welcomed.GetProperty("features").EnumerateArray().Select(f => f.GetString()).SequenceEqual(["library", "watch", "ping"]), "the PC's welcome lists the library and watch features, and that it answers pings");
            phone.Send(new { type = "browse", request = 7, home = true });
            using (var waitRequest = new CancellationTokenSource(5000))
            {
                var browse = await requests.Reader.ReadAsync(waitRequest.Token);
                check(browse.Type == "browse" && browse.Message.GetProperty("home").GetBoolean() && await phone.Next("page") is { } page && page.GetProperty("request").GetInt64() == 7,
                    "a phone's library request reaches the PC with its fields, and the answer reaches the phone");
            }
            phone.Send(new { type = "pair", name = "again" });
            check(await phone.Next("error") is { } refused && refused.GetProperty("code").GetString() == "pairing" && store.Saved.Count == 1, "a paired phone can't pair again on its link");
            // A phone checks its link with pings (PHONE-REMOTE.md, "Checking the link").
            phone.Send(new { type = "ping", number = 12 });
            check(await phone.Next("pong") is { } pong && pong.GetProperty("number").GetInt64() == 12, "the PC answers a phone's ping with its number");
            // The PC times the link as well (PHONE-REMOTE.md, "Measuring the link"): how it was set up, then its round trips and reads.
            check(connects.Any(c => c is { Outcome: "greeted", Pairing: true, PhoneApp: "test" }) &&
                connects.Any(c => c is { Outcome: "greeted", Pairing: false, Via: "server", Path: "direct", PhoneApp: "test" } && c.Trace is ConcurrentQueue<string> stages && stages.Contains("connected") && stages.Contains("channel")),
                "the PC reports how each phone's link was set up: its stages, its path, and the phone app's version");
            check(await Until(() => phone.PcPings > 0 && windows.Any(w => w is { Pongs: > 0, RttMedian: > 0, Path: "direct", Via: "server", End: "open" } && w.RttP90 >= w.RttMedian && w.RttMax >= w.RttP90), 8000),
                "the PC pings a phone whose hello says it answers, and reports the round trips in stretches while the link stays open");
            check(await Until(() => windows.Sum(w => w.Reads) >= 3 && windows.Sum(w => w.MediaBytes) == 262144 + 10 && windows.Sum(w => w.Busy) >= 1 && windows.Any(w => w.ReadWaitMedian >= 0), 8000),
                $"the PC's link reports count the reads it answered, their bytes, and the ones it was too busy for ({string.Join("; ", windows.Select(w => $"{w.Reads} reads {w.MediaBytes} bytes {w.Busy} busy"))})");
            check(windows.All(w => w.Measurements.Count <= PhoneLinkWindow.MeasurementLimit), "a link report sets no more measurements than Sentry keeps");
            // Windows of 2 seconds here, so a dozen have passed: those with only pings go once an hour.
            await Task.Delay(3000);
            check(windows.Count(w => w is { End: "open", Reads: 0 }) <= 1, $"stretches with only pings are reported once an hour ({windows.Count(w => w is { End: "open", Reads: 0 })} sent)");
            // The pairing link closing counted too; this counts only the paired phone's.
            Interlocked.Exchange(ref disconnected, 0);
            host.Forget(phoneId);
            check(await phone.Closed() && await Until(() => disconnected == 1, 5000), "removing a phone closes its link and tells the PC it went");
            check(await Until(() => windows.Any(w => w.End == "removed"), 3000), "the link's last stretch is reported with why it closed");
        }
        check(connects.Count(c => c.Outcome == "greeted") == 2 && connects.Any(c => c.Outcome == "unreadable-offer"),
            "only the links that reached a phone's hello report as greeted, and one that failed says why");
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

        // Everything above, as the diagnostics log tells it: enough to trace a phone that couldn't connect, and no keys.
        await PlaybackDiagnostics.FlushAsync();
        string text;
        using (var stream = new FileStream(Path.Combine(PlaybackDiagnostics.DirectoryPath!, "diagnostics.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream)) text = await reader.ReadToEndAsync();
        var records = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line).RootElement)
            .Where(r => r.GetProperty("kind").GetString() is { } kind && (kind.StartsWith("remote-", StringComparison.Ordinal) || kind.StartsWith("phone-", StringComparison.Ordinal))).ToArray();
        string Field(JsonElement record, string name) => record.GetProperty("data").TryGetProperty(name, out var value) ? value.ToString() : "";
        bool Has(string kind, Func<JsonElement, bool> match) => records.Any(r => r.GetProperty("kind").GetString() == kind && match(r));
        check(Has("remote-start", r => Field(r, "pc") == pcId[..8]) && Has("remote-server", r => Field(r, "state") == "ready") && Has("remote-server", r => Field(r, "state") == "replaced"),
            "the log records the phone remote starting, reaching its room server, and being replaced by a copy of the library");
        check(Has("remote-signal-refused", r => Field(r, "reason") == "unknown-phone") && Has("remote-signal-refused", r => Field(r, "reason") == "pairing-expired") &&
            Has("remote-signal-refused", r => Field(r, "reason") == "bad-signature" && Field(r, "phone") == phoneId[..8]),
            "the log records signals the PC turned away, with why: an unknown phone, an expired code, a wrong secret");
        var greeted = records.Where(r => r.GetProperty("kind").GetString() == "remote-session-greeted" && Field(r, "phone") == phoneId[..8]).Select(r => Field(r, "session")).ToArray();
        check(greeted.Length == 1 && Has("remote-session-opened", r => Field(r, "session") == greeted[0]) &&
            Has("remote-session-state", r => Field(r, "session") == greeted[0] && Field(r, "state") == "connected" && Field(r, "path") == "direct" && Field(r, "local").Contains("host:", StringComparison.Ordinal)) &&
            Has("remote-session-closed", r => Field(r, "session") == greeted[0] && Field(r, "reason") == "removed" && Field(r, "greeted") == "True"),
            "the log follows a phone's session from offer to close: connected directly, greeted, and closed because the phone was removed");
        check(Has("remote-session-paired", _ => true) && Has("remote-session-refused", r => Field(r, "reason") == "already-paired"), "the log records pairing, and a paired phone's refused second pairing");        check(Has("phone-read-failed", r => Field(r, "resource") == "file" && Field(r, "error") == "UnauthorizedAccessException"), "a read the PC can't answer is logged with the kind of resource asked for");
        check(!new[] { hostKey, secret, code, phoneId, pcId }.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase)), "the phone remote's log holds no keys, secrets, codes or whole ids");
        PhoneLinkReports.PingInterval = pingInterval; PhoneLinkReports.Window = windowLength;
        PhoneLinkReports.Connecting = null; PhoneLinkReports.ConnectStage = null; PhoneLinkReports.Connects = null; PhoneLinkReports.WindowStarting = null; PhoneLinkReports.Windows = null;
    }

    // The numbers a link report is made of (PhoneLinkStats), with made-up times.
    static void LinkStats(Action<bool, string> check)
    {
        long Seconds(double s) => (long)(s * System.Diagnostics.Stopwatch.Frequency);
        var stats = new PhoneLinkStats();
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var first = stats.PingSent(start);
        var second = stats.PingSent(start + Seconds(1));
        var third = stats.PingSent(start + Seconds(2));
        check(stats.Pong(first, start + Seconds(0.040)) && stats.Pong(second, start + Seconds(1.020)) && !stats.Pong(second, start + Seconds(1.5)) && !stats.Pong(99, start + Seconds(2)),
            "a pong times the ping it answers, once; one for a ping never sent counts for nothing");
        check(!stats.Pong(third, start + Seconds(13)), "an answer that comes after the wait counts for nothing");
        stats.Served(262144, TimeSpan.FromMilliseconds(30));
        stats.Served(10, TimeSpan.FromMilliseconds(5));
        stats.Busy();
        var window = stats.Take(start + Seconds(14), "relay", "server", "0.13.0", "open", TimeSpan.FromMinutes(3), null);
        check(window is { Pings: 3, Pongs: 2, Unanswered: 1, RttMedian: 20.0, RttMax: 40.0, MediaBytes: 262154, Reads: 2, Busy: 1, ReadWaitMedian: 5.0, ReadWaitP90: 30.0 } &&
            Math.Abs(window.Duration.TotalSeconds - 14) < 0.01,
            $"a link window has its round trips, unanswered pings, reads and bytes ({window})");
        check(window.Measurements.Count <= PhoneLinkWindow.MeasurementLimit && window.Measurements.Select(m => m.Name).Distinct().Count() == window.Measurements.Count,
            "a link window sets no more measurements than Sentry keeps");
        var next = stats.Take(start + Seconds(20), "relay", "server", null, "open", TimeSpan.Zero, null);
        check(next is { Pings: 0, Pongs: 0, Reads: 0, RttMedian: null } && !next.Measurements.Any(m => m.Name.StartsWith("rtt")) && !stats.Measured,
            "the next window starts empty, and leaves out round trips nothing measured");
        // Pings a locked phone leaves unanswered alone don't make a window worth sending.
        stats.PingSent(start + Seconds(21));
        stats.Take(start + Seconds(40), "relay", "server", null, "open", TimeSpan.Zero, null);
        check(!stats.Measured, "a window with only unanswered pings isn't reported");
        check(PhoneLinkStats.Percentile([1, 2, 3, 4], 0.5) == 2 && PhoneLinkStats.Percentile([.. Enumerable.Range(1, 10).Select(i => (double)i)], 0.9) == 9 && PhoneLinkStats.Percentile([], 0.5) is null,
            "link percentiles take the nearest rank");
        check(PhoneRemoteDiagnostics.AppVersion("0.13.0+31") == "0.13.0+31" && PhoneRemoteDiagnostics.AppVersion("C:\\Users\\Ann") == "other" && PhoneRemoteDiagnostics.AppVersion(null) is null,
            "a link report names the phone app's version, and nothing that isn't one");
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
