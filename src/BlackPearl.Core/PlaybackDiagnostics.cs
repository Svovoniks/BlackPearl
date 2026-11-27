namespace BlackPearl.Core;

public static class PlaybackDiagnostics
{
    private static readonly object gate = new();
    private static DiagnosticLog? log;
    private static readonly string? overrideDirectory = AppEnvironment.Get("DIAGNOSTICS");
    static PlaybackDiagnostics() { if (!string.IsNullOrWhiteSpace(overrideDirectory)) Initialize(overrideDirectory); }
    public static bool Enabled => log is not null;
    // Per-frame and per-tick records (positions, samples, range reads, buffering, settling, clock) are only
    // kept when diagnostics are redirected for a test run or BLACKPEARL_DIAGNOSTICS_DETAIL=1 is set.
    public static bool Detailed => Enabled && detailed;
    private static readonly bool detailed = !string.IsNullOrWhiteSpace(overrideDirectory) || AppEnvironment.Get("DIAGNOSTICS_DETAIL") == "1";
    public static string? DirectoryPath => log?.DirectoryPath;
    public static void Initialize(string directory)
    {
        lock (gate)
        {
            try { log ??= new DiagnosticLog(string.IsNullOrWhiteSpace(overrideDirectory) ? directory : overrideDirectory); }
            catch { /* Diagnostics must not prevent startup. */ }
        }
    }
    public static void Record(string kind, object data) => log?.Record(kind, data);
    public static Task FlushAsync() => log?.FlushAsync() ?? Task.CompletedTask;
    // The latest entries this process recorded, as JSON lines, oldest first.
    public static string[] Recent() => log?.Recent() ?? [];

    // A failure that explains why something didn't work: the exception's type, its HResult, the chain of inner exceptions
    // with their socket, web socket or HTTP error codes, and with message, its text (scrubbed, and cut short). The text is
    // only for errors whose message says something the type doesn't, such as a refused connection; it stays in this log.
    public static void Failed(string kind, Exception error, object? data = null, bool message = false)
    {
        if (log is not { } current) return;
        var fields = new Dictionary<string, object?>();
        if (data is not null)
            foreach (var property in System.Text.Json.JsonSerializer.SerializeToElement(data, Wire.Json).EnumerateObject()) fields[property.Name] = property.Value;
        fields["error"] = error.GetType().Name;
        fields["code"] = error.HResult;
        if (error.InnerException is not null || error is System.Net.WebSockets.WebSocketException or System.Net.Sockets.SocketException or HttpRequestException)
            fields["detail"] = Describe(error);
        if (message && ReportText.Scrub(error.Message) is { Length: > 0 } text) fields["message"] = text.Length > 160 ? text[..160] + "…" : text;
        current.Record(kind, fields);
    }
    // The exception and up to two inner ones, with the error code each carries, such as
    // "HttpRequestException/ConnectionError<-SocketException/ConnectionRefused".
    public static string Describe(Exception error)
    {
        var parts = new List<string>();
        for (var current = error; current is not null && parts.Count < 3; current = current.InnerException)
            parts.Add(current switch
            {
                System.Net.WebSockets.WebSocketException socket => $"{socket.GetType().Name}/{socket.WebSocketErrorCode}",
                System.Net.Sockets.SocketException socket => $"{socket.GetType().Name}/{socket.SocketErrorCode}",
                HttpRequestException http when http.HttpRequestError != HttpRequestError.Unknown => $"{http.GetType().Name}/{http.HttpRequestError}",
                HttpRequestException { StatusCode: { } status } http => $"{http.GetType().Name}/{(int)status}",
                _ => current.GetType().Name,
            });
        return string.Join("<-", parts);
    }
}
