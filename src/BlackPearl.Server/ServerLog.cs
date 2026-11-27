using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

// Everything the room service writes to its log about rooms and phone remotes, so one place lists every event and its
// level. Information is a connection's life: entering, leaving and why, rooms opening and closing, people removed or let
// back in. Warning is a connection turned away or lost without a proper close. Debug is per message and stays off unless
// configured (deploy/README.md, "Logs").
//
// The log lives on the server, so it never holds what identifies people or lets anyone in: no display or room names, no
// invitation codes, keys, member, phone or PC ids, signal contents, or client addresses. Tag stands in where something
// must be told apart. Pings and playback never log, so a room that is just watching writes nothing.
static partial class ServerLog
{
    // The first 6 hex digits of a value's SHA-256: enough to follow one room, person or PC through the log, and no way back
    // to the value.
    public static string Tag(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..6];
    // Every line about one socket starts with its connection id. The service makes one up for now; an id the app sends
    // could be accepted here later, so the app's and the service's logs name a connection alike.
    public static string NewConnectionId() => RandomNumberGenerator.GetHexString(8, lowercase: true);
    public static double Seconds(long since) => Math.Round(Stopwatch.GetElapsedTime(since).TotalSeconds, 1);

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "BlackPearl room server {Version} starting: room protocols {Protocols}, phone remote protocol {Remote}, {Saved} saved rooms with {Members} members and {Banned} people removed")]
    public static partial void Starting(ILogger logger, string version, string protocols, int remote, int saved, int members, int banned);
    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Turned away {Count} requests over the per-address limit since the last such line (latest: {Path})")]
    public static partial void RateLimited(ILogger logger, long count, string path);

    // Rooms.
    [LoggerMessage(EventId = 10, Level = LogLevel.Information, Message = "[{Connection}] Created {Kind} room {Room} (protocol {Protocol})")]
    public static partial void RoomCreated(ILogger logger, string connection, string kind, string room, int protocol);
    [LoggerMessage(EventId = 11, Level = LogLevel.Information, Message = "[{Connection}] Room {Room} opened after {EmptySeconds} s empty")]
    public static partial void RoomOpened(ILogger logger, string connection, string room, long emptySeconds);
    [LoggerMessage(EventId = 12, Level = LogLevel.Information, Message = "Room {Room} is empty ({Kind})")]
    public static partial void RoomEmpty(ILogger logger, string room, string kind);
    [LoggerMessage(EventId = 13, Level = LogLevel.Information, Message = "Room {Room} closed: {Reason}")]
    public static partial void RoomClosed(ILogger logger, string room, string reason);
    [LoggerMessage(EventId = 14, Level = LogLevel.Information, Message = "[{Connection}] {Who} entered room {Room} as {Role} (protocol {Protocol}); {Present} present")]
    public static partial void Entered(ILogger logger, string connection, string who, string room, string role, int protocol, int present);
    [LoggerMessage(EventId = 15, Message = "[{Connection}] {Who} left room {Room} after {Seconds} s: {Reason}; {Present} present; {Messages} messages, {Relayed} signals relayed, {Dropped} dropped")]
    public static partial void Left(ILogger logger, LogLevel level, string connection, string who, string room, double seconds, string reason, int present, int messages, int relayed, int dropped);
    [LoggerMessage(EventId = 16, Level = LogLevel.Warning, Message = "[{Connection}] Turned away from room {Room}: {Reason}")]
    public static partial void TurnedAway(ILogger logger, string connection, string room, string reason);
    [LoggerMessage(EventId = 17, Level = LogLevel.Information, Message = "[{Connection}] {Who} entered room {Room} again, replacing connection {Earlier}")]
    public static partial void Replaced(ILogger logger, string connection, string who, string room, string earlier);
    [LoggerMessage(EventId = 18, Level = LogLevel.Information, Message = "[{Connection}] Removed {Who} from room {Room} ({Presence})")]
    public static partial void Removed(ILogger logger, string connection, string who, string room, string presence);
    [LoggerMessage(EventId = 19, Level = LogLevel.Information, Message = "[{Connection}] Let {Who} back into room {Room}")]
    public static partial void LetBackIn(ILogger logger, string connection, string who, string room);
    [LoggerMessage(EventId = 20, Level = LogLevel.Information, Message = "[{Connection}] Host admitted {Who} into room {Room}; {Admitted} admitted")]
    public static partial void Admitted(ILogger logger, string connection, string who, string room, int admitted);
    [LoggerMessage(EventId = 21, Level = LogLevel.Debug, Message = "[{Connection}] Took the streaming role in room {Room} (epoch {Epoch})")]
    public static partial void Claimed(ILogger logger, string connection, string room, long epoch);
    [LoggerMessage(EventId = 22, Level = LogLevel.Debug, Message = "[{Connection}] Dropped a signal in room {Room}: {Reason}")]
    public static partial void RoomSignalDropped(ILogger logger, string connection, string room, string reason);
    [LoggerMessage(EventId = 23, Level = LogLevel.Information, Message = "[{Connection}] Saved room {Room} opened, the first time since the service started {StartedSeconds} s ago")]
    public static partial void RoomOpenedSinceStart(ILogger logger, string connection, string room, long startedSeconds);

    // Sockets that end before they enter a room or reach a PC: turned away (Warning), or closed or lost on the way.
    [LoggerMessage(EventId = 30, Message = "[{Connection}] {Path} connection ended before entering, after {Seconds} s: {Reason}")]
    public static partial void EndedBeforeEntering(ILogger logger, LogLevel level, string connection, string path, double seconds, string reason);

    // Phone remotes (RemoteLinks).
    [LoggerMessage(EventId = 40, Level = LogLevel.Information, Message = "[{Connection}] PC {Pc} online; {Phones} phones waiting for it")]
    public static partial void PcOnline(ILogger logger, string connection, string pc, int phones);
    [LoggerMessage(EventId = 41, Level = LogLevel.Information, Message = "[{Connection}] PC {Pc} connected again, replacing connection {Earlier}")]
    public static partial void PcReplaced(ILogger logger, string connection, string pc, string earlier);
    [LoggerMessage(EventId = 42, Message = "[{Connection}] PC {Pc} disconnected after {Seconds} s: {Reason}; told {Phones} phones it is offline; {Messages} messages, {Relayed} signals relayed, {Dropped} dropped")]
    public static partial void PcLeft(ILogger logger, LogLevel level, string connection, string pc, double seconds, string reason, int phones, int messages, int relayed, int dropped);
    [LoggerMessage(EventId = 43, Level = LogLevel.Information, Message = "[{Connection}] Phone asked for PC {Pc}, which is {State}; {Phones} phones connecting to it")]
    public static partial void PhoneAsked(ILogger logger, string connection, string pc, string state, int phones);
    [LoggerMessage(EventId = 44, Message = "[{Connection}] Phone for PC {Pc} disconnected after {Seconds} s: {Reason}; {Messages} messages, {Relayed} signals relayed, {Dropped} dropped")]
    public static partial void PhoneLeft(ILogger logger, LogLevel level, string connection, string pc, double seconds, string reason, int messages, int relayed, int dropped);
    [LoggerMessage(EventId = 45, Level = LogLevel.Debug, Message = "[{Connection}] Relayed a signal {Direction} for PC {Pc}")]
    public static partial void RemoteSignalRelayed(ILogger logger, string connection, string direction, string pc);
    [LoggerMessage(EventId = 46, Level = LogLevel.Debug, Message = "[{Connection}] Dropped a signal {Direction} for PC {Pc}: {Reason}")]
    public static partial void RemoteSignalDropped(ILogger logger, string connection, string direction, string pc, string reason);
}

