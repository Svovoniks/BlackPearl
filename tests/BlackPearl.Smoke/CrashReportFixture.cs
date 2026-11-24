using System.Runtime.CompilerServices;
using System.Text;
using BlackPearl.Core;
using Sentry;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;

// What a crash report actually sends, read from the envelope Sentry would upload: the SDK adds the list of loaded files
// and stack frames with their paths after BlackPearl's own text scrubbing, so checking ReportText alone missed them.
static class CrashReportFixture
{
    sealed class KeptTransport : ITransport
    {
        public List<string> Sent { get; } = [];
        public async Task SendEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken = default)
        {
            using var stream = new MemoryStream();
            await envelope.SerializeAsync(stream, null, cancellationToken);
            lock (Sent) Sent.Add(Encoding.UTF8.GetString(stream.ToArray()));
        }
    }

    public static async Task Run(Action<bool, string> check)
    {
        var defaults = new SentryOptions(); CrashReportPrivacy.Apply(defaults);
        check(!defaults.SendDefaultPii && defaults.MaxBreadcrumbs == 0 && !defaults.SendClientReports && defaults.ShutdownTimeout <= TimeSpan.FromSeconds(1),
            "crash reports send no personal defaults, breadcrumbs or client reports, and never hold up closing for long");
        var transport = new KeptTransport();
        using (SentrySdk.Init(options =>
        {
            options.Dsn = "https://00000000000000000000000000000000@127.0.0.1:9/1";
            CrashReportPrivacy.Apply(options);
            options.Transport = transport;
            options.TracesSampleRate = 1.0;
            options.SetBeforeSend(evt => CrashReportPrivacy.Scrub(evt));
            options.SetBeforeSendTransaction(transaction => CrashReportPrivacy.Scrub(transaction));
        }))
        {
            try { Fail(); } catch (Exception ex) { SentrySdk.CaptureException(ex); }
            SentrySdk.StartTransaction("crash-report-check", "check").Finish();
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));
        }
        var sent = string.Join("\n", transport.Sent);
        check(sent.Contains("\"debug_meta\"") && sent.Contains("\"code_file\":\"BlackPearl.Smoke.dll\""), "crash reports name loaded files without their folders");
        check(sent.Contains("tests/BlackPearl.Smoke/CrashReportFixture.cs"), "crash report stack frames keep their place in the repository");
        check(System.Text.RegularExpressions.Regex.Unescape(sent).Contains("Could not find file '<path>'"), "crash reports scrub paths from error text");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var leaks = new[] { profile, profile.Replace("\\", "\\\\"), profile.Replace('\\', '/'), Environment.MachineName, "Ann\\\\Videos" }
            .Where(text => text.Length > 0 && sent.Contains(text, StringComparison.OrdinalIgnoreCase)).ToArray();
        check(leaks.Length == 0 && !Regex().IsMatch(sent), "crash reports carry no user folder, account or machine name (found: " + string.Join(", ", leaks) + ")");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Fail() => throw new IOException(@"Could not find file 'C:\Users\Ann\Videos\Night Show S01E02.mkv'.");
    // A Windows account folder anywhere in what is sent.
    private static System.Text.RegularExpressions.Regex Regex() => new(@"(?i)[a-z]:(\\\\|/|\\)users(\\\\|/|\\)");
}
