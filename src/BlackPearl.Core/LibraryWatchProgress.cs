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
        static IEnumerable<string> Keys(MediaItem item) => item.Season is not null && item.Episode is not null && item.NumberingConflict is null
            ? LibraryIdentity.EpisodeNumbers(item).Select(number => $"{item.Season}:{number}") : ["file:" + item.Id];
        var completed = episodes.Where(item => watched(item.Id)).SelectMany(Keys).ToHashSet();
        var required = card.Level switch
        {
            "series" => episodes,
            "season" => episodes.Where(item => (item.Season ?? -1) == card.Season).ToArray(),
            _ => card.Media.IsVirtual || card.Media.IsExtra ? [] : new[] { card.Media }
        };
        return required.Length > 0 && required.SelectMany(Keys).All(completed.Contains);
    }
}
