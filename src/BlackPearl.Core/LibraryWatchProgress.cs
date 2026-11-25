namespace BlackPearl.Core;

public static class LibraryWatchProgress
{
    public static bool CanContinue(IEnumerable<MediaItem> source, LibraryCard card, IReadOnlyDictionary<string, string> settings)
    {
        var candidates = card.Level is "series" or "season"
            ? source.Where(item => LibraryIdentity.SameShow(item, card.Media) &&
                (card.Level == "series" || (item.Season ?? -1) == card.Season))
            : [card.Media];
        return candidates.Any(item => item.Available && !item.IsVirtual && !item.IsExtra &&
            settings.GetValueOrDefault("watched:" + item.Id) != "true" &&
            long.TryParse(settings.GetValueOrDefault("position:" + item.Id), out var position) && position >= 10000);
    }

    // The videos that marking item watched or unwatched changes, item first: a show's or season's episodes; for a film or
    // episode just its file, except that clearing it also clears the other copies that cover it, which would otherwise
    // keep the title marked.
    public static string[] Marked(IEnumerable<MediaItem> source, MediaItem item, string level, int? season, bool watched)
    {
        var affected = !watched && level is "movie" or "video" && item.Series is null ? LibraryCatalog.Versions(source, item)
            : level is "series" or "season"
            ? source.Where(candidate => !candidate.IsExtra && !candidate.IsVirtual && LibraryIdentity.SameShow(candidate, item) &&
                (level == "series" || (candidate.Season ?? -1) == season)).ToArray()
            : !watched && item.Series is not null && item.Season is not null && item.Episode is not null && item.NumberingConflict is null
            ? source.Where(candidate => LibraryIdentity.SameShow(candidate, item) && candidate.Season == item.Season && candidate.NumberingConflict is null &&
                LibraryIdentity.EpisodeNumbers(candidate).Intersect(LibraryIdentity.EpisodeNumbers(item)).Any()).ToArray()
            : [item];
        return affected.Prepend(item).Select(entry => entry.Id).Distinct().ToArray();
    }

    // A film card with several copies is watched when its copy is, or when another copy is and its own copy isn't part-way:
    // the copy it leads with is the one chosen or played last, as Home's Continue watching row picks it.
    public static bool IsWatched(IEnumerable<MediaItem> source, LibraryCard card, Func<string, bool> watched, Func<string, long>? position = null)
    {
        if (card.Media.Series is null) return watched(card.Media.Id) || card.Versions > 1 && (position?.Invoke(card.Media.Id) ?? 0) < 10000 &&
            source.Any(item => LibraryCatalog.IsVersionOf(card.Media, item) && watched(item.Id));
        var episodes = source.Where(item => LibraryIdentity.SameShow(item, card.Media) && !item.IsExtra && !item.IsVirtual).ToArray();
        var completed = episodes.Where(item => watched(item.Id)).SelectMany(Keys).ToHashSet();
        var required = card.Level switch
        {
            "series" => episodes,
            "season" => episodes.Where(item => (item.Season ?? -1) == card.Season).ToArray(),
            _ => card.Media.IsVirtual || card.Media.IsExtra ? [] : new[] { card.Media }
        };
        return required.Length > 0 && required.SelectMany(Keys).All(completed.Contains);
    }

    // How much of a show, or of one of its seasons, has been watched, by running time. Only episodes with a file count:
    // placeholders for episodes the library doesn't have add nothing to the total, and neither do extras or bonus clips.
    // A watched episode counts its whole length, one part-way its saved place. Copies of one episode count once, and an
    // episode with no known length counts as long as the others' typical one. Episodes are counted as the card's caption
    // counts them (LibraryIdentity.EpisodeCount), so "5 of 12 episodes" matches the 12 below the card.
    public static ShowProgress Progress(IEnumerable<MediaItem> source, LibraryCard card, Func<string, bool> watched, Func<string, long> position)
    {
        if (card.Level is not ("series" or "season")) return default;
        var episodes = source.Where(item => LibraryIdentity.SameShow(item, card.Media) && !item.IsExtra && !item.IsVirtual && !item.IsBonus).ToArray();
        var completed = episodes.Where(item => watched(item.Id)).SelectMany(LibraryIdentity.EpisodeKeys).ToHashSet();
        var files = (card.Level == "series" ? episodes : episodes.Where(item => (item.Season ?? -1) == card.Season)).ToArray();
        var counted = files.GroupBy(item => string.Join("|", LibraryIdentity.EpisodeKeys(item).Order())).ToArray();
        if (counted.Length == 0) return default;
        var keys = files.SelectMany(LibraryIdentity.EpisodeKeys).Distinct().ToArray();
        var known = counted.Select(group => group.Max(item => item.RuntimeMinutes ?? 0)).Where(minutes => minutes > 0).Order().ToArray();
        double typical = known.Length > 0 ? known[known.Length / 2] : 0;
        double total = 0, seen = 0;
        foreach (var group in counted)
        {
            var minutes = group.Max(item => item.RuntimeMinutes ?? 0);
            var length = minutes > 0 ? minutes : typical > 0 ? typical : 1;
            total += length;
            if (group.Any(item => watched(item.Id)) || LibraryIdentity.EpisodeKeys(group.First()).All(completed.Contains)) { seen += length; continue; }
            var at = group.Max(item => position(item.Id));
            if (at >= 10000 && (minutes > 0 || typical > 0)) seen += Math.Min(length, at / 60000d);
        }
        return new(Math.Clamp(seen / total * 100, 0, 100), keys.Count(completed.Contains), keys.Length);
    }

    private static IEnumerable<string> Keys(MediaItem item) => item.Season is not null && item.Episode is not null && item.NumberingConflict is null
        ? LibraryIdentity.EpisodeNumbers(item).Select(number => $"{item.Season}:{number}") : ["file:" + item.Id];
}

// A show's or season's watch progress: the share of its running time watched, and how many of its episodes are done.
public readonly record struct ShowProgress(double Percent, int Watched, int Episodes);
