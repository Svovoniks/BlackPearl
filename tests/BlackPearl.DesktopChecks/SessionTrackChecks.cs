using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Controls;
using LibVLCSharp.Shared;
using BlackPearl.Core;
using BlackPearl.Desktop;

// Audio and subtitles picked by hand carry on to the show's next episode, ahead of Settings › Playback: in the player,
// and for whoever plays a room's video, including what someone sharing the controls picked.
static class SessionTrackChecks
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Set(MainWindow window, string name, object? value) => typeof(MainWindow).GetField(name, Flags)!.SetValue(window, value);
    static object? Call(MainWindow window, string method, params object[] args) => typeof(MainWindow).GetMethod(method, Flags)!.Invoke(window, args);
    static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
    static async Task Wait(Func<bool> condition, string what, Action? refresh = null)
    {
        var deadline = Environment.TickCount64 + 20000;
        while (!condition() && Environment.TickCount64 < deadline) { refresh?.Invoke(); await Task.Delay(50); }
        if (!condition()) throw new Exception("FAIL: timed out waiting for " + what);
    }
    static void Pick(MainWindow window, string list, int id)
    {
        Call(window, "RefreshTracks");
        var picker = (ListBox)window.FindName(list);
        var items = picker.Items.Cast<object>().ToArray();
        picker.SelectedItem = items.FirstOrDefault(t => (int)t.GetType().GetProperty("Id")!.GetValue(t)! == id)
            ?? throw new Exception($"FAIL: {list} has no track {id}: {string.Join(", ", items)}");
    }
    static MediaItem Episode(string path, int episode) => new($"show-{episode}", path, "Session Show", "episode", Series: "Session Show", Season: 1, Episode: episode);

    public static async Task Run(string directory)
    {
        var video = await Video(directory);
        Core.Initialize();
        await Local(video);
        await Room(video, directory);
    }

    // Russian and English audio, and Russian and English subtitles: what Settings › Playback picks is never what is picked here.
    static async Task<string> Video(string directory)
    {
        var path = Path.Combine(directory, "Session Show S01E01.mkv");
        var english = Path.Combine(directory, "english.srt"); var russian = Path.Combine(directory, "russian.srt");
        await File.WriteAllTextAsync(english, "1\n00:00:00,000 --> 00:00:30,000\nEnglish line\n");
        await File.WriteAllTextAsync(russian, "1\n00:00:00,000 --> 00:00:30,000\nРусская строка\n");
        var start = new ProcessStartInfo(Ffmpeg.Find() ?? throw new Exception("FAIL: ffmpeg is needed for the session track checks"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-hide_banner", "-nostdin", "-y", "-loglevel", "error",
            "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=24,format=yuv420p", "-f", "lavfi", "-i", "sine=frequency=400:sample_rate=48000", "-f", "lavfi", "-i", "sine=frequency=800:sample_rate=48000",
            "-i", russian, "-i", english, "-t", "30", "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:s", "-map", "4:s", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-c:s", "srt",
            "-metadata:s:a:0", "language=rus", "-metadata:s:a:1", "language=eng", "-metadata:s:s:0", "language=rus", "-metadata:s:s:1", "language=eng",
            "-disposition:a:0", "default", "-disposition:s:0", "default", path }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var errors = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
        Check(process.ExitCode == 0 && File.Exists(path), "a sample episode with Russian and English audio and subtitles is made " + errors);
        return path;
    }

    static int TrackId(Media media, TrackType type, string language) => media.Tracks.Single(t => t.TrackType == type && t.Language == language).Id;

    static async Task Local(string video)
    {
        using var engine = new LibVLC("--quiet", "--vout=dummy", "--aout=dummy");
        using var player = new MediaPlayer(engine);
        var window = new MainWindow();
        try
        {
            Set(window, "player", player); Set(window, "vlc", engine);
            ((LanguagePriorityPicker)window.FindName("PreferredAudioLanguages")).Text = "Russian, English";
            ((LanguagePriorityPicker)window.FindName("PreferredSubtitleLanguages")).Text = "Russian, off";
            Call(window, "SaveTrackPreferences");
            async Task<Media> Play(MediaItem item, int generation)
            {
                Set(window, "ready", false); Set(window, "playingItem", item); Set(window, "loadGeneration", generation);
                var media = new Media(engine, item.Path, FromType.FromPath); media.AddOption(":sub-autodetect-file=0");
                player.Play(media);
                // As the app: its tracks are picked once the player says it plays (MainWindow's Playing handler).
                await Wait(() => player.State == VLCState.Playing && player.AudioTrackDescription.Count(t => t.Id >= 0) == 2 && player.SpuDescription.Count(t => t.Id >= 0) == 2, "the episode's tracks");
                Set(window, "ready", true);
                return media;
            }
            void Refresh() => Call(window, "RefreshTracks");

            using var first = await Play(Episode(video, 1), 1);
            int russianAudio = TrackId(first, TrackType.Audio, "rus"), englishAudio = TrackId(first, TrackType.Audio, "eng");
            int russianText = TrackId(first, TrackType.Text, "rus"), englishText = TrackId(first, TrackType.Text, "eng");
            await Wait(() => player.AudioTrack == russianAudio && player.Spu == russianText, "the preferences' Russian", Refresh);
            Check(true, "the first episode starts with the tracks Settings › Playback prefers");
            Pick(window, "AudioTracks", englishAudio); Pick(window, "SubtitleTracks", englishText);
            await Wait(() => player.AudioTrack == englishAudio && player.Spu == englishText, "the picked English", Refresh);

            using var second = await Play(Episode(video, 2), 2);
            await Task.Delay(500); Refresh(); await Wait(() => player.AudioTrack == englishAudio && player.Spu == englishText, "the next episode on English", Refresh);
            await Task.Delay(500); Refresh();
            Check(player.AudioTrack == englishAudio && player.Spu == englishText, "the next episode starts with the audio and subtitles picked, though the preferences say Russian");
            string Hint() { Refresh(); Call(window, "RefreshAudioPreferenceHint"); return ((TextBlock)window.FindName("AudioPreferenceHint")).Text; }
            Check(Hint().StartsWith("Started with the audio picked in an earlier episode, ahead of Settings › Preferred audio: "), "the audio menu says the track came from an earlier episode's pick: " + Hint());
            Pick(window, "SubtitleTracks", -1);
            await Wait(() => player.Spu == -1, "subtitles off", Refresh);
            Pick(window, "AudioTracks", russianAudio);
            await Task.Delay(300); Refresh();
            Check(player.Spu == -1, "subtitles picked by hand stay through another audio track");

            using var third = await Play(Episode(video, 3), 3);
            await Wait(() => player.AudioTrack == russianAudio && player.Spu == -1, "the third episode on Russian without subtitles", Refresh);
            Check(true, "turning subtitles off is remembered too");

            using var film = await Play(new MediaItem("film", video, "A Film", "movie"), 4);
            await Wait(() => player.AudioTrack == russianAudio && player.Spu == russianText, "the film on the preferences", Refresh);
            Check(true, "a film is not another episode: it starts with the preferences");
            Check(Hint().StartsWith("Default picked from Settings › Preferred audio: "), "the audio menu names the preferences for a video that started with them: " + Hint());
            var other = Episode(video, 1) with { Id = "other-1", Title = "Other Show", Series = "Other Show" };
            using var otherShow = await Play(other, 5);
            await Wait(() => player.AudioTrack == russianAudio && player.Spu == russianText, "another show on the preferences", Refresh);
            Check(true, "another show starts with the preferences, not with this show's picks");
            Pick(window, "AudioTracks", englishAudio); Pick(window, "SubtitleTracks", englishText);
            await Task.Delay(300);
            using var back = await Play(Episode(video, 4), 6);
            await Wait(() => player.AudioTrack == russianAudio && player.Spu == -1, "the first show's own picks", Refresh);
            Check(true, "each show keeps its own picks");
        }
        finally
        {
            Set(window, "player", null); Set(window, "vlc", null); Set(window, "closing", true); window.Close();
            await Task.Run(player.Stop);
        }
    }

    static async Task Room(string video, string directory)
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
        var url = "http://localhost:" + ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var sdk = File.Exists(".tools/dotnet/dotnet.exe") ? Path.GetFullPath(".tools/dotnet/dotnet.exe") : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var start = new ProcessStartInfo(sdk) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.GetFullPath("src/BlackPearl.Server/bin/Release/net10.0/BlackPearl.Server.dll"));
        start.ArgumentList.Add("--urls"); start.ArgumentList.Add(url); start.Environment["BLACKPEARL_DATA"] = Path.Combine(directory, "session-track-server");
        using var server = Process.Start(start)!; server.BeginOutputReadLine(); server.BeginErrorReadLine();
        try
        {
            using var http = new System.Net.Http.HttpClient();
            for (int attempt = 0; ; attempt++)
            {
                try { using var response = await http.GetAsync(url + "/health"); if (response.IsSuccessStatusCode) break; } catch (System.Net.Http.HttpRequestException) { }
                if (attempt >= 100) throw new IOException("Session track server did not start"); await Task.Delay(50);
            }
            await using var host = new RoomClient(); await using var guest = new RoomClient();
            await host.ConnectAsync(url, "Session Host"); await guest.ConnectAsync(url, "Session Guest", host.Identity!.Room);
            host.Send(new("admit", Target: guest.Identity!.Peer)); await guest.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(25));
            using var hostEngine = new LibVLC("--quiet", "--vout=dummy", "--aout=dummy");
            using var guestEngine = new LibVLC("--quiet", "--vout=dummy", "--aout=dummy");
            using var hostPlayer = new MediaPlayer(hostEngine); using var guestPlayer = new MediaPlayer(guestEngine);
            var hostWindow = new MainWindow(); var guestWindow = new MainWindow();
            try
            {
                Set(hostWindow, "player", hostPlayer); Set(hostWindow, "vlc", hostEngine); Set(hostWindow, "room", host);
                Set(guestWindow, "player", guestPlayer); Set(guestWindow, "vlc", guestEngine); Set(guestWindow, "room", guest);
                ((LanguagePriorityPicker)hostWindow.FindName("PreferredAudioLanguages")).Text = "Russian, English";
                ((LanguagePriorityPicker)hostWindow.FindName("PreferredSubtitleLanguages")).Text = "Russian, off";
                Call(hostWindow, "SaveTrackPreferences");
                void Refresh() { Call(hostWindow, "RefreshTracks"); Call(guestWindow, "RefreshTracks"); }
                async Task<(Media Host, Media Guest)> Play(MediaItem item, int generation, string copy)
                {
                    File.Copy(video, copy, true);
                    Set(hostWindow, "ready", false); Set(guestWindow, "ready", false);
                    host.PublishHostedFile(RoomClient.PrepareHostedFile(copy, item.QueueTitle), true);
                    await Wait(() => guest.Snapshot?.Media?.Title == item.QueueTitle && host.Snapshot?.Media?.Title == item.QueueTitle, "the room's next video");
                    var id = host.Snapshot!.Media!.Id;
                    Set(hostWindow, "playingItem", item with { Path = copy });
                    foreach (var window in new[] { hostWindow, guestWindow }) { Set(window, "loadGeneration", generation); Set(window, "loadedMedia", id); }
                    var hostMedia = new Media(hostEngine, copy, FromType.FromPath); var guestMedia = new Media(guestEngine, copy, FromType.FromPath);
                    hostMedia.AddOption(":sub-autodetect-file=0"); guestMedia.AddOption(":sub-autodetect-file=0");
                    hostPlayer.Play(hostMedia); guestPlayer.Play(guestMedia);
                    await Wait(() => new[] { hostPlayer, guestPlayer }.All(p => p.State == VLCState.Playing && p.AudioTrackDescription.Count(t => t.Id >= 0) == 2 && p.SpuDescription.Count(t => t.Id >= 0) == 2), "the room video's tracks");
                    Set(hostWindow, "ready", true); Set(guestWindow, "ready", true);
                    return (hostMedia, guestMedia);
                }
                var (firstHost, firstGuest) = await Play(Episode(video, 1), 1, Path.Combine(directory, "room-1.mkv"));
                int russianAudio = TrackId(firstHost, TrackType.Audio, "rus"), englishAudio = TrackId(firstHost, TrackType.Audio, "eng");
                int russianText = TrackId(firstHost, TrackType.Text, "rus"), englishText = TrackId(firstHost, TrackType.Text, "eng");
                bool Both(int audio, int text) => hostPlayer.AudioTrack == audio && guestPlayer.AudioTrack == audio && hostPlayer.Spu == text && guestPlayer.Spu == text;
                await Wait(() => Both(russianAudio, russianText), "the host's preferences in the room", Refresh);
                // Someone sharing the controls picks the tracks for everyone.
                await Wait(() => guest.Snapshot?.SharedControls == true, "shared controls");
                Refresh(); Pick(guestWindow, "AudioTracks", englishAudio);
                await Wait(() => Both(englishAudio, englishText) || hostPlayer.AudioTrack == englishAudio && guestPlayer.AudioTrack == englishAudio, "the guest's English audio", Refresh);
                Refresh(); Pick(guestWindow, "SubtitleTracks", englishText);
                await Wait(() => Both(englishAudio, englishText), "the guest's English subtitles", Refresh);
                Check(true, "a guest sharing the controls picks the room's tracks");

                var (secondHost, secondGuest) = await Play(Episode(video, 2), 2, Path.Combine(directory, "room-2.mkv"));
                await Wait(() => Both(englishAudio, englishText) && host.Snapshot?.Tracks?.MediaId == host.Snapshot?.Media?.Id, "the room's next episode on English", Refresh);
                await Task.Delay(500); Refresh();
                Check(Both(englishAudio, englishText), "the room's next episode starts, for everyone, with the tracks picked in the last one, though the host's preferences say Russian");
                foreach (var media in new[] { firstHost, firstGuest, secondHost, secondGuest }) media.Dispose();
            }
            finally
            {
                foreach (var window in new[] { hostWindow, guestWindow })
                {
                    Call(window, "ResetRoomTracks"); Set(window, "room", null); Set(window, "player", null); Set(window, "vlc", null); Set(window, "closing", true); window.Close();
                }
                await Task.WhenAll(Task.Run(hostPlayer.Stop), Task.Run(guestPlayer.Stop));
            }
        }
        finally { if (!server.HasExited) server.Kill(true); await server.WaitForExitAsync(); }
    }
}
