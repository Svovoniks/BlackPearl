using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BlackPearl.Core;
using BlackPearl.Desktop;

static class HomeChecks
{
    public static async Task Run(string directory)
    {
        var window = new MainWindow();
        await using var client = new RoomClient();
        void Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        T Control<T>(string name) => (T)window.FindName(name);
        LibraryCardView[] Continuing() => new[] { Field<LibraryCardView?>("homeHero") }.OfType<LibraryCardView>().Concat(Field<LibraryCardView[]>("homeContinueAll")).ToArray();
        HomeRowView Row(string kind) => Field<HomeRowView[]>("homeRows").Single(row => row.Kind == kind);
        int Suggested() => Field<HomeRowView[]>("homeRows").Sum(row => row.Tiles.Length + (row.Spotlight is null ? 0 : 1));
        IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                yield return child;
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
        async Task Until(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
            if (!condition()) throw new TimeoutException("Home fixture did not finish rendering");
        }
        void Layout(int width, int height, string name)
        {
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
            var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(file);
        }
        var store = Field<LibraryStore>("library");
        var posterPath = Path.Combine(directory, "home-poster.png");
        var poster = BitmapSource.Create(120, 180, 96, 96, PixelFormats.Rgb24, null, Enumerable.Repeat((byte)120, 120 * 180 * 3).ToArray(), 120 * 3);
        var posterEncoder = new PngBitmapEncoder(); posterEncoder.Frames.Add(BitmapFrame.Create(poster));
        using (var posterFile = File.Create(posterPath)) posterEncoder.Save(posterFile);
        var items = new List<MediaItem>
        {
            new("home-episode1", "", "Night Train", "Show", Series: "Night Train", Season: 1, Episode: 1, RuntimeMinutes: 25),
            new("home-episode2", "", "Night Train", "Show", Series: "Night Train", Season: 1, Episode: 2, EpisodeTitle: "The crossing", RuntimeMinutes: 25),
            new("home-movie", "", "A Sunday in October", "Movie", Year: 2025, RuntimeMinutes: 100)
        };
        items.AddRange(Enumerable.Range(0, 12).Select(index => new MediaItem("home-new" + index, "", "Library title " + index, "Movie", Year: 2026, Poster: posterPath)));
        store.Setting("watched:home-episode1", "true"); store.Setting("position:home-movie", "90000");
        typeof(MainWindow).GetField("items", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, items);
        try
        {
            Call("RefreshHome");
            await Until(() => Continuing().Length == 2);
            Check(Control<ScrollViewer>("HomePage").Visibility == Visibility.Visible && Control<Button>("HomeNav").Visibility == Visibility.Visible,
                "Home is the startup tab and has a navigation entry");
            Check(Control<Border>("HomeHero").Visibility == Visibility.Visible && Control<ItemsControl>("HomeContinue").Items.Count == 1 &&
                Control<Border>("HomeStrip").Visibility == Visibility.Visible, "the hero resumes one title and the slim bar lists the other unfinished one");
            Check(Row("Movie").Spotlight is not null && Row("Movie").Tiles.Length == LibraryHome.SuggestionsPerKind - 1 && Row("Show").Spotlight is null && Row("Anime").Spotlight is null &&
                Row("Movie").CountText == "12 not started" && Control<Button>("HomeMysteryPlay").IsEnabled,
                "local Home fills only categories with unstarted titles and enables the mystery deck");
            var next = Continuing().Single(view => view.IsEpisode);
            Check(next.Card.Media.Id == "home-episode2" && next.PlaybackLabel == "Next episode" && next.HomeDetail == "S1 E2 · Up next", "continue watching identifies the next unwatched episode");
            Check(Continuing().Single(view => view.IsMovie).HomeDetail == "1 h 39 m left", "unfinished movies show the time left");
            var firstPick = Field<LibraryCardView>("homeMystery");
            Call("DrawMystery", null, new RoutedEventArgs());
            await Until(() => Control<Button>("HomeMysteryCard").DataContext == Field<LibraryCardView>("homeMystery"));
            Check(Field<LibraryCardView>("homeMystery") is { HomeSection: "draw" } drawn && drawn.Key != firstPick.Key && drawn.Card.Media.Kind == "Movie",
                "drawing another mystery card picks a different unstarted title");
            Control<RadioButton>("MysteryAnime").IsChecked = true;
            await Until(() => Control<TextBlock>("HomeMysteryTitle").Text == "Nothing matches");
            Check(Field<LibraryCardView?>("homeMystery") is null && !Control<Button>("HomeMysteryPlay").IsEnabled && Control<TextBlock>("HomeMysteryTitle").Text == "Nothing matches",
                "mystery filters with no matching titles say so instead of drawing something else");
            Control<RadioButton>("MysteryAnything").IsChecked = true;
            Check(Field<LibraryCardView?>("homeMystery") is { HomeSection: "draw" } && Control<Button>("HomeMysteryPlay").IsEnabled, "clearing the type filter draws again");
            Layout(1280, 850, "home-desktop"); Layout(900, 640, "home-compact");
            Check(Control<ScrollViewer>("HomePage").ScrollableHeight > 0, "Home remains vertically scrollable at the minimum window size");
            Control<ScrollViewer>("HomePage").ScrollToBottom();
            Layout(900, 640, "home-suggestions-section");
            Control<ScrollViewer>("HomePage").ScrollToTop();
            var movies = Row("Movie");
            var suggestionRow = Descendants(Control<ItemsControl>("HomeRows")).OfType<ItemsControl>().Single(control => ReferenceEquals(control.ItemsSource, movies.Tiles));
            var originalSource = suggestionRow.ItemsSource;
            var originalViews = suggestionRow.Items.Cast<LibraryCardView>().ToArray();
            foreach (var view in originalViews) await view.LoadPosterAsync();
            var originalImages = originalViews.Select(view => view.Poster).ToArray();
            var originalContainers = originalViews.Select(view => suggestionRow.ItemContainerGenerator.ContainerFromItem(view)).ToArray();
            Check(originalImages.All(image => image is not null) && originalContainers.All(container => container is not null), "suggestion fixture has loaded posters and realized card containers");
            await movies.Spotlight!.LoadPosterAsync();
            Layout(1280, 850, "home-rows");
            var rowContainer = (FrameworkElement)Control<ItemsControl>("HomeRows").ItemContainerGenerator.ContainerFromItem(movies);
            Check(movies.Spotlight.Poster is not null && rowContainer.ActualHeight is > 250 and < 360, "a recommendation row's artwork backdrop does not stretch the row beyond its posters");
            var pager = Descendants(rowContainer).OfType<HorizontalScroller>().Single();
            Button Arrow(string name) => (Button)pager.Template.FindName(name, pager);
            Check(Arrow("PART_Right").Visibility == Visibility.Visible && Arrow("PART_Left").Visibility == Visibility.Collapsed &&
                !Descendants(Control<ScrollViewer>("HomePage")).OfType<System.Windows.Controls.Primitives.ScrollBar>().Any(bar => bar.IsVisible),
                "recommendation rows page with arrow buttons instead of visible scrollbars");
            pager.Page(1);
            await Until(() => pager.Viewer!.HorizontalOffset > 0 && Arrow("PART_Left").Visibility == Visibility.Visible);
            pager.Page(1); pager.Page(1);
            await Until(() => pager.Viewer!.HorizontalOffset >= pager.Viewer.ScrollableWidth - 1 && Arrow("PART_Right").Visibility == Visibility.Collapsed);
            Check(Arrow("PART_Right").Visibility == Visibility.Collapsed && Arrow("PART_Left").Visibility == Visibility.Visible, "the right arrow hides at the end of a row");
            var frame = Descendants(rowContainer).OfType<Border>().First(border => BlackPearl.Desktop.RoundedClip.GetRadius(border) > 0 && border.Child is Grid);
            Check(frame.Clip is null && frame.Child.Clip is not null, "artwork frames clip the image inside their outline rather than cutting the outline");
            var changes = new List<string?>();
            foreach (var view in originalViews) view.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
            for (var index = 0; index < items.Count; index++)
                items[index] = items[index] with { Title = "Updated title " + (items.Count - index), Overview = "Updated metadata" };
            Call("RefreshHome");
            await Until(() => originalViews[0].Card.Media.Overview == "Updated metadata");
            Check(ReferenceEquals(suggestionRow.ItemsSource, originalSource) && suggestionRow.Items.Cast<LibraryCardView>().SequenceEqual(originalViews),
                "background metadata refresh retains the suggestion row and every card instance");
            Check(originalViews.Select(view => view.Poster).SequenceEqual(originalImages) && !changes.Contains("Poster") &&
                originalViews.Select(view => suggestionRow.ItemContainerGenerator.ContainerFromItem(view)).SequenceEqual(originalContainers),
                "background metadata refresh keeps loaded posters and card containers without blank frames");
            var retainedPoster = originalViews[0].Poster;
            var replacementPath = Path.Combine(directory, "home-poster-replacement.png");
            File.Copy(posterPath, replacementPath);
            var replacedId = originalViews[0].Card.Media.Id;
            var replacedIndex = items.FindIndex(item => item.Id == replacedId);
            items[replacedIndex] = items[replacedIndex] with { Poster = replacementPath };
            var blankPoster = false;
            originalViews[0].PropertyChanged += (_, args) => { if (args.PropertyName == "Poster" && originalViews[0].Poster is null) blankPoster = true; };
            Call("RefreshHome");
            await Until(() => originalViews[0].Card.Poster == replacementPath && !ReferenceEquals(originalViews[0].Poster, retainedPoster));
            Check(!blankPoster && ReferenceEquals(suggestionRow.ItemsSource, originalSource), "new artwork replaces a suggestion's poster in place without a placeholder frame");
            var shownSpotlight = movies.Spotlight;
            for (var attempt = 0; attempt < 8 && ReferenceEquals(movies.Spotlight, shownSpotlight); attempt++)
            {
                var before = movies.Spotlight;
                Call("ReshuffleHomeRow", new FrameworkElement { DataContext = movies }, new RoutedEventArgs());
                var deadline = DateTime.UtcNow.AddSeconds(2);
                while (ReferenceEquals(movies.Spotlight, before) && DateTime.UtcNow < deadline) await Task.Delay(20);
            }
            await Until(() => movies.Spotlight?.Poster is not null);
            Check(!ReferenceEquals(movies.Spotlight, shownSpotlight) && movies.Spotlight!.Poster is not null, "a new spotlight title loads its artwork without the card being rebuilt");
            var snapshot = new RoomSnapshot([new("host", "Fixture Host", true, true), new("guest", "Guest", false, true)], null, null, false,
                LibraryBrowsing: true, LibraryActivity: new("guest", "Guest", "Home", "", Wire.Now));
            typeof(RoomClient).GetProperty("Identity")!.SetValue(client, new Welcome("fixture", "guest", false, [], false));
            typeof(RoomClient).GetProperty("Snapshot")!.SetValue(client, snapshot);
            ((TaskCompletionSource)typeof(RoomClient).GetField("directConnected", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!).SetResult();
            typeof(MainWindow).GetField("room", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, client);
            typeof(MainWindow).GetField("browsingStreamerLibrary", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
            Call("ShowPage", "Library"); Call("ShowPage", "Home");
            Check(Continuing().Length == 0 && System.Windows.Automation.AutomationProperties.GetName(Control<ScrollViewer>("HomePage")) == "Home · Fixture Host’s library",
                "guest Home clears local progress and identifies the host's library");
            var request = Field<LibraryBrowseRequest>("guestBrowse");
            for (var index = 0; index < 3; index++)
            {
                var page = SharedLibrary.Browse(items, request with { Page = index }, playback: store.PlaybackSettings());
                Call("ReceiveHostLibrary", new WireMessage("catalog-page", Number: Field<long>("guestBrowseRequest"), Data: Wire.Serialize(page)));
                await Task.Delay(30);
                if ((index + 1) * SharedLibrary.PageSize >= page.Total) break;
            }
            Check(Continuing().Length == 2 && Suggested() == LibraryHome.SuggestionsPerKind && Control<Button>("HomeMysteryPlay").IsEnabled &&
                Control<StackPanel>("HomeMysteryFilters").Visibility == Visibility.Collapsed && Row("Movie").CountText == "",
                "guest Home assembles all paginated continue, suggestion, and mystery cards");
            var paused = Continuing().Single(view => !view.IsEpisode);
            Check(paused.IsRemote && paused.PlaybackLabel == "Resume · 1:30", "guest Home displays the host's saved movie progress");
            Layout(1280, 850, "home-guest");
            Call("ReceiveHostLibrary", new WireMessage("catalog-page", Number: Field<long>("guestBrowseRequest") - 1,
                Data: Wire.Serialize(new LibraryBrowsePage([], 0, 0, "Home"))));
            Check(Continuing().Length == 2, "stale guest responses cannot replace Home rows");
            // A streamer whose app draws for guests gets the same filters as Home in your own library; the host's app answers each draw.
            var events = typeof(RoomClient).GetField("events", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!;
            var reader = events.GetType().GetProperty("Reader")!.GetValue(events)!;
            var tryRead = reader.GetType().GetMethod("TryRead")!;
            List<WireMessage> Sent(string type)
            {
                var found = new List<WireMessage>(); var slot = new object?[1];
                while ((bool)tryRead.Invoke(reader, slot)!)
                    if (slot[0]!.GetType().GetProperty("Message")!.GetValue(slot[0]) is WireMessage { } sent && sent.Type == type) found.Add(sent);
                return found;
            }
            Task Receive(WireMessage message) => (Task)typeof(MainWindow).GetMethod("OnRoomMessage", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [message])!;
            async Task<WireMessage> Answer()
            {
                var sent = Sent("catalog-draw").Single();
                var drawn = SharedLibrary.Draw(items, Wire.Read<LibraryDrawRequest>(sent.Data!), playback: store.PlaybackSettings());
                await Receive(new WireMessage("catalog-drawn", Number: sent.Number, Data: Wire.Serialize(drawn)));
                await Task.Delay(30);
                return sent;
            }
            Sent("catalog-draw");
            typeof(RoomClient).GetField("hostFeatures", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(client, 3);
            Call("RenderGuestHome");
            Check(Control<StackPanel>("HomeMysteryFilters").Visibility == Visibility.Visible && Field<LibraryCardView?>("homeMystery") is null &&
                Control<Button>("HomeMysteryCard").Visibility == Visibility.Hidden && Control<TextBlock>("HomeMysteryTitle").Text == "" &&
                !Control<Button>("HomeMysteryPlay").IsEnabled && !Control<Button>("HomeMysteryDraw").IsEnabled,
                "while the streamer draws, the filters keep their place and the card waits blank instead of showing a fixed pick");
            var first = await Answer();
            var guestPick = Field<LibraryCardView?>("homeMystery");
            Check(Wire.Read<LibraryDrawRequest>(first.Data!) is { Kind: null, Length: MysteryLength.Any, Current: null, Redraw: false } &&
                Control<StackPanel>("HomeMysteryFilters").Visibility == Visibility.Visible && guestPick is { IsRemote: true, HomeSection: "draw" } &&
                guestPick.Card.Media.Id.StartsWith("home-new") && Control<TextBlock>("HomeMysteryMeta").Text == "Movie · 2026 · Not started yet" &&
                Control<Button>("HomeMysteryDraw").IsEnabled && Control<Button>("HomeMysteryCard").DataContext == guestPick,
                "guest Home shows the type and length filters and a title the streamer's app drew from its unstarted titles");
            Call("RenderGuestHome"); Call("RefreshSharedLibrary");
            Check(Sent("catalog-draw").Count == 0, "refreshing guest Home does not ask the streamer to draw again");
            Call("DrawMystery", null, new RoutedEventArgs());
            var another = await Answer();
            await Until(() => Control<Button>("HomeMysteryCard").DataContext == Field<LibraryCardView?>("homeMystery"));
            Check(Wire.Read<LibraryDrawRequest>(another.Data!) is { Redraw: true } redrawn && redrawn.Current == guestPick.Card.Media.Id &&
                Field<LibraryCardView?>("homeMystery") is { HomeSection: "draw" } other && other.Key != guestPick.Key && Continuing().Length == 2,
                "Draw another asks the streamer for a different title and keeps the rest of guest Home");
            Control<RadioButton>("MysteryAnime").IsChecked = true;
            var anime = await Answer();
            await Until(() => Control<TextBlock>("HomeMysteryTitle").Text == "Nothing matches");
            Check(Wire.Read<LibraryDrawRequest>(anime.Data!) is { Kind: "Anime", Redraw: false } && Field<LibraryCardView?>("homeMystery") is null &&
                !Control<Button>("HomeMysteryPlay").IsEnabled && !Control<Button>("HomeMysteryDraw").IsEnabled,
                "a guest filter with no matching titles in the streamer's library says so");
            Control<RadioButton>("MysteryAnything").IsChecked = true;
            await Answer();
            Check(Field<LibraryCardView?>("homeMystery") is { HomeSection: "draw" } && Control<Button>("HomeMysteryPlay").IsEnabled, "clearing the guest's type filter draws again");
            Layout(1280, 850, "home-guest-draw");
            var kept = Field<LibraryCardView?>("homeMystery");
            Control<RadioButton>("MysteryAnime").IsChecked = true;
            var failed = Sent("catalog-draw").Single();
            await Receive(new WireMessage("catalog-error", Number: failed.Number, Text: "Their app is busy. Try again shortly."));
            Call("RenderGuestHome"); Call("RefreshSharedLibrary");
            Check(ReferenceEquals(Field<LibraryCardView?>("homeMystery"), kept) && Continuing().Length == 2 && Field<string?>("guestLibraryNotice") is null &&
                Sent("catalog-draw").Count == 0, "a failed guest draw keeps the card and Home, and is not asked again on every refresh");
            Control<RadioButton>("MysteryAnything").IsChecked = true;
            await Answer();
            // A draw that cannot be sent, here because the room's control queue is full, is not waited on for 15 seconds.
            var writer = events.GetType().GetProperty("Writer")!.GetValue(events)!;
            var filler = typeof(RoomClient).GetNestedType("RoomEvent", BindingFlags.NonPublic)!.GetConstructors().Single();
            var tryWrite = writer.GetType().GetMethod("TryWrite")!;
            while ((bool)tryWrite.Invoke(writer, [filler.Invoke(filler.GetParameters().Select(p => p.Name == "Kind" ? "filler" : p.DefaultValue).ToArray())])!) { }
            var beforeFull = Field<LibraryCardView?>("homeMystery");
            ((Panel)Control<RadioButton>("MysteryAnime").Parent).Children.OfType<RadioButton>().Single(choice => choice.Tag as string == "Show").IsChecked = true;
            Check(typeof(MainWindow).GetField("guestDrawPending", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window) is null &&
                ReferenceEquals(Field<LibraryCardView?>("homeMystery"), beforeFull) && Control<Button>("HomeMysteryPlay").IsEnabled,
                "a guest draw that cannot be sent keeps the card instead of waiting on an answer");
            Sent("catalog-draw");
            Call("RenderGuestHome");
            var resent = await Answer();
            Check(Wire.Read<LibraryDrawRequest>(resent.Data!) is { Kind: "Show" } && Control<TextBlock>("HomeMysteryTitle").Text == "Nothing matches",
                "the next refresh sends the draw that could not be sent");
            Control<RadioButton>("MysteryAnything").IsChecked = true;
            await Answer();
            Call("OpenHomeTitle", paused);
            Check(Field<string>("currentPage") == "Details" && Field<bool>("guestHomeResume") && Control<Button>("DetailPlay").Content.Equals("Play in room"),
                "guest continue watching opens room playback details with resume intent");
            Call("ShowPage", "Home");
            typeof(RoomClient).GetProperty("Snapshot")!.SetValue(client, snapshot with { LibraryBrowsing = false, LibraryActivity = null });
            Call("RefreshSharedLibrary");
            await Until(() => Continuing().Length == 2 && !Continuing().Any(view => view.IsRemote));
            Check(!Field<bool>("browsingStreamerLibrary") && System.Windows.Automation.AutomationProperties.GetName(Control<ScrollViewer>("HomePage")) == "Home",
                "when the streamer stops sharing their library, Home returns to your own at once");
            typeof(MainWindow).GetField("room", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, null);
            Call("RefreshSharedLibrary");
            await Until(() => Continuing().Length == 2);
            Check(System.Windows.Automation.AutomationProperties.GetName(Control<ScrollViewer>("HomePage")) == "Home", "leaving a room restores local Home content");
        }
        finally { window.Close(); }
    }
}
