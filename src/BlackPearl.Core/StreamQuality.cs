using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BlackPearl.Core;

// The qualities a room's video can be converted to while it plays. Original streams the file as it is.
public sealed record StreamQuality(string Id, string Name, int Height, int VideoKbps)
{
    public const string OriginalId = "original", SettingKey = "streamQuality";
    public static readonly StreamQuality[] Converted =
    [
        new("1080p", "1080p", 1080, 5000),
        new("720p", "720p", 720, 2800),
        new("480p", "480p", 480, 1400),
    ];
    public const int AudioKbps = 160;
    public static StreamQuality? Find(string? id) => Converted.FirstOrDefault(q => q.Id == id);
    // A video is never enlarged: a 720p video offers 720p and 480p.
    public static StreamQuality[] For(int sourceHeight) => Converted.Where(q => q.Height <= Math.Max(sourceHeight, Converted[^1].Height) + 8).ToArray();
    public double TotalMbps => (VideoKbps + AudioKbps) / 1000.0;
}

// What ffmpeg says a video file holds: enough to choose a quality and to draw its subtitles into the picture.
public sealed record VideoProbe(long DurationMs, double StartSeconds, int Width, int Height, bool Hdr, int AudioTracks, ProbedSubtitle[] Subtitles, int[] FontStreams)
{
    private static readonly Regex DurationLine = new(@"Duration: (\d+):(\d\d):(\d\d(?:\.\d+)?)(?:, start: (-?\d+(?:\.\d+)?))?", RegexOptions.Compiled);
    private static readonly Regex StreamLine = new(@"^\s*Stream #0:(\d+)(?:\[0x[0-9a-fA-F]+\])?(?:\([^)]*\))?: (Video|Audio|Subtitle|Attachment): ([\w-]+)(.*)$", RegexOptions.Compiled);
    private static readonly Regex Size = new(@"[, ](\d{2,5})x(\d{2,5})(?:[, \[]|$)", RegexOptions.Compiled);
    private static readonly string[] ImageSubtitles = ["hdmv_pgs_subtitle", "dvd_subtitle", "dvb_subtitle", "xsub"];
    private static readonly string[] Fonts = ["ttf", "otf"];

    public static VideoProbe? Parse(string output)
    {
        long duration = 0; double start = 0; int width = 0, height = 0, audio = 0; var hdr = false;
        var subtitles = new List<ProbedSubtitle>(); var fonts = new List<int>();
        foreach (var line in output.Split('\n'))
        {
            if (duration == 0 && DurationLine.Match(line) is { Success: true } time)
            {
                duration = (long)((int.Parse(time.Groups[1].Value, CultureInfo.InvariantCulture) * 3600 + int.Parse(time.Groups[2].Value, CultureInfo.InvariantCulture) * 60 +
                    double.Parse(time.Groups[3].Value, CultureInfo.InvariantCulture)) * 1000);
                if (time.Groups[4].Success) start = Math.Max(0, double.Parse(time.Groups[4].Value, CultureInfo.InvariantCulture));
                continue;
            }
            if (StreamLine.Match(line.TrimEnd('\r')) is not { Success: true } stream) continue;
            var index = int.Parse(stream.Groups[1].Value, CultureInfo.InvariantCulture); var codec = stream.Groups[3].Value; var rest = stream.Groups[4].Value;
            switch (stream.Groups[2].Value)
            {
                // Cover art is a video stream too; the film is the first one that is not.
                case "Video" when height == 0 && !rest.Contains("(attached pic)") && Size.Match(rest) is { Success: true } size:
                    width = int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture); height = int.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture);
                    hdr = rest.Contains("smpte2084") || rest.Contains("arib-std-b67");
                    break;
                case "Audio": audio++; break;
                case "Subtitle": subtitles.Add(new(codec, ImageSubtitles.Contains(codec))); break;
                case "Attachment" when Fonts.Contains(codec): fonts.Add(index); break;
            }
        }
        return duration > 0 && height > 0 ? new(duration, start, width, height, hdr, audio, [.. subtitles], [.. fonts]) : null;
    }

    public static async Task<VideoProbe?> ReadAsync(string ffmpeg, string path, CancellationToken ct)
    {
        // Without an output ffmpeg describes the input and exits with an error; the description is what is wanted.
        var (_, text) = await Ffmpeg.RunAsync(ffmpeg, ["-hide_banner", "-nostdin", "-i", path], null, TimeSpan.FromSeconds(30), ct);
        return Parse(text);
    }
}
public sealed record ProbedSubtitle(string Codec, bool Image);

