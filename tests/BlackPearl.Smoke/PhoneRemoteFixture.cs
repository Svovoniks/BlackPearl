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
        await HostlessRoomFixture.WithServer(sdk, serverDll, directory, "phone-remote", url => Exercise(url, check));
    }

    static async Task Exercise(string url, Action<bool, string> check)
    {
        using var http = new HttpClient();
        check((await http.GetStringAsync(url + "/health")).Contains("\"remote\":1"), "the room service reports the phone remote protocol");

        var hostKey = PersistentRooms.NewKey();
        var pcId = PhoneRemoteProtocol.PcId(hostKey);
        // A phone that opens the app before the PC is online waits, and hears when it arrives.
        await using var early = await ServerSocket.Open(url, pcId);
        check(await early.Next("remote-welcome") is not null && await early.Next("remote-offline") is not null, "a phone hears that its PC is offline");

        var store = new MemoryStore();
        using var host = new PhoneRemoteHost(new PhoneRemoteOptions { Server = url, HostKey = hostKey, PcName = "Test PC", AppVersion = "0.0.0-test", Store = store });
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
            phone.Send(new { type = "pair", name = "again" });
            check(await phone.Next("error") is { } refused && refused.GetProperty("code").GetString() == "pairing" && store.Saved.Count == 1, "a paired phone can't pair again on its link");
            host.Forget(phoneId);
            check(await phone.Closed(), "removing a phone closes its link");
        }
        await using (var removed = await FakePhone.Connect(url, pcId, phoneId, secret))
            check(await removed.Rejection() == "unknown-phone", "a removed phone is told it was removed");
        await using (var newer = await FakePhone.Connect(url, pcId, "pair", "00000000000000000000000000000000"))
            check(await newer.Rejection() == "pairing-expired", "a phone without a live pairing code is turned away");
    }

    // Drives a running BlackPearl as a paired phone would, for trying the desktop app by hand: each step is a command
    // name with an optional value ("toggle", "seek-by=-10000", "volume=30", "wait=2000"), and every state the PC sends
    // is printed.
    public static async Task Try(string url, string pcId, string phoneId, string secret, string[] steps)
    {
        await using var phone = await FakePhone.Connect(url, pcId, phoneId, secret);
        if (await phone.Rejection(3000) is { } reason) throw new Exception("Refused: " + reason);
        if (!await phone.Opened()) throw new Exception("No link to the PC");
        phone.Send(new { type = "hello", protocol = 1, app = "smoke", device = "Smoke phone" });
        Console.WriteLine("welcome: " + await phone.Next("welcome"));
        _ = Task.Run(async () => { while (await phone.Next("", 600000) is { } message) Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}"); });
        await Task.Delay(1500);
        foreach (var step in steps)
        {
            var parts = step.Split('=', 2);
            if (parts[0] == "wait") { await Task.Delay(int.Parse(parts[1])); continue; }
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

    // The phone's connection to the room server.
    sealed class ServerSocket : IAsyncDisposable
    {
        readonly ClientWebSocket socket = new();
        readonly SemaphoreSlim sending = new(1, 1);
        public readonly Channel<WireMessage> Received = Channel.CreateUnbounded<WireMessage>();
        readonly CancellationTokenSource lifetime = new();
        public static async Task<ServerSocket> Open(string url, string pcId)
        {
            var result = new ServerSocket();
            await result.socket.ConnectAsync(PhoneRemoteProtocol.ServerSocket(url), CancellationToken.None);
            await result.Send(new("remote-phone", Target: pcId, Number: 1));
            _ = Task.Run(async () =>
            {
                try { while (await SocketMessages.ReadAsync(result.socket, result.lifetime.Token) is { } message) result.Received.Writer.TryWrite(message); }
                catch (Exception) { }
                result.Received.Writer.TryComplete();
            });
            return result;
        }
        public async Task Send(WireMessage message)
        {
            await sending.WaitAsync();
            try { await SocketMessages.SendAsync(socket, message, CancellationToken.None); } finally { sending.Release(); }
        }
        public async Task<WireMessage?> Next(string type, int milliseconds = 5000)
        {
            using var wait = new CancellationTokenSource(milliseconds);
            try { while (true) { var message = await Received.Reader.ReadAsync(wait.Token); if (message.Type == type) return message; } }
            catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException) { return null; }
        }
        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel();
            try { using var closing = new CancellationTokenSource(1000); await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, closing.Token); } catch { }
            socket.Dispose();
        }
    }

    // A phone: offers a remote-v1 data channel and signs its signals as PHONE-REMOTE.md describes.
    sealed class FakePhone : IAsyncDisposable
    {
        ServerSocket server = null!;
        RtcPeerConnection peer = null!;
        IRtcDataChannel channel = null!;
        string phone = "", key = "", session = PhoneRemoteProtocol.NewHex(16);
        readonly TaskCompletionSource open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource<string> rejected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly Channel<JsonElement> messages = Channel.CreateUnbounded<JsonElement>();
        readonly ConcurrentQueue<RtcCandidate> early = new();
        bool answered;

        public static async Task<FakePhone> Connect(string url, string pcId, string phone, string key)
        {
            var result = new FakePhone { phone = phone, key = key };
            result.server = await ServerSocket.Open(url, pcId);
            if (await result.server.Next("remote-welcome") is not { } welcome) throw new IOException("No welcome from the room server");
            var servers = Wire.Read<RemoteWelcome>(welcome.Data!).IceServers;
            result.peer = new RtcPeerConnection(new RtcPeerConfiguration { IceServers = servers, MaxMessageSize = 65536, Mtu = 1200 });
            result.peer.OnLocalDescriptionSafe += (_, d) => result.Signal("sdp", Wire.Serialize(new RemoteSdp(d.Type == RtcDescriptionType.Offer ? "offer" : "answer", d.Sdp)));
            result.peer.OnCandidateSafe += (_, c) => result.Signal("ice", Wire.Serialize(new RemoteIce(c.Content.StartsWith("a=") ? c.Content[2..] : c.Content, c.Mid)));
            result.peer.OnConnectionStateChange += (_, s) => { if (s is DataChannelDotnet.Bindings.rtcState.RTC_CLOSED or DataChannelDotnet.Bindings.rtcState.RTC_FAILED or DataChannelDotnet.Bindings.rtcState.RTC_DISCONNECTED) result.closed.TrySetResult(); };
            _ = Task.Run(result.ReadSignals);
            result.channel = result.peer.CreateDataChannel(new RtcCreateDataChannelArgs { Label = PhoneRemoteProtocol.Label });
            result.channel.OnOpen += _ => result.open.TrySetResult();
            result.channel.OnClose += _ => result.closed.TrySetResult();
            result.channel.OnTextReceivedSafe += (_, text) => result.messages.Writer.TryWrite(JsonDocument.Parse(text.Text).RootElement.Clone());
            return result;
        }

        void Signal(string kind, string payload) =>
            _ = server.Send(new("signal", Data: Wire.Serialize(new RemoteSignal(phone, session, kind, payload, PhoneRemoteProtocol.Sign(key, "phone", phone, session, kind, payload)))));

        async Task ReadSignals()
        {
            await foreach (var message in server.Received.Reader.ReadAllAsync())
            {
                if (message.Type != "signal" || message.Data is null) continue;
                var signal = Wire.Read<RemoteSignal>(message.Data);
                if (signal.Session != session) continue;
                if (signal.Kind == "rejected") { rejected.TrySetResult(signal.Payload); continue; }
                if (!PhoneRemoteProtocol.Verify(key, "pc", signal)) continue;
                if (signal.Kind == "sdp")
                {
                    var sdp = Wire.Read<RemoteSdp>(signal.Payload);
                    peer.SetRemoteDescription(new RtcDescription { Type = RtcDescriptionType.Answer, Sdp = sdp.Sdp });
                    answered = true;
                    while (early.TryDequeue(out var candidate)) peer.AddRemoteCandidate(candidate);
                }
                else if (signal.Kind == "ice")
                {
                    var ice = Wire.Read<RemoteIce>(signal.Payload);
                    var candidate = new RtcCandidate { Content = "a=" + ice.Candidate, Mid = ice.Mid ?? "0" };
                    if (answered) peer.AddRemoteCandidate(candidate); else early.Enqueue(candidate);
                }
            }
        }

        public void Send(object message) => channel.Send(Wire.Serialize(message));
        public async Task<bool> Opened(int milliseconds = 15000) => await Task.WhenAny(open.Task, Task.Delay(milliseconds)) == open.Task;
        public async Task<bool> Closed(int milliseconds = 10000) => await Task.WhenAny(closed.Task, Task.Delay(milliseconds)) == closed.Task;
        public async Task<string?> Rejection(int milliseconds = 5000) => await Task.WhenAny(rejected.Task, Task.Delay(milliseconds)) == rejected.Task ? rejected.Task.Result : null;
        public async Task<JsonElement?> Next(string type, int milliseconds = 5000)
        {
            using var wait = new CancellationTokenSource(milliseconds);
            try { while (true) { var message = await messages.Reader.ReadAsync(wait.Token); if (type.Length == 0 || message.GetProperty("type").GetString() == type) return message; } }
            catch (OperationCanceledException) { return null; }
        }
        public async ValueTask DisposeAsync()
        {
            try { channel?.Dispose(); } catch { }
            try { peer?.Dispose(); } catch { }
            await server.DisposeAsync();
        }
    }
}
