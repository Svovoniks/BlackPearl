using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BlackPearl.Core;

// The qualities a room's video can be converted to while it plays. Original streams the file as it is.
// VideoKbps is the most its picture takes as H.264; HevcKbps as HEVC, which shows the same picture in fewer bits.
public sealed record StreamQuality(string Id, string Name, int Height, int VideoKbps, int HevcKbps)
{
    public const string OriginalId = "original", SettingKey = "streamQuality";
    public static readonly StreamQuality[] Converted =
    [
        new("1080p", "1080p", 1080, 5000, 3200),
        new("720p", "720p", 720, 2800, 1900),
        new("480p", "480p", 480, 1400, 1000),
    ];
    public const int AudioKbps = 160;
    public static StreamQuality? Find(string? id) => Converted.FirstOrDefault(q => q.Id == id);
    // A video is never enlarged: a 720p video offers 720p and 480p. The height is the video's class (VideoProbe.ClassHeight).
    public static StreamQuality[] For(int classHeight) => Converted.Where(q => q.Height - 8 <= Math.Max(classHeight, Converted[^1].Height)).ToArray();
    // What the picture takes with the encoder this PC converts with: HEVC where its graphics hardware encodes it.
    public int Kbps(bool hevc) => hevc ? HevcKbps : VideoKbps;
    public double TotalMbps(bool hevc) => (Kbps(hevc) + AudioKbps) / 1000.0;
    // What a video becomes for a chosen quality: the largest quality it is big enough for, at or below the chosen one,
    // and its picture shrunk by as much as a full frame of its class would be. A 1920x800 film at 720p is 1280x532.
    public static StreamPlan Plan(VideoProbe probe, long fileBytes, StreamQuality chosen, bool hevc)
    {
        var quality = For(probe.ClassHeight).FirstOrDefault(q => q.Height <= chosen.Height) ?? Converted[^1];
        var height = (int)Math.Round((double)probe.Height * quality.Height / Math.Max(probe.ClassHeight, quality.Height));
        return new(quality, Math.Min(probe.Height, height) / 2 * 2, fileBytes * 8 / Math.Max(1, probe.DurationMs), quality.Kbps(hevc));
    }
    // A stream needs this much more than its average bitrate for the connection to keep up through its busier scenes.
    public const double Headroom = 1.2;
    public static bool Fits(long neededKbps, int receivedKbps) => neededKbps * Headroom <= receivedKbps;
    // What the room is sent for a choice: the quality's bitrate when the file is converted to it, otherwise the file's own.
    public static long NeededKbps(VideoProbe probe, long fileBytes, string? chosenId, bool hevc) =>
        Find(chosenId) is { } chosen && Plan(probe, fileBytes, chosen, hevc) is { Smaller: true } plan ? plan.VideoKbps + AudioKbps : fileBytes * 8 / Math.Max(1, probe.DurationMs);
    // For a connection that carries this much: null while the choice fits it or nothing smaller exists; otherwise
    // the best quality that fits, or the smallest there is when none does.
    public static StreamQuality? Suggest(VideoProbe probe, long fileBytes, string? chosenId, int receivedKbps, bool hevc)
    {
        var needed = NeededKbps(probe, fileBytes, chosenId, hevc);
        if (Fits(needed, receivedKbps)) return null;
        var smaller = For(probe.ClassHeight).Where(q => q.Kbps(hevc) + AudioKbps < needed && Plan(probe, fileBytes, q, hevc).Smaller).ToArray();
        return smaller.FirstOrDefault(q => Fits(q.Kbps(hevc) + AudioKbps, receivedKbps)) ?? smaller.LastOrDefault();
    }
}

public sealed record StreamPlan(StreamQuality Quality, int Height, long FileKbps, int VideoKbps)
{
    // A file whose bitrate is already this low would take as long to send converted, and look worse, whatever its size.
    public bool Smaller => FileKbps > VideoKbps + StreamQuality.AudioKbps;
}

