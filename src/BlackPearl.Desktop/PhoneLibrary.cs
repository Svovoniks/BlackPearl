using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlackPearl.Core;

namespace BlackPearl.Desktop;

// What BlackPearl Mobile does with this PC's library beyond the remote (PHONE-REMOTE.md): it browses the library, plays a
// video on the phone, streamed over the phone's own link, reports how far it got so Continue watching and the watched
// marks stay the same on both, and hands the video back to this PC at the same moment.
//
// The app's window serves this while it is open (PhonePlayback.cs), and the background phone host while the app is
// closed (PhoneHost.cs). Neither needs the other: whatever only the window can do comes in through the hooks below.
// Requests run on the owner's dispatcher thread; media reads on others.
internal sealed class PhoneLibrary(LibraryStore library, Func<PhoneRemoteHost?> current, Action<Action> post, CancellationToken lifetime)
{
    public const int PageSize = 40;
    public static readonly string[] Features = ["library", "watch", "queues", "unwatched", "mark-watched", "hide-continue"];
    public static string StreamCacheDirectory => Path.Combine(Path.GetTempPath(), "BlackPearl", "stream-cache");

    // The library's videos, read on the dispatcher thread.
    public required Func<IReadOnlyList<MediaItem>> Items { get; init; }
    public required Func<string, bool> WasWatched { get; init; }
    public required Action<MediaItem, bool> SetWatched { get; init; }
    // Marks a title watched or unwatched as the PC's own check mark does: the item, its level ("movie", "episode",
    // "series" or "season") and, for a season, its number.
    public required Action<MediaItem, string, int?, bool> MarkWatched { get; init; }
    // Continue on the PC: plays the video there from where the phone was. False when it was refused (and answered).
    public required Func<JsonElement, Action<string, string>, Task<bool>> Play { get; init; }
    // The window only: stops its own playback of the video the phone takes over, and says where it was. Null when the
    // PC isn't playing it.
    public Func<MediaItem, Task<(long At, bool Rewatch)?>>? TakeOver { get; init; }
    // The window only: true while it plays this video itself, which then keeps its own position.
    public Func<string, bool>? PlaysHere { get; init; }
    // The window only: shows progress the phone saved. The flag is true for the last report of a viewing.
    public Action<MediaItem, PhoneProgress, bool>? ProgressSaved { get; init; }
    // The window only: a phone took a title out of Continue watching, or put it back; Home shows it.
    public Action? ContinueChanged { get; init; }
    // The window only: completes once its library has loaded. Until then Items is empty, and a phone that connected
    // early got an empty Home it kept (it has no reason to ask again), so requests wait for it.
    public Task? Ready { get; init; }
    // Settable for checks, which can't wait 15 seconds for each case.
    internal TimeSpan ReadyWait { get; set; } = TimeSpan.FromSeconds(15);

    // One video a phone is watching: its file, or the conversion of it the phone asked for.
    private sealed class PhoneStream(MediaItem item, FileMediaSource file) : IDisposable
    {
        public MediaItem Item { get; } = item;
        public FileMediaSource File { get; } = file;
        public StreamConverter? Converter { get; init; }
        public SegmentStore? Store { get; init; }
        public string? Directory { get; init; }
        // A conversion's choice of sound: the file's audio tracks, and the one the conversion carries (null: none).
        public ProbedAudio[] AudioTracks { get; init; } = [];
        public int? Audio { get; init; }
        // Its subtitles: the file's tracks, which of them are drawn into the picture rather than sent as files
        // (ProbedSubtitle.BurnsFor), and the one to show (null: none), drawn in when Burns says so.
        public ProbedSubtitle[] SubtitleTracks { get; init; } = [];
        public bool[] Burns { get; init; } = [];
        public int? Subtitle { get; init; }
        // The video was watched when the phone started it, and the rewatch hasn't reached its end yet (see SaveProgress).
        public bool Rewatch { get; set; }
        public void Dispose() => CloseConversion(Converter, Store, Directory);
    }
    // By phone id: a phone whose link drops and comes back keeps reading the same stream.
    private readonly ConcurrentDictionary<string, PhoneStream> streams = new();
    // Picture tokens handed out in library pages, and the files they stand for. Phones never see the paths.
    private readonly ConcurrentDictionary<string, string> artwork = new();
    private readonly ConcurrentDictionary<string, byte[]> artworkCache = new();

    public void Attach(PhoneRemoteHost remote)
    {
        remote.Request += (session, type, message) => post(() => { if (current() == remote) _ = Run(remote, session, type, message); });
        remote.Disconnected += session => ExpireStream(session.PhoneId);
        remote.ResolveMedia = Resolve;
    }

    // Whether a phone is watching a video now.
    public bool Streaming => !streams.IsEmpty;

    public static void CloseConversion(StreamConverter? converter, SegmentStore? store, string? directory)
    {
        if (converter is null && store is null && directory is null) return;
        converter?.Dispose();
        // The converted video and its subtitle files can be gigabytes: they go on another thread.
        _ = Task.Run(() =>
        {
            store?.Dispose();
            try { if (directory is not null && System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        });
    }

    // A phone that loses its link usually reconnects within moments, mid-video, so its stream waits two minutes for it.
    // Each drop starts the wait over: an earlier drop's wait ends without effect once the phone came back in between. A
    // wait ends only the stream it began with, so one the phone started since, on this link or a restarted one, stays.
    private readonly ConcurrentDictionary<string, int> drops = new();
    private void ExpireStream(string phoneId)
    {
        var drop = drops.AddOrUpdate(phoneId, 1, (_, count) => count + 1);
        if (!streams.TryGetValue(phoneId, out var waiting)) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(StreamGrace);
            if (drops.GetValueOrDefault(phoneId) != drop || current() is not { } remote || remote.Connected.Any(s => s.PhoneId == phoneId)) return;
            if (!streams.TryRemove(KeyValuePair.Create(phoneId, waiting))) return;
            expired[phoneId] = (waiting.Item.Id, waiting.Rewatch, DateTime.UtcNow);
            waiting.Dispose();
        });
    }
    // The video of each phone's stream that ended with its link, whether it was a rewatch, and when: a phone back after a
    // long drop asks for the same video again, which goes on with the same viewing. Past ViewingGap it is a new viewing.
    private readonly ConcurrentDictionary<string, (string Id, bool Rewatch, DateTime At)> expired = new();
    private static readonly TimeSpan ViewingGap = TimeSpan.FromHours(6);
    private static readonly TimeSpan StreamGrace = TimeSpan.FromMinutes(2);

