using System.Windows;
using BlackPearl.Core;

namespace BlackPearl.Desktop;

// Shuffle: Next, the up-next countdown and the end of a video pick a random episode of the show, or a random video of
// the queue, that hasn't played yet. Every one plays once before any repeats; Loop all then starts a new round, and
// without it the shuffle ends. Previous goes back through what played. In a room, whoever plays the video decides.
public partial class MainWindow
{
    private bool shuffle;
    // The show's order and the queue's are kept apart: with a queue loaded, Next follows the queue while an episode
    // that didn't come from it still offers one of its own show's.
    private readonly ShuffleOrder showShuffle = new(), queueShuffle = new();
    private bool Shuffling => shuffle && (room is null || room.Identity?.Host == true && room.IsConnected);
    // Loop all keeps shuffling after every video has played; in a room only whoever plays the video loops.
    private bool ShuffleLoops => loopMode == LoopMode.All && (room is null || room.Identity?.Host == true && room.IsConnected);

    private void ShuffleClicked(object sender, RoutedEventArgs e) => ToggleShuffle(sender, e);
    // The button, the R key, the … menu row and a paired phone all turn it on or off.
    private void ToggleShuffle(object sender, RoutedEventArgs e)
    {
        if (!ShuffleButton.IsEnabled) { RefreshShuffleButton(); SetStatus(StreamerName + " is playing the video and controls shuffle."); return; }
        shuffle = !shuffle;
        showShuffle.Reset(); queueShuffle.Reset();
        RefreshShuffleButton();
        if (ShuffleButton.Visibility != Visibility.Visible) RefreshOverflowMenu();
        SetStatus(shuffle ? "Shuffle on: every episode plays once, in a random order." : "Shuffle off: episodes play in order.");
        // Next, Previous, the end-of-episode button and the queue menu's note all change with it.
        episodeActionItem = null;
        RefreshQueueList(true);
        UpdateSessionControls();
        PrefetchNextLanguages();
        PublishPhoneRemoteState();
    }
    private string ShuffleLabel => shuffle ? "Shuffle on" : "Shuffle off";
    private void RefreshShuffleButton()
    {
        // A viewer's own earlier choice must neither show nor carry over if the streaming role passes to them.
        if (room?.Identity is { Host: false } && shuffle) { shuffle = false; showShuffle.Reset(); queueShuffle.Reset(); RefreshOverflowMenu(); }
        ShuffleButton.IsChecked = shuffle;
        ShuffleButton.ToolTip = !ShuffleButton.IsEnabled ? "Whoever plays the video controls shuffle" : ShuffleLabel + " (R)";
        System.Windows.Automation.AutomationProperties.SetName(ShuffleButton, ShuffleLabel);
    }

    // The episodes shuffle picks from: the show's regular episodes in every season; specials only when there is nothing else.
    private MediaItem[] ShufflePool(MediaItem current)
    {
        var episodes = EpisodePlayback.Episodes(items, current, PreferredVersion(current));
        var regular = episodes.Where(item => item.Season != 0 || item.Id == current.Id).ToArray();
        return regular.Any(item => item.Id != current.Id) ? regular : episodes;
    }
    private static string ShuffleShow(MediaItem current) => "show:" + LibraryIdentity.ShowKey(current);

    /// <summary>The episode shuffle plays after <paramref name="current"/>, or null when the show has none left to play.</summary>
    private MediaItem? ShuffledEpisode(MediaItem current, Func<MediaItem, bool> canPlay)
    {
        if (current.Series is null || current.Season is null || current.Episode is null) return null;
        var pool = ShufflePool(current).DistinctBy(item => item.Id).ToDictionary(item => item.Id);
        var id = showShuffle.Next(ShuffleShow(current), current.Id, [.. pool.Keys], ShuffleLoops,
            id => pool[id] is var item && (item.Id == current.Id || item.Available) && canPlay(item));
        return id is null ? null : pool[id];
    }
    /// <summary>The episode that played before <paramref name="current"/> in this shuffle.</summary>
    private MediaItem? ShuffledPreviousEpisode(MediaItem current, Func<MediaItem, bool> canPlay)
    {
        if (current.Series is null || current.Season is null || current.Episode is null) return null;
        var pool = ShufflePool(current).DistinctBy(item => item.Id).ToDictionary(item => item.Id);
        var id = showShuffle.Previous(ShuffleShow(current), current.Id, [.. pool.Keys], id => pool[id] is var item && item.Available && canPlay(item));
        return id is null ? null : pool[id];
    }
    /// <summary>The queue index to play next (1) or back (-1): in order, or while shuffling, at random or back through what played.</summary>
    private int QueueStepIndex(int direction, Func<MediaItem, bool> available)
    {
        if (!Shuffling) return QueuePlayback.FindNext(queue, queuePosition, direction, available);
        var current = queuePosition >= 0 && queuePosition < queue.Count ? queue[queuePosition] : null;
        var scope = "queue:" + activeQueueId;
        string[] ids = [.. queue.Select(item => item.Id).Distinct()];
        bool CanPlay(string id) => queue.Any(item => item.Id == id && available(item));
        // A queue that doesn't hold the playing video starts with a random one of its videos.
        var id = direction > 0 ? queueShuffle.Next(scope, current?.Id ?? "", ids, ShuffleLoops, CanPlay)
            : current is null ? null : queueShuffle.Previous(scope, current.Id, ids, CanPlay);
        if (id is null) return -1;
        // A video queued twice plays from its first place other than the current one.
        var index = queue.FindIndex(item => item.Id == id && available(item));
        return index == queuePosition ? queue.FindIndex(index + 1, item => item.Id == id && available(item)) : index;
    }
    /// <summary>What follows the playing video: the next in the queue it started from, or its next episode.</summary>
    private MediaItem? FollowingEpisode(MediaItem current, Func<MediaItem, bool> canPlay) =>
        Shuffling ? ShuffledEpisode(current, canPlay) : EpisodePlayback.Next(items, current, false, canPlay, PreferredVersion(current));
}
