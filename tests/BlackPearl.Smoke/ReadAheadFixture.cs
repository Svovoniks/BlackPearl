using System.Collections.Concurrent;
using BlackPearl.Core;

static class ReadAheadFixture
{
    public static async Task Run(Action<bool, string> check)
    {
        const int block = MediaStreaming.ChunkBytes;
        check(StreamingBufferOptions.ReadAheadBytes(3_600_000_000, 3_600_000) == 37_500_000,
            "the memory window covers thirty seconds at the movie's average bitrate with variable-bitrate headroom");
        check(StreamingBufferOptions.ReadAheadBytes(long.MaxValue / 4, 1000, 60) == StreamingBufferOptions.MaximumReadAheadBytes &&
            StreamingBufferOptions.ReadAheadBytes(1000, 0) == 0, "the memory window is bounded and waits for a known duration");

        var source = new Source(block * 64);
        var cached = new CachedMediaSource(source);
        cached.Played(0);
        await Task.Delay(200);
        check(source.Offsets.IsEmpty, "before read-ahead starts, playback reads only what the player requests");
        cached.SetReadAhead(block * 10);
        await Until(() => cached.CachedAheadBytes >= block * 10);
        check(source.Offsets.All(offset => offset < block * 10) && source.Offsets.Count == 10,
            "without a cache file, read-ahead fills its window past the playback position without player requests");
        cached.Played(block * 5);
        await Until(() => source.Offsets.Contains(block * 14));
        check(!source.Offsets.Any(offset => offset >= block * 15), "read-ahead follows the playback position and stops at the window");
        var before = source.Offsets.Count;
        check((await cached.ReadAsync(block * 6, block * 4, default)).Length == block * 4 && source.Offsets.Count == before,
            "played video comes from the read-ahead cache");

        cached.Requested(block * 60);
        cached.Played(block * 40);
        await Until(() => source.Offsets.Contains(block * 49));
        check(!source.Offsets.Any(offset => offset is >= block * 15 and < block * 40), "a seek moves read-ahead to the new position");
        cached.Stop();

        // Steady playback with more video played than the cache keeps behind the playback position.
        const int steadyBlocks = 2048, windowBlocks = 768;
        var steady = new Source((long)block * steadyBlocks);
        cached = new CachedMediaSource(steady);
        cached.SetReadAhead((long)block * windowBlocks); cached.Played(0);
        for (var played = 0; played < steadyBlocks; played++)
        {
            var buffered = Math.Min(steadyBlocks, played + windowBlocks);
            await Until(() => steady.Offsets.Count >= buffered);
            await cached.ReadAsync((long)played * block, block, default); cached.Played((long)(played + 1) * block);
        }
        check(steady.Offsets.Count == steadyBlocks && steady.Offsets.Distinct().Count() == steadyBlocks,
            "steady playback keeps the video buffered ahead instead of dropping it and downloading it again");
        cached.Stop();

        var failing = new Source(block * 64) { Fail = true };
        cached = new CachedMediaSource(failing);
        cached.SetReadAhead(block * 10); cached.Played(0);
        await Task.Delay(1500);
        check(failing.Attempts is > 0 and <= 12, "an unavailable host is retried at a bounded rate instead of in a tight loop");
        cached.Stop();

        var directory = Path.Combine(Path.GetTempPath(), "BlackPearl-checks", Guid.NewGuid().ToString("N"));
        try
        {
            // The last block of this movie is a short one.
            var movie = new Source((long)block * 1024 + 1000);
            var disk = StreamCache.TryCreate(directory, movie.Media, reserveBytes: 0);
            check(disk is not null && Directory.GetFiles(directory, "*.stream").Length == 1, "a streamed video gets a cache file");
            cached = new CachedMediaSource(movie, disk: disk);
            cached.SetReadAhead(block * 4); cached.Played(0);
            await Until(() => cached.CachedAheadBytes == movie.Media.Length);
            check(movie.Offsets.Count == 1025 && movie.Offsets.Distinct().Count() == 1025 && disk!.StoredBytes >= movie.Media.Length,
                "with a cache file, read-ahead downloads the rest of the video while the player asks for nothing");
            var tail = await cached.ReadAsync(movie.Media.Length - 5000, 5000, default);
            var middle = await cached.ReadAsync(block * 500L + 7, block * 3, default);
            check(tail.SequenceEqual(Source.Bytes(movie.Media.Length - 5000, 5000)) && middle.SequenceEqual(Source.Bytes(block * 500L + 7, block * 3)) &&
                movie.Offsets.Count == 1025, "buffered video plays from the cache file byte for byte, including the short last block");
            cached.Stop();

            // A reconnect opens the same video again with a new connection.
            var reopened = new Source(movie.Media.Length);
            check(disk!.Matches(reopened.Media with { Id = "after-reconnect" }) && !disk.Matches(reopened.Media with { Length = movie.Media.Length + 1 }) &&
                !disk.Matches(reopened.Media with { Title = "Another video" }), "a cache file is kept only for the same video");
            cached = new CachedMediaSource(reopened, disk: disk);
            cached.SetReadAhead(block * 4); cached.Played(0);
            await Task.Delay(200);
            check((await cached.ReadAsync(block * 10L, block, default)).SequenceEqual(Source.Bytes(block * 10L, block)) && reopened.Offsets.IsEmpty,
                "reopening the same video continues from what is already buffered");
            cached.Stop(); disk.Dispose();
            check(!disk.Accepting, "a closed cache takes no more video at once");
            // The file itself goes on another thread: deleting gigabytes would freeze the window.
            await Until(() => Directory.GetFiles(directory, "*.stream").Length == 0);
            check(true, "the cache file is deleted when the video closes");

            // The player's own reads come first.
            var waiting = new Source((long)block * 256) { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            using var waitingDisk = StreamCache.TryCreate(directory, waiting.Media, reserveBytes: 0)!;
            cached = new CachedMediaSource(waiting, disk: waitingDisk);
            cached.SetReadAhead(block * 4);
            var playerRead = cached.ReadAsync(0, block, default);
            cached.Played(0);
            await Task.Delay(300);
            check(waiting.Attempts == 1, "read-ahead starts no download while the player is waiting for video");
            waiting.Hold.SetResult(); await playerRead;
            await Until(() => cached.CachedAheadBytes == waiting.Media.Length);
            check(waiting.Offsets.Count == 256, "read-ahead continues once the player has what it asked for");
            cached.Stop();

            var seek = new Source((long)block * 512) { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            using var seekDisk = StreamCache.TryCreate(directory, seek.Media, reserveBytes: 0)!;
            cached = new CachedMediaSource(seek, disk: seekDisk);
            cached.SetReadAhead(block * 4); cached.Played(0);
            await Until(() => seek.Attempts == MediaStreaming.Window / 2);
            cached.Requested(block * 300L);
            await Until(() => seek.Canceled == MediaStreaming.Window / 2);
            await Task.Delay(200);
            check(seek.Offsets.IsEmpty && seek.Attempts == MediaStreaming.Window / 2, "a seek outside the buffer stops the downloads read-ahead had under way and starts no others");
            seek.Hold.SetResult();
            // Nothing plays: the room is paused at the new position.
            await Until(() => cached.CachedAheadBytes == seek.Media.Length - block * 300L);
            check(seek.Offsets.Count == 212 && seek.Offsets.Min() == block * 300L,
                "after a seek while paused, read-ahead continues from the new position to the end of the video");
            cached.Played(block * 100L);
            await Until(() => seek.Offsets.Count == 412);
            await Task.Delay(100);
            check(seek.Offsets.Count == 412 && seek.Offsets.Distinct().Count() == 412 && seek.Offsets.Min() == block * 100L,
                "seeking back buffers from there up to the video that is already kept");
            cached.Stop();

            var full = new Source((long)block * 256);
            using var noSpace = StreamCache.TryCreate(directory, full.Media, reserveBytes: long.MaxValue, fullRetryMs: 200)!;
            cached = new CachedMediaSource(full, disk: noSpace);
            cached.SetReadAhead(block * 10); cached.Played(0);
            await Until(() => full.Offsets.Count >= 10);
            await Task.Delay(300);
            check(full.Offsets.Count <= MediaStreaming.Window / 2 && !noSpace.Accepting && noSpace.StoredBytes == 0 &&
                (await cached.ReadAsync(block * 3L, block, default)).SequenceEqual(Source.Bytes(block * 3L, block)) && full.Offsets.Count <= MediaStreaming.Window / 2,
                "a disk without room leaves the buffer in memory, within its window");
            // Several retry intervals later, with playback standing still.
            var fetched = full.Offsets.Count;
            await Task.Delay(1000);
            check(full.Offsets.Count == fetched && !noSpace.Accepting, "a disk that is still full is not tried again with more video");
            cached.Stop();

            var stale = Path.Combine(directory, "left-behind.stream");
            File.WriteAllBytes(stale, new byte[16]);
            using var next = StreamCache.TryCreate(directory, full.Media, reserveBytes: 0)!;
            await Until(() => !File.Exists(stale));
            check(Directory.GetFiles(directory, "*.stream").Length == 4, "a cache file left behind is removed, and the ones in use stay");
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class Source(long length) : IMediaSource
    {
        public readonly ConcurrentBag<long> Offsets = [];
        public bool Fail;
        public int Attempts, Canceled;
        public TaskCompletionSource? Hold;
        public SharedMedia Media { get; } = new("read-ahead", "Read-ahead", length, ".mkv");
        public static byte[] Bytes(long offset, int count)
        {
            var bytes = new byte[count];
            for (var i = 0; i < count; i++) bytes[i] = (byte)((offset + i) * 31 + (offset + i) / 251);
            return bytes;
        }
        public async Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct)
        {
            Interlocked.Increment(ref Attempts);
            if (Fail) throw new IOException("Host unavailable");
            if (Hold is { } hold)
                try { await hold.Task.WaitAsync(ct); }
                catch (OperationCanceledException) { Interlocked.Increment(ref Canceled); throw; }
            Offsets.Add(offset);
            return Bytes(offset, count);
        }
    }
}
