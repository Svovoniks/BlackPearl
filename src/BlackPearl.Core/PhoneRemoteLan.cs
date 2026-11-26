using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace BlackPearl.Core;

// The phone remote on the home network (PHONE-REMOTE.md, "On the home network"): when the room server can't introduce
// a phone to this PC (it is down, the internet is, or it says the PC is offline because the PC lost its internet), a
// phone on the same network reaches the PC directly. The PC listens on one port and speaks the room server's `/remote`
// protocol for its own PC id only. Nothing on it is trusted that the room server's link doesn't trust: the same signed
// signals, the same checks.
public sealed class PhoneLanOptions
{
    // Only 127.0.0.1: development builds and the checks, which then raise no Windows Firewall prompt.
    public bool LoopbackOnly { get; init; }
    // The port tried first: the one used last time, so phones find it where they knew it.
    public int Port { get; init; } = PhoneRemoteProtocol.LanPort;
    // Whether another BlackPearl process of this library (the app, or its phone host) is running and may still hold the
    // port: it hands it over as it ends, so this one waits for it rather than moving to another port.
    public Func<bool>? SiblingRunning { get; init; }
    // Called with the port once listening, to try it first next time.
    public Action<int>? PortChosen { get; init; }
    public int MaxConnections { get; init; } = 8;
    // A connection that hasn't sent one signal signed by a phone this PC knows within this time is closed: anyone on the
    // network can connect, and must not keep the phones' places.
    public TimeSpan Unproven { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan Handshake { get; init; } = TimeSpan.FromSeconds(10);
}

// What Settings shows: off (no listener in this build), starting, waiting (the other BlackPearl process still has the
// port), listening, or busy (every port taken by other programs).
public sealed record PhoneLanState(string State, int? Port, bool LoopbackOnly);

internal sealed class PhoneLanListener : IDisposable
{
    private readonly PhoneRemoteHost host;
    private readonly PhoneLanOptions options;
    private readonly object gate = new();
    private readonly Dictionary<string, Connection> connections = new();
    private readonly CancellationTokenSource lifetime = new();
    private TcpListener? listener;
    private PhoneLanState state;

    public PhoneLanListener(PhoneRemoteHost host, PhoneLanOptions options)
    {
        this.host = host; this.options = options;
        state = new("starting", null, options.LoopbackOnly);
    }

    public PhoneLanState State { get { lock (gate) return state; } }
    public event Action? Changed;
    public int Count { get { lock (gate) return connections.Count; } }

