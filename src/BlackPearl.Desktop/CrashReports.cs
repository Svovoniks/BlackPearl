using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using BlackPearl.Core;
using Sentry;
using Sentry.Protocol;

namespace BlackPearl.Desktop;

// Errors, crashes and timings of startup and playback go to Sentry when the build carries a DSN and the person has not
// turned crash reports off in Settings › Updates. Release builds get the DSN from CI (BLACKPEARL_DESKTOP_SENTRY_DSN at
// build time); BLACKPEARL_SENTRY_DSN sets one at run time, which reports as "development". Builds without either send
// nothing, so checks and local builds never report.
// Reports carry no file paths, web addresses, network addresses, long ids, machine or user names, and no breadcrumbs.
// The switch is a file in the data folder, so it applies before the library opens and in the background processes.
// Program.Main starts this before any WPF type loads; nothing here may touch WPF.
public static class CrashReports
{
    private const string OffFile = "crash-reports-off";
    private static readonly object gate = new();
    private static IDisposable? sdk;
    private static string process = "app";
    private static ITransactionTracer? startup, playback;
    private static volatile bool enabled;

    private static readonly (string? Dsn, bool FromEnvironment) Source = FindDsn();
    private static (string?, bool) FindDsn()
    {
        if (AppEnvironment.Get("SENTRY_DSN") is { Length: > 0 } runtime) return (runtime, true);
        var bundled = typeof(CrashReports).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(x => x.Key == "SentryDsn")?.Value;
        return (string.IsNullOrWhiteSpace(bundled) ? null : bundled.Trim(), false);
    }
    // This build can send reports at all.
    public static bool Available => Source.Dsn is not null;
    // The person's choice, stored beside the library; reports are on unless they turned them off.
    public static bool Allowed => !File.Exists(Path.Combine(Program.DataDirectory, OffFile));
    public static bool Enabled => enabled;

    // Starts reporting for this process (app, phone-host or follow-check). Disposing it sends what is still queued.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IDisposable Start(string processKind)
    {
        process = processKind;
        if (Available && Allowed) Initialize();
        return new Finish();
    }
    private sealed class Finish : IDisposable
    {
        public void Dispose()
        {
            IDisposable? started;
            lock (gate) { started = sdk; sdk = null; enabled = false; }
            try { started?.Dispose(); } catch (Exception) { }
        }
    }

    // Settings › Updates › Send crash reports.
    public static void SetAllowed(bool allowed)
    {
        var path = Path.Combine(Program.DataDirectory, OffFile);
        try
        {
            if (allowed) File.Delete(path);
            else { Directory.CreateDirectory(Program.DataDirectory); File.WriteAllText(path, "Crash reports are turned off in Settings › Updates."); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (!allowed) { enabled = false; lock (gate) { startup = playback = null; } }
        else if (Available) Initialize();
    }

    private static void Initialize()
    {
        lock (gate)
        {
            enabled = true;
            if (sdk is not null) return;
            var version = typeof(CrashReports).Assembly.GetName().Version;
            var environment = AppEnvironment.Get("SENTRY_ENVIRONMENT") ?? (Source.FromEnvironment ? "development" : "production");
            sdk = SentrySdk.Init(options =>
            {
                options.Dsn = Source.Dsn;
                options.Release = version is null ? null : $"blackpearl-desktop@{version.ToString(3)}";
                options.Environment = environment;
                options.IsGlobalModeEnabled = true;
                options.SendDefaultPii = false;
                options.MaxBreadcrumbs = 0;
                options.AutoSessionTracking = false;
                // Every trace while developing; a tenth of them from people's apps.
                options.TracesSampleRate = environment == "production" ? 0.1 : 1.0;
                options.SetBeforeSend(evt => enabled ? Scrubbed(evt) : null);
                options.SetBeforeSendTransaction(transaction => enabled ? Scrubbed(transaction) : null);
            });
            SentrySdk.ConfigureScope(scope => scope.SetTag("process", process));
        }
    }

    // A WPF error the app caught and showed in its "Something went wrong" notice.
    public static void CaptureUiError(Exception exception)
    {
        if (!enabled) return;
        exception.Data[Mechanism.HandledKey] = false;
        exception.Data[Mechanism.MechanismKey] = "Application.DispatcherUnhandledException";
        SentrySdk.CaptureException(exception);
    }
    // Failures the app recovers from but someone should look at.
    public static void Capture(Exception exception, string where)
    {
        if (!enabled) return;
        SentrySdk.CaptureException(exception, scope => scope.SetTag("where", where));
    }

    // Startup, from the process starting to the library on screen. Stages are the ones the diagnostics log records.
    public static void StartupBegan()
    {
        if (!enabled) return;
        startup = SentrySdk.StartTransaction("app.start", "app.start");
    }
    public static void StartupStage(string stage)
    {
        var current = startup;
        if (current is null) return;
        current.StartChild("app.start." + stage).Finish();
        if (stage != "library") return;
        startup = null; current.Finish(SpanStatus.Ok);
    }
    // Opening a video, from the request to the first picture.
    public static void PlaybackOpening(string extension, string source)
    {
        if (!enabled) return;
        playback?.Finish(SpanStatus.Cancelled);
        var transaction = SentrySdk.StartTransaction("playback.open", "playback");
        transaction.SetTag("extension", Regex.IsMatch(extension, "^\\.[a-z0-9]{1,8}$", RegexOptions.IgnoreCase) ? extension.ToLowerInvariant() : "other");
        transaction.SetTag("source", source);
        playback = transaction;
    }
    public static void PlaybackPlaying() => Interlocked.Exchange(ref playback, null)?.Finish(SpanStatus.Ok);
    public static void PlaybackFailed() => Interlocked.Exchange(ref playback, null)?.Finish(SpanStatus.InternalError);

    private static T Anonymized<T>(T item) where T : class, IEventLike
    {
        item.User = new SentryUser();
        if (item.Contexts.Device is { } device) device.Name = null;
        return item;
    }
    private static SentryEvent? Scrubbed(SentryEvent evt)
    {
        Anonymized(evt);
        evt.ServerName = null;
        if (evt.Message is { } message) evt.Message = new SentryMessage { Formatted = ReportText.Scrub(message.Formatted ?? message.Message) };
        foreach (var exception in evt.SentryExceptions ?? [])
        {
            exception.Value = ReportText.Scrub(exception.Value);
            if (exception.Mechanism is { } mechanism)
                foreach (var key in mechanism.Data.Keys.ToArray()) mechanism.Data[key] = ReportText.Scrub(mechanism.Data[key]?.ToString());
        }
        return evt;
    }
    private static SentryTransaction? Scrubbed(SentryTransaction transaction) => Anonymized(transaction);
}
