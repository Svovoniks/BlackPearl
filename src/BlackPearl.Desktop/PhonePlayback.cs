using System.Text.Json;
using System.Windows;
using BlackPearl.Core;

namespace BlackPearl.Desktop;

// The window's part in what BlackPearl Mobile does with the library (PhoneLibrary.cs): a phone taking over the video
// playing here, progress shown on the cards, and Continue on the PC.
public partial class MainWindow
{
    // The window also has a player, whose Shuffle a phone can turn on, whose tracks it can change and whose sound it can
    // play (PhoneSound.cs); the phone host (PhoneHost.cs) has none.
    private static readonly string[] PhoneFeatures = [.. PhoneLibrary.Features, "shuffle", "tracks", "sound"];
    private PhoneLibrary? phoneLibrary;
    // The title of the video a phone took over, which the empty player names; see RefreshPlayerEmpty.
    private string? watchingOnPhone;
    private PhoneLibrary PhoneLibrary => phoneLibrary ??= new(library, () => phoneRemote, action => Dispatcher.BeginInvoke(action), lifetime.Token)
    {
        Items = () => items, WasWatched = WasWatched, SetWatched = SetWatched, MarkWatched = MarkWatchedFromPhone, Play = PlayFromPhone, TakeOver = TakeOverForPhone,
        PlaysHere = id => room is null && playingItem?.Id == id && HasMediaPlayback && WantsMediaPlayback,
        ProgressSaved = PhoneProgressSaved,
        ContinueChanged = RefreshHome,
        // The phone remote starts before the library has loaded; until then the library would look empty to phones.
        Ready = libraryReady.Task,
    };

    private void AttachPhonePlayback(PhoneRemoteHost remote) => PhoneLibrary.Attach(remote);
    private void ClosePhoneStreams() => phoneLibrary?.CloseStreams();

    // The phone continues the video playing here: this PC saves where it was and closes its player. Where the PC is now,
    // also for a title watched before, whose saved position stays 0 while it is watched again. A video still opening
    // holds its resume point until it plays.
    // The player closes rather than stops: a stopped video stays loaded, and Space, a headset's play button or Windows'
    // media controls then started it again here, where the PC ignores the phone's progress on the video it plays.
    // The phone goes on with this viewing: a rewatch when it began on a watched video and wasn't seen to the end yet.
    private async Task<(long At, bool Rewatch)?> TakeOverForPhone(MediaItem item)
    {
        if (room is not null || playingItem?.Id != item.Id || !HasMediaPlayback || player is null) return null;
        var takenAt = Math.Max(0, player.Time <= 0 && localResume > 0 ? localResume : PlaybackPosition());
        var rewatch = playbackStart is { } start && start.Generation == loadGeneration && start.Id == item.Id ? start.Watched : WasWatched(item.Id);
        EndUpNext(false);
        await Disconnect();
        // Another video started meanwhile keeps the player.
        if (playingItem is null && PlayerEmpty.Visibility == Visibility.Visible)
        {
            watchingOnPhone = item.QueueTitle; RefreshPlayerEmpty();
            RoomHeading.Text = "BlackPearl player"; RoomSubtitle.Text = "Continuing on your phone";
        }
        return (takenAt, rewatch);
    }

    private void PhoneProgressSaved(MediaItem item, PhoneProgress progress, bool final)
    {
        foreach (var card in libraryCards.Where(x => x.Card.Media.Id == item.Id || ShowCardOf(x, item))) UpdateCardPlayback(card);
        // Home shows the title at the front of Continue watching as soon as the phone has played enough of it to count,
        // not only once the phone leaves it: a phone left paused never says it has. Once in front, the title's place
        // and time left change when the phone leaves the video. A video Home doesn't lead with even then (one watched
        // before) refreshes it once, not on every report, until Home is built again for anything else: the PC playing
        // another title in between puts that one first, and the phone's next report moves its own back.
        if (currentPage != "Home") return;
        if (final) { phoneHomeRefreshed = null; RefreshHome(); return; }
        if (!progress.Played || homeHero?.Card.Media.Id == item.Id) return;
        if (phoneHomeRefreshed is { } done && done.Generation == homeGeneration && done.Ids.Contains(item.Id)) return;
        // Several phones watching on Home at once each refresh it once; only a build for something else starts over.
        var ids = phoneHomeRefreshed is { } last && last.Generation == homeGeneration ? last.Ids : new HashSet<string>();
        ids.Add(item.Id);
        // RefreshHome takes its generation before its first wait.
        RefreshHome();
        phoneHomeRefreshed = (homeGeneration, ids);
    }
    // The Home build the phones' progress last asked for, and the videos it was asked for.
    private (long Generation, HashSet<string> Ids)? phoneHomeRefreshed;

    // A phone marked a title watched or unwatched: as the PC's own check mark, with the cards, Home and an open details
    // page showing it.
    private void MarkWatchedFromPhone(MediaItem item, string level, int? season, bool watched)
    {
        MarkWatched(item, level, season, watched);
        if (selected is { } shown && Details.Visibility == Visibility.Visible) UpdateDetailPlayback(shown);
    }

    // Continue on the PC: plays the video here from where the phone was.
    private async Task<bool> PlayFromPhone(JsonElement message, Action<string, string> fail)
    {
        if (!PhonesCanStart) { fail("not-allowed", "This PC doesn't let phones start videos. Turn it on in Settings › Profile on the PC."); return false; }
        if (room?.IsConnected == true) { fail("in-room", "The PC is in a room. Leave the room on the PC to play this there."); return false; }
        if (PhoneLibrary.Playable(message, fail) is not { } item) return false;
        PhoneLibrary.SaveHandOver(item, PhoneLibrary.Position(message));
        // From one of this PC's queues: it becomes the player's queue, as when played here, and Next goes on through it.
        // Not while a room is open with its connection dropped: the room keeps its queue, and the video plays on its own.
        if (room is null && PhoneLibrary.QueuePlace(message, item.Id) is ({ } fromQueue, var place) && savedQueues.FirstOrDefault(q => q.Id == fromQueue.Id) is { } saved)
        {
            SelectPlaybackQueue(saved);
            await PlayQueueAt(PhoneQueues.PlaybackIndex(saved, MediaById(), place));
            return true;
        }
        queuePosition = -1;
        await PlayLocalItem(item);
        PublishQueue();
        return true;
    }
}
