using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using LibVLCSharp.Shared;
using BlackPearl.Core;
using BlackPearl.Desktop;

// The room player's stream quality, in the real window against a real room service, with the real converter.
// First the window plays a video for the room and chooses its quality; then it watches a video someone else
// converts. The other member is a plain RoomClient.
static class StreamQualityChecks
{
    public static async Task Run(MainWindow window, LibraryStore store, string directory, Action<bool, string> check)
    {
        PlaybackDiagnostics.Initialize(Path.Combine(directory, "logs"));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Field(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window);
        object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        T Control<T>(string name) where T : class => (T)window.FindName(name);
        string Text(string name) => Control<TextBlock>(name).Text;
        async Task Wait(Func<bool> condition, string label, int seconds = 40)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (!condition()) { if (DateTime.UtcNow > until) throw new TimeoutException("Timeout: " + label); await Task.Delay(25); }
            check(true, label);
        }
        var shots = Path.GetFullPath(Path.Combine("artifacts", "stream-quality-ui", $"{System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleX * 100:F0}")); Directory.CreateDirectory(shots);
        // The player's bars and menus float over the window in windows of their own; each is saved as it is drawn.
        async Task Shot(string name)
        {
            await Task.Delay(500); window.UpdateLayout();
            var index = 0;
            foreach (var root in PresentationSource.CurrentSources.OfType<System.Windows.Interop.HwndSource>().Select(source => source.RootVisual).OfType<FrameworkElement>()
                .Where(root => !ReferenceEquals(root, window) && root.IsVisible && root.ActualWidth > 40 && root.ActualHeight > 20).ToArray())
            {
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
                var picture = new System.Windows.Media.DrawingVisual();
                using (var context = picture.RenderOpen())
                {
                    var bounds = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
                    context.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0B, 0x0B, 0x0E)), null, bounds);
                    context.DrawRectangle(new System.Windows.Media.VisualBrush(root) { Stretch = System.Windows.Media.Stretch.None, AlignmentX = System.Windows.Media.AlignmentX.Left, AlignmentY = System.Windows.Media.AlignmentY.Top }, null, bounds);
                }
                bitmap.Render(picture);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(shots, $"{name}-surface{++index}.png")); encoder.Save(file);
            }
        }
        string[] Choices() => Control<ListBox>("QualityChoices").Items.Cast<object>().Select(item => item.ToString()!).ToArray();
        void Choose(string label)
        {
            var list = Control<ListBox>("QualityChoices");
            list.SelectedItem = list.Items.Cast<object>().Single(item => item.ToString() == label);
        }

        var ffmpeg = Ffmpeg.Find();
        check(ffmpeg is not null && Path.GetDirectoryName(ffmpeg)!.EndsWith("ffmpeg", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(Path.GetDirectoryName(ffmpeg)!, "LICENSE.txt")),
            "the app carries its own converter, with its license");
        // A film large enough to be worth converting, with a subtitle track, and one that is already small.
        var film = Path.Combine(directory, "quality-film.mkv"); var small = Path.Combine(directory, "quality-small.mkv");
        var captions = Path.Combine(directory, "quality-captions.srt");
        await File.WriteAllTextAsync(captions, "1\n00:00:01,000 --> 00:00:50,000\nA line of dialogue\n");
        foreach (var (file, source, rate) in new[] { (film, "testsrc2=size=1920x1080:rate=24", "8M"), (small, "color=c=0x303030:size=640x360:rate=24", "200k") })
        {
            var made = Process.Start(new ProcessStartInfo(ffmpeg!, $"-hide_banner -nostdin -y -loglevel error -f lavfi -i {source},format=yuv420p -f lavfi -i sine=frequency=330:sample_rate=48000 -i \"{captions}\" " +
                $"-t 60 -map 0:v -map 1:a -map 2:s -c:v libx264 -preset ultrafast -b:v {rate} -maxrate {rate} -bufsize 16M -g 48 -c:a aac -c:s srt \"{file}\"") { UseShellExecute = false, CreateNoWindow = true })!;
            await made.WaitForExitAsync();
            check(made.ExitCode == 0 && File.Exists(file), "a sample video is made: " + Path.GetFileName(file));
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var url = "http://localhost:" + ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var sdk = File.Exists(".tools/dotnet/dotnet.exe") ? Path.GetFullPath(".tools/dotnet/dotnet.exe") : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var info = new ProcessStartInfo(sdk) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.Environment["BLACKPEARL_DATA"] = Path.Combine(directory, "stream-quality-server");
        info.ArgumentList.Add(Path.GetFullPath("src/BlackPearl.Server/bin/Release/net10.0/BlackPearl.Server.dll"));
        info.ArgumentList.Add("--urls"); info.ArgumentList.Add(url);
        using var server = Process.Start(info)!;
        _ = server.StandardOutput.ReadToEndAsync(); _ = server.StandardError.ReadToEndAsync();
        var others = new List<RoomClient>();
        var previousName = store.Setting("name"); var previousQuality = store.Setting(StreamQuality.SettingKey);
        try
        {
            using var http = new System.Net.Http.HttpClient();
            for (var attempt = 0; ; attempt++)
            {
                try { using var response = await http.GetAsync(url + "/health"); if (response.IsSuccessStatusCode) break; } catch (System.Net.Http.HttpRequestException) { }
                if (attempt >= 100) throw new IOException("Stream quality fixture server did not start"); await Task.Delay(50);
            }
            store.Setting("name", "Sam");

            // Sam plays a film for the room. The quality saved from last time is 480p.
            store.Setting(StreamQuality.SettingKey, "480p");
            var own = (RoomClient)Call("NewRoom")!;
            await own.ConnectAsync(url, "Sam", persistent: false, roomName: "Quality");
            await own.StartStreamAsync(RoomClient.PrepareHostedFile(film, "Quality film"), true);
            Call("ShowPage", "Room");
            await Wait(() => (bool)Field("ready")! && own.Snapshot?.Stream is { Quality: "480p", Height: 480 }, "the saved quality is applied to the video played for the room");
            var first = own.Snapshot!.Stream!;
            check(first.Segments == (int)Math.Ceiling(first.DurationMs / 4000.0) && Math.Abs(first.DurationMs - 60000) < 300 && own.HostedStream?.Stream == first, "the converted stream covers the whole film in four-second pieces");
            Call("ShowPlayerControls"); Call("PlaybackTickCore");
            check(PillStrip.GetShown(Control<ToggleButton>("QualityPill")) && Text("QualityPillText") == "480p", "the player's quality pill shows the chosen quality (" + Text("QualityPillText") + ")");
            check(Choices().SequenceEqual(["Original", "1080p", "720p", "480p"]) && Control<ListBox>("QualityChoices").IsEnabled, "whoever plays the video chooses among the qualities it is large enough for");
            await Shot("1-streamer-pill");
            Control<Popup>("QualityMenu").IsOpen = true;
            await Shot("2-streamer-menu");
            Control<Popup>("QualityMenu").IsOpen = false;

            // Someone else in the room receives the converted video over a real connection.
            var riley = new RoomClient(); others.Add(riley);
            await riley.ConnectAsync(url, "Riley", own.Identity!.Room);
            await riley.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(30));
            await Wait(() => riley.Snapshot?.Stream == first, "everyone in the room is told about the converted stream");
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var size = BitConverter.ToInt64(await riley.ReadStreamAsync(first, StreamConverter.Resource(first.Id, 2, size: true), 0, 8, limit.Token));
            var head = await riley.ReadStreamAsync(first, StreamConverter.Resource(first.Id, 2), 0, 376, limit.Token);
            check(size > 20000 && size % 188 == 0 && head[0] == 0x47 && head[188] == 0x47, $"a viewer reads a converted piece from the app that plays the video ({size / 1000} KB)");

            // Changing the quality replaces the stream; Original goes back to the file.
            Choose("720p");
            await Wait(() => own.Snapshot?.Stream is { Quality: "720p", Height: 720 } replaced && replaced.Id != first.Id && riley.Snapshot?.Stream?.Id == replaced.Id, "choosing another quality replaces the stream for everyone");
            check(store.Setting(StreamQuality.SettingKey) == "720p" && Text("QualityPillText") == "720p", "the choice is remembered for the next video");
            var second = own.Snapshot!.Stream!;
            try { await riley.ReadStreamAsync(first, StreamConverter.Resource(first.Id, 2, size: true), 0, 8, limit.Token); check(false, "the replaced stream is gone"); }
            catch (IOException) { check(true, "the replaced stream is no longer served"); }
            // The subtitles drawn into the picture are the ones selected in the player.
            var subtitles = Control<ListBox>("SubtitleTracks");
            await Wait(() => subtitles.Items.Count >= 2, "the film's subtitle track is listed");
            var other = subtitles.Items.Cast<object>().First(item => !Equals(item, subtitles.SelectedItem));
            subtitles.SelectedItem = other;
            await Wait(() => own.Snapshot?.Stream is { Quality: "720p" } redrawn && redrawn.Id != second.Id, "changing the subtitles makes a new stream with them drawn in or left out");
            Choose("Original");
            await Wait(() => own.Snapshot is { Stream: null, Media: not null } && own.HostedStream is null && Field("streamConverter") is null, "Original streams the file itself and stops converting");
            check(Text("QualityPillText") == "Original" && store.Setting(StreamQuality.SettingKey) == StreamQuality.OriginalId, "the pill and the saved choice go back to Original");

            // A video that is already small is not made larger; the menu says so.
            Choose("480p");
            await Wait(() => own.Snapshot?.Stream is { Quality: "480p" }, "a lower quality converts again");
            await own.StartStreamAsync(RoomClient.PrepareHostedFile(small, "Small film"), true);
            await Wait(() => Text("QualityHint").Contains("already smaller"), "a video already smaller than the chosen quality is streamed as it is, and the menu says why");
            check(own.Snapshot?.Stream is null && Choices().SequenceEqual(["Original", "480p"]) && store.Setting(StreamQuality.SettingKey) == "480p", "the choice stays for the next video, and only fitting qualities are offered");
            Control<Popup>("QualityMenu").IsOpen = true;
            await Shot("3-streamer-menu-small-video");
            Control<Popup>("QualityMenu").IsOpen = false;
            await (Task)Call("Disconnect", false, false)!;
            check(Field("streamConverter") is null && !PillStrip.GetShown(Control<ToggleButton>("QualityPill")), "leaving the room stops converting and puts the pill away");
            foreach (var client in others) await client.DisposeAsync();
            others.Clear();

            // Now Riley plays the film, converted on Riley's side, and Sam watches.
            var host = new RoomClient(); others.Add(host);
            await host.ConnectAsync(url, "Riley", persistent: false, roomName: "Riley's room");
            await host.StartStreamAsync(RoomClient.PrepareHostedFile(film, "Riley's film"), true);
            var probe = (await VideoProbe.ReadAsync(ffmpeg!, film, default))!;
            var quality = StreamQuality.Find("480p")!;
            var offered = new RoomStream(Guid.NewGuid().ToString("N"), host.Snapshot!.Media!.Id, quality.Id, 480, quality.VideoKbps, probe.DurationMs, StreamConverter.SegmentMs, (int)((probe.DurationMs + StreamConverter.SegmentMs - 1) / StreamConverter.SegmentMs));
            using var hostStore = SegmentStore.TryCreate(Path.Combine(directory, "host-converted"), offered.Segments)!;
            using var converter = new StreamConverter(ffmpeg!, new StreamInput(film, probe, 0, null), offered, hostStore);
            host.HostedStream = converter; host.Send(new("stream", Data: Wire.Serialize(offered)));
            void HostReady()
            {
                if (host.Snapshot is { Media: { } media, Playback: { } playback })
                    try { host.Send(new("ready", Data: Wire.Serialize(new PlaybackReadiness(media.Id, playback.Generation)))); } catch (IOException) { }
            }
            HostReady();
            var me = (RoomClient)Call("NewRoom")!;
            await me.ConnectAsync(url, "Sam", host.Identity!.Room);
            Call("ShowPage", "Room");
            await Wait(() => Field("bridge") is MediaBridge { PlaysConvertedStream: true } && (bool)Field("ready")! && (string?)Field("loadedStream") == offered.Id, "a viewer opens the converted stream instead of the file");
            var player = (MediaPlayer)Field("player")!;
            HostReady();
            await Wait(() => player.IsPlaying && player.Time > 500, "the converted stream plays");
            check(Math.Abs(player.Length - probe.DurationMs) < 500 && player.IsSeekable, $"it has the film's full length ({player.Length} ms) and can seek");
            Call("ShowPlayerControls"); Call("PlaybackTickCore");
            check(PillStrip.GetShown(Control<ToggleButton>("QualityPill")) && Text("QualityPillText") == "480p" && !Control<ListBox>("QualityChoices").IsEnabled && Text("QualityHint").Contains("Riley"),
                "a viewer sees the quality and who chooses it, and cannot change it");
            check(!Control<ListBox>("AudioTracks").IsEnabled && !Control<ListBox>("SubtitleTracks").IsEnabled, "a converted stream leaves a viewer no tracks to change");
            await Shot("4-viewer-pill");
            Control<Popup>("QualityMenu").IsOpen = true;
            await Shot("5-viewer-menu");
            Control<Popup>("QualityMenu").IsOpen = false;
            await Wait(() => Field("bridge") is MediaBridge { BufferedAheadBytes: > 0 } buffering && hostStore.Count >= offered.Segments - 1, "the rest of the converted film is read ahead while the start plays");

            // The room seeks, and the viewer follows on the converted stream.
            host.Send(new("playback", Data: Wire.Serialize(new PlaybackState(0, offered.MediaId, true, 40000, 0, Seek: true))));
            await Task.Delay(300); HostReady();
            await Wait(() => player.IsPlaying && player.Time is > 40500 and < 52000, "a seek in the room lands at the same place in the converted stream");

            // The streamer goes back to the file: the viewer reopens it and carries on from the room's position.
            host.HostedStream = null; host.Send(new("stream"));
            await Wait(() => Field("bridge") is MediaBridge { PlaysConvertedStream: false } && (bool)Field("ready")! && Field("loadedStream") is null, "when the stream is withdrawn the viewer opens the file");
            HostReady();
            await Wait(() => player.IsPlaying && player.Time is > 40000 and < 59000, "and playback continues from where the room is");
            check(Text("QualityPillText") == "Original", "the viewer's pill follows the room back to Original");
        }
        finally
        {
            await (Task)Call("Disconnect", false, false)!;
            foreach (var client in others) try { await client.DisposeAsync(); } catch (Exception) { }
            if (previousName is null) store.Setting("name", Environment.UserName); else store.Setting("name", previousName);
            store.Setting(StreamQuality.SettingKey, previousQuality ?? StreamQuality.OriginalId);
            if (!server.HasExited) { server.Kill(true); await server.WaitForExitAsync(); }
            await PlaybackDiagnostics.FlushAsync();
        }
    }
}
