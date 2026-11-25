using BlackPearl.Core;

static class HomeLibraryFixture
{
    public static void Run(Action<bool, string> check)
    {
        MediaItem Episode(string id, string show, int season, int number, string kind = "Show") =>
            new(id, "C:/private/" + id + ".mkv", show, kind, Series: show, Season: season, Episode: number, RuntimeMinutes: 24);
        MediaItem[] items = [
            Episode("a1", "Started show", 1, 1), Episode("a2", "Started show", 1, 2), Episode("a3", "Started show", 2, 1),
            Episode("done", "Finished show", 1, 1), Episode("anime", "New anime", 1, 1, "Anime"),
            Episode("new", "New show", 1, 1),
            new("film", "C:/private/film.mkv", "Paused movie", "Movie", RuntimeMinutes: 120),
            new("fresh", "C:/private/fresh.mkv", "New movie", "Movie"),
            new("missing", "C:/private/missing.mkv", "Unavailable", "Movie", Available: false),
            Episode("virtual", "Upcoming", 1, 1) with { IsVirtual = true },
            Episode("extra", "Bonus", 1, 1) with { IsExtra = true }];
        var playback = new Dictionary<string, string> { ["watched:a1"] = "true", ["watched:done"] = "true", ["position:film"] = "60000" };
        var home = LibraryHome.Browse(items, playback, 7);
        var continuing = home.Where(entry => entry.Section == "continue").ToArray();
        check(continuing.Length == 2 && continuing.Single(entry => entry.Card.Media.Series is not null).Card.Media.Id == "a2",
            "Home continues a started show at its next unwatched episode across seasons");
        check(continuing.Single(entry => entry.Card.Media.Id == "film").PositionMs == 60000, "Home preserves a paused movie's resume position");
        check(!home.Any(entry => entry.Section != "mystery" && entry.Card.Media.Id == "done"), "completed shows leave continue watching and suggestions");
        check(home.Where(entry => entry.Section == "suggestions").Select(entry => entry.Card.Media.Kind).ToHashSet().SetEquals(["Movie", "Show", "Anime"]),
            "suggestions mix unstarted movies, shows, and anime");
        check(home.All(entry => entry.Card.Media.Available && !entry.Card.Media.IsExtra && !entry.Card.Media.IsVirtual), "all Home sections exclude unavailable files, extras, and virtual episodes");
        playback["position:a3"] = "45000";
        check(LibraryHome.Browse(items, playback, 7).Single(entry => entry.Section == "continue" && entry.Card.Media.Series is not null).Card.Media.Id == "a3",
            "a partially watched later episode takes precedence over an earlier unwatched episode");
        var alternate = items.Append(items[0] with { Id = "alternate", Path = "C:/private/alternate.mkv" }).ToArray();
        check(LibraryHome.Browse(alternate, playback, 7).All(entry => entry.Section != "continue" || entry.Card.Media.Id != "alternate"),
            "watched state covers alternate files for the same logical episode");
        check(LibraryHome.Browse([], playback).Count == 0, "an empty library has no mystery pick");
        playback["played:film"] = "2000"; playback["played:a1"] = "1000";
        check(LibraryHome.Browse(items, playback, 7).First(entry => entry.Section == "continue").Card.Media.Id == "film", "the most recently played title leads continue watching");
        playback["played:a3"] = "3000";
        check(LibraryHome.Browse(items, playback, 7).First(entry => entry.Section == "continue").Card.Media.Series == "Started show", "any episode played later moves its show to the front");
        // Shuffle: an earlier episode left part-way, then a later one played after it.
        playback["position:a2"] = "86000"; playback["played:a2"] = "2500";
        HomeLibraryCard ShowCard() => LibraryHome.Browse(items, playback, 7).Single(entry => entry.Section == "continue" && entry.Card.Media.Series is not null);
        check(ShowCard() is { Card.Media.Id: "a3", PositionMs: 45000 }, "a show continues with the part-way episode played last, not the earliest one");
        playback["played:a2"] = "4000";
        check(ShowCard() is { Card.Media.Id: "a2", PositionMs: 86000 }, "going back to an earlier part-way episode makes it the show's card again");
        playback.Remove("played:a2"); playback.Remove("played:a3");
        check(ShowCard().Card.Media.Id == "a2", "part-way episodes with no play time keep episode order");
        playback.Remove("position:a2"); playback["played:a3"] = "3000";
        var overview = LibraryHome.Overview(items, playback, 7);
        check(overview.Unseen.Select(title => title.Card.Media.Id).Order().SequenceEqual(["anime", "fresh", "new"]), "Home reports every unstarted title for filters and counts");
        var seen = overview.Seen.ToDictionary(title => title.Card.Media.Series ?? title.Card.Media.Id);
        check(seen.Keys.ToHashSet().SetEquals(["Finished show", "Started show", "film"]) && seen["Finished show"].Finished &&
            !seen["Started show"].Finished && !seen["film"].Finished && seen["Started show"].Episodes == 3 && seen["film"].Minutes == 120,
            "Home lists the started titles to rewatch, with their facts and whether they were watched to the end");
        check(Enumerable.Range(0, 20).Select(seed => LibraryHome.Draw(overview.Seen, "Movie", MysteryLength.Any, seed)).All(title => title?.Card.Media.Id == "film") &&
            LibraryHome.Draw(overview.Seen, "Anime", MysteryLength.Any, 1) is null, "a rewatch draw keeps to the watched titles and the filters");
        var mysteryIds = Enumerable.Range(0, 100).Select(seed => LibraryHome.Browse(items, playback, seed).Single(entry => entry.Section == "mystery").Card.Media.Id).ToHashSet();
        check(mysteryIds.Contains("anime") && mysteryIds.Contains("fresh") && mysteryIds.Contains("a1"), "mystery can pick movies, shows, and anime with one chance per title");
        var request = new LibraryBrowseRequest(Home: true, Seed: 7);
        var first = SharedLibrary.Browse(items, request, playback: playback);
        check(first.Location == "Home" && first.Entries.Any(entry => entry.HomeSection == "continue" && entry.PositionMs == 60000), "guest Home shares host progress and section labels");
        check(!Wire.Serialize(first).Contains("private") && !Wire.Serialize(first).Contains("position:"), "guest Home keeps host paths and private settings out of the wire response");
        var many = Enumerable.Range(0, 30).Select(index => new MediaItem("film" + index, "", "Film " + index, LibraryHome.Kinds[index % 3], RuntimeMinutes: 20 + index * 5)).ToArray();
        var beforeMetadata = LibraryHome.Browse(many, seed: 7);
        var afterMetadata = LibraryHome.Browse(many.Reverse().Select((item, index) => item with { Title = "Corrected title " + index, Overview = "New overview" }), seed: 7);
        check(beforeMetadata.Select(entry => (entry.Section, entry.Card.Media.Id)).SequenceEqual(afterMetadata.Select(entry => (entry.Section, entry.Card.Media.Id))),
            "background metadata and source-order changes preserve suggestion order and the mystery pick");
        var rows = LibraryHome.Browse(many, seed: 7).Where(entry => entry.Section == "suggestions").ToArray();
        check(LibraryHome.Kinds.All(kind => rows.Count(entry => entry.Card.Media.Kind == kind) == LibraryHome.SuggestionsPerKind) &&
            rows.Select(entry => entry.Card.Media.Kind).Distinct().SequenceEqual(LibraryHome.Kinds), "each category gets its own row of suggestions in a fixed order");
        var reshuffled = LibraryHome.Overview(many, seed: 7, rowSeeds: new Dictionary<string, int> { ["Movie"] = 99 }).Entries.Where(entry => entry.Section == "suggestions").ToArray();
        check(rows.Where(entry => entry.Card.Media.Kind != "Movie").Select(entry => entry.Card.Media.Id).SequenceEqual(reshuffled.Where(entry => entry.Card.Media.Kind != "Movie").Select(entry => entry.Card.Media.Id)) &&
            !rows.Where(entry => entry.Card.Media.Kind == "Movie").Select(entry => entry.Card.Media.Id).SequenceEqual(reshuffled.Where(entry => entry.Card.Media.Kind == "Movie").Select(entry => entry.Card.Media.Id)),
            "new suggestions for one category leave the other rows unchanged");
        var pool = LibraryHome.Overview(many).Unseen;
        check(Enumerable.Range(0, 40).Select(seed => LibraryHome.Draw(pool, "Anime", MysteryLength.Short, seed)).All(title => title is { Card.Media.Kind: "Anime", Minutes: <= 30 }) &&
            LibraryHome.Draw(pool, "Movie", MysteryLength.LongNight, 1) is { Minutes: > 120 } &&
            LibraryHome.Draw(pool, "Anime", MysteryLength.Short, 1, LibraryIdentity.ShowKey(many[0]))?.Card.Media.Id == "film0",
            "mystery draws respect type and length, and offer the only match again rather than nothing");
        var draws = Enumerable.Range(0, 20).Select(seed => LibraryHome.Draw(pool, null, MysteryLength.Any, seed, LibraryIdentity.ShowKey(many[0]))).ToArray();
        check(draws.All(title => title is not null && title.Card.Media.Id != "film0"), "drawing another card avoids the current pick");
        // A guest's draw in the host's library: the host's own unstarted titles, the same filters and the same pick as the host's Home.
        var shared = SharedLibrary.Draw(many, new(Kind: "Anime", Length: MysteryLength.Evening, Seed: 3));
        check(shared is { Entry.HomeSection: "draw", Entry.Kind: "Anime", Matches: 7, Unseen: 30 } && shared.Entry.RuntimeMinutes <= 120 &&
            shared.Entry.Id == LibraryHome.Draw(pool, "Anime", MysteryLength.Evening, 3)?.Card.Media.Id,
            "a guest's draw matches the host's own draw for the same filters and draw number");
        check(SharedLibrary.Draw(many, new(Kind: "Anime", Seed: 99, Current: shared.Entry!.Id)).Entry?.Id == shared.Entry.Id &&
            SharedLibrary.Draw(many, new(Kind: "Anime", Length: MysteryLength.Evening, Seed: 99, Current: shared.Entry.Id, Redraw: true)).Entry?.Id is { } redrawn && redrawn != shared.Entry.Id,
            "a guest's pick stays while it still fits a new filter, and Draw another changes it");
        check(SharedLibrary.Draw(items, new(Kind: "Movie", Length: MysteryLength.LongNight), playback: playback) is { Entry: null, Matches: 0, Unseen: 3 } &&
            SharedLibrary.Draw([], new()) is { Entry: null, Unseen: 0 }, "a guest's draw says when nothing matches, and when nothing is unstarted");
        var series = SharedLibrary.Draw(items, new(Kind: "Show"), playback: playback);
        check(series.Entry is { Id: "new", Level: "series", Episodes: 1 } && !Wire.Serialize(series).Contains("private"),
            "a guest's drawn series carries its episode count and no host paths");
        check(SharedLibrary.Browse(items, request, playback: playback).Entries.Single(entry => entry.HomeSection == "suggestions" && entry.Id == "new").Episodes == 1 &&
            SharedLibrary.Browse(many, request).Entries.All(entry => entry.Year is null) && SharedLibrary.Browse([new("dated", "", "Dated", "Movie", 1999)], request).Entries[0].Year == 1999,
            "guest Home entries carry episode counts and years like the host's Home");
        foreach (var invalid in new LibraryDrawRequest[] { new(Kind: "Music"), new(Length: (MysteryLength)9), new(Current: new string('x', 129)) })
        {
            var rejected = false;
            try { SharedLibrary.Draw(many, invalid); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "an invalid draw request is rejected: " + invalid);
        }
        var homeCount = LibraryHome.Kinds.Length * LibraryHome.SuggestionsPerKind + 1 + Math.Min(many.Length, LibraryHome.UnwatchedLimit);
        var pageCount = (int)Math.Ceiling(homeCount / (double)SharedLibrary.PageSize);
        var pages = Enumerable.Range(0, pageCount).Select(page => SharedLibrary.Browse(many, request with { Page = page })).ToArray();
        check(pages.Sum(page => page.Entries.Length) == homeCount && pages.SelectMany(page => page.Entries).Count(entry => entry.HomeSection == "mystery") == 1 &&
            pages.SelectMany(page => page.Entries).Count(entry => entry.HomeSection == "unwatched") == Math.Min(many.Length, LibraryHome.UnwatchedLimit),
            "guest Home pagination includes every category row, one mystery pick and the unwatched row");
        check(pages.SelectMany(page => page.Entries).Where(entry => entry.HomeSection == "suggestions").Select(entry => entry.Id).Distinct().Count() == LibraryHome.Kinds.Length * LibraryHome.SuggestionsPerKind,
            "seeded guest suggestions remain stable across pages");
        var unicode = many.Select(item => item with { Title = new string('界', 180), Overview = new string('界', 1000) }).ToArray();
        var illustrated = SharedLibrary.Browse(unicode, request, _ => new string('+', 5400));
        check(System.Text.Encoding.UTF8.GetByteCount(Wire.Serialize(new WireMessage("catalog-page", Number: long.MaxValue, Data: Wire.Serialize(illustrated)))) < 65536,
            "guest Home stays within the peer transport budget with Unicode and escaped posters");
        Unwatched(check);
    }

