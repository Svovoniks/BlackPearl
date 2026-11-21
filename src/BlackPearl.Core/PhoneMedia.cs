using System.Buffers.Binary;
using System.Text.Json;
using System.Threading.Channels;
using DataChannelDotnet;

namespace BlackPearl.Core;

// Something a phone can read over its media-v1 channel (PHONE-REMOTE.md): a video file, a piece of a converted stream,
// or a picture. Length is the whole resource's size in bytes.
public sealed record PhoneResource(long Length, Func<long, int, CancellationToken, Task<byte[]>> Read)
{
    public static PhoneResource FromBytes(byte[] bytes) => new(bytes.Length, (offset, count, _) =>
        Task.FromResult(bytes.AsSpan((int)Math.Min(offset, bytes.Length), (int)Math.Clamp(bytes.Length - offset, 0, count)).ToArray()));
}

// The resource is not ready yet, as a stream piece still being converted: the phone asks again.
public sealed class PhoneResourceBusyException(string message) : IOException(message);

public delegate Task<PhoneResource?> PhoneResourceResolver(PhoneRemoteSession session, string resource, CancellationToken ct);

// What a phone's progress report saves (PHONE-REMOTE.md, "Watching on the phone"), the way the PC saves its own playback:
// the resume point (0 at the end, and for a title already watched), whether it counts as played, and whether it is now
// watched.
public readonly record struct PhoneProgress(long ResumeMs, bool Played, bool Ended)
{
    public static PhoneProgress From(long positionMs, long durationMs, bool ended, bool watched)
    {
        ended |= durationMs > 0 && positionMs >= durationMs - 2000;
        return new(ended || watched ? 0 : Math.Max(0, positionMs), positionMs >= 10000 || ended, ended);
    }
}

