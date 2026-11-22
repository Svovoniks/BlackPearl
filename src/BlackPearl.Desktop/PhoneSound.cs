using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text.Json;
using BlackPearl.Core;

namespace BlackPearl.Desktop;

// Sound on the phone (PHONE-REMOTE.md): a phone that asks plays the sound of the video playing here, in step with the
// picture, while this PC goes quiet. The PC converts the sound alone, as a room's stream is converted, and the phone
// reads it on its media channel; it keeps itself in step by asking where the player is (`clock`, answered in Core).
// The PC's own Mute brings the sound back here.
public partial class MainWindow
{
    // How long a phone whose link dropped keeps the sound: it plays on from what it has read and usually reconnects
    // within moments.
    private const int PhoneSoundDropWaitMs = 15000;
    // A listening phone asks where the player is every second; one silent this long is taken to be gone.
    private const int PhoneSoundSilenceMs = 10000;
    private readonly PlaybackClock phoneSoundClock = new();
    // By phone id. Changed on the dispatcher thread; media reads look up the converter from others.
    private readonly ConcurrentDictionary<string, PhoneListener> phoneListeners = new();
    private readonly Dictionary<string, Task<VideoProbe?>> phoneSoundProbes = [];
    // While a phone has the sound this PC is muted; what its own mute was before is given back after.
    private bool phoneSoundMuted, mutedBeforePhoneSound;
    // The phone the player's speaker button names while it has the sound.
    private string? phoneSoundPhone;

    private sealed class PhoneListener(PhoneRemoteSession session, string name)
    {
        public PhoneRemoteSession Session { get; set; } = session;
        public string Name { get; } = name;
        // What its stream is made from, and the stream once it is ready.
        public SoundSource? Source { get; set; }
        public volatile StreamConverter? Converter;
        public SegmentStore? Store { get; set; }
        public string? Directory { get; set; }
        public int Generation { get; set; }
        public long DroppedAt { get; set; }
        public long Since { get; set; } = Environment.TickCount64;
        public void CloseStream()
        {
            PhoneLibrary.CloseConversion(Converter, Store, Directory);
            Converter = null; Store = null; Directory = null;
        }
    }

    // A video's sound in one audio track. Key changes with either; Clock only with the video, whose timeline the phone
    // follows whatever the track.
    private sealed record SoundSource(string Path, int Audio, string Clock)
    {
        public string Key => Clock + "/" + Audio.ToString(CultureInfo.InvariantCulture);
    }

