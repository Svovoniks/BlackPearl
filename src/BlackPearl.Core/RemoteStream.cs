using System.Globalization;
using System.Text;

namespace BlackPearl.Core;

// A viewer's side of a RoomStream. Pieces are fetched from whoever converts the video, kept in a
// temporary file, and read ahead of playback to the end of the video, as the file itself would be.
public sealed class RemoteStream : IDisposable
{
    public delegate Task<byte[]> Reader(string resource, long offset, int count, CancellationToken ct);
    private const int RangeReads = MediaStreaming.Window / 2, MaximumPieceBytes = 256 * 1024 * 1024, MemoryPieces = 12;
    private readonly object gate = new();
    private readonly Reader read;
    private readonly SegmentStore? store;
    private readonly Dictionary<int, Pending> pending = [];
    // Pieces the file could not take, such as on a full disk: the last few stay in memory.
    private readonly Dictionary<int, byte[]> memory = [];
    private readonly Queue<int> memoryOrder = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private Task? readAhead;
    private CancellationTokenSource? ahead;
    private int anchor = -1, aheadIndex = -1, playerWaits;
    private long retryAt;
    private bool stopped;
    public RoomStream Stream { get; }
    public bool BuffersWholeVideo => store?.Accepting == true;

    public RemoteStream(RoomStream stream, Reader read, SegmentStore? store)
    { Stream = stream; this.read = read; this.store = store; }

    private sealed class Pending
    {
        public readonly CancellationTokenSource Cancellation = new();
        public Task<byte[]> Task = null!;
        public int Users;
    }

    // The whole video as a list of its pieces, which is what the player opens.
    public string Playlist(string pieceAddress)
    {
        var seconds = Stream.SegmentMs / 1000.0;
        var text = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-TARGETDURATION:")
            .Append(((int)Math.Ceiling(seconds)).ToString(CultureInfo.InvariantCulture)).Append("\n#EXT-X-MEDIA-SEQUENCE:0\n");
        for (var index = 0; index < Stream.Segments; index++)
        {
            var length = Math.Min(seconds, Stream.DurationMs / 1000.0 - index * seconds);
            text.Append("#EXTINF:").Append(Math.Max(length, 0.001).ToString("0.000", CultureInfo.InvariantCulture)).Append(",\n")
                .Append(string.Format(CultureInfo.InvariantCulture, pieceAddress, index)).Append('\n');
        }
        return text.Append("#EXT-X-ENDLIST\n").ToString();
    }

    private bool Has(int index) => store?.Has(index) == true || memory.ContainsKey(index);
    private byte[]? Kept(int index)
    {
        if (store?.Length(index) is > 0 and var length && store.Read(index, 0, length) is { } stored) return stored;
        lock (gate) return memory.GetValueOrDefault(index);
    }

    // The player asks for a piece. Read-ahead for another part of the video gives way to it.
    public async Task<byte[]> SegmentAsync(int index, CancellationToken ct)
    {
        if (index < 0 || index >= Stream.Segments) throw new ArgumentOutOfRangeException(nameof(index));
        CancellationTokenSource? superseded = null;
        lock (gate)
        {
            if (!Has(index) && aheadIndex >= 0 && aheadIndex != index) { superseded = ahead; ahead = null; aheadIndex = -1; }
            anchor = index; playerWaits++;
        }
        superseded?.Cancel();
        try { return await Piece(index, ct); }
        finally
        {
            lock (gate) playerWaits--;
            Wake();
        }
    }

    // Starts reading ahead: from the piece the player last took to the end of the video, one piece at a time.
    public void StartReadAhead()
    {
        lock (gate) { if (stopped) return; readAhead ??= Task.Run(ReadAhead); }
        Wake();
    }

    public long BufferedAheadBytes
    {
        get
        {
            lock (gate)
            {
                long bytes = 0;
                for (var index = Math.Max(anchor, 0); index < Stream.Segments && Has(index); index++)
                    bytes += store?.Length(index) is > 0 and var length ? length : memory.GetValueOrDefault(index)?.Length ?? 0;
                return bytes;
            }
        }
    }
    public int BufferedAheadSegments
    {
        get { lock (gate) { var index = Math.Max(anchor, 0); while (index < Stream.Segments && Has(index)) index++; return index - Math.Max(anchor, 0); } }
    }

    private void Wake()
    {
        try { if (wake.CurrentCount == 0) wake.Release(); }
        catch (Exception ex) when (ex is SemaphoreFullException or ObjectDisposedException) { }
    }