// Serves one phone's reads on its media-v1 channel. Reads are answered in the order they were asked, each as binary
// frames; a few are read ahead so the disk overlaps with sending.
internal sealed class PhoneMediaServer : IDisposable
{
    public const int MaxRead = 262144, FrameBytes = 16384, HeaderBytes = 24, MaxQueued = 64;
    private readonly PhoneRemoteSession session;
    private readonly IRtcDataChannel channel;
    private readonly PhoneResourceResolver? resolve;
    private readonly Channel<Request> requests = Channel.CreateBounded<Request>(new BoundedChannelOptions(MaxQueued) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Dictionary<uint, CancellationTokenSource> active = [];
    private readonly CancellationTokenSource lifetime = new();
    private int disposed;

    private sealed record Request(uint Id, string Resource, long Offset, int Count, CancellationTokenSource Cancellation);

    public PhoneMediaServer(PhoneRemoteSession session, IRtcDataChannel channel, PhoneResourceResolver? resolve)
    {
        this.session = session; this.channel = channel; this.resolve = resolve;
        channel.OnTextReceivedSafe += (_, text) => Receive(text.Text);
        _ = Task.Run(ServeAsync);
    }

    private void Receive(string text)
    {
        if (disposed != 0 || text.Length > 4096) return;
        try
        {
            using var json = JsonDocument.Parse(text);
            var message = json.RootElement;
            if (!message.TryGetProperty("id", out var idValue) || !idValue.TryGetUInt32(out var id)) return;
            var type = message.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (type == "cancel")
            {
                lock (active) if (active.Remove(id, out var canceled)) try { canceled.Cancel(); } catch (ObjectDisposedException) { }
                return;
            }
            if (type != "read") return;
            var resource = message.TryGetProperty("resource", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            var offset = message.TryGetProperty("offset", out var o) && o.TryGetInt64(out var at) ? at : -1;
            var count = message.TryGetProperty("count", out var c) && c.TryGetInt32(out var n) ? n : 0;
            if (resource is not { Length: > 0 and <= 512 } || offset < 0 || count is < 1 or > MaxRead) { Fail(id, "invalid", "That request isn't valid."); return; }
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            lock (active)
            {
                if (!active.TryAdd(id, cancellation)) { cancellation.Dispose(); return; }
            }
            if (!requests.Writer.TryWrite(new(id, resource, offset, count, cancellation)))
            {
                lock (active) active.Remove(id);
                cancellation.Dispose();
                Fail(id, "busy", "The PC is busy. Ask again.");
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }
    }

    private async Task<(PhoneResource Resource, byte[] Bytes)?> Read(Request request)
    {
        var resource = resolve is null ? null : await resolve(session, request.Resource, request.Cancellation.Token);
        if (resource is null) throw new UnauthorizedAccessException();
        if (request.Offset >= resource.Length) return (resource, []);
        var bytes = await resource.Read(request.Offset, (int)Math.Min(request.Count, resource.Length - request.Offset), request.Cancellation.Token);
        return (resource, bytes);
    }

    private async Task ServeAsync()
    {
        var reading = new Queue<(Request Request, Task<(PhoneResource Resource, byte[] Bytes)?> Read)>();
        var frame = new byte[HeaderBytes + FrameBytes];
        try
        {
            while (true)
            {
                if (reading.Count == 0 && !await requests.Reader.WaitToReadAsync(lifetime.Token)) break;
                while (reading.Count < 4 && requests.Reader.TryRead(out var queued)) reading.Enqueue((queued, Read(queued)));
                if (reading.Count == 0) continue;
                var (request, read) = reading.Dequeue();
                try
                {
                    if (await read is not { } result || request.Cancellation.IsCancellationRequested) continue;
                    var bytes = result.Bytes;
                    BinaryPrimitives.WriteUInt32LittleEndian(frame, request.Id);
                    BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(8), bytes.Length);
                    BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(12), 0);
                    BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(16), result.Resource.Length);
                    // An empty reply (a read past the end) is one frame with no data.
                    var offset = 0;
                    do
                    {
                        var count = Math.Min(FrameBytes, bytes.Length - offset);
                        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), offset);
                        bytes.AsSpan(offset, count).CopyTo(frame.AsSpan(HeaderBytes));
                        channel.Send(frame.AsSpan(0, HeaderBytes + count));
                        offset += count;
                    } while (offset < bytes.Length && !request.Cancellation.IsCancellationRequested);
                }
                catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested && !lifetime.IsCancellationRequested) { }
                catch (PhoneResourceBusyException ex) when (!lifetime.IsCancellationRequested) { Fail(request.Id, "busy", ex.Message); }
                catch (Exception ex) when (!lifetime.IsCancellationRequested && ex.GetType().Namespace?.StartsWith("DataChannelDotnet") != true)
                {
                    PlaybackDiagnostics.Record("phone-read-failed", new { error = ex.GetType().Name });
                    Fail(request.Id, ex is UnauthorizedAccessException ? "gone" : "failed", ex is UnauthorizedAccessException
                        ? "That video is no longer in the library on the PC." : "The PC couldn't read that video.");
                }
                finally
                {
                    lock (active) if (active.TryGetValue(request.Id, out var current) && current == request.Cancellation) active.Remove(request.Id);
                    request.Cancellation.Dispose();
                }
            }
        }
        // The channel closed while a reply was sending.
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException || lifetime.IsCancellationRequested || ex.GetType().Namespace?.StartsWith("DataChannelDotnet") == true) { }
        finally
        {
            foreach (var (request, read) in reading) _ = read.ContinueWith(t => { _ = t.Exception; request.Cancellation.Dispose(); }, TaskScheduler.Default);
        }
    }

    private void Fail(uint id, string code, string message)
    {
        try { if (channel.IsOpen) channel.Send(Wire.Serialize(new { type = "read-error", id, code, message })); }
        catch (Exception ex) when (ex.GetType().Namespace?.StartsWith("DataChannelDotnet") == true || ex is ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        requests.Writer.TryComplete();
    }
}