    private static long opened;
    private sealed class Connection(string id, TcpClient client)
    {
        public string Id => id;
        public TcpClient Client => client;
        public readonly long OpenedAt = Environment.TickCount64;
        // The order connections came in: OpenedAt only moves every 15 ms or so on Windows.
        public readonly long Order = Interlocked.Increment(ref opened);
        public readonly CancellationTokenSource Lifetime = new();
        // Bounded: a phone that stops reading doesn't make the PC keep its replies forever.
        public readonly Channel<WireMessage> Outgoing = Channel.CreateBounded<WireMessage>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropWrite });
        public volatile bool Proven;
        // Set once the connection has ended, so its unproven timer doesn't then report it.
        public volatile bool Done;
        public string Reason = "closed";
        // Why a connection that wasn't ended on purpose broke off: the error and its code, such as WebSocketException/ConnectionClosedPrematurely.
        public string? Error;
        // Where it comes from, for the cap on unproven connections from one address.
        public IPAddress? From;
        // The session id handed to this connection's phone, and the phone that has proved itself on it.
        public string Session = "";
        public string? Phone;
    }

    // Unproven connections one address may hold at once. A phone tries each of the PC's addresses once, so a few are
    // plenty; one device opening connections in a loop then only takes its own places.
    public const int UnprovenPerAddress = 4;

    public void Start() => _ = Task.Run(() => RunAsync(lifetime.Token));

    private void SetState(string name, int? port)
    {
        lock (gate) state = new(name, port, options.LoopbackOnly);
        Changed?.Invoke();
    }

    // The addresses phones can try, and the port: the PC's IPv4 addresses on its networks, the default route's first
    // (PhoneRemoteProtocol.LanAddresses), read again now and then as a laptop moves between networks.
    public object? Advertised()
    {
        int port;
        lock (gate) { if (state.State != "listening" || state.Port is not { } listening) return null; port = listening; }
        var addresses = options.LoopbackOnly ? ["127.0.0.1"] : PhoneRemoteProtocol.CachedLanAddresses();
        return addresses.Length == 0 ? null : new { port, addresses };
    }

    public bool Send(string target, WireMessage message)
    {
        Connection? connection;
        lock (gate) connections.TryGetValue(target, out connection);
        return connection is not null && connection.Outgoing.Writer.TryWrite(message);
    }

    // A refusal is logged once per reason a minute: a device on the network that keeps trying writes little.
    private readonly Dictionary<string, long> refusedLogged = new();
    private void Refused(string reason, string? connection = null)
    {
        lock (gate)
        {
            if (refusedLogged.TryGetValue(reason, out var at) && Environment.TickCount64 - at < 60000) return;
            refusedLogged[reason] = Environment.TickCount64;
        }
        PlaybackDiagnostics.Record("remote-lan", new { state = "refused", reason, connection });
    }

    private TcpListener? TryListen(int port, out SocketError error)
    {
        error = SocketError.Success;
        // IPv4 only, as only IPv4 addresses are given to phones: on IPv6 one device has many addresses, and could use
        // them to get round the cap on unproven connections from one address.
        var bindings = options.LoopbackOnly ? new[] { IPAddress.Loopback } : new[] { IPAddress.Any };
        foreach (var address in bindings)
        {
            var candidate = new TcpListener(address, port);
            try
            {
                candidate.Start(16);
                return candidate;
            }
            catch (SocketException ex)
            {
                candidate.Stop();
                error = ex.SocketErrorCode;
                // Taken or reserved: another family won't help.
                if (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied) return null;
            }
        }
        return null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var preferred = options.Port is > 0 and <= 65535 ? options.Port : PhoneRemoteProtocol.LanPort;
        var waitedSince = Environment.TickCount64;
        bool waitingLogged = false, busyLogged = false;
        while (!ct.IsCancellationRequested)
        {
            var bound = TryListen(preferred, out var error);
            if (bound is null && options.SiblingRunning?.Invoke() == true && Environment.TickCount64 - waitedSince < 30000)
            {
                // The app and its phone host hand the port over: the one ending lets go of it within a moment.
                if (!waitingLogged) { waitingLogged = true; SetState("waiting", preferred); PlaybackDiagnostics.Record("remote-lan", new { state = "waiting", port = preferred }); }
                try { await Task.Delay(250, ct); } catch (OperationCanceledException) { return; }
                continue;
            }
            // Taken by another program: the next free one of the ten, which phones learn the next time they connect.
            foreach (var other in Enumerable.Range(PhoneRemoteProtocol.LanPort, PhoneRemoteProtocol.LanPorts).Where(p => p != preferred))
            {
                if (bound is not null) break;
                bound = TryListen(other, out _);
            }
            if (bound is null)
            {
                if (!busyLogged)
                {
                    busyLogged = true; SetState("busy", null);
                    PlaybackDiagnostics.Record("remote-lan", new { state = "busy", ports = $"{PhoneRemoteProtocol.LanPort}-{PhoneRemoteProtocol.LanPort + PhoneRemoteProtocol.LanPorts - 1}", error = error.ToString() });
                }
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { return; }
                waitedSince = Environment.TickCount64;
                continue;
            }
            lock (gate) { if (ct.IsCancellationRequested) { bound.Stop(); return; } listener = bound; }
            var port = ((IPEndPoint)bound.LocalEndpoint).Port;
            // Remembering the port is a convenience: a failure to save it mustn't stop phones from connecting.
            try { options.PortChosen?.Invoke(port); }
            catch (Exception ex) { PlaybackDiagnostics.Record("remote-lan", new { state = "port-not-saved", error = ex.GetType().Name }); }
            SetState("listening", port);
            PlaybackDiagnostics.Record("remote-lan", new { state = "started", port, moved = port != preferred, bind = options.LoopbackOnly ? "loopback" : "any",
                addresses = options.LoopbackOnly ? 1 : PhoneRemoteProtocol.CachedLanAddresses().Length });
            await AcceptAsync(bound, ct);
            return;
        }
    }

    private async Task AcceptAsync(TcpListener bound, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await bound.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { if (ct.IsCancellationRequested) return; await Task.Delay(100, CancellationToken.None); continue; }
            var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            var from = remote is { IsIPv4MappedToIPv6: true } ? remote.MapToIPv4() : remote;
            Connection? evicted = null, connection = null;
            var reason = "too-many-connections";
            lock (gate)
            {
                var unproven = connections.Values.Where(c => !c.Proven).ToArray();
                if (unproven.Count(c => Equals(c.From, from)) >= UnprovenPerAddress) reason = "too-many-from-address";
                else if (connections.Count >= options.MaxConnections)
                {
                    // Full: a connection that hasn't proved it is a phone makes room, the longest-waiting one of the
                    // address holding the most. Phones that have proved it keep theirs.
                    evicted = unproven.GroupBy(c => c.From).OrderByDescending(g => g.Count()).FirstOrDefault()?.OrderBy(c => c.Order).First();
                    if (evicted is not null) { connections.Remove(evicted.Id); connection = Add(client, from); }
                }
                else connection = Add(client, from);
            }
            if (evicted is not null) { evicted.Reason = "evicted"; try { evicted.Lifetime.Cancel(); } catch (ObjectDisposedException) { } Refused("too-many-connections", evicted.Id[4..12]); }
            if (connection is null) { Refused(reason); client.Dispose(); continue; }
            _ = Task.Run(() => ServeAsync(connection, ct), CancellationToken.None);
        }
    }

    private Connection Add(TcpClient client, IPAddress? from)
    {
        var connection = new Connection("lan:" + PhoneRemoteProtocol.NewHex(8), client) { From = from, Session = host.NewLanSession() };
        connections[connection.Id] = connection;
        return connection;
    }

    private async Task ServeAsync(Connection connection, CancellationToken stop)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop, connection.Lifetime.Token);
        // As the room server's: no connection outlives a few hours.
        lifetime.CancelAfter(TimeSpan.FromHours(6));
        WebSocket? socket = null;
        var shortId = connection.Id[4..12];
        try
        {
            connection.Client.NoDelay = true;
            var stream = connection.Client.GetStream();
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                handshake.CancelAfter(options.Handshake);
                var key = await ReadUpgrade(stream, handshake.Token);
                if (key is null)
                {
                    connection.Reason = "bad-request"; Refused("bad-request", shortId);
                    await stream.WriteAsync("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), handshake.Token);
                    return;
                }
                var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), handshake.Token);
                socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(20) });
                var first = await SocketMessages.ReadAsync(socket, handshake.Token);
                if (first?.Type != "remote-phone") { connection.Reason = "bad-request"; Refused("bad-request", shortId); throw new InvalidDataException("Expected remote-phone"); }
                if (first.Number != PhoneRemoteProtocol.Version)
                {
                    connection.Reason = "protocol"; Refused("protocol", shortId);
                    throw new InvalidDataException(first.Number > PhoneRemoteProtocol.Version ? "This PC's BlackPearl is older than your phone app. Update BlackPearl on the PC."
                        : "Update BlackPearl Mobile to control this PC.");
                }
                // Another BlackPearl on this network, or this PC under another library: the phone tries its next address.
                if (!string.Equals(first.Target, host.PcId, StringComparison.OrdinalIgnoreCase))
                { connection.Reason = "wrong-pc"; Refused("wrong-pc", shortId); throw new InvalidDataException("This is another PC."); }
            }
            PlaybackDiagnostics.Record("remote-lan", new { state = "opened", connection = shortId, connections = Count });
            var writer = WriteAsync(socket, connection.Outgoing, lifetime.Token);
            // No relay servers: none are needed here. And the session id this connection's phone must offer under: an
            // offer under any other, such as one seen on another connection and sent again, is refused.
            connection.Outgoing.Writer.TryWrite(new("remote-welcome", Target: connection.Id,
                Data: Wire.Serialize(new { iceServers = Array.Empty<string>(), forceRelay = false, session = connection.Session })));
            connection.Outgoing.Writer.TryWrite(new("remote-online"));
            _ = Task.Delay(options.Unproven, lifetime.Token).ContinueWith(_ =>
            {
                if (connection.Proven || connection.Done) return;
                connection.Reason = "unproven"; Refused("unproven", shortId);
                try { lifetime.Cancel(); } catch (ObjectDisposedException) { }
            }, TaskContinuationOptions.OnlyOnRanToCompletion);
            try
            {
                // The room server's limits for a phone's connection.
                const double Burst = 120, PerSecond = 60;
                var tokens = Burst; var refilled = Wire.Now;
                while (await SocketMessages.ReadAsync(socket, lifetime.Token) is { } message)
                {
                    var now = Wire.Now; tokens = Math.Min(Burst, tokens + (now - refilled) * PerSecond / 1000); refilled = now;
                    if (--tokens < 0) { connection.Reason = "rate"; Refused("rate", shortId); throw new InvalidDataException("Message rate limit exceeded"); }
                    if (message.Type == "ping") { connection.Outgoing.Writer.TryWrite(new("pong", Number: message.Number, Data: Wire.Now.ToString())); continue; }
                    if (message.Type != "signal" || message.Data is not { Length: > 0 } data) continue;
                    // Anyone on the network can send signals, so none of them may end this loop.
                    try
                    {
                        var signal = Wire.Read<RemoteSignal>(data);
                        if (host.HandleSignal(connection.Id, signal, connection.Session) && !connection.Proven) Proved(connection, signal.Phone);
                    }
                    catch (Exception ex) { PlaybackDiagnostics.Record("remote-signal-rejected", new { error = ex.GetType().Name, via = "lan" }); }
                }
            }
            finally { connection.Outgoing.Writer.TryComplete(); try { await writer; } catch { } }
        }
        catch (InvalidDataException ex)
        {
            if (connection.Reason == "closed") { connection.Reason = "too-large"; Refused("too-large", shortId); }
            if (socket?.State == WebSocketState.Open)
            {
                try
                {
                    using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await SocketMessages.SendAsync(socket, new("error", Text: ex.Message), closing.Token);
                    await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, null, closing.Token);
                }
                catch (Exception) { }
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        { connection.Reason = "bad-request"; Refused("bad-request", shortId); }
        // The phone went away, or the network did. A connection this PC ended has its reason already and needs no error.
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or SocketException or ObjectDisposedException)
        { if (connection.Reason == "closed" && !lifetime.IsCancellationRequested) connection.Error = PlaybackDiagnostics.Describe(ex); }
        finally
        {
            connection.Done = true;
            try { lifetime.Cancel(); } catch (ObjectDisposedException) { }
            lock (gate) { if (connections.TryGetValue(connection.Id, out var current) && current == connection) connections.Remove(connection.Id); }
            if (socket is not null)
                PlaybackDiagnostics.Record("remote-lan", new { state = "closed", connection = shortId, reason = connection.Reason, error = connection.Error, proven = connection.Proven, ms = Environment.TickCount64 - connection.OpenedAt });
            socket?.Abort();
            connection.Client.Dispose();
            connection.Lifetime.Dispose();
        }
    }

    // A phone proved itself on [connection]: it keeps its place. Its earlier connections go, newest wins, as a phone
    // has one link at a time.
    private void Proved(Connection connection, string phone)
    {
        Connection[] older;
        lock (gate)
        {
            connection.Proven = true; connection.Phone = phone;
            older = phone == "pair" ? [] : connections.Values.Where(c => c != connection && c.Phone == phone).ToArray();
        }
        foreach (var earlier in older) { earlier.Reason = "superseded"; try { earlier.Lifetime.Cancel(); } catch (ObjectDisposedException) { } }
    }

    // The HTTP request that opens the WebSocket: GET /remote with a WebSocket upgrade, at most 8 KiB. Returns its key, or
    // null for anything else.
    internal static async Task<string?> ReadUpgrade(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var length = 0;
        while (true)
        {
            if (length == buffer.Length) return null;
            var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0) return null;
            length += read;
            var end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
            if (end < 0) continue;
            // The client waits for the answer before it sends anything more.
            if (end + 4 != length) return null;
            var lines = Encoding.ASCII.GetString(buffer, 0, end).Split("\r\n");
            var request = lines[0].Split(' ');
            if (request.Length != 3 || request[0] != "GET" || !request[2].StartsWith("HTTP/1.1", StringComparison.Ordinal)) return null;
            var path = request[1];
            var query = path.IndexOf('?');
            if ((query < 0 ? path : path[..query]) != "/remote") return null;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) return null;
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
            if (!headers.TryGetValue("Upgrade", out var upgrade) || !upgrade.Contains("websocket", StringComparison.OrdinalIgnoreCase)) return null;
            if (!headers.TryGetValue("Connection", out var connection) || !connection.Contains("upgrade", StringComparison.OrdinalIgnoreCase)) return null;
            if (!headers.TryGetValue("Sec-WebSocket-Version", out var version) || version != "13") return null;
            if (!headers.TryGetValue("Sec-WebSocket-Key", out var key)) return null;
            try { if (Convert.FromBase64String(key).Length != 16) return null; }
            catch (FormatException) { return null; }
            return key;
        }
    }

    private static async Task WriteAsync(WebSocket socket, Channel<WireMessage> queue, CancellationToken ct)
    {
        try { await foreach (var message in queue.Reader.ReadAllAsync(ct)) await SocketMessages.SendAsync(socket, message, ct); }
        catch (Exception) when (ct.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        lifetime.Cancel();
        TcpListener? stopping;
        Connection[] open;
        lock (gate) { stopping = listener; listener = null; open = connections.Values.ToArray(); }
        var port = (stopping?.LocalEndpoint as IPEndPoint)?.Port;
        // The port is free as soon as this returns, for the BlackPearl process taking over.
        stopping?.Stop();
        foreach (var connection in open) { connection.Reason = "stopped"; try { connection.Lifetime.Cancel(); } catch (ObjectDisposedException) { } }
        if (stopping is not null) PlaybackDiagnostics.Record("remote-lan", new { state = "stopped", port, connections = open.Length });
        SetState("off", null);
    }
}
