using System.Diagnostics;

namespace BlackPearl.Core;

/// <summary>How a phone's link was set up, from its offer to its <c>hello</c> (PHONE-REMOTE.md, "Measuring the link").
/// <see cref="Outcome"/> is <c>greeted</c>, or the reason it closed before. <see cref="Trace"/> is what
/// <see cref="PhoneLinkReports.Connecting"/> returned for it.</summary>
public sealed record PhoneConnectReport(string Outcome, bool Pairing, string Via, string Path, string? PhoneApp, string Local, string Remote, object? Trace);

/// <summary>A paired phone's link over one stretch of time: at most <see cref="PhoneLinkReports.Window"/>, or until it closed
/// (<see cref="End"/> is <c>open</c> while it still is, otherwise the reason it closed). Round trips are of the PC's pings, which
/// phones that list <c>ping</c> in their <c>hello</c> answer. Reads are the phone's on <c>media-v1</c>: their bytes, and how long
/// each waited on the PC from its request to its first frame (the disk, a conversion, the reads ahead of it).</summary>
public sealed record PhoneLinkWindow(string Path, string Via, string? PhoneApp, string End, TimeSpan Duration, TimeSpan LinkAge,
    int Pings, int Pongs, int Unanswered, double? RttMedian, double? RttP90, double? RttMax,
    long MediaBytes, int Reads, int Busy, double? ReadWaitMedian, double? ReadWaitP90, object? Trace)
{
    /// <summary>What goes as the trace's measurements: at most ten, which is all Sentry keeps of a transaction's. Milliseconds,
    /// bytes or counts; a number nothing measured is left out.</summary>
    public IReadOnlyList<(string Name, double Value, string Unit)> Measurements =>
        new (string, double?, string)[]
        {
            ("rtt", RttMedian, "ms"), ("rtt_p90", RttP90, "ms"), ("rtt_max", RttMax, "ms"),
            ("pings", Pings, ""), ("pings_unanswered", Unanswered, ""),
            ("media_bytes", MediaBytes, "byte"), ("media_reads", Reads, ""), ("media_busy", Busy, ""),
            ("read_wait", ReadWaitMedian, "ms"), ("read_wait_p90", ReadWaitP90, "ms"),
        }.Where(x => x.Item2 is not null).Select(x => (x.Item1, x.Item2!.Value, x.Item3)).ToArray();
    public const int MeasurementLimit = 10;
}

/// <summary>
/// Where the phone remote's link reports go beyond the diagnostics log. The desktop app sends them to Sentry while crash reports
/// are on, and clears these hooks when they are turned off. They carry no ids, names or addresses: the path's kind, how the
/// phone was introduced, the phone app's version and numbers.
/// </summary>
public static class PhoneLinkReports
{
    /// <summary>A paired link is reported in stretches this long, so one that stays open all day still says how it went.</summary>
    public static TimeSpan Window { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>How often the PC pings a phone that answers pings: twenty round trips a window, and little for a locked phone
    /// to wake for.</summary>
    public static TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>A link's window with no reads in it (only pings, as with a phone left connected in the background all day) goes at
    /// most this often; one with reads, or the one that ends a link, always.</summary>
    public static readonly TimeSpan IdleInterval = TimeSpan.FromHours(1);
    /// <summary>A setup that ended without the phone's hello goes once per reason this often.</summary>
    public static readonly TimeSpan FailureInterval = TimeSpan.FromMinutes(30);
    private static readonly Dictionary<string, long> failedAt = [];
    internal static bool MayReportFailure(string outcome)
    {
        var now = Stopwatch.GetTimestamp();
        lock (failedAt)
        {
            if (failedAt.TryGetValue(outcome, out var last) && Stopwatch.GetElapsedTime(last, now) < FailureInterval) return false;
            failedAt[outcome] = now;
            return true;
        }
    }
    internal static void ForgetFailures() { lock (failedAt) failedAt.Clear(); }
    /// <summary>A ping not answered within this long counts as unanswered; an answer after it counts for nothing.</summary>
    public static readonly TimeSpan PongWait = TimeSpan.FromSeconds(10);

    // Called as a phone's offer opens a session; what it returns comes back to ConnectStage and in the report's Trace.
    public static Func<object?>? Connecting { get; set; }
    // The session reached a stage: "connected" (ICE), then "channel" (remote-v1 open).
    public static Action<object, string>? ConnectStage { get; set; }
    public static Action<PhoneConnectReport>? Connects { get; set; }
    // Called as a window begins; what it returns comes back in that window's Trace. A window with nothing measured (a locked
    // phone, whose pings stop) isn't reported, and its trace is dropped unfinished.
    public static Func<object?>? WindowStarting { get; set; }
    public static Action<PhoneLinkWindow>? Windows { get; set; }

    internal static object? Start(Func<object?>? starting)
    {
        try { return starting?.Invoke(); } catch (Exception) { return null; }
    }
    internal static void Safe(Action action)
    {
        try { action(); } catch (Exception) { }
    }
}

/// <summary>One link's numbers since its window began. Called from native threads, the ping timer and the media server.</summary>
internal sealed class PhoneLinkStats
{
    private readonly object gate = new();
    private readonly Dictionary<long, long> waiting = [];
    private readonly List<double> rtts = [], waits = [];
    private int pings, pongs, unanswered, reads, busy;
    private long bytes, number;
    private long windowStarted = Stopwatch.GetTimestamp();

