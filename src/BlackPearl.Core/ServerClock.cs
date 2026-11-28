namespace BlackPearl.Core;

// Room timestamps are UTC, but elapsed playback time must not depend on changes
// to the operating system clock (VM clock synchronization, NTP, manual changes).
public sealed class ServerClock
{
    private readonly TimeProvider time;
    private readonly object gate = new();
    private readonly Dictionary<long, long> pending = [];
    private readonly List<(long At, long Rtt, long Server)> samples = [];
    private long anchor, serverAnchor;
    public ServerClock(TimeProvider? time = null)
    {
        this.time = time ?? TimeProvider.System;
        anchor = this.time.GetTimestamp(); serverAnchor = this.time.GetUtcNow().ToUnixTimeMilliseconds();
    }
    public long Now { get { lock (gate) return serverAnchor + Elapsed(anchor, time.GetTimestamp()); } }
    public long OffsetMs => Now - time.GetUtcNow().ToUnixTimeMilliseconds();
    public long RecentRttMs { get { lock (gate) return samples.Where(s => Elapsed(s.At, time.GetTimestamp()) <= 10000).Select(s => s.Rtt).DefaultIfEmpty(0).Max(); } }
    internal void Initialize(long hostUnixMs)
    {
        lock (gate) { anchor = time.GetTimestamp(); serverAnchor = hostUnixMs; samples.Clear(); pending.Clear(); }
    }
    private long Elapsed(long from, long to) => (long)time.GetElapsedTime(from, to).TotalMilliseconds;

    // A sample puts the streamer's clock within half its round trip of the truth: the reply can have spent any part of
    // it on the way back. On a congested link replies can wait seconds in a queue on their way back while the
    // requests went through at once; a room kept only ten seconds of samples, so for a minute it had no good one,
    // and its clock fell up to 1.7 s behind and seeked everyone back and forth. A sample stays good for minutes:
    // since it, the two PCs' clocks can only have drifted apart by what their crystals differ, well under this
    // allowance. So samples are kept for ten minutes and the one with the smallest bound sets the clock.
    private const long KeepMs = 10 * 60 * 1000;
    private const double DriftPerMs = .0005;
    private double Uncertainty((long At, long Rtt, long Server) sample, long now) => sample.Rtt / 2.0 + Elapsed(sample.At, now) * DriftPerMs;
    public void Sent(long id)
    {
        lock (gate)
        {
            var now = time.GetTimestamp();
            foreach (var old in pending.Where(p => Elapsed(p.Value, now) > 10000).Select(p => p.Key).ToArray()) pending.Remove(old);
            pending[id] = now;
        }
    }
    public bool Receive(long id, long serverUnixMs)
    {
        lock (gate)
        {
            if (!pending.Remove(id, out var sent)) return false;
            var now = time.GetTimestamp(); var rtt = Elapsed(sent, now);
            if (rtt < 0 || rtt > 10000 || serverUnixMs <= 0) return false;
            var sample = (At: now, Rtt: rtt, Server: serverUnixMs + rtt / 2);
            // The true clock lies within every sample's bound. One that a new sample contradicts no longer holds,
            // as when the streamer's clock itself moved.
            samples.RemoveAll(s => Elapsed(s.At, now) > KeepMs ||
                Math.Abs(s.Server + Elapsed(s.At, now) - sample.Server) > Uncertainty(s, now) + Uncertainty(sample, now) + 50);
            samples.Add(sample);
            var best = samples.OrderBy(s => Uncertainty(s, now)).ThenByDescending(s => s.At).First();
            var before = serverAnchor + Elapsed(anchor, now);
            anchor = now; serverAnchor = best.Server + Elapsed(best.At, now);
            if (PlaybackDiagnostics.Detailed) PlaybackDiagnostics.Record("clock", new { id, rtt, selectedRtt = best.Rtt, sampleAgeMs = Elapsed(best.At, now), correctionMs = serverAnchor - before, serverMs = serverAnchor });
            return true;
        }
    }
}
