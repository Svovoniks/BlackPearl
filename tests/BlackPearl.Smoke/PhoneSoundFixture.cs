using System.Diagnostics;
using BlackPearl.Core;

// Sound on the phone (PHONE-REMOTE.md): the PC's sound converted alone with the real ffmpeg, in the audio track the PC
// plays, and the clock a phone follows it by.
static class PhoneSoundFixture
{
    public static async Task Run(string directory, Action<bool, string> check)
    {
        await Clock(check);
        var ffmpeg = StreamQualityFixture.FindFfmpeg();
        check(ffmpeg is not null, "the converter is available to the sound checks");
        Directory.CreateDirectory(directory);
        // Two audio tracks told apart by when they click: the first on every whole second, the second half a second later.
        var video = Path.Combine(directory, "clicks.mkv");
        var (made, errors) = await Ffmpeg.RunAsync(ffmpeg!, ["-hide_banner", "-nostdin", "-y", "-loglevel", "error",
            "-f", "lavfi", "-i", "color=c=0x202020:size=640x360:rate=24,format=yuv420p",
            "-f", "lavfi", "-i", "aevalsrc='if(lt(mod(t,1),0.02),sin(2*PI*1000*t),0)':s=48000",
            "-f", "lavfi", "-i", "aevalsrc='if(between(mod(t,1),0.5,0.52),sin(2*PI*500*t),0)':s=48000",
            "-t", "30", "-map", "0:v", "-map", "1:a", "-map", "2:a", "-c:v", "libx264", "-preset", "ultrafast", "-g", "48", "-c:a", "ac3", "-b:a", "192k", video], null, TimeSpan.FromMinutes(2), default);
        check(made == 0 && File.Exists(video), "a sample video with two audio tracks is made" + (made == 0 ? "" : ": " + errors));
        var music = Path.Combine(directory, "song.m4a");
        (made, errors) = await Ffmpeg.RunAsync(ffmpeg!, ["-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100", "-t", "10", "-c:a", "aac", music],
            null, TimeSpan.FromMinutes(1), default);
        check(made == 0, "a sample song is made" + (made == 0 ? "" : ": " + errors));

        var probe = await VideoProbe.ReadAsync(ffmpeg!, video, default, sound: true);
        check(probe is { AudioTracks: 2, Height: 360 } && Math.Abs(probe.DurationMs - 30000) < 200, "the converter reads the video's sound tracks and length");
        check(await VideoProbe.ReadAsync(ffmpeg!, music, default) is null && await VideoProbe.ReadAsync(ffmpeg!, music, default, sound: true) is { Height: 0, AudioTracks: 1 },
            "a file with no picture is described only when its sound is all that's wanted");

        var segments = (int)((probe!.DurationMs + StreamConverter.SegmentMs - 1) / StreamConverter.SegmentMs);
        var stream = new RoomStream("sound", "clock", "sound", 0, StreamQuality.AudioKbps, probe.DurationMs, StreamConverter.SegmentMs, segments);
        var work = Path.Combine(directory, "work"); Directory.CreateDirectory(work);
        using var store = SegmentStore.TryCreate(work, segments, reserveBytes: 0)!;
        using var converter = new StreamConverter(ffmpeg!, new StreamInput(video, probe, 1, null, Sound: true), stream, store);
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var started = Stopwatch.StartNew();
        var fifth = await converter.LengthAsync(5, limit.Token);
        check(fifth > 0 && !store.Has(0) && store.Has(5), $"the sound is converted from the piece asked for ({started.ElapsedMilliseconds} ms)");
        var piece = converter.Read(5, 0, fifth)!;
        check(piece[0] == 0x47 && Pids(piece).SetEquals([0, 0x11, 0x1000, 0x100]), "a piece of sound is a transport stream with its tables and one stream");
        check(Math.Abs(FirstTime(piece) - (Ffmpeg.TimestampOffsetSeconds + 20)) < 0.03, "a piece holds the sound from its place in the video");
        var streams = await Describe(ffmpeg!, piece, work);
        check(streams.Contains("Audio: aac") && streams.Contains("48000 Hz, stereo") && !streams.Contains("Video:"), "the sound is stereo AAC with no picture");
        var clicks = await Clicks(ffmpeg!, piece, work);
        check(clicks.Count >= 3 && clicks.All(at => Math.Abs(at % 1 - 0.5) < 0.005), $"the piece has the PC's audio track, the second one ({string.Join(", ", clicks.Select(c => c.ToString("0.000")))})");
        for (var index = 0; index < segments; index++) await converter.LengthAsync(index, limit.Token);
        check(Enumerable.Range(0, segments).All(index => Math.Abs(FirstTime(converter.Read(index, 0, store.Length(index))!) - (Ffmpeg.TimestampOffsetSeconds + index * 4)) < 0.03),
            "pieces of sound converted in separate runs line up on the video's clock");
        var kbps = Enumerable.Range(0, segments).Sum(store.Length) * 8.0 / 30 / 1000;
        check(kbps < StreamQuality.AudioKbps + 40, $"the sound takes about its bitrate ({kbps:F0} kbps)");

        var songProbe = (await VideoProbe.ReadAsync(ffmpeg!, music, default, sound: true))!;
        var songStream = new RoomStream("song", "clock", "sound", 0, StreamQuality.AudioKbps, songProbe.DurationMs, StreamConverter.SegmentMs, 3);
        using var songStore = SegmentStore.TryCreate(Path.Combine(work, "song"), 3, reserveBytes: 0)!;
        using var songConverter = new StreamConverter(ffmpeg!, new StreamInput(music, songProbe, 0, null, Sound: true), songStream, songStore);
        check(await songConverter.LengthAsync(2, limit.Token) > 0 && songConverter.Failure is null, "a song's sound converts too");
    }

