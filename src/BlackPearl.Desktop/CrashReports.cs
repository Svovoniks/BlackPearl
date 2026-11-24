using System.IO;
using System.Net.Http;
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
    private const string OffFile = "crash-reports-off", FileNamesFile = "crash-reports-file-names", CacheFolder = "crash-reports-cache";
    // The data folder; checks point it at a folder of their own.
    private static string? dataForCheck;
    private static string Data => dataForCheck ?? Program.DataDirectory;
    // Reports wait here, already scrubbed, while Sentry cannot be reached, and go out once it can. One folder per process kind,
    // since the phone host and the auto-download check run beside the app.
    private static string CacheDirectory => Path.Combine(Data, CacheFolder, process);
    // Deletes waiting reports (all of them, or those matching), keeping the folders the SDK writes to. A report the SDK is
    // sending at that moment stays locked; Gate drops it instead.
    private static void DeleteWaiting(Func<string, bool>? matching = null)
    {
        var folder = Path.Combine(Data, CacheFolder);
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder, "*.envelope", SearchOption.AllDirectories))
            try { if (matching is null || matching(File.ReadAllText(file))) File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    // What a report carries when it names files (see MetadataFailed and MetadataRunEnded).
    private static bool NamesFiles(string envelope) => envelope.Contains("metadata-unmatched-files") || envelope.Contains("\"file\":");

    // The SDK's innermost network handler. Reports leave only through it, also those that waited on disk, so it is where
    // turning reports off takes effect at once: while they are off it answers as Sentry would without sending anything, and
    // the SDK then forgets the report. With file names turned off, a waiting report that names files is dropped the same way.
    // A request can wait a while for its connection (Windows retries a refused one for about two seconds), so the choice is
    // checked again once the connection opens: a request that may no longer go fails there, stays on disk, and is dropped by
    // the next attempt.
    private sealed class Gate() : DelegatingHandler(new SocketsHttpHandler
    {
        ConnectCallback = async (context, cancellationToken) =>
        {
            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(context.DnsEndPoint, cancellationToken);
                if (await Drops(context.InitialRequestMessage, cancellationToken)) throw new HttpRequestException("This report may no longer be sent.");
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }
    })
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (await Drops(request, cancellationToken)) return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent("{}") };
            return await base.SendAsync(request, cancellationToken);
        }
        private static async Task<bool> Drops(HttpRequestMessage request, CancellationToken cancellationToken) =>
            turnedOff || !FileNamesAllowed && request.Content is { } content && NamesFiles(await content.ReadAsStringAsync(cancellationToken));
    }
    private static readonly object gate = new();
    private static IDisposable? sdk;
    private static string process = "app";
    private static ITransactionTracer? startup, playback;
    private static ISpan? startupStage;
    private static volatile bool enabled;
    // Set only by Settings (SetAllowed), never by the process ending: what is still queued at exit is still sent.
    private static volatile bool turnedOff;

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
    public static bool Allowed => !File.Exists(Path.Combine(Data, OffFile));
    public static bool Enabled => enabled;
    // Settings › Updates & about › Include names of files that weren't identified: off unless turned on, and only with reports on.
    public static bool FileNamesAllowed => File.Exists(Path.Combine(Data, FileNamesFile));
    public static void SetFileNamesAllowed(bool allowed)
    {
        var path = Path.Combine(Data, FileNamesFile);
        try
        {
            if (!allowed) { File.Delete(path); File.Delete(Path.Combine(Data, UnmatchedSentFile)); }
            else { Directory.CreateDirectory(Data); File.WriteAllText(path, "Crash reports include the names of files that weren't identified (Settings › Updates & about)."); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        // Waiting reports that name files are not sent once names are turned off.
        if (!allowed) DeleteWaiting(NamesFiles);
    }

    // Starts reporting for this process (app, phone-host or follow-check). Disposing it sends what is still queued.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IDisposable Start(string processKind)
    {
        process = processKind;
        if (Available && Allowed) Initialize(Source.Dsn!, AppEnvironment.Get("SENTRY_ENVIRONMENT") ?? (Source.FromEnvironment ? "development" : "production"));
        // Reports turned off: anything left waiting from before goes, if deleting it failed then.
        else if (!Allowed) DeleteWaiting();
        return new Finish();
    }
    private sealed class Finish : IDisposable
    {
        public void Dispose()
        {
            IDisposable? started;
            lock (gate) { started = sdk; sdk = null; enabled = false; startup = playback = null; startupStage = null; }
            try { started?.Dispose(); } catch (Exception) { }
            dataForCheck = null; startedAs = null;
        }
    }

    // Settings › Updates & about › Send crash reports.
    public static void SetAllowed(bool allowed)
    {
        var path = Path.Combine(Data, OffFile);
        try
        {
            if (allowed) File.Delete(path);
            else { Directory.CreateDirectory(Data); File.WriteAllText(path, "Crash reports are turned off in Settings › Updates & about."); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        // Turned off, nothing more leaves (Gate), and reports still waiting for Sentry are deleted rather than sent later.
        turnedOff = !allowed;
        if (!allowed) { enabled = false; HookMetadata(false); lock (gate) { startup = playback = null; startupStage = null; } DeleteWaiting(); }
        // Back on: what still waits is from before turning them off (a report being retried could not be deleted then), so it
        // goes first. The running SDK then resumes as it was started; otherwise it starts now.
        else if (sdk is not null && startedAs is { } running) { DeleteWaiting(); Initialize(running.Dsn, running.Environment); }
        else if (Available) Initialize(Source.Dsn!, AppEnvironment.Get("SENTRY_ENVIRONMENT") ?? (Source.FromEnvironment ? "development" : "production"));
    }

    // A check's own reporting, into its own data folder (CrashReportCacheChecks).
    internal static IDisposable StartForCheck(string dsn, string data)
    {
        dataForCheck = data; process = "check"; turnedOff = false;
        Initialize(dsn, "development");
        return new Finish();
    }

    private static (string Dsn, string Environment)? startedAs;
    private static void Initialize(string dsn, string environment)
    {
        lock (gate)
        {
            enabled = true; startedAs = (dsn, environment);
            if (sdk is not null) { HookMetadata(true); return; }
            var version = typeof(CrashReports).Assembly.GetName().Version;
            sdk = SentrySdk.Init(options =>
            {
                options.Dsn = dsn;
                options.Release = version is null ? null : $"blackpearl-desktop@{version.ToString(3)}";
                options.Environment = environment;
                options.IsGlobalModeEnabled = true;
                options.CacheDirectoryPath = CacheDirectory;
                // Waiting reports are sent in the background, never holding up startup.
                options.InitCacheFlushTimeout = TimeSpan.Zero;
                options.CreateHttpMessageHandler = () => new Gate();
                // Uncompressed, so Gate can tell a report that names files; reports are a few kilobytes.
                options.RequestBodyCompressionLevel = System.IO.Compression.CompressionLevel.NoCompression;
                CrashReportPrivacy.Apply(options);
                // Every trace while developing; a tenth of them from people's apps.
                options.TracesSampleRate = environment == "production" ? 0.1 : 1.0;
                // Every metadata run, so the share of titles left unmatched can be followed; others use the rate above.
                options.TracesSampler = context => context.TransactionContext.Name == MetadataRunName ? 1.0 : null;
                options.SetBeforeSend(evt => enabled ? CrashReportPrivacy.Scrub(evt) : null);
                options.SetBeforeSendTransaction(transaction => enabled ? CrashReportPrivacy.Scrub(transaction) : null);
            });
            SentrySdk.ConfigureScope(scope => scope.SetTag("process", process));
            HookMetadata(true);
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
    // The player's error ends whichever open is current; a failed load passes the open it started.
    public static void PlaybackFailed(object? opening = null)
    {
        if (opening is null) Interlocked.Exchange(ref playback, null)?.Finish(SpanStatus.InternalError);
        else if (opening is ITransactionTracer transaction && Interlocked.CompareExchange(ref playback, null, transaction) == transaction) transaction.Finish(SpanStatus.InternalError);
    }

    // Metadata: provider failures as warnings, and one trace per run with what it looked up and how many titles stayed unmatched.
    private const string MetadataRunName = "metadata.refresh";
    private static readonly Dictionary<string, DateTime> metadataFailures = [];
    private const string UnmatchedSentFile = "crash-reports-unmatched-sent";
    private static DateTime idleRunSent;

    // Metadata reports reach Sentry only while crash reports are on; off, the run summary is not even built.
    private static void HookMetadata(bool on)
    {
        MetadataReports.Starting = on ? () => enabled ? SentrySdk.StartTransaction(MetadataRunName, "metadata") : null : null;
        MetadataReports.Abandoned = on ? (trace, failed) => (trace as ITransactionTracer)?.Finish(failed ? SpanStatus.InternalError : SpanStatus.Cancelled) : null;
        MetadataReports.Failures = on ? MetadataFailed : null;
        MetadataReports.Runs = on ? MetadataRunEnded : null;
    }

    // One report per kind of failure an hour: a provider that is down fails every title, and one report says so.
    private static void MetadataFailed(MetadataFailure failure)
    {
        if (!enabled) return;
        var error = failure.Error;
        var status = error is System.Net.Http.HttpRequestException { StatusCode: { } code } ? ((int)code).ToString() : null;
        var provider = failure.Provider ?? "unknown";
        string[] fingerprint = ["metadata-failure", failure.Stage, provider, error.GetType().FullName ?? "", status ?? ""];
        // The hour is reserved before capturing, so failures that arrive together send one report, and handed back when Sentry's
        // client did not take the report. A report it took is sent, or kept on disk until it can be, unless writing it failed.
        var key = string.Join("|", fingerprint);
        lock (metadataFailures)
        {
            if (metadataFailures.TryGetValue(key, out var at) && DateTime.UtcNow - at < TimeSpan.FromHours(1)) return;
            metadataFailures[key] = DateTime.UtcNow;
        }
        var id = SentrySdk.CaptureException(error, scope =>
        {
            scope.Level = SentryLevel.Warning;
            scope.SetTag("metadata.stage", failure.Stage);
            scope.SetTag("metadata.provider", provider);
            if (status is not null) scope.SetTag("http.status", status);
            scope.SetFingerprint(fingerprint);
            if (FileNamesAllowed && failure.File is { } file) scope.SetExtra("file", file);
        });
        if (id == SentryId.Empty) lock (metadataFailures) metadataFailures.Remove(key);
    }

    private static void MetadataRunEnded(MetadataRun run)
    {
        if (!enabled) return;
        // A run that looked nothing up and nothing failed (most startup and folder-watcher runs) is sent once every six hours,
        // which keeps the library's counts current without a trace for each.
        var idle = run.LookedUp == 0 && run.ProviderFailures == 0 && run.ArtworkFailures == 0;
        if (idle && DateTime.UtcNow - idleRunSent < TimeSpan.FromHours(6)) return;
        if (idle) idleRunSent = DateTime.UtcNow;
        if (run.Trace is ITransactionTracer trace)
        {
            trace.SetTag("metadata.mode", run.Mode);
            trace.SetTag("metadata.providers", run.Providers);
            trace.SetTag("metadata.tmdb", run.TmdbAvailable ? "yes" : "no");
            foreach (var (name, value) in run.Measurements) trace.SetMeasurement(name, value);
            // Sentry keeps ten measurements at most, so the counts per reason go as span data.
            foreach (var (reason, count) in run.UnmatchedReasons) trace.SetData("unmatched." + Regex.Replace(reason.ToLowerInvariant(), "[^a-z0-9]+", "_"), count);
            trace.Finish(run.Completed ? SpanStatus.Ok : SpanStatus.Aborted);
        }
        // With file names allowed: the library's unmatched titles, after a run that looked something up, when the list changed.
        if (!FileNamesAllowed || run.LookedUp == 0 || run.LibraryUnmatched.Count == 0) return;
        var list = string.Join("\n", run.LibraryUnmatched.Select(x => $"{x.Reason}\t{x.Files}\t{x.File}"));
        // The same list is sent once, also across launches; only a hash of it is kept beside the library.
        var sentPath = Path.Combine(Data, UnmatchedSentFile);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(list)));
        try { if (File.Exists(sentPath) && File.ReadAllText(sentPath).Trim() == hash) return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var text = $"{run.LibraryUnmatched.Count} of {run.LibraryTitles} titles unmatched\nreason\tfiles\tfirst file\n{list}\n";
        var id = SentrySdk.CaptureMessage("Unmatched files in the library", scope =>
        {
            scope.SetFingerprint(["metadata-unmatched-files"]);
            scope.SetTag("metadata.library_unmatched", run.LibraryUnmatched.Count.ToString());
            scope.SetExtra("unmatched", string.Join("\n", run.LibraryUnmatched.Take(100).Select(x => $"{x.Reason}  {x.File}")));
            scope.AddAttachment(System.Text.Encoding.UTF8.GetBytes(text), "unmatched-files.txt", AttachmentType.Default, "text/plain");
        }, SentryLevel.Info);
        // Marked as sent only once Sentry's client took the list (see MetadataFailed).
        if (id == SentryId.Empty) return;
        try { File.WriteAllText(sentPath, hash); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