    public void CloseStreams()
    {
        foreach (var phone in streams.Keys) if (streams.TryRemove(phone, out var stream)) stream.Dispose();
        expired.Clear(); artwork.Clear(); artworkCache.Clear();
        lock (listingGate) listing = null;
    }

    private static string? Text(JsonElement message, string name) =>
        message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long? Number(JsonElement message, string name) =>
        message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? (long)number : null;
    private static bool Flag(JsonElement message, string name) => message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    // A phone's request never opens a dialog on the PC: whatever goes wrong is answered to the phone, which shows it.
    private async Task Run(PhoneRemoteHost remote, PhoneRemoteSession session, string type, JsonElement message)
    {
        var request = Number(message, "request") ?? 0;
        void Fail(string code, string text) => remote.SendMessage(session, new { type = "error", code, message = text, request });
        try
        {
            // Within the phone's 20 seconds for an answer; a library that takes longer is said to be loading. Only the
            // library's own requests wait: every request reaches here, and others (listen) are answered elsewhere.
            if (Ready is { IsCompleted: false } ready && type is "browse" or "queues" or "watch" or "play" or "progress" or "mark-watched" or "hide-continue")
            {
                await Task.WhenAny(ready, Task.Delay(ReadyWait, lifetime));
                if (current() != remote) return;
                if (!ready.IsCompleted)
                {
                    if (type is not "progress") Fail("loading", "BlackPearl on the PC is still loading its library. Try again in a moment.");
                    return;
                }
            }
            switch (type)
            {
                case "browse" when Text(message, "queue") is not null || Text(message, "episodesOf") is not null: await BrowseList(remote, session, request, message); break;
                case "browse": await Browse(remote, session, request, message); break;
                case "queues": await Queues(remote, session, request); break;
                case "watch": await Watch(remote, session, request, message, Fail); break;
                case "progress": SaveProgress(session, message); break;
                case "mark-watched": MarkWatchedFor(remote, session, request, message, Fail); break;
                case "hide-continue": HideContinueFor(remote, session, request, message, Fail); break;
                case "play":
                    if (!await Play(message, Fail)) break;
                    // The phone hands its video to the PC and stops watching. Its stream ends first: a phone still
                    // reading video gets answers on its link only once the video ahead of them has gone, which on a
                    // slow phone took longer than it waited for this one.
                    if (streams.TryRemove(session.PhoneId, out var handed)) handed.Dispose();
                    remote.SendMessage(session, new { type = "done", request });
                    break;
                case "stream-stop": if (streams.TryRemove(session.PhoneId, out var stream)) stream.Dispose(); break;
            }
        }
        catch (Exception ex) when (!lifetime.IsCancellationRequested)
        {
            PlaybackDiagnostics.Record("phone-request-failed", new { type, error = ex.GetType().Name });
            try
            {
                // Not the exception's own message: those can name the file's path, which phones never see.
                Fail("failed", ex is IOException or UnauthorizedAccessException
                    ? "The PC couldn't open that video. Its drive may be disconnected." : "The PC couldn't do that. Try again.");
            }
            catch (Exception) { }
        }
    }

    private async Task Browse(PhoneRemoteHost remote, PhoneRemoteSession session, long request, JsonElement message)
    {
        var browse = new LibraryBrowseRequest(Text(message, "query") ?? "", (int)(Number(message, "category") ?? 0), Text(message, "parentId"),
            Number(message, "season") is { } season ? (int)season : null, (int)(Number(message, "page") ?? 0), Flag(message, "home"), (int)(Number(message, "seed") ?? 0),
            Flag(message, "unwatched"));
        var source = Items().ToArray();
        Dictionary<string, string> playback;
        LibraryBrowsePage page;
        bool[] watched;
        try
        {
            (playback, page, watched) = await Task.Run(() =>
            {
                var settings = library.PlaybackSettings();
                var page = SharedLibrary.Browse(source, browse, ArtworkToken, settings, PageSize);
                return (settings, page, CardsWatched(source, page.Entries, settings));
            });
        }
        catch (InvalidDataException ex)
        {
            remote.SendMessage(session, new { type = "error", code = "browse", message = ex.Message, request });
            return;
        }
        if (current() != remote) return;
        // A watched episode in Continue watching is being rewatched, and picks up at its place in the rewatch.
        long Position(string id) => LibraryWatchProgress.Resume(key => playback.GetValueOrDefault(key), id, playback.GetValueOrDefault("watched:" + id) == "true");
        var byId = source.ToDictionary(x => x.Id);
        var entries = page.Entries.Select((entry, index) =>
        {
            var file = entry.Level is "movie" or "episode" || entry.HomeSection == "continue";
            // A show's or season's card stands on one of its episodes; it describes the show, not that episode.
            var overview = entry.Level is "series" or "season" && byId.GetValueOrDefault(entry.Id)?.Overview is { Length: > 0 } show ? show[..Math.Min(show.Length, 400)] : entry.Overview;
            return new Entry(entry.Id, entry.Title, entry.Caption, entry.Level, entry.Season, entry.Episode, overview, entry.Kind,
                entry.RuntimeMinutes, entry.Year, entry.Episodes, entry.HomeSection, file ? Math.Max(entry.PositionMs, Position(entry.Id)) : 0,
                watched[index], entry.Thumbnail, file);
        }).ToArray();
        // Each message is at most 64 KiB: summaries shorten first, then the page drops entries from its end.
        for (var limit = 400; ; limit /= 2)
        {
            var reply = new { type = "page", request, entries, total = page.Total, page = page.Page, pageSize = PageSize, location = page.Location };
            if (Encoding.UTF8.GetByteCount(Wire.Serialize(reply)) < 60000) { remote.SendMessage(session, reply); return; }
            entries = limit > 0 ? entries.Select(entry => entry with { Overview = entry.Overview[..Math.Min(entry.Overview.Length, limit)] }).ToArray() : entries[..^1];
        }
    }