// Which subtitles the converted picture carries: a subtitle track of the video, or a subtitle file beside it.
public sealed record BurnedSubtitle(int? Track = null, string? File = null);

// The video as ffmpeg is to read it for one converted stream.
public sealed record StreamInput(string Path, VideoProbe Probe, int? Audio, StreamSubtitle? Subtitle);
// Text subtitles are drawn from File with the fonts in FontsDirectory; picture subtitles straight from the video's track.
public sealed record StreamSubtitle(string? File, string? FontsDirectory, int? ImageTrack);

public static class Ffmpeg
{
    // The copy shipped with the app, or the one named by BLACKPEARL_FFMPEG. Development builds without one use ffmpeg from PATH.
    public static string? Find()
    {
        if (AppEnvironment.Get("FFMPEG") is { Length: > 0 } named) return File.Exists(named) ? named : null;
        var bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        if (File.Exists(bundled)) return bundled;
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { var candidate = Path.Combine(folder.Trim(), "ffmpeg.exe"); if (File.Exists(candidate)) return candidate; }
            catch (ArgumentException) { }
        }
        return null;
    }

    internal static async Task<(int ExitCode, string Errors)> RunAsync(string ffmpeg, IEnumerable<string> arguments, string? workingDirectory, TimeSpan limit, CancellationToken ct)
    {
        var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, StandardErrorEncoding = Encoding.UTF8 };
        if (workingDirectory is not null) info.WorkingDirectory = workingDirectory;
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("ffmpeg did not start.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(limit);
        try
        {
            var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            var errors = await process.StandardError.ReadToEndAsync(deadline.Token);
            await output; await process.WaitForExitAsync(deadline.Token);
            return (process.ExitCode, errors);
        }
        finally { if (!process.HasExited) try { process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
    }

    // Subtitles are prepared once for a stream: a text track is copied out of the video with its fonts, because the
    // filter that draws it would otherwise read the whole file again every time conversion starts at a new position.
    public static async Task<StreamSubtitle?> PrepareSubtitleAsync(string ffmpeg, string path, VideoProbe probe, BurnedSubtitle? choice, string directory, CancellationToken ct)
    {
        if (choice is null) return null;
        string? fonts = null;
        if (probe.FontStreams.Length > 0)
        {
            fonts = Path.Combine(directory, "fonts"); Directory.CreateDirectory(fonts);
            // Named by stream number: the names inside the file are not trusted as paths.
            var dump = new List<string> { "-hide_banner", "-nostdin", "-y" };
            foreach (var stream in probe.FontStreams) { dump.Add("-dump_attachment:" + stream.ToString(CultureInfo.InvariantCulture)); dump.Add("font" + stream.ToString(CultureInfo.InvariantCulture)); }
            dump.AddRange(["-i", path]);
            await RunAsync(ffmpeg, dump, fonts, TimeSpan.FromMinutes(2), ct);
            if (!Directory.EnumerateFiles(fonts).Any()) fonts = null;
        }
        if (choice.File is { } file) return File.Exists(file) ? new(file, fonts, null) : null;
        if (choice.Track is not { } track || track < 0 || track >= probe.Subtitles.Length) return null;
        if (probe.Subtitles[track].Image) return new(null, null, track);
        var extracted = Path.Combine(directory, "subtitles.ass");
        var styled = probe.Subtitles[track].Codec is "ass" or "ssa";
        var (exit, errors) = await RunAsync(ffmpeg, ["-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-copyts", "-i", path,
            "-map", "0:s:" + track.ToString(CultureInfo.InvariantCulture), "-c:s", styled ? "copy" : "ass", "-f", "ass", extracted], null, TimeSpan.FromMinutes(15), ct);
        if (exit != 0 || !File.Exists(extracted) || new FileInfo(extracted).Length == 0)
        {
            PlaybackDiagnostics.Record("stream-subtitles", new { state = "failed", exit, error = Tail(errors) });
            return null;
        }
        return new(extracted, fonts, null);
    }

    internal static string Tail(string text) => text.Length <= 400 ? text.Trim() : text[^400..].Trim();

    // A value inside a filter's options, inside a filter graph: each level has its own escaping.
    internal static string FilterValue(string value)
    {
        var option = value.Replace('\\', '/').Replace("\\", "\\\\").Replace(":", "\\:").Replace("'", "\\'");
        var graph = new StringBuilder();
        foreach (var c in option)
        {
            if (c is '\\' or '\'' or '[' or ']' or ',' or ';' or '=') graph.Append('\\');
            graph.Append(c);
        }
        return graph.ToString();
    }

    public static readonly string[] Encoders = ["h264_nvenc", "h264_qsv", "h264_amf", "libx264", "h264_mf"];

    internal static string[] EncoderArguments(string encoder, int kbps)
    {
        string rate = kbps.ToString(CultureInfo.InvariantCulture) + "k", buffer = (kbps * 2).ToString(CultureInfo.InvariantCulture) + "k";
        // Pieces are cut at keyframes, so the only keyframes are the ones forced at piece boundaries.
        string[] shared = ["-g", "100000", "-maxrate", rate, "-bufsize", buffer];
        return encoder switch
        {
            "h264_nvenc" => ["-c:v", encoder, "-preset", "p4", "-rc", "vbr", "-cq", "24", "-b:v", "0", "-no-scenecut", "1", "-forced-idr", "1", .. shared],
            "h264_qsv" => ["-c:v", encoder, "-preset", "fast", "-global_quality", "24", "-forced_idr", "1", .. shared],
            "h264_amf" => ["-c:v", encoder, "-rc", "vbr_peak", "-b:v", rate, "-forced_idr", "1", .. shared],
            "libx264" => ["-c:v", encoder, "-preset", "veryfast", "-crf", "22", "-sc_threshold", "0", "-keyint_min", "100000", .. shared],
            _ => ["-c:v", encoder, "-b:v", rate, .. shared],
        };
    }

    internal static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    // Scaled first, so tone mapping and subtitles are drawn at the size that is sent.
    internal static string FilterGraph(StreamInput input, int height)
    {
        var target = Math.Min(height, input.Probe.Height) / 2 * 2;
        var video = new StringBuilder("[0:v:0]scale=-2:" + target.ToString(CultureInfo.InvariantCulture) + ":flags=bicubic");
        if (input.Probe.Hdr) video.Append(",zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv");
        if (input.Subtitle is { ImageTrack: { } track })
        {
            // Picture subtitles are made for the video's own size and are centered on it, as players do when a film is cropped.
            var factor = Number((double)target / input.Probe.Height);
            return video + "[v];[0:s:" + track.ToString(CultureInfo.InvariantCulture) + "]scale=iw*" + factor + ":ih*" + factor + ":flags=bicubic[s];" +
                "[v][s]overlay=(W-w)/2:(H-h)/2:eof_action=pass,format=yuv420p[out]";
        }
        if (input.Subtitle is { File: { } file })
        {
            video.Append(",subtitles=filename=" + FilterValue(file));
            if (input.Subtitle.FontsDirectory is { } fonts) video.Append(":fontsdir=" + FilterValue(fonts));
        }
        return video + ",format=yuv420p[out]";
    }

    // Every converted timestamp is the video's own plus this, so the first frames never need a negative decoding time,
    // which ffmpeg would otherwise fix by shifting one run against the others.
    internal const int TimestampOffsetSeconds = 10;

    // Converts from piece firstSegment to the end of the video, as an MPEG transport stream on standard output.
    // Timestamps stay the video's own, so pieces converted in separate runs line up.
    internal static List<string> ConversionArguments(StreamInput input, RoomStream stream, string encoder, int firstSegment)
    {
        var seconds = stream.SegmentMs / 1000.0; var origin = input.Probe.StartSeconds;
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error" };
        // ffmpeg counts a seek from where the video starts.
        if (firstSegment > 0) arguments.AddRange(["-ss", Number(firstSegment * seconds)]);
        arguments.AddRange(["-copyts", "-i", input.Path, "-filter_complex", FilterGraph(input, stream.Height), "-map", "[out]"]);
        if (input.Audio is { } audio) arguments.AddRange(["-map", "0:a:" + audio.ToString(CultureInfo.InvariantCulture), "-c:a", "aac", "-b:a", StreamQuality.AudioKbps.ToString(CultureInfo.InvariantCulture) + "k", "-ac", "2", "-ar", "48000"]);
        arguments.AddRange(["-sn", "-dn"]);
        arguments.AddRange(EncoderArguments(encoder, stream.VideoKbps));
        // A keyframe at the first frame, and at the first frame of every later piece.
        var cell = "floor((t-" + Number(origin) + ")/" + Number(seconds) + "+0.0001)";
        arguments.AddRange(["-force_key_frames", "expr:isnan(prev_forced_t)+gt(" + cell + "," + cell.Replace("(t-", "(prev_forced_t-") + ")",
            "-output_ts_offset", TimestampOffsetSeconds.ToString(CultureInfo.InvariantCulture), "-muxdelay", "0", "-muxpreload", "0", "-mpegts_flags", "+resend_headers", "-flush_packets", "1", "-f", "mpegts", "pipe:1"]);
        return arguments;
    }
}
