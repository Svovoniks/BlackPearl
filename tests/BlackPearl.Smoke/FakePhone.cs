using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using BlackPearl.Core;
using DataChannelDotnet;
using DataChannelDotnet.Data;
using DataChannelDotnet.Impl;

// A stand-in for BlackPearl Mobile, written against PHONE-REMOTE.md with the same WebRTC library as the PC. The smoke
// fixtures and the desktop checks (PhoneLibraryChecks) both use it.
// The phone's connection to the room server.
sealed class PhoneServerSocket : IAsyncDisposable
{
    readonly ClientWebSocket socket = new();
    readonly SemaphoreSlim sending = new(1, 1);
    public readonly Channel<WireMessage> Received = Channel.CreateUnbounded<WireMessage>();
    readonly CancellationTokenSource lifetime = new();
    public static async Task<PhoneServerSocket> Open(string url, string pcId)
    {
        var result = new PhoneServerSocket();
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
    PhoneServerSocket server = null!;
    RtcPeerConnection peer = null!;
    IRtcDataChannel channel = null!, media = null!;
    readonly TaskCompletionSource mediaOpen = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly ConcurrentDictionary<uint, Pending> reads = new();
    uint nextRead;
    public Task<JsonElement> Welcome => welcome.Task;
    readonly TaskCompletionSource<JsonElement> welcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
    sealed class Pending
    {
        public readonly TaskCompletionSource<(byte[] Bytes, long Length, string? Error)> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[]? Bytes; public int Received; public long Length;
    }
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
        result.server = await PhoneServerSocket.Open(url, pcId);
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
        result.channel.OnTextReceivedSafe += (_, text) =>
        {
            var message = JsonDocument.Parse(text.Text).RootElement.Clone();
            if (message.GetProperty("type").GetString() == "welcome") result.welcome.TrySetResult(message);
            result.messages.Writer.TryWrite(message);
        };
        result.media = result.peer.CreateDataChannel(new RtcCreateDataChannelArgs { Label = PhoneRemoteProtocol.MediaLabel });
        result.media.OnOpen += _ => result.mediaOpen.TrySetResult();
        result.media.OnBinaryReceivedSafe += (_, frame) => result.Frame(frame.Data.ToArray());
        result.media.OnTextReceivedSafe += (_, text) =>
        {
            using var json = JsonDocument.Parse(text.Text);
            if (result.reads.TryRemove(json.RootElement.GetProperty("id").GetUInt32(), out var failed)) failed.Done.TrySetResult(([], 0, json.RootElement.GetProperty("code").GetString()));
        };
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
    public void SendText(string text) => channel.Send(text);
    public async Task<bool> MediaOpened(int milliseconds = 10000) => await Task.WhenAny(mediaOpen.Task, Task.Delay(milliseconds)) == mediaOpen.Task;
    // A frame: id, offset in the reply, reply length, reserved, the resource's length (PHONE-REMOTE.md), then the bytes.
    void Frame(byte[] frame)
    {
        var id = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(frame);
        if (!reads.TryGetValue(id, out var pending)) return;
        var offset = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(4));
        var total = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(8));
        pending.Length = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(frame.AsSpan(16));
        pending.Bytes ??= new byte[total];
        frame.AsSpan(24).CopyTo(pending.Bytes.AsSpan(offset));
        pending.Received += frame.Length - 24;
        if (pending.Received >= total && reads.TryRemove(id, out _)) pending.Done.TrySetResult((pending.Bytes, pending.Length, null));
    }
    public async Task<(byte[] Bytes, long Length, string? Error)> Read(string resource, long offset, int count)
    {
        var id = Interlocked.Increment(ref nextRead);
        var pending = reads[id] = new Pending();
        media.Send(Wire.Serialize(new { type = "read", id, resource, offset, count }));
        return await pending.Done.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }
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
        try { media?.Dispose(); } catch { }
        try { peer?.Dispose(); } catch { }
        await server.DisposeAsync();
    }
}