    private async Task ReadAhead()
    {
        var token = lifetime.Token;
        while (!token.IsCancellationRequested)
        {
            int next = -1; CancellationTokenSource? current = null; var wait = 0L;
            lock (gate)
            {
                var now = Environment.TickCount64;
                if (now < retryAt) wait = retryAt - now;
                else if (playerWaits == 0 && anchor >= 0)
                {
                    next = anchor; while (next < Stream.Segments && Has(next)) next++;
                    // Without the file only a few pieces are kept, so only a few are read ahead.
                    if (next >= Stream.Segments || store?.Accepting != true && next > anchor + MemoryPieces - 4) next = -1;
                    else { ahead = current = CancellationTokenSource.CreateLinkedTokenSource(token); aheadIndex = next; }
                }
            }
            if (next < 0)
            {
                try { await wake.WaitAsync(wait > 0 ? (int)Math.Min(wait, 1000) : Timeout.Infinite, token); }
                catch (OperationCanceledException) { return; }
                continue;
            }
            try { await Piece(next, current!.Token); }
            catch (OperationCanceledException) when (current!.IsCancellationRequested) { }
            // Whoever converts the video being unreachable must not turn read-ahead into a retry storm.
            catch (Exception) { Interlocked.Exchange(ref retryAt, Environment.TickCount64 + 1000); }
            finally
            {
                lock (gate) { if (ahead == current) { ahead = null; aheadIndex = -1; } }
                current!.Dispose();
            }
        }
    }

    private async Task<byte[]> Piece(int index, CancellationToken ct)
    {
        if (Kept(index) is { } kept) return kept;
        Pending fetch;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(stopped, this);
            if (!pending.TryGetValue(index, out fetch!))
            {
                fetch = new Pending(); pending.Add(index, fetch);
                fetch.Task = Fetch(index, fetch);
            }
            fetch.Users++;
        }
        try { return await fetch.Task.WaitAsync(ct); }
        finally
        {
            lock (gate)
            {
                if (--fetch.Users == 0)
                {
                    if (!fetch.Task.IsCompleted)
                    {
                        if (pending.GetValueOrDefault(index) == fetch) pending.Remove(index);
                        fetch.Cancellation.Cancel();
                    }
                    _ = fetch.Task.ContinueWith(t => { _ = t.Exception; fetch.Cancellation.Dispose(); }, TaskScheduler.Default);
                }
            }
        }
    }

    private async Task<byte[]> Fetch(int index, Pending fetch)
    {
        // Yield so the fetch is registered and has a consumer before completion.
        await Task.Yield();
        var token = fetch.Cancellation.Token;
        try
        {
            var size = BitConverter.ToInt64(await read(StreamConverter.Resource(Stream.Id, index, size: true), 0, sizeof(long), token));
            if (size is <= 0 or > MaximumPieceBytes) throw new InvalidDataException("Invalid stream piece size");
            var data = new byte[size];
            var resource = StreamConverter.Resource(Stream.Id, index);
            using var window = new SemaphoreSlim(RangeReads);
            var ranges = new List<Task>();
            for (long offset = 0; offset < size; offset += MediaStreaming.ChunkBytes)
            {
                await window.WaitAsync(token);
                var at = offset; var count = (int)Math.Min(MediaStreaming.ChunkBytes, size - offset);
                ranges.Add(Task.Run(async () =>
                {
                    try
                    {
                        var bytes = await read(resource, at, count, token);
                        if (bytes.Length != count) throw new EndOfStreamException("Incomplete stream piece");
                        bytes.CopyTo(data, at);
                    }
                    finally { window.Release(); }
                }, token));
                // One failed range ends the piece rather than fetching the rest of it.
                if (ranges.FirstOrDefault(range => range.IsFaulted) is { } failed) await failed;
            }
            await Task.WhenAll(ranges);
            token.ThrowIfCancellationRequested();
            if (store?.TryAdd(index, data) != true)
                lock (gate)
                {
                    if (memory.TryAdd(index, data)) memoryOrder.Enqueue(index);
                    while (memoryOrder.Count > MemoryPieces) memory.Remove(memoryOrder.Dequeue());
                }
            return data;
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            // The ranges still on their way are for a piece nobody gets.
            try { fetch.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
            throw;
        }
        finally
        {
            lock (gate) { if (pending.GetValueOrDefault(index) == fetch) pending.Remove(index); }
        }
    }

    public void Stop()
    {
        lock (gate) { if (stopped) return; stopped = true; }
        lifetime.Cancel();
        _ = readAhead?.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
    }

    // Closing the file deletes it, which takes long enough for gigabytes that it happens on another thread.
    public void Dispose() { Stop(); if (store is not null) _ = Task.Run(store.Dispose); }
}
