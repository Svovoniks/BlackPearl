using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using BlackPearl.Core;

// Exercises the room service's cleanup and rate limits, plus the streamer's resilience against misbehaving members.
static class RoomServerFixture
{
    public static async Task Run(string sdk, string serverDll, string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var url = "http://localhost:" + ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var start = new ProcessStartInfo(sdk) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["BLACKPEARL_DATA"] = Path.Combine(directory, "server");
        start.Environment["BLACKPEARL_EMPTY_ROOM_SECONDS"] = "1"; start.Environment["BLACKPEARL_SWEEP_SECONDS"] = "1";
        start.Environment["BLACKPEARL_PERSISTENT_ROOMS_PER_HOUR"] = "2";
        start.ArgumentList.Add(serverDll); start.ArgumentList.Add("--urls"); start.ArgumentList.Add(url);
        using var server = Process.Start(start)!;
        var stdout = server.StandardOutput.ReadToEndAsync(); var stderr = server.StandardError.ReadToEndAsync();
        try
        {
            using var http = new HttpClient();
            for (var i = 0; i < 100; i++) { try { if ((await http.GetAsync(url + "/health")).IsSuccessStatusCode) break; } catch (HttpRequestException) { } await Task.Delay(200); }
            var socketUrl = new Uri(url.Replace("http", "ws") + "/room");
            async Task<(ClientWebSocket Socket, WireMessage Reply)> Open(WireMessage first)
            {
                var socket = new ClientWebSocket(); await socket.ConnectAsync(socketUrl, default);
                await SocketMessages.SendAsync(socket, first, default);
                return (socket, await Read(socket));
            }
            static async Task<WireMessage> Read(ClientWebSocket socket)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                return await SocketMessages.ReadAsync(socket, timeout.Token) ?? new("closed");
            }
            static async Task Close(ClientWebSocket socket)
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, default); } catch (Exception) { socket.Abort(); }
                socket.Dispose();
            }

            static WireMessage Enter(string name, string? room = null, string? key = null, bool temporary = true) =>
                new("enter", Text: name, Data: Wire.Serialize(new EnterRequest(RoomProtocol.Version, room, key, Temporary: temporary)));
            var (temporary, created) = await Open(Enter("First"));
            var temporaryCode = Wire.Read<Welcome>(created.Data!).Room;
            await Close(temporary); await Task.Delay(3500);
            var (late, rejected) = await Open(Enter("Late", temporaryCode));
            check(rejected is { Type: "error", Text: "Invitation is invalid or expired" }, "server sweeps an empty temporary room after its grace period");
            await Close(late);

            var (live, liveWelcome) = await Open(Enter("First"));
            await Task.Delay(3500);
            var (waiting, joined) = await Open(Enter("Second", Wire.Read<Welcome>(liveWelcome.Data!).Room));
            check(joined.Type == "welcome", "server keeps temporary rooms that still have members");

            for (var i = 0; i < 300; i++) await SocketMessages.SendAsync(live, new("ping", Number: i), default);
            var pongs = 0;
            while (pongs < 300) { var reply = await Read(live); if (reply.Type == "pong") pongs++; else if (reply.Type is "error" or "closed") break; }
            check(pongs == 300, "signaling bursts for a room full of members stay connected");
            var floodError = "";
            try
            {
                for (var i = 0; i < 1200; i++) await SocketMessages.SendAsync(waiting, new("ping", Number: i), default);
                for (var i = 0; i < 1300 && floodError == ""; i++) { var reply = await Read(waiting); if (reply.Type is "error" or "closed") floodError = reply.Text ?? reply.Type; }
            }
            catch (WebSocketException) { floodError = "closed"; }
            check(floodError is "Message rate limit exceeded" or "closed", "message floods still disconnect that member");
            await Close(waiting); await Close(live);

            var (owner, ownerWelcome) = await Open(Enter("Owner", temporary: false));
            var saved = Wire.Read<Welcome>(ownerWelcome.Data!);
            await Close(owner); await Task.Delay(3500);
            var (returned, returnedWelcome) = await Open(Enter("Owner", saved.Room, saved.HostKey));
            check(returnedWelcome.Type == "welcome" && Wire.Read<Welcome>(returnedWelcome.Data!) is { Owner: true } again && again.Room == saved.Room, "saved rooms survive the sweep, and their creator returns with their key");
            await Close(returned);
            var (second, secondWelcome) = await Open(Enter("Owner", temporary: false));
            await Close(second);
            var (third, limited) = await Open(Enter("Owner", temporary: false));
            check(secondWelcome.Type == "welcome" && limited is { Type: "error", Text: "Persistent room limit reached. Try again later." }, "saved room creation is limited per address");
            await Close(third);

            // One member cannot make the service flood another with requests.
            var (target, targetWelcome) = await Open(Enter("Target"));
            var targetIdentity = Wire.Read<Welcome>(targetWelcome.Data!);
            var (spammer, _) = await Open(Enter("Spammer", targetIdentity.Room));
            var requests = 0;
            var counting = Task.Run(async () =>
            {
                try { while (true) { var reply = await Read(target); if (reply.Type == "connect") Interlocked.Increment(ref requests); if (reply.Type == "closed") break; } }
                catch (Exception) { }
            });
            for (var i = 0; i < 100; i++) await SocketMessages.SendAsync(spammer, new("reconnect", Target: targetIdentity.Peer), default);
            for (var i = 0; i < 100; i++) await SocketMessages.SendAsync(spammer, new("claim"), default);
            await Task.Delay(1000);
            check(Volatile.Read(ref requests) is > 0 and <= 30, $"one member's requests are bounded before they reach other members ({requests} of 100 relayed)");
            await Close(spammer); await Close(target); await counting.WaitAsync(TimeSpan.FromSeconds(15));

            await HostSurvivesGuests(url, directory, check);
        }
        finally
        {
            if (!server.HasExited) server.Kill(true);
            await server.WaitForExitAsync();
            await File.WriteAllTextAsync(Path.Combine(directory, "server.log"), await stdout + await stderr);
        }
        await ConnectionsEndCleanly(sdk, serverDll, directory, check);
        await CloudflareRelay(sdk, serverDll, directory, check);
    }

    // Members leaving must not leave failed tasks behind: on the live server each one was an error report. A server of
    // its own collects garbage often, since a task that failed with nobody awaiting it is only reported once collected;
    // the other checks keep their timing on a server that runs normally.
    private static async Task ConnectionsEndCleanly(string sdk, string serverDll, string directory, Action<bool, string> check)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var url = "http://localhost:" + ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var start = new ProcessStartInfo(sdk) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["BLACKPEARL_DATA"] = Path.Combine(directory, "leaving-server");
        // A 256 KB first generation; the value is hex.
        start.Environment["DOTNET_GCgen0MaxBudget"] = "40000";
        start.ArgumentList.Add(serverDll); start.ArgumentList.Add("--urls"); start.ArgumentList.Add(url);
        using var server = Process.Start(start)!;
        var stdout = server.StandardOutput.ReadToEndAsync(); var stderr = server.StandardError.ReadToEndAsync();
        var welcomed = 0;
        try
        {
            using var http = new HttpClient();
            for (var i = 0; i < 100; i++) { try { if ((await http.GetAsync(url + "/health")).IsSuccessStatusCode) break; } catch (HttpRequestException) { } await Task.Delay(200); }
            // Members enter and leave, some closing properly and some dropping. Each comes from its own address, as
            // the loopback proxy reports it, so the per-address request limit doesn't turn them away.
            for (var i = 0; i < 60; i++)
            {
                using var socket = new ClientWebSocket();
                socket.Options.SetRequestHeader("X-Forwarded-For", $"198.51.100.{i + 1}");
                await socket.ConnectAsync(new Uri(url.Replace("http", "ws") + "/room"), default);
                await SocketMessages.SendAsync(socket, new("enter", Text: "Leaving", Data: Wire.Serialize(new EnterRequest(RoomProtocol.Version, Temporary: true))), default);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                if (await SocketMessages.ReadAsync(socket, timeout.Token) is { Type: "welcome" }) welcomed++;
                if (i % 2 == 0) { try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, default); } catch (Exception) { } }
                else socket.Abort();
            }
            await Task.Delay(1000);
        }
        finally
        {
            if (!server.HasExited) server.Kill(true);
            await server.WaitForExitAsync();
            await File.WriteAllTextAsync(Path.Combine(directory, "leaving-server.log"), await stdout + await stderr);
        }
        var lines = (await stdout + await stderr).Split('\n').Select(line => line.Trim()).ToArray();
        var unobserved = Array.FindIndex(lines, line => line.Contains("Unobserved task exception", StringComparison.Ordinal));
        check(welcomed == 60 && unobserved < 0, $"members leaving end their connections without failed tasks nobody awaited ({welcomed} of 60 entered)" +
            (unobserved < 0 ? "" : $" ({string.Join(" | ", lines.Skip(unobserved).Take(3))})"));
    }

    // A stand-in for Cloudflare's credential API checks that the server turns its ICE servers into libdatachannel URLs,
    // and that rate limits follow the client address reported by the loopback reverse proxy.
    private static async Task CloudflareRelay(string sdk, string serverDll, string directory, Action<bool, string> check)
    {
        static int FreePort() { using var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); return ((IPEndPoint)probe.LocalEndpoint).Port; }
        var api = $"http://localhost:{FreePort()}/";
        using var cloudflare = new HttpListener(); cloudflare.Prefixes.Add(api); cloudflare.Start();
        var requests = new ConcurrentQueue<(string Path, string? Authorization, string Body)>();
        var failing = 1; var served = 0;
        _ = Task.Run(async () =>
        {
            while (cloudflare.IsListening)
            {
                HttpListenerContext context;
                try { context = await cloudflare.GetContextAsync(); } catch (Exception) { return; }
                using (var reader = new StreamReader(context.Request.InputStream)) requests.Enqueue((context.Request.Url!.AbsolutePath, context.Request.Headers["Authorization"], await reader.ReadToEndAsync()));
                if (Volatile.Read(ref failing) == 1) { context.Response.StatusCode = 503; context.Response.Close(); continue; }
                var body = """{"iceServers":[{"urls":["stun:stun.cloudflare.com:3478","stun:stun.cloudflare.com:53"]},{"urls":["turn:turn.cloudflare.com:3478?transport=udp","turn:turn.cloudflare.com:53?transport=udp","turns:turn.cloudflare.com:5349?transport=tcp"],"username":"user/1","credential":"pa:ss"}]}"""u8.ToArray();
                context.Response.ContentType = "application/json"; await context.Response.OutputStream.WriteAsync(body); context.Response.Close();
                Interlocked.Increment(ref served);
            }
        });
        var url = "http://localhost:" + FreePort();
        var start = new ProcessStartInfo(sdk) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["BLACKPEARL_DATA"] = Path.Combine(directory, "cloudflare-server");
        start.Environment["CLOUDFLARE_TURN_KEY_ID"] = "key-id"; start.Environment["CLOUDFLARE_TURN_API_TOKEN"] = "api-token";
        start.Environment["CLOUDFLARE_TURN_API"] = api.TrimEnd('/'); start.Environment["CLOUDFLARE_TURN_RETRY_SECONDS"] = "1";
        // A four-second credential lifetime makes the server refresh every two seconds, which exercises idle expiry quickly.
        start.Environment["CLOUDFLARE_TURN_TTL_SECONDS"] = "4";
        start.ArgumentList.Add(serverDll); start.ArgumentList.Add("--urls"); start.ArgumentList.Add(url);
        using var server = Process.Start(start)!;
        var stdout = server.StandardOutput.ReadToEndAsync(); var stderr = server.StandardError.ReadToEndAsync();
        try
        {
            using var http = new HttpClient();
            string health = "";
            for (var i = 0; i < 100 && health == ""; i++) { try { var reply = await http.GetAsync(url + "/health"); if (reply.IsSuccessStatusCode) health = await reply.Content.ReadAsStringAsync(); } catch (HttpRequestException) { } if (health == "") await Task.Delay(200); }
            async Task<string[]> CreateRoom()
            {
                using var socket = new ClientWebSocket(); await socket.ConnectAsync(new Uri(url.Replace("http", "ws") + "/room"), default);
                await SocketMessages.SendAsync(socket, new("enter", Text: "Host", Data: Wire.Serialize(new EnterRequest(RoomProtocol.Version, Temporary: true))), default);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var reply = await SocketMessages.ReadAsync(socket, timeout.Token);
                socket.Abort();
                return reply is { Type: "welcome" } ? Wire.Read<Welcome>(reply.Data!).IceServers : ["no welcome: " + reply?.Type];
            }
            await Task.Delay(500);
            health = await http.GetStringAsync(url + "/health");
            check(health.Contains("\"relayConfigured\":false") && health.Contains("\"stunConfigured\":true"), "a failed Cloudflare request at startup reports STUN without relay");
            var fallback = await CreateRoom();
            check(fallback.SequenceEqual(["stun:stun.cloudflare.com:3478"]), $"rooms still open with STUN alone while Cloudflare fails ({string.Join(' ', fallback)})");

            string[] Expected = ["stun:stun.cloudflare.com:3478", "turn://user%2F1:pa%3Ass@turn.cloudflare.com:3478?transport=udp", "turns://user%2F1:pa%3Ass@turn.cloudflare.com:5349?transport=tcp"];
            Volatile.Write(ref failing, 0);
            for (var i = 0; i < 10 && !health.Contains("\"relayConfigured\":true"); i++) { await Task.Delay(300); health = await http.GetStringAsync(url + "/health"); }
            check(health.Contains("\"relayConfigured\":true"), "the server retries Cloudflare in the background and reports relay once it recovers");
            check(requests.Any(r => r.Path == "/turn/keys/key-id/credentials/generate-ice-servers" && r.Authorization == "Bearer api-token" && r.Body.Contains("\"ttl\":4")),
                "the server requests Cloudflare credentials for its configured lifetime with its API token");
            var ice = await CreateRoom();
            check(ice.SequenceEqual(Expected), $"welcomes carry Cloudflare's STUN and TURN servers with escaped credentials and without port 53 ({string.Join(' ', ice)})");
            var before = Volatile.Read(ref served);
            await Task.Delay(6000);
            var idle = await CreateRoom();
            check(Volatile.Read(ref served) > before && idle.SequenceEqual(Expected), $"credentials stay fresh while no rooms are open, so a room created after their lifetime still gets TURN ({string.Join(' ', idle)})");

            async Task<int> Probe(string client)
            {
                using var message = new HttpRequestMessage(HttpMethod.Get, url + "/health"); message.Headers.Add("X-Forwarded-For", client);
                return (int)(await http.SendAsync(message)).StatusCode;
            }
            var statuses = new List<int>();
            for (var i = 0; i < 31; i++) statuses.Add(await Probe("203.0.113.7"));
            check(statuses[^1] == 429 && await Probe("203.0.113.8") == 200, "rate limits apply per client address forwarded by a loopback proxy");
        }
        finally
        {
            if (!server.HasExited) server.Kill(true);
            await server.WaitForExitAsync(); cloudflare.Stop();
            await File.WriteAllTextAsync(Path.Combine(directory, "cloudflare-server.log"), await stdout + await stderr);
        }
    }

    private static async Task HostSurvivesGuests(string url, string directory, Action<bool, string> check)
    {
        var blocked = Path.Combine(directory, "blocked-video.mkv"); Directory.CreateDirectory(blocked);
        await using var host = new RoomClient(); await using var guest = new RoomClient();
        var guestMessages = new ConcurrentQueue<WireMessage>(); guest.Message += guestMessages.Enqueue;
        host.SetHostedLibrary([new MediaItem("blocked", blocked, "Blocked", "Movie")]);
        await host.ConnectAsync(url, "Host");
        await guest.ConnectAsync(url, "Guest", host.Identity!.Room);
        await Wait(() => host.Snapshot!.People.Any(p => p.Id == guest.Identity!.Peer));
        host.Send(new("admit", Target: guest.Identity!.Peer));
        await guest.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(20));
        host.Send(new("catalog-permission", Number: 1));
        await Wait(() => guest.Snapshot?.LibraryBrowsing == true);
        guest.Send(new("catalog-browse", Number: 7, Data: Wire.Serialize(new LibraryBrowseRequest())));
        await Wait(() => guestMessages.Any(m => m.Type == "catalog-page" && m.Number == 7));
        check(Wire.Read<LibraryBrowsePage>(guestMessages.First(m => m.Type == "catalog-page").Data!).Entries.Any(e => e.Id == "blocked"), "offloaded library browse returns the host page");
        guest.Send(new("catalog-watch", Number: 8, Target: "blocked"));
        await Wait(() => guestMessages.Any(m => m.Type == "catalog-error" && m.Number == 8));
        var error = guestMessages.First(m => m.Type == "catalog-error" && m.Number == 8).Text ?? "";
        check(host.IsConnected && guest.IsConnected && !error.Contains(directory, StringComparison.OrdinalIgnoreCase),
            "an unreadable library title reports an error without closing the host room or exposing its path");
        host.Send(new("room-name", Text: "Still open"));
        await Wait(() => guest.Snapshot?.Name == "Still open");
        check(true, "host room controls keep working after the failed library start");

        var hostId = host.Identity.Peer;
        try { for (var i = 0; i < 1500; i++) guest.SendLibrary(hostId, new("library-flood", Number: i)); }
        catch (IOException) { /* The host disconnected the flooding peer. */ }
        await Task.Delay(1500);
        check(host.IsConnected && guest.IsConnected, "a guest message flood never overflows or closes the host's room controls");
        host.Send(new("room-name", Text: "After flood"));
        await Wait(() => guest.DirectControlsReady.IsCompletedSuccessfully && guest.Snapshot?.Name == "After flood", 30);
        check(true, "the flooding guest recovers and receives later host state");
    }

    private static async Task Wait(Func<bool> condition, int seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition()) { if (DateTime.UtcNow > until) throw new TimeoutException("Room server fixture timed out"); await Task.Delay(50); }
    }
}