    static async Task Clock(Action<bool, string> check)
    {
        var clock = new PlaybackClock { Source = "a" };
        clock.Report(1000);
        await Task.Delay(120);
        check(clock.Read() is ("a", 1000, false), "a paused player's clock stays where it was reported");
        clock.SetRunning(true);
        await Task.Delay(200);
        var running = clock.Read();
        check(running.Playing && running.PositionMs is >= 1180 and <= 1300, $"a playing player's clock advances from the last report ({running.PositionMs})");
        clock.Report(5000);
        check(clock.Read().PositionMs is >= 5000 and <= 5020, "a new report replaces the guess");
        await Task.Delay(200); clock.Report(5002);
        check(clock.Read().PositionMs >= 5180, $"a report repeating the last time, as a converted stream's do, does not set the clock back ({clock.Read().PositionMs})");
        clock.SetRunning(false);
        var stopped = clock.Read().PositionMs;
        await Task.Delay(100);
        check(clock.Read().PositionMs == stopped && stopped >= 5000, "pausing holds the position it had reached");
    }

    static HashSet<int> Pids(byte[] stream)
    {
        var pids = new HashSet<int>();
        for (var at = 0; at + 188 <= stream.Length; at += 188) pids.Add((stream[at + 1] & 0x1F) << 8 | stream[at + 2]);
        return pids;
    }

    // The presentation time of the first audio frame in a piece, in seconds.
    static double FirstTime(byte[] stream)
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

    static async Task<string> Describe(string ffmpeg, byte[] piece, string work)
    {
        var file = Path.Combine(work, Guid.NewGuid().ToString("N") + ".ts"); await File.WriteAllBytesAsync(file, piece);
        var (_, text) = await Ffmpeg.RunAsync(ffmpeg, ["-hide_banner", "-nostdin", "-i", file], null, TimeSpan.FromMinutes(1), default);
        File.Delete(file);
        return text;
    }

    // Where the clicks in a piece are, in seconds of the video.
    static async Task<List<double>> Clicks(string ffmpeg, byte[] piece, string work)
    {
        var file = Path.Combine(work, Guid.NewGuid().ToString("N") + ".ts"); await File.WriteAllBytesAsync(file, piece);
        var raw = file + ".raw";
        await Ffmpeg.RunAsync(ffmpeg, ["-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-copyts", "-i", file, "-ac", "1", "-ar", "48000", "-f", "f32le", raw], null, TimeSpan.FromMinutes(1), default);
        var bytes = await File.ReadAllBytesAsync(raw);
        File.Delete(file); File.Delete(raw);
        var start = FirstTime(piece) - Ffmpeg.TimestampOffsetSeconds;
        var clicks = new List<double>(); var last = -1.0;
        for (var i = 0; i + 4 <= bytes.Length; i += 4)
        {
            var at = start + i / 4 / 48000.0;
            if (Math.Abs(BitConverter.ToSingle(bytes, i)) > 0.1 && at - last > 0.3) { clicks.Add(at); last = at; }
            else if (Math.Abs(BitConverter.ToSingle(bytes, i)) > 0.1) last = at;
        }
        return clicks;
    }
}
