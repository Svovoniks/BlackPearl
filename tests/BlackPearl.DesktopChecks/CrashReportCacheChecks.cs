using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using BlackPearl.Desktop;
using Sentry;

// Crash reports while Sentry cannot be reached: they wait on disk and go out later, turning reports off stops even those
// already waiting, turning them back on in the same session still works, and turning file names off drops waiting reports
// that name files. Sentry is played by a local listener that is closed ("down") or accepting ("up").
static class CrashReportCacheChecks
{
    // A minimal HTTP endpoint that keeps every request body it receives.
    sealed class FakeSentry : IDisposable
    {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource stop = new();
        public List<string> Bodies { get; } = [];
        private static int count; private readonly int id = ++count;
        public FakeSentry(int port)
        {
            listener = new TcpListener(IPAddress.Loopback, port); listener.Start();
            _ = Task.Run(async () => { while (!stop.IsCancellationRequested) { try { _ = Serve(await listener.AcceptTcpClientAsync(stop.Token)); } catch (Exception) { return; } } });
        }
        private async Task Serve(TcpClient client)
        {
            using var _ = client; var stream = client.GetStream(); var raw = new MemoryStream(); var buffer = new byte[65536];
            int headerEnd;
            while ((headerEnd = IndexOf(raw, "\r\n\r\n"u8)) < 0) { var n = await stream.ReadAsync(buffer); if (n == 0) return; raw.Write(buffer, 0, n); }
            var headers = Encoding.ASCII.GetString(raw.GetBuffer(), 0, headerEnd).ToLowerInvariant();
            var body = new MemoryStream(); body.Write(raw.GetBuffer(), headerEnd + 4, (int)raw.Length - headerEnd - 4);
            if (headers.Contains("transfer-encoding: chunked"))
            {
                while (!EndsWith(body, "0\r\n\r\n"u8)) { var n = await stream.ReadAsync(buffer); if (n == 0) break; body.Write(buffer, 0, n); }
                body = Dechunk(body.ToArray());
            }
            else if (System.Text.RegularExpressions.Regex.Match(headers, @"content-length: *(\d+)") is { Success: true } length)
                while (body.Length < long.Parse(length.Groups[1].Value)) { var n = await stream.ReadAsync(buffer); if (n == 0) break; body.Write(buffer, 0, n); }
            lock (Bodies) Bodies.Add(Encoding.UTF8.GetString(body.ToArray())); Log($"server {id} received {(Encoding.UTF8.GetString(body.ToArray()) is var b && b.Contains("cache-check") ? System.Text.RegularExpressions.Regex.Match(b, "cache-check[a-z ]+").Value : "other: " + System.Text.RegularExpressions.Regex.Match(b, "\"type\":\"[a-z_]+\"").Value + " names=" + b.Contains("S01E01"))}");
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}"u8.ToArray());
        }
        private static int IndexOf(MemoryStream data, ReadOnlySpan<byte> marker) => data.GetBuffer().AsSpan(0, (int)data.Length).IndexOf(marker);
        private static bool EndsWith(MemoryStream data, ReadOnlySpan<byte> marker) => data.GetBuffer().AsSpan(0, (int)data.Length).EndsWith(marker);
        private static MemoryStream Dechunk(byte[] data)
        {
            var output = new MemoryStream(); var at = 0;
            while (at < data.Length)
            {
                var line = data.AsSpan(at).IndexOf("\r\n"u8); if (line < 0) break;
                var size = Convert.ToInt32(Encoding.ASCII.GetString(data, at, line).Split(';')[0].Trim(), 16); at += line + 2;
                if (size == 0) break;
                output.Write(data, at, size); at += size + 2;
            }
            return output;
        }
        public bool Received(string text) { lock (Bodies) return Bodies.Any(x => x.Contains(text)); }
        public void Dispose() { stop.Cancel(); listener.Stop(); Log($"server {id} down"); }
    }

    static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    static void Log(string text) => Console.WriteLine($"  [{clock.ElapsedMilliseconds,6} ms] {text}");
    static bool Until(Func<bool> condition, int seconds)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end) { if (condition()) return true; Thread.Sleep(250); }
        return condition();
    }

    public static void Run(Action<bool, string> check)
    {
        var data = Path.Combine(Path.GetTempPath(), "blackpearl-crash-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        int Waiting() => Directory.Exists(Path.Combine(data, "crash-reports-cache")) ? Directory.EnumerateFiles(Path.Combine(data, "crash-reports-cache"), "*.envelope", SearchOption.AllDirectories).Count() : 0;
        try
        {
            using (CrashReports.StartForCheck($"http://0123456789abcdef0123456789abcdef@127.0.0.1:{port}/1", data))
            {
                // Sentry down: a report waits on disk, then goes out once Sentry is back.
                SentrySdk.CaptureMessage("cache-check waits");
                check(Until(() => Waiting() > 0, 15), "a report waits on disk while Sentry cannot be reached");
                // Settings sets both switches to their saved values as it opens (reports on, file names off): no change. Several
                // reports wait, since the one the SDK is retrying is locked and would survive a delete anyway.
                SentrySdk.CaptureMessage("cache-check settings one"); SentrySdk.CaptureMessage("cache-check settings two");
                Until(() => Waiting() >= 3, 15);
                var before = Waiting();
                CrashReports.SetAllowed(true); CrashReports.SetFileNamesAllowed(false);
                check(before >= 3 && Waiting() == before, $"opening Settings with reports on keeps the reports waiting on disk ({before} before, {Waiting()} after)");
                using (var sentry = new FakeSentry(port))
                    check(Until(() => sentry.Received("cache-check waits") && sentry.Received("cache-check settings one") && sentry.Received("cache-check settings two"), 30),
                        "waiting reports go out once Sentry is reachable again");

                // Sentry down, reports turned off: the waiting report is never sent, also once Sentry is back.
                SentrySdk.CaptureMessage("cache-check turned off");
                Until(() => Waiting() > 0, 15); Log($"waiting before off: {Waiting()}");
                CrashReports.SetAllowed(false); Log($"turned off, waiting: {Waiting()}");
                using (var sentry = new FakeSentry(port))
                {
                    Log("sentry up");
                    Thread.Sleep(12000);
                    check(!sentry.Received("cache-check turned off"),
                        $"turning reports off stops a report already waiting for Sentry ({Waiting()} still on disk)");
                }

                // Back on in the same session: reports wait and go out again.
                CrashReports.SetAllowed(true);
                SentrySdk.CaptureMessage("cache-check back on");
                check(Until(() => Waiting() > 0, 15), "after turning reports off and on, a report waits on disk again");
                using (var sentry = new FakeSentry(port))
                {
                    check(Until(() => sentry.Received("cache-check back on"), 30), "after turning reports off and on, waiting reports go out again");
                    Thread.Sleep(3000);
                    check(!sentry.Received("cache-check turned off"), "a report from before turning reports off is not sent after turning them back on");
                }

                // File names turned off: a waiting report that names a file is dropped, and one that does not still goes.
                CrashReports.SetFileNamesAllowed(true);
                SentrySdk.CaptureMessage("cache-check names a file", scope => scope.SetExtra("file", "Shows/Some Show/S01E01.mkv"));
                SentrySdk.CaptureMessage("cache-check plain");
                Until(() => Waiting() >= 2, 15);
                CrashReports.SetFileNamesAllowed(false);
                using (var sentry = new FakeSentry(port))
                {
                    check(Until(() => sentry.Received("cache-check plain"), 30), "with file names turned off, a waiting report without file names still goes out");
                    Thread.Sleep(3000);
                    check(!sentry.Received("cache-check names a file") && !sentry.Received("S01E01.mkv"), "with file names turned off, a waiting report that names a file is never sent");
                }

                // The app's log: an error carries the diagnostics log's latest entries, reduced to kinds, numbers and codes.
                BlackPearl.Core.PlaybackDiagnostics.Initialize(Path.Combine(data, "logs"));
                BlackPearl.Core.PlaybackDiagnostics.Record("room-connect-failed", new { stage = "health", error = "HttpRequestException", path = @"C:\Users\Ann\Videos\Night Show S01E02.mkv", name = "Ann" });
                // A subtitle file's language, as track detection records it; file names are still off here.
                BlackPearl.Core.PlaybackDiagnostics.Record("track-language", new { file = true, language = "en", confidence = 0.9 });
                check(!CrashReports.FileNamesAllowed, "the app's log is checked with file names off");
                check(CrashReports.LogAllowed, "the app's log goes with crash reports unless it is turned off");
                SentrySdk.CaptureMessage("cache-check with log", SentryLevel.Error);
                SentrySdk.CaptureMessage("cache-check info without log");
                Until(() => Waiting() >= 2, 15);
                using (var sentry = new FakeSentry(port))
                {
                    check(Until(() => sentry.Received("cache-check with log") && sentry.Received("cache-check info without log"), 30), "reports with and without the app's log go out, with file names off and a log entry about a subtitle file");
                    string Body(string text) { lock (sentry.Bodies) return sentry.Bodies.First(body => body.Contains(text)); }
                    var withLog = Body("cache-check with log");
                    check(withLog.Contains("\"breadcrumbs\":") && withLog.Contains("room-connect-failed") && withLog.Contains("\"stage\":\"health\""), "an error report carries the app's log");
                    check(!withLog.Contains("Night Show") && !withLog.Contains("\"name\":\"Ann\"") && !withLog.Contains("\"path\""), "the app's log leaves out paths and names an entry holds");
                    check(!Body("cache-check info without log").Contains("\"breadcrumbs\":"), "an informational report carries no log");
                }
                // Turned off: a waiting report that holds the log is dropped, and new ones carry none.
                SentrySdk.CaptureMessage("cache-check log waiting", SentryLevel.Error);
                Until(() => Waiting() > 0, 15);
                CrashReports.SetLogAllowed(false);
                check(!CrashReports.LogAllowed, "the app's log can be turned off");
                SentrySdk.CaptureMessage("cache-check log off", SentryLevel.Error);
                Until(() => Waiting() > 0, 15);
                using (var sentry = new FakeSentry(port))
                {
                    check(Until(() => sentry.Received("cache-check log off"), 30), "with the app's log off, error reports still go out");
                    Thread.Sleep(3000);
                    check(!sentry.Received("cache-check log waiting"), "with the app's log turned off, a waiting report that holds it is never sent");
                    lock (sentry.Bodies) check(!sentry.Bodies.Any(body => body.Contains("cache-check log off") && body.Contains("\"breadcrumbs\":")), "with the app's log off, error reports carry no log");
                }
                CrashReports.SetLogAllowed(true);
                check(CrashReports.LogAllowed, "the app's log can be turned back on");

                // Metadata lookups (CrashReports.MetadataFailed): one that never reached its provider is reported only while Windows
                // says the PC has the internet, or can't say; a provider that answered with an error is reported either way.
                var windows = InternetConnection.Ask();
                check(windows is not null, $"Windows answers whether this PC has the internet ({(windows is null ? "no answer" : windows.Value ? "online" : "offline")})");
                static Exception Dns(string text) => new HttpRequestException(HttpRequestError.NameResolutionError, text, new SocketException(11001));
                var refused = new HttpRequestException(HttpRequestError.ConnectionError, "offline-check refused while offline", new SocketException(10061));
                var unavailable = new HttpRequestException("offline-check unavailable while offline", null, HttpStatusCode.ServiceUnavailable);
                var timedOut = new TaskCanceledException("offline-check timed out while offline", new TimeoutException());
                check(InternetConnection.Unreached(Dns("x")) && InternetConnection.Unreached(refused) && InternetConnection.Unreached(timedOut) &&
                    !InternetConnection.Unreached(unavailable) && !InternetConnection.Unreached(new System.Text.Json.JsonException("Wikipedia returned an API error")),
                    "a lookup whose name didn't resolve, whose connection was refused or that timed out never reached its provider; one answered with an error did");
                using (var sentry = new FakeSentry(port))
                {
                    var report = BlackPearl.Core.MetadataReports.Failures;
                    check(report is not null, "metadata failures are hooked up while crash reports are on");
                    try
                    {
                        InternetConnection.AnswerForCheck = () => false;
                        report!(new("offline-check-dns", "tmdb", Dns("offline-check dns while offline"), null));
                        report(new("offline-check-refused", "tvmaze", refused, null));
                        report(new("offline-check-timeout", "tvmaze", timedOut, null));
                        report(new("offline-check-status", "tmdb", unavailable, null));
                        // The same kind of failure once the PC is back online: the report left out above kept no hour for it.
                        InternetConnection.AnswerForCheck = () => true;
                        report(new("offline-check-dns", "tmdb", Dns("offline-check dns while online"), null));
                        InternetConnection.AnswerForCheck = () => null;
                        report(new("offline-check-unknown", "tmdb", Dns("offline-check dns while unknown"), null));
                    }
                    finally { InternetConnection.AnswerForCheck = null; }
                    check(Until(() => sentry.Received("offline-check unavailable while offline") && sentry.Received("offline-check dns while online") &&
                        sentry.Received("offline-check dns while unknown"), 30),
                        "a provider's error while offline, and a failed name while online or when Windows can't say, are reported");
                    Thread.Sleep(3000);
                    check(!sentry.Received("offline-check dns while offline") && !sentry.Received("offline-check refused while offline") &&
                        !sentry.Received("offline-check timed out while offline"),
                        "a lookup that never reached its provider while the PC is offline is not reported");
                }

                // Phone links (PHONE-REMOTE.md, "Measuring the link"): a link's setup and its stretches go out as traces, with
                // their stages and numbers and nothing that names the phone or the PC.
                using (var sentry = new FakeSentry(port))
                {
                    var connect = BlackPearl.Core.PhoneLinkReports.Connecting?.Invoke();
                    check(connect is not null && BlackPearl.Core.PhoneLinkReports.WindowStarting is not null && BlackPearl.Core.PhoneLinkReports.Windows is not null,
                        "phone link reports are hooked up while crash reports are on");
                    BlackPearl.Core.PhoneLinkReports.ConnectStage!(connect!, "connected");
                    BlackPearl.Core.PhoneLinkReports.ConnectStage!(connect!, "channel");
                    BlackPearl.Core.PhoneLinkReports.Connects!(new("greeted", false, "server", "relay", "0.13.0", "host:2 srflx:1 prflx:0 relay:1", "host:1 srflx:1 prflx:0 relay:1", connect));
                    // Native threads can tell the data channel opening before ICE connecting: the "ice" stage then covers both.
                    var early = BlackPearl.Core.PhoneLinkReports.Connecting!.Invoke()!;
                    BlackPearl.Core.PhoneLinkReports.ConnectStage!(early, "channel");
                    BlackPearl.Core.PhoneLinkReports.ConnectStage!(early, "connected");
                    BlackPearl.Core.PhoneLinkReports.Connects!(new("failed", true, "lan", "direct", null, "", "", early));
                    var window = BlackPearl.Core.PhoneLinkReports.WindowStarting!.Invoke();
                    BlackPearl.Core.PhoneLinkReports.Windows!(new("relay", "server", "0.13.0", "removed", TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(25),
                        60, 58, 2, 48.5, 91.2, 300, 5_000_000, 20, 1, 12, 40, window));
                    check(Until(() => sentry.Received("\"transaction\":\"phone.connect\"") && sentry.Received("\"transaction\":\"phone.link\""), 30), "a phone link's setup and stretches go out as traces");
                    string Body(string text) { lock (sentry.Bodies) return sentry.Bodies.First(body => body.Contains(text)); }
                    check(Until(() => sentry.Received("\"phone.outcome\":\"failed\""), 30), "a link that failed before its hello goes out too");
                    string[] Stages(string body) => System.Text.RegularExpressions.Regex.Matches(body, "\"op\":\"phone.connect.stage\",\"description\":\"([a-z]+)\"").Select(m => m.Groups[1].Value).ToArray();
                    var setup = Body("\"phone.outcome\":\"greeted\"");
                    var failed = Body("\"phone.outcome\":\"failed\"");
                    check(setup.Contains("\"phone.path\":\"relay\"") && setup.Contains("\"phone.via\":\"server\"") && setup.Contains("\"phone.app\":\"0.13.0\"") && Stages(setup).SequenceEqual(["ice", "channel", "hello"]),
                        $"a link's setup goes with its path, the phone app's version, and a span for each stage ({string.Join(",", Stages(setup))})");
                    check(Stages(failed).SequenceEqual(["ice", "hello"]) && failed.Contains("\"status\":\"unavailable\"") && failed.Contains("\"phone.pairing\":\"yes\""),
                        $"a setup whose stages came out of order keeps them in order, and a failed one says so ({string.Join(",", Stages(failed))})");
                    var link = Body("\"transaction\":\"phone.link\"");
                    check(link.Contains("\"rtt\":{\"value\":48.5,\"unit\":\"millisecond\"}") && link.Contains("\"media_bytes\":{\"value\":5000000,\"unit\":\"byte\"}") &&
                        link.Contains("\"pings_unanswered\":{\"value\":2") && link.Contains("\"phone.end\":\"removed\""),
                        $"a link's stretch goes with its round trips, reads and bytes as measurements ({System.Text.RegularExpressions.Regex.Match(link, "\"measurements\":\\{[^}]*\\}[^}]*\\}").Value})");
                }
                CrashReports.SetAllowed(false);
                check(BlackPearl.Core.PhoneLinkReports.Connecting is null && BlackPearl.Core.PhoneLinkReports.Windows is null, "turning crash reports off unhooks phone link reports");
                CrashReports.SetAllowed(true);
                check(BlackPearl.Core.PhoneLinkReports.Connecting is not null, "turning them back on hooks them up again");
            }
            // Closing the process right after a report: what is queued is still sent, or stays on disk if the 500 ms
            // shutdown runs out, but is never deleted unsent.
            foreach (var file in Directory.EnumerateFiles(Path.Combine(data, "crash-reports-cache"), "*.envelope", SearchOption.AllDirectories)) File.Delete(file);
            using (var sentry = new FakeSentry(port))
            {
                using (CrashReports.StartForCheck($"http://0123456789abcdef0123456789abcdef@127.0.0.1:{port}/1", data))
                    SentrySdk.CaptureMessage("cache-check at exit");
                var sent = Until(() => sentry.Received("cache-check at exit"), 3);
                var kept = Waiting();
                check(sent || kept > 0, $"a report queued when the process closes is sent or kept, never deleted unsent (sent {sent}, kept {kept})");
            }
        }
        finally { try { Directory.Delete(data, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }
}
