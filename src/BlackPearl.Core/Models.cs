using System.Text.Json;

namespace BlackPearl.Core;

public record LibraryFolder(string Path, string Kind = "Mixed")
{
    public override string ToString() => $"{Path} · {Kind}";
}
public record MediaItem(string Id, string Path, string Title, string Kind, int? Year = null,
    string? Series = null, int? Season = null, int? Episode = null, string? Poster = null,
    string? Overview = null, bool Available = true, bool Matched = false, string? SeriesPoster = null, string? SeasonPoster = null, string? PosterSource = null,
    string? SeasonTitle = null, string? SeasonOverview = null, string? EpisodeTitle = null, string? EpisodeOverview = null,
    string? EpisodePoster = null, string? AirDate = null, int? RuntimeMinutes = null, string? SeasonSource = null, string? EpisodeSource = null,
    string? MetadataProvider = null, int? MetadataId = null, long MetadataFetchedAt = 0, string? MetadataKind = null, string? MetadataType = null,
    string? ShowId = null, string? SeasonId = null, string? SeriesPath = null, int? EpisodeEnd = null, int? AbsoluteEpisode = null,
    string NumberingOrder = "aired", Dictionary<string, string>? ProviderIds = null, bool MetadataLocked = false,
    string[]? LockedFields = null, string[]? LocalMetadataFields = null, string? OriginalTitle = null, string[]? Genres = null,
    string[]? Cast = null, double? Rating = null, bool IsVirtual = false, int? AirsBeforeSeason = null,
    int? AirsBeforeEpisode = null, int? AirsAfterSeason = null, string? NumberingConflict = null, long ArtworkFetchedAt = 0,
    string? LocalPosterUrl = null, string? LocalSeasonPosterUrl = null, string? LocalEpisodePosterUrl = null, int? AbsoluteEpisodeEnd = null,
    int? SourcePart = null, int? SourcePartEpisode = null, int? SourcePartEpisodeEnd = null,
    string? NumberingSource = null, string? SourceLibraryKind = null, bool IsExtra = false, string? GeneratedEpisodePoster = null, string? EpisodePosterQualityPath = null, bool IsBonus = false, Dictionary<string, TmdbField>? TmdbFields = null, int TmdbRetentionVersion = 0, long? FileSize = null,
    // The film this clip came with, for a clip from a film's own extras folder.
    string? ExtraOf = null,
    // A music track's performer and length, read from its tags; its album is Series and its disc and track are Season and Episode.
    // FileWriteTime lets a rescan skip reading the tags of a track whose file has not changed.
    string? Artist = null, int? DurationSeconds = null, long? FileWriteTime = null,
    // A video's length in minutes as its file header records it; 0 once read and found absent. It wins over provider runtimes.
    int? FileRuntimeMinutes = null,
    // What a bonus clip is: "special", "opening", "ending" or "trailer"; null for any other clip.
    string? BonusKind = null,
    // A film's name from its metadata provider, in the metadata language. Title keeps the name its file gave it, which matching uses.
    string? ProviderTitle = null)
{
    public bool IsMusic => Kind == MusicFiles.Kind;
    public string Caption => IsMusic ? string.Join(" · ", new[] { Artist, Series, DurationSeconds is > 0 ? MusicFiles.Duration(DurationSeconds.Value) : null }.Where(x => !string.IsNullOrWhiteSpace(x))) : IsBonus ? Series is null ? BonusLabel : $"{Series} · {BonusLabel}" : Series is not null ? $"{Series} · {(Season is null ? "Season unknown" : $"S{Season:00}")} {(Episode is null ? "Episode unknown" : $"E{Episode:00}" + (EpisodeEnd > Episode ? $"–E{EpisodeEnd:00}" : ""))}" : $"{Kind} · {Year?.ToString() ?? "Local video"}";
    public string BonusLabel => BonusKind switch { "special" => "Special", "opening" => "Opening", "ending" => "Ending", "trailer" => "Trailer", _ => "Bonus content" };
    // Extras list specials first, then openings, endings, trailers and the rest.
    public int BonusOrder => BonusKind switch { "special" => 0, "opening" => 1, "ending" => 2, "trailer" => 3, _ => 4 };
    public string DisplayTitle => Series ?? Title;
    // What people see: a film shows its provider name unless its title was corrected by hand or comes from an NFO file.
    // Locking a title's whole metadata freezes the provider name it already has, so MetadataLocked alone does not hide it.
    public string ShownName => Series is null && !IsMusic && !string.IsNullOrWhiteSpace(ProviderTitle) &&
        !(LockedFields ?? []).Concat(LocalMetadataFields ?? []).Contains("Title", StringComparer.OrdinalIgnoreCase) ? ProviderTitle : Title;
    public string ShownTitle => Series ?? ShownName;
    public string QueueTitle => IsMusic ? Artist is null ? Title : $"{Title} · {Artist}" : Episode is null ? ShownName : Caption;
    public string EpisodeDisplayTitle => IsMusic ? Episode is { } track ? $"{track}. {Title}" : Title : Episode is { } ep ? $"Episode {ep}" + (EpisodeEnd > ep ? $"–{EpisodeEnd}" : "") + (string.IsNullOrWhiteSpace(EpisodeTitle) ? "" : " · " + EpisodeTitle)
        : IsBonus && !string.IsNullOrWhiteSpace(EpisodeTitle) ? EpisodeTitle : ShownName;
    public string? DetailOverview => new[] { EpisodeOverview, SeasonOverview, Overview }.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
    public override string ToString() => IsMusic ? QueueTitle : Episode is not null ? Caption + " · " + Title : Title;
}
// HostKey is the creator's key for the room: it lets them rename it and remove people. Everyone else's key is kept
// in settings under the room's address.
public record SavedRoom(string Id, string Name, string Server, string Code, string? HostKey = null)
{
    public string Caption => (HostKey is null ? "Joined" : "Created by you") + " · " + (Uri.TryCreate(Server, UriKind.Absolute, out var address) ? address.Host : Server);
    public override string ToString() => Name + " · " + Caption;
}
public record SavedQueue(string Id, string Name, string[] MediaIds);
public record SharedMedia(string Id, string Title, long Length, string Extension, SharedMedia[]? Subtitles = null);
// IsHost marks whoever is streaming, or would be: the one member whose app keeps the room's playback state. The room
// service moves it to whoever starts a video, and to someone else when that person leaves. Owner marks the room's creator.
public record Participant(string Id, string Name, bool IsHost, bool Approved, bool Ready = false, string? GuestId = null, bool Owner = false);
public record PlaybackState(long Revision, string MediaId, bool Playing, long PositionMs, long AtUnixMs, string? CommandId = null,
    bool? RequestedPlaying = null, long Generation = 0, bool Seek = false, PlaybackStop? Stop = null)
{
    public bool WantsPlayback => RequestedPlaying ?? Playing;
}
public record PlaybackStop(string Reason, string? Peer = null, string? Name = null);
public record PlaybackReadiness(string MediaId, long Generation);
public record SharedTrack(int Index, string? AssetId = null);
public record RoomTracks(string MediaId, SharedTrack? Audio = null, SharedTrack? Subtitles = null, long Revision = 0);
public record SubtitleAttachment(string MediaId, SharedMedia Subtitle);
// A copy of the room's video converted while it plays, which viewers play instead of the file itself: HEVC or H.264 at Height
// and at most VideoKbps, the room's audio track, its subtitles drawn into the picture. It arrives in pieces of SegmentMs;
// piece k holds the video from k * SegmentMs. A new Id replaces the stream, as when the quality or a track changes.
// Preparing announces a stream that is not ready yet: viewers wait for it rather than start on the file and switch.
public record RoomStream(string Id, string MediaId, string Quality, int Height, int VideoKbps, long DurationMs, int SegmentMs, int Segments, bool Preparing = false);
public record WireMessage(string Type, string? Target = null, string? Sender = null,
    string? Text = null, string? Data = null, long Number = 0);
