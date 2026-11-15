using System.Diagnostics;
using LibVLCSharp.Shared;
using BlackPearl.Core;

// A room's video converted to a lower quality while it plays: the room state that offers it, the
// conversion itself with the real ffmpeg, and a viewer playing it with the real player.
static class StreamQualityFixture
{
    // The app's own copy when the checkout has built it, otherwise whatever the machine has.
    public static string? FindFfmpeg()
    {
        for (var folder = new DirectoryInfo(Directory.GetCurrentDirectory()); folder is not null; folder = folder.Parent)
        {
            var models = Path.Combine(folder.FullName, ".tools", "models");
            if (!Directory.Exists(models)) continue;
            foreach (var cached in Directory.EnumerateDirectories(models, "ffmpeg-*").OrderDescending())
                if (File.Exists(Path.Combine(cached, "ffmpeg.exe"))) return Path.Combine(cached, "ffmpeg.exe");
        }
        return Ffmpeg.Find();
    }

    public static async Task Run(string directory, Action<bool, string> check)
    {
        RoomState(check);
        Probing(check);
        var ffmpeg = FindFfmpeg();
        check(ffmpeg is not null, "the converter is available to the checks");
        // Characters that mean something to ffmpeg's filter syntax, in the path the subtitles are drawn from.
        directory = Path.Combine(directory, "it's [odd], dir; x=1"); Directory.CreateDirectory(directory);
        var video = Path.Combine(directory, "sample.mkv");
        var captions = Path.Combine(directory, "captions.srt");
        // A white block in the lower part of the picture from 4 to 8 seconds, as a subtitle file and as a track of the video.
        await File.WriteAllTextAsync(captions, "1\n00:00:04,200 --> 00:00:07,800\n████████████████\n");
        var (made, errors) = await Ffmpeg.RunAsync(ffmpeg!, ["-hide_banner", "-nostdin", "-y", "-loglevel", "error",
            "-f", "lavfi", "-i", "color=c=0x202020:size=1280x720:rate=24,format=yuv420p", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
            "-i", captions, "-t", "30", "-map", "0:v", "-map", "1:a", "-map", "2:s", "-c:v", "libx264", "-preset", "ultrafast", "-g", "48", "-c:a", "aac", "-c:s", "srt", video], null, TimeSpan.FromMinutes(2), default);
        check(made == 0 && File.Exists(video), "a sample video with a subtitle track is made" + (made == 0 ? "" : ": " + errors));

        var probe = await VideoProbe.ReadAsync(ffmpeg!, video, default);
        check(probe is { Width: 1280, Height: 720, AudioTracks: 1, Hdr: false } && probe.Subtitles is [{ Image: false }] && Math.Abs(probe.DurationMs - 30000) < 200,
            "the converter reads the video's size, length and tracks");
        var quality = StreamQuality.Find("480p")!;
        RoomStream Offer(string id) => new(id, "sample", quality.Id, 480, quality.VideoKbps, probe!.DurationMs, StreamConverter.SegmentMs, (int)((probe.DurationMs + StreamConverter.SegmentMs - 1) / StreamConverter.SegmentMs));
        var stream = Offer("plain");
        check(stream.Segments == 8 && HostRoomCoordinator.ValidStream(stream), "thirty seconds of video are offered as eight four-second pieces");

        var work = Path.Combine(directory, "work"); Directory.CreateDirectory(work);
        using var store = SegmentStore.TryCreate(work, stream.Segments, reserveBytes: 0)!;
        using var converter = new StreamConverter(ffmpeg!, new StreamInput(video, probe!, 0, null), stream, store);
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        // A viewer who joins in the middle: conversion starts there, not at the beginning.
        var fifth = await converter.LengthAsync(5, limit.Token);
        check(fifth > 0 && !store.Has(0) && store.Has(5), "conversion starts at the piece that is asked for");
        var piece = converter.Read(5, 0, fifth)!;
        check(piece.Length == fifth && piece.Length % 188 == 0 && piece[0] == 0x47 && Pid(piece, 0) == 0 && Pid(piece, 1) == 0x1000,
            "a piece is a transport stream that begins with its program tables");
        check(Math.Abs(FirstVideoTime(piece) - (Ffmpeg.TimestampOffsetSeconds + 20)) < 0.05, "a piece holds the video from its place in the film");
        check(await Decodes(ffmpeg!, piece, work), "a piece plays on its own");
        for (var index = 0; index < stream.Segments; index++) await converter.LengthAsync(index, limit.Token);
        check(store.Count == stream.Segments, "every piece is converted once somebody has asked for it");
        check(Enumerable.Range(0, stream.Segments).All(index => Math.Abs(FirstVideoTime(converter.Read(index, 0, store.Length(index))!) - (Ffmpeg.TimestampOffsetSeconds + index * 4)) < 0.05),
            "pieces converted in separate runs line up on the same clock");
        var total = Enumerable.Range(0, stream.Segments).Sum(store.Length);
        check(total * 8.0 / 30 / 1000 < quality.VideoKbps + StreamQuality.AudioKbps + 300, $"the converted video stays within its quality's bitrate ({total * 8.0 / 30 / 1000:F0} kbps)");
        var resource = converter.Resolve(StreamConverter.Resource(stream.Id, 5, size: true));
        check(resource is not null && BitConverter.ToInt64(await resource.ReadAsync(0, 8, default)) == fifth &&
            (await converter.Resolve(StreamConverter.Resource(stream.Id, 5))!.ReadAsync(188, 376, default)).AsSpan().SequenceEqual(piece.AsSpan(188, 376)) &&
            converter.Resolve("stream/another/5") is null && converter.Resolve(StreamConverter.Resource(stream.Id, 99)) is null && converter.Resolve("stream/" + stream.Id + "/x") is null,
            "viewers read a piece's size and ranges by name, and nothing outside the stream");

        // Subtitles drawn into the picture, from the video's own track and from a file beside it.
        var plain = await LowerBrightness(ffmpeg!, converter.Read(1, 0, store.Length(1))!, work);
        foreach (var (name, choice) in new[] { ("track", new BurnedSubtitle(Track: 0)), ("file", new BurnedSubtitle(File: captions)) })
        {
            var subtitleWork = Path.Combine(directory, "subtitles from " + name); Directory.CreateDirectory(subtitleWork);
            var prepared = await Ffmpeg.PrepareSubtitleAsync(ffmpeg!, video, probe!, choice, subtitleWork, limit.Token);
            check(prepared is { File: not null, ImageTrack: null }, $"text subtitles from a {name} are prepared once for the stream");
            using var burnedStore = SegmentStore.TryCreate(subtitleWork, stream.Segments, reserveBytes: 0)!;
            using var burning = new StreamConverter(ffmpeg!, new StreamInput(video, probe!, 0, prepared), Offer("burned-" + name), burnedStore);
            var burned = await LowerBrightness(ffmpeg!, burning.Read(1, 0, await burning.LengthAsync(1, limit.Token))!, work);
            var before = await LowerBrightness(ffmpeg!, burning.Read(0, 0, await burning.LengthAsync(0, limit.Token))!, work);
            check(burned > plain + 5 && Math.Abs(before - plain) < 2, $"subtitles from a {name} are drawn into the picture while they are shown (brightness {plain:F1} without, {burned:F1} with, {before:F1} before)");
        }

        // A viewer: pieces fetched by name, kept, read ahead to the end, and played by the real player.
        var reads = 0;
        using var viewerStore = SegmentStore.TryCreate(Path.Combine(directory, "viewer"), stream.Segments, reserveBytes: 0)!;
        using var remote = new RemoteStream(stream, async (name, offset, count, ct) =>
        {
            Interlocked.Increment(ref reads);
            return await (converter.Resolve(name) ?? throw new IOException("No such part")).ReadAsync(offset, count, ct);
        }, viewerStore);
        var third = await remote.SegmentAsync(3, limit.Token);
        check(third.AsSpan().SequenceEqual(converter.Read(3, 0, store.Length(3))), "a viewer receives a piece exactly as it was converted");
        var afterFirst = reads;
        check((await remote.SegmentAsync(3, limit.Token)).Length == third.Length && reads == afterFirst, "a piece the viewer has is not fetched again");
        remote.StartReadAhead();
        await Until(() => remote.BufferedAheadSegments == stream.Segments - 3);
        check(viewerStore.Count == stream.Segments - 2 || viewerStore.Count == stream.Segments - 3, "the rest of the stream is read ahead while the player asks for nothing");
        var playlist = remote.Playlist("piece/{0}.ts");
        check(playlist.Contains("#EXT-X-PLAYLIST-TYPE:VOD") && playlist.Split('\n').Count(line => line.EndsWith(".ts")) == stream.Segments && playlist.Contains("#EXT-X-ENDLIST"),
            "the player is given the whole video as a list of its pieces");

        await using var bridge = new MediaBridge();
        var url = await bridge.StartAsync(remote);
        Core.Initialize();
        using var vlc = new LibVLC("--vout=dummy", "--no-video-title-show", "--quiet");
        using var player = new MediaPlayer(vlc);
        player.SetAudioFormat("S16N", 48000, 2);
        player.SetAudioCallbacks((_, _, _, _) => { }, null, null, null, null);
        using var media = new Media(vlc, url);
        media.AddOption($":network-caching={MediaStreaming.NetworkCacheForRtt(50)}");
        try
        {
            check(player.Play(media), "the player opens the converted stream");
            await Until(() => player.IsPlaying && player.Time > 500);
            check(Math.Abs(player.Length - probe!.DurationMs) < 500 && player.IsSeekable, $"the player shows the video's full length ({player.Length} ms) and can seek");
            player.Time = 18500;
            // The player first reports where the piece it needs begins, refills its buffer, and then plays on from the requested time.
            var sought = Stopwatch.StartNew();
            await Until(() => player.Time is >= 19500 and < 24000);
            check(player.IsPlaying && sought.ElapsedMilliseconds < 1000 + 6000, $"a seek continues from the requested time ({player.Time} ms, {sought.ElapsedMilliseconds} ms after asking for 18500)");
        }
        finally { player.Stop(); }

        // The video's end and a conversion nothing can start.
        using var shortStore = SegmentStore.TryCreate(Path.Combine(directory, "short"), stream.Segments + 3, reserveBytes: 0)!;
        var longer = Offer("longer") with { DurationMs = probe!.DurationMs + 12000, Segments = stream.Segments + 3 };
        using var ending = new StreamConverter(ffmpeg!, new StreamInput(video, probe, 0, null), longer, shortStore);
        var beyond = await ending.LengthAsync(longer.Segments - 1, limit.Token);
        check(beyond > 0 && ending.Read(longer.Segments - 1, 0, beyond) is { } nothing && nothing.Length % 188 == 0 && nothing[0] == 0x47,
            "a piece past the end of a video that is shorter than stated holds nothing, and the player gets it at once");
        using var failingStore = SegmentStore.TryCreate(Path.Combine(directory, "failing"), stream.Segments, reserveBytes: 0)!;
        using var failing = new StreamConverter(ffmpeg!, new StreamInput(Path.Combine(directory, "missing.mkv"), probe, 0, null), Offer("failing"), failingStore);
        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        failing.Failed += message => failed.TrySetResult(message);
        try { await failing.LengthAsync(0, limit.Token); check(false, "a video that cannot be converted fails"); }
        catch (IOException) { check(failing.Failure is not null && (await failed.Task.WaitAsync(TimeSpan.FromSeconds(10))).Length > 0, "a video that cannot be converted is reported once, after every encoder was tried"); }
    }

