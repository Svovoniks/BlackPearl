namespace BlackPearl.Core;

// Everything fetched for a movie is kept so nearby seeks and the buffer ahead are reused.
// With a disk cache the video is kept in its temporary file and read-ahead runs to the end
// of the movie; without one, a memory cache holds a bounded window past the playback
// position. Shared reads are canceled only after their last HTTP consumer leaves.
// Read-ahead fills the cache in the background, so starting playback never waits for it.
internal sealed class CachedMediaSource(IMediaSource source, int capacityBytes = 16 * 1024 * 1024, StreamCache? disk = null) : IMediaSource
{
    // Half of a peer's request window, so read-ahead takes what the connection gives. It
    // starts no read while the player is itself waiting for video, which keeps the whole
    // connection for the frames needed next.
    private const int PrefetchReads = MediaStreaming.Window / 2;
    private const long JumpSlackBytes = 2L * 1024 * 1024;
    // A seek makes several requests (index, then video); read-ahead follows the last one.
    private const int SeekHoldMs = 1000;
    private readonly object gate = new();
    private readonly Dictionary<long, LinkedListNode<(long Offset, byte[] Bytes)>> cache = [];
    private readonly LinkedList<(long Offset, byte[] Bytes)> recent = [];
    private readonly Dictionary<long, Pending> pending = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private CancellationTokenSource epoch = new();
    private Task? prefetch;
    private long readAhead, anchor = -1, cursor = -1, retryAt, resumeFrom = -1, resumeAt;
    private int playerWaits, prefetching;
    private bool faulted, stopped;
    // A run of blocks known to be on disk, which never loses one, so measuring the buffer does not rescan it.
    private long diskRunFrom = -1, diskRunTo = -1;
    private readonly int baseCapacity = capacityBytes;
    private int capacity = capacityBytes;
    private int bytes;
    public SharedMedia Media => source.Media;
    public long ReadAheadBytes { get { lock (gate) return readAhead; } }
    // True while read-ahead has no window and continues to the end of the movie.
    public bool Unbounded => disk?.Accepting == true;
    public long CachedAheadBytes { get { lock (gate) return anchor < 0 ? 0 : Math.Max(0, Math.Min(CachedUntil(), Media.Length) - anchor); } }
    // True once everything from the playback position to the end of the video is held.
    public bool CachedToEnd { get { lock (gate) return anchor >= 0 && CachedUntil() >= Media.Length; } }
    // Where the video held from the playback position stops. Called with the lock held.
    private long CachedUntil()
    {
        var block = Align(anchor);
        if (disk is not null)
        {
            var start = block;
            if (start >= diskRunFrom && start <= diskRunTo) block = diskRunTo; else diskRunFrom = start;
            while (block < Media.Length && disk.Contains(block)) block += MediaStreaming.ChunkBytes;
            diskRunTo = block;
        }
        while (block < Media.Length && Has(block)) block += MediaStreaming.ChunkBytes;
        return block;
    }
    public async Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct)
    {
        if (offset < 0 || count < 0 || offset > Media.Length || count > Media.Length - offset) throw new ArgumentOutOfRangeException(nameof(offset));
        var result = new byte[count]; var written = 0;
        while (written < count)
        {
            ct.ThrowIfCancellationRequested();
            var position = offset + written; var blockOffset = Align(position);
            var block = await Block(blockOffset, ct, player: true);
            var within = (int)(position - blockOffset); var copy = Math.Min(count - written, block.Length - within);
            if (copy <= 0) throw new EndOfStreamException("Incomplete media range");
            block.AsSpan(within, copy).CopyTo(result.AsSpan(written)); written += copy;
        }
        return result;
    }
    // Starts read-ahead. The window applies while there is no disk cache to hold more.
    public void SetReadAhead(long aheadBytes)
    {
        lock (gate)
        {
            if (stopped) return;
            readAhead = Math.Clamp(aheadBytes, 0, StreamingBufferOptions.MaximumReadAheadBytes);
            capacity = (int)Math.Max(baseCapacity, readAhead + baseCapacity);
            if (readAhead > 0) prefetch ??= Task.Run(Prefetch);
        }
        Wake();
    }
    // A new HTTP range outside what is buffered (a seek or an index probe) stops the reads
    // read-ahead has under way. It continues from there once a playback stream advances, or
    // after a moment when the video is paused and the player has what it asked for.
    public void Requested(long offset)
    {
        CancellationTokenSource? stale = null;
        lock (gate)
        {
            if (anchor >= 0)
            {
                var block = Align(offset);
                if (Has(block)) return;
                if (offset >= anchor - JumpSlackBytes && offset <= Math.Max(cursor, anchor) + JumpSlackBytes)
                {
                    // A gap left by an abandoned read inside the buffer is filled again.
                    if (block >= Align(anchor) && block < cursor) cursor = block;
                    return;
                }
                stale = epoch; epoch = new();
                anchor = cursor = -1;
            }
            resumeFrom = offset; resumeAt = Environment.TickCount64 + SeekHoldMs;
        }
        // Canceling runs the canceled reads' continuations, read-ahead's own loop among them, so the
        // new state is in place first and the lock is released.
        stale?.Cancel();
        Wake();
    }
    public void Played(long position)
    {
        lock (gate)
        {
            resumeFrom = -1;
            if (anchor < 0 || position < anchor || position > Math.Max(cursor, anchor) + JumpSlackBytes) cursor = Align(position);
            anchor = position;
        }
        Wake();
    }
    public void Stop()
    {
        CancellationTokenSource stale;
        lock (gate) { if (stopped) return; stopped = true; stale = epoch; }
        lifetime.Cancel(); stale.Cancel();
        _ = prefetch?.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
    }
    private static long Align(long position) => position / MediaStreaming.ChunkBytes * MediaStreaming.ChunkBytes;
    private bool Has(long block) => cache.ContainsKey(block) || disk?.Contains(block) == true;
    private void Wake()
    {
        try { if (wake.CurrentCount == 0) wake.Release(); }
        catch (Exception ex) when (ex is SemaphoreFullException or ObjectDisposedException) { }
    }
    private async Task Prefetch()
    {
        var reads = new List<Task>(); Task? signal = null;
        while (!lifetime.IsCancellationRequested)
        {
            reads.RemoveAll(read => read.IsCompleted);
            var wait = 0L;
            if (reads.Count < PrefetchReads && NextBlock(out wait) is { } next) { reads.Add(Warm(next.Offset, next.Token)); continue; }
            signal ??= wake.WaitAsync(lifetime.Token);
            // Retry after a failed read and resume after a seek, even when nothing else wakes the loop.
            await Task.WhenAny(wait > 0 ? [.. reads, signal, Task.Delay((int)Math.Min(wait, 1000), lifetime.Token)] : [.. reads, signal]);
            if (signal.IsCompleted) { if (signal.IsCanceled) return; signal = null; }
        }
    }
    private (long Offset, CancellationToken Token)? NextBlock(out long waitMs)
    {
        lock (gate)
        {
            waitMs = 0;
            // After a failed read one probe at a time finds out when the streamer is back.
            if (readAhead <= 0 || playerWaits > 0 || faulted && prefetching > 0) return null;
            var now = Environment.TickCount64;
            if (now < retryAt) { waitMs = retryAt - now; return null; }
            if (anchor < 0)
            {
                if (resumeFrom < 0) return null;
                if (now < resumeAt) { waitMs = resumeAt - now; return null; }
                anchor = resumeFrom; cursor = Align(resumeFrom); resumeFrom = -1;
            }
            var limit = Unbounded ? Media.Length : Math.Min(Media.Length, anchor + readAhead);
            cursor = Math.Max(cursor, Align(anchor));
            while (cursor < limit && (Has(cursor) || pending.ContainsKey(cursor))) cursor += MediaStreaming.ChunkBytes;
            if (cursor >= limit) return null;
            var offset = cursor; cursor += MediaStreaming.ChunkBytes; prefetching++;
            return (offset, epoch.Token);
        }
    }
    private async Task Warm(long offset, CancellationToken ct)
    {
        try { await Block(offset, ct, player: false); lock (gate) faulted = false; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception)
        {
            // An unavailable host must not turn read-ahead into a retry storm.
            lock (gate) { faulted = true; retryAt = Environment.TickCount64 + 1000; if (cursor > offset) cursor = offset; }
        }
        finally { lock (gate) prefetching--; }
    }
    private async Task<byte[]> Block(long offset, CancellationToken ct, bool player)
    {
        Pending read;
        for (var again = false; ; again = true)
        {
            if (disk?.TryRead(offset) is { } stored) return stored;
            lock (gate)
            {
                if (cache.TryGetValue(offset, out var hit))
                {
                    recent.Remove(hit); recent.AddLast(hit); return hit.Value.Bytes;
                }
                if (!pending.TryGetValue(offset, out read!))
                {
                    // Stored between the look at the file above and here: read it rather than download it again.
                    if (!again && disk?.Contains(offset) == true) continue;
                    read = new Pending(); pending.Add(offset, read);
                    read.Task = Fetch(offset, read);
                }
                read.Users++; if (player) playerWaits++;
            }
            break;
        }
        try { return await read.Task.WaitAsync(ct); }
        finally
        {
            var resume = false;
            lock (gate)
            {
                if (player) resume = --playerWaits == 0;
                if (--read.Users == 0)
                {
                    if (!read.Task.IsCompleted)
                    {
                        if (pending.GetValueOrDefault(offset) == read) pending.Remove(offset);
                        read.Cancellation.Cancel();
                    }
                    _ = read.Task.ContinueWith(t => { _ = t.Exception; read.Cancellation.Dispose(); }, TaskScheduler.Default);
                }
            }
            if (resume) Wake();
        }
    }
    private async Task<byte[]> Fetch(long offset, Pending read)
    {
        // Yield so the read is registered and has a consumer before completion.
        await Task.Yield();
        try
        {
            var count = (int)Math.Min(MediaStreaming.ChunkBytes, Media.Length - offset);
            var data = await source.ReadAsync(offset, count, read.Cancellation.Token);
            if (data.Length != count) throw new EndOfStreamException("Incomplete media range");
            if (disk?.TryStore(offset, data) == true) return data;
            lock (gate)
            {
                if (!read.Cancellation.IsCancellationRequested && !cache.ContainsKey(offset) && data.Length <= capacity)
                {
                    cache.Add(offset, recent.AddLast((offset, data))); bytes += data.Length;
                    while (bytes > capacity && Oldest() is { } oldest)
                    { bytes -= oldest.Value.Bytes.Length; cache.Remove(oldest.Value.Offset); recent.Remove(oldest); }
                }
            }
            return data;
        }
        finally
        {
            lock (gate) { if (pending.GetValueOrDefault(offset) == read) pending.Remove(offset); }
        }
    }
    // The least recently used block that is not buffered ahead of playback. Video waiting to
    // be played was fetched before the video behind it was last read, so by age alone it
    // would be dropped first and downloaded again.
    private LinkedListNode<(long Offset, byte[] Bytes)>? Oldest()
    {
        if (anchor < 0 || readAhead <= 0) return recent.First;
        var from = Align(anchor); var to = anchor + readAhead;
        for (var node = recent.First; node is not null; node = node.Next)
            if (node.Value.Offset < from || node.Value.Offset >= to) return node;
        return recent.First;
    }
    private sealed class Pending
    {
        public readonly CancellationTokenSource Cancellation = new();
        public Task<byte[]> Task = null!;
        public int Users;
    }
}
