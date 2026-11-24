using System.IO;
using System.Net;
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
