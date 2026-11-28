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
        await UntaggedAudio(ffmpeg!, directory, check);
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

        // A picture that is hard to encode still takes no more than its quality's bitrate. Noise is the hardest there
        // is, and a film's grain is noise. With HEVC, where this PC's graphics hardware encodes it, the bitrate is lower.
        var busy = Path.Combine(directory, "busy.mkv");
        await Ffmpeg.RunAsync(ffmpeg!, ["-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=426x240:rate=24,noise=alls=40:allf=t,format=yuv420p",
            "-t", "16", "-c:v", "libx264", "-preset", "ultrafast", "-qp", "30", "-g", "48", busy], null, TimeSpan.FromMinutes(2), default);
        var busyProbe = await VideoProbe.ReadAsync(ffmpeg!, busy, default);
        var hevcEncoder = await Ffmpeg.HevcEncoderAsync(ffmpeg!);
        if (hevcEncoder is null) Console.WriteLine("No HEVC hardware encoder on this PC: streams are checked as H.264 only.");
        foreach (var hevc in hevcEncoder is null ? new[] { false } : [false, true])
        {
            var plan = StreamQuality.Plan(busyProbe!, new FileInfo(busy).Length, quality, hevc);
            using var busyStore = SegmentStore.TryCreate(Path.Combine(directory, hevc ? "busy-hevc" : "busy"), 4, reserveBytes: 0)!;
            using var busyConverter = new StreamConverter(ffmpeg!, new StreamInput(busy, busyProbe!, null, null),
                new RoomStream("busy", "busy", quality.Id, plan.Height, plan.VideoKbps, busyProbe!.DurationMs, StreamConverter.SegmentMs, 4), busyStore, hevc ? hevcEncoder : null);
            long bytes = 0;
            for (var index = 0; index < 4; index++) bytes += await busyConverter.LengthAsync(index, limit.Token);
            var kbps = bytes * 8.0 / 16 / 1000;
            // The first seconds may spend the encoder's buffer, and the transport stream adds a little. Intel's
            // encoder, told a quality and a peak alone, sent this sample at four times the bitrate.
            check(plan.Smaller && plan.VideoKbps == quality.Kbps(hevc) && busyConverter.Encoder.StartsWith("hevc") == hevc && kbps < plan.VideoKbps * 1.25,
                $"a picture full of noise is sent within its quality's bitrate ({kbps:F0} kbps for {plan.VideoKbps}, with {busyConverter.Encoder})");
        }

        // A DVD's pixels are not square, and scaling leaves pixel shapes one encoder writes wrongly. The picture is sent
        // with square pixels, at the width it is shown.
        var dvd = Path.Combine(directory, "dvd.mkv");
        await Ffmpeg.RunAsync(ffmpeg!, ["-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=0x202020:size=720x480:rate=24,format=yuv420p,setsar=32/27",
            "-t", "8", "-c:v", "libx264", "-preset", "ultrafast", "-g", "48", dvd], null, TimeSpan.FromMinutes(2), default);
        var dvdProbe = await VideoProbe.ReadAsync(ffmpeg!, dvd, default);
        var dvdPlan = StreamQuality.Plan(dvdProbe!, new FileInfo(dvd).Length, quality, hevc: false);
        var dvdWork = Path.Combine(directory, "dvd"); Directory.CreateDirectory(dvdWork);
        using (var dvdStore = SegmentStore.TryCreate(dvdWork, 2, reserveBytes: 0)!)
        using (var dvdConverter = new StreamConverter(ffmpeg!, new StreamInput(dvd, dvdProbe!, null, null), new RoomStream("dvd", "dvd", quality.Id, dvdPlan.Height, quality.VideoKbps, dvdProbe!.DurationMs, StreamConverter.SegmentMs, 2), dvdStore))
        {
            var file = Path.Combine(dvdWork, "piece.ts");
            await File.WriteAllBytesAsync(file, dvdConverter.Read(0, 0, await dvdConverter.LengthAsync(0, limit.Token))!);
            var (_, described) = await Ffmpeg.RunAsync(ffmpeg!, ["-hide_banner", "-i", file], null, TimeSpan.FromMinutes(1), default);
            var size = described.Split('\n').FirstOrDefault(line => line.Contains("Video:"))?.Trim() ?? described;
            check(dvdPlan.Height == 480 && size.Contains("854x480 [SAR 1:1 "), "a video whose pixels are not square is sent with square ones, at the width it is shown: " + size);
        }

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
        // What the timeline draws as buffered: up to the end of the last piece held from the one being played.
        using (var fresh = new RemoteStream(stream, (_, _, _, _) => Task.FromException<byte[]>(new IOException("unused")), null))
            check(fresh.BufferedUntilMs == -1 && remote.BufferedUntilMs == stream.DurationMs && bridge.BufferedUntilMs(13000, stream.DurationMs) == stream.DurationMs && bridge.BufferedUntilMs(13000, 0) == 0,
                "a stream read ahead to its last piece is buffered to the end of the video");
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
            // libVLC repeats this stream's time between the moments it moves on (about once a second), and the room's
            // sync read every repeat as new: up to 750 ms behind, so it changed the speed every few seconds.
            var clock = await MeasureClock(player, 8000);
            check(clock.Spread < 300, $"the room's estimate of where the converted stream plays keeps to a steady clock (strays {clock.Spread} ms; {clock.Times} times in {clock.Events} reports)");
            player.Time = 18500;
            // The player first reports where the piece it needs begins, refills its buffer, and then plays on from the requested time.
            var sought = Stopwatch.StartNew();
            await Until(() => player.Time is >= 19500 and < 24000);
            check(player.IsPlaying && sought.ElapsedMilliseconds < 1000 + 6000, $"a seek continues from the requested time ({player.Time} ms, {sought.ElapsedMilliseconds} ms after asking for 18500)");
        }
        finally { player.Stop(); }
        using (var local = new Media(vlc, video, FromType.FromPath))
        {
            try
            {
                check(player.Play(local), "the player opens the video itself");
                await Until(() => player.IsPlaying && player.Time > 500);
                var own = await MeasureClock(player, 6000);
                check(own.Spread < 100, $"the room's estimate of where a video file plays keeps to a steady clock (strays {own.Spread} ms; {own.Times} times in {own.Events} reports)");
            }
            finally { player.Stop(); }
        }

        // The pieces are the viewer's to keep: a stream that is closed and opened again, as after a reconnect to the
        // room, starts with everything that was downloaded and asks for none of it again.
        var held = viewerStore.Count; var fetched = reads;
        using (var closed = new RemoteStream(stream, (_, _, _, _) => Task.FromException<byte[]>(new IOException("unused")), viewerStore)) closed.Dispose();
        using (var reopened = new RemoteStream(stream, (_, _, _, _) => { Interlocked.Increment(ref reads); return Task.FromException<byte[]>(new IOException("Whoever converts the video is away")); }, viewerStore))
        {
            var again = await reopened.SegmentAsync(3, limit.Token);
            check(viewerStore.Accepting && viewerStore.Count == held && again.AsSpan().SequenceEqual(third) && reopened.BufferedUntilMs == stream.DurationMs && reads == fetched,
                $"a stream opened again starts with the pieces already downloaded ({held} of {stream.Segments}) and fetches none of them again");
        }

        // The same video as HEVC: a piece still plays on its own and sits at its place in the film, and the player
        // plays and seeks it like the other.
        if (hevcEncoder is not null)
        {
            var hevcStream = Offer("hevc") with { VideoKbps = quality.HevcKbps };
            using var hevcStore = SegmentStore.TryCreate(Path.Combine(directory, "hevc"), stream.Segments, reserveBytes: 0)!;
            using var hevcConverter = new StreamConverter(ffmpeg!, new StreamInput(video, probe!, 0, null), hevcStream, hevcStore, hevcEncoder);
            var sixth = hevcConverter.Read(5, 0, await hevcConverter.LengthAsync(5, limit.Token))!;
            var codec = await Codec(ffmpeg!, sixth, work);
            check(hevcConverter.Encoder == hevcEncoder && codec == "hevc" && Math.Abs(FirstVideoTime(sixth) - (Ffmpeg.TimestampOffsetSeconds + 20)) < 0.05 && await Decodes(ffmpeg!, sixth, work),
                $"with {hevcEncoder}, a piece is HEVC, holds the video from its place in the film and plays on its own (it is {codec})");
            using var hevcRemote = new RemoteStream(hevcStream, (name, offset, count, ct) => (hevcConverter.Resolve(name) ?? throw new IOException("No such part")).ReadAsync(offset, count, ct), null);
            await using var hevcBridge = new MediaBridge();
            using var hevcMedia = new Media(vlc, await hevcBridge.StartAsync(hevcRemote));
            hevcMedia.AddOption($":network-caching={MediaStreaming.NetworkCacheForRtt(50)}");
            try
            {
                check(player.Play(hevcMedia), "the player opens the HEVC stream");
                await Until(() => player.IsPlaying && player.Time > 500);
                player.Time = 18500;
                await Until(() => player.Time is >= 19500 and < 24000);
                check(player.IsPlaying && Math.Abs(player.Length - probe!.DurationMs) < 500, $"the player plays the HEVC stream at its full length and seeks in it ({player.Time} ms after asking for 18500)");
            }
            finally { player.Stop(); }
        }

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
        check(Ffmpeg.HevcEncoder == hevcEncoder, "a video that no encoder can convert says nothing about the encoders");

        // An HEVC encoder that passed the check and then converts nothing. Last, because it changes what later streams use.
        using var fallbackStore = SegmentStore.TryCreate(Path.Combine(directory, "fallback"), stream.Segments, reserveBytes: 0)!;
        using var fallback = new StreamConverter(ffmpeg!, new StreamInput(video, probe, 0, null), Offer("fallback"), fallbackStore, "hevc_none");
        check(await fallback.LengthAsync(0, limit.Token) > 0 && !fallback.Encoder.StartsWith("hevc") && Ffmpeg.HevcEncoder is null,
            $"when the HEVC encoder converts nothing the stream is converted by the next one ({fallback.Encoder}), and later streams are planned without HEVC");
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
        var hevcEncoder = await Ffmpeg.HevcEncoderAsync(ffmpeg);
        var plan = StreamQuality.Plan(probe, new FileInfo(path).Length, chosen, hevcEncoder is not null); var quality = plan.Quality;
        var directory = Path.Combine(Path.GetTempPath(), "BlackPearl-checks", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var clock = Stopwatch.StartNew();
            var subtitle = int.TryParse(Option("--subtitle"), out var track) ? await Ffmpeg.PrepareSubtitleAsync(ffmpeg, path, probe, new BurnedSubtitle(Track: track), directory, default) : null;
            var prepared = clock.Elapsed.TotalSeconds;
            var segments = (int)((probe.DurationMs + StreamConverter.SegmentMs - 1) / StreamConverter.SegmentMs);
            var stream = new RoomStream("frame", "video", quality.Id, plan.Height, plan.VideoKbps, probe.DurationMs, StreamConverter.SegmentMs, segments);
            using var store = SegmentStore.TryCreate(directory, segments, reserveBytes: 0)!;
            using var converter = new StreamConverter(ffmpeg, new StreamInput(path, probe, probe.AudioTracks == 0 ? null : int.TryParse(Option("--audio"), out var audio) ? audio : 0, subtitle), stream, store, hevcEncoder);
            var index = (int)(seconds * 1000 / StreamConverter.SegmentMs);
            clock.Restart();
            var length = await converter.LengthAsync(index, default);
            var piece = Path.Combine(directory, "piece.ts"); await File.WriteAllBytesAsync(piece, converter.Read(index, 0, length)!);
            var within = seconds - index * StreamConverter.SegmentMs / 1000.0;
            var (exit, errors) = await Ffmpeg.RunAsync(ffmpeg, ["-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-i", piece, "-vf", "select=gte(t\\," + Ffmpeg.Number(within) + ")", "-frames:v", "1", args[4]], null, TimeSpan.FromMinutes(1), default);
            Console.WriteLine($"{probe.Width}x{probe.Height}{(probe.Hdr ? " HDR" : "")} to {quality.Name}, {stream.Height} high, with {converter.Encoder}; subtitles {(subtitle is null ? "none" : subtitle.ImageTrack is null ? $"text, {(subtitle.FontsDirectory is null ? "no" : Directory.GetFiles(subtitle.FontsDirectory).Length.ToString())} fonts" : "picture")} prepared in {prepared:F1} s; piece {index} ({length / 1000} KB) ready {clock.Elapsed.TotalSeconds:F1} s after asking; frame {(exit == 0 && File.Exists(args[4]) ? "saved" : "failed: " + errors)}");
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
        var coming = new RoomStream("preparing", "movie", "720p", 0, 0, 0, 0, 0, Preparing: true);
        check(room.Apply("host", new("stream", Data: Wire.Serialize(coming)), 1000, out _) && room.Snapshot.Stream == coming && !HostRoomCoordinator.ValidStream(coming with { Preparing = false }),
            "a stream can be announced before it is ready");
        room.Apply("host", new("stream", Data: Wire.Serialize(stream)), 1000, out _);
        room.Apply("host", new("media", Data: Wire.Serialize(new SharedMedia("next", "Next", 1024, ".mkv"))), 1000, out _);
        check(room.Snapshot.Stream is null, "the next video starts as its file");
        // What an app from before converted streams makes of the state: the same room, without the stream.
        check(Wire.Serialize(room.Snapshot with { Stream = stream }).Contains("\"stream\":") && Wire.Read<RoomSnapshot>(Wire.Serialize(room.Snapshot).Replace(",\"stream\":null", "")).Media?.Id == "next",
            "the stream is an addition older apps ignore");
        check(StreamQuality.For(1080).Select(q => q.Id).SequenceEqual(["1080p", "720p", "480p"]) && StreamQuality.For(720).Select(q => q.Id).SequenceEqual(["720p", "480p"]) &&
            StreamQuality.For(360).Select(q => q.Id).SequenceEqual(["480p"]) && StreamQuality.For(2160).Length == 3 && StreamQuality.For(int.MaxValue).Length == 3, "a video is offered the qualities it is large enough for");
    }

    // A Matroska track that names no language is English to ffmpeg, as to players. A converted stream takes it as unknown,
    // or in the language identified while it played, as the PC's player does.
    private static async Task UntaggedAudio(string ffmpeg, string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "untagged-audio.mkv");
        MatroskaFixture.Write(path, [new(new short[16000 * 3], 16000), new(new short[16000 * 3], 16000, "jpn", "Commentary"), new(new short[16000 * 3], 16000, "und")], []);
        var probe = await VideoProbe.ReadAsync(ffmpeg, path, default, sound: true);
        check(probe?.Audio is [{ Language: "eng" }, { Language: "jpn", Title: "Commentary" }, { Language: null }],
            "ffmpeg calls a Matroska audio track without a language English: " + string.Join(" | ", probe?.Audio.Select(a => a.ToString()) ?? []));
        var container = SubtitleText.Tracks(path);
        var lined = ProbedAudio.WithContainerLanguages(probe!.Audio, container, _ => null);
        check(lined is [{ Language: null }, { Language: "jpn" }, { Language: null }] && ProbedAudio.Preferred(probe.Audio, ["en", "ja"]) == 0 && ProbedAudio.Preferred(lined, ["en", "ja"]) == 1,
            "lined up with the file's own tracks, an untagged one has no language, so English doesn't pick it");
        var identified = ProbedAudio.WithContainerLanguages(probe.Audio, container, id => id == 0 ? "fr" : id == 2 ? "" : null);
        check(identified is [{ Language: "fr" }, { Language: "jpn" }, { Language: null }] && ProbedAudio.Preferred(identified, ["fr", "ja"]) == 0,
            "an untagged track takes the language identified while it played, by its place in the file");
        check(ProbedAudio.WithContainerLanguages(probe.Audio, [], _ => "fr") == probe.Audio, "tracks that can't be lined up keep what ffmpeg says");
        // ffmpeg reads no LanguageBCP47: a track tagged only so is English to it, and Japanese to the file.
        ContainerTrack[] bcp47 = [new(1, ContainerTrackType.Video, "V_MPEG4/ISO/AVC", null, false), new(2, ContainerTrackType.Audio, "A_AAC", "ja", false), new(3, ContainerTrackType.Audio, "A_AAC", "en-US", false)];
        var tagged = ProbedAudio.WithContainerLanguages([new("eng", null, "aac", "stereo", true), new("eng", null, "aac", "stereo", false)], bcp47, _ => "fr");
        check(tagged is [{ Language: "ja" }, { Language: "en-US" }] && ProbedAudio.Preferred(tagged, ["en"]) == 1,
            "a track has the language its file gives it, also one ffmpeg doesn't read");
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
        // Each audio track's language, title, codec and layout, for the phone's Audio menu; a title elsewhere (the file's,
        // a chapter's, a subtitle's) names no audio track.
        var tracks = VideoProbe.Parse("""
            Input #0, matroska,webm, from 'show.mkv':
              Metadata:
                title           : Night Signal
              Duration: 00:24:00.00, start: 0.000000, bitrate: 4000 kb/s
              Chapters:
                Chapter #0:0: start 0.000000, end 90.000000
                  Metadata:
                    title           : Opening
              Stream #0:0: Video: h264 (High), yuv420p(progressive), 1920x1080, 23.98 fps (default)
              Stream #0:1(jpn): Audio: flac, 48000 Hz, stereo, s16 (default)
              Stream #0:2(eng): Audio: eac3, 48000 Hz, 5.1(side), fltp, 640 kb/s
                Metadata:
                  title           : Commentary
                  BPS             : 640000
              Stream #0:3: Audio: aac (LC), 44100 Hz, 6 channels, fltp
              Stream #0:4(eng): Subtitle: ass (ssa)
                Metadata:
                  title           : Signs
              Stream #0:5(und): Audio: opus, 48000 Hz, mono, fltp
            """);
        check(tracks?.Audio is [("jpn", null, "flac", "stereo", true), ("eng", "Commentary", "eac3", "5.1(side)", false), (null, null, "aac", "6 channels", false), (null, null, "opus", "mono", false)],
            "each audio track's language, title, codec and channel layout is read: " + string.Join(" | ", tracks?.Audio.Select(a => a.ToString()) ?? []));
        check(ProbedAudio.Preferred(tracks!.Audio, ["en", "ja"]) == 1 && ProbedAudio.Preferred(tracks.Audio, ["ja"]) == 0 && ProbedAudio.Preferred(tracks.Audio, ["fr"]) == 0 &&
            ProbedAudio.Preferred(tracks.Audio.Select(a => a with { Default = a.Codec == "aac" }).ToArray(), []) == 2 &&
            ProbedAudio.Preferred(tracks.Audio.Select(a => a with { Default = false }).ToArray(), ["fr"]) == 0 && ProbedAudio.Preferred([], ["en"]) is null,
            "a converted stream starts with the first preferred language a track has, else the file's default track, else its first");
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
        check(graph == @"[0:v:0]scale=-2:720:flags=bicubic,subtitles=filename=C\\:/Users/Jo\\\'e\, \[x\]/sub\;s.ass:fontsdir=C\\:/fonts,scale=trunc(ih*dar/2+0.5)*2:ih:flags=bicubic,setsar=1,format=yuv420p[out]",
            "a subtitle file's path is written so the filter reads it as a path: " + graph);
        check(Ffmpeg.FilterGraph(new("film.mkv", film!, 0, new(null, null, 0)), 1080).StartsWith("[0:v:0]scale=-2:1040:") && Ffmpeg.FilterGraph(new("x", phone!, 0, null), 720).Contains("tonemap"),
            "a video is never enlarged, and HDR is tone mapped");
        // A film's picture is as wide as its class and often shorter. Judged by height alone, 1920x1040 lost its 1080p.
        VideoProbe Sized(int width, int height) => film! with { Width = width, Height = height };
        long Bytes(int kbps) => (film!.DurationMs * kbps + 7) / 8;
        StreamPlan Plan(VideoProbe video, int kbps, string quality, bool hevc = false) => StreamQuality.Plan(video, Bytes(kbps), StreamQuality.Find(quality)!, hevc);
        check(film!.ClassHeight == 1080 && Sized(1920, 800).ClassHeight == 1080 && Sized(1280, 536).ClassHeight == 720 && Sized(1440, 1080).ClassHeight == 1080 &&
            Sized(1080, 1920).ClassHeight == 1920 && StreamQuality.For(Sized(1920, 800).ClassHeight).Length == 3 && StreamQuality.For(Sized(1280, 536).ClassHeight).Select(q => q.Id).SequenceEqual(["720p", "480p"]),
            "a wide film is offered the qualities of the frame it is cut from");
        check(Plan(film, 6813, "1080p") is { Quality.Id: "1080p", Height: 1040, FileKbps: 6813, Smaller: true } && Plan(film, 6813, "720p") is { Quality.Id: "720p", Height: 692 } &&
            Plan(Sized(1920, 800), 6813, "720p").Height == 532 && Plan(Sized(3840, 2160), 6813, "480p").Height == 480 && Plan(Sized(640, 360), 6813, "1080p") is { Quality.Id: "480p", Height: 360 } &&
            Plan(Sized(1280, 720), 6813, "1080p") is { Quality.Id: "720p", Height: 720 },
            "a wide film shrinks by as much as its full frame would, and a small video keeps its size");
        check(!Plan(film, 2761, "1080p").Smaller && !Plan(film, 2761, "720p").Smaller && Plan(film, 2761, "480p").Smaller && !Plan(Sized(3840, 2160), 1500, "480p").Smaller && Plan(Sized(640, 360), 6813, "480p").Smaller,
            "a file whose bitrate is already below the quality's is not converted, whatever its size");

        // HEVC shows the same picture in fewer bits, so each quality takes less where this PC encodes it.
        var hd = StreamQuality.Find("1080p")!; var small = StreamQuality.Find("480p")!;
        check(StreamQuality.Converted.All(q => q.HevcKbps < q.VideoKbps && q.Kbps(true) == q.HevcKbps && q.Kbps(false) == q.VideoKbps) && hd.TotalMbps(true) < hd.TotalMbps(false) &&
            Plan(film, hd.HevcKbps + 500, "1080p", hevc: true) is { Smaller: true } converted && converted.VideoKbps == hd.HevcKbps && !Plan(film, hd.HevcKbps + 500, "1080p").Smaller,
            "with HEVC a quality takes less, so a file is worth converting that would not be as H.264");
        string Arguments(string encoder) => string.Join(' ', Ffmpeg.EncoderArguments(encoder, 3000));
        check(Ffmpeg.Encoders.Concat(Ffmpeg.HevcEncoders).All(encoder => Arguments(encoder).Contains("-maxrate ") && Arguments(encoder).Contains("-bufsize 6000k") && Arguments(encoder).Contains("-g 100000")) &&
            Arguments("hevc_qsv").StartsWith("-c:v hevc_qsv ") && Arguments("hevc_qsv").Contains("-global_quality ") && Arguments("hevc_qsv").Contains("-b:v 3000k -maxrate 3001k") &&
            Arguments("h264_qsv/vbr").StartsWith("-c:v h264_qsv ") && !Arguments("h264_qsv/vbr").Contains("-global_quality") && Arguments("h264_qsv/vbr").Contains("-b:v 3000k -maxrate 3001k"),
            "every encoder is given the bitrate as a limit, and Intel's a target with a peak just above it, which is what makes it keep to one");

        // What a connection carries against what is sent: the best quality that fits it is suggested.
        StreamQuality? Suggest(int fileKbps, string chosen, int received, bool hevc = false) => StreamQuality.Suggest(film, Bytes(fileKbps), chosen, received, hevc);
        long Needed(int fileKbps, string chosen, bool hevc = false) => StreamQuality.NeededKbps(film, Bytes(fileKbps), chosen, hevc);
        check(Needed(6813, StreamQuality.OriginalId) == 6813 && Needed(6813, "720p") == 2960 && Needed(2761, "720p") == 2761,
            "a choice needs its quality's bitrate when the file is converted to it, and the file's own otherwise");
        check(Suggest(6813, StreamQuality.OriginalId, 20000) is null && Suggest(6813, StreamQuality.OriginalId, 8200) is null && Suggest(6813, StreamQuality.OriginalId, 8000)?.Id == "1080p",
            "nothing is suggested while the connection carries the video with room to spare");
        check(Suggest(6813, StreamQuality.OriginalId, 6000)?.Id == "720p" && Suggest(6813, "1080p", 3000)?.Id == "480p" && Suggest(6813, "1080p", 3600)?.Id == "720p",
            "the best quality the connection carries is suggested");
        check(Suggest(6813, StreamQuality.OriginalId, 900)?.Id == "480p" && Suggest(6813, "480p", 900) is null, "a connection slower than every quality is offered the smallest, and nothing once that is chosen");
        check(Suggest(2761, StreamQuality.OriginalId, 2500)?.Id == "480p" && Suggest(2761, "720p", 2500)?.Id == "480p" && Suggest(1500, StreamQuality.OriginalId, 1000) is null,
            "only qualities that would make the file smaller are suggested");
        check(Needed(6813, "720p", hevc: true) == StreamQuality.Find("720p")!.HevcKbps + StreamQuality.AudioKbps && Suggest(6813, StreamQuality.OriginalId, (int)((small.HevcKbps + StreamQuality.AudioKbps) * 1.25), hevc: true)?.Id == "480p" &&
            Suggest(6813, StreamQuality.OriginalId, (int)((small.HevcKbps + StreamQuality.AudioKbps) * 1.25)) is { Id: "480p" } && !StreamQuality.Fits(small.VideoKbps + StreamQuality.AudioKbps, (int)((small.HevcKbps + StreamQuality.AudioKbps) * 1.25)),
            "a connection too slow for the smallest H.264 quality is carried by the HEVC one");

        // The speed of a connection is measured only while many reads are under way.
        var time = 0L; var meter = new TransferMeter(() => time);
        void Reads(int count) { for (var i = 0; i < count; i++) meter.Started(); }
        Reads(4);
        for (var second = 0; second < 10; second++) { time += 1000; meter.Finished(125000); meter.Started(); }
        check(meter.Rate is null, "a few reads at a time say nothing about the connection");
        Reads(12);
        for (var tick = 0; tick < 30; tick++) { time += 100; meter.Finished(25000); meter.Started(); }
        check(meter.Rate is null, "three seconds are too few to measure");
        for (var tick = 0; tick < 30; tick++) { time += 100; meter.Finished(25000); meter.Started(); }
        check(meter.Rate is { Kbps: 2000, AgeMs: 0 }, $"the speed is what arrived over the time many reads were under way ({meter.Rate?.Kbps} kbps)");
        for (var i = 0; i < 12; i++) meter.Finished(0);
        for (var second = 0; second < 8; second++) { time += 1000; meter.Finished(1000); meter.Started(); }
        check(meter.Rate is { Kbps: 2000, AgeMs: 8000 }, "time spent playing from the buffer is left out, and the measurement ages");
        Reads(12);
        time += 6000; meter.Finished(0); meter.Started();
        check(meter.Rate is { Kbps: < 1100 and > 900, AgeMs: 0 }, $"a connection that stalls while video is wanted measures slower ({meter.Rate?.Kbps} kbps)");
        for (var i = 0; i < 12; i++) meter.Finished(0);
        time += 30000;
        Reads(12);
        for (var tick = 0; tick < 50; tick++) { time += 100; meter.Finished(100000); meter.Started(); }
        check(meter.Rate is { Kbps: 8000 }, $"measurements older than twenty seconds are forgotten ({meter.Rate?.Kbps} kbps)");
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

    // The video codec ffmpeg reads in a piece.
    private static async Task<string> Codec(string ffmpeg, byte[] piece, string work)
    {
        var file = Path.Combine(work, Guid.NewGuid().ToString("N") + ".ts"); await File.WriteAllBytesAsync(file, piece);
        var (_, described) = await Ffmpeg.RunAsync(ffmpeg, ["-hide_banner", "-i", file], null, TimeSpan.FromMinutes(1), default);
        File.Delete(file);
        return System.Text.RegularExpressions.Regex.Match(described, @"Video: (\w+)").Groups[1].Value;
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

    // What a room's sync sees of the player: its time events fed to PlaybackPosition, read on the app's 250 ms tick.
    // The player plays at 1x, so a right estimate keeps one offset to a steady clock. Spread is how far it strays from
    // that, 5th to 95th percentile, after a first second for the player to settle.
    internal static async Task<(int Events, int Times, long Spread)> MeasureClock(MediaPlayer player, int milliseconds)
    {
        var position = new PlaybackPosition(); var events = 0; var times = new HashSet<long>();
        void Observe(object? sender, MediaPlayerTimeChangedEventArgs e)
        {
            position.Observe(e.Time, Environment.TickCount64);
            lock (times) { events++; times.Add(e.Time); }
        }
        player.TimeChanged += Observe;
        var offsets = new List<long>();
        try
        {
            var start = Environment.TickCount64;
            while (Environment.TickCount64 - start < milliseconds)
            {
                await Task.Delay(250); var now = Environment.TickCount64;
                if (now - start >= 1000) offsets.Add(position.Estimate(player.Time, player.IsPlaying, player.Rate, now) - now);
            }
        }
        finally { player.TimeChanged -= Observe; }
        offsets.Sort();
        lock (times) return (events, times.Count, offsets[(int)(offsets.Count * .95)] - offsets[(int)(offsets.Count * .05)]);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
}
