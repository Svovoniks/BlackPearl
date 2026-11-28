using LibVLCSharp.Shared;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BlackPearl.Core;

namespace BlackPearl.Desktop;

public partial class MainWindow
{
    private sealed class SubtitleRuleDraft(string audio, string subtitles) : INotifyPropertyChanged
    {
        private string audioLanguage = TrackLanguages.Normalize(audio) ?? audio, subtitleLanguages = subtitles;
        public string AudioLanguage { get => audioLanguage; set { audioLanguage = TrackLanguages.Normalize(value) ?? value ?? ""; PropertyChanged?.Invoke(this, new(nameof(AudioLanguage))); } }
        public string SubtitleLanguages { get => subtitleLanguages; set { subtitleLanguages = value; PropertyChanged?.Invoke(this, new(nameof(SubtitleLanguages))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private readonly ObservableCollection<SubtitleRuleDraft> subtitleRuleDrafts = [];
    private string[] audioLanguages = [], subtitleLanguages = [];
    private string[] videoAudioLanguages = [], videoSubtitleLanguages = [];
    private SubtitleLanguageRule[] subtitleLanguageRules = [], videoSubtitleLanguageRules = [];
    private string? subtitleAudioLanguage;
    private int? videoDefaultSubtitle;
    private int preferredTrackGeneration = -1;
    private bool audioTrackChosen, subtitleTrackChosen, subtitleTrackManuallyChosen;
    // This video's audio started on the track picked by hand for an earlier episode, rather than on the preferences.
    private bool audioTrackCarried;
    // What was picked by hand for each show while the app runs; the show's next episode starts with it.
    private readonly SessionTracks sessionTracks = new();
    private ChosenTrack? videoChosenAudio, videoChosenSubtitles;
    // The show whose tracks this player picks: the video played here, or the room's when it is this app's to play.
    private string? TrackShow => room?.Identity?.Host == false ? null : SessionTracks.Key(playingItem);

    // A track picked by hand (in the menu, from a phone, or by someone sharing the controls of this app's room) is the
    // one the show's next episode starts with.
    private void RememberTrack(TrackType type, int id)
    {
        if (player is null || TrackShow is not { } show) return;
        var subtitles = type == TrackType.Text;
        if (id == -1) { if (subtitles) sessionTracks.Remember(show, new(null, null, true), true); return; }
        var choices = (subtitles ? player.SpuDescription.Select(t => new TrackChoice(t.Id, RoomSubtitleName(t.Id, t.Name)))
            : player.AudioTrackDescription.Select(t => new TrackChoice(t.Id, t.Name))).ToArray();
        if (LanguageTracks(choices, type).FirstOrDefault(t => t.Id == id) is { } track) sessionTracks.Remember(show, ChosenTrack.Of(track), subtitles);
    }

    private void LoadTrackPreferences()
    {
        audioLanguages = subtitleLanguages = []; subtitleLanguageRules = [];
        try { audioLanguages = TrackLanguages.Parse(library.Setting("audioLanguages") ?? ""); } catch (ArgumentException) { }
        try { subtitleLanguages = TrackLanguages.Parse(library.Setting("subtitleLanguages") ?? "", true); } catch (ArgumentException) { }
        try
        {
            var savedRules = Wire.Read<SubtitleLanguageRule[]>(library.Setting("subtitleLanguageRules") ?? "[]") ?? [];
            if (savedRules.Any(rule => rule?.AudioLanguage is null || rule.Languages is null)) throw new ArgumentException("Invalid saved subtitle rules.");
            subtitleLanguageRules = TrackLanguages.ParseSubtitleRules(savedRules.Select(rule =>
                new KeyValuePair<string, string>(rule.AudioLanguage, string.Join(", ", rule.Languages))));
        }
        catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException) { }
        PreferredAudioLanguages.Text = string.Join(", ", audioLanguages);
        PreferredSubtitleLanguages.Text = string.Join(", ", subtitleLanguages);
        subtitleRuleDrafts.Clear();
        foreach (var rule in subtitleLanguageRules) AddSubtitleRuleDraft(rule.AudioLanguage, string.Join(", ", rule.Languages));
    }
    private void AddSubtitleRuleDraft(string audio = "", string subtitles = "")
    {
        var draft = new SubtitleRuleDraft(audio, subtitles);
        draft.PropertyChanged += (_, _) => PreferencesChanged(this, new RoutedEventArgs());
        subtitleRuleDrafts.Add(draft);
    }
    private void AddSubtitleRule(object sender, RoutedEventArgs e)
    {
        AddSubtitleRuleDraft(); PreferencesChanged(sender, e);
        SubtitleLanguageRules.UpdateLayout();
        var container = SubtitleLanguageRules.ItemContainerGenerator.ContainerFromIndex(subtitleRuleDrafts.Count - 1) as FrameworkElement;
        container?.BringIntoView();
        container?.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }
    private void RemoveSubtitleRule(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SubtitleRuleDraft draft) return;
        subtitleRuleDrafts.Remove(draft); PreferencesChanged(sender, e);
    }
    private bool TryReadTrackPreferences(out string[] audio, out string[] subtitles, out SubtitleLanguageRule[] rules)
    {
        audio = subtitles = []; rules = [];
        try
        {
            audio = TrackLanguages.Parse(PreferredAudioLanguages.Text);
            subtitles = TrackLanguages.Parse(PreferredSubtitleLanguages.Text, true);
            // A rule just added and not filled in yet waits rather than being reported as invalid.
            rules = TrackLanguages.ParseSubtitleRules(subtitleRuleDrafts.Where(rule => !string.IsNullOrWhiteSpace(rule.AudioLanguage) || !string.IsNullOrWhiteSpace(rule.SubtitleLanguages)).Select(rule =>
                new KeyValuePair<string, string>(rule.AudioLanguage, rule.SubtitleLanguages)));
            return true;
        }
        catch (ArgumentException ex) { TrackPreferencesError.Text = ex.Message; return false; }
    }
    private void PersistTrackPreferences(string[] audio, string[] subtitles, SubtitleLanguageRule[] rules)
    {
        audioLanguages = audio; subtitleLanguages = subtitles; subtitleLanguageRules = rules;
        library.Setting("audioLanguages", string.Join(", ", audio));
        library.Setting("subtitleLanguages", string.Join(", ", subtitles));
        library.Setting("subtitleLanguageRules", Wire.Serialize(rules));
    }
    private bool SaveTrackPreferences()
    {
        if (!TryReadTrackPreferences(out var audio, out var subtitles, out var rules)) return false;
        PersistTrackPreferences(audio, subtitles, rules); return true;
    }
    private LanguageTrack[] LanguageTracks(TrackChoice[] choices, TrackType type)
    {
        using var media = player?.Media;
        var metadata = media?.Tracks ?? [];
        return choices.Select(choice =>
        {
            var (name, language) = TrackLanguage(type, choice.Id, choice.Name, metadata.FirstOrDefault(track => track.Id == choice.Id && track.TrackType == type).Language);
            return new LanguageTrack(choice.Id, name, language);
        }).ToArray();
    }
    private int? ApplySubtitlePreference(LanguageTrack? audio, LanguageTrack[] subtitles, bool restoreDefault = false)
    {
        if (player is null || subtitleTrackManuallyChosen || room?.Identity?.Host == false) return null;
        var preferences = TrackLanguages.SubtitlePreferences(audio, videoSubtitleLanguages, videoSubtitleLanguageRules);
        var match = TrackLanguages.Select(subtitles, preferences, true);
        if ((match ?? (restoreDefault ? videoDefaultSubtitle : null)) is not { } preferred ||
            preferred != -1 && !subtitles.Any(track => track.Id == preferred)) return null;
        if (player.Spu != preferred && !player.SetSpu(preferred)) return null;
        subtitleTrackChosen = match is not null; return preferred;
    }
    private int? ApplySubtitlesForAudio(int id)
    {
        if (player is null) return null;
        var audio = LanguageTracks(player.AudioTrackDescription.Select(track => new TrackChoice(track.Id, track.Name)).ToArray(), TrackType.Audio);
        var subtitles = LanguageTracks(player.SpuDescription.Select(track => new TrackChoice(track.Id, RoomSubtitleName(track.Id, track.Name))).ToArray(), TrackType.Text);
        var selected = audio.FirstOrDefault(track => track.Id == id);
        subtitleAudioLanguage = TrackLanguages.LanguageOf(selected);
        subtitleTrackChosen = false;
        return ApplySubtitlePreference(selected, subtitles, restoreDefault: true);
    }
    private void ApplyTrackPreferences(TrackChoice[] audio, TrackChoice[] subtitles)
    {
        if (player is null || !ready) return;
        if (room?.Identity?.Host == false) return;
        if (preferredTrackGeneration != loadGeneration)
        {
            preferredTrackGeneration = loadGeneration; audioTrackChosen = subtitleTrackChosen = subtitleTrackManuallyChosen = audioTrackCarried = false;
            subtitleAudioLanguage = null;
            videoDefaultSubtitle = player.Spu;
            videoAudioLanguages = audioLanguages; videoSubtitleLanguages = subtitleLanguages;
            videoSubtitleLanguageRules = subtitleLanguageRules;
            // Picked by hand earlier for this show: ahead of the preferences, even where they say otherwise.
            videoChosenAudio = sessionTracks.Audio(TrackShow); videoChosenSubtitles = sessionTracks.Subtitles(TrackShow);
        }
        var audioChoices = LanguageTracks(audio, TrackType.Audio);
        if (room?.Snapshot?.Tracks?.Audio is null && !audioTrackChosen && !TrackLanguagesPending(TrackType.Audio))
        {
            var carried = SessionTracks.Match(audioChoices, videoChosenAudio);
            if ((carried ?? TrackLanguages.Select(audioChoices, videoAudioLanguages)) is { } preferredAudio)
            {
                audioTrackChosen = player.AudioTrack == preferredAudio || player.SetAudioTrack(preferredAudio);
                audioTrackCarried = audioTrackChosen && carried is not null;
            }
        }
        var selectedAudio = audioChoices.FirstOrDefault(track => track.Id == player.AudioTrack);
        var language = TrackLanguages.LanguageOf(selectedAudio);
        var restoreDefault = language != subtitleAudioLanguage && subtitleTrackChosen;
        if (language != subtitleAudioLanguage) { subtitleAudioLanguage = language; subtitleTrackChosen = false; }
        if (room?.Snapshot?.Tracks?.Subtitles is null && !subtitleTrackChosen && !TrackLanguagesPending())
        {
            var subtitleChoices = LanguageTracks(subtitles, TrackType.Text);
            // Subtitles picked by hand stay through another audio track, in the episode they were picked in and after.
            if (!subtitleTrackManuallyChosen && SessionTracks.Match(subtitleChoices, videoChosenSubtitles, true) is { } chosen &&
                (player.Spu == chosen || player.SetSpu(chosen)))
                subtitleTrackChosen = subtitleTrackManuallyChosen = true;
            else ApplySubtitlePreference(selectedAudio, subtitleChoices, restoreDefault);
        }
    }
}
