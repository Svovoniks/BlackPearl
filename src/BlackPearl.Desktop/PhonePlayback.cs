using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using BlackPearl.Core;

namespace BlackPearl.Desktop;

// What BlackPearl Mobile does with this PC's library beyond the remote (PHONE-REMOTE.md): it browses the library, plays a
// video on the phone, streamed over the phone's own link, reports how far it got so Continue watching and the watched
// marks stay the same on both, and hands the video back to this PC at the same moment.
public partial class MainWindow
{
    private const int PhonePageSize = 40;
    private static readonly string[] PhoneFeatures = ["library", "watch"];

    // One video a phone is watching: its file, or the conversion of it the phone asked for.
    private sealed class PhoneStream(MediaItem item, FileMediaSource file) : IDisposable
    {
        public MediaItem Item { get; } = item;
        public FileMediaSource File { get; } = file;
        public StreamConverter? Converter { get; init; }
        public SegmentStore? Store { get; init; }
        public string? Directory { get; init; }
        public void Dispose() => CloseConversion(Converter, Store, Directory);
    }
    private readonly ConcurrentDictionary<PhoneRemoteSession, PhoneStream> phoneStreams = new();
    // Picture tokens handed out in library pages, and the files they stand for. Phones never see the paths.
    private readonly ConcurrentDictionary<string, string> phoneArtwork = new();
    private readonly ConcurrentDictionary<string, byte[]> phoneArtworkCache = new();

    private void AttachPhonePlayback(PhoneRemoteHost remote)
    {
        remote.Request += (session, type, message) => Dispatcher.BeginInvoke(() => { if (phoneRemote == remote) _ = Guard(() => RunPhoneRequest(remote, session, type, message)); });
        remote.Disconnected += session => { if (phoneStreams.TryRemove(session, out var stream)) stream.Dispose(); };
        remote.ResolveMedia = ResolvePhoneResource;
    }

    private void ClosePhoneStreams()
    {
        foreach (var session in phoneStreams.Keys) if (phoneStreams.TryRemove(session, out var stream)) stream.Dispose();
        phoneArtwork.Clear(); phoneArtworkCache.Clear();
    }

    private static string? Text(JsonElement message, string name) =>
        message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long? Number(JsonElement message, string name) =>
        message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? (long)number : null;
    private static bool Flag(JsonElement message, string name) => message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private async Task RunPhoneRequest(PhoneRemoteHost remote, PhoneRemoteSession session, string type, JsonElement message)
    {
        var request = Number(message, "request") ?? 0;
        void Fail(string code, string text) => remote.SendMessage(session, new { type = "error", code, message = text, request });
        switch (type)
        {
            case "browse": await BrowseForPhone(remote, session, request, message); break;
            case "watch": await WatchOnPhone(remote, session, request, message, Fail); break;
            case "progress": SavePhoneProgress(message); break;
            case "play": await PlayFromPhone(message, Fail); break;
            case "stream-stop": if (phoneStreams.TryRemove(session, out var stream)) stream.Dispose(); break;
        }
    }

