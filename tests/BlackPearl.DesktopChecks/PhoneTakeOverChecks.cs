using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using LibVLCSharp.Shared;
using BlackPearl.Core;
using BlackPearl.Desktop;
using Windows.Media;

// A phone taking over the video playing here (PhonePlayback.cs, TakeOverForPhone). The PC's player closes: a stopped
// video stayed loaded, and Play from Space, a headset, Windows' media controls or the phone's Remote tab started it again
// here, where the PC then ignored the phone's progress on it, its finish included.
static class PhoneTakeOverChecks
{
    public static async Task Run(MainWindow window, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        bool Property(string name) => (bool)typeof(MainWindow).GetProperty(name, flags)!.GetValue(window)!;
        var directory = Path.Combine(Path.GetTempPath(), "wr-takeover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Takeover.avi");
        VideoFixture.Write(path, 20);
        var item = new MediaItem("phone-takeover", path, "Takeover", "Movie");
        var otherPath = Path.Combine(directory, "Afterwards.avi");
        VideoFixture.Write(otherPath, 20);
        var other = new MediaItem("phone-takeover-after", otherPath, "Afterwards", "Movie");
        var queue = Field<List<MediaItem>>("queue"); var previousQueue = queue.ToArray(); var previousPosition = Field<int>("queuePosition");
        var empty = (Border)window.FindName("PlayerEmpty");
        var title = (TextBlock)window.FindName("PlayerEmptyTitle");
        var hint = (TextBlock)window.FindName("PlayerEmptyHint");
        try
        {
            queue.Clear(); Set("queuePosition", -1);
            await (Task)Call("PlayLocalItem", item)!;
            var player = Field<MediaPlayer>("player");
            for (var i = 0; i < 50 && !(player.IsPlaying && player.Time > 3000); i++) await Task.Delay(100);
            check(player.IsPlaying && player.Time > 3000, "the fixture video plays before the phone takes it over");

            var takenAt = await (Task<long?>)Call("TakeOverForPhone", item)!;
            check(takenAt is > 3000, $"the phone takes over from where the PC was ({takenAt} ms)");
            check(empty.Visibility == Visibility.Visible && Field<MediaItem?>("playingItem") is null && !Property("HasMediaPlayback"),
                "the PC's player closes once a phone takes its video over");
            check(title.Text == "Playing on your phone" && hint.Visibility == Visibility.Visible && hint.Text.StartsWith("Takeover continues on the phone.") &&
                ((TextBlock)window.FindName("RoomHeading")).Text == "BlackPearl player" && ((TextBlock)window.FindName("RoomSubtitle")).Text == "Continuing on your phone",
                "the closed player says the video went on to the phone, and its header no longer names it as playing here");
            var controls = Field<SystemMediaTransportControls?>("systemMediaControls");
            check(controls is null || !controls.IsEnabled && controls.PlaybackStatus == MediaPlaybackStatus.Closed,
                "Windows' media controls no longer offer the video the phone took over");

            // Play from a headset or a keyboard's media key, and the phone Remote tab's Play: nothing starts here.
            Call("HandleMediaPlayback", true, true);
            Call("HandleMediaPlayback", null, false);
            Call("RequestPlayback", true);
            await Task.Delay(800);
            check(!player.IsPlaying && player.State is not (VLCState.Playing or VLCState.Opening or VLCState.Buffering) && empty.Visibility == Visibility.Visible,
                "Play on the PC after a phone took the video over doesn't start it again here");

            await (Task)Call("PlayLocalItem", other)!;
            for (var i = 0; i < 50 && !player.IsPlaying; i++) await Task.Delay(100);
            await (Task)Call("Disconnect", false, false)!;
            check(title.Text == "Choose a video to play" && hint.Visibility == Visibility.Collapsed,
                "once another video has played, the empty player no longer says a phone has one");

            // A video left paused here while a phone watches it, or watches something else. The PC saved the paused
            // video's place every 5 seconds, over the phone's progress on it, and renewed its "played" time each time,
            // which kept it first in Continue watching on both.
            var store = Field<LibraryStore>("library");
            var pausedPath = Path.Combine(directory, "Paused.avi");
            VideoFixture.Write(pausedPath, 20);
            var paused = new MediaItem("phone-paused-here", pausedPath, "Paused", "Movie");
            store.SetSettings([KeyValuePair.Create("position:" + paused.Id, "0"), KeyValuePair.Create("played:" + paused.Id, "0"), KeyValuePair.Create("watched:" + paused.Id, "false")]);
            await (Task)Call("PlayLocalItem", paused)!;
            for (var i = 0; i < 50 && !(player.IsPlaying && player.Time > 3000); i++) await Task.Delay(100);
            player.SetPause(true);
            for (var i = 0; i < 30 && player.IsPlaying; i++) await Task.Delay(100);
            await Task.Delay(300);
            Call("SavePlaybackPosition", false);
            var pausedAt = long.Parse(store.Setting("position:" + paused.Id) ?? "-1");
            check(!player.IsPlaying && pausedAt > 2000, $"the paused video's place is saved once it pauses ({pausedAt} ms)");
            // What PhoneLibrary.SaveProgress writes for the phone, further on in the same video.
            const string phoneAt = "15000", phonePlayed = "1700000000000";
            store.SetSettings([KeyValuePair.Create("position:" + paused.Id, phoneAt), KeyValuePair.Create("played:" + paused.Id, phonePlayed)]);
            Call("SavePlaybackPosition", false);
            Call("SavePlaybackPosition", false);
            check(store.Setting("position:" + paused.Id) == phoneAt && store.Setting("played:" + paused.Id) == phonePlayed,
                $"a paused video doesn't save its place again over a phone's progress on it (position {store.Setting("position:" + paused.Id)}, played {store.Setting("played:" + paused.Id)})");
            await (Task)Call("Disconnect", false, false)!;
            check(store.Setting("position:" + paused.Id) == phoneAt && store.Setting("played:" + paused.Id) == phonePlayed,
                "closing the paused player keeps the phone's progress on its video");
            await (Task)Call("PlayLocalItem", paused)!;
            for (var i = 0; i < 50 && !(player.IsPlaying && player.Time > 14000); i++) await Task.Delay(100);
            check(player.Time >= 14000, $"the video opened here again resumes where the phone left it ({player.Time} ms)");
            Call("SavePlaybackPosition", false);
            check(long.Parse(store.Setting("position:" + paused.Id) ?? "0") >= 14000 && store.Setting("played:" + paused.Id) != phonePlayed,
                "a video playing here saves its place again as it moves");
            store.SetSettings([KeyValuePair.Create("position:" + paused.Id, "0"), KeyValuePair.Create("played:" + paused.Id, "0"), KeyValuePair.Create("watched:" + paused.Id, "false")]);
        }
        finally
        {
            await (Task)Call("Disconnect", false, false)!;
            queue.Clear(); queue.AddRange(previousQueue); Set("queuePosition", previousPosition);
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
}