// What ffmpeg says a video file holds: enough to choose a quality and to draw its subtitles into the picture.
public sealed record VideoProbe(long DurationMs, double StartSeconds, int Width, int Height, bool Hdr, int AudioTracks, ProbedSubtitle[] Subtitles, int[] FontStreams)
{
    // The audio tracks in order, as "-map 0:a:<n>" counts them.
    public ProbedAudio[] Audio { get; init; } = [];
    private static readonly Regex DurationLine = new(@"Duration: (\d+):(\d\d):(\d\d(?:\.\d+)?)(?:, start: (-?\d+(?:\.\d+)?))?", RegexOptions.Compiled);
    private static readonly Regex StreamLine = new(@"^\s*Stream #0:(\d+)(?:\[0x[0-9a-fA-F]+\])?(?:\((?<language>[^)]*)\))?: (Video|Audio|Subtitle|Attachment): ([\w-]+)(.*)$", RegexOptions.Compiled);
    // A stream's own title, in the metadata ffmpeg lists under it.
    private static readonly Regex TitleLine = new(@"^\s+title\s*: (.*)$", RegexOptions.Compiled);
    // The channel layout after the sample rate: "48000 Hz, 5.1(side)", "44100 Hz, stereo", "48000 Hz, 6 channels".
    private static readonly Regex Layout = new(@"\d+ Hz, ([^,]+)", RegexOptions.Compiled);
    private static readonly Regex Size = new(@"[, ](\d{2,5})x(\d{2,5})(?:[, \[]|$)", RegexOptions.Compiled);
    private static readonly string[] ImageSubtitles = ["hdmv_pgs_subtitle", "dvd_subtitle", "dvb_subtitle", "xsub"];
    private static readonly string[] Fonts = ["ttf", "otf"];
    // The height of the full 16:9 frame this picture is cut from: a film is as wide as its class and often shorter, so
    // 1920x800 and 1920x1034 are 1080p films.
    public int ClassHeight => Math.Max(Height, (Width * 9 + 8) / 16);

    // A file with no picture (music) is described only when sound is all that is wanted: its height is 0.
    public static VideoProbe? Parse(string output, bool sound = false)
    {
        long duration = 0; double start = 0; int width = 0, height = 0, audio = 0; var hdr = false;
        var subtitles = new List<ProbedSubtitle>(); var fonts = new List<int>(); var audioTracks = new List<ProbedAudio>();
        var inAudio = false;
        foreach (var line in output.Split('\n'))
        {
            // A title under an audio stream names that track; a title anywhere else (the file's, a chapter's) does not.
            if (inAudio && TitleLine.Match(line.TrimEnd('\r')) is { Success: true } title)
            {
                if (audioTracks[^1].Title is null && title.Groups[1].Value.Trim() is { Length: > 0 } named) audioTracks[^1] = audioTracks[^1] with { Title = named };
                continue;
            }
            if (duration == 0 && DurationLine.Match(line) is { Success: true } time)
            {
                duration = (long)((int.Parse(time.Groups[1].Value, CultureInfo.InvariantCulture) * 3600 + int.Parse(time.Groups[2].Value, CultureInfo.InvariantCulture) * 60 +
                    double.Parse(time.Groups[3].Value, CultureInfo.InvariantCulture)) * 1000);
                if (time.Groups[4].Success) start = Math.Max(0, double.Parse(time.Groups[4].Value, CultureInfo.InvariantCulture));
                continue;
            }
            if (StreamLine.Match(line.TrimEnd('\r')) is not { Success: true } stream)
            {
                // A stream's metadata is indented under it; a line less indented ends the stream.
                if (!line.StartsWith("    ", StringComparison.Ordinal)) inAudio = false;
                continue;
            }
            inAudio = stream.Groups[2].Value == "Audio";
            var index = int.Parse(stream.Groups[1].Value, CultureInfo.InvariantCulture); var codec = stream.Groups[3].Value; var rest = stream.Groups[4].Value;
            switch (stream.Groups[2].Value)
            {
                // Cover art is a video stream too; the film is the first one that is not.
                case "Video" when height == 0 && !rest.Contains("(attached pic)") && Size.Match(rest) is { Success: true } size:
                    width = int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture); height = int.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture);
                    hdr = rest.Contains("smpte2084") || rest.Contains("arib-std-b67");
                    break;
                case "Audio":
                    audio++;
                    var language = stream.Groups["language"].Value is { Length: > 0 } tag && tag != "und" ? tag : null;
                    audioTracks.Add(new(language, null, codec, Layout.Match(rest) is { Success: true } layout ? layout.Groups[1].Value.Trim() : null, rest.Contains("(default)")));
                    break;
                case "Subtitle": subtitles.Add(new(codec, ImageSubtitles.Contains(codec))); break;
                case "Attachment" when Fonts.Contains(codec): fonts.Add(index); break;
            }
        }
        return duration > 0 && (height > 0 || sound && audio > 0) ? new(duration, start, width, height, hdr, audio, [.. subtitles], [.. fonts]) { Audio = [.. audioTracks] } : null;
    }

    public static async Task<VideoProbe?> ReadAsync(string ffmpeg, string path, CancellationToken ct, bool sound = false)
    {
        // Without an output ffmpeg describes the input and exits with an error; the description is what is wanted.
        var (_, text) = await Ffmpeg.RunAsync(ffmpeg, ["-hide_banner", "-nostdin", "-i", path], null, TimeSpan.FromSeconds(30), ct);
        return Parse(text, sound);
    }
}
public sealed record ProbedSubtitle(string Codec, bool Image);
// An audio track: its language tag ("jpn"; null when untagged), title, codec, channel layout ("5.1(side)", "stereo"),
// and whether the file marks it as the one to play.
public sealed record ProbedAudio(string? Language, string? Title, string Codec, string? Layout, bool Default)
{
    // The track to start with: the first language of Settings › Playback that a track has, as the PC's player picks;
    // where none has one, the track the file marks, or else its first.
    public static int? Preferred(IReadOnlyList<ProbedAudio> tracks, IReadOnlyList<string> preferences)
    {
        if (tracks.Count == 0) return null;
        if (TrackLanguages.Select(tracks.Select((track, i) => new LanguageTrack(i, track.Title ?? "", track.Language)), preferences) is { } preferred) return preferred;
        for (var i = 0; i < tracks.Count; i++) if (tracks[i].Default) return i;
        return 0;
    }