    private async Task BrowseForPhone(PhoneRemoteHost remote, PhoneRemoteSession session, long request, JsonElement message)
    {
        var browse = new LibraryBrowseRequest(Text(message, "query") ?? "", (int)(Number(message, "category") ?? 0), Text(message, "parentId"),
            Number(message, "season") is { } season ? (int)season : null, (int)(Number(message, "page") ?? 0), Flag(message, "home"), (int)(Number(message, "seed") ?? 0));
        var source = items.ToArray();
        var playback = library.PlaybackSettings();
        LibraryBrowsePage page;
        try { page = await Task.Run(() => SharedLibrary.Browse(source, browse, PhoneArtworkToken, playback, PhonePageSize)); }
        catch (InvalidDataException ex)
        {
            remote.SendMessage(session, new { type = "error", code = "browse", message = ex.Message, request });
            return;
        }
        if (phoneRemote != remote) return;
        bool Watched(string id) => playback.GetValueOrDefault("watched:" + id) == "true";
        long Position(string id) => long.TryParse(playback.GetValueOrDefault("position:" + id), out var at) ? Math.Max(0, at) : 0;
        var entries = page.Entries.Select(entry =>
        {
            var file = entry.Level is "movie" or "episode" || entry.HomeSection == "continue";
            return new PhoneLibraryEntry(entry.Id, entry.Title, entry.Caption, entry.Level, entry.Season, entry.Episode, entry.Overview, entry.Kind,
                entry.RuntimeMinutes, entry.Year, entry.Episodes, entry.HomeSection, file ? Math.Max(entry.PositionMs, Position(entry.Id)) : 0,
                file && Watched(entry.Id), entry.Thumbnail, file);
        }).ToArray();
        // Each message is at most 64 KiB: summaries shorten first, then the page drops entries from its end.
        for (var limit = 400; ; limit /= 2)
        {
            var reply = new { type = "page", request, entries, total = page.Total, page = page.Page, pageSize = PhonePageSize, location = page.Location };
            if (Encoding.UTF8.GetByteCount(Wire.Serialize(reply)) < 60000) { remote.SendMessage(session, reply); return; }
            entries = limit > 0 ? entries.Select(entry => entry with { Overview = entry.Overview[..Math.Min(entry.Overview.Length, limit)] }).ToArray() : entries[..^1];
        }
    }

    private record PhoneLibraryEntry(string Id, string Title, string Caption, string Level, int? Season, int? Episode, string Overview, string Kind,
        int? RuntimeMinutes, int? Year, int Episodes, string? HomeSection, long PositionMs, bool Watched, string? Artwork, bool Playable);

