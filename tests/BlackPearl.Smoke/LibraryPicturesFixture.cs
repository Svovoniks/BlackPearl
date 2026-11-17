using BlackPearl.Core;

// The "4K" and "Full HD" labels on a video's details: picture sizes read from file headers, stored once, read again for a replaced file.
static class LibraryPicturesFixture
{
    public static async Task Run(string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        var labels = new (int? Width, int? Height, string? Label)[]
        {
            (7680, 4320, "8K"), (3840, 2160, "4K"), (3840, 1600, "4K"), (4096, 1716, "4K"), (2560, 1440, "QHD"), (3440, 1440, "QHD"),
            (1920, 1080, "Full HD"), (1920, 800, "Full HD"), (1440, 1080, "Full HD"), (1916, 1036, "Full HD"), (1280, 720, "HD"), (960, 720, "HD"),
            (1280, 536, "HD"), (720, 576, "SD"), (720, 400, "SD"), (0, 0, null), (null, null, null),
        };
        var wrong = labels.Where(x => LibraryPictures.Label(x.Width, x.Height) != x.Label).Select(x => $"{x.Width}x{x.Height}").ToArray();
        check(wrong.Length == 0, "picture sizes get the label of the 16:9 frame they fill, so cropped widescreen films keep theirs" +
            (wrong.Length > 0 ? ": " + string.Join(", ", wrong) : ""));

        var film = Path.Combine(directory, "Film.avi"); await File.WriteAllBytesAsync(film, VideoFileDetailsFixture.Avi(1920, 800));
        var silent = Path.Combine(directory, "Silent.avi"); await File.WriteAllBytesAsync(silent, new byte[256]);
        var read = VideoFileDetails.ReadPicture(film, out var picture);
        var readSilent = VideoFileDetails.ReadPicture(silent, out var none);
        var readMissing = VideoFileDetails.ReadPicture(Path.Combine(directory, "unplugged", "Gone.mkv"), out _);
        bool readBusy;
        using (new FileStream(film, FileMode.Open, FileAccess.Read, FileShare.None)) readBusy = VideoFileDetails.ReadPicture(film, out _);
        check(read && picture == (1920, 800) && readSilent && none is null && !readMissing && !readBusy,
            "a file's picture size comes from its header; a file without one is read as having none, and one that can't be opened is tried later");

        var store = new LibraryStore(Path.Combine(directory, "db"));
        var busyPath = Path.Combine(directory, "Busy.avi"); await File.WriteAllBytesAsync(busyPath, VideoFileDetailsFixture.Avi(3840, 2160));
        MediaItem movie = new("film", film, "Film", "Movie"), broken = new("silent", silent, "Silent", "Movie"), busy = new("busy", busyPath, "Busy", "Movie"),
            placeholder = new("missing", Path.Combine(directory, "Show S01E09.mkv"), "Show", "Show", Series: "Show", Season: 1, Episode: 9, IsVirtual: true);
        foreach (var item in new[] { movie, broken, busy, placeholder }) store.Save(item);
        int filled;
        using (new FileStream(busyPath, FileMode.Open, FileAccess.Read, FileShare.None)) filled = LibraryPictures.Backfill(store, store.All());
        var stored = store.All().ToDictionary(item => item.Id);
        check(filled == 1 && stored["film"] is { FileVideoWidth: 1920, FileVideoHeight: 800 } && stored["silent"].FileVideoHeight == 0 &&
            stored["busy"].FileVideoHeight is null && stored["missing"].FileVideoHeight is null && LibraryPictures.Label(stored["film"]) == "Full HD" && LibraryPictures.Label(stored["silent"]) is null,
            "the background pass stores each readable file's picture size, marks a file without one, and skips placeholders and locked files");
        filled = LibraryPictures.Backfill(store, store.All());
        check(filled == 1 && store.All().Single(item => item.Id == "busy").FileVideoHeight == 2160 && store.FillFilePicture("film", 1, 1) is null,
            "a locked file is read on the next pass, and a file already read is not read again");

        var scanRoot = Path.Combine(directory, "scan", "Movies"); Directory.CreateDirectory(scanRoot);
        var scanned = Path.Combine(scanRoot, "Scanned Film (2020).avi"); await File.WriteAllBytesAsync(scanned, VideoFileDetailsFixture.Avi(1280, 720));
        var scanStore = new LibraryStore(Path.Combine(directory, "scan-db")); var folders = new[] { new LibraryFolder(scanRoot, "Movie") };
        await scanStore.ScanAsync(folders); LibraryPictures.Backfill(scanStore, scanStore.All());
        await scanStore.ScanAsync(folders); var unchanged = scanStore.All().Single();
        await File.WriteAllBytesAsync(scanned, VideoFileDetailsFixture.Avi(3840, 1600).Concat(new byte[32]).ToArray()); await scanStore.ScanAsync(folders);
        var replaced = scanStore.All().Single(); LibraryPictures.Backfill(scanStore, scanStore.All());
        check(unchanged.FileVideoHeight == 720 && replaced.FileVideoHeight is null && LibraryPictures.Label(scanStore.All().Single()) == "4K",
            "a rescan keeps a file's picture size until the file is replaced, then reads the new one");
    }
}
