using Sentry;

namespace BlackPearl.Core;

/// <summary>
/// What every crash report from BlackPearl leaves out, for the desktop app and the room server alike: who sent it (user,
/// machine and device names, addresses), where things are on that computer (file paths in text, stack frames and the
/// list of loaded files, which name the Windows account), request details beyond the endpoint's path, breadcrumbs other
/// than the app's own trail (<see cref="Trail"/>), and reports about reports. Error text goes through <see cref="ReportText"/>.
/// </summary>
public static partial class CrashReportPrivacy
{
    // What an error report carries of the diagnostics log: its latest entries, at most this many and none older than this,
    // and at most TrailPerKind of one kind, so a steady one (playback samples every two seconds) can't push the rest out.
    // Metadata entries stay out: they describe titles, and metadata failures have reports of their own.
    public const int TrailEntries = 200, TrailPerKind = 30;
    public static readonly TimeSpan TrailAge = TimeSpan.FromMinutes(10);
    // The only fields whose text may leave, and only as a fixed code (TrailCode): what kind of error, stage, state or reason.
    // Everything else written as text, such as ids, names, titles, paths, addresses, messages and whatever a server or
    // another app said, is left out, as are nested objects, whose keys can be anything.
    private static readonly HashSet<string> TrailText = new(StringComparer.Ordinal)
    {
        "error", "detail", "inner", "stage", "state", "reason", "result", "outcome", "why", "action", "mode", "phase", "via", "source",
        "command", "type", "container", "provider", "extension", "encoder", "quality", "denoiser", "which", "status", "network", "scope", "content",
    };
    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9]+(?:(?:[-_.:/]|<-)[A-Za-z0-9]+)*$")] private static partial System.Text.RegularExpressions.Regex TrailCode();
    [System.Text.RegularExpressions.GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*$")] private static partial System.Text.RegularExpressions.Regex TrailKind();

    /// <summary>
    /// The diagnostics log's latest entries (<see cref="DiagnosticLog.Recent"/>) as breadcrumbs for an error report: each
    /// entry's kind and time, with only its numbers, true/false values and the codes in <see cref="TrailText"/>, which are
    /// scrubbed again as error text is. At most <see cref="TrailEntries"/>, none older than <see cref="TrailAge"/>.
    /// </summary>
    public static List<Breadcrumb> Trail(IEnumerable<string> entries, long nowMs)
    {
        var kept = new List<(long At, Breadcrumb Crumb)>();
        var oldest = nowMs - (long)TrailAge.TotalMilliseconds;
        foreach (var line in entries)
        {
            try
            {
                using var json = System.Text.Json.JsonDocument.Parse(line);
                var root = json.RootElement;
                if (!root.TryGetProperty("kind", out var kindValue) || kindValue.GetString() is not { Length: <= 64 } kind || !TrailKind().IsMatch(kind) || kind.StartsWith("metadata-")) continue;
                if (!root.TryGetProperty("utcMs", out var atValue) || !atValue.TryGetInt64(out var at) || at < oldest || at > nowMs + 60_000) continue;
                var data = new Dictionary<string, string>(StringComparer.Ordinal);
                if (root.TryGetProperty("data", out var fields) && fields.ValueKind == System.Text.Json.JsonValueKind.Object)
                    foreach (var field in fields.EnumerateObject())
                        if (TrailValue(field.Name, field.Value) is { } value && data.Count < 24) data[field.Name] = value;
                var level = kind.EndsWith("-failed") || kind.Contains("-error") ? "error"
                    : kind.EndsWith("-rejected") || kind.EndsWith("-refused") || kind.Contains("timeout") || kind.EndsWith("-unreadable") ? "warning" : "info";
                var crumb = System.Text.Json.JsonSerializer.SerializeToElement(new Dictionary<string, object>
                {
                    ["timestamp"] = DateTimeOffset.FromUnixTimeMilliseconds(at).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    ["message"] = kind, ["category"] = kind.Split('-')[0], ["level"] = level, ["data"] = data,
                });
                kept.Add((at, Breadcrumb.FromJson(crumb)));
            }
            catch (System.Text.Json.JsonException) { }
            catch (InvalidOperationException) { }
        }
        // Newest first, keeping each kind's latest few, then back in order.
        var perKind = new Dictionary<string, int>();
        var latest = new List<Breadcrumb>();
        foreach (var (_, crumb) in kept.OrderBy(entry => entry.At).Reverse())
        {
            if (latest.Count == TrailEntries) break;
            var count = perKind[crumb.Message!] = perKind.GetValueOrDefault(crumb.Message!) + 1;
            if (count <= TrailPerKind) latest.Add(crumb);
        }
        latest.Reverse();
        return latest;
    }
    private static string? TrailValue(string key, System.Text.Json.JsonElement value)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(key, "^[A-Za-z][A-Za-z0-9]{0,39}$")) return null;
        switch (value.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Number: return value.GetRawText();
            case System.Text.Json.JsonValueKind.True: return "true";
            case System.Text.Json.JsonValueKind.False: return "false";
            case System.Text.Json.JsonValueKind.String:
                // A value that was scrubbed (an address, a long id) is left out rather than sent as a placeholder.
                var text = value.GetString();
                return TrailText.Contains(key) && text is { Length: > 0 and <= 80 } && TrailCode().IsMatch(text) && ReportText.Scrub(text) == text ? text : null;
            case System.Text.Json.JsonValueKind.Array:
                if (value.GetArrayLength() is 0 or > 16 || value.EnumerateArray().Any(item => item.ValueKind != System.Text.Json.JsonValueKind.Number)) return null;
                return string.Join(",", value.EnumerateArray().Select(item => item.GetRawText()));
            default: return null;
        }
    }

    public static void Apply(SentryOptions options)
    {
        options.SendDefaultPii = false;
        options.MaxBreadcrumbs = 0;
        options.AutoSessionTracking = false;
        // Counts of dropped events would still reach Sentry after someone turned reports off.
        options.SendClientReports = false;
        // Closing the app waits at most this long for a report still on its way.
        options.ShutdownTimeout = TimeSpan.FromMilliseconds(500);
    }

    public static SentryEvent Scrub(SentryEvent evt)
    {
        Anonymize(evt);
        evt.ServerName = null;
        if (evt.Message is { } message) evt.Message = new SentryMessage { Formatted = ReportText.Scrub(message.Formatted ?? message.Message) };
        foreach (var exception in evt.SentryExceptions ?? [])
        {
            exception.Value = ReportText.Scrub(exception.Value);
            if (exception.Mechanism is { } mechanism)
                foreach (var key in mechanism.Data.Keys.ToArray()) mechanism.Data[key] = ReportText.Scrub(mechanism.Data[key]?.ToString()) ?? "";
            foreach (var frame in exception.Stacktrace?.Frames ?? []) ScrubFrame(frame);
        }
        foreach (var thread in evt.SentryThreads ?? [])
            foreach (var frame in thread.Stacktrace?.Frames ?? []) ScrubFrame(frame);
        foreach (var image in evt.DebugImages ?? [])
        {
            image.CodeFile = FileName(image.CodeFile);
            image.DebugFile = FileName(image.DebugFile);
        }
        return evt;
    }
    public static SentryTransaction Scrub(SentryTransaction transaction) { Anonymize(transaction); return transaction; }

    private static void Anonymize(IEventLike item)
    {
        item.User = new SentryUser();
        if (item.Contexts.Device is { } device) device.Name = null;
        // The path says which endpoint failed; the host is the server's own domain, and the query can hold keys.
        if (item.Request is { } request)
            item.Request = new SentryRequest { Method = request.Method, Url = Uri.TryCreate(request.Url, UriKind.Absolute, out var url) ? url.AbsolutePath : null };
    }
    // Source files keep their place in the repository (src/…), which is what links a frame to the code; anything else
    // keeps only its name.
    private static void ScrubFrame(SentryStackFrame frame)
    {
        frame.FileName = SourcePath(frame.FileName);
        frame.AbsolutePath = SourcePath(frame.AbsolutePath);
    }
    internal static string? SourcePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        var normalized = path.Replace('\\', '/');
        foreach (var root in (string[])["/src/", "/tests/"])
            if (normalized.LastIndexOf(root, StringComparison.Ordinal) is var at and >= 0) return normalized[(at + 1)..];
        return FileName(path);
    }
    private static string? FileName(string? path) => string.IsNullOrEmpty(path) ? path : path[(path.Replace('\\', '/').LastIndexOf('/') + 1)..];
}
