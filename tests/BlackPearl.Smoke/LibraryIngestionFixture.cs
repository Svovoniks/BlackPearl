using BlackPearl.Core;
using System.Net;

static class LibraryIngestionFixture
{
    // A library folder such as the Desktop can hold a code project: TypeScript shares ".ts" with transport streams, and the
    // project's node_modules folder holds thousands of such files.
    private static async Task CodeFiles(string directory, Action<bool, string> check)
    {
        var root = Path.Combine(directory, "Desktop"); var project = Path.Combine(root, "App"); var packages = Path.Combine(project, "node_modules", "pkg");
        Directory.CreateDirectory(Path.Combine(project, "src")); Directory.CreateDirectory(packages);
        static byte[] Stream(int packet, int prefix)
        {
            var bytes = new byte[packet * 6];
            for (var offset = prefix; offset < bytes.Length; offset += packet) bytes[offset] = 0x47;
            return bytes;
        }
        var code = System.Text.Encoding.UTF8.GetBytes("export const settings = { theme: 'dark' };\n" + new string('/', 300));
        var recording = Path.Combine(root, "Recording 2024.ts"); var timestamped = Path.Combine(root, "Broadcast 2023.ts"); var cut = Path.Combine(root, "Cut Recording 2022.ts");
        await File.WriteAllBytesAsync(cut, Stream(188, 100));
        var source = Path.Combine(project, "src", "app_model.ts"); var listedSource = Path.Combine(project, "src", "android.ts");
        var declaration = Path.Combine(packages, "index.d.ts"); var packaged = Path.Combine(packages, "sample.mp4");
        // A download's start stays zeros until its first piece arrives, and finishing it renames nothing.
        var downloading = Path.Combine(root, "Downloading 2025.ts"); var empty = Path.Combine(root, "Starting 2025.ts");
        await File.WriteAllBytesAsync(recording, Stream(188, 0)); await File.WriteAllBytesAsync(timestamped, Stream(192, 4));
        await File.WriteAllBytesAsync(source, code); await File.WriteAllBytesAsync(listedSource, code);
        await File.WriteAllBytesAsync(declaration, code); await File.WriteAllBytesAsync(packaged, [0]);
        await File.WriteAllBytesAsync(downloading, new byte[4096]); await File.WriteAllBytesAsync(empty, []);
        await File.WriteAllBytesAsync(Path.Combine(root, "Example Film 2021.mkv"), [0]);
        var store = new LibraryStore(Path.Combine(directory, "db"));
        // What an earlier version listed: TypeScript files as films, one of them watched. Reading their picture found none.
        store.Save(new("old-source", listedSource, "android", "Movie", FileSize: code.Length, FileVideoWidth: 0, FileVideoHeight: 0));
        store.Setting("position:old-source", "1000");
        store.Save(new("old-package", declaration, "index d", "Movie", FileSize: code.Length));
        await store.ScanAsync([new(root, "Mixed")]);
        var paths = store.All().Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        check(paths.Contains(recording) && paths.Contains(timestamped) && paths.Contains(cut) && paths.Contains(Path.Combine(root, "Example Film 2021.mkv")),
            "transport streams with 188- and 192-byte packets, or starting partway through a packet, are listed beside other videos");
        check(!paths.Contains(source) && !paths.Contains(listedSource), "TypeScript files named .ts are not listed as videos");
        check(!paths.Contains(declaration) && !paths.Contains(packaged), "nothing inside a node_modules folder is listed");
        check(store.All().All(x => x.Id is not ("old-source" or "old-package")) && store.Setting("position:old-source") is null,
            "TypeScript files listed by an earlier version leave the library with their progress");
        check(paths.Contains(downloading) && paths.Contains(empty) && store.ByPath(downloading)?.FileWriteTime is null,
            "a .ts download whose start is still empty is listed, and read again on the next scan");
        // A file found to be a transport stream isn't read again until it changes: this one now holds text but kept its
        // size and write time, while the other changed.
        var recordedAt = File.GetLastWriteTimeUtc(recording);
        await File.WriteAllBytesAsync(recording, new byte[Stream(188, 0).Length].Select(_ => (byte)'x').ToArray()); File.SetLastWriteTimeUtc(recording, recordedAt);
        var replaced = code.Concat(new byte[Stream(192, 4).Length - code.Length]).ToArray(); await File.WriteAllBytesAsync(timestamped, replaced);
        File.SetLastWriteTimeUtc(timestamped, File.GetLastWriteTimeUtc(timestamped).AddMinutes(1));
        await File.WriteAllBytesAsync(downloading, Stream(188, 0));
        await store.ScanAsync([new(root, "Mixed")]);
        paths = store.All().Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        check(paths.Contains(recording) && store.ByPath(recording)?.FileWriteTime == recordedAt.Ticks, "an unchanged .ts file already found to be a transport stream is not read again");
        check(!paths.Contains(timestamped), "a .ts file is read again once it changes");
        check(paths.Contains(downloading) && store.ByPath(downloading)?.FileWriteTime is not null, "a finished .ts download is found to be a transport stream");
        check(!FileNames.AffectsLibrary(Path.Combine(packages, "other.ts"), false) && !FileNames.AffectsLibrary(Path.Combine(packages, "other.ts"), true) &&
            FileNames.AffectsLibrary(Path.Combine(root, "New Recording.ts"), false), "changes inside node_modules don't start a library scan");
        check(!FileNames.AffectsLibrary(source, false) && FileNames.AffectsLibrary(cut, false) && FileNames.AffectsLibrary(downloading, false) && FileNames.AffectsLibrary(source, true),
            "saving TypeScript source doesn't start a library scan, while a transport stream still does");
    }
    public static async Task Run(string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        await CodeFiles(Path.Combine(directory, "code-files"), check);
        var gto = FileNames.Parse(Path.Combine(directory, "[Kanjouteki] GTO - Great Teacher Onizuka - E01v2 [1080p AI Upscale][x265].mkv"));
        check(gto.Series == "GTO - Great Teacher Onizuka" && gto.Episode == 1 && gto.Kind == "Anime", "versioned E01v2 release groups into its anime show");
        var outlaw = FileNames.Parse(Path.Combine(directory, "Outlaw Star - 01 (BDRip 1440x1080p x265 HEVC AC3, FLACx2 2.0x3)(Dual Audio)[sxales].mkv"));
        check(outlaw.Series == "Outlaw Star" && outlaw.Season == 1 && outlaw.Episode == 1, "audio channel layout is not parsed as an episode");
        check(FileNames.Parse(Path.Combine(directory, "Outlaw Star - 26 END (BDRip 1080p).mkv")).Episode == 26, "last-episode END suffix preserves grouping");
        check(FileNames.Parse(Path.Combine(directory, "[Subsplease] Example - 11V2 (1080P).mp4")).Episode == 11, "uppercase release version recognized");
        check(FileNames.Parse(Path.Combine(directory, "Example S01E02v2.mkv")).Episode == 2, "versioned season/episode marker recognized");
        check(FileNames.Parse(Path.Combine(directory, "Movie 2024 (1080p x265).mkv")).Series is null, "ordinary movie remains outside show groups");
        var pink = FileNames.Parse(Path.Combine(directory, "Anime", "The Pink Panther", "001 The Pink Phink (Dec 18, 1964).mp4"));
        check(pink.Series == "The Pink Panther" && pink.Episode == 1 && pink.Year is null, "numbered cartoons use folder show name rather than episode date");
        var onePiece = FileNames.Parse(Path.Combine(directory, "Anime", "One Piece", "One Piece Season 19 (Whole Cake Island) EP 783-In Progress", "[Kayoanime] - 0783.mkv"));
        check(onePiece.Series == "One Piece" && onePiece.Season == 19 && onePiece.Episode == 783, "four-digit absolute anime episode uses named season folder");
        var bean = FileNames.Parse(Path.Combine(directory, "Shows", "Mr. Bean", "Mr. Bean 01.mp4"));
        check(bean.Series == "Mr Bean" && bean.Episode == 1, "plain numbered show suffix groups episodes");
        var russian = FileNames.Parse(Path.Combine(directory, "Shows", "Подозрительная Сова", "02 сезон", "Подозрительная Сова - 02 сезон 03 серия.avi"));
        check(russian.Series == "Подозрительная Сова" && russian.Season == 2 && russian.Episode == 3, "Russian season and episode labels recognized");
        var caseVariant = outlaw with { Id = "case-variant", Series = "OUTLAW STAR", Episode = 2 };
        check(LibraryCatalog.Browse([outlaw, caseVariant]).Count == 1
            && LibraryCatalog.Browse([outlaw, caseVariant], "Outlaw Star", outlaw.Kind, 1).Count == 2,
            "case differences keep episodes in one browsable show");

        var media = Path.Combine(directory, "media"); Directory.CreateDirectory(media);
        var firstPath = Path.Combine(media, "Example S01E01.mkv"); var secondPath = Path.Combine(media, "Example S01E02.mkv");
        await File.WriteAllBytesAsync(firstPath, [0]); await File.WriteAllBytesAsync(secondPath, [0]);
        var store = new LibraryStore(Path.Combine(directory, "scan-db"));
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var notified = 0;
        void OnSave(MediaItem item)
        {
            if (Interlocked.Increment(ref notified) != 1) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Scan fixture not released");
        }
        store.MediaSaved += OnSave;
        var scan = store.ScanAsync([new(media)]);
        try { check(entered.Wait(TimeSpan.FromSeconds(10)) && !scan.IsCompleted && store.All().Count == 1,
            "scan publishes a committed item before the remaining files finish"); }
        finally { release.Set(); }
        await scan; store.MediaSaved -= OnSave;
        check(notified == 2 && LibraryCatalog.Browse(store.All()).Count == 1, "incremental scan groups both episodes into one show");

        // A moved or renamed file keeps its record, playback progress and metadata.
        var moveRoot = Path.Combine(directory, "move-media"); var oldFolder = Path.Combine(moveRoot, "Moved Show");
        Directory.CreateDirectory(oldFolder);
        var oldPath = Path.Combine(oldFolder, "Moved Show S01E01.mkv"); await File.WriteAllBytesAsync(oldPath, [1, 2, 3]);
        var moveStore = new LibraryStore(Path.Combine(directory, "move-db"));
        await moveStore.ScanAsync([new(moveRoot)]);
        var original = moveStore.All().Single();
        moveStore.Save(original with { Overview = "Kept overview" }); moveStore.Setting("position:" + original.Id, "42000");
        var newFolder = Path.Combine(moveRoot, "Shared", "Moved Show"); Directory.CreateDirectory(newFolder);
        var newPath = Path.Combine(newFolder, "Moved Show S01E01.mkv"); File.Move(oldPath, newPath);
        await moveStore.ScanAsync([new(moveRoot)]);
        var relocated = moveStore.All();
        check(relocated.Count == 1 && relocated[0].Id == original.Id && relocated[0].Path == newPath && relocated[0].Available &&
            relocated[0].Overview == "Kept overview" && moveStore.Setting("position:" + original.Id) == "42000",
            "moved file keeps its record, progress and metadata instead of leaving an unavailable copy");
        await moveStore.ScanAsync([new(moveRoot)]);
        check(moveStore.All() is [{ Available: true } again] && again.Id == original.Id && again.Path == newPath, "rescanning a moved file keeps its original record");
        var renamedPath = Path.Combine(newFolder, "Moved Show - S01E01 - Pilot.mkv"); File.Move(newPath, renamedPath);
        await moveStore.ScanAsync([new(moveRoot)]);
        check(moveStore.All() is [{ Available: true } renamed] && renamed.Id == original.Id && renamed.Path == renamedPath,
            "a file renamed within its folder keeps its record when its size is unchanged");
        var twinA = Path.Combine(moveRoot, "A", "Twin.mkv"); var twinB = Path.Combine(moveRoot, "B", "Twin.mkv");
        foreach (var twin in new[] { twinA, twinB }) { Directory.CreateDirectory(Path.GetDirectoryName(twin)!); await File.WriteAllBytesAsync(twin, [4, 5]); }
        await moveStore.ScanAsync([new(moveRoot)]);
        var twins = moveStore.All().Where(item => item.Path.EndsWith("Twin.mkv")).Select(item => item.Id).ToArray();
        var twinC = Path.Combine(moveRoot, "C", "Twin.mkv"); Directory.CreateDirectory(Path.GetDirectoryName(twinC)!);
        File.Move(twinA, twinC); File.Delete(twinB);
        await moveStore.ScanAsync([new(moveRoot)]);
        var afterTwins = moveStore.All().Where(item => item.Path.EndsWith("Twin.mkv")).ToArray();
        check(afterTwins.Length == 3 && afterTwins.Count(item => !item.Available && twins.Contains(item.Id)) == 2 && afterTwins.Single(item => item.Available).Path == twinC,
            "an ambiguous move adds the file as new and leaves both candidates unavailable");

        var poster = Path.Combine(directory, "season.png"); await File.WriteAllBytesAsync(poster, ArtworkFixture.Png);
        var artworkStore = new LibraryStore(Path.Combine(directory, "artwork-db"));
        artworkStore.Save(new("matched", "matched.mkv", "Example Show", "Show", 2020, "Example Show", 1, 1,
            Poster: Path.Combine(directory, "deleted.png"), Matched: true, SeasonPoster: poster, Overview: "Keep my overview",
            MetadataProvider: "tvmaze", MetadataId: 1, MetadataKind: "Show", MetadataType: "tv"));
        check(LibraryCatalog.Browse(artworkStore.All()).Single().Poster == poster, "show card uses season artwork while show poster is missing");
        var posterPublished = false; var textPublished = false;
        artworkStore.MediaSaved += item => { posterPublished |= File.Exists(item.SeriesPoster); textPublished |= item.EpisodeTitle == "Pilot"; };
        using var http = new HttpClient(new ArtworkFixture());
        await AutomaticArtwork.FetchAsync(artworkStore, directory, null, null, CancellationToken.None, http);
        var repaired = artworkStore.All().Single();
        check(File.Exists(repaired.SeriesPoster) && repaired.SeriesPoster != poster, "matched title with only a season image still downloads a show poster");
        check(repaired.Overview == "Keep my overview", "poster repair preserves an existing overview");
        check(posterPublished && textPublished, "poster and episode metadata changes publish individually");

        var failures = new LibraryStore(Path.Combine(directory, "failure-db"));
        foreach (var title in new[] { "A Fail", "B Fail", "C Fail", "Example Show" })
            failures.Save(new(title, title + ".mkv", title, "Show", 2020, title, 1, 1));
        using var failingHttp = new HttpClient(new FirstThreeFailures());
        await AutomaticArtwork.FetchAsync(failures, Path.Combine(directory, "failure-artwork"), null, null, CancellationToken.None, failingHttp);
        check(File.Exists(failures.All().Single(item => item.Id == "Example Show").SeriesPoster),
            "three earlier provider failures do not skip later shows");
    }
    private sealed class FirstThreeFailures : HttpMessageHandler
    {
        private readonly HttpMessageInvoker inner = new(new ArtworkFixture());
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri!.Query.Contains("Fail") ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
                : inner.SendAsync(request, cancellationToken);
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
