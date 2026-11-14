using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using BlackPearl.Core;
using BlackPearl.Desktop;

// Closing the app while a video plays. The player's controls and its empty state are drawn in LibVLC's own
// see-through window over the video, which follows the main window's moves and closing but not its hiding.
// Once the main window is hidden, nothing of the player may stay on screen while playback stops: the empty
// player ("Choose a video to play") flashed on the desktop before the app closed.
static class PlayerCloseChecks
{
    public static async Task Run(MainWindow window, string directory, string way, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        var path = Path.Combine(directory, "Closing S01E01.avi");
        VideoFixture.Write(path, 20);
        var item = new MediaItem("player-close-1", path, "Closing", "Show", Series: "Closing", Season: 1, Episode: 1);
        typeof(MainWindow).GetField("items", flags)!.SetValue(window, Field<List<MediaItem>>("items").Append(item).ToList());
        Call("ShowPage", "Library");
        await (Task)Call("PlayLocalItem", item)!;
        var native = Field<MediaPlayer>("player");
        for (var i = 0; i < 50 && !(native.IsPlaying && native.Time > 300); i++) await Task.Delay(100);
        var pictureInPicture = way == "picture in picture"; var mode = "in " + way;
        check(native.IsPlaying, $"the fixture video plays {mode} before the app closes");
        if (pictureInPicture)
        {
            Call("SetPictureInPicture", true, true);
            await Task.Delay(500);
            check(Field<Window>("pictureInPictureWindow").IsVisible, "the video plays in the small window before the app closes");
        }
        if (way == "fullscreen")
        {
            // From a maximized window: leaving fullscreen while closing maximizes the hidden window again.
            window.WindowState = WindowState.Maximized;
            await Task.Delay(300);
            Call("SetFullscreen", true);
            await Task.Delay(500);
            check(Field<bool>("fullscreen") && Field<WindowState>("previousState") == WindowState.Maximized, "the video plays fullscreen, entered from a maximized window, before the app closes");
        }
        Call("ShowPlayerControls");
        var empty = (UIElement)window.FindName("PlayerEmpty");
        var overlay = Window.GetWindow((UIElement)window.FindName("PlayerOverlay"));
        check(overlay is not null && overlay != window && IsWindowVisible(new WindowInteropHelper(overlay).Handle),
            $"the player's controls are drawn in LibVLC's own window {mode}");

        // Every moment the main window is hidden while the app closes, LibVLC's window must be hidden too.
        // A timer at the highest priority looks between the closing steps; the empty state is checked as it appears.
        var seen = new List<string>(); var hidden = false;
        void Look(string moment)
        {
            if (!hidden && !window.IsVisible) hidden = true;
            if (!hidden) return;
            if (IsWindowVisible(new WindowInteropHelper(window).Handle)) seen.Add("the main window again " + moment);
            if (Window.GetWindow(empty) is { } host && host != window && IsWindowVisible(new WindowInteropHelper(host).Handle))
                seen.Add("LibVLC's window " + moment + (empty.Visibility == Visibility.Visible ? " (showing the empty player)" : ""));
            if (typeof(MainWindow).GetField("pictureInPictureWindow", flags)!.GetValue(window) is Window small && IsWindowVisible(new WindowInteropHelper(small).Handle))
                seen.Add("the small window " + moment);
        }
        var poll = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(1) };
        poll.Tick += (_, _) => Look("while playback stopped");
        var visibility = DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, typeof(UIElement));
        EventHandler emptyShown = (_, _) => { if (empty.Visibility == Visibility.Visible) Look("as the empty player appeared"); };
        visibility.AddValueChanged(empty, emptyShown);
        var closed = new TaskCompletionSource();
        window.Closed += (_, _) => closed.TrySetResult();
        poll.Start();
        // The docked player's own close button in its top bar, as the user presses it; otherwise Alt+F4's WM_CLOSE.
        var closeButton = (System.Windows.Controls.Button)window.FindName("PlayerCloseButton");
        if (way == "the full player")
        {
            check(closeButton.IsVisible, "the docked player shows its own close button");
            _ = window.Dispatcher.BeginInvoke(() => ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(closeButton)
                .GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke());
        }
        else PostMessage(new WindowInteropHelper(window).Handle, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
        try { await closed.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { poll.Stop(); visibility.RemoveValueChanged(empty, emptyShown); }
        check(seen.Count == 0, $"closing the app {mode} leaves nothing of the player on screen once the window is hidden" +
            (seen.Count > 0 ? $" (seen {seen.Count} times: {string.Join("; ", seen.Distinct())})" : ""));
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
