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
