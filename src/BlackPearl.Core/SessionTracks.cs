using System.Text.RegularExpressions;

namespace BlackPearl.Core;

// A track picked by hand: its language and name as the menu showed them, or subtitles turned off.
public record ChosenTrack(string? Language, string? Name, bool Off = false)
{
    public static ChosenTrack Of(LanguageTrack track) => track.Id < 0 ? new(null, null, true) : new(TrackLanguages.LanguageOf(track), Title(track.Name));

    // A track's own title, without the language VLC appends ("Full - [English]") and never its number ("Track 3"),
    // which another episode's file gives to another track. Null when it has none.
    public static string? Title(string? name)
    {
        // A title from a phone is the phone's to send: one longer than any track's is cut before it is read.
        name = name is { Length: > 256 } ? name[..256] : name;
        var title = Regex.Replace(name ?? "", @"\s*-\s*\[[^\]]+\]$", "").Trim();
        title = Regex.Replace(title, @"\s*·\s*(detected|identifying language)$", "", RegexOptions.IgnoreCase).Trim();
        return title.Length == 0 || Regex.IsMatch(title, @"^(Track\s*\d+|Track|Detected)$", RegexOptions.IgnoreCase) ? null : title;
    }
}

// The audio and subtitles picked by hand while the app runs, by show. The show's next episode starts with them, ahead
// of Settings › Playback, which only decides what was never picked. Nothing is saved: closing the app forgets them.
public sealed class SessionTracks
{
    private readonly Dictionary<string, (ChosenTrack? Audio, ChosenTrack? Subtitles)> shows = new(StringComparer.Ordinal);

    // Films and videos outside a show have no next episode, and no key.
    public static string? Key(MediaItem? item) => item?.Series is null || item.IsMusic ? null : LibraryIdentity.ShowKey(item);

    public void Remember(string? show, ChosenTrack track, bool subtitles)
    {
        if (show is null || !subtitles && track.Off) return;
        var chosen = shows.GetValueOrDefault(show);
        shows[show] = subtitles ? chosen with { Subtitles = track } : chosen with { Audio = track };
    }
    public ChosenTrack? Audio(string? show) => show is null ? null : shows.GetValueOrDefault(show).Audio;
    public ChosenTrack? Subtitles(string? show) => show is null ? null : shows.GetValueOrDefault(show).Subtitles;

    // The track of another video that is the one picked: off, or one in the same language, the same name first (as
    // "Signs & Songs" beside "Full"). A track no language is known for matches by its name only. Null when the video has
    // none of it, so the preferences decide instead.
    public static int? Match(IEnumerable<LanguageTrack> tracks, ChosenTrack? chosen, bool subtitles = false)
    {
        if (chosen is null) return null;
        if (chosen.Off) return subtitles ? -1 : null;
        var available = tracks.Where(track => track.Id >= 0).ToArray();
        bool SameName(LanguageTrack track) => chosen.Name is { Length: > 0 } name && string.Equals(ChosenTrack.Title(track.Name), name, StringComparison.OrdinalIgnoreCase);
        if (chosen.Language is null) return available.FirstOrDefault(SameName)?.Id;
        var same = available.Where(track => TrackLanguages.LanguageOf(track) == chosen.Language).ToArray();
        return (same.FirstOrDefault(SameName) ?? same.FirstOrDefault())?.Id;
    }
}
