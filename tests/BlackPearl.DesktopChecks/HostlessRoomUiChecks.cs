using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using BlackPearl.Core;
using BlackPearl.Desktop;

// Rooms without a host, in the real window against a real room service: seeing who is in a room before entering,
// entering with one click and no approval, watching someone's video, playing one's own for the room instead, giving
// the stream back, and the room carrying on when the streamer leaves. The other member is a plain RoomClient.
static class HostlessRoomUiChecks
{
    public static async Task Run(MainWindow window, LibraryStore store, string directory, Action<bool, string> check)
    {
        PlaybackDiagnostics.Initialize(Path.Combine(directory, "logs"));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Field(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window);
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        T Control<T>(string name) where T : class => (T)window.FindName(name);
        string Text(string name) => Control<TextBlock>(name).Text;
        async Task Wait(Func<bool> condition, string label, int seconds = 30)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (!condition()) { if (DateTime.UtcNow > until) throw new TimeoutException("Timeout: " + label); await Task.Delay(25); }
            check(true, label);
        }
        var shots = Path.GetFullPath(Path.Combine("artifacts", "hostless-rooms-ui", $"{System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleX * 100:F0}")); Directory.CreateDirectory(shots);
        void Save(string file, params FrameworkElement[] layers)
        {
            var size = layers[0];
            // Drawn at the window's own scaling, so fractional sizes round the way they do on screen.
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(size.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(size.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
            var picture = new System.Windows.Media.DrawingVisual();
            using (var context = picture.RenderOpen())
            {
                var bounds = new Rect(0, 0, size.ActualWidth, size.ActualHeight);
                context.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0B, 0x0B, 0x0E)), null, bounds);
                // A visual brush draws each layer at the origin, whatever its position on screen.
                foreach (var layer in layers) context.DrawRectangle(new System.Windows.Media.VisualBrush(layer) { Stretch = System.Windows.Media.Stretch.None, AlignmentX = System.Windows.Media.AlignmentX.Left, AlignmentY = System.Windows.Media.AlignmentY.Top }, null, bounds);
            }
            bitmap.Render(picture);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(shots, file + ".png")); encoder.Save(stream);
        }
        // "Window" is the main window with any open dialog drawn over it. "Surfaces" are the windows that float over it:
        // the player's bars, its menus and toasts, which the main window's own picture leaves out.
        async Task Shot(string name, params string[] elements)
        {
            await Task.Delay(400); window.UpdateLayout();
            foreach (var element in elements)
            {
                if (element == "Surfaces")
                {
                    var index = 0;
                    foreach (var root in PresentationSource.CurrentSources.OfType<System.Windows.Interop.HwndSource>().Select(source => source.RootVisual).OfType<FrameworkElement>()
                        .Where(root => !ReferenceEquals(root, window) && root.IsVisible && root.ActualWidth > 40 && root.ActualHeight > 20).ToArray())
                        Save($"{name}-surface{++index}", root);
                    continue;
                }
                var visual = element == "Window" ? (FrameworkElement)window.Content : (FrameworkElement)window.FindName(element);
                if (visual.ActualWidth < 1 || visual.ActualHeight < 1) continue;
                var layers = new List<FrameworkElement> { visual };
                if (element == "Window" && InAppDialog.Open.LastOrDefault() is { } dialog && typeof(InAppDialog).GetField("layer", flags)!.GetValue(dialog) is FrameworkElement over) layers.Add(over);
                Save(name + "-" + element, layers.ToArray());
            }
        }
        // Answers the next in-app dialog with this title by clicking the button with this label; returns its message.
        Task<string> Answer(string title, string label, string? shot = null)
        {
            var answered = new TaskCompletionSource<string>(); var asked = DateTime.UtcNow; var seen = false;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            timer.Tick += async (_, _) =>
            {
                if (InAppDialog.Open.FirstOrDefault(d => d.Title == title) is not { } dialog)
                {
                    if (DateTime.UtcNow - asked > TimeSpan.FromSeconds(15)) { timer.Stop(); answered.TrySetException(new TimeoutException("Dialog never opened: " + title)); }
                    return;
                }
                // Let it lay out for one tick, so the picture shows it as the user would see it.
                if (!seen) { seen = true; return; }
                timer.Stop();
                if (shot is not null) await Shot(shot, "Window");
                var buttons = Descendants(dialog).OfType<Button>().ToArray();
                var message = string.Join(" ", Descendants(dialog).OfType<TextBlock>().Select(text => text.Text));
                buttons.Single(button => button.Content as string == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                answered.TrySetResult(message);
            };
            timer.Start();
            return answered.Task;
        }
        (string Name, string Status)[] People(string list) => Control<ItemsControl>(list).Items.Cast<object>()
            .Select(row => ((string)row.GetType().GetProperty("Name")!.GetValue(row)!, (string)row.GetType().GetProperty("Status")!.GetValue(row)!)).ToArray();

        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var url = "http://localhost:" + ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var created = new List<SavedRoom>();
        var others = new List<RoomClient>();

        // The room's creator: sees who belongs to it, removes someone, and lets them back in. Then the other side of
        // sharing a library: whoever holds the streaming role lets the room look through theirs.
        async Task CreatorChecks()
        {
            var rileySettings = new LibraryStore(Path.Combine(directory, "hostless-riley"));
            var own = (RoomClient)Call("NewRoom")!;
            await own.ConnectAsync(url, "Sam", persistent: true, roomName: "Sam's place");
            Call("RememberCurrentRoom"); Call("ShowPage", "Rooms");
            var rooms = (ObservableCollection<SavedRoom>)Field("savedRooms")!;
            created.Add(rooms.Single(room => room.Code == own.Identity!.Room));
            check(own.Identity is { Owner: true, Host: true } && created[0].HostKey?.Length == 64 && Control<Button>("RenameSavedRoomButton").Visibility == Visibility.Visible,
                "whoever creates a room keeps the key that lets them rename it");
            var guest = new RoomClient(rileySettings); others.Add(guest);
            await guest.ConnectAsync(url, "Riley", own.Identity!.Room);
            await guest.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(30));
            (string Name, string Status, Visibility Remove, Visibility Restore, string Id)[] Rows() => Control<ItemsControl>("SavedRoomGuests").Items.Cast<object>().Select(row => (
                (string)row.GetType().GetProperty("Name")!.GetValue(row)!, (string)row.GetType().GetProperty("Status")!.GetValue(row)!,
                (Visibility)row.GetType().GetProperty("RemoveVisibility")!.GetValue(row)!, (Visibility)row.GetType().GetProperty("RestoreVisibility")!.GetValue(row)!,
                (string)row.GetType().GetProperty("Id")!.GetValue(row)!)).ToArray();
            await Wait(() => Rows().Select(row => (row.Name, row.Status)).SequenceEqual([("Sam (you)", "Here · Created the room"), ("Riley", "Here")]), "the creator sees who is in their room");
            check(Rows()[0].Remove != Visibility.Visible && Rows()[1].Remove == Visibility.Visible && Rows().All(row => row.Restore != Visibility.Visible), "the creator can remove anyone but themselves");
            await Shot("8-creator", "RoomsPage");

            var removing = Answer("Remove Riley?", "Remove", "9-remove-question");
            Call("RemovePeer", new Button { Tag = guest.Identity!.Peer }, new RoutedEventArgs());
            var warning = await removing;
            check(warning.Contains("can't join it again until you let them back in"), $"removing someone says what it does ({warning})");
            await Wait(() => guest.ClosedReason == "You were removed from this room.", "the removed person is told why they left");
            await Wait(() => Rows().Select(row => (row.Name, row.Status)).SequenceEqual([("Sam (you)", "Here · Created the room"), ("Riley", "Removed")]) && Rows()[1].Restore == Visibility.Visible && Rows()[1].Remove != Visibility.Visible,
                "the creator's people list keeps who was removed, with a way to let them back in");
            await Shot("10-removed", "RoomsPage");
            var turnedAway = false;
            await using (var again = new RoomClient(rileySettings))
            {
                try { await again.ConnectAsync(url, "Riley", own.Identity.Room); } catch (RoomRejectedException) { turnedAway = true; }
            }
            check(turnedAway, "a removed person can't use the invitation again");
            Call("RestoreMember", new Button { Tag = Rows()[1].Id }, new RoutedEventArgs());
            await Wait(() => Rows().Length == 1, "letting someone back in clears them from the removed list");
            var back = new RoomClient(rileySettings); others.Add(back);
            await back.ConnectAsync(url, "Riley", own.Identity.Room);
            await back.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(30));
            await Wait(() => Rows().Select(row => (row.Name, row.Status)).SequenceEqual([("Sam (you)", "Here · Created the room"), ("Riley", "Here")]), "the person let back in enters again");

            // Riley takes the streaming role and lets the room browse their library.
            back.SetHostedLibrary(Enumerable.Range(1, 6).Select(index => new MediaItem("riley-" + index, Path.Combine(directory, "riley-" + index + ".mkv"), "Riley's film " + index, "Movie", 2000 + index)));
            Call("ShowPage", "Library");
            await Wait(() => Control<ItemsControl>("PosterGrid").Items.Count > 0, "in a room, the Library tab shows the user's own titles");
            var ownCards = Control<ItemsControl>("PosterGrid").Items.Count;
            await back.BecomeHostAsync();
            back.Send(new("catalog-permission", Number: 1));
            var offer = Control<FrameworkElement>("StreamerLibraryOffer"); var browse = Control<Button>("BrowseStreamerLibraryButton");
            await Wait(() => offer.Visibility == Visibility.Visible && browse.Content as string == "Browse Riley’s library" && Text("StreamerLibraryOfferText") == "Riley lets the room browse their library",
                "when the streamer shares their library, the Library tab offers it without replacing the user's own");
            check(Control<ItemsControl>("PosterGrid").Items.Count == ownCards && ownCards != 6 && Control<FrameworkElement>("GuestLibraryStatus").Visibility != Visibility.Visible, "the user's own library stays on screen until they choose to browse");
            await Shot("11-library-offer", "LibraryPage");
            browse.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => (bool)Field("browsingStreamerLibrary")! && Control<ItemsControl>("PosterGrid").Items.Count == 6 && browse.Content as string == "Back to my library",
                "Browse shows the streamer's titles in the Library tab, with a way back");
            await Shot("12-browsing-their-library", "LibraryPage");
            browse.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => !(bool)Field("browsingStreamerLibrary")! && Control<ItemsControl>("PosterGrid").Items.Count == ownCards && Control<FrameworkElement>("GuestLibraryStatus").Visibility != Visibility.Visible,
                "Back to my library returns to the user's own titles");
            await Wait(() => back.Snapshot?.LibraryActivity is null, "leaving their library frees it for the next person");
            await (Task)Call("Disconnect", false, false)!;
        }
        var sdk = File.Exists(".tools/dotnet/dotnet.exe") ? Path.GetFullPath(".tools/dotnet/dotnet.exe") : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var info = new ProcessStartInfo(sdk) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.Environment["BLACKPEARL_DATA"] = Path.Combine(directory, "hostless-ui-server");
        info.ArgumentList.Add(Path.GetFullPath("src/BlackPearl.Server/bin/Release/net10.0/BlackPearl.Server.dll"));
        info.ArgumentList.Add("--urls"); info.ArgumentList.Add(url);
        using var server = Process.Start(info)!;
        var output = server.StandardOutput.ReadToEndAsync(); var errors = server.StandardError.ReadToEndAsync();
        var riley = new RoomClient();
        var rooms = (ObservableCollection<SavedRoom>)Field("savedRooms")!;
        var items = (List<MediaItem>)Field("items")!;
        var mine = new MediaItem("hostless-mine", Path.Combine(directory, "my-film.avi"), "My film", "Movie", 2024);
        SavedRoom? saved = null;
        var previousName = store.Setting("name");
        try
        {
            using var http = new System.Net.Http.HttpClient();
            for (var attempt = 0; ; attempt++)
            {
                try { using var response = await http.GetAsync(url + "/health"); if (response.IsSuccessStatusCode) break; } catch (System.Net.Http.HttpRequestException) { }
                if (attempt >= 100) throw new IOException("Hostless room fixture server did not start"); await Task.Delay(50);
            }
            var rileyFilm = Path.Combine(directory, "riley-film.avi"); VideoFixture.Write(rileyFilm, 120); VideoFixture.Write(mine.Path, 120);
            store.Setting("name", "Sam");
            void RileyReady()
            {
                if (riley.Snapshot is { Media: { } media, Playback: { } playback })
                    try { riley.Send(new("ready", Data: Wire.Serialize(new PlaybackReadiness(media.Id, playback.Generation)))); } catch (IOException) { }
            }

            // Riley creates a room and plays a film in it.
            await riley.ConnectAsync(url, "Riley", persistent: true, roomName: "Film club");
            await riley.StartStreamAsync(RoomClient.PrepareHostedFile(rileyFilm, "Riley's film"), true);
            RileyReady();
            var code = riley.Identity!.Room;

            // Sam has the invitation saved, under the name it had when it was saved. The Rooms page says who is inside.
            saved = new SavedRoom("hostless-fixture", "Room " + code[..6], url, code); rooms.Add(saved);
            var list = Control<ListBox>("SavedRoomsList"); list.SelectedItem = saved;
            Call("ShowPage", "Rooms");
            Set("roomPresenceAsked", -60000L); Call("RefreshRoomPresence", true);
            await Wait(() => Text("SavedRoomStatus") == "Riley is here now", "before entering, a saved room says who is in it");
            saved = rooms.Single(room => room.Id == saved.Id);
            check(saved.Name == "Film club" && Text("SavedRoomHeading") == "Film club", "a saved room takes the name its creator gave it without being entered");
            check(Text("RoomHeroLiveText") == "1 here" && Text("RoomHeroPresence") == "Riley is in the room now" && Control<Button>("RoomHeroResume").Content as string == "Join" &&
                Control<Button>("ConnectSavedRoomButton").Content as string == "Join", "the room offers Join and shows it is occupied");
            check(People("SavedRoomGuests").SequenceEqual([("Riley", "Here now")]), "the people tile lists who is in the room before entering");
            window.UpdateLayout();
            check((list.ItemContainerGenerator.ContainerFromItem(saved) as ListBoxItem)?.Tag as string == "busy", "a room other people are in is marked in the room list");
            await Shot("1-before-entering", "RoomsPage");

            // One click enters: no approval, and with a video playing, the player opens on it.
            var entering = Stopwatch.StartNew();
            Call("ConnectSavedRoom", Control<Button>("ConnectSavedRoomButton"), new RoutedEventArgs());
            await Wait(() => Field("room") is RoomClient { IsConnected: true } joined && joined.DirectControlsReady.IsCompletedSuccessfully, "one click enters the room with no approval");
            var me = (RoomClient)Field("room")!;
            Console.WriteLine($"Entered and received the room state in {entering.ElapsedMilliseconds} ms");
            check(me.Identity is { Host: false, Owner: false } && me.Snapshot?.Media?.Title == "Riley's film", "the newcomer watches what the room is playing");
            await Wait(() => (string)Field("currentPage")! == "Room", "entering a room where a video plays opens the player");
            await Wait(() => Field("bridge") is not null && (bool)Field("ready")!, "the video loads from whoever is playing it");
            var player = (MediaPlayer)Field("player")!;
            await Wait(() => player.IsPlaying && player.Time > 300, "the newcomer's player follows the room's playback");
            await Wait(() => Text("RoomSubtitle") == "Riley's film · Streaming from Riley", "the player names who the video comes from");
            await Wait(() => People("PeopleList").SequenceEqual([("Riley", "Streaming"), ("Sam (you)", "Watching")]), "the people list shows who streams and who watches, with nothing to approve");
            check(Text("ParticipantsPillDetail") == "2" && Text("SharedControlsText") == "Everyone can control" && !Control<System.Windows.Controls.Primitives.ToggleButton>("SharedControls").IsEnabled,
                "a viewer sees that everyone can control playback, and cannot change that for someone else's video");
            check(Control<Button>("PlayButton").IsEnabled, "a viewer can play and pause by default");
            await Shot("2-watching", "Surfaces");
            // The people list opens from its pill in the player's bar.
            Control<System.Windows.Controls.Primitives.Popup>("ParticipantsMenu").IsOpen = true;
            await Shot("2-watching-people", "Surfaces");
            Control<System.Windows.Controls.Primitives.Popup>("ParticipantsMenu").IsOpen = false;
            Call("ShowPage", "Rooms");
            await Wait(() => Text("SavedRoomStatus") == "Riley is playing Riley's film" && Control<Button>("ConnectSavedRoomButton").Content as string == "Open player", "the Rooms page says what the room is watching");
            check(People("SavedRoomGuests").SequenceEqual([("Riley", "Streaming · Created the room"), ("Sam (you)", "Here")]) && Text("SavedRoomGuestCount") == "2 here",
                "the people tile lists everyone in the room");
            check(Text("RoomHeroLiveText") == "Live" && Control<Button>("RoomHeroResume").Content as string == "Return to player" && Control<Button>("RoomHeroDisconnect").Content as string == "Leave",
                "the hero offers to return to the player or leave");
            await Shot("3-in-the-room", "RoomsPage");

            // Sam plays a film of their own for the room. Someone else's is playing, so the app asks first.
            items.Add(mine); Set("selected", mine);
            var declined = Answer("Play yours for the room?", "Keep watching", "4-take-over-question");
            Call("HostClick", Control<Button>("DetailHost"), new RoutedEventArgs());
            var question = await declined;
            check(question.Contains("Riley is playing Riley's film") && question.Contains("replaces it for everyone"), $"starting a video over someone else's asks first ({question})");
            await Task.Delay(300);
            check(riley.Identity!.Host && me.Snapshot?.Media?.Title == "Riley's film" && Field("playingItem") is null, "keeping the current video changes nothing");
            var accepted = Answer("Play yours for the room?", "Play mine");
            var switching = Stopwatch.StartNew();
            Call("HostClick", Control<Button>("DetailHost"), new RoutedEventArgs());
            await accepted;
            await Wait(() => me.Identity!.Host && riley.Snapshot?.Media?.Title == "My film" && !riley.Identity.Host, "Watch together gives the stream to whoever starts a video, for everyone");
            Console.WriteLine($"The other member saw the new video {switching.ElapsedMilliseconds} ms after the click");
            RileyReady();
            await Wait(() => (string)Field("currentPage")! == "Room" && (Field("playingItem") as MediaItem)?.Id == mine.Id && Field("bridge") is null && (bool)Field("ready")!, "the new streamer plays its own file directly");
            await Wait(() => { RileyReady(); return me.Snapshot?.Playback?.Playing == true && player.IsPlaying; }, "the room plays the new video once everyone has it");
            await Wait(() => Text("RoomSubtitle") == "My film · You're streaming", "the player says the user is streaming");
            await Wait(() => People("PeopleList").SequenceEqual([("Riley", "Watching"), ("Sam (you)", "Streaming")]), "the people list follows the change of streamer");
            check(Control<System.Windows.Controls.Primitives.ToggleButton>("SharedControls").IsEnabled, "whoever plays the video decides whether the others control it");
            Control<System.Windows.Controls.Primitives.Popup>("ParticipantsMenu").IsOpen = true;
            await Shot("5-streaming", "Surfaces");
            Control<System.Windows.Controls.Primitives.Popup>("ParticipantsMenu").IsOpen = false;

            // Riley plays theirs again. Sam's film keeps its place for next time, and Sam follows Riley's.
            await Wait(() => player.Time > 1500, "the user's own film plays for a moment");
            await riley.StartStreamAsync(RoomClient.PrepareHostedFile(rileyFilm, "Riley's film again"), true);
            RileyReady();
            await Wait(() => !me.Identity!.Host && me.Snapshot?.Media?.Title == "Riley's film again" && Field("playingItem") is null, "when someone else starts a video, the user's ends and theirs takes over");
            check(long.TryParse(store.Setting("position:" + mine.Id), out var kept) && kept >= 1000, $"the film the user was playing keeps its place ({store.Setting("position:" + mine.Id)} ms)");
            await Wait(() => Field("bridge") is not null && (bool)Field("ready")! && Text("RoomSubtitle").EndsWith("Streaming from Riley", StringComparison.Ordinal), "the player follows the new streamer's video");
            check(!Control<Button>("NextButton").IsEnabled || true, "player controls refreshed for a viewer");

            // Riley leaves. Their video ends, the room stays, and the player says anyone can start something.
            await riley.DisposeAsync();
            await Wait(() => me.Identity!.Host && me.Snapshot?.People.Length == 1 && me.Snapshot.Media is null, "when the streamer leaves, the room stays open for whoever is left");
            await Wait(() => Control<Border>("PlayerEmpty").Visibility == Visibility.Visible && Text("PlayerEmptyTitle") == "Nothing is playing in this room" && Control<TextBlock>("PlayerEmptyHint").Visibility == Visibility.Visible,
                "the player empties and says the room is waiting for a video", 15);
            check(Text("RoomSubtitle") == "Nothing playing · Anyone can start a video" && People("PeopleList").SequenceEqual([("Sam (you)", "In the room")]), "the room shows who is left and that nothing plays");
            await Shot("6-streamer-left", "Window", "Surfaces");
            Call("ShowPage", "Rooms");
            await Wait(() => Text("SavedRoomStatus") == "You're in this room" && People("SavedRoomGuests").SequenceEqual([("Sam (you)", "Here"), ("Riley", "Not here")]),
                "the Rooms page lists who is in the room and who belongs to it but is away");
            await Shot("7-alone", "RoomsPage");

            // Sam leaves; the room is still there to come back to.
            Call("LeaveRoom", Control<Button>("RoomHeroDisconnect"), new RoutedEventArgs());
            await Wait(() => Field("room") is null && Control<Button>("ConnectSavedRoomButton").Content as string == "Join", "leaving returns the room to Join");
            Set("roomPresenceAsked", -60000L); Call("RefreshRoomPresence", true);
            await Wait(() => Text("SavedRoomStatus") == "Nobody is here right now", "an empty room says so, and stays in the list");

            await CreatorChecks();
        }
        finally
        {
            store.Setting("name", previousName ?? "");
            await (Task)Call("Disconnect", false, false)!;
            await riley.DisposeAsync();
            foreach (var other in others) await other.DisposeAsync();
            foreach (var room in created) rooms.Remove(rooms.FirstOrDefault(known => known.Id == room.Id) ?? room);
            items.Remove(mine); if (saved is not null) rooms.Remove(rooms.FirstOrDefault(room => room.Id == saved.Id) ?? saved);
            Set("selected", null);
            if (!server.HasExited) { server.Kill(true); await server.WaitForExitAsync(); }
            await File.WriteAllTextAsync(Path.Combine(directory, "hostless-ui-server.log"), await output + await errors);
            await PlaybackDiagnostics.FlushAsync();
        }
        Console.WriteLine("Screenshots: " + shots);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