    public long WindowStarted { get { lock (gate) return windowStarted; } }

    /// <summary>The number for the next ping, now sent. Pings unanswered for <see cref="PhoneLinkReports.PongWait"/> are counted
    /// and forgotten here.</summary>
    public long PingSent(long now)
    {
        lock (gate)
        {
            Expire(now);
            pings++;
            waiting[++number] = now;
            return number;
        }
    }
    private void Expire(long now)
    {
        foreach (var (n, sent) in waiting.ToArray())
            if (Stopwatch.GetElapsedTime(sent, now) > PhoneLinkReports.PongWait) { waiting.Remove(n); unanswered++; }
    }

    /// <summary>The phone's pong: whether it answered a ping still waiting.</summary>
    public bool Pong(long pingNumber, long now)
    {
        lock (gate)
        {
            if (!waiting.Remove(pingNumber, out var sent)) return false;
            var elapsed = Stopwatch.GetElapsedTime(sent, now);
            if (elapsed > PhoneLinkReports.PongWait) { unanswered++; return false; }
            pongs++;
            if (rtts.Count < 4096) rtts.Add(elapsed.TotalMilliseconds);
            return true;
        }
    }

    public void Served(int count, TimeSpan wait)
    {
        lock (gate)
        {
            reads++; bytes += count;
            if (waits.Count < 4096) waits.Add(wait.TotalMilliseconds);
        }
    }
    public void Busy() { lock (gate) busy++; }

    // A phone that answered or read something; a locked one's link sits silent, and says nothing about the link.
    public bool Measured { get { lock (gate) return pongs > 0 || reads > 0; } }

    /// <summary>The window so far, and a new one from <paramref name="now"/>. Pings still waiting carry over into it.</summary>
    public PhoneLinkWindow Take(long now, string path, string via, string? app, string end, TimeSpan age, object? trace)
    {
        lock (gate)
        {
            Expire(now);
            var window = new PhoneLinkWindow(path, via, app, end, Stopwatch.GetElapsedTime(windowStarted, now), age,
                pings, pongs, unanswered, Percentile(rtts, 0.5), Percentile(rtts, 0.9), Percentile(rtts, 1),
                bytes, reads, busy, Percentile(waits, 0.5), Percentile(waits, 0.9), trace);
            rtts.Clear(); waits.Clear();
            pings = pongs = unanswered = reads = busy = 0; bytes = 0;
            windowStarted = now;
            return window;
        }
    }

    // Nearest rank: the median of 1, 2, 3, 4 is 2, and the 90th percentile of ten samples their ninth.
    internal static double? Percentile(IReadOnlyList<double> samples, double fraction)
    {
        if (samples.Count == 0) return null;
        var sorted = samples.Order().ToArray();
        return Math.Round(sorted[Math.Clamp((int)Math.Ceiling(fraction * sorted.Length) - 1, 0, sorted.Length - 1)], 1);
    }
}
