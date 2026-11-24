using Sentry;

namespace BlackPearl.Core;

/// <summary>
/// What every crash report from BlackPearl leaves out, for the desktop app and the room server alike: who sent it (user,
/// machine and device names, addresses), where things are on that computer (file paths in text, stack frames and the
/// list of loaded files, which name the Windows account), request details beyond the endpoint's path, breadcrumbs, and
/// reports about reports. Error text goes through <see cref="ReportText"/>.
/// </summary>
public static class CrashReportPrivacy
{
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
                foreach (var key in mechanism.Data.Keys.ToArray()) mechanism.Data[key] = ReportText.Scrub(mechanism.Data[key]?.ToString());
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
