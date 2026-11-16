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
            await Wait(() => own.Snapshot?.Stream is { Quality: "480p" }, "as the video starts, the room is told a converted stream is coming", 5);
            await Wait(() => (bool)Field("ready")! && own.Snapshot?.Stream is { Quality: "480p", Height: 480, Preparing: false }, "the saved quality is applied to the video played for the room");
            var first = own.Snapshot!.Stream!;
            check(first.Segments == (int)Math.Ceiling(first.DurationMs / 4000.0) && Math.Abs(first.DurationMs - 60000) < 300 && own.HostedStream?.Stream == first, "the converted stream covers the whole film in four-second pieces");
            Call("ShowPlayerControls"); Call("PlaybackTickCore");
            check(PillStrip.GetShown(Control<ToggleButton>("QualityPill")) && Text("QualityPillText") == "480p", "the player's quality pill shows the chosen quality (" + Text("QualityPillText") + ")");
            Call("PlaybackTickCore"); window.UpdateLayout(); await Task.Delay(300); Call("PlaybackTickCore"); window.UpdateLayout();
            var strip = Control<PillStrip>("PlayerPills");
            Console.WriteLine("Pills: " + string.Join(", ", new[] { "AudioPill", "SubtitlePill", "QueuePill", "ParticipantsPill", "QualityPill", "OverflowPill" }.Select(name => $"{name} {Control<ToggleButton>(name).DesiredSize.Width:F0}{(strip.Folded.Contains(Control<ToggleButton>(name)) ? " folded" : "")}")) + $"; strip {strip.ActualWidth:F0} of {((FrameworkElement)strip.Parent).ActualWidth:F0}");
            // A screen too narrow for the wider bar, such as a build runner's, folds the quality pill into the … menu.
            var qualityFolded = strip.Folded.Contains(Control<ToggleButton>("QualityPill"));
            check(strip.Folded.All(pill => pill == Control<ToggleButton>("QualityPill")) && (!qualityFolded || window.ActualWidth < 1200),
                $"the bar grows by the quality pill's width, so no other pill is pushed out of it{(qualityFolded ? "; this window is too narrow for it, and the quality pill is the one that folds" : "")}");
            check(Choices().SequenceEqual(["Original", "1080p", "720p", "480p"]) && Control<ListBox>("QualityChoices").IsEnabled, "whoever plays the video chooses among the qualities it is large enough for");
            await Shot("1-streamer-pill");
            // The pill opens its menu like the others: above the pill, and gone when another pill's menu opens.
            var qualityPill = Control<ToggleButton>("QualityPill"); var qualityMenu = Control<Popup>("QualityMenu");
            // A folded pill's menu opens from the … pill, as its row in the … menu does.
            var anchor = qualityFolded ? Control<ToggleButton>("OverflowPill") : qualityPill;
            void OpenQualityMenu() { if (qualityFolded) qualityMenu.IsOpen = true; else qualityPill.IsChecked = true; }
            OpenQualityMenu();
            await Wait(() => qualityMenu.IsOpen && qualityMenu.Child is FrameworkElement { ActualHeight: > 100 }, "the quality pill opens its menu", 5);
            await Shot("2-streamer-menu");
            var card = (FrameworkElement)qualityMenu.Child;
            var above = card.PointToScreen(new Point(0, card.ActualHeight)).Y <= anchor.PointToScreen(new Point(0, anchor.ActualHeight)).Y + 1;
            check(above && card.ActualHeight < 400 && card.ActualWidth < 500 && qualityMenu.PlacementTarget == anchor, $"the menu is a compact card above its pill ({card.ActualWidth:F0} by {card.ActualHeight:F0})");
            // It is one of the player's menus: the bar stays while it is open, and a click on a quality, Escape, or
            // the bar going away closes it. The window is off the screen, so the click and the key are raised.
            bool MenuOpen() => (bool)typeof(MainWindow).GetProperty("PlayerMenuOpen", flags)!.GetValue(window)!;
            typeof(MainWindow).GetField("lastPlayerInteraction", flags)!.SetValue(window, Environment.TickCount64 - 10000);
            Call("PlaybackTickCore");
            check(MenuOpen() && qualityMenu.IsOpen && Control<FrameworkElement>("PlaybackControls").Visibility == Visibility.Visible, "the player's bar stays while the quality menu is open");
            var qualities = Control<ListBox>("QualityChoices");
            ListBoxItem Row() { qualities.UpdateLayout(); return (ListBoxItem)qualities.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, qualities.SelectedIndex)); }
            await Wait(() => Row() is { IsKeyboardFocusWithin: true } || qualities.IsKeyboardFocusWithin, "opening the menu moves the keyboard into its list", 5);
            Row().RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left) { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseUpEvent });
            await Wait(() => !qualityMenu.IsOpen && !MenuOpen(), "a click on a quality closes the menu", 5);
            OpenQualityMenu();
            await Wait(() => qualityMenu.IsOpen && qualityMenu.Child is FrameworkElement { ActualHeight: > 100 }, "the menu opens again", 5);
            Row().RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(Row())!, Environment.TickCount, System.Windows.Input.Key.Escape) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            await Wait(() => !qualityMenu.IsOpen, "Escape closes the menu", 5);
            OpenQualityMenu();
            await Wait(() => qualityMenu.IsOpen, "the menu opens once more", 5);
            Call("ClosePlayerMenusNow");
            check(!qualityMenu.IsOpen, "the menu goes at once when the bar does");
            OpenQualityMenu();
            await Wait(() => qualityMenu.IsOpen, "the menu opens for the next check", 5);
            Control<ToggleButton>("AudioPill").IsChecked = true;
            await Wait(() => Control<Popup>("AudioMenu").IsOpen && !qualityMenu.IsOpen && qualityPill.IsChecked == false, "opening another pill's menu closes the quality menu", 5);
            Call("ClosePlayerMenus");
            await Wait(() => !Control<Popup>("AudioMenu").IsOpen, "the menus close", 5);

            // Someone else in the room receives the converted video over a real connection.
            var riley = new RoomClient(); others.Add(riley);
            await riley.ConnectAsync(url, "Riley", own.Identity!.Room);
            await riley.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(30));
            await Wait(() => riley.Snapshot?.Stream == first, "everyone in the room is told about the converted stream");
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var size = BitConverter.ToInt64(await riley.ReadStreamAsync(first, StreamConverter.Resource(first.Id, 2, size: true), 0, 8, limit.Token));
            var head = await riley.ReadStreamAsync(first, StreamConverter.Resource(first.Id, 2), 0, 376, limit.Token);
            check(size > 20000 && size % 188 == 0 && head[0] == 0x47 && head[188] == 0x47, $"a viewer reads a converted piece from the app that plays the video ({size / 1000} KB)");

            // Sam's own connection to the room drops and comes back. The conversion is kept and offered again as the
            // stream it was, so Riley, who stayed, continues with the pieces already downloaded.
            var conversion = (StreamConverter)Field("streamConverter")!; var convertedBefore = conversion.ConvertedSegments;
            await ((Task)Call("ReconnectRoomAsync", own)!).WaitAsync(TimeSpan.FromSeconds(60));
            await Wait(() => Field("room") is RoomClient { IsConnected: true } back && back != own && (bool)Field("ready")! && back.Snapshot?.Stream == first && ReferenceEquals(Field("streamConverter"), conversion) && back.HostedStream == conversion,
                "after the streamer's own reconnect, the conversion it kept is offered again as the same stream", 45);
            own = (RoomClient)Field("room")!;
            await Wait(() => riley.Snapshot?.Stream == first && riley.Snapshot.Media?.Id == first.MediaId, "a viewer who stayed is offered the stream it already had");
            var again = await riley.ReadStreamAsync(first, StreamConverter.Resource(first.Id, 2), 0, 376, limit.Token);
            await Task.Delay(2500); Call("PlaybackTickCore");
            Console.WriteLine($"Streamer reconnect: {convertedBefore} pieces converted before, {conversion.ConvertedSegments} after; stream {(own.Snapshot?.Stream?.Id == first.Id ? "kept" : "replaced")}");
            check(again.AsSpan().SequenceEqual(head) && conversion.ConvertedSegments >= convertedBefore && conversion.Failure is null && own.Snapshot?.Stream == first && ReferenceEquals(Field("streamConverter"), conversion),
                "the pieces served after the reconnect are the ones converted before it, and the stream stays once the tracks have settled");

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

            // Original says what sending the file takes, and the menu what the viewer's connection carries.
            string Detail(int row) { var item = Control<ListBox>("QualityChoices").Items[row]!; return (string)item.GetType().GetProperty("Detail")!.GetValue(item)!; }
            await Wait(() => System.Text.RegularExpressions.Regex.IsMatch(Detail(0), @"^The file, about \d+(\.\d)? Mbps$"), "Original says what sending the file takes", 10);
            Console.WriteLine("Original: " + Detail(0));
            var offerCard = Control<Border>("QualityOffer");
            // Riley's app reports its speed every second, as a viewer's does every two while it pulls video.
            var reported = 50000;
            using var reporting = new Timer(_ => { if (Volatile.Read(ref reported) is > 0 and var kbps) typeof(RoomClient).GetMethod("ReportReceiveRate", flags)!.Invoke(riley, [kbps]); }, null, 0, 1000);
            await Wait(() => Text("QualityHint").Contains("Riley is receiving about 50 Mbps"), "the menu says how fast the video reaches the viewer", 10);
            await Task.Delay(1000);
            check(offerCard.Visibility != Visibility.Visible && !Text("QualityHint").Contains("would keep up"), "a connection that carries the video gets no offer");

            // Riley's connection carries 2 Mbps of a film that takes 8. Once that has lasted, a quality that fits is offered.
            Volatile.Write(ref reported, 2000);
            await Wait(() => Text("QualityHint").Contains("about 2 Mbps"), "the menu follows the measurement", 10);
            var slow = Stopwatch.StartNew();
            check(offerCard.Visibility != Visibility.Visible && Text("QualityHint").Contains("480p would keep up"), "the menu says at once which quality would keep up: " + Text("QualityHint"));
            await Wait(() => offerCard.Visibility == Visibility.Visible, "a lower quality is offered when the viewer receives less than is sent", 30);
            check(slow.ElapsedMilliseconds >= 8500, $"the offer waits for the slow connection to last ({slow.ElapsedMilliseconds} ms)");
            Console.WriteLine("Offer: " + Text("QualityOfferText") + " [" + Text("QualityOfferSwitchText") + "]");
            check(Text("QualityOfferText").Contains("Riley is receiving about 2 Mbps") && Text("QualityOfferText").Contains($"480p (up to {StreamQuality.Find("480p")!.TotalMbps(Ffmpeg.HevcEncoder is not null):0.#} Mbps) would keep up") && Text("QualityOfferSwitchText") == "Switch to 480p",
                "the offer names the viewer, the speeds and the best quality that fits");
            Call("ShowPlayerControls"); window.UpdateLayout(); await Task.Delay(400); window.UpdateLayout();
            var dock = Control<FrameworkElement>("PlaybackDock"); var offerText = Control<TextBlock>("QualityOfferText");
            var offerRoot = (FrameworkElement)PresentationSource.FromVisual(offerCard)!.RootVisual;
            var cardBox = offerCard.TransformToAncestor(offerRoot).TransformBounds(new Rect(0, 0, offerCard.ActualWidth, offerCard.ActualHeight));
            var dockBox = dock.TransformToAncestor(offerRoot).TransformBounds(new Rect(0, 0, dock.ActualWidth, dock.ActualHeight));
            Console.WriteLine($"Offer card {cardBox}; dock {dockBox}; surface {offerRoot.ActualWidth:F0} by {offerRoot.ActualHeight:F0}");
            check(cardBox.Bottom <= dockBox.Top && cardBox.Left >= 0 && cardBox.Right <= offerRoot.ActualWidth && cardBox.Top >= 0 && offerText.ActualHeight >= offerText.DesiredSize.Height - 0.5 && offerText.ActualWidth <= offerCard.ActualWidth,
                "the offer sits above the bar, inside the player, with its whole text shown");
            await Shot("4-quality-offer");
            Control<ToggleButton>("QualityPill").IsChecked = true;
            await Wait(() => Control<Popup>("QualityMenu").IsOpen, "the quality menu opens beside the offer", 5);
            await Shot("5-menu-with-speed");
            Call("ClosePlayerMenus");
            var buttons = ((Panel)((FrameworkElement)Control<TextBlock>("QualityOfferSwitchText").Parent).Parent).Children.OfType<Button>().ToArray();
            // The checks' window is kept off the screen, where the mouse cannot reach: the buttons' clicks are raised.
            buttons[1].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Wait(() => own.Snapshot?.Stream is { Quality: "480p", Preparing: false } && offerCard.Visibility != Visibility.Visible && store.Setting(StreamQuality.SettingKey) == "480p" && Text("QualityPillText") == "480p",
                "accepting switches the room's stream to the offered quality and puts the offer away");
            await Task.Delay(1500);
            check(offerCard.Visibility != Visibility.Visible && !Text("QualityHint").Contains("would keep up"), "nothing more is offered once what is sent fits the connection");

            // Back on the file, the offer comes again only because the check clears its memory of the answer; "Not now" keeps the quality.
            Choose("Original");
            await Wait(() => own.Snapshot is { Stream: null } && Text("QualityHint").Contains("480p would keep up"), "back on the file, the menu says again what would keep up", 15);
            await Task.Delay(1200);
            check(offerCard.Visibility != Visibility.Visible, "a video whose offer was answered is not offered again");
            typeof(MainWindow).GetField("qualityOfferAnswered", flags)!.SetValue(window, null);
            typeof(MainWindow).GetField("qualitySlowSince", flags)!.SetValue(window, Environment.TickCount64 - 20000);
            await Wait(() => offerCard.Visibility == Visibility.Visible, "the offer is shown again for the check", 10);
            buttons[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Wait(() => offerCard.Visibility != Visibility.Visible, "Not now puts the offer away", 5);
            await Task.Delay(1500);
            check(offerCard.Visibility != Visibility.Visible && store.Setting(StreamQuality.SettingKey) == StreamQuality.OriginalId && own.Snapshot is { Stream: null } && Text("QualityHint").Contains("480p would keep up"),
                "the quality stays as it was, the offer stays away, and the menu still says what would keep up");
            Volatile.Write(ref reported, 0);

            // A video that is already small is not made larger; the menu says so.
            Choose("480p");
            await Wait(() => own.Snapshot?.Stream is { Quality: "480p" }, "a lower quality converts again");
            await own.StartStreamAsync(RoomClient.PrepareHostedFile(small, "Small film"), true);
            await Wait(() => Text("QualityHint").Contains("already smaller"), "a video already smaller than the chosen quality is streamed as it is, and the menu says why");
            check(own.Snapshot?.Stream is null && Choices().SequenceEqual(["Original", "480p"]) && store.Setting(StreamQuality.SettingKey) == "480p", "the choice stays for the next video, and only fitting qualities are offered");
            Control<Popup>("QualityMenu").IsOpen = true;
            await Shot("3-streamer-menu-small-video");
            Control<Popup>("QualityMenu").IsOpen = false;

            // The next video that will be converted is announced like the first one, although the conversion of the
            // video before it is still there when it starts.
            await own.StartStreamAsync(RoomClient.PrepareHostedFile(film, "Quality film"), true);
            await Wait(() => own.Snapshot?.Stream is { Quality: "480p", Preparing: false } && Field("streamConverter") is not null, "the film converts again");
            var next = RoomClient.PrepareHostedFile(film, "Next film"); var nextId = next.Media.Media.Id;
            await own.StartStreamAsync(next, true);
            var coming = false; var told = new List<string>();
            for (var until = Environment.TickCount64 + 20000; Environment.TickCount64 < until; await Task.Delay(15))
            {
                if (own.Snapshot is not { Media: { } playing } state || playing.Id != nextId) continue;
                var now = state.Stream is null ? "file" : state.Stream.Preparing ? "coming" : "stream";
                if (told.LastOrDefault() != now) told.Add(now);
                if (now == "coming") coming = true;
                if (now == "stream") break;
            }
            Console.WriteLine("Next video: " + string.Join(", ", told));
            check(coming && told.LastOrDefault() == "stream", "the next video's stream is announced before it is ready, so viewers wait for it");

            // Music has no picture. Nothing is announced or converted, nobody is told a conversion failed, and there
            // is no quality to choose.
            var song = Path.Combine(directory, "quality-song.mp3");
            using (var made = Process.Start(new ProcessStartInfo(ffmpeg!, $"-hide_banner -nostdin -y -loglevel error -f lavfi -i sine=frequency=440:sample_rate=44100 -t 30 \"{song}\"") { UseShellExecute = false, CreateNoWindow = true })!) await made.WaitForExitAsync();
            Control<TextBlock>("StatusText").Text = "";
            var music = RoomClient.PrepareHostedFile(song, "Quality song"); var musicId = music.Media.Media.Id;
            await own.StartStreamAsync(music, true);
            var songOffered = false;
            for (var until = Environment.TickCount64 + 5000; Environment.TickCount64 < until; await Task.Delay(15)) songOffered |= own.Snapshot is { Stream: not null, Media: { } playing } && playing.Id == musicId;
            Call("PlaybackTickCore");
            Console.WriteLine($"Music: status \"{Text("StatusText")}\", pill {(PillStrip.GetShown(qualityPill) ? "shown" : "hidden")}");
            check(File.Exists(song) && own.Snapshot?.Media?.Id == musicId && !songOffered && own.Snapshot.Stream is null && !Text("StatusText").Contains("convert", StringComparison.OrdinalIgnoreCase) && !PillStrip.GetShown(qualityPill),
                "music is streamed as its file: no stream is announced, no failure is reported, and there is no quality pill");

            // A converter that cannot start, as when security software holds it. The video is asked about once, not
            // with every tick, and the menu says the file is streamed as it is.
            var unread = Path.Combine(directory, "quality-unread.mkv"); File.Copy(small, unread, true);
            var notProgram = Path.Combine(directory, "not-a-program.exe"); await File.WriteAllTextAsync(notProgram, "This is not a program.");
            var converterBefore = Environment.GetEnvironmentVariable("BLACKPEARL_FFMPEG");
            Environment.SetEnvironmentVariable("BLACKPEARL_FFMPEG", notProgram);
            try
            {
                await own.StartStreamAsync(RoomClient.PrepareHostedFile(unread, "Unread film"), true);
                await Wait(() => Text("QualityHint").Contains("cannot be converted"), "with a converter that does not start, the menu says the file is streamed as it is", 20);
                await Task.Delay(1000); Call("PlaybackTickCore");
                var read = ((string Path, VideoProbe? Probe))Field("probedVideo")!;
                check(read.Path == unread && read.Probe is null && Field("probingVideo") is null && own.Snapshot?.Stream is null && PillStrip.GetShown(qualityPill),
                    "the failed read is remembered, so the converter is not started again with every tick");
            }
            finally { Environment.SetEnvironmentVariable("BLACKPEARL_FFMPEG", converterBefore); }
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
            // The stream is announced first; a viewer who arrives now waits for it rather than opening the file.
            host.Send(new("stream", Data: Wire.Serialize(new RoomStream("preparing", offered.MediaId, quality.Id, 0, 0, 0, 0, 0, Preparing: true))));
            void HostReady()
            {
                if (host.Snapshot is { Media: { } media, Playback: { } playback })
                    try { host.Send(new("ready", Data: Wire.Serialize(new PlaybackReadiness(media.Id, playback.Generation)))); } catch (IOException) { }
            }
            HostReady();
            var me = (RoomClient)Call("NewRoom")!;
            await me.ConnectAsync(url, "Sam", host.Identity!.Room);
            Call("ShowPage", "Room");
            await Wait(() => (string?)Field("loadedStream") == "preparing", "a viewer told a stream is coming waits for it");
            await Task.Delay(1500); Call("PlaybackTickCore");
            check(Field("bridge") is null && !(bool)Field("ready")! && Text("QualityPillText") == "480p" && Text("QualityHint").Contains("Riley is preparing"), "while it waits it opens nothing, and the quality menu says the video is being prepared");
            host.HostedStream = converter; host.Send(new("stream", Data: Wire.Serialize(offered)));
            await Wait(() => Field("bridge") is MediaBridge { PlaysConvertedStream: true } && (bool)Field("ready")! && (string?)Field("loadedStream") == offered.Id, "a viewer opens the converted stream instead of the file");
            var player = (MediaPlayer)Field("player")!;
            HostReady();
            await Wait(() => player.IsPlaying && player.Time > 500, "the converted stream plays");
            // The timeline draws how far the video is buffered as a lighter grey over the rail, under the played part.
            var timeline = Control<Slider>("Timeline");
            await Wait(() => SliderBuffer.GetFraction(timeline) == 1, "the stream is read ahead to its end, and the timeline shows all of it as buffered", 60);
            // The most opaque pixel in a column of the drawn slider: the rail alone, or the rail under the buffered bar.
            int Rail(double at, string? save = null)
            {
                timeline.UpdateLayout();
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(timeline);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(timeline.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(timeline.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
                var picture = new System.Windows.Media.DrawingVisual();
                using (var context = picture.RenderOpen())
                    context.DrawRectangle(new System.Windows.Media.VisualBrush(timeline) { Stretch = System.Windows.Media.Stretch.None, AlignmentX = System.Windows.Media.AlignmentX.Left, AlignmentY = System.Windows.Media.AlignmentY.Top }, null, new Rect(0, 0, timeline.ActualWidth, timeline.ActualHeight));
                bitmap.Render(picture);
                if (save is not null) { var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(shots, save)); encoder.Save(file); }
                var pixel = new byte[4]; var most = 0;
                for (var y = 0; y < bitmap.PixelHeight; y++) { bitmap.CopyPixels(new Int32Rect((int)(bitmap.PixelWidth * at), y, 1, 1), pixel, 4, 0); most = Math.Max(most, pixel[3]); }
                return most;
            }
            // Set and read in one go: the next playback tick puts the real value back.
            SliderBuffer.SetFraction(timeline, 0); var bare = Rail(0.8);
            SliderBuffer.SetFraction(timeline, 0.6); var part = (Near: Rail(0.55, "7-timeline-buffered-to-60.png"), Far: Rail(0.65));
            SliderBuffer.SetFraction(timeline, 1); var whole = Rail(0.8);
            Console.WriteLine($"Timeline rail opacity: bare {bare}, buffered {whole}; buffered to 60%: {part.Near} at 55%, {part.Far} at 65%; position {timeline.Value / timeline.Maximum:P0}");
            check(timeline.Value < timeline.Maximum / 2 && bare is > 40 and < 90 && whole > bare + 40 && Math.Abs(part.Near - whole) <= 6 && Math.Abs(part.Far - bare) <= 6,
                "the buffered part of the timeline is drawn lighter than the rest of the rail, and ends where the buffer does");
            Call("ShowPlayerControls"); Call("PlaybackTickCore");
            await Shot("6-viewer-buffered");
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

            // Sam's connection to the room drops and comes back. The film continues from the pieces Sam already has:
            // Riley serves none while Sam returns, so anything played and shown as buffered is what was kept.
            var pieces = ((ValueTuple<string, SegmentStore>)Field("streamPieces")!).Item2;
            var before = (Count: pieces.Count, Bytes: pieces.StoredBytes, Time: player.Time);
            host.HostedStream = null;
            await ((Task)Call("ReconnectRoomAsync", me)!).WaitAsync(TimeSpan.FromSeconds(60));
            await Wait(() => Field("room") is RoomClient { IsConnected: true } returned && returned != me && Field("bridge") is MediaBridge { PlaysConvertedStream: true } && (bool)Field("ready")! && (string?)Field("loadedStream") == offered.Id,
                "after a reconnect to the room the viewer opens the same converted stream", 45);
            HostReady();
            await Wait(() => player.IsPlaying && player.Time > before.Time + 1000 && SliderBuffer.GetFraction(timeline) == 1, "it plays on, and the timeline shows the whole film as buffered, with nobody serving it", 45);
            var after = Field("streamPieces") is ValueTuple<string, SegmentStore> kept ? kept : default;
            Console.WriteLine($"Reconnect: {before.Count} pieces ({before.Bytes / 1000} KB) before, {after.Item2?.Count} ({after.Item2?.StoredBytes / 1000} KB) after; position {before.Time} ms before, {player.Time} ms after");
            check(ReferenceEquals(after.Item2, pieces) && after.Item1 == offered.Id && pieces.Accepting && pieces.Count == before.Count && pieces.StoredBytes == before.Bytes && before.Count >= offered.Segments - 1,
                "the pieces downloaded before the reconnect are the ones played after it");
            host.HostedStream = converter;

            // Riley's connection drops: Riley leaves the room and the film goes with it. Sam keeps what was downloaded.
            // Riley returns, starts the film again and offers the same stream, serving none of it.
            var roomCode = host.Identity!.Room;
            await host.DisposeAsync(); others.Remove(host);
            await Wait(() => Field("bridge") is null && Field("loadedMedia") is null, "when the streamer leaves, the player empties", 20);
            check(Field("streamPieces") is ValueTuple<string, SegmentStore> waiting && ReferenceEquals(waiting.Item2, pieces) && pieces.Accepting && pieces.Count == before.Count,
                "what was downloaded waits for the streamer to come back");
            host = new RoomClient(); others.Add(host);
            await host.ConnectAsync(url, "Riley", roomCode);
            await host.StartStreamAsync(RoomClient.PrepareHostedFile(film, "Riley's film", offered.MediaId), true, 20000);
            host.Send(new("stream", Data: Wire.Serialize(offered)));
            await Wait(() => Field("bridge") is MediaBridge { PlaysConvertedStream: true } && (bool)Field("ready")! && (string?)Field("loadedStream") == offered.Id, "when the streamer returns with the same stream, the viewer opens it", 45);
            HostReady();
            await Wait(() => player.IsPlaying && player.Time is > 20500 and < 40000 && SliderBuffer.GetFraction(timeline) == 1, "and plays on from what it kept, shown as buffered to the end, with nobody serving it", 45);
            check(Field("streamPieces") is ValueTuple<string, SegmentStore> returned && ReferenceEquals(returned.Item2, pieces) && pieces.Count == before.Count && pieces.StoredBytes == before.Bytes,
                "the pieces downloaded before the streamer left are the ones played after it returns");
            host.HostedStream = converter;

            // The room seeks, and the viewer follows on the converted stream.
            host.Send(new("playback", Data: Wire.Serialize(new PlaybackState(0, offered.MediaId, true, 40000, 0, Seek: true))));
            await Task.Delay(300); HostReady();
            await Wait(() => player.IsPlaying && player.Time is > 40500 and < 52000, "a seek in the room lands at the same place in the converted stream");

            // The streamer goes back to the file: the viewer reopens it and carries on from the room's position.
            host.HostedStream = null; host.Send(new("stream"));
            await Wait(() => Field("bridge") is MediaBridge { PlaysConvertedStream: false } && (bool)Field("ready")! && Field("loadedStream") is null, "when the stream is withdrawn the viewer opens the file");
            // The pieces of the stream that was withdrawn wait a while like any buffer, in case it is offered again.
            check(Field("streamPieces") is ValueTuple<string, SegmentStore> idle && ReferenceEquals(idle.Item2, pieces), "the stream's pieces wait in case it is offered again");
            typeof(MainWindow).GetField("bufferGraceMs", flags)!.SetValue(window, 0);
            Call("CloseIdleBuffers"); Call("CloseIdleBuffers");
            typeof(MainWindow).GetField("bufferGraceMs", flags)!.SetValue(window, 120000);
            check(Field("streamPieces") is null && Field("streamCache") is StreamCache { Accepting: true }, "and are let go when it is not, while the file's buffer, which is being read, stays");
            // The file itself is closed on another thread, because deleting gigabytes takes a while.
            await Wait(() => !pieces.Accepting, "the pieces' file is closed", 10);
            HostReady();
            await Wait(() => player.IsPlaying && player.Time is > 40000 and < 59000, "and playback continues from where the room is");
            Call("PlaybackTickCore");
            check(!PillStrip.GetShown(Control<ToggleButton>("QualityPill")), "with the file itself arriving, a viewer has no quality pill");
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
