namespace BlackPearl.Core;

// How sharp a video is, from the picture size its file header records: the label its details show, such as "4K" or "Full HD".
// Sizes are read once per file in the background, like runtimes (LibraryRuntimes), and read again when the file is replaced.
public static class LibraryPictures
{
    public static bool NeedsPicture(MediaItem item) => !item.IsMusic && !item.IsVirtual && item.Available && item.FileVideoHeight is null;

    // A picture is classed by the height a 16:9 frame of its width would have, so a widescreen film cropped to 1920 × 800 is
    // still Full HD and a 1440 × 1080 one too. The thresholds sit a little under each standard size for slightly cropped
    // encodes; an ultrawide 3440 × 1440 stays QHD.
    public static string? Label(int? width, int? height)
    {
        if (width is not > 0 || height is not > 0) return null;
        var size = Math.Max(height.Value, (int)Math.Ceiling(width.Value * 9 / 16.0));
        return size switch { >= 4000 => "8K", >= 2000 => "4K", >= 1300 => "QHD", >= 1000 => "Full HD", >= 700 => "HD", _ => "SD" };
    }
    public static string? Label(MediaItem item) => item.IsMusic || item.IsVirtual ? null : Label(item.FileVideoWidth, item.FileVideoHeight);

    public static int Backfill(LibraryStore store, IEnumerable<MediaItem> items, CancellationToken ct = default)
    {
        var filled = 0;
        foreach (var item in items.Where(NeedsPicture))
        {
            ct.ThrowIfCancellationRequested();
            // A file that can't be read now (drive offline, file locked) is tried again on the next pass.
            if (!VideoFileDetails.ReadPicture(item.Path, out var picture, ct)) continue;
            if (store.FillFilePicture(item.Id, picture?.Width ?? 0, picture?.Height ?? 0) is not null && picture is not null) filled++;
        }
        return filled;
    }
}
