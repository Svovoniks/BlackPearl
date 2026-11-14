using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BlackPearl.Core;

namespace BlackPearl.Desktop;

public partial class MainWindow
{
    private readonly ObservableCollection<SavedRoom> savedRooms = [];
    private string? pendingRoomName;
    private void LoadSavedRooms()
    {
        try { foreach (var saved in Wire.Read<SavedRoom[]>(library.Setting("rooms") ?? "[]")) savedRooms.Add(saved); }
        catch (System.Text.Json.JsonException) { }
        SavedRoomsList.ItemsSource = savedRooms;
        SavedRoomsList.ItemContainerGenerator.StatusChanged += (_, _) => MarkLiveRoomPill();
        if (savedRooms.Count > 0) SavedRoomsList.SelectedIndex = 0;
        RefreshSavedRooms();
    }
    private void RefreshSavedRooms()
    {
        if (SavedRoomsList is null) return;
        CreateSavedRoomButton.IsEnabled = AddSavedInvitationButton.IsEnabled = !roomBusy;
        RefreshRoomPresence();
        RoomsEmptyState.Visibility = savedRooms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CreateSavedRoomButton.Visibility = AddSavedInvitationButton.Visibility = savedRooms.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SavedRoomsContent.Visibility = savedRooms.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SavedRoomDetails.Visibility = SavedRoomsList.SelectedItem is SavedRoom ? Visibility.Visible : Visibility.Collapsed;
        ConnectSavedRoomButton.IsEnabled = !roomBusy && SavedRoomsList.SelectedItem is SavedRoom;
        ForgetSavedRoomButton.IsEnabled = CopySavedInviteButton.IsEnabled = CopySavedCodeButton.IsEnabled = SavedRoomsList.SelectedItem is SavedRoom;
        RefreshRoomSettings(); RefreshRoomHero();
    }
    private readonly Dictionary<string, string> savedMemberLists = [];
    private string SavedRoomSetting(SavedRoom saved, string field) => "room:" + saved.Server + ":" + saved.Code.ToUpperInvariant() + ":" + field;
    private bool ConnectedTo(SavedRoom? saved) => saved is not null && room?.IsConnected == true && room.ServerAddress == saved.Server && room.Identity?.Room == saved.Code;

