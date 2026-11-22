using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using LibVLCSharp.Shared;
using BlackPearl.Core;
using BlackPearl.Desktop;

// Shuffle: Next, Previous, the end-of-episode button and the up-next countdown follow a random order of the show or the
// queue, each video once; Loop all starts a new round, and turning Shuffle off brings the usual order back.
static class ShuffleChecks
{
    public static async Task Run(MainWindow window, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        var loopField = typeof(MainWindow).GetField("loopMode", flags)!;
        void Loop(string mode) { loopField.SetValue(window, Enum.Parse(loopField.FieldType, mode)); Call("RefreshLoopButton"); }
        var directory = Path.Combine(Path.GetTempPath(), "wr-shuffle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var episodes = Enumerable.Range(1, 5).Select(number =>
        {
            var path = Path.Combine(directory, $"Shuffle Show S01E0{number}.avi");
            VideoFixture.Write(path, 20);
            return new MediaItem($"shuffle-{number}", path, "Shuffle Show", "Show", Series: "Shuffle Show", Season: 1, Episode: number);
        }).ToArray();
        var specialPath = Path.Combine(directory, "Shuffle Show S00E01.avi"); VideoFixture.Write(specialPath, 20);
        var special = new MediaItem("shuffle-special", specialPath, "Shuffle Show", "Show", Series: "Shuffle Show", Season: 0, Episode: 1);
        var movies = Enumerable.Range(1, 3).Select(number =>
        {
            var path = Path.Combine(directory, $"Shuffle movie {number}.avi");
            VideoFixture.Write(path, 20);
            return new MediaItem($"shuffle-movie-{number}", path, $"Shuffle movie {number}", "Movie");
        }).ToArray();
        // Run on its own, the window may still be loading its library, which would replace these videos and the queue.
        for (var i = 0; i < 300 && !Field<bool>("libraryLoaded"); i++) await Task.Delay(100);
        await Task.Delay(500);
        var originalItems = Field<List<MediaItem>>("items");
        var queue = Field<List<MediaItem>>("queue"); var previousQueue = queue.ToArray(); var previousPosition = Field<int>("queuePosition");
        var button = (ToggleButton)window.FindName("ShuffleButton");
        var next = (Button)window.FindName("NextButton");
        var previous = (Button)window.FindName("PreviousButton");
        var endButton = (Button)window.FindName("EpisodeEndButton");
        var card = (Border)window.FindName("UpNextCard");
        var upNext = (TextBlock)window.FindName("UpNextEpisode");
        MediaItem Playing() => Field<MediaItem>("playingItem");
        MediaItem? Adjacent(int direction) => (MediaItem?)Call("AdjacentEpisode", direction);
        async Task Step(int direction) => await (Task)Call("PlayQueueStep", direction)!;
        async Task<Task> End(MediaItem item)
        {
            var player = Field<MediaPlayer>("player");
            player.Time = 15000; await Task.Delay(300);
            player.SetPause(true);
            for (var i = 0; i < 30 && player.IsPlaying; i++) await Task.Delay(100);
            return (Task)Call("PlaybackEnded", item, null, Field<int>("loadGeneration"))!;
        }
        var previousQueueMode = Field<bool>("queueModeRequested");
        try
        {
            Set("items", originalItems.Concat([.. episodes, special, .. movies]).ToList());
            // No queue: Previous and Next follow the show's episodes.
            queue.Clear(); Set("queuePosition", -1); Set("queueModeRequested", false); Call("PublishQueue");
            Loop("Off");
            Call("ShowPage", "Library");
            await (Task)Call("PlayLocalItem", episodes[0])!;
            // The up-next countdown runs only while the player shows.
            Call("ShowPage", "Room");
            // A queue restored at startup can arrive after the window opens; clear it again once the video plays.
            queue.Clear(); Set("queuePosition", -1); Set("queueModeRequested", false); Call("PublishQueue");
            Call("UpdateSessionControls");

            // The button turns shuffle on and off, with the R key's hint.
            check(button.IsEnabled && button.IsChecked == false && (string)button.ToolTip == "Shuffle off (R)" && !Field<bool>("shuffle"), "Shuffle starts off and is enabled outside a room");
            check(Adjacent(1)?.Id == episodes[1].Id, "with Shuffle off Next plays the next episode");
            Call("ToggleShuffle", window, new RoutedEventArgs());
            check(Field<bool>("shuffle") && button.IsChecked == true && (string)button.ToolTip == "Shuffle on (R)" && (string)next.ToolTip == "Random episode (N)",
                "one press turns Shuffle on and Next names a random episode");
            check(((TextBlock)window.FindName("QueueStatus")).Text.EndsWith("shuffled"), "the queue menu says the episodes are shuffled");

            // Folded into the … menu, the Shuffle row names its state.
            var overflow = (StackPanel)window.FindName("OverflowItems");
            bool RowSays(string label) => overflow.Children.OfType<Button>().Any(row => row.Content is Grid grid && grid.Children.OfType<TextBlock>().Any(text => text.Text == label));
            button.Visibility = Visibility.Collapsed; Call("RefreshOverflowMenu");
            check(RowSays("Shuffle on"), "a folded Shuffle row shows that it is on");
            button.Visibility = Visibility.Visible; Call("RefreshOverflowMenu");

            // Next keeps its pick, and the end-of-episode button and the up-next card offer the same one.
            var pick = Adjacent(1);
            check(pick is not null && pick.Id != episodes[0].Id && pick.Season == 1 && Enumerable.Range(0, 5).All(_ => Adjacent(1)?.Id == pick.Id),
                "the random pick is a regular episode and stays the same until it plays");
            Call("RefreshEpisodeEndAction");
            check((string)endButton.Content == "Next episode →" && (string)endButton.ToolTip == $"Season 1 · {pick!.EpisodeDisplayTitle}", "the end-of-episode button offers the random pick");
            var ended = await End(episodes[0]);
            for (var i = 0; i < 30 && card.Visibility != Visibility.Visible; i++) await Task.Delay(100);
            check(card.Visibility == Visibility.Visible && upNext.Text == $"Season 1 · {pick.EpisodeDisplayTitle}", "the up-next countdown offers the random pick");
            Call("UpNextPlayClicked", window, new RoutedEventArgs(Button.ClickEvent));
            await ended;
            check(Playing().Id == pick.Id, "the countdown plays the random pick");

            // Previous goes back to the episode that played before, not the one before in the show.
            check(Adjacent(-1)?.Id == episodes[0].Id, "Previous goes back to the episode that played before");
            await Step(-1);
            check(Playing().Id == episodes[0].Id, "Previous plays the episode that played before");
            check(Adjacent(1)?.Id == pick.Id, "Next after Previous retraces the same path");

            // Every regular episode plays once; then, without Loop all, Next has nothing left.
            var played = new List<string> { episodes[0].Id };
            for (var i = 0; i < 10 && Adjacent(1) is not null; i++) { await Step(1); played.Add(Playing().Id); Call("UpdateSessionControls"); }
            check(played.Count == episodes.Length && played.Distinct().Count() == episodes.Length && !played.Contains(special.Id),
                "shuffle plays every regular episode once and skips the special");
            check(!next.IsEnabled, "after every episode has played Next is disabled without Loop all");
            Call("RefreshEpisodeEndAction");
            check((string)endButton.Content == "Finish ✓", "the end-of-episode button finishes instead of offering a repeat");
            Loop("All"); Call("UpdateSessionControls");
            var again = Adjacent(1);
            check(next.IsEnabled && again is not null && again.Id != Playing().Id, "Loop all starts a new round with another episode");
            Loop("Off");

            // A queue shuffles too: each video once.
            queue.Clear(); queue.AddRange(movies); Call("PublishQueue");
            await (Task)Call("PlayQueueAt", 0)!;
            Call("UpdateSessionControls");
            check((string)next.ToolTip == "Random video from the queue (N)", "Next names a random video from the queue");
            var queued = new List<string> { Playing().Id };
            for (var i = 0; i < 6 && next.IsEnabled; i++) { await Step(1); queued.Add(Playing().Id); Call("UpdateSessionControls"); }
            check(queued.Count == movies.Length && queued.Distinct().Count() == movies.Length && !next.IsEnabled, "a shuffled queue plays each video once");
            check(previous.IsEnabled, "Previous can go back through the shuffled queue");

            // Off again: the queue's order is back.
            Call("ToggleShuffle", window, new RoutedEventArgs());
            await (Task)Call("PlayQueueAt", 0)!;
            Call("UpdateSessionControls");
            check(!Field<bool>("shuffle") && (string)next.ToolTip == "Next in queue (N)", "turning Shuffle off restores the queue's order");
            await Step(1);
            check(Playing().Id == movies[1].Id, "with Shuffle off Next plays the queue's next video");
        }
        finally
        {
            Set("shuffle", false); Loop("Off");
            if (card.Visibility == Visibility.Visible) Call("EndUpNext", false);
            await (Task)Call("Disconnect", false, false)!;
            queue.Clear(); queue.AddRange(previousQueue); Set("queuePosition", previousPosition);
            Set("queueModeRequested", previousQueueMode);
            Set("items", originalItems);
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
}