    private void AttachPhoneSound(PhoneRemoteHost remote)
    {
        remote.Clock = phoneSoundClock;
        remote.Request += (session, type, message) =>
        {
            if (type == "listen") Dispatcher.BeginInvoke(() => { if (phoneRemote == remote) PhoneListen(remote, session, message); });
        };
        remote.Disconnected += session => Dispatcher.BeginInvoke(() =>
        {
            if (phoneListeners.TryGetValue(session.PhoneId, out var listener) && listener.Session == session) listener.DroppedAt = Environment.TickCount64;
        });
        // The library's resolver answers everything but the sound's pieces.
        var library = remote.ResolveMedia;
        remote.ResolveMedia = async (session, resource, ct) =>
        {
            var parts = resource.Split('/');
            if (parts is ["stream", var id, var piece] && phoneListeners.TryGetValue(session.PhoneId, out var listener) && listener.Converter is { } converter &&
                converter.Stream.Id == id && int.TryParse(piece, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < converter.Stream.Segments)
                return await converter.PhonePieceAsync(index, ct);
            return library is null ? null : await library(session, resource, ct);
        };
    }

    private void PhoneListen(PhoneRemoteHost remote, PhoneRemoteSession session, JsonElement message)
    {
        if (!message.TryGetProperty("request", out var r) || !r.TryGetInt64(out var request)) return;
        var on = message.TryGetProperty("on", out var o) && o.ValueKind == JsonValueKind.True;
        if (!on)
        {
            if (phoneListeners.TryGetValue(session.PhoneId, out var leaving)) EndPhoneSound(leaving, null);
            remote.SendMessage(session, new { type = "listen", request, on = false });
            return;
        }
        if (room is not null)
        {
            remote.SendMessage(session, new { type = "error", request, code = "in-room", message = "Sound on the phone isn't available in a room." });
            return;
        }
        var name = remote.Phones.FirstOrDefault(p => p.Id == session.PhoneId)?.Name ?? session.Device ?? "your phone";
        if (phoneListeners.TryGetValue(session.PhoneId, out var listener))
        {
            // The same phone again, as after its link came back: it keeps its stream.
            listener.Session = session; listener.DroppedAt = 0; listener.Since = Environment.TickCount64;
        }
        else
        {
            listener = new PhoneListener(session, name);
            phoneListeners[session.PhoneId] = listener;
            SetStatus($"The sound is playing on {name}. Press Mute here to hear it on the PC again.");
            PlaybackDiagnostics.Record("phone-sound", new { state = "on" });
        }
        remote.SendMessage(session, new { type = "listen", request, on = true });
        // Whatever it has now, even after a reconnect: a phone that lost a message hears it again.
        var source = CurrentSoundSource(out var why);
        if (listener.Source?.Key == source?.Key && (listener.Converter is not null || source is null)) TellPhoneSound(listener, why);
        SyncPhoneSound();
    }

    // The sound the phones should hear now, or null with why there is none (null when nothing plays).
    private SoundSource? CurrentSoundSource(out string? why)
    {
        why = null;
        if (room is not null) { why = "Sound on the phone isn't available in a room."; return null; }
        if (!ready || !HasMediaPlayback || player is null || playingItem is not { Path: { Length: > 0 } path }) return null;
        var choice = DescribeRoomTrack(player.AudioTrack, AudioTracks.ItemsSource as TrackChoice[] ?? [], false);
        if (choice is { Index: < 0 }) { why = "The PC's audio is turned off for this video."; return null; }
        // The track list isn't known yet: it is, a moment after the video starts.
        if (choice is null && player.AudioTrackCount > 0) return null;
        return new(path, choice?.Index ?? 0, PersistentRooms.Hash(path)[..16].ToLowerInvariant());
    }

    // Every quarter second while phones are linked, and after a phone's request: each listening phone gets the sound of
    // what plays now, and this PC stays quiet while any does.
    private void SyncPhoneSound()
    {
        if (phoneListeners.IsEmpty) { phoneSoundClock.Source = null; ApplyPhoneSoundMute(); return; }
        var source = CurrentSoundSource(out var why);
        phoneSoundClock.Source = source?.Clock;
        var now = Environment.TickCount64;
        foreach (var listener in phoneListeners.Values.ToArray())
        {
            var silent = now - Math.Max(listener.Since, phoneSoundClock.LastHeard(listener.Session.PhoneId) ?? 0) > PhoneSoundSilenceMs;
            if (silent || listener.DroppedAt != 0 && now - listener.DroppedAt > PhoneSoundDropWaitMs) { EndPhoneSound(listener, null); continue; }
            if (listener.Source?.Key == source?.Key) continue;
            listener.CloseStream();
            listener.Source = source;
            var generation = ++listener.Generation;
            if (source is null) TellPhoneSound(listener, why);
            else _ = StartPhoneSound(listener, source, generation);
        }
        ApplyPhoneSoundMute();
    }

    private async Task StartPhoneSound(PhoneListener listener, SoundSource source, int generation)
    {
        bool Current() => phoneListeners.TryGetValue(listener.Session.PhoneId, out var now) && now == listener && listener.Generation == generation;
        string? failed = null;
        try
        {
            if (Ffmpeg.Find() is not { } ffmpeg) { failed = "This copy of BlackPearl has no converter, so it can't send its sound to the phone."; return; }
            if (!phoneSoundProbes.TryGetValue(source.Path, out var probing) || probing.IsCompleted && probing.Result is null)
            {
                if (phoneSoundProbes.Count > 50) phoneSoundProbes.Clear();
                var token = lifetime.Token;
                phoneSoundProbes[source.Path] = probing = Task.Run(async () =>
                {
                    try { return await VideoProbe.ReadAsync(ffmpeg, source.Path, token, sound: true); }
                    catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException) { return null; }
                });
            }
            var probe = await probing;
            if (!Current()) return;
            if (probe is not { AudioTracks: > 0 }) { failed = probe is null ? "The PC couldn't read this video's sound." : "This video has no sound."; return; }
            var segments = (int)((probe.DurationMs + StreamConverter.SegmentMs - 1) / StreamConverter.SegmentMs);
            var directory = Path.Combine(PhoneLibrary.StreamCacheDirectory, "sound-" + Guid.NewGuid().ToString("N"));
            var store = SegmentStore.TryCreate(directory, segments);
            if (store is null) { failed = "There is no room on the PC's disk for the sound."; return; }
            var stream = new RoomStream(Guid.NewGuid().ToString("N"), source.Clock, "sound", 0, StreamQuality.AudioKbps, probe.DurationMs, StreamConverter.SegmentMs, segments);
            var converter = new StreamConverter(ffmpeg, new StreamInput(source.Path, probe, Math.Min(source.Audio, probe.AudioTracks - 1), null, Sound: true), stream, store);
            converter.Failed += message => Dispatcher.BeginInvoke(() => { if (Current() && listener.Converter == converter) EndPhoneSound(listener, message); });
            (listener.Converter, listener.Store, listener.Directory) = (converter, store, directory);
            PlaybackDiagnostics.Record("phone-sound", new { state = "stream", segments, audio = source.Audio });
            TellPhoneSound(listener, null);
        }
        finally
        {
            if (failed is not null && Current()) EndPhoneSound(listener, failed);
        }
    }

