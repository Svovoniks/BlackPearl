using Microsoft.Win32.SafeHandles;

namespace BlackPearl.Core;

// The room's video as it arrives, kept in a temporary file so the buffer ahead of playback
// is bounded by the disk rather than by memory. Blocks are stored in arrival order, so a
// seek to the end of a large movie does not allocate the file up to there. The file is
// deleted when it closes, including when the app is killed.
public sealed class StreamCache : IDisposable
{
    // Buffering stops while the drive has less than this free, and resumes when space returns.
    public const long ReserveBytes = 4L * 1024 * 1024 * 1024;
    private const int SpaceCheckBlocks = 256;
    private const int FullRetryMs = 10000;
    private readonly object gate = new();
    private readonly SafeFileHandle file;
    private readonly string drive;
    private readonly long reserve;
    private readonly SharedMedia media;
    // Zero while a block is absent, otherwise one more than its position in the file.
    private readonly int[] slots;
    private int stored;
    private long fullUntil = long.MinValue;
    private bool recheck, reported;
    private volatile bool disposed;

    private StreamCache(SafeFileHandle file, string path, SharedMedia media, long reserve)
    {
        this.file = file; this.media = media; this.reserve = reserve;
        drive = Path.GetPathRoot(path) ?? path;
        slots = new int[(media.Length + MediaStreaming.ChunkBytes - 1) / MediaStreaming.ChunkBytes];
    }

    public static StreamCache? TryCreate(string directory, SharedMedia media, long reserveBytes = ReserveBytes)
    {
        if (media.Length <= 0 || media.Length / MediaStreaming.ChunkBytes >= int.MaxValue - 1) return null;
        try
        {
            directory = Path.GetFullPath(directory);
            Directory.CreateDirectory(directory);
            // A file left by a power loss is removed here; one another running app holds open refuses and stays.
            foreach (var stale in Directory.EnumerateFiles(directory, "*.stream"))
                try { File.Delete(stale); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".stream");
            var cache = new StreamCache(File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.DeleteOnClose), path, media, reserveBytes);
            PlaybackDiagnostics.Record("stream-cache", new { state = "created", mediaBytes = media.Length, freeBytes = cache.FreeBytes() });
            return cache;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.Security.SecurityException)
        {
            PlaybackDiagnostics.Record("stream-cache", new { state = "unavailable", error = ex.GetType().Name });
            return null;
        }
    }

    // The same file streamed again, such as after a reconnect, keeps what is already buffered.
    public bool Matches(SharedMedia other) => other.Length == media.Length &&
        string.Equals(other.Extension, media.Extension, StringComparison.OrdinalIgnoreCase) && other.Title == media.Title;

    public long StoredBytes => (long)Volatile.Read(ref stored) * MediaStreaming.ChunkBytes;
    // False while the drive is short of space or the file is closed; read-ahead then falls back to its memory window.
    // Like the reads, this never waits for a write to the file to finish.
    public bool Accepting => !disposed && Environment.TickCount64 >= Volatile.Read(ref fullUntil);
    internal bool Contains(long offset) => Volatile.Read(ref slots[offset / MediaStreaming.ChunkBytes]) != 0;

    internal bool TryStore(long offset, byte[] data)
    {
        lock (gate)
        {
            if (disposed) return false;
            var index = offset / MediaStreaming.ChunkBytes;
            if (slots[index] != 0) return true;
            if (!HasSpace()) return false;
            try { RandomAccess.Write(file, data, (long)stored * MediaStreaming.ChunkBytes); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException) { Full(ex.GetType().Name); return false; }
            Volatile.Write(ref slots[index], ++stored);
            return true;
        }
    }

    internal byte[]? TryRead(long offset)
    {
        var slot = Volatile.Read(ref slots[offset / MediaStreaming.ChunkBytes]);
        if (slot == 0) return null;
        var data = new byte[(int)Math.Min(MediaStreaming.ChunkBytes, media.Length - offset)];
        try
        {
            for (var read = 0; read < data.Length;)
            {
                var count = RandomAccess.Read(file, data.AsSpan(read), (long)(slot - 1) * MediaStreaming.ChunkBytes + read);
                if (count == 0) return null;
                read += count;
            }
            return data;
        }
        // Closed while a read was on its way: the caller fetches the block again or is itself stopping.
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { return null; }
    }

    private bool HasSpace()
    {
        if (Environment.TickCount64 < fullUntil) return false;
        if (!recheck && stored % SpaceCheckBlocks != 0) return true;
        recheck = false;
        if (FreeBytes() is not { } free || free >= reserve) { reported = false; return true; }
        Full("space");
        return false;
    }
    private void Full(string reason)
    {
        if (!reported) PlaybackDiagnostics.Record("stream-cache", new { state = "full", reason, storedBytes = StoredBytes });
        recheck = reported = true; Volatile.Write(ref fullUntil, Environment.TickCount64 + FullRetryMs);
    }
    private long? FreeBytes()
    {
        try { return new DriveInfo(drive).AvailableFreeSpace; }
        // A network share has no drive to ask; a failed write reports a full disk instead.
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }

    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        PlaybackDiagnostics.Record("stream-cache", new { state = "closed", storedBytes = StoredBytes });
        file.Dispose();
    }
}