    // ffmpeg reports a Matroska track that names no language as English, as players do (ContainerTrack.Untagged). Lined
    // up with the container's own list (Matroska and MP4: SubtitleText.Tracks), such a track has no language, or the one
    // identified while it played: detected takes the player's track id, its position in the file. Tracks that can't be
    // lined up, as in other containers, keep what ffmpeg says.
    public static ProbedAudio[] WithContainerLanguages(ProbedAudio[] audio, IReadOnlyList<ContainerTrack> container, Func<int, string?> detected)
    {
        var positions = container.Select((track, id) => (track, id)).Where(entry => entry.track.Type == ContainerTrackType.Audio).ToArray();
        if (positions.Length != audio.Length) return audio;
        return audio.Select((track, n) => !positions[n].track.Untagged ? track :
            track with { Language = detected(positions[n].id) is { Length: > 0 } found ? found : null }).ToArray();
    }
}

// Which subtitles the converted picture carries: a subtitle track of the video, or a subtitle file beside it.
public sealed record BurnedSubtitle(int? Track = null, string? File = null);

// The video as ffmpeg is to read it for one converted stream. A Sound stream has no picture: only the Audio track, for a
// phone that plays the PC's sound (PHONE-REMOTE.md, "Sound on the phone").
public sealed record StreamInput(string Path, VideoProbe Probe, int? Audio, StreamSubtitle? Subtitle, bool Sound = false);
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
        finally { Kill(process); }
    }

    // Stops a process that may be exiting, or gone, at this very moment: stopping it then fails in one of these ways.
    internal static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException) { }
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

    // Tried in this order. "/vbr" is the same encoder aiming at the bitrate itself, for graphics hardware
    // too old for the mode that stops at a picture quality.
    public static readonly string[] Encoders = ["h264_nvenc", "h264_qsv", "h264_qsv/vbr", "h264_amf", "libx264", "h264_mf"];
    // HEVC shows the same picture in fewer bits. Only graphics hardware is used for it: in software it ran at two
    // thirds of x264's speed on a 16-core processor, and the PCs whose graphics cannot encode HEVC are older ones.
    public static readonly string[] HevcEncoders = ["hevc_nvenc", "hevc_qsv", "hevc_qsv/vbr", "hevc_amf"];
    public const string CodecSetting = "STREAM_CODEC";

    private static readonly object hevcGate = new();
    private static Task<string?>? hevcEncoder;
    // The HEVC encoder this PC's graphics hardware has, found once by encoding a second of video with each; null when
    // there is none. BLACKPEARL_STREAM_CODEC=h264 leaves HEVC unused.
    public static Task<string?> HevcEncoderAsync(string ffmpeg)
    {
        lock (hevcGate) return hevcEncoder ??= Task.Run(() => FindHevcEncoderAsync(ffmpeg));
    }
    // The encoder found so far: null until the check has finished.
    public static string? HevcEncoder { get { lock (hevcGate) return hevcEncoder is { IsCompletedSuccessfully: true } found ? found.Result : null; } }
    // The encoder passed the check and then converted nothing where another did: it is left alone from here on.
    internal static void HevcFailed() { lock (hevcGate) hevcEncoder = Task.FromResult<string?>(null); }

    private static async Task<string?> FindHevcEncoderAsync(string ffmpeg)
    {
        if (string.Equals(AppEnvironment.Get(CodecSetting), "h264", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var encoder in HevcEncoders)
        {
            try
            {
                var (exit, errors) = await RunAsync(ffmpeg, ["-hide_banner", "-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=24,format=yuv420p", "-frames:v", "24",
                    .. EncoderArguments(encoder, StreamQuality.Converted[1].HevcKbps), "-f", "null", "-"], null, TimeSpan.FromSeconds(20), default);
                PlaybackDiagnostics.Record("stream-encoder", new { encoder, works = exit == 0, error = exit == 0 ? null : Tail(errors) });
                if (exit == 0) return encoder;
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException) { PlaybackDiagnostics.Record("stream-encoder", new { encoder, works = false, error = ex.GetType().Name }); }
        }
        return null;
    }

    // kbps is the most the picture takes. NVIDIA's and Intel's encoders and x264 stop at a picture quality where the
    // video needs less; the others aim at the bitrate itself.
    internal static string[] EncoderArguments(string encoder, int kbps)
    {
        string rate = kbps.ToString(CultureInfo.InvariantCulture) + "k", above = (kbps + 1).ToString(CultureInfo.InvariantCulture) + "k", buffer = (kbps * 2).ToString(CultureInfo.InvariantCulture) + "k";
        var name = encoder.Split('/')[0];
        // Pieces are cut at keyframes, so the only keyframes are the ones forced at piece boundaries.
        string[] shared = ["-g", "100000", "-bufsize", buffer];
        return encoder switch
        {
            "h264_nvenc" or "hevc_nvenc" => ["-c:v", name, "-preset", "p4", "-rc", "vbr", "-cq", "24", "-b:v", "0", "-maxrate", rate, "-no-scenecut", "1", "-forced-idr", "1", .. shared],
            // Intel's encoder takes a quality and a bitrate together only when given a target and a higher peak. Given a
            // quality and a peak alone it encodes every frame at fixed quantizers and ignores the peak. The peak is
            // barely higher, because its HEVC encoder stays at the peak for as long as the video is hard: two minutes
            // of noise came out 17% over the target with a peak a fifth above it.
            "h264_qsv" or "hevc_qsv" => ["-c:v", name, "-preset", "fast", "-global_quality", IntelQuality, "-b:v", rate, "-maxrate", above, "-forced_idr", "1", .. shared],
            "h264_qsv/vbr" or "hevc_qsv/vbr" => ["-c:v", name, "-preset", "fast", "-b:v", rate, "-maxrate", above, "-forced_idr", "1", .. shared],
            "h264_amf" or "hevc_amf" => ["-c:v", name, "-rc", "vbr_peak", "-b:v", rate, "-maxrate", rate, "-forced_idr", "1", .. shared],
            "libx264" => ["-c:v", name, "-preset", "veryfast", "-crf", "22", "-maxrate", rate, "-sc_threshold", "0", "-keyint_min", "100000", .. shared],
            _ => ["-c:v", name, "-b:v", rate, "-maxrate", rate, .. shared],
        };
    }
    // Intel's quality setting means about the same picture as H.264 and as HEVC: on a clean film and a grainy one,
    // at every size, HEVC scored within 1.5 VMAF points of H.264 in 27 to 50% fewer bits.
    private const string IntelQuality = "24";

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
                "[v][s]overlay=(W-w)/2:(H-h)/2:eof_action=pass" + SquarePixels + ",format=yuv420p[out]";
        }
        if (input.Subtitle is { File: { } file })
        {
            video.Append(",subtitles=filename=" + FilterValue(file));
            if (input.Subtitle.FontsDirectory is { } fonts) video.Append(":fontsdir=" + FilterValue(fonts));
        }
        return video + SquarePixels + ",format=yuv420p[out]";
    }
    // The picture is sent with square pixels. Scaling leaves a pixel shape such as 3301888:3302933, which Intel's
    // encoder writes cut to 16 bits: a 1920x1034 film at 720p arrived half as wide as it should be. A video whose
    // pixels are not square (a DVD) is resized to its shown width here; for any other this changes no pixel.
    private const string SquarePixels = ",scale=trunc(ih*dar/2+0.5)*2:ih:flags=bicubic,setsar=1";

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
        // Sound alone: its pieces are cut at the first audio frame of each, all of which can start one. ffmpeg gives the
        // only stream the video's usual identifier and marks every audio frame as one to start from.
        if (input.Sound)
        {
            arguments.AddRange(["-copyts", "-i", input.Path, "-map", "0:a:" + (input.Audio ?? 0).ToString(CultureInfo.InvariantCulture), "-vn", "-sn", "-dn",
                "-c:a", "aac", "-b:a", StreamQuality.AudioKbps.ToString(CultureInfo.InvariantCulture) + "k", "-ac", "2", "-ar", "48000",
                "-output_ts_offset", TimestampOffsetSeconds.ToString(CultureInfo.InvariantCulture), "-muxdelay", "0", "-muxpreload", "0", "-mpegts_flags", "+resend_headers", "-flush_packets", "1", "-f", "mpegts", "pipe:1"]);
            return arguments;
        }
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