// The first message a client sends the room service. Room is the invitation's code, or null to create a room. Key is
// the member's saved key for the room, or null on a first visit; the creator's key also proves ownership.
public record EnterRequest(int Protocol, string? Room = null, string? Key = null, string? RoomName = null, bool Temporary = false);
// Host says whether this member holds the streaming role right now; it changes during a session. HostKey is set only
// for the room's creator.
public record Welcome(string Room, string Peer, bool Host, string[] IceServers, bool ForceRelay, string? HostKey = null, string? GuestKey = null, bool Owner = false);
public record AdmittedGuest(string Id, string Name);
public record RoomSnapshot(Participant[] People, SharedMedia? Media, PlaybackState? Playback, bool SharedControls, string[]? Queue = null, AdmittedGuest[]? AdmittedGuests = null, string? Name = null,
    bool LibraryBrowsing = false, LibraryBrowseActivity? LibraryActivity = null, RoomTracks? Tracks = null, RoomSession? LastSession = null,
    // Null from hosts without voice chat; otherwise everyone whose app supports voice, in or out of it.
    VoiceMember[]? Voice = null,
    // True from hosts that show the browsing guest's mouse; guests send catalog-pointer only to them.
    bool GuestPointers = false,
    // From the room service: counts each change of the streaming role, so state from an earlier streamer is ignored.
    long Epoch = 0,
    // From the room service, for the room's creator: the people it removed, who can't come back until let in again.
    AdmittedGuest[]? Removed = null,
    // Null while the room's video is streamed as the file it is. Apps from before converted streams ignore it and
    // play the file.
    RoomStream? Stream = null);
// What the room service tells someone holding a room's invitation before they enter.
public record RoomPresenceInfo(string? Name, string[] People);
public static class RoomProtocol
{
    // Version 2: rooms without a dedicated host. The service and every member must speak the same version.
    public const int Version = 2;
    public const int MaxPresent = 8, MaxMembers = 64;
}

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Json) ?? throw new InvalidDataException("Empty message");
    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

public static class SyncMath
{
    public static long TargetPosition(PlaybackState state, long now) => Math.Max(0,
        state.PositionMs + (state.Playing ? Math.Max(0, now - state.AtUnixMs) : 0));
    // Aim for a <= 500 ms p95 pair difference in the measured LAN/VM fixture.
    // The hard bound is per client; live pair/frame measurements remain required.
    // The default desktop controller uses bounded position interpolation and
    // PlaybackRateControl; keep experimental tighter seeks explicitly opt-in.
    public static bool UseTunedPolicy { get; } = AppEnvironment.Get("SYNC_EXPERIMENTAL") == "1";
    public static long HardSeekMs => UseTunedPolicy ? 600 : 1200;
    public static float Correction(long driftMs) => UseTunedPolicy ? TunedCorrection(driftMs) : EstablishedCorrection(driftMs);
    // Legacy instantaneous rate policy, retained for benchmark comparisons.
    public static float EstablishedCorrection(long driftMs) => Math.Abs(driftMs) < 100 ? 1f : driftMs > 0 ? 1.03f : .97f;
    public static float TunedCorrection(long driftMs) => Math.Abs(driftMs) < 80 ? 1f :
        1f + (float)Math.Clamp(driftMs / 4000d, -.05, .05);
}
