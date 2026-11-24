using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BlackPearl.Core;
using LibVLCSharp.Shared;

namespace BlackPearl.Desktop;

// Settings > Profile > Phone remote, and the PC's side of BlackPearl Mobile (PHONE-REMOTE.md). Paired phones send the same
// commands as the player's keys and buttons, so a room's control rules apply to them the same way, and they get the
// player's state as it changes.
public partial class MainWindow
{
    private PhoneRemoteHost? phoneRemote;
    private string? phoneRemoteServer;
    private DispatcherTimer? phoneRemoteTimer;
    private string? phoneRemoteSentKey;
    private long phoneRemoteSentAt, phoneRemoteSentPosition;
    private string? phoneRemoteKey;
    private Window? pairingWindow;

    private bool PhoneRemoteEnabled => PhoneRemoteSettings.Enabled(library);
    private bool PhonesCanStart => PhoneRemoteSettings.PhonesCanStart(library);
    private static string PcName => PhoneRemoteSettings.PcName;

    // Whether closing BlackPearl starts the phone host (PhoneHost.cs), worked out before closing stops the link. Not when
    // it closes to install an update, which needs every copy of the app gone, or when Windows is signing out.
    // A sign-out the user cancels leaves the flag behind, so it counts for a minute.
    private bool closingForUpdate;
    private long sessionEndingAt = long.MinValue / 2;
    private bool SessionEnding => Environment.TickCount64 - sessionEndingAt < 60000;
    private bool KeepsPhonesAfterClosing => PhoneRemoteEnabled && PhoneRemoteSettings.WhileClosed(library) && !closingForUpdate && !SessionEnding &&
        OpenFileHandoff.Claimed && PhoneRemoteSettings.HostKey(library) is not null && PhoneRemoteSettings.HasPhones(library);

    // Keeps the sign-in entry in step with the settings, off the UI thread.
    private void SyncPhoneHostStartup() { var store = library; _ = Task.Run(() => PhoneHostStartup.Sync(store)); }

