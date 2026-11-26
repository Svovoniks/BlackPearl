namespace BlackPearl.Core;

public record HomeLibraryCard(LibraryCard Card, string Section, long PositionMs = 0);
// A title with the facts Home uses to describe it and filter mystery draws. AddedAt: when its newest file arrived
// (LibraryAdded), 0 when unknown. Finished: a started title watched to the end, as its watched mark says.
public record HomeTitle(LibraryCard Card, int Episodes, int? Minutes, long AddedAt = 0, bool Finished = false);
// Unseen lists the unstarted titles newest first, as the Unwatched row does; Seen the started ones, to draw a rewatch from.
public record HomeOverview(List<HomeLibraryCard> Entries, HomeTitle[] Unseen, HomeTitle[] Seen);
public enum MysteryLength { Any, Short, Evening, LongNight }

public static class LibraryHome
{
    public static readonly string[] Kinds = ["Anime", "Show", "Movie"];
    public const int ContinueLimit = 20, SuggestionsPerKind = 7, UnwatchedLimit = 30;
    public static List<HomeLibraryCard> Browse(IEnumerable<MediaItem> source, IReadOnlyDictionary<string, string>? playback = null, int seed = 0) =>
        Overview(source, playback, seed).Entries;
    // Row seeds let one category reshuffle its suggestions without moving the others.
    public static HomeOverview Overview(IEnumerable<MediaItem> source, IReadOnlyDictionary<string, string>? playback = null, int seed = 0,
        IReadOnlyDictionary<string, int>? rowSeeds = null)
    {
        playback ??= new Dictionary<string, string>();
        bool Watched(string id) => playback.TryGetValue("watched:" + id, out var value) && value == "true";
        long Position(string id) => playback.TryGetValue("position:" + id, out var value) && long.TryParse(value, out var position) ? Math.Max(0, position) : 0;
        long Played(string id) => playback.TryGetValue("played:" + id, out var value) && long.TryParse(value, out var time) ? time : 0;
        long Resume(string id) => LibraryWatchProgress.Resume(key => playback.GetValueOrDefault(key), id, Watched(id));
        // Home suggests things to watch; music lives in its own library tab.
        var all = source.Where(item => !item.IsExtra && !item.IsVirtual && !item.IsMusic).ToArray();
        var titles = LibraryCatalog.WithCurrentVersions(all, LibraryCatalog.Browse(all.Where(item => item.Available)), playback);
        var groups = all.GroupBy(LibraryIdentity.TitleKey).ToDictionary(group => group.Key, group => group.ToArray());
        var continuing = new List<(HomeLibraryCard Entry, long Played)>();
        var unseen = new List<HomeTitle>();
        var seen = new List<HomeTitle>();
        foreach (var title in titles)
        {
            var members = groups[LibraryIdentity.TitleKey(title.Media)];
            bool Completed(MediaItem item) => LibraryWatchProgress.IsWatched(members,
                new("", "", null, item, item.Series is null ? "movie" : "episode"), Watched);
            var started = members.Any(item => Watched(item.Id) || Position(item.Id) >= 10000);
            var files = LibraryCatalog.PreferredFiles(members.Where(item => item.Available)).ToArray();
            HomeTitle Facts(bool finished) => new(title, title.Level == "series" ? files.Length : 0,
                files.Select(item => item.RuntimeMinutes).FirstOrDefault(minutes => minutes > 0),
                files.Select(item => LibraryAdded.At(playback, item.Id) ?? 0).DefaultIfEmpty().Max(), finished);
            if (!started) { unseen.Add(Facts(false)); continue; }
            seen.Add(Facts(LibraryWatchProgress.IsWatched(members, title, Watched, Position)));
            // A film continues with a copy left part-way, the one played last; with none, a watched copy means it's done. A
            // watched copy being watched again is part-way too, at its place in the rewatch.
            if (title.Level == "movie")
            {
                if (members.Where(item => item.Available && Resume(item.Id) >= 10000).MaxBy(item => Played(item.Id)) is { } resume)
                    continuing.Add((new(resume.Id == title.Media.Id ? title : title with { Media = resume }, "continue", Resume(resume.Id)), members.Max(item => Played(item.Id))));
                continue;
            }
            var remaining = files.Where(item => !Completed(item)).OrderBy(item => item.Season ?? int.MaxValue)
                .ThenBy(item => item.Episode ?? int.MaxValue).ThenBy(item => item.Id).ToArray();
            // A show continues with the episode left part-way that was played last, so one watched out of order (shuffle, a
            // queue, a jump ahead) isn't hidden behind an earlier one left part-way; ties keep episode order. With none part-way,
            // the first unwatched episode is next. Specials (season 0: a pilot, an OVA, a credit-free opening) aren't next in
            // line: they'd sort before season 1 and pull a viewer in season 2 back to a pilot, and a show with only specials
            // left is done. A special left part-way still continues, and a show with nothing but specials goes through them.
            static bool Regular(MediaItem item) => item.Season != 0 && !item.IsBonus;
            var regular = files.Any(Regular);
            var next = remaining.Where(item => Position(item.Id) >= 10000).MaxBy(item => Played(item.Id))
                ?? remaining.FirstOrDefault(item => !regular || Regular(item));
            // Every episode watched: a show being watched again continues with the episode rewatched last, where the rewatch
            // left it, or once that was seen to the end, the episode after it. A show finished and not started again, or
            // whose rewatch reached its last episode, stays out.
            if (next is null)
            {
                bool Counts(MediaItem item) => !regular || Regular(item);
                if (members.Where(item => item.Available && Counts(item) && playback.ContainsKey(LibraryWatchProgress.RewatchPrefix + item.Id))
                    .MaxBy(item => Played(item.Id)) is not { } again) continue;
                // Only while the rewatch is the show's latest viewing: one replayed before the rest of the show was watched
                // for the first time is over.
                if (Played(again.Id) < members.Where(Counts).Max(item => Played(item.Id))) continue;
                var end = (again.Season ?? int.MaxValue, again.EpisodeEnd ?? again.Episode ?? int.MaxValue);
                next = Resume(again.Id) >= 10000 ? again : files.Where(Counts)
                    .OrderBy(item => item.Season ?? int.MaxValue).ThenBy(item => item.Episode ?? int.MaxValue).ThenBy(item => item.Id)
                    .FirstOrDefault(item => (item.Season ?? int.MaxValue, item.Episode ?? int.MaxValue).CompareTo(end) > 0);
                if (next is null) continue;
            }
            var card = next.Series is null ? title : new LibraryCard(next.Series + " · " + next.EpisodeDisplayTitle,
                next.Caption, LibraryCatalog.EpisodeArtwork(next), next, "episode", next.Season);
            continuing.Add((new(card, "continue", Resume(next.Id)), members.Max(item => Played(item.Id))));
        }
        // Most recently played first; titles without a play time keep catalog order.
        var result = continuing.OrderByDescending(entry => entry.Played).Select(entry => entry.Entry).Take(ContinueLimit).ToList();
        // Rank stable identities so metadata/title sorting cannot reshuffle a row.
        string Rank(LibraryCard card, string section, int rankSeed) => LibraryIdentity.Hash($"{rankSeed}:{section}:{LibraryIdentity.TitleKey(card.Media)}");
        foreach (var kind in Kinds)
        {
            var rowSeed = rowSeeds?.GetValueOrDefault(kind, seed) ?? seed;
            result.AddRange(unseen.Where(title => title.Card.Media.Kind == kind).Select(title => title.Card)
                .OrderBy(card => Rank(card, "suggestions", rowSeed), StringComparer.Ordinal).Take(SuggestionsPerKind)
                .Select(card => new HomeLibraryCard(card, "suggestions")));
        }
        if (titles.Count > 0) result.Add(new(titles.MinBy(card => Rank(card, "mystery", seed), StringComparer.Ordinal)!, "mystery"));
        // Unwatched: every unstarted title, newest first; a show is as new as its newest file. Titles with no known date
        // follow, by their stable identity, so metadata arriving in the background doesn't reorder them. It comes last, so
        // apps that don't know it find the other sections where they were.
        var newest = unseen.OrderByDescending(title => title.AddedAt).ThenBy(title => LibraryIdentity.TitleKey(title.Card.Media), StringComparer.Ordinal).ToArray();
        result.AddRange(newest.Take(UnwatchedLimit).Select(title => new HomeLibraryCard(title.Card, "unwatched")));
        return new(result, newest, seen.ToArray());
    }
    public static bool Matches(HomeTitle title, string? kind, MysteryLength length)
    {
        if (kind is not null && title.Card.Media.Kind != kind) return false;
        var series = title.Card.Level == "series";
        return length switch
        {
            MysteryLength.Short => title.Minutes is > 0 and <= 30,
            MysteryLength.Evening => title.Minutes is > 0 and <= 120,
            MysteryLength.LongNight => series ? title.Episodes >= 6 : title.Minutes > 120,
            _ => true
        };
    }
    // The draw's types of title: any, then each one.
    public static readonly string?[] MysteryKinds = [null, "Movie", "Show", "Anime"];
    /// <summary>
    /// A random choice for the draw's filters, among the ones that have something to draw: the titles not started or the
    /// ones watched, a type and a length. With a title to avoid (TitleKey), only choices with another title to draw, while
    /// there are any. Null when neither pool has anything.
    /// </summary>
    public static (bool Rewatch, string? Kind, MysteryLength Length)? RandomFilters(IReadOnlyCollection<HomeTitle> unseen, IReadOnlyCollection<HomeTitle> seen, Random random, string? avoid = null)
    {
        (bool, string?, MysteryLength)[] Choices(string? skip) =>
            (from rewatch in new[] { false, true }
             from kind in MysteryKinds
             from length in Enum.GetValues<MysteryLength>()
             where (rewatch ? seen : unseen).Any(title => Matches(title, kind, length) && (skip is null || LibraryIdentity.TitleKey(title.Card.Media) != skip))
             select (rewatch, kind, length)).ToArray();
        var choices = Choices(avoid);
        if (choices.Length == 0 && avoid is not null) choices = Choices(null);
        return choices.Length == 0 ? null : choices[random.Next(choices.Length)];
    }
    // Keeps the current pick while it still matches, unless another is asked for; a new draw avoids the current pick.
    public static HomeTitle? Pick(IEnumerable<HomeTitle> pool, string? kind, MysteryLength length, int seed, string? current, bool redraw)
    {
        var titles = pool as IReadOnlyCollection<HomeTitle> ?? pool.ToArray();
        return (redraw || current is null ? null : titles.FirstOrDefault(title => LibraryIdentity.TitleKey(title.Card.Media) == current && Matches(title, kind, length)))
            ?? Draw(titles, kind, length, seed, redraw ? current : null);
    }
    // Draws one title from the pool, avoiding the current pick when another match exists.
    public static HomeTitle? Draw(IEnumerable<HomeTitle> pool, string? kind, MysteryLength length, int seed, string? avoid = null)
    {
        var matches = pool.Where(title => Matches(title, kind, length)).ToArray();
        var choices = matches.Where(title => LibraryIdentity.TitleKey(title.Card.Media) != avoid).ToArray();
        if (choices.Length == 0) choices = matches;
        return choices.MinBy(title => LibraryIdentity.Hash($"{seed}:draw:{LibraryIdentity.TitleKey(title.Card.Media)}"), StringComparer.Ordinal);
    }
}