    // --stream-frame <video> <seconds> <quality> <picture.png> [--audio <track>] [--subtitle <track>]
    // Converts the piece of a real video that holds that moment, as a room would, and saves the frame viewers get.
    public static async Task Frame(string[] args)
    {
        string? Option(string name) { var at = Array.IndexOf(args, name); return at >= 0 && at + 1 < args.Length ? args[at + 1] : null; }
        var path = args[1]; var seconds = double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
        var ffmpeg = FindFfmpeg() ?? throw new IOException("ffmpeg was not found.");
        var probe = await VideoProbe.ReadAsync(ffmpeg, path, default) ?? throw new IOException("The video could not be read.");
        var chosen = StreamQuality.Find(args[3]) ?? throw new ArgumentException("Unknown quality");
        var quality = StreamQuality.For(probe.Height).FirstOrDefault(q => q.Height <= chosen.Height) ?? StreamQuality.Converted[^1];
        var directory = Path.Combine(Path.GetTempPath(), "BlackPearl-checks", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var clock = Stopwatch.StartNew();
            var subtitle = int.TryParse(Option("--subtitle"), out var track) ? await Ffmpeg.PrepareSubtitleAsync(ffmpeg, path, probe, new BurnedSubtitle(Track: track), directory, default) : null;
            var prepared = clock.Elapsed.TotalSeconds;
            var segments = (int)((probe.DurationMs + StreamConverter.SegmentMs - 1) / StreamConverter.SegmentMs);
            var stream = new RoomStream("frame", "video", quality.Id, Math.Min(quality.Height, probe.Height) / 2 * 2, quality.VideoKbps, probe.DurationMs, StreamConverter.SegmentMs, segments);
            using var store = SegmentStore.TryCreate(directory, segments, reserveBytes: 0)!;
            using var converter = new StreamConverter(ffmpeg, new StreamInput(path, probe, probe.AudioTracks == 0 ? null : int.TryParse(Option("--audio"), out var audio) ? audio : 0, subtitle), stream, store);
            var index = (int)(seconds * 1000 / StreamConverter.SegmentMs);
            clock.Restart();
            var length = await converter.LengthAsync(index, default);
            var piece = Path.Combine(directory, "piece.ts"); await File.WriteAllBytesAsync(piece, converter.Read(index, 0, length)!);
            var within = seconds - index * StreamConverter.SegmentMs / 1000.0;
            var (exit, errors) = await Ffmpeg.RunAsync(ffmpeg, ["-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-i", piece, "-vf", "select=gte(t\\," + Ffmpeg.Number(within) + ")", "-frames:v", "1", args[4]], null, TimeSpan.FromMinutes(1), default);
            Console.WriteLine($"{probe.Width}x{probe.Height}{(probe.Hdr ? " HDR" : "")} to {stream.Height}p with {converter.Encoder}; subtitles {(subtitle is null ? "none" : subtitle.ImageTrack is null ? $"text, {(subtitle.FontsDirectory is null ? "no" : Directory.GetFiles(subtitle.FontsDirectory).Length.ToString())} fonts" : "picture")} prepared in {prepared:F1} s; piece {index} ({length / 1000} KB) ready {clock.Elapsed.TotalSeconds:F1} s after asking; frame {(exit == 0 && File.Exists(args[4]) ? "saved" : "failed: " + errors)}");
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }

    private static void RoomState(Action<bool, string> check)
    {
        var room = new HostRoomCoordinator("host");
        Participant[] members = [new("host", "Host", true, true), new("guest", "Guest", false, true)];
        room.Discover(members, 1000); room.SetConnected("guest", true, 1000);
        var stream = new RoomStream("s1", "movie", "720p", 720, 2800, 600000, 4000, 150);
        check(!room.Apply("host", new("stream", Data: Wire.Serialize(stream)), 1000, out _), "a stream cannot be offered before a video");
        room.Apply("host", new("media", Data: Wire.Serialize(new SharedMedia("movie", "Movie", 1024, ".mkv"))), 1000, out _);
        check(room.Apply("host", new("stream", Data: Wire.Serialize(stream)), 1000, out _) && Wire.Read<RoomSnapshot>(Wire.Serialize(room.Snapshot)).Stream == stream,
            "whoever plays the video offers a converted stream, and it reaches everyone with the room's state");
        check(!room.Apply("guest", new("stream"), 1000, out _) && room.Snapshot.Stream == stream, "a viewer cannot change the stream");
        check(!room.Apply("host", new("stream", Data: Wire.Serialize(stream with { MediaId = "other" })), 1000, out _) &&
            !room.Apply("host", new("stream", Data: Wire.Serialize(stream with { Segments = 149 })), 1000, out _) &&
            !room.Apply("host", new("stream", Data: Wire.Serialize(stream with { SegmentMs = 10 })), 1000, out _) && room.Snapshot.Stream == stream,
            "a stream for another video, or one whose pieces do not add up, is refused");
        check(room.Apply("host", new("stream"), 1000, out _) && room.Snapshot.Stream is null, "the stream is withdrawn to go back to the file");
        room.Apply("host", new("stream", Data: Wire.Serialize(stream)), 1000, out _);
        room.Apply("host", new("media", Data: Wire.Serialize(new SharedMedia("next", "Next", 1024, ".mkv"))), 1000, out _);
        check(room.Snapshot.Stream is null, "the next video starts as its file");
        // What an app from before converted streams makes of the state: the same room, without the stream.
        check(Wire.Serialize(room.Snapshot with { Stream = stream }).Contains("\"stream\":") && Wire.Read<RoomSnapshot>(Wire.Serialize(room.Snapshot).Replace(",\"stream\":null", "")).Media?.Id == "next",
            "the stream is an addition older apps ignore");
        check(StreamQuality.For(1080).Select(q => q.Id).SequenceEqual(["1080p", "720p", "480p"]) && StreamQuality.For(720).Select(q => q.Id).SequenceEqual(["720p", "480p"]) &&
            StreamQuality.For(360).Select(q => q.Id).SequenceEqual(["480p"]) && StreamQuality.For(2160).Length == 3, "a video is offered the qualities it is large enough for");
    }

    private static void Probing(Action<bool, string> check)
    {
        var film = VideoProbe.Parse("""
            Input #0, matroska,webm, from 'film.mkv':
              Duration: 01:50:50.69, start: 0.000000, bitrate: 6813 kb/s
              Stream #0:0: Video: hevc (Main 10), yuv420p10le(tv, bt709), 1920x1040 [SAR 1:1 DAR 24:13], 23.98 fps, 23.98 tbr, 1k tbn (default)
              Stream #0:1(jpn): Audio: eac3, 48000 Hz, 5.1(side), fltp, 640 kb/s (default)
              Stream #0:2(eng): Audio: eac3, 48000 Hz, 5.1(side), fltp, 640 kb/s
              Stream #0:3(eng): Subtitle: hdmv_pgs_subtitle (pgssub), 1920x1080
              Stream #0:4(eng): Subtitle: ass (ssa)
              Stream #0:5: Attachment: ttf
              Stream #0:6: Attachment: otf
              Stream #0:7: Video: mjpeg (Baseline), yuvj420p(pc, bt470bg/unknown/unknown), 600x882, 90k tbr, 90k tbn (attached pic)
            """);
        check(film is { DurationMs: 6650690, Width: 1920, Height: 1040, Hdr: false, AudioTracks: 2, StartSeconds: 0 } && film.Subtitles is [{ Image: true }, { Image: false, Codec: "ass" }] && film.FontStreams is [5, 6],
            "a film's description gives its size, length, audio, subtitle kinds and fonts, leaving its cover art aside");
        var phone = VideoProbe.Parse("""
              Duration: 00:01:05.77, start: 3595.827000, bitrate: 7036 kb/s
              Stream #0:0[0x100](und): Video: hevc (Main 10) (HEVC / 0x43564548), yuv420p10le(tv, bt2020nc/bt2020/smpte2084), 3840x2160 [SAR 1:1 DAR 16:9], 59.94 fps, 90k tbn, start 3595.827000
              Stream #0:1[0x101](jpn): Audio: aac (LC), 48000 Hz, stereo, fltp, 192 kb/s
            """);
        check(phone is { Height: 2160, Hdr: true, AudioTracks: 1, DurationMs: 65770 } && Math.Abs(phone.StartSeconds - 3595.827) < 0.0001 && phone.Subtitles.Length == 0,
            "a video that starts late and is in HDR is described as such");
        check(VideoProbe.Parse("sample.mkv: No such file or directory") is null && VideoProbe.Parse("  Duration: 00:03:00.00, start: 0.000000\n  Stream #0:0: Audio: flac, 44100 Hz, stereo") is null,
            "something that is not a video is not described");
        var graph = Ffmpeg.FilterGraph(new("film.mkv", film!, 0, new(@"C:\Users\Jo'e, [x]\sub;s.ass", @"C:\fonts", null)), 720);
        check(graph == @"[0:v:0]scale=-2:720:flags=bicubic,subtitles=filename=C\\:/Users/Jo\\\'e\, \[x\]/sub\;s.ass:fontsdir=C\\:/fonts,format=yuv420p[out]",
            "a subtitle file's path is written so the filter reads it as a path: " + graph);
        check(Ffmpeg.FilterGraph(new("film.mkv", film!, 0, new(null, null, 0)), 1080).StartsWith("[0:v:0]scale=-2:1040:") && Ffmpeg.FilterGraph(new("x", phone!, 0, null), 720).Contains("tonemap"),
            "a video is never enlarged, and HDR is tone mapped");
    }

    private static int Pid(byte[] stream, int packet) => (stream[packet * 188 + 1] & 0x1F) << 8 | stream[packet * 188 + 2];

    // The presentation time of the first video frame in a piece, in seconds.
    private static double FirstVideoTime(byte[] stream)
    {
        for (var at = 0; at + 188 <= stream.Length; at += 188)
        {
            var packet = stream.AsSpan(at, 188);
            if (((packet[1] & 0x1F) << 8 | packet[2]) != 0x100 || (packet[1] & 0x40) == 0) continue;
            var start = (packet[3] & 0x20) != 0 ? 5 + packet[4] : 4;
            if (start + 14 > 188 || (packet[start + 7] & 0x80) == 0) continue;
            var b = packet.Slice(start + 9, 5);
            return ((long)(b[0] >> 1 & 7) << 30 | (long)b[1] << 22 | (long)(b[2] >> 1) << 15 | (long)b[3] << 7 | (long)(b[4] >> 1)) / 90000.0;
        }
        return -1;
    }

    private static async Task<bool> Decodes(string ffmpeg, byte[] piece, string work)
    {
        var file = Path.Combine(work, Guid.NewGuid().ToString("N") + ".ts"); await File.WriteAllBytesAsync(file, piece);
        var (exit, errors) = await Ffmpeg.RunAsync(ffmpeg, ["-hide_banner", "-nostdin", "-loglevel", "error", "-i", file, "-f", "null", "-"], null, TimeSpan.FromMinutes(1), default);
        File.Delete(file);
        return exit == 0 && errors.Trim().Length == 0;
    }

    // Average brightness of the lower quarter of the picture two seconds into a piece, where the sample's subtitles are drawn.
    private static async Task<double> LowerBrightness(string ffmpeg, byte[] piece, string work)
    {
        var file = Path.Combine(work, Guid.NewGuid().ToString("N") + ".ts"); await File.WriteAllBytesAsync(file, piece);
        var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-i", file, "-vf", "select=gte(n\\,48),crop=iw:ih/4:0:ih*3/4,scale=64:16", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1" }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        _ = process.StandardError.ReadToEndAsync();
        using var frame = new MemoryStream(); await process.StandardOutput.BaseStream.CopyToAsync(frame);
        await process.WaitForExitAsync(); File.Delete(file);
        return frame.Length == 0 ? -1 : frame.ToArray().Average(value => (double)value);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
}
