using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace BlackPearl.Core;

public sealed class MediaBridge : IAsyncDisposable
{
    private WebApplication? app;
    private readonly string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private CachedMediaSource? source;
    private long diagnosticReads;
    private readonly CancellationTokenSource shutdown = new();
    private int stopped;
    public Uri? Url { get; private set; }
    private RemoteStream? stream;
    // With a cache file the video is buffered to the end of the movie; without one, in memory within a window.
    public async Task<Uri> StartAsync(IMediaSource media, StreamCache? cache = null)
    {
        source = new CachedMediaSource(media, disk: cache);
        await Listen();
        return Url = new Uri(app!.Urls.Single() + $"/{token}/media");
    }
    // A converted stream is served as a playlist of its pieces, which the player fetches one at a time.
    public async Task<Uri> StartAsync(RemoteStream converted)
    {
        stream = converted;
        await Listen();
        return Url = new Uri(app!.Urls.Single() + $"/{token}/stream.m3u8");
    }
    private async Task Listen()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 0));
        app = builder.Build();
        app.MapMethods("/{secret}/media", ["GET", "HEAD"], Serve);
        app.MapGet("/{secret}/stream.m3u8", ServePlaylist);
        app.MapGet("/{secret}/piece/{index:int}.ts", ServePiece);
        await app.StartAsync();
    }
    private bool Allowed(HttpContext context) => string.Equals(context.Request.RouteValues["secret"]?.ToString(), token, StringComparison.Ordinal);
    private async Task ServePlaylist(HttpContext context)
    {
        if (!Allowed(context) || stream is null) { context.Response.StatusCode = 404; return; }
        context.Response.ContentType = "application/vnd.apple.mpegurl";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(stream.Playlist("piece/{0}.ts"), context.RequestAborted);
    }
    private async Task ServePiece(HttpContext context, int index)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, shutdown.Token);
        using var abort = shutdown.Token.Register(context.Abort);
        if (!Allowed(context) || stream is null || index < 0 || index >= stream.Stream.Segments) { context.Response.StatusCode = 404; return; }
        try
        {
            var start = Environment.TickCount64;
            var piece = await stream.SegmentAsync(index, cancellation.Token);
            if (PlaybackDiagnostics.Enabled && Environment.TickCount64 - start is >= 250 and var elapsedMs) PlaybackDiagnostics.Record("piece-read", new { index, bytes = piece.Length, elapsedMs });
            context.Response.ContentType = "video/mp2t";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.ContentLength = piece.Length;
            await context.Response.Body.WriteAsync(piece, cancellation.Token);
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException) PlaybackDiagnostics.Record("piece-error", new { index, error = ex.GetType().Name });
            context.Abort();
        }
    }
    private async Task Serve(HttpContext context)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, shutdown.Token);
        using var abort = shutdown.Token.Register(context.Abort);
        if (!Allowed(context) || source is null)
        { context.Response.StatusCode = 404; return; }
        var length = source.Media.Length;
        if (!TryRange(context.Request.Headers.Range.ToString(), length, out var start, out var end))
        { context.Response.StatusCode = 416; context.Response.Headers.ContentRange = $"bytes */{length}"; return; }
        var partial = context.Request.Headers.ContainsKey("Range");
        context.Response.StatusCode = partial ? 206 : 200;
        context.Response.ContentType = "application/octet-stream";
        context.Response.Headers.AcceptRanges = "bytes";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentLength = end - start + 1;
        if (partial) context.Response.Headers.ContentRange = $"bytes {start}-{end}/{length}";
        if (context.Request.Method == "HEAD") return;
        source.Requested(start);
        try
        {
            // A bounded pipeline plus a per-movie cache amortizes nearby seeks.
            var inflight = new Queue<Task<byte[]>>(); var offset = start; long served = 0;
            while (offset <= end || inflight.Count > 0)
            {
                while (offset <= end && inflight.Count < MediaStreaming.Window)
                {
                    int count = (int)Math.Min(MediaStreaming.ChunkBytes, end - offset + 1);
                    inflight.Enqueue(ReadRange(offset, count, cancellation.Token)); offset += count;
                }
                var bytes = await inflight.Dequeue();
                await context.Response.Body.WriteAsync(bytes, cancellation.Token);
                // Only a sustained stream, not a short index probe, moves read-ahead.
                served += bytes.Length; if (served >= MediaStreaming.ChunkBytes * MediaStreaming.Window) source.Played(start + served);
            }
        }
        catch (Exception) { context.Abort(); }
    }
    private async Task<byte[]> ReadRange(long offset, int count, CancellationToken ct)
    {
        if (!PlaybackDiagnostics.Enabled) return await source!.ReadAsync(offset, count, ct);
        var start = Environment.TickCount64;
        try
        {
            var bytes = await source!.ReadAsync(offset, count, ct);
            var elapsedMs = Environment.TickCount64 - start;
            if (elapsedMs >= 250 || Interlocked.Increment(ref diagnosticReads) % 64 == 1)
                PlaybackDiagnostics.Record("range-read", new { offset, count, bytes = bytes.Length, elapsedMs });
            return bytes;
        }
        catch (Exception ex)
        {
            PlaybackDiagnostics.Record("range-error", new { offset, count, elapsedMs = Environment.TickCount64 - start, error = ex.GetType().Name });
            throw;
        }
    }
    public static bool TryRange(string? value, long length, out long start, out long end)
    {
        start = 0; end = length - 1;
        if (length <= 0) return false;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!value.StartsWith("bytes=", StringComparison.Ordinal) || value.Contains(',')) return false;
        var parts = value[6..].Split('-');
        if (parts.Length != 2) return false;
        if (parts[0] == "")
        {
            if (!long.TryParse(parts[1], out var suffix) || suffix <= 0) return false;
            start = Math.Max(0, length - suffix); return true;
        }
        if (!long.TryParse(parts[0], out start) || start < 0 || start >= length) return false;
        if (parts[1] != "" && (!long.TryParse(parts[1], out end) || end < start)) return false;
        end = Math.Min(end, length - 1); return true;
    }
    // Background read-ahead past the playback position; it never delays the
    // first frames, which only wait for VLC's short network cache.
    public void SetReadAhead(long bytes) { source?.SetReadAhead(bytes); stream?.StartReadAhead(); }
    public long BufferedAheadBytes => source?.CachedAheadBytes ?? stream?.BufferedAheadBytes ?? 0;
    public bool BuffersWholeVideo => source?.Unbounded == true || stream?.BuffersWholeVideo == true;
    // The playback time up to which the video is held, for a player at positionMs of a video durationMs long: what
    // the timeline draws as buffered. A converted stream's pieces give it exactly. A file's bytes are taken as spread
    // evenly over its length, so for a file it is an estimate, except that a file held to its end reads as the end.
    public long BufferedUntilMs(long positionMs, long durationMs)
    {
        if (durationMs <= 0) return 0;
        positionMs = Math.Clamp(positionMs, 0, durationMs);
        if (stream is not null) return Math.Clamp(stream.BufferedUntilMs, positionMs, durationMs);
        if (source is null) return positionMs;
        if (source.CachedToEnd) return durationMs;
        return Math.Min(durationMs, positionMs + (long)((double)source.CachedAheadBytes * durationMs / Math.Max(1, source.Media.Length)));
    }
    public bool PlaysConvertedStream => stream is not null;
    public void CancelPendingReads()
    {
        if (Interlocked.Exchange(ref stopped, 1) == 0) { shutdown.Cancel(); source?.Stop(); stream?.Stop(); }
    }
    public async ValueTask DisposeAsync()
    {
        CancelPendingReads();
        stream?.Dispose();
        var server = Interlocked.Exchange(ref app, null);
        if (server is null) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await server.StopAsync(deadline.Token); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        finally { await server.DisposeAsync(); }
    }
}
