using System.IO;
using System.Collections;
using System.Reflection;
using System.Windows.Controls;
using LibVLCSharp.Shared;
using BlackPearl.Desktop;
using BlackPearl.Core;

static class TrackPreferenceChecks
{
    public static void Run(string video)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Core.Initialize();
        using var engine = new LibVLC("--quiet", "--vout=dummy", "--aout=dummy");
        using var player = new MediaPlayer(engine);
        using var media = new Media(engine, Path.GetFullPath(video), FromType.FromPath);
        var window = new MainWindow();
        void Field(string name, object? value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        object? Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
        void Wait(Func<bool> condition)
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!condition() && DateTime.UtcNow < until) Thread.Sleep(25);
            if (!condition()) throw new Exception("Native track selection timed out");
        }
        var audioInput = (LanguagePriorityPicker)window.FindName("PreferredAudioLanguages");
        var subtitlesInput = (LanguagePriorityPicker)window.FindName("PreferredSubtitleLanguages");
        var rules = (IList)typeof(MainWindow).GetField("subtitleRuleDrafts", flags)!.GetValue(window)!;
        void AddRule(string audio, string subtitles)
        {
            Call("AddSubtitleRule", window, new System.Windows.RoutedEventArgs());
            var rule = rules[^1]!;
            rule.GetType().GetProperty("AudioLanguage")!.SetValue(rule, audio);
            rule.GetType().GetProperty("SubtitleLanguages")!.SetValue(rule, subtitles);
        }
        try
        {
            audioInput.Text = "Russian, English"; subtitlesInput.Text = "English, off";
            Check((bool)Call("SaveTrackPreferences")!, "ordered audio and subtitle preferences save successfully");
            audioInput.Clear(); subtitlesInput.Clear(); Call("LoadTrackPreferences");
            Check(audioInput.Text == "ru, en" && subtitlesInput.Text == "en, off", "saved language preferences reload in the same order");
            Field("player", player); Field("vlc", engine); Field("loadGeneration", 1);
            player.Play(media);
            Wait(() => player.AudioTrackDescription.Count(t => t.Id >= 0) == 2 && player.SpuDescription.Count(t => t.Id >= 0) == 2);
            Field("ready", true);
            var tracks = media.Tracks;
            var russian = tracks.Single(t => t.TrackType == TrackType.Audio && t.Language == "rus").Id;
            var english = tracks.Single(t => t.TrackType == TrackType.Audio && t.Language == "eng").Id;
            var subtitle = tracks.Single(t => t.TrackType == TrackType.Text && t.Language == "eng").Id;
            Call("RefreshTracks"); Wait(() => player.AudioTrack == russian && player.Spu == subtitle);
            Check(true, "native playback picks preferred audio and subtitles from stream language metadata");
            var picker = (ListBox)window.FindName("AudioTracks");
            picker.SelectedItem = picker.Items.Cast<object>().Single(t => (int)t.GetType().GetProperty("Id")!.GetValue(t)! == english);
            Call("RefreshTracks"); Wait(() => player.AudioTrack == english);
            Check(true, "manual track selection survives periodic player refreshes");
            Field("loadGeneration", 2); Call("RefreshTracks"); Wait(() => player.AudioTrack == russian);
            Check(true, "a new video applies preferred languages again");
            subtitlesInput.Text = "off, English"; Call("SaveTrackPreferences");
            Field("loadGeneration", 3); Call("RefreshTracks"); Wait(() => player.Spu == -1);
            Check(true, "off preference disables native subtitles");
            subtitlesInput.Text = "English, off";
            AddRule("Russian", "off"); AddRule("English", "Russian, English, off");
            Check((bool)Call("SaveTrackPreferences")!, "audio-specific subtitle lists save with the default priority");
            Field("loadGeneration", 4); Call("RefreshTracks"); Wait(() => player.AudioTrack == russian && player.Spu == -1);
            Check(true, "native playback uses the selected audio language's subtitle rule");
            picker.SelectedItem = picker.Items.Cast<object>().Single(t => (int)t.GetType().GetProperty("Id")!.GetValue(t)! == english);
            var russianSubtitle = tracks.Single(t => t.TrackType == TrackType.Text && t.Language == "rus").Id;
            Call("RefreshTracks"); Wait(() => player.AudioTrack == english && player.Spu == russianSubtitle);
            Check(true, "manual audio changes apply the matching subtitle priority");
            var subtitlePicker = (ListBox)window.FindName("SubtitleTracks");
            subtitlePicker.SelectedItem = subtitlePicker.Items.Cast<object>().Single(t => (int)t.GetType().GetProperty("Id")!.GetValue(t)! == subtitle);
            picker.SelectedItem = picker.Items.Cast<object>().Single(t => (int)t.GetType().GetProperty("Id")!.GetValue(t)! == russian);
            Call("RefreshTracks"); Wait(() => player.AudioTrack == russian && player.Spu == subtitle);
            Check(true, "manual subtitle selection survives audio changes and periodic refreshes");
            rules.Clear(); AddRule("Japanese", "off"); Call("SaveTrackPreferences");
            Call("RefreshTracks"); Wait(() => player.Spu == subtitle);
            Check(true, "saving new rules leaves the current video's manual subtitle choice intact");
            Field("loadGeneration", 5); Call("RefreshTracks"); Wait(() => player.AudioTrack == russian && player.Spu == subtitle);
            Check(true, "a new video uses default subtitles for audio without a configured rule");
            audioInput.Text = "Japanese, English"; rules.Clear(); AddRule("English", "off"); Call("SaveTrackPreferences");
            Field("loadGeneration", 6); Call("RefreshTracks"); Wait(() => player.AudioTrack == english && player.Spu == -1);
            Check(true, "subtitle rules follow the actual audio fallback when the first audio language is unavailable");
            audioInput.Text = "Russian, English"; rules.Clear(); AddRule("Russian", "off"); AddRule("English", ""); Call("SaveTrackPreferences");
            player.SetSpu(subtitle); Wait(() => player.Spu == subtitle);
            Field("loadGeneration", 7); Call("RefreshTracks"); Wait(() => player.AudioTrack == russian && player.Spu == -1);
            picker.SelectedItem = picker.Items.Cast<object>().Single(t => (int)t.GetType().GetProperty("Id")!.GetValue(t)! == english);
            Call("RefreshTracks"); Wait(() => player.Spu == subtitle);
            Check(true, "switching to a blank subtitle rule restores the video's original subtitle default");
            PhoneRemoteChecks(window, player, english, russian, subtitle, Check, Wait);
            audioInput.Text = "Not a language";
            Check(!(bool)Call("SaveTrackPreferences")!, "invalid settings are rejected without replacing saved preferences");
            player.Stop(); Field("player", null);
            var client = new RoomClient();
            var identity = new Welcome(new string('A', 24), "host", true, [], false);
            typeof(RoomClient).GetProperty("Identity")!.SetValue(client, identity);
            var coordinatorType = typeof(RoomClient).Assembly.GetType("BlackPearl.Core.HostRoomCoordinator")!;
            var coordinator = Activator.CreateInstance(coordinatorType, "host")!;
            coordinatorType.GetMethod("Discover")!.Invoke(coordinator, [new Participant[] { new("host", "Host", true, true) }, Wire.Now]);
            typeof(RoomClient).GetField("coordinator", flags)!.SetValue(client, coordinator);
            var control = Task.Run(() => (Task)typeof(RoomClient).GetMethod("ControlLoop", flags)!.Invoke(client, null)!);
            typeof(RoomClient).GetField("controlTask", flags)!.SetValue(client, control);
            Field("room", client);
            var first = new MediaItem("first", Path.GetFullPath(video), "Watch together fixture", "Movie");
            var second = first with { Id = "second", Title = "Queue fixture" };
            try
            {
                Call("StartHostedVideo", first);
                Wait(() => client.Snapshot?.Media?.Title == first.Title && client.Snapshot.Playback?.WantsPlayback == true);
                Check(true, "Watch Together selection requests playback automatically");
                client.Send(new("ready")); Wait(() => client.Snapshot?.Playback?.Playing == true);
                Check(true, "Watch Together starts when participants become ready");
                Field("queue", new List<MediaItem> { first, second }); Field("queuePosition", 0);
                ((Task)Call("PlayQueueStep", 1)!).GetAwaiter().GetResult();
                Wait(() => client.Snapshot?.Media?.Title == second.Title && client.Snapshot.Playback?.WantsPlayback == true);
                Check(true, "queue transition selects the next video and requests playback automatically");
                client.Send(new("ready")); Wait(() => client.Snapshot?.Playback?.Playing == true);
                Check(true, "next queued video starts when participants become ready");
            }
            finally { Field("room", null); Task.Run(() => client.DisposeAsync().AsTask()).GetAwaiter().GetResult(); }
        }
        finally { Field("player", null); Field("vlc", null); Field("closing", true); window.Close(); }
    }
    // BlackPearl Mobile's remote (PHONE-REMOTE.md, "Tracks"): the state carries the player's Audio and Subtitles menus as
    // they show, and `audio-track` and `subtitle-track` choose as a click in them does.
    static void PhoneRemoteChecks(MainWindow window, MediaPlayer player, int english, int russian, int subtitle, Action<bool, string> check, Action<Func<bool>> wait)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        object? Item(object tuple, int n) => tuple.GetType().GetField("Item" + n)!.GetValue(tuple);
        (string Code, string Message)? Command(string action, int id) =>
            ((string, string)?)Call("RunPhoneTrackCommand", new RemoteCommand(action, System.Text.Json.JsonDocument.Parse(id.ToString(System.Globalization.CultureInfo.InvariantCulture)).RootElement.Clone()));
        var empty = (System.Windows.FrameworkElement)window.FindName("PlayerEmpty");
        var emptyBefore = empty.Visibility;
        try
        {
            check(Command("audio-track", russian) is { Code: "nothing-playing" }, "a phone's track choice with nothing in the player is refused as nothing playing");
            empty.Visibility = System.Windows.Visibility.Collapsed;
            Call("RefreshTracks");
            var tracks = Call("PhoneRemoteTracks")!;
            var audio = (RemoteTrack[])Item(tracks, 1)!; var subtitles = (RemoteTrack[])Item(tracks, 3)!;
            check(audio.Count(t => t.Id >= 0) == 2 && audio.Any(t => t.Id == english && t.Name == "English") && audio.Any(t => t.Id == russian && t.Name == "Russian")
                && (int?)Item(tracks, 2) == english && (bool)Item(tracks, 5)!,
                "the phone's state lists the audio menu's tracks by language, with the playing one");
            check(subtitles.FirstOrDefault() is { Id: -1, Name: "Off", Detail: null } && subtitles.Any(t => t.Id == subtitle) && (int?)Item(tracks, 4) == subtitle,
                "the phone's subtitle list starts with Off and ticks the subtitles shown");
            var state = Wire.Serialize(Call("PhoneRemoteState")!);
            check(state.Contains("\"audioTracks\":[{\"id\":" + audio[0].Id) && state.Contains("\"subtitleTrack\":" + subtitle) && state.Contains("\"canChangeTracks\":true"),
                "the state message names the track fields as PHONE-REMOTE.md does");
            check(ReferenceEquals(Item(Call("PhoneRemoteTracks")!, 1), audio), "an unchanged menu is not converted again on every state");
            check(Command("audio-track", russian) is null, "a phone picks another audio track");
            wait(() => player.AudioTrack == russian && player.Spu == -1);
            check(true, "a phone's audio choice also applies the subtitle rule for its language, as the menu does");
            // The player refreshes its menus when a track is selected (its ES events), which this check does by hand.
            Call("RefreshTracks");
            check((int?)Item(Call("PhoneRemoteTracks")!, 2) == russian && (int?)Item(Call("PhoneRemoteTracks")!, 4) == -1, "the next state ticks the phone's audio choice and Off");
            check(Command("subtitle-track", subtitle) is null, "a phone picks subtitles");
            wait(() => player.Spu == subtitle);
            check(true, "the phone's subtitle choice shows on the PC");
            check(Command("subtitle-track", 9999) is null && player.Spu == subtitle && Command("audio-track", 9999) is null && player.AudioTrack == russian,
                "an id the menu doesn't have changes nothing");
            typeof(MainWindow).GetField("nativeStopCount", flags)!.SetValue(window, 1);
            var refused = Command("audio-track", english);
            check(refused is { Code: "not-allowed" } && player.AudioTrack == russian && !(bool)Item(Call("PhoneRemoteTracks")!, 5)!,
                "while the menus are greyed out a phone's choice is refused and the state says so");
            typeof(MainWindow).GetField("nativeStopCount", flags)!.SetValue(window, 0);
            check(Command("audio-track", english) is null, "the phone's audio choice works again once the menus do");
            wait(() => player.AudioTrack == english);
        }
        finally { empty.Visibility = emptyBefore; }
    }
}
