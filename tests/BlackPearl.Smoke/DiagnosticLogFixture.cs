using System.Text.Json;
using BlackPearl.Core;

static class DiagnosticLogFixture
{
    public static async Task Run(string root, Action<bool, string> check)
    {
        var directory = Path.Combine(root, "rotation");
        await using (var log = new DiagnosticLog(directory, maximumBytes: 1024, retainedFiles: 3))
        {
            log.Record("oversized", new { text = new string('x', 5000) });
            for (var index = 0; index < 60; index++) log.Record("fixture", new { index, detail = new string('x', 100) });
            await log.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var files = Directory.GetFiles(directory);
            check(files.Length == 3 && files.All(path => new FileInfo(path).Length <= 1024), "diagnostics rotate within the configured storage limit");
            var lines = ReadLines(Path.Combine(directory, "diagnostics.jsonl"));
            using var latest = JsonDocument.Parse(lines.Last());
            check(latest.RootElement.GetProperty("data").GetProperty("index").GetInt32() == 59 &&
                latest.RootElement.GetProperty("utcMs").GetInt64() > 0, "flushed diagnostics retain the latest event with timestamps");
            check(files.SelectMany(ReadLines).All(line => { using var json = JsonDocument.Parse(line); return json.RootElement.GetProperty("kind").GetString() == "fixture"; }),
                "rotation retains complete structured records");
        }
        // The latest entries also stay in memory, for crash reports: the newest 256, oldest first.
        await using (var log = new DiagnosticLog(Path.Combine(root, "recent")))
        {
            check(log.Recent().Length == 0, "a new diagnostics log has no recent entries");
            for (var index = 0; index < 300; index++) log.Record("fixture", new { index });
            var recent = log.Recent().Select(line => { using var json = JsonDocument.Parse(line); return json.RootElement.GetProperty("data").GetProperty("index").GetInt32(); }).ToArray();
            check(recent.Length == DiagnosticLog.RecentCapacity && recent[0] == 300 - DiagnosticLog.RecentCapacity && recent[^1] == 299 && recent.SequenceEqual(recent.Order()),
                "the diagnostics log keeps its latest 256 entries in memory, oldest first");
        }
        check(PlaybackDiagnostics.Describe(new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known. (example.org:443)", new System.Net.Sockets.SocketException(11001)))
            == "HttpRequestException/NameResolutionError<-SocketException/HostNotFound", "a failure is described by its error codes, without its message");
        var blocked = Path.Combine(root, "not-a-directory"); File.WriteAllText(blocked, "fixture");
        await using (var log = new DiagnosticLog(blocked))
        {
            log.Record("fixture", new { test = true });
            await log.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
            check(true, "unwritable diagnostic directory cannot interrupt or hang playback");
        }
        // Crash reports carry error text only after ReportText takes out what identifies people, their files and servers.
        check(ReportText.Scrub(@"Could not find file 'C:\Users\Ann Lee\Videos\Night Show S01E02.mkv'.") == "Could not find file '<path>'.", "crash reports drop Windows file paths");
        check(ReportText.Scrub(@"Access to the path '\\nas\media\Films\Heat (1995).mkv' is denied.") == "Access to the path '<path>' is denied.", "crash reports drop network share paths");
        check(ReportText.Scrub("Failed /home/ann/films/x.mkv") == "Failed <path>", "crash reports drop Unix paths");
        check(ReportText.Scrub("GET https://rooms.example.org/remote?key=abc failed") == "GET <url> failed", "crash reports drop web addresses with their queries");
        check(ReportText.Scrub("No route to 192.168.1.20:5000 or fe80::1c2b:3a4d") == "No route to <address> or <address>", "crash reports drop network addresses");
        check(ReportText.Scrub("Invitation 0123456789ABCDEF01234567 expired") == "Invitation <id> expired", "crash reports drop invitations and keys");
        check(ReportText.Scrub("Index was outside the bounds of the array.") == "Index was outside the bounds of the array." && ReportText.Scrub("Took 12:34:56") == "Took 12:34:56",
            "crash reports keep ordinary error text and times");
    }
    private static string[] ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }
}
