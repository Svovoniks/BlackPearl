namespace BlackPearl.Core;

/// <summary>A metadata provider failed: a lookup, an image or a language link. <see cref="File"/> is the file the lookup was for,
/// relative to its library folder (that folder's own name first), or null when there was none.</summary>
public sealed record MetadataFailure(string Stage, string? Provider, Exception Error, string? File);

/// <summary>A title left unmatched in the library: one of its files, relative to its library folder, how many files it has, and
/// why the last lookup didn't match it ("not-looked-up" when nothing has tried yet).</summary>
public sealed record UnmatchedTitle(string File, int Files, string Reason);

/// <summary>One metadata run: what it looked up and how that went, and the library's unmatched titles after it. <see cref="Trace"/> is what <see cref="MetadataReports.Starting"/> returned when the run began.</summary>
public sealed record MetadataRun(string Mode, bool TmdbAvailable, string Providers, TimeSpan Duration, bool Completed,
    int LookedUp, int Matched, int Unmatched, int Incomplete, int ProviderFailures, int ArtworkFailures, int Deferred,
    int LibraryTitles, IReadOnlyDictionary<string, int> UnmatchedReasons, IReadOnlyList<UnmatchedTitle> LibraryUnmatched, object? Trace = null);

/// <summary>
/// Where identification reports go beyond the local diagnostics log. The desktop app sends them to Sentry when crash reports
/// are on. File names are always relative to a library folder; whoever receives them decides whether to keep them.
/// </summary>
public static class MetadataReports
{
    public static Action<MetadataFailure>? Failures { get; set; }
    public static Action<MetadataRun>? Runs { get; set; }
    // Called as a run begins; what it returns comes back in that run's MetadataRun.Trace.
    public static Func<object?>? Starting { get; set; }
    internal static object? Start() { try { return Starting?.Invoke(); } catch (Exception) { return null; } }

    private static string[] roots = [];
    // Library folders, longest first so a folder inside another one wins. Read at the start of each run.
    internal static void UseFolders(LibraryStore library)
    {
        try { UseFolders(Wire.Read<LibraryFolder[]>(library.Setting("folders") ?? "[]").Select(x => x.Path)); }
        catch (System.Text.Json.JsonException) { roots = []; }
    }
    internal static void UseFolders(IEnumerable<string> folders) => roots = folders.Where(x => !string.IsNullOrWhiteSpace(x)).OrderByDescending(x => x.Length).ToArray();

    /// <summary>The path below its library folder, led by that folder's own name (Shows/Some Show/S01E01.mkv). A file outside every
    /// library folder keeps only its own name, so nothing above the library, such as the Windows account folder, is ever sent.</summary>
    public static string? LibraryName(string? path) => LibraryName(path, roots);
    internal static string? LibraryName(string? path, IEnumerable<string> folders)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        foreach (var root in folders)
        {
            var folder = root.TrimEnd('\\', '/');
            if (path.Length > folder.Length + 1 && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && path[folder.Length] is '\\' or '/')
            {
                var name = Path.GetFileName(folder);
                var below = path[(folder.Length + 1)..].Replace('\\', '/');
                return string.IsNullOrEmpty(name) ? below : name + "/" + below;
            }
        }
        return Path.GetFileName(path.Replace('\\', '/').TrimEnd('/'));
    }

    internal static void Failure(string stage, string? provider, Exception error, string? path)
    {
        if (Failures is not { } sink) return;
        try { sink(new(stage, provider, error, LibraryName(path))); } catch (Exception) { }
    }

    internal static void Run(MetadataRun run)
    {
        if (Runs is not { } sink) return;
        try { sink(run); } catch (Exception) { }
    }

    // The library's unmatched titles, as the Unmatched category counts them, with the reason the last lookup gave for each.
    internal static (int Titles, UnmatchedTitle[] Unmatched) Library(IEnumerable<MediaItem> items, IEnumerable<MetadataIssues.Issue> issues)
    {
        var titles = items.Where(x => x.Available && !x.IsVirtual && !x.IsExtra && !x.IsBonus && !x.IsMusic).GroupBy(LibraryIdentity.ShowKey).ToArray();
        var unmatched = LibraryCatalog.UnmatchedTitles(titles.SelectMany(x => x));
        var reasons = issues.Where(x => x.Stage == "title").ToDictionary(x => x.Key, x => x.Code);
        return (titles.Length, titles.Where(x => unmatched.Contains(x.Key))
            .Select(x => new UnmatchedTitle(LibraryName(x.OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase).First().Path) ?? "", x.Count(),
                reasons.GetValueOrDefault("title:" + x.Key) ?? "not-looked-up"))
            .OrderBy(x => x.File, StringComparer.OrdinalIgnoreCase).ToArray());
    }
}