// Why a connection ended, and how loudly to say so.
sealed record Ending(string Reason, LogLevel Level)
{
    public static readonly Ending ClosedByApp = new("closed by the app", LogLevel.Information);
    // Turned away with an error the app is told. The service writes every InvalidDataException's text itself; JSON and
    // format errors can quote what the app sent, so only their kind is logged.
    public static Ending Refused(Exception error) => new("refused: " + (error is InvalidDataException ? error.Message : "malformed message (" + error.GetType().Name + ")"), LogLevel.Warning);
    // BlackPearl leaves a room by cutting its connection (RoomConnection.Abort) rather than closing it, so this is how most
    // people leave, and an app that quits or crashes looks the same. A network that drops without a word shows as a missed
    // keep-alive instead.
    public static readonly Ending Cut = new("the app cut the connection without a WebSocket close", LogLevel.Information);
    // The app or its network went quiet without closing: a sleeping laptop, a lost Wi-Fi link.
    public static readonly Ending MissedKeepAlive = new("no answer to the service's keep-alive pings for 30 s", LogLevel.Warning);
    // The socket failed under the service.
    public static Ending Lost(Exception error) => error switch
    {
        WebSocketException { WebSocketErrorCode: WebSocketError.ConnectionClosedPrematurely } => Cut,
        WebSocketException socket => new($"lost ({socket.WebSocketErrorCode})", LogLevel.Warning),
        _ => new($"lost ({error.GetType().Name})", LogLevel.Warning),
    };
}