    // Whether each card is watched, as the PC's own card is (LibraryWatchProgress.IsWatched): a show or a season once every
    // episode the library has is, an episode or a film also when another copy of it is. Continue watching names an episode
    // not finished yet, as the PC's Home card does: one already watched is being watched again. Off the dispatcher thread:
    // only the shows and films on the page are gathered, in one pass over the library, so a film is compared with its own
    // copies rather than with every film.
    private static bool[] CardsWatched(MediaItem[] source, SharedLibraryEntry[] entries, IReadOnlyDictionary<string, string> playback)
    {
        bool Watched(string id) => playback.GetValueOrDefault("watched:" + id) == "true";
        long Position(string id) => long.TryParse(playback.GetValueOrDefault("position:" + id), out var at) ? Math.Max(0, at) : 0;
        static string Title(MediaItem item) => item.Series is null ? "film:" + LibraryIdentity.TitleKey(item) : "show:" + LibraryIdentity.ShowKey(item);
        var byId = source.ToDictionary(item => item.Id);
        var cards = entries.Select(entry => entry.HomeSection == "continue" ? null : byId.GetValueOrDefault(entry.Id)).ToArray();
        var titles = cards.OfType<MediaItem>().Select(Title).ToHashSet();
        if (titles.Count == 0) return entries.Select(entry => entry.HomeSection != "continue" && Watched(entry.Id)).ToArray();
        var members = source.Where(item => titles.Contains(Title(item))).ToLookup(Title);
        return entries.Select((entry, index) => cards[index] is not { } item ? entry.HomeSection != "continue" && Watched(entry.Id) : LibraryWatchProgress.IsWatched(members[Title(item)],
            new LibraryCard("", "", null, item, entry.Level, entry.Season, Versions: members[Title(item)].Count()), Watched, Position)).ToArray();
    }

    private record Entry(string Id, string Title, string Caption, string Level, int? Season, int? Episode, string Overview, string Kind,
        int? RuntimeMinutes, int? Year, int Episodes, string? HomeSection, long PositionMs, bool Watched, string? Artwork, bool Playable, int? Index = null);