    // Starts, restarts or stops the link to match the settings. Called at startup and when they change.
    private void SyncPhoneRemote()
    {
        var server = SavedServerAddress;
        if (!PhoneRemoteEnabled || closing)
        {
            // Phones then hear nothing from this PC at all: their offers go unanswered.
            if (!closing) PlaybackDiagnostics.Record("remote-off", new { running = phoneRemote is not null });
            StopPhoneRemote();
            return;
        }
        if (phoneRemote is not null && phoneRemoteServer == server) return;
        StopPhoneRemote();
        var hostKey = library.Setting("phoneRemoteHostKey");
        if (hostKey is not { Length: 64 }) { hostKey = PersistentRooms.NewKey(); library.Setting("phoneRemoteHostKey", hostKey); }
        try { RoomAddress.ValidateServer(server); }
        catch (ArgumentException) { PlaybackDiagnostics.Record("remote-server", new { state = "invalid-address" }); RenderPhoneRemote(); return; }
        phoneRemoteServer = server;
        phoneRemote = new PhoneRemoteHost(new PhoneRemoteOptions
        {
            Server = server, HostKey = hostKey, PcName = PcName, Store = new LibraryPhoneStore(library), CanStart = PhonesCanStart, Features = PhoneFeatures,
            AppVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            // The phone host hands the port over as it ends, once this window has taken the phones over.
            Lan = PhoneRemoteSettings.Lan(library, () => PhoneHost.Running),
        });
        var remote = phoneRemote;
        remote.Changed += () => Dispatcher.BeginInvoke(() => { if (phoneRemote == remote) RenderPhoneRemote(); });
        remote.Paired += _ => Dispatcher.BeginInvoke(() => { if (phoneRemote == remote) { pairingWindow?.Close(); SetStatus("Phone paired. It can now control BlackPearl."); } });
        remote.Command += (session, command) => Dispatcher.BeginInvoke(() => { if (phoneRemote == remote) RunPhoneCommand(session, command); });
        // Asked from a native thread when a phone connects. The state is read on the UI thread, which then sends it to
        // every phone, rather than holding the native thread until it can.
        remote.CurrentState = () => { Dispatcher.BeginInvoke(() => { if (phoneRemote == remote) PublishPhoneRemoteState(force: true); }); return null; };
        AttachPhonePlayback(remote);
        AttachPhoneSound(remote);
        remote.Start();
        phoneRemoteTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => { SyncPhoneSound(); PublishPhoneRemoteState(); RefreshPhoneFirewall(); }, Dispatcher);
        phoneRemoteTimer.Start();
        RenderPhoneRemote();
    }

    private void StopPhoneRemote()
    {
        foreach (var listener in phoneListeners.Values.ToArray()) EndPhoneSound(listener, null);
        phoneRemoteTimer?.Stop();
        pairingWindow?.Close();
        if (phoneRemote is { } remote) { phoneRemote = null; remote.Dispose(); }
        ClosePhoneStreams();
        phoneRemoteServer = null; phoneRemoteSentKey = null; phoneFirewall = null;
        RenderPhoneRemote();
    }

    // What Windows Firewall decided about the home-network listener (PhoneFirewall.cs): read once it listens, for the log,
    // then every ten seconds while Settings shows the phone remote, so allowing BlackPearl in Windows Security shows there
    // soon after. Read off the UI thread; it takes a moment.
    private PhoneFirewall.Status? phoneFirewall;
    private long phoneFirewallAt = long.MinValue / 2;
    private bool phoneFirewallReading;
    private void RefreshPhoneFirewall(bool soon = false)
    {
        if (phoneRemote?.Lan is not { State: "listening", Port: { } port, LoopbackOnly: false } || phoneFirewallReading) return;
        var showing = SettingsPage.Visibility == Visibility.Visible && preferenceSection == "Profile";
        if (phoneFirewall is not null && !(showing && Environment.TickCount64 - phoneFirewallAt > (soon ? 2000 : 10000))) return;
        phoneFirewallReading = true;
        var remote = phoneRemote;
        Task.Run(() => PhoneFirewallLog.Check(port)).ContinueWith(read => Dispatcher.BeginInvoke(() =>
        {
            phoneFirewallReading = false; phoneFirewallAt = Environment.TickCount64;
            if (phoneRemote != remote) return;
            // A read that failed counts as one that couldn't tell, so the next tick doesn't start another at once.
            phoneFirewall = read.IsCompletedSuccessfully ? read.Result : new(PhoneFirewall.Verdict.Unknown, 0, null, []);
            RenderPhoneRemote();
        }), TaskScheduler.Default);
    }

    // Settings' lines about the home network: where phones reach this PC when the room server can't introduce them, and
    // whether Windows Firewall lets them in. Nothing here says it works: only what the firewall decided, which is one
    // of the things that can stop it.
    internal FrameworkElement PhoneHomeNetwork(PhoneLanState lan, PhoneFirewall.Status? wall, string? address)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        AutomationProperties.SetName(panel, "Home network");
        var heading = new TextBlock { Text = "On your home network", FontSize = 13, FontWeight = FontWeights.SemiBold };
        panel.Children.Add(heading);
        var (said, showSettings) = PhoneHomeNetworkText(lan, wall, address);
        var text = new TextBlock { Text = said, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Polite);
        panel.Children.Add(text);
        if (showSettings)
        {
            // Windows' "Allow an app through firewall" page. Changing it there asks for an administrator.
            var open = new Button { Content = "Firewall settings", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
            open.Click += (_, _) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("control.exe", "/name Microsoft.WindowsFirewall /page pageConfigureApps") { UseShellExecute = true })?.Dispose(); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { SetStatus("Windows' firewall settings didn't open."); }
            };
            panel.Children.Add(open);
        }
        return panel;
    }

    // What the lines say, and whether they offer Firewall settings: for the listener's state, what Windows Firewall
    // decided (null while it is read), and the address phones are given first.
    internal static (string Text, bool ShowSettings) PhoneHomeNetworkText(PhoneLanState lan, PhoneFirewall.Status? wall, string? address)
    {
        if (lan.State == "busy")
            return ($"Phones can't reach this PC directly on the home network: ports {PhoneRemoteProtocol.LanPort} to {PhoneRemoteProtocol.LanPort + PhoneRemoteProtocol.LanPorts - 1} are all in use by other programs. They still connect through the room server.", false);
        if (lan.State == "waiting") return ("BlackPearl's phone access, which served phones while BlackPearl was closed, is handing the home network over to this window.", false);
        if (lan.State != "listening") return ("Starting.", false);
        if (lan.LoopbackOnly) return ($"This build listens for phones on this PC only, at port {lan.Port}, for testing.", false);
        var lead = "When the room server can't introduce them, phones on your home network connect to this PC directly" + (address is null ? "." : $", at {address}.");
        var network = wall?.Network is { Length: > 0 } name ? name : "this network";
        var kind = wall?.ProfileType switch { PhoneFirewall.Public => "public", PhoneFirewall.Domain => "domain", _ => "private" };
        var showSettings = false;
        string verdict;
        switch (wall?.Verdict)
        {
            case null: verdict = "Checking Windows Firewall."; break;
            case PhoneFirewall.Verdict.Allowed: verdict = $"Windows Firewall lets BlackPearl in on {network}."; break;
            case PhoneFirewall.Verdict.Off: verdict = $"Windows Firewall is off on {network}."; break;
            case PhoneFirewall.Verdict.Undecided:
                verdict = "Windows Firewall keeps BlackPearl out until you allow it: choose Allow when Windows asks, or tick BlackPearl in Firewall settings.";
                showSettings = true; break;
            case PhoneFirewall.Verdict.Blocked:
                verdict = $"Windows Firewall blocks BlackPearl on {network}, so phones there only connect through the room server. To allow it, open Firewall settings, choose Change settings, and tick BlackPearl for {kind} networks.";
                if (wall.PublicNetwork) verdict += $" Windows counts {network} as a public network; if it's your home network, set it to Private in Windows Settings › Network & internet first.";
                showSettings = true; break;
            default: verdict = "BlackPearl can't tell whether Windows Firewall lets phones in."; break;
        }
        var text = lead + " " + verdict;
        if (wall?.OtherFirewalls is { Length: > 0 } others)
            text += $" {string.Join(" and ", others)} {(others.Length == 1 ? "has" : "have")} a firewall of {(others.Length == 1 ? "its" : "their")} own, which may need to allow BlackPearl too.";
        return (text, showSettings);
    }

    // Whether the video playing is one of the library's, rather than a file opened on its own. Remembered per video and
    // library list: the state is read four times a second while a phone is connected, and the library can load after a
    // file opened at startup.
    private (string Id, List<MediaItem> Items, bool Known) phoneStateItem;
    private bool InLibrary(MediaItem item)
    {
        if (phoneStateItem.Id != item.Id || !ReferenceEquals(phoneStateItem.Items, items))
            phoneStateItem = (item.Id, items, !string.IsNullOrEmpty(item.Path) && items.Any(x => x.Id == item.Id));
        return phoneStateItem.Known;
    }

    private RemoteState PhoneRemoteState()
    {
        RemoteRoom? inRoom = room?.Snapshot is { } snapshot ? new(snapshot.Name ?? "Room", snapshot.People.Length) : null;
        if (!HasMediaPlayback || player is null) return new RemoteState(false, Room: inRoom, Volume: (int)Volume.Value);
        var item = playingItem;
        var title = room?.Snapshot?.Media?.Title ?? item?.ShownTitle ?? "BlackPearl";
        string? detail = null;
        if (audioPlayback) detail = string.Join(" · ", new[] { AudioArtist.Text, AudioAlbum.Text }.Where(x => !string.IsNullOrWhiteSpace(x)));
        else if (item is { Series: not null })
            detail = string.Join(" · ", new[] { item.Season is { } season ? $"Season {season}" : null, item.EpisodeDisplayTitle }.OfType<string>());
        else if (item?.Year is { } year) detail = year.ToString(CultureInfo.InvariantCulture);
        var position = Math.Max(0, PlaybackPosition());
        var tracks = PhoneRemoteTracks();
        return new RemoteState(true, title, string.IsNullOrWhiteSpace(detail) ? null : detail, WantsMediaPlayback, position, Math.Max(0, player.Length),
            (int)Math.Round(Volume.Value), UserMuted, loopMode switch { LoopMode.All => "all", LoopMode.One => "one", _ => "off" }, fullscreen,
            PreviousButton.IsEnabled, NextButton.IsEnabled, CanControlMediaPlayback, inRoom,
            // Only a video this PC plays on its own can move to the phone; a room's belongs to the room.
            room is null && item is { IsMusic: false } && InLibrary(item) ? item.Id : null,
            room is null ? PhoneRemoteQueue(item) : null, shuffle,
            tracks.Audio, tracks.AudioId, tracks.Subtitles, tracks.SubtitleId, tracks.CanChange, phoneSoundMuted);
    }

    // The player's Audio and Subtitles menus as they are now: their choices, the ticked one, and whether they can be
    // changed (not in a room where only whoever plays the video picks them). The menus' lists are replaced only when
    // they change, so each is converted once rather than four times a second.
    private (TrackChoice[]? Source, RemoteTrack[] Tracks) phoneAudioTracks, phoneSubtitleTracks;
    private (RemoteTrack[] Audio, int? AudioId, RemoteTrack[] Subtitles, int? SubtitleId, bool CanChange) PhoneRemoteTracks()
    {
        static RemoteTrack[] Convert(ListBox list, ref (TrackChoice[]? Source, RemoteTrack[] Tracks) cache)
        {
            var source = list.ItemsSource as TrackChoice[] ?? [];
            if (!ReferenceEquals(cache.Source, source) || cache.Tracks is null)
                cache = (source, [.. source.Select(t => new RemoteTrack(t.Id, t.Label, string.IsNullOrWhiteSpace(t.Detail) ? null : t.Detail))]);
            return cache.Tracks;
        }
        var audio = Convert(AudioTracks, ref phoneAudioTracks);
        var subtitles = Convert(SubtitleTracks, ref phoneSubtitleTracks);
        return (audio, (AudioTracks.SelectedItem as TrackChoice)?.Id, subtitles, (SubtitleTracks.SelectedItem as TrackChoice)?.Id, CanChangeRoomTracks);
    }

    // `audio-track` and `subtitle-track` pick a choice of the player's menus by its id, through the same code as
    // clicking it, so a room's rule on who picks tracks and the subtitle rules for the audio's language apply the same
    // way. Returns the error to send, or null.
    private (string Code, string Message)? RunPhoneTrackCommand(RemoteCommand command)
    {
        if (!HasMediaPlayback || player is null) return ("nothing-playing", "Nothing is playing on the PC.");
        if (command.Number is not { } number || number != Math.Floor(number) || Math.Abs(number) > int.MaxValue) return null;
        var list = command.Action == "audio-track" ? AudioTracks : SubtitleTracks;
        if (!CanChangeRoomTracks)
            return ("not-allowed", room is { } inRoom && inRoom.Identity?.Host != true && inRoom.Snapshot?.SharedControls != true
                ? "In this room, only whoever plays the video picks the audio and subtitles."
                : "The PC can't change tracks right now. Try again in a moment.");
        if ((list.ItemsSource as TrackChoice[])?.FirstOrDefault(t => t.Id == (int)number) is not { } choice) return null;
        if (!Equals(list.SelectedItem, choice)) list.SelectedItem = choice;
        return null;
    }

    // The saved queue playing here and the video's place in it. The player's queue is the saved one's videos that the
    // library has, in order, so one pass finds the place without a lookup on every state.
    private RemoteQueue? PhoneRemoteQueue(MediaItem? item)
    {
        if (!PlayingFromQueue(item) || activeQueueId is null || savedQueues.FirstOrDefault(q => q.Id == activeQueueId) is not { } saved) return null;
        for (int index = 0, at = 0; index < saved.MediaIds.Length && at < queue.Count; index++)
        {
            if (saved.MediaIds[index] != queue[at].Id) continue;
            if (at == queuePosition) return new(saved.Id, PhoneQueues.Name(saved.Name), index);
            at++;
        }
        return null;
    }

    // Sends the state when something other than the advancing position changed, after a seek, and every five seconds
    // while playing so phones that advance the position themselves don't drift.
    private void PublishPhoneRemoteState(bool force = false)
    {
        if (phoneRemote is not { } remote || remote.Connected.Count == 0) { phoneRemoteSentKey = null; return; }
        var state = PhoneRemoteState();
        var key = Wire.Serialize(state with { PositionMs = 0 });
        var now = Environment.TickCount64;
        var expected = phoneRemoteSentPosition + (state.Playing == true ? now - phoneRemoteSentAt : 0);
        var jumped = state.PositionMs is { } position && Math.Abs(position - expected) > 1500;
        if (!force && key == phoneRemoteSentKey && !jumped && (state.Playing != true || now - phoneRemoteSentAt < 5000)) return;
        phoneRemoteSentKey = key; phoneRemoteSentAt = now; phoneRemoteSentPosition = state.PositionMs ?? 0;
        remote.Publish(state);
    }

    private void RunPhoneCommand(PhoneRemoteSession session, RemoteCommand command)
    {
        if (phoneRemote is not { } remote) return;
        var action = command.Action;
        if (action is "volume" or "mute")
        {
            if (action == "volume" && command.Number is { } level) Volume.Value = Math.Clamp(Math.Round(level), 0, 100);
            else if (action == "mute") MutePlayback(this, new RoutedEventArgs());
            return;
        }
        if (action is "audio-track" or "subtitle-track")
        {
            if (RunPhoneTrackCommand(command) is { } error) remote.SendError(session, error.Code, error.Message);
            // Also when nothing changed, so a phone waiting on its choice learns the outcome at once.
            PublishPhoneRemoteState(force: true);
            return;
        }
        if (action is not ("toggle" or "play" or "pause" or "seek-by" or "seek-to" or "previous" or "next" or "stop" or "loop" or "shuffle" or "fullscreen")) return;
        if (!HasMediaPlayback)
        {
            // Play continues the up-next countdown even with the player's video ended.
            if (action is "toggle" or "play" && upNextChoice is not null) RequestPlayback(true);
            else remote.SendError(session, "nothing-playing", "Nothing is playing on the PC.");
            return;
        }
        if (!CanControlMediaPlayback && action is not "fullscreen")
        {
            remote.SendError(session, "not-allowed", StreamerName + " is playing the video and controls playback in this room.");
            return;
        }
        switch (action)
        {
            case "toggle": RequestPlayback(!WantsMediaPlayback); break;
            case "play": if (!WantsMediaPlayback) RequestPlayback(true); break;
            case "pause": if (WantsMediaPlayback) RequestPlayback(false); break;
            case "seek-by" when command.Number is { } offset: MovePlayback((long)Math.Clamp(offset, -86400000, 86400000)); break;
            case "seek-to" when command.Number is { } to && player!.Length > 0: SeekPlayback(Math.Clamp((long)to, 0, player.Length)); break;
            case "previous": if (PreviousButton.IsEnabled) PreviousQueued(this, new RoutedEventArgs()); break;
            case "next": if (NextButton.IsEnabled) NextQueued(this, new RoutedEventArgs()); break;
            case "stop": StopPlayback(this, new RoutedEventArgs()); break;
            case "loop": ToggleLoop(this, new RoutedEventArgs()); break;
            case "shuffle": ToggleShuffle(this, new RoutedEventArgs()); break;
            case "fullscreen":
                if (RoomPage.Visibility != Visibility.Visible) remote.SendError(session, "not-shown", "Open the player on the PC to go fullscreen.");
                else SetFullscreen(!fullscreen);
                break;
        }
        // The player applies most of these a moment later; the next tick sends the state once it has.
        UpdateSystemMediaControls();
    }

    // Settings > Profile, below the server. Rebuilt only when what it shows changed, so it doesn't move focus.
    private void RenderPhoneRemote(bool force = false)
    {
        if (PhoneRemoteContent is null || preferenceSection != "Profile") return;
        var remote = phoneRemote;
        var phones = remote?.Phones ?? new LibraryPhoneStore(library).Load();
        var connected = remote?.Connected.Select(s => (s.PhoneId, s.Relayed)).ToArray() ?? [];
        if (force) RefreshPhoneFirewall(soon: true);
        var key = Wire.Serialize(new { enabled = PhoneRemoteEnabled, canStart = PhonesCanStart, status = remote?.Status, ready = remote?.ServerReady, phones, connected, server = SavedServerAddress,
            lan = remote?.Lan, firewall = phoneFirewall is { } wall ? $"{wall.Verdict}|{wall.ProfileType}|{wall.Network}|{string.Join(",", wall.OtherFirewalls)}" : null });
        if (!force && key == phoneRemoteKey) return;
        phoneRemoteKey = key;
        PhoneRemoteContent.Content = PhoneRemotePanel(remote, phones, connected);
    }

    private FrameworkElement PhoneRemotePanel(PhoneRemoteHost? remote, IReadOnlyList<PairedPhone> phones, (string PhoneId, bool Relayed)[] connected)
    {
        var page = new StackPanel();
        TextBlock Line(string text, bool muted = true, double size = 13)
        {
            var line = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
            if (muted) line.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            return line;
        }
        StackPanel Card(string title)
        {
            var panel = new StackPanel();
            var heading = new TextBlock { Text = title }; heading.SetResourceReference(StyleProperty, "CardTitle"); panel.Children.Add(heading);
            var border = new Stone { Child = panel, Margin = new Thickness(0, 0, 0, 20) }; border.SetResourceReference(StyleProperty, "Card");
            AutomationProperties.SetName(border, title); page.Children.Add(border);
            return panel;
        }
        var enabled = PhoneRemoteEnabled;
        var main = Card("Phone remote");
        main.Children.Add(Line("Control playback on this PC from BlackPearl Mobile on your phone, at home or away, or browse this library and watch it on the phone, then carry on here from the same moment. The phone connects straight to this PC; your room server only introduces them."));
        var toggle = new CheckBox { IsChecked = enabled, Content = "Allow paired phones to control BlackPearl", Margin = new Thickness(0, 14, 0, 0) };
        toggle.Click += (_, _) => { library.Setting("phoneRemote", toggle.IsChecked == true ? "on" : "off"); SyncPhoneRemote(); SyncPhoneHostStartup(); RenderPhoneRemote(force: true); };
        main.Children.Add(toggle);
        var canStart = new CheckBox { IsChecked = PhonesCanStart, IsEnabled = enabled, Content = "Phones can start videos and change queues on their own", Margin = new Thickness(0, 10, 0, 0) };
        canStart.Click += (_, _) =>
        {
            library.Setting("phoneRemoteCanStart", canStart.IsChecked == true ? "on" : "off");
            // Phones learn this when they connect, so the link starts again.
            StopPhoneRemote(); SyncPhoneRemote();
        };
        main.Children.Add(canStart);
        var whileClosed = PhoneRemoteSettings.WhileClosed(library);
        var keep = new CheckBox { IsChecked = whileClosed, IsEnabled = enabled, Content = "Phones can browse and watch while BlackPearl is closed", Margin = new Thickness(0, 10, 0, 0) };
        keep.Click += (_, _) => { library.Setting("phoneRemoteWhileClosed", keep.IsChecked == true ? "on" : "off"); SyncPhoneHostStartup(); RenderPhoneRemote(force: true); };
        main.Children.Add(keep);
        var signIn = new CheckBox { IsChecked = PhoneRemoteSettings.AtSignIn(library), IsEnabled = enabled && whileClosed, Content = "Also when I sign in to Windows, before BlackPearl is opened", Margin = new Thickness(28, 8, 0, 0) };
        signIn.Click += (_, _) => { library.Setting("phoneRemoteAtSignIn", signIn.IsChecked == true ? "on" : "off"); SyncPhoneHostStartup(); };
        main.Children.Add(signIn);
        var keepHint = Line("BlackPearl's icon stays in the notification area meanwhile. Right-click it to turn this off.", size: 12.5);
        keepHint.Margin = new Thickness(28, 6, 0, 0);
        if (!enabled || !whileClosed) keepHint.Opacity = .6;
        main.Children.Add(keepHint);
        var status = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        var pair = new Button { Content = "Pair a phone", IsEnabled = enabled && remote is not null, VerticalAlignment = VerticalAlignment.Center };
        pair.SetResourceReference(StyleProperty, "Primary");
        pair.Click += (_, _) => ShowPairing();
        DockPanel.SetDock(pair, Dock.Right); status.Children.Add(pair);
        var statusText = !enabled ? "Off. Phones can't connect." : remote is null ? "Enter a valid BlackPearl server above to use the phone remote."
            : remote.ServerReady ? $"Ready. Phones reach this PC through {new Uri(SavedServerAddress).Host}." : remote.Status;
        var statusLine = Line(statusText); statusLine.Margin = new Thickness(0, 0, 12, 0); statusLine.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetLiveSetting(statusLine, AutomationLiveSetting.Polite);
        status.Children.Add(statusLine);
        main.Children.Add(status);
        if (enabled && remote?.Lan is { } lan) main.Children.Add(PhoneHomeNetwork(lan, phoneFirewall, lan is { State: "listening", LoopbackOnly: false } ? PhoneRemoteProtocol.CachedLanAddresses().FirstOrDefault() : null));

        var list = Card("Paired phones");
        if (phones.Count == 0) list.Children.Add(Line("No phones yet. Choose Pair a phone and scan the code with BlackPearl Mobile."));
        foreach (var phone in phones.OrderByDescending(p => p.LastSeen))
        {
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
            var remove = new Button { Content = "Remove", VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(remove, "Remove " + phone.Name);
            remove.Click += (_, _) =>
            {
                if (phoneRemote is { } live) live.Forget(phone.Id);
                else { var store = new LibraryPhoneStore(library); store.Save(store.Load().Where(p => p.Id != phone.Id).ToArray()); }
                RenderPhoneRemote(force: true);
            };
            DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            var name = Line(phone.Name, muted: false); name.FontWeight = FontWeights.SemiBold; texts.Children.Add(name);
            var link = connected.FirstOrDefault(c => c.PhoneId == phone.Id);
            texts.Children.Add(Line(link.PhoneId is not null ? (link.Relayed ? "Connected through the relay" : "Connected") : "Last used " + When(phone.LastSeen), size: 12.5));
            row.Children.Add(texts); AutomationProperties.SetName(row, phone.Name);
            list.Children.Add(row);
        }
        if (phones.Count > 0) { var hint = Line("Removing a phone cuts it off at once, wherever it is."); hint.Margin = new Thickness(0, 6, 0, 0); list.Children.Add(hint); }
        return page;
    }

    private static string When(long at) => DateTimeOffset.FromUnixTimeMilliseconds(at).ToLocalTime() is var local && local.Date == DateTime.Today
        ? "today, " + local.ToString("t", CultureInfo.CurrentCulture) : local.ToString("MMM d", CultureInfo.CurrentCulture);

    // A window with the pairing QR code, the link as text, and how long it lasts. Pairing closes it.
    private void ShowPairing()
    {
        if (phoneRemote is not { } remote) return;
        if (pairingWindow is not null) { pairingWindow.Activate(); return; }
        var link = remote.BeginPairing();
        var expires = DateTimeOffset.Now + PhoneRemoteProtocol.PairingLifetime;
        var panel = new StackPanel { Margin = new Thickness(28, 24, 28, 24), Width = 340 };
        var title = new TextBlock { Text = "Pair a phone", FontSize = 20, FontWeight = FontWeights.SemiBold };
        title.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
        panel.Children.Add(title);
        var hint = new TextBlock { Text = "Open BlackPearl Mobile and scan this code. You can also scan it with the phone's camera once the app is installed.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 16), FontSize = 13 };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        panel.Children.Add(hint);
        var code = new Image { Source = QrImage(link), Width = 260, Height = 260, SnapsToDevicePixels = true, HorizontalAlignment = HorizontalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(code, BitmapScalingMode.NearestNeighbor);
        AutomationProperties.SetName(code, "Pairing code");
        panel.Children.Add(new Border { Background = Brushes.White, CornerRadius = new CornerRadius(16), Padding = new Thickness(14), HorizontalAlignment = HorizontalAlignment.Center, Child = code });
        var timeLeft = new TextBlock { Margin = new Thickness(0, 14, 0, 0), FontSize = 12.5, TextAlignment = TextAlignment.Center };
        timeLeft.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        AutomationProperties.SetLiveSetting(timeLeft, AutomationLiveSetting.Off);
        panel.Children.Add(timeLeft);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
        // Both buttons set their margins: the default bottom margin would make one taller than the other in this row.
        var copy = new Button { Content = "Copy link", Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        copy.Click += (_, _) => { try { Clipboard.SetText(link); copy.Content = "Copied"; } catch (System.Runtime.InteropServices.COMException) { } };
        var close = new Button { Content = "Close", IsCancel = true, Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Top };
        buttons.Children.Add(copy); buttons.Children.Add(close);
        panel.Children.Add(buttons);
        var window = new Window { Title = "Pair a phone", Content = panel, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        window.SetResourceReference(BackgroundProperty, "Canvas");
        window.SetResourceReference(ForegroundProperty, "Ink");
        close.Click += (_, _) => window.Close();
        var countdown = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            var left = expires - DateTimeOffset.Now;
            if (left <= TimeSpan.Zero || phoneRemote is null || !phoneRemote.PairingOpen) { timeLeft.Text = "This code has expired. Close and pair again for a new one."; code.Opacity = .25; return; }
            timeLeft.Text = $"Works once, for {Math.Ceiling(left.TotalMinutes)} more minute{(left.TotalMinutes > 1 ? "s" : "")}.";
        }, Dispatcher);
        timeLeft.Text = $"Works once, for {PhoneRemoteProtocol.PairingLifetime.TotalMinutes} minutes.";
        window.Closed += (_, _) => { countdown.Stop(); pairingWindow = null; if (phoneRemote == remote && remote.PairingOpen) remote.CancelPairing(); };
        pairingWindow = window;
        window.Show();
    }

    private static BitmapSource QrImage(string text)
    {
        using var generator = new QRCoder.QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
        var modules = data.ModuleMatrix;
        var size = modules.Count;
        var pixels = new byte[size * size];
        for (var y = 0; y < size; y++) for (var x = 0; x < size; x++) pixels[y * size + x] = modules[y][x] ? (byte)0 : (byte)255;
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, pixels, size);
        bitmap.Freeze();
        return bitmap;
    }
}
