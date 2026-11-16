using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using LibVLCSharp.Shared;
using BlackPearl.Core;
using BlackPearl.Desktop;

static class GuestRecoveryChecks
{
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int length);
    // VLC's own output windows ("VLC (Direct3D11 output)"), which it opens when it has no window to draw into.
    private static int VlcOutputWindows()
    {
        var count = 0; var self = (uint)Environment.ProcessId;
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var process);
            var title = new System.Text.StringBuilder(256);
            if (process == self && IsWindowVisible(handle) && GetWindowText(handle, title, title.Capacity) > 0 && title.ToString().StartsWith("VLC (", StringComparison.Ordinal)) count++;
            return true;
        }, IntPtr.Zero);
        return count;
    }

    public static async Task Run(MainWindow window, string directory, Action<bool, string>? check = null)
    {
        PlaybackDiagnostics.Initialize(Path.Combine(directory, "logs"));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Field(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window);
        object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        check ??= (condition, label) => { if (!condition) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); };
        void Check(bool condition, string label) => check(condition, label);
        async Task Wait(Func<bool> condition)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!condition()) await Task.Delay(25, deadline.Token);
        }
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var url = "http://localhost:" + ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var sdk = File.Exists(".tools/dotnet/dotnet.exe") ? Path.GetFullPath(".tools/dotnet/dotnet.exe") : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var info = new ProcessStartInfo(sdk)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.Environment["BLACKPEARL_DATA"] = Path.Combine(directory, "guest-recovery-server");
        info.ArgumentList.Add(Path.GetFullPath("src/BlackPearl.Server/bin/Release/net10.0/BlackPearl.Server.dll"));
        info.ArgumentList.Add("--urls"); info.ArgumentList.Add(url);
        using var server = Process.Start(info)!;
        var output = server.StandardOutput.ReadToEndAsync(); var errors = server.StandardError.ReadToEndAsync();
        await using var host = new RoomClient();
        host.Status += text => Console.WriteLine("Host: " + text);
        try
        {
            using var http = new System.Net.Http.HttpClient();
            for (var attempt = 0; attempt < 100; attempt++)
            {
                try { using var response = await http.GetAsync(url + "/health"); if (response.IsSuccessStatusCode) break; }
                catch (System.Net.Http.HttpRequestException) { }
                if (attempt == 99) throw new IOException("Guest recovery fixture server did not start");
                await Task.Delay(50);
            }
            var file = Path.Combine(directory, "recovery-video.avi"); VideoFixture.Write(file, 120);
            host.SetHostedFile(file, "Recovery video");
            await host.ConnectAsync(url, "UI recovery host");
            host.Send(new("media", Data: Wire.Serialize(host.HostedMedia!.Media)));
            var guest = (RoomClient)Call("NewRoom")!;
            guest.Status += text => Console.WriteLine("Guest: " + text);
            await guest.ConnectAsync(url, "UI recovery guest", host.Identity!.Room);
            await Wait(() => host.Snapshot!.People.Any(p => p.Id == guest.Identity!.Peer));
            host.Send(new("admit", Target: guest.Identity!.Peer));
            await guest.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(30));
            await Wait(() => Field("bridge") is not null && (bool)Field("ready")!);
            var player = (MediaPlayer)Field("player")!;
            host.Send(new("ready", Data: Wire.Serialize(new PlaybackReadiness(host.HostedMedia!.Media.Id, host.Snapshot!.Playback!.Generation))));
            host.Send(new("playback", Data: Wire.Serialize(new PlaybackState(0, host.HostedMedia.Media.Id, true, 0, 0))));
            await Wait(() => player.IsPlaying && player.Time > 500);
            // Run on its own (--guest-recovery), the window has never shown the player, as when a room is joined from Watch rooms.
            var playerHidden = ((FrameworkElement)window.FindName("RoomPage")).Visibility != Visibility.Visible;
            var strays = VlcOutputWindows();
            Check(player.Hwnd != IntPtr.Zero && strays == 0,
                $"a room video started {(playerHidden ? "with the player hidden" : "in the player")} draws into the player, not a separate VLC window ({strays} found)");
            // Nothing asks for the rest of the video: it downloads on its own while the first seconds play.
            var cache = (StreamCache?)Field("streamCache");
            await Wait(() => cache is not null && cache.StoredBytes >= host.HostedMedia.Media.Length);
            Check(((MediaBridge)Field("bridge")!).BuffersWholeVideo && player.Time < player.Length / 2,
                "a guest downloads the rest of the room's video into its cache file while the start is still playing");
            // The timeline draws what is buffered; a file held to its end reads as buffered to the end.
            var timeline = (System.Windows.Controls.Slider)window.FindName("Timeline");
            await Wait(() => SliderBuffer.GetFraction(timeline) == 1);
            Check(timeline.Value < timeline.Maximum / 2, $"the timeline shows the whole video as buffered while the start is still playing (buffered to {SliderBuffer.GetFraction(timeline):P0}, position {timeline.Value / timeline.Maximum:P0})");
            var priorPosition = player.Time;
            var bridge = Field("bridge"); var loadedMedia = Field("loadedMedia"); var loadGeneration = Field("loadGeneration");
            var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var preserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Observe(WireMessage message)
            {
                if (message.Type == "peer-recovering") window.Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        Check(Equals(Field("loadedMedia"), loadedMedia) && Equals(Field("loadGeneration"), loadGeneration) && ReferenceEquals(Field("bridge"), bridge),
                            "guest interruption retains VLC media, input bridge and load generation");
                        Check(((Border)window.FindName("PlayerEmpty")).Visibility == Visibility.Collapsed && Field("disconnectToast") is null,
                            "brief interruption preserves the video surface and does not open a disconnect alert");
                        Check(player.IsPlaying, "brief host connection loss continues playback from the forward buffer");
                        preserved.TrySetResult();
                    }
                    catch (Exception ex) { preserved.TrySetException(ex); }
                });
                if (message.Type == "peer-recovered") window.Dispatcher.BeginInvoke(() => restored.TrySetResult());
            }
            guest.Message += Observe;
            var peers = (ConcurrentDictionary<string, PeerTransport>)typeof(RoomClient).GetField("peers", flags)!.GetValue(host)!;
            peers[guest.Identity!.Peer].Dispose();
            await preserved.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await restored.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Check(Equals(Field("loadedMedia"), loadedMedia) && Equals(Field("loadGeneration"), loadGeneration) && ReferenceEquals(Field("bridge"), bridge),
                "restored guest connection resumes through the same player input without reloading the movie");
            Check(!guest.IsPeerRecovering && Field("disconnectToast") is null, "successful recovery clears the reconnecting state quietly");
            Check(ReferenceEquals(Field("streamCache"), cache), "the buffered video survives the interruption");
            // A buffer waits only so long for its video. One the player is reading from is not waiting.
            var grace = typeof(MainWindow).GetField("bufferGraceMs", flags)!;
            grace.SetValue(window, 0);
            Call("CloseIdleBuffers"); Call("CloseIdleBuffers");
            Check(ReferenceEquals(Field("streamCache"), cache) && cache!.Accepting, "a buffer the player is reading from is never closed for waiting");
            grace.SetValue(window, 5000);
            await Wait(() => host.Snapshot!.People.Any(p => p.Id == guest.Identity!.Peer && p.Ready));
            Check(true, "the retained player reports readiness again after host readmission");
            await Wait(() => player.IsPlaying && player.Time > priorPosition + 250);
            Check(true, "guest playback automatically resumes and advances after reconnecting");
            guest.Message -= Observe;
            await host.DisposeAsync();
            await Wait(() => guest.Snapshot?.Media is null && Field("loadedMedia") is null);
            Check(!guest.IsPeerRecovering, "an actual host departure clears playback instead of retaining a disconnected movie");
            // The streamer may only have lost the connection, and then starts the video again within moments: what was
            // buffered waits for that, and is closed when the video has not come back.
            await Wait(() => Field("bridge") is null);
            Check(ReferenceEquals(Field("streamCache"), cache) && cache!.Accepting, "with the room's video gone, what was buffered waits for it to come back");
            await Wait(() => Field("streamCache") is null);
            Check(cache!.StoredBytes > 0 && !cache.Accepting, "the cache file closes when the video has stayed away");
            // While this app is itself reconnecting to the room, the wait has not begun: what it returns to decides.
            var kept = StreamCache.TryCreate(Path.Combine(directory, "kept-stream-cache"), new SharedMedia("kept", "Recovery video", 4096, ".avi"))!;
            var reconnecting = typeof(MainWindow).GetField("roomReconnect", flags)!;
            using (var retry = new CancellationTokenSource())
            {
                grace.SetValue(window, 0); reconnecting.SetValue(window, retry);
                typeof(MainWindow).GetField("streamCache", flags)!.SetValue(window, kept);
                Call("CloseIdleBuffers"); Call("CloseIdleBuffers");
                Check(ReferenceEquals(Field("streamCache"), kept) && kept.Accepting, "a buffer is kept for as long as this app is reconnecting to the room");
                reconnecting.SetValue(window, null);
            }
            Call("CloseIdleBuffers"); Call("CloseIdleBuffers");
            Check(Field("streamCache") is null && !kept.Accepting, "and closed when the room it returns to does not play that video");
        }
        finally
        {
            typeof(MainWindow).GetField("bufferGraceMs", flags)!.SetValue(window, 120000);
            await (Task)Call("Disconnect", false, false)!;
            if (!server.HasExited) { server.Kill(true); await server.WaitForExitAsync(); }
            await File.WriteAllTextAsync(Path.Combine(directory, "guest-recovery-errors.log"), await errors);
            await File.WriteAllTextAsync(Path.Combine(directory, "guest-recovery-server.log"), await output);
            await PlaybackDiagnostics.FlushAsync();
        }
    }
}
