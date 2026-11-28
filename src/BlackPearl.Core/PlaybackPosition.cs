namespace BlackPearl.Core;

// Where the player is between libVLC's reports of it. libVLC sends its time about every quarter second, but how
// fresh each time is depends on the input. A local file's every report is new and exact. A room's converted stream
// (MPEG-TS pieces in an HLS list) repeats the last time every quarter second and moves it on only about once a
// second, measured in StreamQualityFixture. Treating each repeat as new read up to 750 ms behind and made the room
// change speed every few seconds. A new time is exact when libVLC takes it, and is seen at the first report after,
// up to a quarter second late. So every new time anchors an extrapolation, and the estimate is the furthest of the
// last few seconds' anchors: the one seen soonest after it changed. Extrapolation stops a little past the usual gap
// between new times, so a stalled read is never extrapolated indefinitely.
public sealed class PlaybackPosition
{
    // A report this close to the last new time repeats it: repeats wander by a few milliseconds.
    private const long RepeatMs = 50;
    // Anchors older than this are dropped. The anchor seen soonest after its change comes round every few seconds.
    private const long WindowMs = 5000;
    // A new time this far ahead of the estimate is a seek or another video. One behind it by more than a new time can
    // be late means playback slipped, as in a seek, a stall or a hiccup: a stream that repeats its time shows a new one
    // up to a report late (measured spread 245 ms), a file whose every report is new shows it at once.
    private const long JumpMs = 1500, RepeatingSlipMs = 400, ExactSlipMs = 100;
    private readonly object gate = new();
    private readonly List<(long Position, long At)> anchors = [];
    private long position = -1, at, intervalMs = 250;
    private float rate = 1;

    public void Observe(long milliseconds, long monotonicMs)
    {
        lock (gate)
        {
            if (position >= 0 && Math.Abs(milliseconds - position) < RepeatMs) return;
            var gap = monotonicMs - at; var step = milliseconds - position;
            // The usual gap between new times: a longer one counts at once, so a stream that moves on once a second
            // is not cut short while this learns it.
            if (position >= 0 && gap is > 0 and < 3000 && step is > 0 and < 3000) intervalMs = Math.Max(gap, (intervalMs * 3 + gap) / 4);
            if (position < 0) anchors.Clear();
            else
            {
                var expected = Extrapolate(monotonicMs);
                if (milliseconds > expected + JumpMs || milliseconds < expected - (intervalMs > 400 ? RepeatingSlipMs : ExactSlipMs)) anchors.Clear();
            }
            position = milliseconds; at = monotonicMs;
            anchors.RemoveAll(a => monotonicMs - a.At > WindowMs);
            anchors.Add((milliseconds, monotonicMs));
        }
    }

    public long Estimate(long actual, bool playing, float rate, long monotonicMs)
    {
        lock (gate)
        {
            // Earlier anchors were extrapolated at another speed.
            if (rate != this.rate) { this.rate = rate; if (anchors.Count > 1) anchors.RemoveRange(0, anchors.Count - 1); }
            if (!playing)
            {
                // Paused, the position holds; playing again, it moves on from here, not from the last report before.
                if (actual >= 0) { position = actual; at = monotonicMs; anchors.Clear(); anchors.Add((actual, monotonicMs)); }
                return Math.Max(0, actual);
            }
            // A time not reported yet, such as the one just set by a seek.
            if (actual < 0 || position < 0 || Math.Abs(actual - position) >= RepeatMs) return Math.Max(0, actual);
            return Math.Max(0, Extrapolate(monotonicMs));
        }
    }

    // Called with the lock held. Each anchor extrapolates freely while newer times keep confirming that playback moves
    // on; only the newest bounds how far the estimate may go once times stop changing.
    private long Extrapolate(long monotonicMs)
    {
        var limit = Math.Clamp(intervalMs * 5 / 4 + 100, 500, 1500);
        var estimate = position;
        foreach (var (anchor, since) in anchors)
            estimate = Math.Max(estimate, anchor + (long)(Math.Max(0, monotonicMs - since) * rate));
        return Math.Min(estimate, position + (long)(limit * rate));
    }
}