    // The library page names each picture by a token rather than its path.
    private string? PhoneArtworkToken(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..24].ToLowerInvariant();
        if (phoneArtwork.Count > 20000) phoneArtwork.Clear();
        phoneArtwork[token] = path;
        return token;
    }

    // Reads for the phone's media channel, on threads other than the UI's: "file/<id>" for the video it is watching,
    // "stream/<stream id>/<piece>" for its conversion, and "art/<token>/<width>" for pictures.
    private async Task<PhoneResource?> ResolvePhoneResource(PhoneRemoteSession session, string resource, CancellationToken ct)
    {
        var parts = resource.Split('/');
        switch (parts)
        {
            case ["file", var id] when phoneStreams.TryGetValue(session, out var stream) && stream.Item.Id == id:
                return new(stream.File.Media.Length, stream.File.ReadAsync);
            case ["stream", var id, var piece] when phoneStreams.TryGetValue(session, out var stream) && stream.Converter is { } converter && converter.Stream.Id == id &&
                int.TryParse(piece, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < converter.Stream.Segments:
                return await converter.PhonePieceAsync(index, ct);
            case ["art", var token, var size] when phoneArtwork.TryGetValue(token, out var path) && int.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out var width):
                width = Math.Clamp(width, 64, 1280);
                var key = token + "/" + width;
                if (phoneArtworkCache.TryGetValue(key, out var cached)) return PhoneResource.FromBytes(cached);
                var bytes = await Task.Run(() => PhonePicture(path, width), ct);
                if (bytes is null) return null;
                if (phoneArtworkCache.Count > 300) phoneArtworkCache.Clear();
                phoneArtworkCache[key] = bytes;
                return PhoneResource.FromBytes(bytes);
            default: return null;
        }
    }

    // The picture at most width pixels wide, as JPEG, never enlarged.
    private static byte[]? PhonePicture(string path, int width)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var file = File.OpenRead(path);
            var frame = System.Windows.Media.Imaging.BitmapDecoder.Create(file, System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation, System.Windows.Media.Imaging.BitmapCacheOption.None).Frames[0];
            file.Position = 0;
            var bitmap = new System.Windows.Media.Imaging.BitmapImage(); bitmap.BeginInit();
            bitmap.DecodePixelWidth = Math.Min(width, frame.PixelWidth); bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bitmap.StreamSource = file; bitmap.EndInit(); bitmap.Freeze();
            var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var output = new MemoryStream(); encoder.Save(output);
            return output.ToArray();
        }
        catch (Exception) { return null; }
    }

    // The phone asks to play a video. takeOver: the video is the one playing here, and the phone continues it, so this
    // PC saves where it was and stops. The phone hears back where to start and how to read the video: the file itself,
    // or a conversion of it at a lower bitrate for a slow link.
    private async Task WatchOnPhone(PhoneRemoteHost remote, PhoneRemoteSession session, long request, JsonElement message, Action<string, string> fail)
    {
        var id = Text(message, "id");
        var item = items.FirstOrDefault(x => x.Id == id && x.Available && !x.IsVirtual);
        if (item is null || !File.Exists(item.Path)) { fail("gone", "That video isn't available on the PC. Its drive may be disconnected."); return; }
        if (Flag(message, "takeOver") && room is null && playingItem?.Id == item.Id && HasMediaPlayback && player is not null)
        {
            SavePlaybackPosition();
            if (nativeStopCount == 0) { ready = false; await loading.WaitAsync(); try { await StopNativePlayback(); } finally { loading.Release(); } }
        }
        if (phoneStreams.TryRemove(session, out var previous)) previous.Dispose();
        var resume = !WasWatched(item.Id) && !item.IsMusic && long.TryParse(library.Setting("position:" + item.Id), out var saved) ? saved : 0;
        var file = new FileMediaSource(item.Path, PlaybackTitle(item), item.Id);
        var wanted = Text(message, "quality") ?? "auto";
        var home = Text(message, "network") == "home";
        var qualities = new List<string> { StreamQuality.OriginalId };
        PhoneStream stream = new(item, file);
        string? note = null;
        long durationMs = (long)((item.DurationSeconds ?? 0) * 1000);
        if (wanted != StreamQuality.OriginalId && !(wanted == "auto" && home) && !item.IsMusic)
        {
            var (converted, why) = await ConvertForPhone(item, file, wanted == "auto" ? "720p" : wanted, qualities);
            if (phoneRemote != remote) { converted?.Dispose(); return; }
            if (converted is not null) { stream = converted; durationMs = converted.Converter!.Stream.DurationMs; }
            else note = why;
        }
        else if (!item.IsMusic && Ffmpeg.Find() is not null) qualities.AddRange(StreamQuality.Converted.Select(q => q.Id));
        phoneStreams[session] = stream;
        var title = PlaybackTitle(item);
        string? detail = item.Series is not null
            ? string.Join(" · ", new[] { item.Season is { } season ? $"Season {season}" : null, item.EpisodeDisplayTitle }.OfType<string>())
            : item.Year?.ToString(CultureInfo.InvariantCulture);
        remote.SendMessage(session, stream.Converter is { } converter
            ? new { type = "stream", request, id = item.Id, title, detail, positionMs = resume, durationMs, kind = "hls", streamId = converter.Stream.Id,
                segments = converter.Stream.Segments, segmentMs = converter.Stream.SegmentMs, quality = converter.Stream.Quality, qualities, note }
            : (object)new { type = "stream", request, id = item.Id, title, detail, positionMs = resume, durationMs, kind = "file", resource = "file/" + item.Id,
                length = file.Media.Length, extension = file.Media.Extension, quality = StreamQuality.OriginalId, qualities, note });
    }

    // A conversion of the video at the quality asked for, or why there is none: no converter, a file that can't be read,
    // or a file already smaller than the conversion would be.
    private async Task<(PhoneStream? Stream, string? Why)> ConvertForPhone(MediaItem item, FileMediaSource file, string qualityId, List<string> qualities)
    {
        if (Ffmpeg.Find() is not { } ffmpeg) return (null, "This copy of BlackPearl has no converter, so the phone plays the file as it is.");
        VideoProbe? probe;
        try { probe = await VideoProbe.ReadAsync(ffmpeg, item.Path, lifetime.Token); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { probe = null; }
        if (probe is null) return (null, "This file can't be converted, so the phone plays it as it is.");
        qualities.AddRange(StreamQuality.For(probe.ClassHeight).Select(q => q.Id));
        var quality = StreamQuality.Find(qualityId) is { } found && qualities.Contains(found.Id) ? found : StreamQuality.For(probe.ClassHeight).Last();
        var plan = StreamQuality.Plan(probe, file.Media.Length, quality, false);
        if (!plan.Smaller) return (null, $"This file is already smaller than {quality.Name} would be, so the phone plays it as it is.");
        var directory = Path.Combine(StreamCacheDirectory, "phone-" + Guid.NewGuid().ToString("N"));
        var segments = (int)((probe.DurationMs + StreamConverter.SegmentMs - 1) / StreamConverter.SegmentMs);
        var store = SegmentStore.TryCreate(directory, segments);
        if (store is null) return (null, "There is no room on the PC's disk to convert the video, so the phone plays the file as it is.");
        var stream = new RoomStream(Guid.NewGuid().ToString("N"), item.Id, plan.Quality.Id, plan.Height, plan.VideoKbps, probe.DurationMs, StreamConverter.SegmentMs, segments);
        var converter = new StreamConverter(ffmpeg, new StreamInput(item.Path, probe, null, null), stream, store);
        PlaybackDiagnostics.Record("phone-stream", new { quality = plan.Quality.Id, height = plan.Height, kbps = plan.VideoKbps, segments });
        return (new PhoneStream(item, file) { Converter = converter, Store = store, Directory = directory }, null);
    }

    // How far the phone got. Saved as this PC saves its own playback, so Continue watching and the watched marks match.
    // A video playing here right now keeps its own position.
    private void SavePhoneProgress(JsonElement message)
    {
        var id = Text(message, "id");
        if (items.FirstOrDefault(x => x.Id == id) is not { IsMusic: false } item || Number(message, "positionMs") is not { } position) return;
        if (room is null && playingItem?.Id == id && HasMediaPlayback && WantsMediaPlayback) return;
        var duration = Number(message, "durationMs") ?? 0;
        var ended = Flag(message, "ended") || duration > 0 && position >= duration - 2000;
        var saved = new List<KeyValuePair<string, string>> { KeyValuePair.Create("position:" + item.Id, ended || WasWatched(item.Id) ? "0" : Math.Max(0, position).ToString(CultureInfo.InvariantCulture)) };
        if (position >= 10000 || ended) saved.Add(KeyValuePair.Create("played:" + item.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)));
        library.SetSettings(saved);
        if (ended && !WasWatched(item.Id)) SetWatched(item, true);
        else
        {
            foreach (var card in libraryCards.Where(x => x.Card.Media.Id == item.Id)) UpdateCardPlayback(card);
            // Home moves the title to the front of Continue watching once the phone stops or pauses.
            if (Flag(message, "final") && currentPage == "Home") RefreshHome();
        }
    }

    // Continue on the PC: plays the video here from where the phone was.
    private async Task PlayFromPhone(JsonElement message, Action<string, string> fail)
    {
        if (!PhonesCanStart) { fail("not-allowed", "This PC doesn't let phones start videos. Turn it on in Settings › Profile on the PC."); return; }
        if (room?.IsConnected == true) { fail("in-room", "The PC is in a room. Leave the room on the PC to play this there."); return; }
        var id = Text(message, "id");
        var item = items.FirstOrDefault(x => x.Id == id && x.Available && !x.IsVirtual);
        if (item is null || !File.Exists(item.Path)) { fail("gone", "That video isn't available on the PC. Its drive may be disconnected."); return; }
        if (Number(message, "positionMs") is { } position && !item.IsMusic)
        {
            if (position >= 10000 && WasWatched(item.Id)) SetWatched(item, false);
            library.Setting("position:" + item.Id, Math.Max(0, position).ToString(CultureInfo.InvariantCulture));
        }
        queuePosition = -1;
        await PlayLocalItem(item);
        PublishQueue();
    }
}
