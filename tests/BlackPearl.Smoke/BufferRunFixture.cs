using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using LibVLCSharp.Shared;
using BlackPearl.Core;

// Measures buffering of a real video through real peer connections and the real player:
//   --buffer-run <video> [seconds] [--mbps <limit>] [--relay <room server>] [--memory]
// The video plays for a third of the time, is paused for a third, and plays again. Every five
// seconds it prints the playback position, the video buffered ahead of it, what was downloaded,
// and how much of that had been downloaded before. --mbps limits what the streaming side sends,
// like a slow uplink; --relay sends the video through the room server's relay; --memory runs
// without a cache file.
static class BufferRunFixture
{
    public static async Task Run(string[] args)
    {
        var path = args[1];
        var seconds = args.Length > 2 && int.TryParse(args[2], out var parsed) ? parsed : 90;
        string? Option(string name) { var at = Array.IndexOf(args, name); return at >= 0 && at + 1 < args.Length ? args[at + 1] : null; }
        var limit = double.TryParse(Option("--mbps"), System.Globalization.CultureInfo.InvariantCulture, out var mbps) ? mbps : 0;
        var server = Option("--relay");
        var useFile = !args.Contains("--memory");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 120));

        string[] ice = [];
        System.Net.WebSockets.ClientWebSocket? socket = null;
        if (server is not null)
        {
            socket = new();
            await socket.ConnectAsync(new Uri(new Uri(server.Replace("https://", "wss://").Replace("http://", "ws://")), "/room"), timeout.Token);
            await SocketMessages.SendAsync(socket, new("enter", Text: "Buffer measurement", Data: Wire.Serialize(new EnterRequest(RoomProtocol.Version, Temporary: true))), timeout.Token);
            ice = Wire.Read<Welcome>((await SocketMessages.ReadAsync(socket, timeout.Token))!.Data!).IceServers;
        }
        var directory = Path.Combine(Path.GetTempPath(), "BlackPearl-checks", Guid.NewGuid().ToString("N"));
        try
        {
            var file = new FileMediaSource(path);
            var sent = new LimitedSource(file, limit);
            using var host = new PeerTransport(ice, relayOnly: server is not null);
            using var guest = new PeerTransport(ice, relayOnly: server is not null);
            var signals = Channel.CreateUnbounded<(PeerTransport Peer, string Type, string Data)>();
            host.Signal += (type, data) => signals.Writer.TryWrite((guest, type, data));
            guest.Signal += (type, data) => signals.Writer.TryWrite((host, type, data));
            var pump = Task.Run(async () =>
            {
                try { await foreach (var signal in signals.Reader.ReadAllAsync(timeout.Token)) signal.Peer.ReceiveSignal(signal.Type, signal.Data); }
                catch (OperationCanceledException) { }
            });
            host.ResolveMedia = id => id == file.Media.Id ? sent : null;
            host.StartOffer();
            await Task.WhenAll(host.Ready, guest.Ready, host.ControlReady, guest.ControlReady).WaitAsync(TimeSpan.FromSeconds(35), timeout.Token);

            var received = new CountingSource(new RemoteMediaSource(guest, file.Media));
            using var cache = useFile ? StreamCache.TryCreate(directory, file.Media) : null;
            await using var bridge = new MediaBridge();
            var url = await bridge.StartAsync(received, cache);
            Core.Initialize();
            using var vlc = new LibVLC("--vout=dummy", "--no-video-title-show", "--quiet");
            using var player = new MediaPlayer(vlc);
            // Decoded audio goes nowhere, so the run is silent.
            player.SetAudioFormat("S16N", 48000, 2);
            player.SetAudioCallbacks((_, _, _, _) => { }, null, null, null, null);
            var stalls = 0;
            player.Buffering += (_, e) => { if (e.Cache < 100) Interlocked.Increment(ref stalls); };
            using var media = new Media(vlc, url);
            media.AddOption($":network-caching={MediaStreaming.NetworkCacheForRtt(50)}");
            media.AddOption(":sub-autodetect-file=0");
            Console.WriteLine($"{Path.GetExtension(path)} {file.Media.Length / 1e9:F2} GB; {(server is null ? "direct" : "relay")}; {(limit > 0 ? limit + " Mbps limit" : "no limit")}; {(cache is null ? "memory window" : "cache file")}");
            var clock = Stopwatch.StartNew();
            player.Play(media);
            var readAhead = false; long lastBytes = 0, lastAt = 0, played = 0, lastTime = -1;
            Console.WriteLine("   t  state    position  buffered-ahead  downloaded  again   rate");
            for (var tick = 1; clock.ElapsedMilliseconds < seconds * 1000L; tick++)
            {
                await Task.Delay(250, timeout.Token);
                var now = clock.ElapsedMilliseconds;
                if (!readAhead && player.IsPlaying && player.Time > 500 && player.Length > 0)
                {
                    readAhead = true;
                    bridge.SetReadAhead(StreamingBufferOptions.ReadAheadBytes(file.Media.Length, player.Length));
                    Console.WriteLine($"{now / 1000.0,4:F0}  duration {TimeSpan.FromMilliseconds(player.Length):h\\:mm\\:ss}, average {file.Media.Length * 8.0 / player.Length / 1000:F1} Mbps; read-ahead started");
                }
                if (now >= seconds * 1000L / 3 && now < seconds * 2000L / 3) { if (player.IsPlaying) player.SetPause(true); }
                else if (readAhead && player.State == VLCState.Paused) player.SetPause(false);
                if (player.Time != lastTime && lastTime >= 0 && player.IsPlaying) played += 250;
                lastTime = player.Time;
                if (tick % 20 != 0) continue;
                var bytes = received.Bytes;
                Console.WriteLine($"{now / 1000.0,4:F0}  {player.State,-8} {TimeSpan.FromMilliseconds(Math.Max(0, player.Time)):mm\\:ss}     {bridge.BufferedAheadBytes / 1e6,8:F1} MB   {bytes / 1e6,7:F1} MB  {received.RepeatedBytes / 1e6,5:F1}  {(bytes - lastBytes) * 8.0 / (now - lastAt) / 1000,5:F1} Mbps");
                lastBytes = bytes; lastAt = now;
            }
            var total = received.Bytes;
            Console.WriteLine($"RESULT downloaded {total / 1e6:F1} MB in {clock.Elapsed.TotalSeconds:F0} s ({total * 8.0 / clock.ElapsedMilliseconds / 1000:F1} Mbps average); downloaded again {received.RepeatedBytes / 1e6:F1} MB; " +
                $"buffered ahead at the end {bridge.BufferedAheadBytes / 1e6:F1} MB; position {TimeSpan.FromMilliseconds(Math.Max(0, player.Time)):mm\\:ss}; buffering events {stalls}; cache file {(cache?.StoredBytes ?? 0) / 1e6:F1} MB");
            player.Stop();
            timeout.Cancel(); await pump;
        }
        finally
        {
            socket?.Abort();
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    // What the viewer's side fetched from the streamer, and how much of it a second time.
    private sealed class CountingSource(IMediaSource inner) : IMediaSource
    {
        private readonly ConcurrentDictionary<long, int> seen = new();
        private long bytes, repeated;
        public long Bytes => Interlocked.Read(ref bytes);
        public long RepeatedBytes => Interlocked.Read(ref repeated);
        public SharedMedia Media => inner.Media;
        public async Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct)
        {
            var data = await inner.ReadAsync(offset, count, ct);
            Interlocked.Add(ref bytes, data.Length);
            if (seen.AddOrUpdate(offset, 1, (_, times) => times + 1) > 1) Interlocked.Add(ref repeated, data.Length);
            return data;
        }
    }

    // A streamer whose connection carries no more than the limit.
    private sealed class LimitedSource(IMediaSource inner, double mbps) : IMediaSource
    {
        private readonly object gate = new();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private double nextMs;
        public SharedMedia Media => inner.Media;
        public async Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct)
        {
            if (mbps > 0)
            {
                double wait;
                lock (gate)
                {
                    var now = clock.Elapsed.TotalMilliseconds;
                    nextMs = Math.Max(nextMs, now) + count * 8 / (mbps * 1000);
                    wait = nextMs - now;
                }
                if (wait > 1) await Task.Delay((int)wait, ct);
            }
            return await inner.ReadAsync(offset, count, ct);
        }
    }
}
