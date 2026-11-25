using System.Globalization;

namespace BlackPearl.Core;

// When each video arrived in the library, for Home's Unwatched row: its file's creation time, which a download or a copy
// sets as the file arrives and a move within a drive keeps. Kept as "added:<id>" beside the watched marks rather than in
// the video's record, so recording it rewrites no record (and starts no metadata pass), and a moved file found again
// keeps its date with its id.
public static class LibraryAdded
{
    public const string Prefix = "added:";

    public static long? At(IReadOnlyDictionary<string, string> settings, string id) =>
        settings.TryGetValue(Prefix + id, out var value) && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var at) ? at : null;

    // Records the date of each video that has none yet, and returns how many it recorded. Reads file dates, which can wake
    // a sleeping drive: callers run it off the UI thread.
    public static int Record(LibraryStore library, IEnumerable<MediaItem> items, CancellationToken cancellation = default)
    {
        var known = library.SettingsWithPrefix(Prefix);
        var found = new List<KeyValuePair<string, string>>();
        foreach (var item in items)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!item.Available || item.IsVirtual || item.IsExtra || item.IsMusic || known.ContainsKey(Prefix + item.Id)) continue;
            DateTime created;
            try { created = File.GetCreationTimeUtc(item.Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { continue; }
            // A file that is gone reads as 1601; it is dated once it is back.
            if (created.Year < 1980) continue;
            found.Add(KeyValuePair.Create(Prefix + item.Id, new DateTimeOffset(created).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)));
        }
        if (found.Count > 0) library.SetSettings(found);
        return found.Count;
    }
}
