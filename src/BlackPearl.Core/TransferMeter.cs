namespace BlackPearl.Core;

// How fast data arrives from one member while this app asks for more than arrives: what the connection carries.
// Time with few reads under way is left out, because then the video's own bitrate, or a pause, sets the pace and
// the number would say nothing about the connection.
public sealed class TransferMeter(Func<long>? clock = null)
{
    // Read-ahead keeps sixteen reads under way while the connection is what holds it back.
    public const int BusyReads = 8;
    private const int WindowMs = 20000, EnoughMs = 4000, SliceMs = 1000;
    private readonly Func<long> now = clock ?? (() => Environment.TickCount64);
    private readonly object gate = new();
    private readonly Queue<(long At, long BusyMs, long Bytes)> slices = new();
    private int reads;
    private long changedAt, sliceAt, sliceBusyMs, sliceBytes;
    private (int Kbps, long At)? last;

    public void Started() { lock (gate) { Account(0); reads++; } }
    public void Finished(int bytes) { lock (gate) { Account(bytes); reads--; } }
    // The speed over the last twenty seconds of such time, and how long ago it was last measured.
    public (int Kbps, long AgeMs)? Rate
    {
        get { lock (gate) { Account(0); return last is { } measured ? (measured.Kbps, now() - measured.At) : null; } }
    }

    private void Account(int bytes)
    {
        var time = now();
        var busy = reads >= BusyReads;
        if (busy) { sliceBusyMs += time - changedAt; sliceBytes += bytes; }
        changedAt = time;
        if (time - sliceAt >= SliceMs)
        {
            if (sliceBusyMs > 0) slices.Enqueue((time, sliceBusyMs, sliceBytes));
            sliceAt = time; sliceBusyMs = sliceBytes = 0;
        }
        while (slices.TryPeek(out var old) && time - old.At > WindowMs) slices.Dequeue();
        if (!busy) return;
        long ms = sliceBusyMs, total = sliceBytes;
        foreach (var slice in slices) { ms += slice.BusyMs; total += slice.Bytes; }
        if (ms >= EnoughMs) last = ((int)Math.Min(int.MaxValue, total * 8 / ms), time);
    }
}