    // The library page names each picture by a token rather than its path.
    private string? ArtworkToken(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..24].ToLowerInvariant();
        // Every picture of a large library fits: FindArtwork lists them all at once.
        if (artwork.Count > 60000) artwork.Clear();
        artwork[token] = path;
        return token;
    }

    // Reads for the phone's media channel, on threads other than the dispatcher's: "file/<id>" for the video it is
    // watching, "stream/<stream id>/<piece>" for its conversion, and "art/<token>/<width>" for pictures.
    // A read this PC can't answer is logged with why: the phone only hears "gone".
    private async Task<PhoneResource?> Resolve(PhoneRemoteSession session, string resource, CancellationToken ct)
    {
        var parts = resource.Split('/');
        streams.TryGetValue(session.PhoneId, out var stream);
        switch (parts)
        {
            case ["file", var id] when stream is not null && stream.Item.Id == id:
                return new(stream.File.Media.Length, stream.File.ReadAsync);
            case ["file", _]:
                return Missing(session, "file", stream is null ? "no-video" : "other-video");
            case ["stream", var id, var piece] when stream?.Converter is { } converter && converter.Stream.Id == id &&
                int.TryParse(piece, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < converter.Stream.Segments:
                return await converter.PhonePieceAsync(index, ct);
            case ["stream", var id, _]:
                return Missing(session, "stream", stream is null ? "no-video" : stream.Converter is not { } current ? "not-converted" : current.Stream.Id != id ? "other-stream" : "no-such-piece");
            case ["subtitle", var id, var number] when stream?.Converter is { } converted && converted.Stream.Id == id &&
                int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var track) && track < stream.SubtitleTracks.Length && !stream.Burns[track]:
                return await SubtitleFile(session, stream, track, ct);
            case ["subtitle", var id, _]:
                return Missing(session, "subtitle", stream is null ? "no-video" : stream.Converter is not { } running ? "not-converted" : running.Stream.Id != id ? "other-stream" : "no-such-subtitle");
            case ["art", var token, var size] when int.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out var width):
                // A token handed out by another process (the phone host before the app took over, or the app before it
                // restarted) is a picture of the library this one hasn't listed yet: it is looked for among them all.
                if (!artwork.TryGetValue(token, out var path))
                {
                    if (await FindArtwork(token, ct) is not { } found) return Missing(session, "art", "unknown-picture");
                    path = found;
                }
                width = Math.Clamp(width, 64, 1280);
                var key = token + "/" + width;
                if (artworkCache.TryGetValue(key, out var cached)) return PhoneResource.FromBytes(cached);
                var (bytes, problem) = await Task.Run(() => Picture(path, width), ct);
                if (bytes is null) return Missing(session, "art", problem);
                if (artworkCache.Count > 300) artworkCache.Clear();
                artworkCache[key] = bytes;
                return PhoneResource.FromBytes(bytes);
            default:
                return Missing(session, parts[0] is "file" or "stream" or "subtitle" or "art" ? parts[0] : "other", "malformed");
        }
    }

    // A conversion's subtitle track as a file, read out of the video once, the first time a phone asks: that reads the
    // whole video, so until it is done the phone hears "busy" and asks again. Kept by file, so another quality or audio
    // track of the same video (a new stream) doesn't read it out again; a failure isn't kept.
    private readonly ConcurrentDictionary<string, Task<byte[]?>> subtitleFiles = new();
    private async Task<PhoneResource?> SubtitleFile(PhoneRemoteSession session, PhoneStream stream, int track, CancellationToken ct)
    {
        if (Ffmpeg.Find() is not { } ffmpeg) return Missing(session, "subtitle", "no-converter");
        var key = stream.Item.Path + "|" + (stream.Item.FileSize ?? 0) + "|" + track;
        if (subtitleFiles.Count > 40) subtitleFiles.Clear();
        var reading = subtitleFiles.GetOrAdd(key, _ => Task.Run(() => Ffmpeg.SubtitleFileAsync(ffmpeg, stream.Item.Path, stream.SubtitleTracks[track], track, StreamCacheDirectory, lifetime)));
        if (await Task.WhenAny(reading, Task.Delay(SubtitleAnswerWait, ct)) != reading) throw new PhoneResourceBusyException("The subtitles are still being read out of the video.");
        byte[]? bytes;
        try { bytes = await reading; }
        // Any failure, so a track that failed once is read out again next time rather than failing until the cache clears.
        catch (Exception ex) { PlaybackDiagnostics.Record("phone-subtitles", new { state = "failed", error = ex.GetType().Name }); bytes = null; }
        if (bytes is not null) return PhoneResource.FromBytes(bytes);
        subtitleFiles.TryRemove(KeyValuePair.Create(key, reading));
        return Missing(session, "subtitle", "unreadable-subtitle");
    }
    private static readonly TimeSpan SubtitleAnswerWait = TimeSpan.FromSeconds(5);

    // Every picture the library's videos have, as tokens: listed once per library list, however many unknown tokens
    // arrive at once (a page of posters asks for 40). A token still unknown after that names no picture of this library.
    private readonly object listingGate = new();
    private object? listedItems;
    private Task? listing;
    private async Task<string?> FindArtwork(string token, CancellationToken ct)
    {
        var read = new TaskCompletionSource<(object List, MediaItem[] Items)>(TaskCreationOptions.RunContinuationsAsynchronously);
        post(() => { var list = Items(); read.TrySetResult((list, list.ToArray())); });
        try
        {
            var (list, items) = await read.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Task task;
            lock (listingGate)
            {
                if (listing is null || !ReferenceEquals(listedItems, list))
                {
                    listedItems = list;
                    listing = Task.Run(() =>
                    {
                        foreach (var item in items)
                            foreach (var picture in new[] { item.Poster, item.SeriesPoster, item.SeasonPoster, item.EpisodePoster, item.GeneratedEpisodePoster })
                                ArtworkToken(picture);
                    });
                }
                task = listing;
            }
            await task.WaitAsync(ct);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { return null; }
        return artwork.TryGetValue(token, out var path) ? path : null;
    }

    // Named by the session's short id, as phone-read-failed is, to match the two when several phones read at once.
    internal static PhoneResource? Missing(PhoneRemoteSession session, string kind, string reason)
    {
        PlaybackDiagnostics.Record("phone-resource-missing", new { session = session.Session[..8], resource = kind, reason });
        return null;
    }

    // The picture at most width pixels wide, as JPEG, never enlarged, or why there is none.
    private static (byte[]? Bytes, string Problem) Picture(string path, int width)
    {
        if (!File.Exists(path)) return (null, "missing-file");
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
            return (output.ToArray(), "");
        }
        catch (Exception ex) { return (null, "unreadable:" + ex.GetType().Name); }
    }

    // The phone asks to play a video. takeOver: the video is the one playing here, and the phone continues it, so this
    // PC saves where it was and stops. The phone hears back where to start and how to read the video: the file itself,
    // or a conversion of it at a lower bitrate for a slow link.
    private async Task Watch(PhoneRemoteHost remote, PhoneRemoteSession session, long request, JsonElement message, Action<string, string> fail)
    {
        // The phone's latest watch is the one it plays: one it asked for before, still preparing (subtitles drawn into the
        // picture take up to BurnWait), stops there and leaves the newer one's stream alone.
        using var asked = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        var earlier = latestWatch.GetValueOrDefault(session.PhoneId);
        latestWatch[session.PhoneId] = asked;
        try { earlier?.Cancel(); } catch (ObjectDisposedException) { }
        try { await WatchLatest(remote, session, request, message, fail, asked.Token); }
        finally { latestWatch.TryRemove(KeyValuePair.Create(session.PhoneId, asked)); }
    }
    // By phone id: the watch each phone asked for last, while it is prepared.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> latestWatch = new();

    private async Task WatchLatest(PhoneRemoteHost remote, PhoneRemoteSession session, long request, JsonElement message, Action<string, string> fail, CancellationToken replaced)
    {
        bool Stale() => current() != remote || replaced.IsCancellationRequested;
        var id = Text(message, "id");
        var item = Items().FirstOrDefault(x => x.Id == id && x.Available && !x.IsVirtual);
        if (item is null || !File.Exists(item.Path)) { fail("gone", "That video isn't available on the PC. Its drive may be disconnected."); return; }
        var wanted = Text(message, "quality") ?? "auto";
        var home = Text(message, "network") == "home";
        // At home the file plays as it is, and the probe only lists the qualities the file allows; it starts now, so a
        // video taken over from the PC isn't left stopped while it runs.
        var ffmpeg = item.IsMusic ? null : Ffmpeg.Find();
        if (ffmpeg is not null && (wanted == StreamQuality.OriginalId || wanted == "auto" && home)) _ = Probe(ffmpeg, item, TimeSpan.Zero);
        var taken = Flag(message, "takeOver") && TakeOver is { } takeOver ? await takeOver(item) : null;
        if (streams.TryRemove(session.PhoneId, out var previous)) previous.Dispose();
        // A phone asks for the video it is watching again to get past a stall, at another quality, or back after a long
        // drop: that goes on with the same viewing, a rewatch or not as it began. A video taken over from the PC goes on
        // with the PC's viewing. Any other video starts a viewing, a rewatch when it was watched before.
        var (lastId, lastRewatch) = previous is not null ? (previous.Item.Id, previous.Rewatch)
            : expired.TryRemove(session.PhoneId, out var gone) && DateTime.UtcNow - gone.At < ViewingGap ? (gone.Id, gone.Rewatch) : default;
        var startedWatched = taken is { } fromPc ? fromPc.Rewatch : lastId == item.Id ? lastRewatch : !item.IsMusic && WasWatched(item.Id);
        var resume = taken?.At ?? (item.IsMusic ? 0 : LibraryWatchProgress.Resume(library.Setting, item.Id, WasWatched(item.Id)));
        var file = new FileMediaSource(item.Path, item.QueueTitle, item.Id);
        var qualities = new List<string> { StreamQuality.OriginalId };
        PhoneStream stream = new(item, file);
        string? note = null;
        // The file is already as small as the conversion would be: asked again with convertAnyway, the PC converts it.
        var convertible = false;
        long durationMs = (long)((item.DurationSeconds ?? 0) * 1000);
        if (wanted != StreamQuality.OriginalId && !(wanted == "auto" && home) && !item.IsMusic)
        {
            var (converted, why, smaller) = await ConvertVideo(item, file, wanted == "auto" ? "720p" : wanted, Number(message, "audio"), Number(message, "subtitle"), qualities, Flag(message, "convertAnyway"), replaced);
            if (Stale()) { converted?.Dispose(); return; }
            if (converted is not null) { stream = converted; durationMs = converted.Converter!.Stream.DurationMs; }
            else (note, convertible) = (why, smaller);
        }
        else if (ffmpeg is not null && await Probe(ffmpeg, item, TimeSpan.FromSeconds(2)) is { } probe)
            qualities.AddRange(StreamQuality.For(probe.ClassHeight).Select(q => q.Id));
        // Where the phone goes from here, worked out before the stream is registered, so the answer follows at once. A
        // failure on the way closes the stream (and its conversion), which nothing else would close.
        SavedQueue? inQueue; int place; EpisodeStep? back, forward, start; object tracks;
        try
        {
            var (source, version) = (Items().ToArray(), LibraryIdentity.TitleKey(item) is { } show ? library.Setting("version:" + show) : null);
            (inQueue, place) = QueuePlace(Text(message, "queue"), Number(message, "queueIndex"), item.Id);
            var queued = inQueue;
            (back, forward, start) = await Task.Run(() => queued is null ? EpisodeSteps(source, item, version) : QueueSteps(source, queued, place));
            tracks = TrackPreferences();
        }
        catch { stream.Dispose(); throw; }
        if (Stale()) { stream.Dispose(); return; }
        if (streams.TryRemove(session.PhoneId, out var raced)) raced.Dispose();
        stream.Rewatch = startedWatched;
        streams[session.PhoneId] = stream;
        // The phone left while the video was being prepared: the stream waits for it as for any dropped link.
        if (remote.Connected.All(s => s.PhoneId != session.PhoneId)) ExpireStream(session.PhoneId);
        var title = item.QueueTitle;
        var detail = StreamDetail(item);
        var queue = inQueue is null ? null : new { id = inQueue.Id, name = inQueue.Name, index = place };
        remote.SendMessage(session, stream.Converter is { } converter
            ? new { type = "stream", request, id = item.Id, title, detail, positionMs = resume, durationMs, kind = "hls", streamId = converter.Stream.Id,
                segments = converter.Stream.Segments, segmentMs = converter.Stream.SegmentMs, quality = converter.Stream.Quality, qualities, note,
                previous = back, next = forward, first = start, tracks, queue,
                audioTracks = stream.AudioTracks.Select((a, index) => new { index, language = a.Language, title = a.Title, codec = a.Codec, layout = a.Layout }), audioTrack = stream.Audio,
                subtitleTracks = stream.SubtitleTracks.Select((s, index) => new { index, language = s.Language, title = s.Title, codec = s.Codec, @default = s.Default, forced = s.Forced, burn = stream.Burns[index] }),
                subtitleTrack = stream.Subtitle }
            : (object)new { type = "stream", request, id = item.Id, title, detail, positionMs = resume, durationMs, kind = "file", resource = "file/" + item.Id,
                length = file.Media.Length, extension = file.Media.Extension, quality = StreamQuality.OriginalId, qualities, note, convertible,
                previous = back, next = forward, first = start, tracks, queue });
    }

    // The saved queue a phone plays a video from, and the video's place in it: the place it named while that still holds
    // the video, otherwise the video's first place. None when the queue is gone or no longer has the video.
    public (SavedQueue? Queue, int Place) QueuePlace(JsonElement message, string mediaId) => QueuePlace(Text(message, "queue"), Number(message, "queueIndex"), mediaId);
    public (SavedQueue? Queue, int Place) QueuePlace(string? queueId, long? named, string mediaId)
    {
        if (queueId is null || PhoneQueues.Read(library.Setting("queues")).FirstOrDefault(q => q.Id == queueId) is not { } saved) return (null, -1);
        if (named is { } at && at is >= 0 and < int.MaxValue && PhoneQueues.Holds(saved, (int)at, mediaId)) return (saved, (int)at);
        var first = Array.IndexOf(saved.MediaIds, mediaId);
        return first < 0 ? (null, -1) : (saved, first);
    }

    // Previous, Next and Loop all's start within a queue, as the PC's player steps through it.
    private static (EpisodeStep? Previous, EpisodeStep? Next, EpisodeStep? First) QueueSteps(MediaItem[] source, SavedQueue saved, int place)
    {
        var byId = ById(source);
        var (previous, next, first) = PhoneQueues.Steps(saved, byId, place, item => File.Exists(item.Path));
        EpisodeStep? Step(int? index) => index is { } at && byId.GetValueOrDefault(saved.MediaIds[at]) is { } item
            ? new(item.Id, item.QueueTitle, StreamDetail(item), at) : null;
        return (Step(previous), Step(next), Step(first));
    }

    private static Dictionary<string, MediaItem> ById(IEnumerable<MediaItem> source)
    {
        var result = new Dictionary<string, MediaItem>();
        foreach (var item in source) result.TryAdd(item.Id, item);
        return result;
    }

    // A video's picture in a list: an episode's still, or its season's or show's poster; a film's poster.
    private static string? Picture(MediaItem item) => item.Series is null ? item.Poster
        : LibraryCatalog.EpisodeArtwork(item) ?? item.SeasonPoster ?? item.SeriesPoster ?? item.Poster;

    // The PC's saved queues, for the phone's Library: each with its count, a picture, and where Play continues, as on the
    // PC's Queues page. Active is the one the PC's player uses.
    private async Task Queues(PhoneRemoteHost remote, PhoneRemoteSession session, long request)
    {
        var source = Items().ToArray();
        var saved = PhoneQueues.Read(library.Setting("queues"));
        var activeId = library.Setting("activeQueue");
        var position = int.TryParse(library.Setting("queuePosition"), out var at) ? at : -1;
        var queues = await Task.Run(() =>
        {
            var playback = library.PlaybackSettings();
            var byId = ById(source);
            bool Watched(string id) => playback.GetValueOrDefault("watched:" + id) == "true";
            return saved.Select(queue =>
            {
                var active = queue.Id == activeId ? PhoneQueues.SavedIndex(queue, byId, position) : -1;
                var upNext = PhoneQueues.UpNext(queue, byId, active, Watched);
                var entries = PhoneQueues.Entries(queue, byId);
                var cover = upNext >= 0 ? byId[queue.MediaIds[upNext]] : entries.Select(e => e.Item).FirstOrDefault();
                return new QueueCard(queue.Id, queue.Name, entries.Length, upNext >= 0 ? upNext : null,
                    queue.Id == activeId, cover is null ? null : ArtworkToken(Picture(cover)));
            }).ToArray();
        });
        if (current() != remote) return;
        // Each message is at most 64 KiB; past that, the last queues are left out.
        while (queues.Length > 0 && Encoding.UTF8.GetByteCount(Wire.Serialize(new { type = "queues", request, queues })) >= 60000) queues = queues[..^1];
        remote.SendMessage(session, new { type = "queues", request, queues });
    }

    private sealed record QueueCard(string Id, string Name, int Count, int? UpNext, bool Active, string? Artwork);

    // A queue's videos in its order (queue), or the show of a video's episodes (episodesOf), as Library entries that also
    // carry their place, for watch and play to take back.
    private async Task BrowseList(PhoneRemoteHost remote, PhoneRemoteSession session, long request, JsonElement message)
    {
        var source = Items().ToArray();
        var queueId = Text(message, "queue");
        var saved = queueId is null ? null : PhoneQueues.Read(library.Setting("queues")).FirstOrDefault(q => q.Id == queueId);
        var anchor = Text(message, "episodesOf") is { } of ? source.FirstOrDefault(x => x.Id == of) : null;
        if (saved is null && anchor is null)
        {
            remote.SendMessage(session, new { type = "error", code = "browse", request,
                message = queueId is not null ? "This queue is no longer on the PC." : "This video is no longer in the PC's library." });
            return;
        }
        var version = anchor is not null && LibraryIdentity.TitleKey(anchor) is { } show ? library.Setting("version:" + show) : null;
        var requested = (int)Math.Clamp(Number(message, "page") ?? 0, 0, 100000);
        var (entries, total, page) = await Task.Run(() =>
        {
            var playback = library.PlaybackSettings();
            var listed = saved is not null ? PhoneQueues.Entries(saved, ById(source))
                : EpisodePlayback.Episodes(source, anchor!, version).Select((item, index) => (index, item)).ToArray();
            var page = Math.Min(requested, Math.Max(0, (listed.Length - 1) / PageSize));
            var entries = listed.Skip(page * PageSize).Take(PageSize).Select(e => ListEntry(e.Item2, e.Item1, playback)).ToArray();
            return (entries, listed.Length, page);
        });
        if (current() != remote) return;
        var location = saved?.Name ?? anchor!.Series ?? anchor.DisplayTitle;
        for (var limit = 300; ; limit /= 2)
        {
            var reply = new { type = "page", request, entries, total, page, pageSize = PageSize, location };
            if (Encoding.UTF8.GetByteCount(Wire.Serialize(reply)) < 60000) { remote.SendMessage(session, reply); return; }
            entries = limit > 0 ? entries.Select(entry => entry with { Overview = entry.Overview[..Math.Min(entry.Overview.Length, limit)] }).ToArray() : entries[..^1];
        }
    }

    private Entry ListEntry(MediaItem item, int index, IReadOnlyDictionary<string, string> playback)
    {
        var watched = playback.GetValueOrDefault("watched:" + item.Id) == "true";
        var position = LibraryWatchProgress.Resume(key => playback.GetValueOrDefault(key), item.Id, watched);
        var overview = item.DetailOverview ?? "";
        return new Entry(item.Id, item.QueueTitle, StreamDetail(item) ?? "", item.Series is null ? "movie" : "episode", item.Season, item.Episode,
            overview[..Math.Min(overview.Length, 300)], item.Kind, item.RuntimeMinutes, item.Year, 0, null, position, watched,
            ArtworkToken(Picture(item)), item.Available && !item.IsVirtual && File.Exists(item.Path), index);
    }

    private static string? StreamDetail(MediaItem item) => item.Series is not null
        ? string.Join(" · ", new[] { item.Season is { } season ? $"Season {season}" : null, item.EpisodeDisplayTitle }.OfType<string>())
        : item.Year?.ToString(CultureInfo.InvariantCulture);

    // QueueIndex: the step's place in the queue the phone plays from.
    private sealed record EpisodeStep(string Id, string Title, string? Detail, int? QueueIndex = null);

    // The episodes the phone's Previous and Next play, chosen as the PC's own buttons and up-next countdown choose them,
    // and the show's first regular episode, where Loop all starts again after the last one. Null for a film, or at either
    // end. Off the dispatcher thread: checking a file can wake a sleeping drive.
    private static (EpisodeStep? Previous, EpisodeStep? Next, EpisodeStep? First) EpisodeSteps(MediaItem[] source, MediaItem item, string? version)
    {
        if (item.Series is null || item.Season is null || item.Episode is null || item.IsMusic) return (null, null, null);
        static EpisodeStep Step(MediaItem episode) => new(episode.Id, episode.QueueTitle, StreamDetail(episode));
        bool Plays(MediaItem episode) => episode.Id == item.Id || File.Exists(episode.Path);
        var episodes = EpisodePlayback.Episodes(source, item, version);
        var at = Array.FindIndex(episodes, episode => episode.Id == item.Id);
        MediaItem? previous = null;
        for (var index = at - 1; at > 0 && index >= 0 && previous is null; index--)
            if (episodes[index].Available && File.Exists(episodes[index].Path)) previous = episodes[index];
        var next = EpisodePlayback.Next(source, item, false, episode => File.Exists(episode.Path), version);
        // As the window's Loop all: the first regular episode that plays, specials only when there is nothing else.
        var first = next is null ? episodes.FirstOrDefault(episode => episode.Season != 0 && Plays(episode)) ?? episodes.FirstOrDefault(Plays) : null;
        if (first?.Id == item.Id) first = null;
        return (previous is null ? null : Step(previous), next is null ? null : Step(next), first is null ? null : Step(first));
    }

    // Settings › Playback, as the window reads it: audio and subtitle language priorities, and subtitle rules by audio
    // language. The phone picks its starting tracks with them as the window would.
    private string[] AudioLanguages()
    {
        try { return TrackLanguages.Parse(library.Setting("audioLanguages") ?? ""); } catch (ArgumentException) { return []; }
    }

    private (string[] Languages, SubtitleLanguageRule[] Rules) SubtitleLanguages()
    {
        string[] subtitles = [];
        SubtitleLanguageRule[] rules = [];
        try { subtitles = TrackLanguages.Parse(library.Setting("subtitleLanguages") ?? "", true); } catch (ArgumentException) { }
        try
        {
            var saved = Wire.Read<SubtitleLanguageRule[]>(library.Setting("subtitleLanguageRules") ?? "[]") ?? [];
            if (saved.All(rule => rule?.AudioLanguage is not null && rule.Languages is not null))
                rules = TrackLanguages.ParseSubtitleRules(saved.Select(rule => new KeyValuePair<string, string>(rule.AudioLanguage, string.Join(", ", rule.Languages))));
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException) { }
        return (subtitles, rules);
    }

    private object TrackPreferences()
    {
        var (subtitles, rules) = SubtitleLanguages();
        return new { audio = AudioLanguages(), subtitles, rules = rules.Select(rule => new { audio = rule.AudioLanguage, languages = rule.Languages }) };
    }

    // A conversion of the video at the quality asked for, or why there is none: no converter, a file that can't be read,
    // or a file already smaller than the conversion would be (Smaller), unless anyway: the phone asks for that when it
    // can't play the file itself, so it gets the picture converted though that saves no data. It carries one audio
    // track: the one the phone asked for, or the one the PC's player would start with. Of the subtitles, the one the
    // phone shows (asked for, -1 for none, or the PC player's choice) is drawn into the picture when it has to be.
    private async Task<(PhoneStream? Stream, string? Why, bool Smaller)> ConvertVideo(MediaItem item, FileMediaSource file, string qualityId, long? audioWanted, long? subtitleWanted, List<string> qualities, bool anyway, CancellationToken replaced)
    {
        if (Ffmpeg.Find() is not { } ffmpeg) return (null, "This copy of BlackPearl has no converter, so the phone plays the file as it is.", false);
        var probe = await Probe(ffmpeg, item, null);
        if (probe is null) return (null, "This file can't be converted, so the phone plays it as it is.", false);
        qualities.AddRange(StreamQuality.For(probe.ClassHeight).Select(q => q.Id));
        var quality = StreamQuality.Find(qualityId) is { } found && qualities.Contains(found.Id) ? found : StreamQuality.For(probe.ClassHeight).Last();
        var plan = StreamQuality.Plan(probe, file.Media.Length, quality, false);
        if (!plan.Smaller && !anyway) return (null, $"This file is already smaller than {quality.Name} would be, so the phone plays it as it is.", true);
        var directory = Path.Combine(StreamCacheDirectory, "phone-" + Guid.NewGuid().ToString("N"));
        var segments = (int)((probe.DurationMs + StreamConverter.SegmentMs - 1) / StreamConverter.SegmentMs);
        var store = SegmentStore.TryCreate(directory, segments);
        if (store is null) return (null, "There is no room on the PC's disk to convert the video, so the phone plays the file as it is.", false);
        var videoKbps = ConvertedKbps(plan);
        var stream = new RoomStream(Guid.NewGuid().ToString("N"), item.Id, plan.Quality.Id, plan.Height, videoKbps, probe.DurationMs, StreamConverter.SegmentMs, segments);
        // Languages as the PC's player knows them: a Matroska track ffmpeg calls English without the file saying so is
        // unknown, or the language identified while it played here. The header is a few kilobytes off a drive the probe woke.
        var container = await Task.Run(() => SubtitleText.Tracks(item.Path));
        var fileKey = LibraryStore.TrackLanguageFile(item.Path);
        var tracks = ProbedAudio.WithContainerLanguages(probe.Audio, container, id => fileKey is null ? null : library.Setting(LibraryStore.TrackLanguageKey(fileKey, true, id)));
        var audio = audioWanted is >= 0 && audioWanted < tracks.Length ? (int)audioWanted.Value : ProbedAudio.Preferred(tracks, AudioLanguages());
        var subtitles = ProbedSubtitle.WithContainerLanguages(probe.Subtitles, container, id => fileKey is null ? null : library.Setting(LibraryStore.TrackLanguageKey(fileKey, false, id)));
        var burns = subtitles.Select(track => track.BurnsFor(probe)).ToArray();
        var (subtitleLanguages, rules) = SubtitleLanguages();
        var shown = subtitleWanted is { } asked ? asked >= 0 && asked < subtitles.Length ? (int?)asked : null
            : ProbedSubtitle.Preferred(subtitles, audio is { } a ? new LanguageTrack(a, tracks[a].Title ?? "", tracks[a].Language) : null, subtitleLanguages, rules);
        StreamSubtitle? burned = null;
        if (shown is { } drawn && burns[drawn])
        {
            burned = await PrepareBurn(ffmpeg, item.Path, probe, drawn, directory, replaced);
            // Not drawn in after all: the phone shows none, rather than a track it can't have.
            if (burned is null) shown = null;
        }
        var converter = new StreamConverter(ffmpeg, new StreamInput(item.Path, probe, audio, burned), stream, store);
        PlaybackDiagnostics.Record("phone-stream", new { quality = plan.Quality.Id, height = plan.Height, kbps = videoKbps, segments, audio, audioTracks = tracks.Length, anyway = !plan.Smaller,
            subtitleTracks = subtitles.Length, subtitles = shown is null ? "none" : burned is null ? "file" : burned.ImageTrack is null ? "drawn text" : "drawn pictures" });
        return (new PhoneStream(item, file) { Converter = converter, Store = store, Directory = directory, AudioTracks = tracks, Audio = audio, SubtitleTracks = subtitles, Burns = burns, Subtitle = shown }, null, false);
    }

    // A subtitle track ready to be drawn into a conversion's picture, or null when it can't be: a text track (with the
    // video's fonts) is read out of the whole video first, which the phone waits for. Past BurnWait it plays without.
    private async Task<StreamSubtitle?> PrepareBurn(string ffmpeg, string path, VideoProbe probe, int track, string directory, CancellationToken replaced)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(replaced);
        limit.CancelAfter(BurnWait);
        try { return await Ffmpeg.PrepareSubtitleAsync(ffmpeg, path, probe, new BurnedSubtitle(Track: track), directory, limit.Token); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            PlaybackDiagnostics.Record("phone-subtitles", new { state = "not drawn", error = ex.GetType().Name });
            return null;
        }
    }
    private static readonly TimeSpan BurnWait = TimeSpan.FromSeconds(25);

    // The least a conversion asked for anyway gets for its picture, however little the file takes: below this H.264 at
    // DVD size turns to blocks.
    private const int AnywayFloorKbps = 300;
    // The most a conversion's picture takes. Converted anyway, no more than the whole file does: Intel's, AMD's and
    // Windows' encoders aim at the quality's own rate, which would send a 400 kbps XviD at three times its size, and
    // the point is a picture the phone can play, not a different size.
    internal static int ConvertedKbps(StreamPlan plan) => plan.Smaller ? plan.VideoKbps : (int)Math.Clamp(plan.FileKbps, AnywayFloorKbps, plan.VideoKbps);

    // The video's description, or null when the converter can't read it: it failed, took longer than its own 30 seconds
    // (a sleeping drive, or security software holding ffmpeg), or longer than wait. Read once per file.
    private readonly ConcurrentDictionary<string, Task<VideoProbe?>> probes = new();
    private async Task<VideoProbe?> Probe(string ffmpeg, MediaItem item, TimeSpan? wait)
    {
        var key = item.Path + "|" + (item.FileSize ?? 0);
        if (probes.Count > 200) probes.Clear();
        var probing = probes.GetOrAdd(key, _ => Task.Run(async () =>
        {
            try { return await VideoProbe.ReadAsync(ffmpeg, item.Path, lifetime); }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException)
            {
                PlaybackDiagnostics.Record("phone-stream", new { state = "probe failed", error = ex.GetType().Name });
                return null;
            }
        }));
        var probe = wait is { } limit ? await Task.WhenAny(probing, Task.Delay(limit)) == probing ? await probing : null : await probing;
        // A failure isn't kept: the next try reads the file again.
        if (probing.IsCompleted && probing.Result is null) probes.TryRemove(KeyValuePair.Create(key, probing));
        return probe;
    }

    // How far the phone got. Saved as this PC saves its own playback, so Continue watching and the watched marks match.
    // A video playing here right now keeps its own position.
    private void SaveProgress(PhoneRemoteSession session, JsonElement message)
    {
        var id = Text(message, "id");
        if (Items().FirstOrDefault(x => x.Id == id) is not { IsMusic: false } item || Number(message, "positionMs") is not { } position) return;
        if (PlaysHere?.Invoke(item.Id) == true) return;
        var progress = PhoneProgress.From(position, Number(message, "durationMs") ?? 0, Flag(message, "ended"), WasWatched(item.Id), Flag(message, "final"));
        var saved = new List<KeyValuePair<string, string>> { KeyValuePair.Create("position:" + item.Id, progress.ResumeMs.ToString(CultureInfo.InvariantCulture)) };
        // The phone's stream of a video already watched when it started is a rewatch, and the phone's place is the rewatch's
        // (LibraryWatchProgress.RewatchPrefix). Its reports, final or not (the phone also reports as it goes to the
        // background, and on after the end), count until the rewatch is seen to the end; a stream of a video watched for the
        // first time never does.
        if (streams.TryGetValue(session.PhoneId, out var stream) && stream.Item.Id == item.Id && stream.Rewatch && WasWatched(item.Id) && progress.Played)
        {
            saved.Add(KeyValuePair.Create(LibraryWatchProgress.RewatchPrefix + item.Id, (progress.Ended ? 0 : Math.Max(0, position)).ToString(CultureInfo.InvariantCulture)));
            if (progress.Ended) stream.Rewatch = false;
        }
        if (progress.Played) saved.Add(KeyValuePair.Create("played:" + item.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)));
        library.SetSettings(saved);
        if (progress.Ended && !WasWatched(item.Id)) SetWatched(item, true);
        else ProgressSaved?.Invoke(item, progress, Flag(message, "final"));
    }

    // The phone marks a title watched or unwatched: a film or an episode, or all of a show's or a season's episodes.
    private void MarkWatchedFor(PhoneRemoteHost remote, PhoneRemoteSession session, long request, JsonElement message, Action<string, string> fail)
    {
        var level = Text(message, "level");
        if (level is not ("movie" or "episode" or "series" or "season") || !message.TryGetProperty("watched", out var flag) || flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            fail("invalid", "The PC didn't understand that. Update BlackPearl on the PC.");
            return;
        }
        var id = Text(message, "id");
        if (Items().FirstOrDefault(x => x.Id == id) is not { IsMusic: false } item) { fail("gone", "That title is no longer in the PC's library."); return; }
        // A file is a film or an episode by what the PC knows of it, whichever card the phone marked it from: clearing a
        // film also clears its other copies. A season card without a number holds the episodes that have none, as on the PC.
        if (level is "movie" or "episode") level = item.Series is null ? "movie" : "episode";
        int? season = level == "season" ? (int?)Number(message, "season") ?? -1 : null;
        MarkWatched(item, level, season, flag.ValueKind == JsonValueKind.True);
        remote.SendMessage(session, new { type = "done", request });
    }

    // The phone takes a title out of Continue watching, as the PC's own Remove does, until it is played again; or, with
    // hidden false (its Undo), puts it back. The id is the video its Continue watching entry names.
    private void HideContinueFor(PhoneRemoteHost remote, PhoneRemoteSession session, long request, JsonElement message, Action<string, string> fail)
    {
        var hidden = true;
        if (message.TryGetProperty("hidden", out var flag))
        {
            if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { fail("invalid", "The PC didn't understand that. Update BlackPearl on the PC."); return; }
            hidden = flag.ValueKind == JsonValueKind.True;
        }
        var id = Text(message, "id");
        if (Items().FirstOrDefault(x => x.Id == id) is not { IsMusic: false } item) { fail("gone", "That title is no longer in the PC's library."); return; }
        library.HideFromContinue(item.Id, hidden);
        ContinueChanged?.Invoke();
        remote.SendMessage(session, new { type = "done", request });
    }

    // What Continue on the PC saves before the PC plays the video: where the phone was. A video watched before stays
    // watched, and the phone's place is its place in the rewatch, which the PC picks up and carries on.
    public void SaveHandOver(MediaItem item, long? positionMs)
    {
        if (positionMs is not { } position || item.IsMusic) return;
        if (!WasWatched(item.Id)) library.Setting("position:" + item.Id, Math.Max(0, position).ToString(CultureInfo.InvariantCulture));
        else if (position >= 10000) library.Setting(LibraryWatchProgress.RewatchPrefix + item.Id, position.ToString(CultureInfo.InvariantCulture));
        else library.ForgetRewatch([item.Id]);
    }

    // The video a play request names, if it can play.
    public MediaItem? Playable(JsonElement message, Action<string, string> fail)
    {
        var id = Text(message, "id");
        var item = Items().FirstOrDefault(x => x.Id == id && x.Available && !x.IsVirtual);
        if (item is null || !File.Exists(item.Path)) { fail("gone", "That video isn't available on the PC. Its drive may be disconnected."); return null; }
        return item;
    }
    public static long? Position(JsonElement message) => Number(message, "positionMs");

    // Play on the PC from a queue while BlackPearl is closed: the queue is saved as the player's, at the video's place,
    // which BlackPearl takes up as it opens, so Next goes on through the queue.
    public void SaveQueueHandOver(SavedQueue saved, int place)
    {
        var byId = ById(Items());
        var resolved = PhoneQueues.Entries(saved, byId).Select(e => e.Item.Id).ToArray();
        library.Setting("activeQueue", saved.Id);
        library.Setting("lastQueue", saved.Id);
        library.Setting("queue", Wire.Serialize(resolved));
        library.Setting("queuePosition", PhoneQueues.PlaybackIndex(saved, byId, place).ToString(CultureInfo.InvariantCulture));
    }

    // Play on the PC outside a queue while BlackPearl is closed: the video plays on its own, as it does when BlackPearl
    // is open, rather than as part of a queue the player last had it in.
    public void SaveNoQueueHandOver() => library.Setting("queuePosition", "-1");
}
