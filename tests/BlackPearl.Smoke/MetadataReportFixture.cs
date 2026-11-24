using BlackPearl.Core;

// What identification reports may say about a file: its path below its library folder, never anything above it.
static class MetadataReportFixture
{
    public static void Run(Action<bool, string> check)
    {
        string[] folders = [@"C:\Users\Ann\Videos\Shows\Anime", @"C:\Users\Ann\Videos\Shows", @"D:\"];
        check(MetadataReports.LibraryName(@"C:\Users\Ann\Videos\Shows\Night Show\Season 1\Night.Show.S01E02.mkv", folders) == "Shows/Night Show/Season 1/Night.Show.S01E02.mkv",
            "identification reports name a file from its library folder down");
        check(MetadataReports.LibraryName(@"C:\Users\Ann\Videos\Shows\Anime\Some Anime\01.mkv", folders) == "Anime/Some Anime/01.mkv",
            "a library folder inside another one names the file from the inner folder");
        check(MetadataReports.LibraryName(@"D:\Movies\Film (2020).mkv", folders) == "Movies/Film (2020).mkv", "a library on a drive's root names the file from the drive down, without the drive");
        check(MetadataReports.LibraryName(@"C:\Users\Ann\Desktop\Film.mkv", folders) == "Film.mkv"
            && MetadataReports.LibraryName(@"C:\Users\Ann\Videos\ShowsExtra\Film.mkv", folders) == "Film.mkv",
            "a file outside every library folder is named by its own name alone");
        check(MetadataReports.LibraryName(@"C:\Users\Ann\Night Show\01.mkv", [@"C:\Users\Ann"]) == "Night Show/01.mkv"
            && MetadataReports.LibraryName(@"E:\Ann\Film.mkv", [@"E:\Ann"], account: "ann") == "Film.mkv",
            "a library folder that is an account's own folder is left out of file names");

        // Sentry keeps ten custom measurements on a transaction and drops the rest, so a run never sets more.
        var run = new MetadataRun("FillMissing", true, "tmdb", TimeSpan.Zero, true, 3, 1, 2, 0, 0, 0, 0, 5,
            new Dictionary<string, int> { ["weak-title"] = 1, ["no-candidates"] = 1, ["missing-release-year"] = 1, ["ambiguous-candidates"] = 1 }, []);
        check(run.Measurements.Count <= MetadataRun.MeasurementLimit && run.Measurements.Select(x => x.Name).Distinct().Count() == run.Measurements.Count,
            "a metadata run sets no more measurements than Sentry keeps, whatever its unmatched reasons");

        // The unmatched list counts titles the way the Unmatched category does and gives the last lookup's reason for each.
        MetadataReports.UseFolders([@"C:\Users\Ann\Videos\Shows"]);
        try
        {
            MediaItem Episode(string id, string show, int episode, int? metadataId = null) =>
                new(id, $@"C:\Users\Ann\Videos\Shows\{show}\{show} S01E0{episode}.mkv", show, "Show", Series: show, Season: 1, Episode: episode, MetadataId: metadataId, MetadataProvider: metadataId is null ? null : "tvmaze");
            MediaItem[] items = [Episode("a1", "Known Show", 1, 42), Episode("b1", "Odd Show", 1), Episode("b2", "Odd Show", 2), Episode("c1", "New Show", 1)];
            var issue = new MetadataIssues.Issue("title:" + LibraryIdentity.ShowKey(items[1]), "title", "Odd Show", "weak-title", ["b1"], "", 0);
            var (titles, unmatched) = MetadataReports.Library(items, [issue]);
            check(titles == 3 && unmatched.Length == 2
                && unmatched.Any(x => x is { File: "Shows/Odd Show/Odd Show S01E01.mkv", Files: 2, Reason: "weak-title" })
                && unmatched.Any(x => x is { File: "Shows/New Show/New Show S01E01.mkv", Files: 1, Reason: "not-looked-up" }),
                $"the unmatched list names each unmatched title's first file, its file count and its last reason ({string.Join("; ", unmatched.Select(x => $"{x.File} {x.Files} {x.Reason}"))})");
        }
        finally { MetadataReports.UseFolders(Array.Empty<string>()); }

        // A provider failure during a title's lookup reaches the report hook with that file's library name, and the local
        // diagnostics log does not gain the path.
        var reported = new List<MetadataFailure>();
        MetadataReports.Failures = reported.Add;
        try
        {
            MetadataReports.UseFolders(folders);
            var item = new MediaItem("report", @"C:\Users\Ann\Videos\Shows\Night Show\Season 1\Night.Show.S01E02.mkv", "Night Show", "Show", Series: "Night Show", Season: 1, Episode: 2);
            using (MetadataDiagnostics.BeginItem("title", item))
                MetadataDiagnostics.Failure("title", item.DisplayTitle, "tmdb", new HttpRequestException("Service unavailable", null, System.Net.HttpStatusCode.ServiceUnavailable));
            MetadataDiagnostics.Failure("wanted", "Some title", "tmdb", new TimeoutException());
        }
        finally { MetadataReports.Failures = null; MetadataReports.UseFolders(Array.Empty<string>()); }
        check(reported.Count == 2 && reported[0] is { Stage: "title", Provider: "tmdb", File: "Shows/Night Show/Season 1/Night.Show.S01E02.mkv" } && reported[1].File is null,
            "a provider failure is reported with its file's library name, or none outside a title's lookup");
    }
}
