using System.Buffers.Binary;
using System.Reflection;
using System.Threading.Channels;
using DataChannelDotnet;
using DataChannelDotnet.Data;
using DataChannelDotnet.Impl;
using BlackPearl.Core;

static class PeerMediaFixture
{
    public static async Task Run(Action<bool, string> check)
    {
        // A minimal pre-upgrade peer validates both directions of compatibility.
        foreach (var newHost in new[] { true, false })
        {
            using var modern = new PeerTransport([]);
            using var legacy = new RtcPeerConnection(new RtcPeerConfiguration { MaxMessageSize = 65536 });
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var signals = Channel.CreateUnbounded<Action>();
            modern.Signal += (type, data) => signals.Writer.TryWrite(() => {
                if (type == "sdp") legacy.SetRemoteDescription(Wire.Read<RtcDescription>(data));
                else legacy.AddRemoteCandidate(Wire.Read<RtcCandidate>(data));
            });
            legacy.OnLocalDescriptionSafe += (_, description) => signals.Writer.TryWrite(() => modern.ReceiveSignal("sdp", Wire.Serialize(description)));
            legacy.OnCandidateSafe += (_, candidate) => signals.Writer.TryWrite(() => modern.ReceiveSignal("ice", Wire.Serialize(candidate)));
            var pump = Task.Run(async () => { try { await foreach (var action in signals.Reader.ReadAllAsync(cancellation.Token)) action(); } catch (OperationCanceledException) { } });
            IRtcDataChannel? mediaChannel = null;
            var mediaOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reply = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var source = new BytesSource(); modern.ResolveMedia = id => id == source.Media.Id ? source : null;
            void Attach(IRtcDataChannel channel)
            {
                if (channel.Label != "media") return;
                mediaChannel = channel;
                channel.OnOpen += _ => mediaOpen.TrySetResult(); if (channel.IsOpen) mediaOpen.TrySetResult();
                channel.OnTextReceivedSafe += (_, text) => {
                    var message = Wire.Read<WireMessage>(text.Text);
                    if (message.Type == "bytes") reply.TrySetResult(Wire.Read<Reply>(message.Data!).Bytes);
                    if (message.Type != "read") return;
                    var request = Wire.Read<Request>(message.Data!);
                    // Old hosts return JSON even when the guest offers binary.
                    channel.Send(Wire.Serialize(new WireMessage("bytes", Data: Wire.Serialize(new Reply(request.Id, source.Bytes(request.Offset, request.Count))))));
                };
            }
            legacy.OnDataChannel += (_, channel) => Attach(channel);
            if (newHost) modern.StartOffer();
            else { legacy.CreateDataChannel(new RtcCreateDataChannelArgs { Label = "watchroom-control-v2" }); Attach(legacy.CreateDataChannel(new RtcCreateDataChannelArgs { Label = "media" })); }
            await Task.WhenAll(modern.Ready, mediaOpen.Task).WaitAsync(cancellation.Token);
            byte[] bytes;
            if (newHost) {
                mediaChannel!.Send(Wire.Serialize(new WireMessage("read", Data: Wire.Serialize(new Request(Guid.NewGuid().ToString("N"), source.Media.Id, 123, 32768)))));
                bytes = await reply.Task.WaitAsync(cancellation.Token);
            } else bytes = await modern.ReadAsync(source.Media.Id, 123, 32768, cancellation.Token);
            check(bytes.SequenceEqual(source.Bytes(123, 32768)), newHost ? "new host preserves legacy JSON media replies" : "new guest accepts legacy JSON media replies");
            check(!modern.UsesIsolatedMedia, "legacy peers do not require the optional isolated connection");
            cancellation.Cancel(); await pump;
        }
        await SendWhileDisposed(check);
    }
    // A send can pass the open check just before another thread disposes the transport, as when the room drops a member
    // while the app is sending them library pages. The channel then throws ObjectDisposedException, which callers such as
    // the desktop's queue sharing don't expect; they handle a lost connection as IOException. The channel here is
    // disposed right after its open check, so the race is hit every time.
    private static async Task SendWhileDisposed(Action<bool, string> check)
    {
        using var offerer = new PeerTransport([]); using var answerer = new PeerTransport([]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var signals = Channel.CreateUnbounded<Action>();
        offerer.Signal += (type, data) => signals.Writer.TryWrite(() => answerer.ReceiveSignal(type, data));
        answerer.Signal += (type, data) => signals.Writer.TryWrite(() => offerer.ReceiveSignal(type, data));
        var pump = Task.Run(async () => { try { await foreach (var action in signals.Reader.ReadAllAsync(cancellation.Token)) action(); } catch (OperationCanceledException) { } });
        offerer.StartOffer();
        await Task.WhenAll(offerer.ControlReady, answerer.ControlReady).WaitAsync(cancellation.Token);
        var field = typeof(PeerTransport).GetField("controls", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var controls = (IRtcDataChannel)field.GetValue(offerer)!;
        field.SetValue(offerer, DisposedAfterOpenCheck.Wrap(controls));
        Exception? error = null;
        try { offerer.SendControl(new("playback")); } catch (Exception ex) { error = ex; }
        check(error is IOException { InnerException: ObjectDisposedException }, $"a control send that races the transport's disposal fails as a lost connection ({error?.GetType().Name ?? "no error"})");
        field.SetValue(offerer, controls);
        cancellation.Cancel(); await pump;
    }
    // Answers the open check from the real channel, then disposes it before anything else reaches it.
    public class DisposedAfterOpenCheck : DispatchProxy
    {
        private IRtcDataChannel inner = null!;
        public static IRtcDataChannel Wrap(IRtcDataChannel inner)
        {
            var proxy = Create<IRtcDataChannel, DisposedAfterOpenCheck>();
            ((DisposedAfterOpenCheck)(object)proxy).inner = inner;
            return proxy;
        }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            object? result;
            try { result = method!.Invoke(inner, args); }
            catch (TargetInvocationException ex) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex.InnerException!); throw; }
            if (method.Name == "get_IsOpen") inner.Dispose();
            return result;
        }
    }
    private record Request(string Id, string Media, long Offset, int Count);
    private record Reply(string Id, byte[] Bytes);
    private sealed class BytesSource : IMediaSource
    {
        public SharedMedia Media { get; } = new("compatibility", "Generated bytes", 65536, ".bin");
        public byte[] Bytes(long offset, int count) => Enumerable.Range(0, count).Select(i => (byte)((offset + i) % 251)).ToArray();
        public Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct) => Task.FromResult(Bytes(offset, count));
    }
}