    // Sends a listening phone the stream it is to play now, or none (why says why, or nothing plays).
    private void TellPhoneSound(PhoneListener listener, string? why)
    {
        if (phoneRemote is not { } remote) return;
        var stream = listener.Converter?.Stream is { } s ? new PhoneSoundStream(s.Id, s.MediaId, s.Segments, s.SegmentMs, s.DurationMs) : null;
        try { remote.SendMessage(listener.Session, new { type = "sound", on = true, stream, why }); }
        catch (InvalidDataException) { }
    }

    // The phone stops playing the sound: it asked to, it is gone, or the PC couldn't go on (why tells it).
    private void EndPhoneSound(PhoneListener listener, string? why)
    {
        if (!phoneListeners.TryRemove(KeyValuePair.Create(listener.Session.PhoneId, listener))) return;
        listener.Generation++;
        listener.CloseStream();
        PlaybackDiagnostics.Record("phone-sound", new { state = "off", why });
        if (why is not null && phoneRemote is { } remote)
        {
            try { remote.SendMessage(listener.Session, new { type = "sound", on = false, why }); }
            catch (InvalidDataException) { }
        }
        ApplyPhoneSoundMute();
        if (phoneListeners.IsEmpty) SetStatus(why is null ? "The sound is back on the PC." : why + " The sound is back on the PC.");
    }

    private void EndAllPhoneSound(string why)
    {
        foreach (var listener in phoneListeners.Values.ToArray()) EndPhoneSound(listener, why);
    }

    // The PC's Mute (its button, M or a phone's mute) while a phone has the sound brings it back here.
    private bool TakeSoundBackFromPhones()
    {
        if (!phoneSoundMuted) return false;
        mutedBeforePhoneSound = false;
        EndAllPhoneSound("The sound is back on the PC.");
        return true;
    }

    private void ApplyPhoneSoundMute()
    {
        var wanted = !phoneListeners.IsEmpty;
        var name = phoneListeners.Values.FirstOrDefault()?.Name;
        if (name != phoneSoundPhone) { phoneSoundPhone = name; RefreshVolumeIndicator(); }
        if (wanted == phoneSoundMuted)
        {
            // A new video can start with the output's own mute: the PC stays quiet.
            if (wanted && player is { Mute: false }) player.Mute = true;
            return;
        }
        phoneSoundMuted = wanted;
        if (wanted) { mutedBeforePhoneSound = player?.Mute == true; if (player is not null) player.Mute = true; }
        else { if (player is not null) player.Mute = mutedBeforePhoneSound; phoneSoundRestoredAt = Environment.TickCount64; }
        RefreshVolumeIndicator();
    }

    // Whether this PC is muted as its user set it, which is what phones show while one of them has the sound. libVLC
    // reports a mute it was just given back a moment later, so for a second after that the restored one is shown.
    private long phoneSoundRestoredAt = long.MinValue / 2;
    private bool UserMuted => phoneSoundMuted || Environment.TickCount64 - phoneSoundRestoredAt < 1000 ? mutedBeforePhoneSound : player?.Mute == true;
    private string? PhoneSoundName => phoneSoundMuted ? phoneSoundPhone : null;
}
