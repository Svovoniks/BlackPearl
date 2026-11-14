using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using BlackPearl.Core;

// Rooms without a host, over a real room service and real peer connections: entering with only the invitation, a
// direct connection between every pair of members, anyone starting a video, the streaming role moving when someone
// else starts one or the streamer leaves, and what the room's creator alone can do.
static class HostlessRoomFixture
{
    public static async Task Run(string sdk, string serverDll, string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        // The service allows each address 30 requests a minute, so each part runs against a service of its own.
        await WithServer(sdk, serverDll, directory, "streaming", async url =>
        {
            using var http = new HttpClient();
            check((await http.GetStringAsync(url + "/health")).Contains("\"protocol\":2"), "the room service reports the protocol for rooms without a host");
            await Streaming(url, directory, check);
        });
        await WithServer(sdk, serverDll, directory, "membership", url => Membership(url, directory, check));
        await WithServer(sdk, serverDll, directory, "crowd", url => Crowd(url, check));
    }

    internal static async Task WithServer(string sdk, string serverDll, string directory, string name, Func<string, Task> run)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var url = "http://localhost:" + ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var start = new ProcessStartInfo(sdk) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["BLACKPEARL_DATA"] = Path.Combine(directory, "server-" + name);
        start.ArgumentList.Add(serverDll); start.ArgumentList.Add("--urls"); start.ArgumentList.Add(url);
        using var server = Process.Start(start)!;
        var stdout = server.StandardOutput.ReadToEndAsync(); var stderr = server.StandardError.ReadToEndAsync();
        try
        {
            using var http = new HttpClient();
            for (var i = 0; i < 100; i++) { try { if ((await http.GetAsync(url + "/health")).IsSuccessStatusCode) break; } catch (HttpRequestException) { } await Task.Delay(200); }
            await run(url);
        }
        finally
        {
            if (!server.HasExited) server.Kill(true);
            await server.WaitForExitAsync();
            await File.WriteAllTextAsync(Path.Combine(directory, $"server-{name}.log"), await stdout + await stderr);
        }
    }

    // A full room turns the next person away. Plain sockets keep this within the service's request limit.
    private static async Task Crowd(string url, Action<bool, string> check)
    {
        var sockets = new List<ClientWebSocket>();
        async Task<WireMessage?> Enter(string name, string? room)
        {
            var socket = new ClientWebSocket(); sockets.Add(socket);
            await socket.ConnectAsync(new Uri(url.Replace("http", "ws") + "/room"), default);
            await SocketMessages.SendAsync(socket, new("enter", Text: name, Data: Wire.Serialize(new EnterRequest(RoomProtocol.Version, room, Temporary: true))), default);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await SocketMessages.ReadAsync(socket, timeout.Token);
        }
        try
        {
            var room = Wire.Read<Welcome>((await Enter("Member 1", null))!.Data!).Room;
            for (var i = 2; i <= RoomProtocol.MaxPresent; i++)
                if ((await Enter("Member " + i, room))?.Type != "welcome") throw new Exception("FAIL: member " + i + " could not enter");
            var extra = await Enter("One too many", room);
            check(extra is { Type: "error" } && extra.Text!.StartsWith("Room is full", StringComparison.Ordinal), $"a room holds {RoomProtocol.MaxPresent} people at once and turns the next one away");
        }
        finally { foreach (var socket in sockets) { socket.Abort(); socket.Dispose(); } }
    }

    private static string WriteVideo(string directory, string name, byte seed)
    {
        var path = Path.Combine(directory, name);
        var bytes = new byte[300000]; new Random(seed).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static async Task Streaming(string url, string directory, Action<bool, string> check)
    {
        var first = WriteVideo(directory, "first.mkv", 1); var second = WriteVideo(directory, "second.mkv", 2);
        var firstBytes = await File.ReadAllBytesAsync(first); var secondBytes = await File.ReadAllBytesAsync(second);
        await using var a = new RoomClient(); await using var b = new RoomClient(); await using var c = new RoomClient();
        var aMessages = new ConcurrentQueue<WireMessage>(); var bMessages = new ConcurrentQueue<WireMessage>(); var cMessages = new ConcurrentQueue<WireMessage>();
        a.Message += aMessages.Enqueue; b.Message += bMessages.Enqueue; c.Message += cMessages.Enqueue;

        await a.ConnectAsync(url, "Ann");
        check(a.Identity is { Host: true, Owner: true } && a.DirectControlsReady.IsCompletedSuccessfully, "whoever creates a room is its creator and, alone in it, holds the streaming role");
        var entering = Stopwatch.StartNew();
        await b.ConnectAsync(url, "Ben", a.Identity!.Room);
        await b.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(25));
        check(b.Identity is { Host: false, Owner: false }, $"someone with the invitation is in the room without anyone letting them in ({entering.ElapsedMilliseconds} ms to room state)");
        await Wait(() => a.Snapshot?.People.Length == 2 && b.Snapshot?.People.Length == 2, "both members list each other");
        check(a.Snapshot!.People.Single(p => p.IsHost).Id == a.Identity.Peer && b.Snapshot!.People.Single(p => p.IsHost).Id == a.Identity.Peer, "everyone agrees on who holds the streaming role");

        await a.StartStreamAsync(RoomClient.PrepareHostedFile(first, "First film"), true);
        await Wait(() => b.Snapshot?.Media?.Title == "First film", "a video started by the first member reaches the second");
        var fromA = await b.GetSourceAsync(b.Snapshot!.Media!, CancellationToken.None);
        check((await fromA.ReadAsync(1000, 500, CancellationToken.None)).SequenceEqual(firstBytes[1000..1500]), "the second member reads the video from the first");

        await c.ConnectAsync(url, "Cy", a.Identity.Room);
        await c.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(25));
        await Wait(() => new[] { a, b, c }.All(client => Negotiated(client).Count == 2), "every member holds a direct connection to every other member");
        check(c.Snapshot?.Media?.Title == "First film", "someone entering mid-video sees what is playing");

        // Anyone can play, pause and seek unless whoever streams turns that off.
        a.Send(new("ready")); b.Send(new("ready")); c.Send(new("ready"));
        await Wait(() => a.Snapshot!.People.All(p => p.Ready), "everyone reports ready");
        c.Send(new("playback", Data: Wire.Serialize(new PlaybackState(0, a.Snapshot!.Media!.Id, true, 4000, 0))));
        await Wait(() => a.Snapshot?.Playback is { Playing: true, PositionMs: 4000 } && b.Snapshot?.Playback is { Playing: true, PositionMs: 4000 }, "a viewer's play command moves the room by default");
        a.Send(new("controls", Number: 0));
        await Wait(() => c.Snapshot?.SharedControls == false, "the streamer can keep playback to itself");
        c.Send(new("playback", Data: Wire.Serialize(new PlaybackState(0, a.Snapshot!.Media!.Id, false, 9000, 0))));
        await Task.Delay(300);
        check(a.Snapshot!.Playback is { Playing: true, PositionMs: 4000 }, "a viewer's command is refused while the streamer keeps playback to itself");

        // Someone else starts a video: the streaming role moves to them and everyone follows it.
        var switching = Stopwatch.StartNew();
        await b.StartStreamAsync(RoomClient.PrepareHostedFile(second, "Second film"), true);
        check(b.Identity!.Host, "starting a video gives its member the streaming role");
        await Wait(() => a.Snapshot?.Media?.Title == "Second film" && c.Snapshot?.Media?.Title == "Second film" && a.Identity!.Host == false, "everyone, including the previous streamer, follows the new video");
        var switched = switching.ElapsedMilliseconds;
        check(new[] { a, b, c }.All(client => client.Snapshot!.People.Single(p => p.IsHost).Id == b.Identity.Peer), $"everyone agrees the streaming role moved ({switched} ms from starting the video to everyone seeing it)");
        check(!aMessages.Concat(cMessages).Any(m => m.Type == "snapshot" && Wire.Read<RoomSnapshot>(m.Data!) is { Media: null } state && state.People.Any(p => p.IsHost && p.Id == b.Identity.Peer)),
            "the new streamer's first state already carries its video, so nobody sees an empty room in between");
        check(aMessages.Any(m => m is { Type: "role", Number: 0 }) && bMessages.Any(m => m is { Type: "role", Number: 1 }), "both sides of the handover are told their role changed");
        var fromB = await c.GetSourceAsync(c.Snapshot!.Media!, CancellationToken.None);
        check((await fromB.ReadAsync(2000, 700, CancellationToken.None)).SequenceEqual(secondBytes[2000..2700]), "a viewer reads the new video from the new streamer");
        var fromBAtA = await a.GetSourceAsync(a.Snapshot!.Media!, CancellationToken.None);
        check((await fromBAtA.ReadAsync(0, 400, CancellationToken.None)).SequenceEqual(secondBytes[..400]), "the previous streamer reads the new video too");
        var stale = false;
        try { await fromA.ReadAsync(0, 100, CancellationToken.None); } catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) { stale = true; }
        check(stale, "the previous streamer's video is no longer served once the role moved");
        check(Negotiated(a).Count == 2 && Negotiated(c).Count == 2, "no connection was rebuilt for the handover");
        await Wait(() => c.Snapshot?.SharedControls == true, "the new streamer's own choice about shared playback applies to its video");

        // The streamer leaves: its video ends, the room stays, and the role passes to whoever has been there longest.
        var leaving = b.Identity.Peer;
        await b.DisposeAsync();
        await Wait(() => a.Identity!.Host && c.Snapshot?.People.Length == 2 && c.Snapshot.People.Single(p => p.IsHost).Id == a.Identity.Peer, "when the streamer leaves, the role passes to whoever has been in the room longest");
        check(a.Snapshot!.Media is null && c.Snapshot!.Media is null, "the streamer's video ends for everyone when it leaves");
        check(cMessages.Any(m => m.Type == "stream-ended" && m.Sender == leaving) && cMessages.Any(m => m.Type == "peer-left" && m.Sender == leaving && m.Text == "Ben"), "viewers are told the stream ended and who left");
        await Wait(() => c.DirectControlsReady.IsCompletedSuccessfully, "the remaining members are connected to the new holder of the role");
        await c.StartStreamAsync(RoomClient.PrepareHostedFile(first, "First film again"), false);
        await Wait(() => a.Snapshot?.Media?.Title == "First film again" && !a.Identity!.Host, "the room carries on: another member starts the next video");

        // The creator leaves; the room does not close.
        await a.DisposeAsync();
        await Wait(() => c.Snapshot?.People.Length == 1, "the creator leaving does not close the room");
        check(c.IsConnected && c.Snapshot?.Media?.Title == "First film again", "a video someone else is streaming keeps playing after the creator leaves");
    }

    private static async Task Membership(string url, string directory, Action<bool, string> check)
    {
        var ownerSettings = new LibraryStore(Path.Combine(directory, "owner")); var guestSettings = new LibraryStore(Path.Combine(directory, "guest"));
        var owner = new RoomClient(ownerSettings);
        await owner.ConnectAsync(url, "Olga", persistent: true, roomName: "Friday night");
        var room = owner.Identity!.Room; var ownerKey = owner.Identity.HostKey;
        check(ownerKey?.Length == 64 && owner.Snapshot?.Name == "Friday night", "the creator gets the key that marks the room as theirs, and the room has the name they gave it");
        var guest = new RoomClient(guestSettings);
        var guestMessages = new ConcurrentQueue<WireMessage>(); guest.Message += guestMessages.Enqueue;
        await guest.ConnectAsync(url, "Gil", room);
        await guest.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(25));
        check(guest.Identity is { HostKey: null, GuestKey.Length: 64 } && guest.Snapshot?.Name == "Friday night", "a member gets a key of their own, never the creator's");
        await Wait(() => owner.Members.Length == 2 && guest.Members.Length == 2 && owner.Roster.Length == 2, "everyone sees who belongs to the room");

        var presence = await RoomClient.PresenceAsync(url, [room, new string('0', 24)], CancellationToken.None);
        check(presence.Count == 1 && presence[room] is { Name: "Friday night" } listed && listed.People.SequenceEqual(["Olga", "Gil"]), "someone holding the invitation can see who is in the room before entering");

        var refused = false;
        try { guest.Send(new("room-name", Text: "Gil's room")); } catch (IOException) { refused = true; }
        check(refused, "only the creator can rename the room");
        owner.Send(new("room-name", Text: "Saturday night"));
        await Wait(() => guest.Snapshot?.Name == "Saturday night" && owner.Snapshot?.Name == "Saturday night", "the creator's rename reaches everyone");

        // The creator is not needed for the room to work.
        await owner.DisposeAsync();
        await Wait(() => guest.Identity!.Host && guest.Snapshot?.People.Length == 1, "with the creator gone, a member holds the streaming role");
        await using (var late = new RoomClient())
        {
            await late.ConnectAsync(url, "Lea", room);
            await late.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(25));
            check(late.Snapshot?.Name == "Saturday night" && late.Snapshot.People.Length == 2, "new people enter and connect while the creator is away");
        }
        await Wait(() => guest.Snapshot?.People.Length == 1);

        // Entering again from the same app replaces the earlier connection instead of being turned away.
        var again = new RoomClient(guestSettings);
        await again.ConnectAsync(url, "Gil", room);
        await Wait(() => guest.ClosedReason is not null && !guest.IsConnected, "entering again replaces that member's earlier connection");
        check(again.Identity!.GuestKey == guest.Identity!.GuestKey && again.Roster.Length == 1, "the member keeps their key and is listed once");
        await guest.DisposeAsync();

        // The creator returns with their key, renames while entering, and removes someone.
        owner = new RoomClient(ownerSettings);
        await owner.ConnectAsync(url, "Olga", room, ownerKey, roomName: "Sunday night");
        check(owner.Identity is { Owner: true, Host: false }, "the creator returns as creator; the streaming role stays with whoever holds it");
        await owner.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(25));
        await Wait(() => again.Snapshot?.Name == "Sunday night", "a name the creator changed while away applies when they return");
        await using (var impostor = new RoomClient())
        {
            await impostor.ConnectAsync(url, "Olga", room, new string('A', 64));
            check(impostor.Identity is { Owner: false, HostKey: null }, "the creator's name without their key gives no creator rights");
        }
        var removedId = owner.Roster.Single(p => p.Id == again.Identity.Peer).GuestId!;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        again.Status += text => { if (text.StartsWith("Closed", StringComparison.Ordinal)) closed.TrySetResult(); };
        owner.Send(new("remove", Target: again.Identity.Peer));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        check(again.ClosedReason == "You were removed from this room.", "a removed member is told why their connection ended");
        await Wait(() => owner.Removed.Any(r => r.Id == removedId && r.Name == "Gil") && owner.Members.All(m => m.Id != removedId) && owner.Snapshot?.People.Length == 1, "the creator sees who was removed");
        var turnedAway = false;
        await using (var returning = new RoomClient(guestSettings))
        {
            try { await returning.ConnectAsync(url, "Gil", room); } catch (RoomRejectedException) { turnedAway = true; }
        }
        check(turnedAway, "a removed member can't enter again with the invitation");
        owner.Send(new("restore", Target: removedId));
        await Wait(() => owner.Removed.Length == 0, "the creator can let a removed member back in");
        await using (var back = new RoomClient(guestSettings))
        {
            await back.ConnectAsync(url, "Gil", room);
            await back.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(25));
            check(back.Snapshot?.People.Length == 2, "the restored member enters again");
        }
        await again.DisposeAsync();

        // Apps from before rooms lost their host are told to update rather than left waiting.
        using (var old = new ClientWebSocket())
        {
            await old.ConnectAsync(new Uri(url.Replace("http", "ws") + "/room"), default);
            await SocketMessages.SendAsync(old, new("join", Text: "Old app", Data: room), default);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var reply = await SocketMessages.ReadAsync(old, timeout.Token);
            check(reply is { Type: "error" } && reply.Text!.Contains("Update the app"), "an app from before rooms lost their host is told to update");
        }
        var unknown = false;
        await using (var lost = new RoomClient())
        {
            try { await lost.ConnectAsync(url, "Nobody", new string('F', 24)); } catch (RoomRejectedException) { unknown = true; }
        }
        check(unknown, "an invitation the service doesn't know is turned down for good");
        await owner.DisposeAsync();
    }

    internal static ConcurrentDictionary<string, byte> Negotiated(RoomClient client) =>
        (ConcurrentDictionary<string, byte>)typeof(RoomClient).GetField("negotiated", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!;

    private static async Task Wait(Func<bool> condition, string? name = null, int seconds = 25)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition()) { if (DateTime.UtcNow > until) throw new TimeoutException("Timeout: " + (name ?? "hostless room fixture")); await Task.Delay(40); }
        if (name is not null) Console.WriteLine("PASS: " + name);
    }
}
