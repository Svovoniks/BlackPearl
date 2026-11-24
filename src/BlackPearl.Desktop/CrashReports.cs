using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using BlackPearl.Core;
using Sentry;
using Sentry.Protocol;

namespace BlackPearl.Desktop;

// Errors, crashes and timings of startup and playback go to Sentry when the build carries a DSN and the person has not
// turned crash reports off in Settings › Updates & about. Release builds get the DSN from CI (BLACKPEARL_DESKTOP_SENTRY_DSN
// at build time); BLACKPEARL_SENTRY_DSN sets one at run time, which reports as "development". Builds without either send
// nothing, so checks and local builds never report. CrashReportPrivacy (Core) decides what reports leave out.
// The switch is a file in the data folder, so it applies before the library opens and in the background processes.
// Program.Main starts this before any WPF type loads; nothing here may touch WPF.
public static class CrashReports
{
    private const string OffFile = "crash-reports-off";
    private static readonly object gate = new();
    private static IDisposable? sdk;
    private static string process = "app";
    private static ITransactionTracer? startup, playback;
    private static ISpan? startupStage;
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
        if (Available && Allowed) Initialize(Source.Dsn!, AppEnvironment.Get("SENTRY_ENVIRONMENT") ?? (Source.FromEnvironment ? "development" : "production"));
        return new Finish();
    }
    private sealed class Finish : IDisposable
    {
        public void Dispose()
        {
            IDisposable? started;
            lock (gate) { started = sdk; sdk = null; enabled = false; startup = playback = null; startupStage = null; }
            try { started?.Dispose(); } catch (Exception) { }
        }
    }

    // Settings › Updates & about › Send crash reports.
    public static void SetAllowed(bool allowed)
    {
        var path = Path.Combine(Program.DataDirectory, OffFile);
        try
        {
            if (allowed) File.Delete(path);
            else { Directory.CreateDirectory(Program.DataDirectory); File.WriteAllText(path, "Crash reports are turned off in Settings › Updates & about."); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (!allowed) { enabled = false; lock (gate) { startup = playback = null; startupStage = null; } }
        else if (Available) Initialize(Source.Dsn!, AppEnvironment.Get("SENTRY_ENVIRONMENT") ?? (Source.FromEnvironment ? "development" : "production"));
    }

    private static void Initialize(string dsn, string environment)
    {
        lock (gate)
        {
            enabled = true;
            if (sdk is not null) return;
            var version = typeof(CrashReports).Assembly.GetName().Version;
            sdk = SentrySdk.Init(options =>
            {
                options.Dsn = dsn;
                options.Release = version is null ? null : $"blackpearl-desktop@{version.ToString(3)}";
                options.Environment = environment;
                options.IsGlobalModeEnabled = true;
                CrashReportPrivacy.Apply(options);
                // Every trace while developing; a tenth of them from people's apps.
                options.TracesSampleRate = environment == "production" ? 0.1 : 1.0;
                options.SetBeforeSend(evt => enabled ? CrashReportPrivacy.Scrub(evt) : null);
                options.SetBeforeSendTransaction(transaction => enabled ? CrashReportPrivacy.Scrub(transaction) : null);
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

    // Startup, from crash reporting starting (shortly after the process does) to the library on screen. Each span covers
    // the time up to a stage the diagnostics log records: the window built, then loaded, then the library shown.
    public static void StartupBegan()
    {
        if (!enabled) return;
        startup = SentrySdk.StartTransaction("app.start", "app.start");
        startupStage = startup.StartChild("app.start.stage", "window");
    }
    public static void StartupStage(string stage)
    {
        var current = startup;
        if (current is null || startupStage is not { } span || span.Description != stage) return;
        span.Finish(SpanStatus.Ok);
        if (stage == "library") { startup = null; startupStage = null; current.Finish(SpanStatus.Ok); return; }
        startupStage = current.StartChild("app.start.stage", stage == "window" ? "loaded" : "library");
    }

    // Opening a video, from the request to the first picture. The caller keeps what this returns and passes it back if
    // it gives up before asking the player to play, so a later video's Playing never closes it.
    public static object? PlaybackOpening(string extension, string source)
    {
        if (!enabled) return null;
        var transaction = SentrySdk.StartTransaction("playback.open", "playback");
        transaction.SetTag("extension", Regex.IsMatch(extension, "^\\.[a-z0-9]{1,8}$", RegexOptions.IgnoreCase) ? extension.ToLowerInvariant() : "other");
        transaction.SetTag("source", source);
        Interlocked.Exchange(ref playback, transaction)?.Finish(SpanStatus.Cancelled);
        return transaction;
    }
    public static void PlaybackAbandoned(object? opening)
    {
        if (opening is ITransactionTracer transaction && Interlocked.CompareExchange(ref playback, null, transaction) == transaction) transaction.Finish(SpanStatus.Cancelled);
    }
    public static void PlaybackPlaying() => Interlocked.Exchange(ref playback, null)?.Finish(SpanStatus.Ok);
    public static void PlaybackFailed() => Interlocked.Exchange(ref playback, null)?.Finish(SpanStatus.InternalError);
}
