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
        await Trail(check);
    }

    // The diagnostics log's entries as they go with an error: their kinds, numbers and fixed codes, never what names
    // people, their videos, rooms, phones or servers, even when an entry holds them.
    private static async Task Trail(Action<bool, string> check)
    {
        var now = Wire.Now;
        string Line(string kind, object data, long at) => System.Text.Json.JsonSerializer.Serialize(new { kind, monoMs = 1, utcMs = at, process = 1, dropped = 0, data }, Wire.Json);
        var lines = new List<string>
        {
            Line("room-connect-failed", new { room = "ab12cd34", action = "enter", stage = "health", error = "HttpRequestException", code = -2147467259,
                detail = "HttpRequestException/NameResolutionError<-SocketException/HostNotFound", message = "No such host is known. (rooms.private-host.example:443)" }, now - 3000),
            Line("open-file", new { path = @"C:\Users\Ann\Videos\Night Show S01E02.mkv", title = "Night Show", reason = "Night Show S01E02", error = @"C:\Users\Ann\x" }, now - 2500),
            Line("remote-session-closed", new { session = "5f3a9c1e", phone = "Anns-Pixel", reason = "phone-closed", via = "192.168.1.20", ms = 1234, greeted = true }, now - 2000),
            Line("host-command-received", new { sender = "peer-7731", type = "playback", data = "{\"mediaId\":\"m-881\",\"title\":\"Night Show\"}", serverMs = now }, now - 1500),
            Line("metadata-fetch-start", new { Context = new Dictionary<string, int> { ["Night Show"] = 1 }, Data = new { title = "Night Show" } }, now - 1000),
            Line("queue-change", new { action = "add", source = "guest", entries = 4, videos = 2, queues = 3, active = false, sizes = new[] { 1, 2 }, names = new[] { "Night Show" } }, now - 500),
            Line("Night Show", new { count = 1 }, now - 400),
            Line("voice-left", new { why = "left", ms = 6000, state = "0123456789abcdef0123456789" }, now - 300),
            Line("room-failed", new { room = "ab12cd34", reason = "QWERTY room closed", final = true }, now - (long)TimeSpan.FromMinutes(11).TotalMilliseconds),
        };
        var crumbs = CrashReportPrivacy.Trail(lines, now);
        string Text(Breadcrumb crumb) => crumb.Message + " " + string.Join(" ", (crumb.Data ?? new Dictionary<string, string>()).Select(pair => pair.Key + "=" + pair.Value));
        var all = string.Join("\n", crumbs.Select(Text));
        check(crumbs.Select(crumb => crumb.Message).SequenceEqual(["room-connect-failed", "open-file", "remote-session-closed", "host-command-received", "queue-change", "voice-left"]),
            "the app's log keeps its entries from the last ten minutes in order, by kind, without metadata entries or ones whose kind isn't a fixed code: " + string.Join(", ", crumbs.Select(crumb => crumb.Message)));
        var connect = crumbs[0].Data!;
        check(connect.GetValueOrDefault("stage") == "health" && connect.GetValueOrDefault("error") == "HttpRequestException" && connect.GetValueOrDefault("code") == "-2147467259"
            && connect.GetValueOrDefault("detail") == "HttpRequestException/NameResolutionError<-SocketException/HostNotFound" && crumbs[0].Category == "room" && crumbs[0].Level == BreadcrumbLevel.Error,
            "the app's log keeps an error's stage, type, code and error chain: " + Text(crumbs[0]));
        check(crumbs[2].Data!.GetValueOrDefault("ms") == "1234" && crumbs[2].Data!.GetValueOrDefault("greeted") == "true" && crumbs[2].Data!.GetValueOrDefault("reason") == "phone-closed"
            && crumbs[4].Data!.GetValueOrDefault("sizes") == "1,2" && crumbs[4].Data!.GetValueOrDefault("source") == "guest", "the app's log keeps numbers, true/false values and fixed codes");
        var leaks = new[] { "Ann", "Night", "Show", "private-host", "192.168", "peer-7731", "ab12cd34", "5f3a9c1e", "Pixel", "mediaId", "m-881", "QWERTY", "0123456789abcdef", "Users", "message", "path", "title" }
            .Where(text => all.Contains(text, StringComparison.OrdinalIgnoreCase)).ToArray();
        check(leaks.Length == 0, "the app's log leaves out titles, paths, names, ids, addresses, messages and nested values (found: " + string.Join(", ", leaks) + ")");
        var many = Enumerable.Range(0, 300).Select(index => Line($"check-kind{index % 15}", new { index }, now - 300 + index)).ToList();
        var latest = CrashReportPrivacy.Trail(many, now);
        check(latest.Count == CrashReportPrivacy.TrailEntries && latest[0].Data!["index"] == "100" && latest[^1].Data!["index"] == "299", "the app's log sends its latest 200 entries at most");
        var steady = Enumerable.Range(0, 100).Select(index => Line("sample", new { index }, now - 5000 + index)).Append(Line("room-failed", new { final = false }, now - 6000)).ToList();
        var capped = CrashReportPrivacy.Trail(steady, now);
        check(capped.Count == CrashReportPrivacy.TrailPerKind + 1 && capped[0].Message == "room-failed" && capped[1].Data!["index"] == "70" && capped[^1].Data!["index"] == "99",
            "one kind recorded over and over keeps only its latest entries in the app's log, so it can't push the others out");

        // What Sentry would upload: an error carries the entries as breadcrumbs, scrubbed, and nothing more.
        var transport = new KeptTransport();
        using (SentrySdk.Init(options =>
        {
            options.Dsn = "https://00000000000000000000000000000000@127.0.0.1:9/1";
            CrashReportPrivacy.Apply(options);
            options.Transport = transport;
            options.SetBeforeSend(evt =>
            {
                CrashReportPrivacy.Scrub(evt);
                foreach (var crumb in CrashReportPrivacy.Trail(lines, Wire.Now)) evt.AddBreadcrumb(crumb);
                return evt;
            });
        }))
        {
            SentrySdk.AddBreadcrumb("Night Show from the SDK");
            try { Fail(); } catch (Exception ex) { SentrySdk.CaptureException(ex); }
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));
        }
        var sent = string.Join("\n", transport.Sent);
        check(sent.Contains("\"breadcrumbs\":") && sent.Contains("\"message\":\"room-connect-failed\"") && sent.Contains("\"message\":\"voice-left\""),
            "an error report carries the app's log as breadcrumbs");
        var sentLeaks = new[] { "from the SDK", "private-host", "192.168", "peer-7731", "ab12cd34", "Pixel", "m-881", "QWERTY" }.Where(sent.Contains).ToArray();
        check(sentLeaks.Length == 0 && !sent.Contains("Night Show S01E02") && !sent.Contains("\"file\":"),
            "an error report holds no breadcrumb but the app's own, and none of what the log leaves out (found: " + string.Join(", ", sentLeaks) + ")");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Fail() => throw new IOException(@"Could not find file 'C:\Users\Ann\Videos\Night Show S01E02.mkv'.");
    // A Windows account folder anywhere in what is sent.
    private static System.Text.RegularExpressions.Regex Regex() => new(@"(?i)[a-z]:(\\\\|/|\\)users(\\\\|/|\\)");
}