    // Home's Unwatched row: every unstarted title, newest first by its newest file, and the full list behind it.
    static void Unwatched(Action<bool, string> check)
    {
        MediaItem Episode(string id, string show, int season, int number) =>
            new(id, "C:/private/" + id + ".mkv", show, "Show", Series: show, Season: season, Episode: number, RuntimeMinutes: 24);
        MediaItem[] items = [
            new("old", "C:/private/old.mkv", "Old film", "Movie"), new("recent", "C:/private/recent.mkv", "Recent film", "Movie"),
            new("undated", "C:/private/undated.mkv", "Undated film", "Movie"), new("seen", "C:/private/seen.mkv", "Seen film", "Movie"),
            Episode("s1", "Slow show", 1, 1), Episode("s2", "Slow show", 1, 2), Episode("p1", "Paused show", 1, 1),
            new("song", "C:/private/song.flac", "Song", "Music", Series: "Album", Episode: 1)];
        var playback = new Dictionary<string, string>
        {
            ["added:old"] = "1000", ["added:recent"] = "5000", ["added:seen"] = "9000", ["watched:seen"] = "true",
            // A show is as new as its newest episode.
            ["added:s1"] = "500", ["added:s2"] = "7000", ["added:p1"] = "8000", ["position:p1"] = "60000",
        };
        var home = LibraryHome.Overview(items, playback);
        var row = home.Entries.Where(entry => entry.Section == "unwatched").Select(entry => entry.Card.Media.Series ?? entry.Card.Media.Id).ToArray();
        check(row.SequenceEqual(["Slow show", "recent", "old", "undated"]),
            "the Unwatched row lists unstarted titles newest first, a show by its newest episode, titles without a date last");
        check(LibraryHome.Overview(items.Reverse().Append(new("undated2", "C:/private/undated2.mkv", "Another undated", "Movie")), playback).Entries
            .Where(entry => entry.Section == "unwatched").Select(entry => entry.Card.Media.Id).Where(id => id.StartsWith("undated", StringComparison.Ordinal))
            .SequenceEqual(LibraryHome.Overview(items.Append(new("undated2", "C:/private/undated2.mkv", "Renamed undated", "Movie")), playback).Entries
            .Where(entry => entry.Section == "unwatched").Select(entry => entry.Card.Media.Id).Where(id => id.StartsWith("undated", StringComparison.Ordinal))),
            "undated titles keep their order when the library's order or their titles change");
        check(home.Unseen.Select(title => title.Card.Media.Series ?? title.Card.Media.Id).SequenceEqual(row) && home.Unseen[0].AddedAt == 7000,
            "Home's unstarted titles come newest first, with the date they were added");
        var many = Enumerable.Range(0, LibraryHome.UnwatchedLimit + 12).Select(index => new MediaItem("m" + index, "", "Film " + index, "Movie")).ToArray();
        var dated = many.ToDictionary(item => "added:" + item.Id, item => item.Id[1..]);
        check(LibraryHome.Overview(many, dated).Entries.Where(entry => entry.Section == "unwatched").Select(entry => entry.Card.Media.Id)
            .SequenceEqual(Enumerable.Range(12, LibraryHome.UnwatchedLimit).Reverse().Select(index => "m" + index)), "the Unwatched row shows the newest titles up to its limit");
        // The phone's See all: every unstarted title, in pages, newest first.
        var request = new LibraryBrowseRequest(Unwatched: true);
        var first = SharedLibrary.Browse(many, request, playback: dated, pageSize: 40);
        var second = SharedLibrary.Browse(many, request with { Page = 1 }, playback: dated, pageSize: 40);
        check(first is { Location: "Unwatched", Total: 42 } && first.Entries.Length == 40 && second.Entries.Length == 2 &&
            first.Entries.Concat(second.Entries).Select(entry => entry.Id).SequenceEqual(Enumerable.Range(0, 42).Reverse().Select(index => "m" + index)) &&
            first.Entries.All(entry => entry.HomeSection == "unwatched"), "the unwatched list pages through every unstarted title, newest first");
        check(SharedLibrary.Browse(items, request, playback: playback).Entries.Single(entry => entry.Level == "series").Episodes == 2,
            "a show in the unwatched list carries its episode count");

        // Marking: a show or a season marks its episodes; clearing a film clears its other copies too.
        var episodes = new[] { Episode("e1", "Marked", 1, 1), Episode("e2", "Marked", 1, 2), Episode("e3", "Marked", 2, 1), Episode("x1", "Other", 1, 1),
            Episode("bonus", "Marked", 1, 3) with { IsExtra = true } };
        check(LibraryWatchProgress.Marked(episodes, episodes[0], "series", null, true).Order().SequenceEqual(["e1", "e2", "e3"]),
            "marking a show marks every episode of it");
        check(LibraryWatchProgress.Marked(episodes, episodes[0], "season", 1, true).Order().SequenceEqual(["e1", "e2"]) &&
            LibraryWatchProgress.Marked(episodes, episodes[2], "season", 2, false).SequenceEqual(["e3"]),
            "marking a season marks only that season's episodes");
        check(LibraryWatchProgress.Marked(episodes, episodes[1], "episode", null, true).SequenceEqual(["e2"]), "marking an episode marks only it");
        MediaItem[] copies = [new("cut", "C:/private/a.mkv", "Film", "Movie", 2001, MetadataProvider: "tmdb", MetadataId: 42, Matched: true), new("remux", "C:/private/b.mkv", "Film", "Movie", 2001, MetadataProvider: "tmdb", MetadataId: 42, Matched: true)];
        check(LibraryWatchProgress.Marked(copies, copies[0], "movie", null, false).Order().SequenceEqual(["cut", "remux"]) &&
            LibraryWatchProgress.Marked(copies, copies[0], "movie", null, true).SequenceEqual(["cut"]),
            "clearing a film's mark clears its other copies; marking it marks only the copy");

        // Dates come from the files' creation times, once, and skip what isn't a video file on disk.
        var directory = Path.Combine(Path.GetTempPath(), "blackpearl-added-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new LibraryStore(directory);
            var file = Path.Combine(directory, "arrived.mkv");
            File.WriteAllText(file, "x");
            var created = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);
            File.SetCreationTimeUtc(file, created);
            MediaItem[] listed = [new("arrived", file, "Arrived", "Movie"), new("gone", Path.Combine(directory, "gone.mkv"), "Gone", "Movie"),
                new("away", file, "Away", "Movie", Available: false), new("track", file, "Track", "Music", Series: "Album")];
            check(LibraryAdded.Record(store, listed) == 1 && store.Setting("added:arrived") == new DateTimeOffset(created).ToUnixTimeMilliseconds().ToString() &&
                store.Setting("added:gone") is null && store.Setting("added:away") is null && store.Setting("added:track") is null,
                "a video's added date is its file's creation time, recorded only for videos whose file is there");
            File.SetCreationTimeUtc(file, created.AddDays(3));
            check(LibraryAdded.Record(store, listed) == 0 && LibraryAdded.At(store.PlaybackSettings(), "arrived") == new DateTimeOffset(created).ToUnixTimeMilliseconds(),
                "a recorded date is kept, and reaches Home with the watched marks");
            store.Save(listed[2]); store.Setting("added:away", "5");
            check(store.RemoveUnavailable(["away"]) == 1 && store.Setting("added:away") is null && store.Setting("added:arrived") is not null,
                "removing a video whose file is gone also removes its added date");
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }
}
