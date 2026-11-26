namespace BlackPearl.Core;

public static class LibraryWatchProgress
{
    // A video already watched and played again is a rewatch. Its place in the rewatch is kept under rewatch:, apart from
    // position:, so the watched mark stays and a watched video's saved place stays 0. 0 once the rewatch reached the end.
    // Only a rewatch writes it, and marking the video watched or unwatched forgets it, so a show whose episodes are all
    // watched comes back to Continue watching only when someone is actually watching it again.
    public const string RewatchPrefix = "rewatch:";
    // Where a video picks up: its saved place, or for a watched one, its place in the rewatch.
    public static long Resume(Func<string, string?> setting, string id, bool watched) =>
        long.TryParse(setting((watched ? RewatchPrefix : "position:") + id), out var at) ? Math.Max(0, at) : 0;

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

    // How much of a show, or of one of its seasons, has been watched, by running time. Only files the library has count:
    // placeholders for episodes it doesn't have add nothing to the total, and neither do extras. Bonus clips (openings,
    // endings) count towards the time, as the watched mark (IsWatched) waits for them too, but not as episodes. A watched
    // episode counts its whole length, one part-way its saved place. Copies of one episode count once, a file holding
    // several episodes shares its length among them, and an episode with no known length counts as long as the others'
    // typical one. Episodes are counted as the card's caption counts them (LibraryIdentity.EpisodeCount), so "5 of 12
    // episodes" matches the 12 below the card. It reaches 100% only when the watched mark would be set.
    public static ShowProgress Progress(IEnumerable<MediaItem> source, LibraryCard card, Func<string, bool> watched, Func<string, long> position)
    {
        if (card.Level is not ("series" or "season")) return default;
        var shown = source.Where(item => LibraryIdentity.SameShow(item, card.Media) && !item.IsExtra && !item.IsVirtual).ToArray();
        var completed = shown.Where(item => watched(item.Id)).SelectMany(LibraryIdentity.EpisodeKeys).ToHashSet();
        var files = (card.Level == "series" ? shown : shown.Where(item => (item.Season ?? -1) == card.Season)).ToArray();
        var episodes = files.Where(item => !item.IsBonus).SelectMany(LibraryIdentity.EpisodeKeys).Distinct().ToArray();
        if (episodes.Length == 0) return default;
        static double Share(MediaItem item) => item.RuntimeMinutes is > 0 ? item.RuntimeMinutes.Value / (double)LibraryIdentity.EpisodeKeys(item).Count() : 0;
        double Typical(bool bonus) => files.Where(item => item.IsBonus == bonus).Select(Share).Where(minutes => minutes > 0).Order().ToArray() is { Length: > 0 } known ? known[known.Length / 2] : 0;
        double typicalEpisode = Typical(false), typicalBonus = Typical(true);
        double Length(MediaItem item) => Share(item) is > 0 and var share ? share
            : (item.IsBonus ? typicalBonus : typicalEpisode) is > 0 and var typical ? typical : item.IsBonus ? typicalEpisode : typicalBonus;
        // How far into a file its saved place is, when its length is known well enough to tell.
        double Part(MediaItem item)
        {
            var at = position(item.Id); var minutes = Length(item) * LibraryIdentity.EpisodeKeys(item).Count();
            return at >= 10000 && minutes > 0 ? Math.Min(1, at / 60000d / minutes) : 0;
        }
        double total = 0, seen = 0;
        foreach (var episode in files.SelectMany(item => LibraryIdentity.EpisodeKeys(item).Select(key => (Key: key, File: item))).GroupBy(entry => entry.Key))
        {
            var length = episode.Max(entry => Length(entry.File)) is > 0 and var known ? known : 1;
            total += length;
            seen += completed.Contains(episode.Key) ? length : length * episode.Max(entry => Part(entry.File));
        }
        var percent = Math.Clamp(seen / total * 100, 0, 100);
        // A saved place past a length that was read short must not look finished.
        if (!files.SelectMany(LibraryIdentity.EpisodeKeys).All(completed.Contains)) percent = Math.Min(percent, 99);
        return new(percent, episodes.Count(completed.Contains), episodes.Length);
    }

    private static IEnumerable<string> Keys(MediaItem item) => item.Season is not null && item.Episode is not null && item.NumberingConflict is null
        ? LibraryIdentity.EpisodeNumbers(item).Select(number => $"{item.Season}:{number}") : ["file:" + item.Id];
}

// A show's or season's watch progress: the share of its running time watched, and how many of its episodes are done.
public readonly record struct ShowProgress(double Percent, int Watched, int Episodes);
