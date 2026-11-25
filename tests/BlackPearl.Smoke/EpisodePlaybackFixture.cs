using BlackPearl.Core;

static class EpisodePlaybackFixture
{
    public static void Run(Action<bool, string> check)
    {
        check(!EpisodePlayback.NearEnd(0, 0) && !EpisodePlayback.NearEnd(-1, 60000), "unknown duration and unavailable position never show the end action");
        check(!EpisodePlayback.NearEnd(509999, 600000) && EpisodePlayback.NearEnd(540000, 600000), "end action waits for the final ten percent of a short episode");
        check(!EpisodePlayback.NearEnd(3509999, 3600000) && EpisodePlayback.NearEnd(3510000, 3600000), "long episodes show the end action only in the final ninety seconds");
        var first = new MediaItem("first", "first.mkv", "Example", "Show", Series: "Example", Season: 1, Episode: 1, ShowId: "original");
        var second = first with { Id = "second", Path = "second.mkv", Episode = 2 };
        var nextSeason = first with { Id = "next-season", Path = "next.mkv", Season = 2 };
        var otherShow = second with { Id = "other", ShowId = "remake" };
        MediaItem[] library = [otherShow, nextSeason, second, first];
        check(EpisodePlayback.Next(library, first, false, _ => true)?.Id == second.Id, "next episode follows numbering rather than library order");
        check(EpisodePlayback.Next(library, second, false, _ => true)?.Id == nextSeason.Id, "last episode offers the first episode of the next season");
        check(EpisodePlayback.Next(library, first, true, _ => true) is null, "an existing queue suppresses the next-episode offer");
        check(EpisodePlayback.Next(library, nextSeason, false, _ => true) is null, "series finale has no next-episode offer");
        check(EpisodePlayback.Next(library, first with { Series = null }, false, _ => true) is null, "standalone videos do not offer another episode");
        check(EpisodePlayback.Next(library, first with { Episode = null }, false, _ => true) is null, "unknown episode numbering does not guess a continuation");
        check(EpisodePlayback.Next(library, first, false, item => item.Id != second.Id)?.Id == nextSeason.Id, "unplayable files are skipped across season boundaries");
        check(EpisodePlayback.Next([first, second with { IsVirtual = true }, nextSeason], first, false, _ => true)?.Id == nextSeason.Id,
            "missing episode placeholders are not offered for playback");
        check(EpisodePlayback.Next([first, second with { IsExtra = true }, nextSeason], first, false, _ => true)?.Id == nextSeason.Id,
            "extras are excluded from episode continuation");
        check(EpisodePlayback.Next(library, first with { EpisodeEnd = 2 }, false, _ => true)?.Id == nextSeason.Id,
            "multi-episode files continue after the entire completed range");
        check(EpisodePlayback.Next([first, otherShow], first, false, _ => true) is null, "same-name remakes cannot supply the next episode");

        var duplicate = second with { Id = "duplicate", Path = "duplicate.mkv" };
        MediaItem[] collection = [first, second, duplicate, nextSeason, otherShow];
        var completed = new HashSet<string> { first.Id };
        var seasonCard = new LibraryCard("Season 1", "", null, first, "season", 1);
        var showCard = new LibraryCard("Example", "", null, first, "series");
        bool Watched(LibraryCard card) => LibraryWatchProgress.IsWatched(collection, card, completed.Contains);
        check(!Watched(seasonCard) && !Watched(showCard), "partially watched seasons and shows have no completed badge");
        completed.Add(duplicate.Id);
        check(Watched(seasonCard) && !Watched(showCard), "one watched version completes its episode and season without completing the show");
        check(Watched(new("Episode 2", "", null, second, "episode", 1)), "watched episode badge carries across duplicate file versions");
        completed.Add(nextSeason.Id);
        check(Watched(showCard), "show badge appears only after every season is watched");
        completed.Remove(first.Id);
        check(!Watched(seasonCard) && !Watched(showCard), "clearing an episode's watched state clears season and show badges");
        completed.Add("double");
        collection = [first, second, first with { Id = "double", EpisodeEnd = 2 }, nextSeason, first with { Id = "extra", IsExtra = true }, first with { Id = "future", Season = 3, IsVirtual = true }];
        check(Watched(seasonCard) && Watched(showCard), "watched multi-episode files cover their range while extras and future placeholders do not block completion");
        check(!LibraryWatchProgress.IsWatched(collection, seasonCard with { Season = 99 }, completed.Contains), "empty seasons are never marked watched");
        var movie = first with { Id = "movie", Series = null };
        var movieCard = new LibraryCard("Movie", "", null, movie, "movie");
        check(!LibraryWatchProgress.IsWatched([movie], movieCard, completed.Contains), "unwatched movie has no completed badge");
        completed.Add(movie.Id);
        check(LibraryWatchProgress.IsWatched([movie], movieCard, completed.Contains), "completed movie has a watched badge");

        // Show and season progress is by running time, over the episodes that have a file.
        var e1 = first with { RuntimeMinutes = 20 }; var e2 = second with { RuntimeMinutes = 20 };
        var e3 = first with { Id = "e3", Path = "e3.mkv", Episode = 3, RuntimeMinutes = 40 };
        var copy = e2 with { Id = "e2-copy", Path = "e2-copy.mkv" };
        var missing = first with { Id = "e4", Path = "", Episode = 4, RuntimeMinutes = 500, IsVirtual = true };
        var s2 = nextSeason with { RuntimeMinutes = null };
        MediaItem[] shows = [e1, e2, copy, e3, missing, s2, first with { Id = "bonus", IsExtra = true, RuntimeMinutes = 90 }, otherShow with { RuntimeMinutes = 20 }];
        var done = new HashSet<string>(); var places = new Dictionary<string, long>();
        ShowProgress Of(LibraryCard card) => LibraryWatchProgress.Progress(shows, card, done.Contains, id => places.GetValueOrDefault(id));
        check(Of(seasonCard) == new ShowProgress(0, 0, 3) && Of(showCard).Episodes == 4, "an unwatched show has no progress; copies count once and placeholders and extras not at all");
        done.Add(e1.Id); places[e3.Id] = 20 * 60000;
        check(Math.Abs(Of(seasonCard).Percent - 50) < 0.01 && Of(seasonCard).Watched == 1, "a season's progress counts watched episodes whole and part-way ones to their place, by running time");
        // Season 2's one episode has no known length, so it counts as the show's typical one (20 minutes of 20, 20, 40).
        check(Math.Abs(Of(showCard).Percent - 40) < 0.01, "an episode with no known length counts as long as a typical one");
        done.Add(copy.Id);
        check(Of(seasonCard).Watched == 2 && Math.Abs(Of(seasonCard).Percent - 75) < 0.01, "one watched copy counts its episode as watched");
        places[e1.Id] = 5000; places[s2.Id] = 9000;
        check(Math.Abs(Of(showCard).Percent - 60) < 0.01, "a moment's start does not count as progress");
        check(Of(movieCard) == default && Of(seasonCard with { Season = 99 }) == default, "films and empty seasons have no show progress");

        // An opening clip waits for the watched mark, so it keeps the bar short of full without counting as an episode.
        var opening = first with { Id = "opening", Path = "opening.mkv", Episode = null, IsBonus = true, RuntimeMinutes = 2 };
        shows = [e1 with { RuntimeMinutes = 24 }, e2 with { RuntimeMinutes = 24 }, opening];
        done.Clear(); places.Clear(); done.Add(e1.Id); done.Add(e2.Id);
        var unfinished = Of(seasonCard);
        check(!LibraryWatchProgress.IsWatched(shows, seasonCard, done.Contains) && unfinished is { Watched: 2, Episodes: 2 } && Math.Abs(unfinished.Percent - 96) < 0.01,
            "an unwatched bonus clip keeps a season short of watched, by its own length, and is not an episode");
        done.Add(opening.Id);
        check(LibraryWatchProgress.IsWatched(shows, seasonCard, done.Contains) && Of(seasonCard).Percent == 100, "a season reaches 100% when it is marked watched");
        // A double episode beside a copy of its first half: episode 1 counts once, not once per file.
        var both = first with { Id = "both", Path = "both.mkv", EpisodeEnd = 2, RuntimeMinutes = 40 };
        shows = [both, e1, e3 with { RuntimeMinutes = 20 }];
        done.Clear(); done.Add(both.Id);
        check(Math.Abs(Of(seasonCard).Percent - 200 / 3d) < 0.01 && Of(seasonCard) is { Watched: 2, Episodes: 3 }, "a file of two episodes shares its length between them");
        shows = [e1]; done.Clear(); places[e1.Id] = 25 * 60000;
        check(Of(seasonCard).Percent < 100, "a saved place past a short-read length does not look finished");
    }
}
