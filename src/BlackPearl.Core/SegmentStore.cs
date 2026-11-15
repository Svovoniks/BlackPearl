using Microsoft.Win32.SafeHandles;

namespace BlackPearl.Core;

// The pieces of a converted stream, kept in one temporary file in the order they arrive.
// Whoever converts the video keeps what it has produced here, and each viewer keeps what
// it has downloaded. The file is deleted when it closes, including when the app is killed.
public sealed class SegmentStore : IDisposable
{
    private const int FullRetryMs = 10000;
    private readonly object gate = new();
    private readonly SafeFileHandle file;
    private readonly string drive;
    private readonly long reserve;
    private readonly long[] offsets;
    // Zero while a piece is absent.
    private readonly int[] lengths;
    private long end, fullUntil = long.MinValue;
    private int stored;
    private bool reported;
    private volatile bool disposed, broken;

    private SegmentStore(SafeFileHandle file, string path, int segments, long reserve)
    {
        this.file = file; this.reserve = reserve;
        drive = Path.GetPathRoot(path) ?? path;
        offsets = new long[segments]; lengths = new int[segments];
    }

    public static SegmentStore? TryCreate(string directory, int segments, long reserveBytes = StreamCache.ReserveBytes)
    {
        if (segments <= 0) return null;
        try
        {
            directory = Path.GetFullPath(directory);
            Directory.CreateDirectory(directory);
            // A file left by a power loss is removed here; one a running app holds open refuses and stays.
            foreach (var stale in Directory.EnumerateFiles(directory, "*.segments"))
                try { File.Delete(stale); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".segments");
            return new(File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.DeleteOnClose), path, segments, reserveBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.Security.SecurityException)
        {
            PlaybackDiagnostics.Record("segment-store", new { state = "unavailable", error = ex.GetType().Name });
            return null;
        }
    }

    public int Segments => lengths.Length;
    public int Count => Volatile.Read(ref stored);
    public long StoredBytes => Interlocked.Read(ref end);
    // False while the drive is short of space or the file is closed. A drive that was full is asked again first.
    public bool Accepting
    {
        get
        {
            if (disposed || broken) return false;
            var until = Volatile.Read(ref fullUntil);
            if (until == long.MinValue) return true;
            var now = Environment.TickCount64;
            if (now < until) return false;
            if (FreeBytes() is { } free && free < reserve) { Volatile.Write(ref fullUntil, now + FullRetryMs); return false; }
            return true;
        }
    }
    public bool Has(int index) => index >= 0 && index < lengths.Length && Volatile.Read(ref lengths[index]) != 0;
    public int Length(int index) => index >= 0 && index < lengths.Length ? Volatile.Read(ref lengths[index]) : 0;

    public bool TryAdd(int index, ReadOnlySpan<byte> data)
    {
        if (index < 0 || index >= lengths.Length || data.Length == 0) return false;
        lock (gate)
        {
            if (disposed || broken) return false;
            if (lengths[index] != 0) return true;
            if (Environment.TickCount64 < fullUntil) return false;
            if (FreeBytes() is { } free && free - data.Length < reserve) { Full("space"); return false; }
            try { RandomAccess.Write(file, data, end); }
            // A file that cannot be written, as with a quota or a failing disk, is not tried again.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException) { Full(ex.GetType().Name); broken = true; return false; }
            offsets[index] = end; Interlocked.Add(ref end, data.Length); reported = false; Volatile.Write(ref fullUntil, long.MinValue);
            Volatile.Write(ref lengths[index], data.Length); Interlocked.Increment(ref stored);
            return true;
        }
    }

    // Null when the piece is absent, the range is outside it, or the file closed while reading.
    public byte[]? Read(int index, long offset, int count)
    {
        var length = Length(index);
        if (length == 0 || offset < 0 || count < 0 || offset + count > length) return null;
        var data = new byte[count];
        try
        {
            for (var read = 0; read < count;)
            {
                var got = RandomAccess.Read(file, data.AsSpan(read), offsets[index] + offset + read);
                if (got == 0) return null;
                read += got;
            }
            return data;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { return null; }
    }

    private void Full(string reason)
    {
        if (!reported) PlaybackDiagnostics.Record("segment-store", new { state = "full", reason, storedBytes = StoredBytes });
        reported = true; Volatile.Write(ref fullUntil, Environment.TickCount64 + FullRetryMs);
    }
    private long? FreeBytes()
    {
        try { return new DriveInfo(drive).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }

    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        file.Dispose();
    }
}
