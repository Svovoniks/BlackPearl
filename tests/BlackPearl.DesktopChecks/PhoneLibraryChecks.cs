using System.Diagnostics;
using System.IO;
using BlackPearl.Core;
using BlackPearl.Desktop;

// What BlackPearl Mobile does with the library (PhoneLibrary.cs), the part the app and the phone host share, through a
// local room server with a stand-in phone (tests/BlackPearl.Smoke/FakePhone.cs). Continue on PC ends the phone's stream
// before the PC confirms: a phone still reading video got the confirmation late, and stayed on its player.
static class PhoneLibraryChecks
{
    static int passed;
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name); passed++;
    }

    public static async Task Run(string directory)
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
        var url = "http://localhost:" + ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var sdk = File.Exists(".tools/dotnet/dotnet.exe") ? Path.GetFullPath(".tools/dotnet/dotnet.exe") : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var start = new ProcessStartInfo(sdk) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.GetFullPath("src/BlackPearl.Server/bin/Release/net10.0/BlackPearl.Server.dll"));
        start.ArgumentList.Add("--urls"); start.ArgumentList.Add(url); start.Environment["BLACKPEARL_DATA"] = Path.Combine(directory, "phone-server");
        using var server = Process.Start(start)!; server.BeginOutputReadLine(); server.BeginErrorReadLine();
        try
        {
            using (var http = new System.Net.Http.HttpClient())
                for (var attempt = 0; ; attempt++)
                {
                    try { using var response = await http.GetAsync(url + "/health"); if (response.IsSuccessStatusCode) break; } catch (System.Net.Http.HttpRequestException) { }
                    if (attempt >= 200) throw new IOException("Phone library check server did not start"); await Task.Delay(50);
                }

            var data = Path.Combine(directory, "phone-library");
            Directory.CreateDirectory(data);
            var path = Path.Combine(data, "Night Signal (2024).mkv");
            var bytes = Enumerable.Range(0, 3_000_000).Select(i => (byte)(i * 7 % 253)).ToArray();
            File.WriteAllBytes(path, bytes);
            var store = new LibraryStore(data);
            store.Save(new MediaItem("night-signal", path, "Night Signal", "Movie", 2024));
            // A show of three episodes, and a queue that holds one of them twice and a video the library no longer has.
            foreach (var episode in new[] { 1, 2, 3 })
            {
                var file = Path.Combine(data, $"Harbor Lights S01E0{episode}.mkv");
                File.WriteAllBytes(file, bytes.AsSpan(0, 1000).ToArray());
                store.Save(new MediaItem($"harbor-{episode}", file, "Harbor Lights", "Show", 2023, "Harbor Lights", 1, episode));
            }
            var queued = new SavedQueue("evening", "Evening", ["harbor-3", "gone-video", "night-signal", "harbor-1", "harbor-3"]);
            store.Setting("queues", Wire.Serialize(new[] { queued }));
            store.Setting("activeQueue", "evening");
            store.Setting("queuePosition", "-1");
            store.Setting("watched:harbor-3", "true");
            var paired = new PairedPhone(PhoneRemoteProtocol.NewHex(16), "Check phone", PhoneRemoteProtocol.NewHex(32), Wire.Now, Wire.Now);
            new LibraryPhoneStore(store).Save([paired]);
            var dual = await DualAudioVideo(data);
            store.Save(new MediaItem("dual-audio", dual, "Dual Audio", "Movie", 2025));
            store.Save(new MediaItem("untagged-audio", UntaggedCopy(dual), "Untagged Audio", "Movie", 2025));
            IReadOnlyList<MediaItem> items = store.All();
            var hostKey = PersistentRooms.NewKey();

            using var remote = new PhoneRemoteHost(new PhoneRemoteOptions
            {
                Server = url, HostKey = hostKey, PcName = "Check PC", AppVersion = "0.0.0-check", Store = new LibraryPhoneStore(store), Features = PhoneLibrary.Features,
            });
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            var plays = 0;
            var library = new PhoneLibrary(store, () => remote, action => dispatcher.BeginInvoke(action), CancellationToken.None)
            {
                Items = () => items, WasWatched = _ => false, SetWatched = (_, _) => { },
                Play = (_, _) => { plays++; return Task.FromResult(true); },
            };
            library.Attach(remote);
            remote.CurrentState = () => new RemoteState(false);
            remote.Start();
            for (var waited = 0; !remote.ServerReady && waited < 15000; waited += 50) await Task.Delay(50);
            Check(remote.ServerReady, "the PC connects to the local room server for phones");

            await using var phone = await FakePhone.Connect(url, remote.PcId, paired.Id, paired.Secret);
            Check(await phone.Opened() && await phone.MediaOpened(), "the stand-in phone links to the PC");
            phone.Send(new { type = "hello", protocol = 1, app = "check", device = "Check phone" });
            Check(await phone.Next("welcome", 10000) is not null, "the PC welcomes the phone");

            phone.Send(new { type = "watch", request = 1, id = "night-signal", quality = StreamQuality.OriginalId, network = "home" });
            var stream = await phone.Next("stream", 15000);
            Check(stream is { } s && s.GetProperty("resource").GetString() == "file/night-signal", "the phone starts watching the video");
            var read = await phone.Read("file/night-signal", 1000, 50000);
            Check(read.Error is null && read.Bytes.AsSpan().SequenceEqual(bytes.AsSpan(1000, 50000)), "the phone reads the video");

            // Queues: the list, a queue's videos with their places, a show's episodes, and stepping through a queue.
            phone.Send(new { type = "queues", request = 10 });
            var queues = await phone.Next("queues", 10000);
            var listed = queues?.GetProperty("queues").EnumerateArray().ToArray() ?? [];
            Check(listed.Length == 1 && listed[0].GetProperty("name").GetString() == "Evening" && listed[0].GetProperty("count").GetInt32() == 4 &&
                listed[0].GetProperty("active").GetBoolean(), "the phone lists the PC's queues, counting the videos the library still has");
            Check(listed.Length == 1 && listed[0].GetProperty("upNext").GetInt32() == 2, "a queue's Play continues at its first unwatched video, as on the PC");
            phone.Send(new { type = "browse", request = 11, queue = "evening" });
            var page = await phone.Next("page", 10000);
            var entries = page?.GetProperty("entries").EnumerateArray().Select(e => (e.GetProperty("id").GetString(), e.GetProperty("index").GetInt32())).ToArray() ?? [];
            Check(entries.SequenceEqual([("harbor-3", 0), ("night-signal", 2), ("harbor-1", 3), ("harbor-3", 4)]) && page?.GetProperty("location").GetString() == "Evening",
                "a queue's page lists its videos in order with their places, skipping one the library lost");
            phone.Send(new { type = "browse", request = 12, episodesOf = "harbor-2" });
            page = await phone.Next("page", 10000);
            Check(page?.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("id").GetString()).SequenceEqual(["harbor-1", "harbor-2", "harbor-3"]) == true,
                "a show's episodes are listed for a video of it");
            // A queue named at absurd length still lists its videos: phones are sent the first 120 characters of a name.
            store.Setting("queues", Wire.Serialize(new[] { queued, new SavedQueue("long", new string('x', 70000), ["harbor-1"]) }));
            phone.Send(new { type = "browse", request = 20, queue = "long" });
            page = await phone.Next("page", 10000);
            Check(page?.GetProperty("entries").GetArrayLength() == 1 && page?.GetProperty("location").GetString()?.Length == 120,
                "a queue with a very long name still lists its videos, under the first 120 characters of its name");
            phone.Send(new { type = "watch", request = 13, id = "harbor-1", quality = StreamQuality.OriginalId, network = "home", queue = "evening", queueIndex = 3 });
            var inQueue = await phone.Next("stream", 15000);
            string? Step(System.Text.Json.JsonElement? answer, string name, string field = "id") =>
                answer is { } a && a.GetProperty(name).ValueKind == System.Text.Json.JsonValueKind.Object ? a.GetProperty(name).GetProperty(field).ToString() : null;
            Check(Step(inQueue, "previous") == "night-signal" && Step(inQueue, "previous", "queueIndex") == "2" && Step(inQueue, "next") == "harbor-3" &&
                Step(inQueue, "next", "queueIndex") == "4" && Step(inQueue, "first") is null, "a video played from a queue steps to the queue's videos around it");
            Check(Step(inQueue, "queue", "index") == "3" && Step(inQueue, "queue", "name") == "Evening", "the answer says which queue and place the phone plays");
            phone.Send(new { type = "watch", request = 14, id = "harbor-3", quality = StreamQuality.OriginalId, network = "home", queue = "evening", queueIndex = 4 });
            var last = await phone.Next("stream", 15000);
            Check(Step(last, "next") is null && Step(last, "first", "queueIndex") == "0", "after a queue's last video, Loop all starts it again from its first");
            phone.Send(new { type = "watch", request = 15, id = "harbor-2", quality = StreamQuality.OriginalId, network = "home", queue = "evening", queueIndex = 0 });
            var outside = await phone.Next("stream", 15000);
            Check(Step(outside, "previous") == "harbor-1" && Step(outside, "next") == "harbor-3" && Step(outside, "queue", "index") is null,
                "a video that isn't in the queue named steps through its show instead");
            // Play on the PC from a queue while BlackPearl is closed: BlackPearl opens on the queue, at the video's place.
            var (handed, place) = library.QueuePlace("evening", 3, "harbor-1");
            if (handed is not null) library.SaveQueueHandOver(handed, place);
            Check(store.Setting("activeQueue") == "evening" && store.Setting("queuePosition") == "2" &&
                store.Setting("queue") == Wire.Serialize(new[] { "harbor-3", "night-signal", "harbor-1", "harbor-3" }), "a queue handed to the PC is saved as its player's queue at the video's place");
            library.SaveNoQueueHandOver();
            Check(store.Setting("queuePosition") == "-1", "a video handed to the PC outside a queue plays on its own, not from the queue it was last in");
            await ConvertedSound(store, phone, data);
            phone.Send(new { type = "watch", request = 16, id = "night-signal", quality = StreamQuality.OriginalId, network = "home" });
            await phone.Next("stream", 15000);

            // Continue on PC: the PC plays it, and the phone's stream has ended by the time the phone hears so.
            phone.Send(new { type = "play", request = 2, id = "night-signal", positionMs = 60000 });
            var done = await phone.Next("done", 10000);
            Check(done is { } d && d.GetProperty("request").GetInt64() == 2 && plays == 1, "Continue on PC plays the video on the PC and confirms it");
            Check((await phone.Read("file/night-signal", 0, 1000)).Error == "gone", "the phone's stream has ended once the PC confirms Continue on PC");
            Console.WriteLine($"{passed} phone library checks passed.");
        }
        finally { if (!server.HasExited) server.Kill(true); await server.WaitForExitAsync(); }
    }

    // A 720p film worth converting, with a silent Japanese track (the file's default) and an English one that sounds.
    static async Task<string> DualAudioVideo(string data)
    {
        var path = Path.Combine(data, "Dual Audio (2025).mkv");
        var (made, errors) = await RunFfmpeg(["-hide_banner", "-nostdin", "-y", "-loglevel", "error",
            "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=24,format=yuv420p", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000",
            "-t", "12", "-map", "0:v", "-map", "1:a", "-map", "2:a", "-c:v", "libx264", "-preset", "ultrafast", "-b:v", "5M", "-maxrate", "5M", "-bufsize", "10M", "-g", "48", "-c:a", "aac",
            "-metadata:s:a:0", "language=jpn", "-metadata:s:a:1", "language=eng", "-metadata:s:a:1", "title=English", "-disposition:a:0", "default", "-disposition:a:1", "0", path]);
        Check(made == 0 && File.Exists(path), "a sample film with two audio tracks is made" + (made == 0 ? "" : ": " + errors));
        return path;
    }

    // The same film with its Japanese track's language taken out (an EBML Void of the same length in its place), as
    // muxers leave a track whose language they don't know. ffmpeg then calls it English.
    static string UntaggedCopy(string tagged)
    {
        var bytes = File.ReadAllBytes(tagged);
        byte[] language = [0x22, 0xB5, 0x9C, 0x83, (byte)'j', (byte)'p', (byte)'n'];
        var at = bytes.AsSpan(0, Math.Min(bytes.Length, 1 << 16)).IndexOf(language);
        Check(at > 0, "the sample film's Japanese track names its language");
        byte[] blank = [0xEC, 0x85, 0, 0, 0, 0, 0];
        blank.CopyTo(bytes, at);
        var path = Path.Combine(Path.GetDirectoryName(tagged)!, "Untagged Audio (2025).mkv");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    static async Task<(int ExitCode, string Errors)> RunFfmpeg(string[] arguments)
    {
        var start = new ProcessStartInfo(Ffmpeg.Find()!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(limit.Token);
        return (process.ExitCode, await errors);
    }

    // A converted stream carries sound: the track Settings › Playback prefers, then the one the phone picks. A conversion
    // for the phone once mapped no audio at all, and played silent.
    static async Task ConvertedSound(LibraryStore store, FakePhone phone, string data)
    {
        store.Setting("audioLanguages", "en");
        async Task<(System.Text.Json.JsonElement? Answer, double? Volume)> Watch(int request, int? audio, string id = "dual-audio")
        {
            phone.Send(audio is { } track
                ? new { type = "watch", request, id, quality = "480p", network = "away", audio = track }
                : (object)new { type = "watch", request, id, quality = "480p", network = "away" });
            var answer = await phone.Next("stream", 60000);
            if (answer is not { } a || a.GetProperty("kind").GetString() != "hls") return (answer, null);
            // The first piece, read as the phone reads it, and how loud its sound is.
            var resource = "stream/" + a.GetProperty("streamId").GetString() + "/0";
            var piece = new MemoryStream();
            for (var (offset, length, tries) = (0L, long.MaxValue, 0); offset < length && tries < 120;)
            {
                var read = await phone.Read(resource, offset, 262144);
                if (read.Error is not null) { Console.WriteLine($"read {resource} at {offset}: {read.Error}"); tries++; await Task.Delay(500); continue; }
                length = read.Length; piece.Write(read.Bytes); offset += read.Bytes.Length;
                if (read.Bytes.Length == 0) break;
            }
            var file = Path.Combine(data, $"piece-{request}.ts");
            await File.WriteAllBytesAsync(file, piece.ToArray());
            var (_, report) = await RunFfmpeg(["-hide_banner", "-nostdin", "-i", file, "-map", "0:a:0", "-af", "volumedetect", "-f", "null", "-"]);
            var mean = System.Text.RegularExpressions.Regex.Match(report, @"mean_volume: (-?[\d.]+|-inf) dB");
            return (answer, !mean.Success ? null : mean.Groups[1].Value == "-inf" ? double.NegativeInfinity : double.Parse(mean.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        }

        var (preferred, loud) = await Watch(30, null);
        var tracks = preferred?.GetProperty("audioTracks").EnumerateArray().Select(t => (t.GetProperty("index").GetInt32(), t.GetProperty("language").GetString(), t.GetProperty("title").ToString())).ToArray() ?? [];
        Check(preferred?.GetProperty("kind").GetString() == "hls" && tracks.SequenceEqual([(0, "jpn", ""), (1, "eng", "English")]),
            "a 480p conversion lists the file's audio tracks for the phone: " + string.Join(", ", tracks));
        Check(preferred?.GetProperty("audioTrack").GetInt32() == 1 && loud > -40,
            $"a 480p conversion has sound, in the language Settings › Playback prefers over the file's default (mean {loud} dB)");
        var (picked, quiet) = await Watch(31, 0);
        Check(picked?.GetProperty("audioTrack").GetInt32() == 0 && quiet < -80, $"the phone picks another audio track of a conversion (mean {quiet} dB)");
        var (outOfRange, _) = await Watch(32, 7);
        Check(outOfRange?.GetProperty("audioTrack").GetInt32() == 1, "a track the file doesn't have falls back to the preferred one");
        // A track the file names no language for is English to ffmpeg; the PC's player knows better, and so does this.
        string? Language(System.Text.Json.JsonElement? answer, int index) => answer?.GetProperty("audioTracks")[index].GetProperty("language").GetString();
        var (untagged, untaggedLoud) = await Watch(34, null, "untagged-audio");
        Check(untagged?.GetProperty("audioTrack").GetInt32() == 1 && Language(untagged, 0) is null && Language(untagged, 1) == "eng" && untaggedLoud > -40,
            $"a track without a language isn't taken for English (mean {untaggedLoud} dB)");
        var fileKey = LibraryStore.TrackLanguageFile(Path.Combine(data, "Untagged Audio (2025).mkv"))!;
        store.Setting(LibraryStore.TrackLanguageKey(fileKey, true, 1), "ja");
        store.Setting("audioLanguages", "ja, en");
        var (identified, _) = await Watch(35, null, "untagged-audio");
        Check(identified?.GetProperty("audioTrack").GetInt32() == 0 && Language(identified, 0) == "ja",
            "a track without a language has the one identified while it played on the PC, and Settings › Playback picks by it");
        store.Setting("audioLanguages", "");
        var (unranked, _) = await Watch(33, null);
        Check(unranked?.GetProperty("audioTrack").GetInt32() == 0, "with no preference that matches, a conversion starts with the file's default track");
    }
}