    // Who is in each saved room right now, asked of its room service while the Rooms page is open. A room the service
    // doesn't answer for has no entry.
    private readonly Dictionary<string, RoomPresenceInfo> roomPresence = [];
    private readonly DispatcherTimer roomPresenceTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private long roomPresenceAsked = -60000;
    private bool roomPresencePending;
    private static string PresenceKey(SavedRoom saved) => saved.Server + "#" + saved.Code.ToUpperInvariant();
    private void InitializeRoomPresence() => roomPresenceTimer.Tick += (_, _) => RefreshRoomPresence(force: true);
    private async void RefreshRoomPresence(bool force = false)
    {
        var visible = RoomsPage?.Visibility == Visibility.Visible && IsActive && savedRooms.Count > 0;
        if (roomPresenceTimer.IsEnabled != visible) { if (visible) roomPresenceTimer.Start(); else roomPresenceTimer.Stop(); }
        if (!visible || roomPresencePending || Environment.TickCount64 - roomPresenceAsked < (force ? 5000 : 15000)) return;
        roomPresencePending = true; roomPresenceAsked = Environment.TickCount64;
        try
        {
            var found = new Dictionary<string, RoomPresenceInfo>();
            foreach (var group in savedRooms.GroupBy(saved => saved.Server).ToArray())
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    foreach (var pair in await RoomClient.PresenceAsync(group.Key, group.Select(saved => saved.Code), timeout.Token)) found[group.Key + "#" + pair.Key] = pair.Value;
                }
                // An unreachable or older room service only means nothing is known about its rooms.
                catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or OperationCanceledException or System.Text.Json.JsonException or ArgumentException) { }
            }
            if (closing) return;
            var changed = found.Count != roomPresence.Count || found.Any(pair => !roomPresence.TryGetValue(pair.Key, out var before) || before.Name != pair.Value.Name || !before.People.SequenceEqual(pair.Value.People));
            roomPresence.Clear(); foreach (var pair in found) roomPresence[pair.Key] = pair.Value;
            // A room its creator renamed keeps its new name here too.
            foreach (var saved in savedRooms.ToArray())
                if (!ConnectedTo(saved) && roomPresence.GetValueOrDefault(PresenceKey(saved))?.Name is { Length: > 0 } name && name != saved.Name && saved.HostKey is null)
                {
                    var selected = SavedRoomsList.SelectedItem == (object)saved;
                    var updated = saved with { Name = name };
                    savedRooms[savedRooms.IndexOf(saved)] = updated; SaveRooms(); changed = true;
                    if (selected) SavedRoomsList.SelectedItem = updated;
                }
            if (changed) { RefreshRoomSettings(); RefreshRoomHero(); }
        }
        finally { roomPresencePending = false; }
    }
    // The names of who is in a room the app isn't connected to, or null when the room service hasn't said.
    private string[]? PeopleInRoom(SavedRoom saved) => roomPresence.GetValueOrDefault(PresenceKey(saved))?.People;

    private void RefreshRoomSettings()
    {
        if (SavedRoomSharedControls is null) return;
        var saved = SavedRoomsList.SelectedItem as SavedRoom;
        var connected = ConnectedTo(saved);
        // The room's creator can rename it and remove people; nothing else sets them apart.
        var owner = saved is not null && (connected ? room!.Identity!.Owner : saved.HostKey is not null);
        var streaming = connected && room!.Identity!.Host;
        SavedRoomHostControls.Visibility = saved is not null ? Visibility.Visible : Visibility.Collapsed;
        MarkLiveRoomPill();
        RenameSavedRoomButton.Visibility = owner ? Visibility.Visible : Visibility.Collapsed;
        RenameSavedRoomButton.IsEnabled = owner;
        RenameSavedRoomButton.ToolTip = "Change the room name for everyone";
        SavedRoomHeading.Text = saved?.Name ?? "Choose a room";
        var elsewhere = saved is null || connected ? null : PeopleInRoom(saved);
        var playing = connected ? room!.Snapshot?.Media : null;
        SavedRoomStatus.Text = saved is null ? "Create a room, or join one with an invitation."
            : connected ? playing is null ? "You're in this room" : (streaming ? "You're playing " : StreamerName + " is playing ") + playing.Title
            : elsewhere is { Length: > 0 } ? RoomSessions.Names(elsewhere) + (elsewhere.Length == 1 ? " is" : " are") + " in here now"
            : elsewhere is not null ? "Nobody is in here right now" : "Not connected";
        SavedRoomStatusDot.Fill = (System.Windows.Media.Brush)FindResource(connected || elsewhere is { Length: > 0 } ? "Success" : "Muted");
        SavedRoomCode.Text = saved is null ? "" : string.Join(" ", saved.Code.Chunk(6).Select(part => new string(part)));
        SavedRoomInviteHint.Text = saved is null ? "" : (Uri.TryCreate(saved.Server, UriKind.Absolute, out var server) ? server.Host : saved.Server) + " · Anyone with the invitation can join";
        ShowSavedRoomSession(saved);
        ConnectSavedRoomButton.Content = connected ? "Open player" : "Join";
        DisconnectSavedRoomButton.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        DisconnectSavedRoomButton.IsEnabled = connected && !roomBusy;
        // What others may do while this person plays a video here. It is their own choice, kept for the room.
        SavedRoomSharedControls.IsEnabled = SavedRoomLibraryBrowsing.IsEnabled = saved is not null;
        updatingLibraryPermission = true;
        SavedRoomLibraryBrowsing.IsChecked = streaming ? room!.Snapshot?.LibraryBrowsing == true : saved is not null && library.Setting(SavedRoomSetting(saved, "libraryBrowsing")) == "true";
        updatingLibraryPermission = false;
        SavedRoomSharedControls.IsChecked = streaming ? room!.Snapshot?.SharedControls == true : saved is not null && library.Setting(SavedRoomSetting(saved, "sharedControls")) != "false";
        // Members are remembered for when the app isn't connected.
        AdmittedGuest[] members = [];
        if (saved is not null)
        {
            var key = SavedRoomSetting(saved, "members");
            if (connected)
            {
                members = room!.Members;
                // Snapshots arrive often; write the list only when it changes.
                var json = Wire.Serialize(members);
                if (members.Length > 0 && savedMemberLists.GetValueOrDefault(key) != json) { library.Setting(key, json); savedMemberLists[key] = json; }
            }
            else try { members = Wire.Read<AdmittedGuest[]>(library.Setting(key) ?? "[]"); } catch (System.Text.Json.JsonException) { }
        }
        var rows = new List<RoomGuestRow>();
        if (connected)
        {
            var client = room!; var me = client.Identity!.Peer;
            // The room service lists who is here; until the streamer's state arrives, that is all there is.
            var present = client.Roster.Length > 0 ? client.Roster : client.Snapshot?.People ?? [];
            rows.AddRange(present.Select(p => new RoomGuestRow(p.Id, p.Id == me ? p.Name + " (you)" : p.Name,
                string.Join(" · ", new[] { p.IsHost && playing is not null ? "Streaming" : "Here", client.VoiceState(p.Id)?.Joined == true ? "In voice" : null, p.Owner ? "Created the room" : null }.OfType<string>()),
                owner && p.Id != me, true, false)));
            rows.AddRange(members.Where(member => present.All(p => p.GuestId != member.Id)).Select(member => new RoomGuestRow(member.Id, member.Name, "Not here", owner, false, false)));
            rows.AddRange(client.Removed.Select(removed => new RoomGuestRow(removed.Id, removed.Name, "Removed", false, false, true)));
        }
        else if (saved is not null)
        {
            // Names stand in for people the room service lists without ids. Someone listed twice is here twice.
            var here = (elsewhere ?? []).ToList();
            rows.AddRange(here.Select((name, index) => new RoomGuestRow("here:" + index, name, "Here now", false, true, false)));
            foreach (var member in members)
                if (!here.Remove(member.Name)) rows.Add(new(member.Id, member.Name, "Not here", false, false, false));
        }
        if (SavedRoomGuests.ItemsSource is not IEnumerable<RoomGuestRow> shown || !shown.SequenceEqual(rows)) SavedRoomGuests.ItemsSource = rows;
        var count = rows.Count(row => row.Here);
        SavedRoomGuestCount.Text = saved is null ? "" : count == 0 ? "" : count + " here";
        SavedRoomGuestHint.Text = saved is null ? "" : rows.Count == 0 ? "Nobody has joined yet. Copy the invitation and send it to your friends." : "";
        SavedRoomGuestHintPanel.Visibility = SavedRoomGuestHint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var empty = rows.Count == 0;
        SavedRoomGuestHintPanel.VerticalAlignment = empty ? VerticalAlignment.Center : VerticalAlignment.Top;
        SavedRoomGuestHintPanel.Margin = new(empty ? 16 : 0, 0, empty ? 16 : 0, empty ? 24 : 12);
        Grid.SetRowSpan(SavedRoomGuestHintPanel, empty ? 2 : 1);
        SavedRoomGuestEmptyIcon.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        SavedRoomGuestHint.TextAlignment = empty ? TextAlignment.Center : TextAlignment.Left;
    }
    // Here: in the room now. CanRemove: the room's creator may remove them. Removed: only the creator sees these, and can let them back in.
    private sealed record RoomGuestRow(string Id, string Name, string Status, bool CanRemove, bool Here, bool Removed)
    {
        public Visibility RemoveVisibility => CanRemove ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RestoreVisibility => Removed ? Visibility.Visible : Visibility.Collapsed;
        public string Initial => Initials(Name, 1);
        public double Presence => Here ? 1 : 0.45;
        // Each person keeps a colour of their own, derived from the name.
        public System.Windows.Media.Brush Tint { get { var brush = new System.Windows.Media.SolidColorBrush(Hsl(TitleHue(Name), .65, .74)); brush.Freeze(); return brush; } }
    }
    private void SavedRoomControlsChanged(object sender, RoutedEventArgs e)
    {
        if (SavedRoomsList.SelectedItem is not SavedRoom saved || !SavedRoomSharedControls.IsEnabled) return;
        var enabled = SavedRoomSharedControls.IsChecked == true;
        library.Setting(SavedRoomSetting(saved, "sharedControls"), enabled ? "true" : "false");
        // It applies at once when this person is the one playing; otherwise the next time they are.
        if (ConnectedTo(saved) && room!.Identity?.Host == true) room.Send(new("controls", Number: enabled ? 1 : 0));
        SetStatus(enabled ? "Others can play, pause and seek what you play in this room." : "Only you control what you play in this room.");
    }
    private void SaveRooms() => library.Setting("rooms", Wire.Serialize(savedRooms));
    private void SavedRoomSelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshSavedRooms();
    private void RememberCurrentRoom()
    {
        if (room?.Identity is not { } identity || room.ServerAddress is null) return;
        var existing = savedRooms.FirstOrDefault(x => x.Server == room.ServerAddress && x.Code == identity.Room);
        if (existing is null)
        {
            existing = new(Guid.NewGuid().ToString("N"), room.Snapshot?.Name ?? pendingRoomName ?? "Room " + identity.Room[..6], room.ServerAddress, identity.Room, identity.HostKey);
            savedRooms.Add(existing);
        }
        else savedRooms[savedRooms.IndexOf(existing)] = existing with { HostKey = identity.HostKey ?? existing.HostKey, Name = room.Snapshot?.Name ?? existing.Name };
        SaveRooms(); SavedRoomsList.SelectedItem = savedRooms.First(x => x.Id == existing.Id);
    }
    private void SyncCurrentRoomName(RoomSnapshot snapshot)
    {
        if (room?.Identity is not { } identity || string.IsNullOrWhiteSpace(snapshot.Name)) return;
        RoomHeading.Text = snapshot.Name;
        var saved = savedRooms.FirstOrDefault(x => x.Server == room.ServerAddress && x.Code == identity.Room);
        if (saved is not null && saved.Name != snapshot.Name)
        {
            var selected = (SavedRoomsList.SelectedItem as SavedRoom)?.Id == saved.Id;
            var updated = saved with { Name = snapshot.Name };
            savedRooms[savedRooms.IndexOf(saved)] = updated; SaveRooms();
            if (selected) SavedRoomsList.SelectedItem = updated;
        }
    }
    private static string? ValidateRoomName(string name) => name.Length > 100 || name.Any(char.IsControl)
        ? "Use at most 100 characters without control characters." : null;
    private async void CreatePersistentRoom(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (roomBusy) return;
        var name = Dialogs.Prompt(this, "Create room", "Room name", validate: ValidateRoomName, action: "Create room",
            help: "The room stays open for whoever has its invitation, whether or not you're in it. Anyone in the room can play a video for the others."); if (name is null) return;
        pendingRoomName = name; roomBusy = true; RefreshSavedRooms();
        try
        {
            await Disconnect(); var client = NewRoom();
            try { await client.ConnectAsync(SavedServerAddress, SavedDisplayName, persistent: true, roomName: name); }
            catch { await Disconnect(); throw; }
            RememberCurrentRoom(); RoomHeading.Text = name; RoomSubtitle.Text = "Copy the invitation to bring friends in.";
            ShowPage("Rooms");
        }
        finally { pendingRoomName = null; roomBusy = false; RefreshSavedRooms(); }
    });
    private async void ConnectSavedRoom(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (roomBusy || SavedRoomsList.SelectedItem is not SavedRoom saved) return;
        if (ConnectedTo(saved)) { ShowPage("Room"); return; }
        if (await ConnectToSavedRoom(saved)) await OpenPlayerIfPlaying(room);
    });
    // Double-clicking a room's name joins it.
    private void SavedRoomDoubleClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(SavedRoomsList, (DependencyObject)e.OriginalSource) is ListBoxItem) ConnectSavedRoom(sender, e);
    }
    // Someone who joins while a video is playing goes to it; otherwise the Rooms page shows who is there.
    private async Task OpenPlayerIfPlaying(RoomClient? client)
    {
        if (client is null) return;
        try { await client.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(8), lifetime.Token); }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException) { return; }
        if (room == client && client.Snapshot?.Media is not null && currentPage == "Rooms") ShowPage("Room");
    }
    private async Task<bool> ConnectToSavedRoom(SavedRoom saved)
    {
        if (roomBusy) return false;
        roomBusy = true; RefreshSavedRooms();
        try
        {
            await Disconnect(); var client = NewRoom();
            SetStatus("Joining " + saved.Name + "…");
            // Only the room's creator names it; anyone else enters with the name it has.
            try { await client.ConnectAsync(saved.Server, SavedDisplayName, saved.Code, saved.HostKey, roomName: saved.HostKey is null ? null : saved.Name); }
            catch { await Disconnect(); throw; }
            RoomHeading.Text = client.Snapshot?.Name ?? saved.Name;
            RoomSubtitle.Text = "Connecting to the room…";
            RememberCurrentRoom();
            PublishRoomSession(client, saved);
            SetStatus("You're in " + (client.Snapshot?.Name ?? saved.Name) + ".");
            return true;
        }
        finally { roomBusy = false; RefreshSavedRooms(); }
    }
    private async void RenameSavedRoom(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        if (SavedRoomsList.SelectedItem is not SavedRoom saved || !RenameSavedRoomButton.IsEnabled) return Task.CompletedTask;
        var name = Dialogs.Prompt(this, "Rename room", "Room name", saved.Name, ValidateRoomName, action: "Save name"); if (name is null) return Task.CompletedTask;
        RenameHostRoom(saved, name);
        return Task.CompletedTask;
    });
    private void RenameHostRoom(SavedRoom saved, string name)
    {
        var connected = ConnectedTo(saved);
        if (!(connected ? room!.Identity!.Owner : saved.HostKey is not null)) return;
        if (string.IsNullOrWhiteSpace(name) || ValidateRoomName(name.Trim()) is not null) return;
        name = name.Trim();
        // Away from the room, the new name is sent the next time its creator enters.
        if (connected) room!.Send(new("room-name", Text: name));
        var updated = saved with { Name = name }; savedRooms[savedRooms.IndexOf(saved)] = updated;
        SaveRooms(); SavedRoomsList.SelectedItem = updated;
    }
    private void ForgetSavedRoom(object sender, RoutedEventArgs e)
    {
        if (SavedRoomsList.SelectedItem is not SavedRoom saved) return;
        savedRooms.Remove(saved); SaveRooms();
        SavedRoomsList.SelectedItem = savedRooms.FirstOrDefault(); RefreshSavedRooms();
    }
    private void CopySavedCode(object sender, RoutedEventArgs e)
    {
        if (SavedRoomsList.SelectedItem is not SavedRoom saved) return;
        try { Clipboard.SetText(saved.Code); SetStatus("Room code copied."); }
        catch (System.Runtime.InteropServices.ExternalException) { SetStatus("Clipboard is busy."); }
    }
    // The pill of the connected room shows a live dot; a room other people are in shows a quieter one.
    private void MarkLiveRoomPill()
    {
        var live = ConnectedSavedRoom;
        foreach (var saved in savedRooms)
            if (SavedRoomsList.ItemContainerGenerator.ContainerFromItem(saved) is ListBoxItem pill)
            {
                var people = saved == live ? null : PeopleInRoom(saved);
                pill.Tag = saved == live ? "live" : people is { Length: > 0 } ? "busy" : null;
                pill.ToolTip = saved == live ? "You're in this room" : people is { Length: > 0 } ? RoomSessions.Names(people) + (people.Length == 1 ? " is" : " are") + " in here · Double-click to join" : "Double-click to join";
            }
    }
    // The selected room's last session: its picture when one is known, otherwise a card in the title's colour.
    private string? savedRoomSessionKey;
    private void ShowSavedRoomSession(SavedRoom? saved)
    {
        var session = saved is null ? null : LoadRoomSession(saved);
        SavedRoomSessionTitle.Text = session?.Title ?? "Nothing watched here yet";
        SavedRoomSessionTitle.Foreground = (System.Windows.Media.Brush)FindResource(session is null ? "Muted" : "Ink");
        SavedRoomSessionDetail.Text = session is null ? "It appears here after someone plays a video."
            : (session.DurationMs > 0 && session.PositionMs >= session.DurationMs - 60000 ? "Finished" : "Stopped at " + FormatTime(session.PositionMs)) + " · " + Ago(session.UpdatedAt);
        SavedRoomSessionThumb.Visibility = session is null ? Visibility.Collapsed : Visibility.Visible;
        if (saved is null || session is null) { savedRoomSessionKey = null; SavedRoomSessionImage.Source = null; return; }
        // A video this app played comes with artwork from its own library; someone else's was saved from their app.
        string? file = session.LibraryId is { } id && items.FirstOrDefault(x => x.Id == id) is { } item ? HeroArtworkPath(item) : null;
        if (file is null && library.Setting(SavedRoomSetting(saved, "artwork")) == session.Title) file = HeroArtworkFile(saved);
        var key = session.Title + "|" + file + "|" + (file is not null && File.Exists(file) ? File.GetLastWriteTimeUtc(file).Ticks : 0);
        if (key == savedRoomSessionKey) return;
        savedRoomSessionKey = key;
        var hue = TitleHue(session.Title);
        var fill = new System.Windows.Media.LinearGradientBrush(Hsl(hue, .5, .16), Hsl(hue, .55, .34), 45); fill.Freeze();
        SavedRoomSessionThumb.Background = fill;
        SavedRoomSessionInitials.Text = Initials(session.Title, 2);
        SavedRoomSessionImage.Source = file is not null && File.Exists(file) ? LoadThumbnail(file) : null;
    }
    private static System.Windows.Media.Imaging.BitmapSource? LoadThumbnail(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            var image = new System.Windows.Media.Imaging.BitmapImage(); image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; image.DecodePixelWidth = 160; image.StreamSource = stream; image.EndInit(); image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException) { return null; }
    }
    private void CopySavedInvitation(object sender, RoutedEventArgs e)
    {
        if (SavedRoomsList.SelectedItem is not SavedRoom saved) return;
        try { Clipboard.SetText(saved.Server + "#" + saved.Code); SetStatus("Invitation copied."); }
        catch (System.Runtime.InteropServices.ExternalException) { SetStatus("Clipboard is busy."); }
    }
}
