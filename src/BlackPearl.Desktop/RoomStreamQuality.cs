using System.IO;
using System.Windows;
using System.Windows.Controls;
using BlackPearl.Core;

namespace BlackPearl.Desktop;

// The quality a room's video is streamed in. Whoever plays the video chooses it in the player: Original sends
// the file as it is; a lower quality converts it on this PC while it plays, and viewers receive that instead.
public partial class MainWindow
{
    private sealed record QualityChoice(string Id, string Label, string Detail)
    {
        public override string ToString() => Label;
    }
    // What a converted stream was made from: a change to any of it means a new stream.
    private sealed record ConvertedFrom(string MediaId, string Quality, int? Audio, BurnedSubtitle? Subtitle, bool Hevc);

    private StreamConverter? streamConverter;
    private SegmentStore? streamConverterStore;
    private string? streamConverterDirectory;
    private CancellationTokenSource? streamPreparation;
    // What the running conversion, or the one being prepared, is for. Null while the file itself is streamed.
    private ConvertedFrom? convertedFrom;
    private ConvertedFrom? convertedWanted;
    private long convertedWantedSince;
    private bool streamPreparing, qualityUpdating;
    // The converted stream this viewer's player has open; null while it plays the file.
    private string? loadedStream;
    private (string Path, VideoProbe? Probe) probedVideo;
    // Why the chosen quality is not what the room gets, such as a video that is already smaller.
    private (string MediaId, string Text, bool Failed)? qualityNotice;

    private const double PlaybackDockWidth = 980;
    private string SavedStreamQuality => library.Setting(StreamQuality.SettingKey) is { } saved && StreamQuality.Find(saved) is not null ? saved : StreamQuality.OriginalId;
    private bool StreamsRoomVideo => room is { IsConnected: true, Identity.Host: true, HostedMedia: ILocalMediaSource } client && client.Snapshot?.Media is not null;

    private void InitializeStreamQuality()
    {
        QualityPillText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("SelectedItem.Label") { Source = QualityChoices, FallbackValue = "Quality", TargetNullValue = "Quality" });
    }

    // Runs with every playback tick in a room: keeps the menu current and, for whoever plays the video, the
    // conversion in step with the chosen quality and tracks.
    private void RefreshStreamQuality()
    {
        var media = room?.IsConnected == true ? room.Snapshot?.Media : null;
        var streaming = media is not null && StreamsRoomVideo;
        var stream = media is not null && room!.Snapshot!.Stream is { } offered && offered.MediaId == media.Id ? offered : null;
        // Whoever plays the video always has the choice. A viewer sees the pill while what arrives is not the file itself.
        PillStrip.SetShown(QualityPill, streaming || stream is not null);
        // The bar is as wide as its other pills need. Where the window has room it grows by this pill's width, so the
        // pills that were there keep their place; where it has none, this pill is the first to fold.
        var dockWidth = PlaybackDockWidth + (PillStrip.GetShown(QualityPill) ? QualityPill.DesiredSize.Width > 0 ? Math.Ceiling(QualityPill.DesiredSize.Width) : 100 : 0);
        if (PlaybackDock.MaxWidth != dockWidth) PlaybackDock.MaxWidth = dockWidth;
        if (media is null) { if (convertedFrom is not null || streamPreparation is not null) StopConversion(); RefreshQualityOffer(null, null); return; }
        var notice = qualityNotice is { } said && said.MediaId == media.Id ? said : ((string, string Text, bool Failed)?)null;

        var hostedPath = streaming ? (room!.HostedMedia as ILocalMediaSource)?.LocalPath : null;
        // The video's size decides which qualities are offered, so it is read as soon as the video is this app's to stream.
        if (hostedPath is not null && probedVideo.Path != hostedPath && probingVideo != hostedPath && Ffmpeg.Find() is { } converter) _ = Guard(() => ProbeHostedVideo(converter, hostedPath));
        var probe = probedVideo.Probe is { } read && probedVideo.Path == hostedPath ? read : null;
        var height = probe?.ClassHeight ?? int.MaxValue;
        // Whether this PC converts to HEVC, which decides what each quality takes. Known once the video has been read.
        var hevc = Ffmpeg.HevcEncoder is not null;
        // What sending the file takes: its size over its length.
        var durationMs = probe?.DurationMs ?? (stream is { DurationMs: > 0 } ? stream.DurationMs : loadedMedia == media.Id && loadedStream is null && player is { Length: > 0 } ? player.Length : 0);
        var fileKbps = durationMs > 0 ? media.Length * 8 / durationMs : 0;
        var choices = new List<QualityChoice> { new(StreamQuality.OriginalId, "Original", fileKbps > 0 ? $"The file, about {Mbps(fileKbps)} Mbps" : "The file as it is") };
        // A quality's bitrate is a limit: an encoder that stops at a picture quality sends most video in less.
        if (streaming) choices.AddRange(StreamQuality.For(height).Select(q => new QualityChoice(q.Id, q.Name, $"up to {q.TotalMbps(hevc):0.#} Mbps")));
        else if (stream is not null) choices.Add(new(stream.Quality, stream.Quality, stream.VideoKbps > 0 ? $"up to {Mbps(stream.VideoKbps + StreamQuality.AudioKbps)} Mbps" : "Being prepared"));
        var selected = streaming ? notice is { Failed: true } ? StreamQuality.OriginalId : SavedStreamQuality : stream?.Quality ?? StreamQuality.OriginalId;
        qualityUpdating = true;
        try
        {
            if (QualityChoices.ItemsSource is not QualityChoice[] shown || !shown.SequenceEqual(choices)) QualityChoices.ItemsSource = choices.ToArray();
            // A saved quality the video is too small for shows as the nearest one below it.
            QualityChoices.SelectedItem = ((QualityChoice[])QualityChoices.ItemsSource).FirstOrDefault(c => c.Id == selected)
                ?? ((QualityChoice[])QualityChoices.ItemsSource).Skip(1).FirstOrDefault() ?? ((QualityChoice[])QualityChoices.ItemsSource)[0];
        }
        finally { qualityUpdating = false; }
        QualityChoices.IsEnabled = streaming;
        // How fast the video reaches the slowest viewer, against what is being sent. An old measurement still says
        // what the connection carries; only a recent one means the viewer is still short of video.
        QualityAdvice? Advise(long maximumAgeMs)
        {
            if (!streaming || room!.SlowestReceiveRate(maximumAgeMs) is not { } slowest) return null;
            var sent = notice is { Failed: true } ? StreamQuality.OriginalId : SavedStreamQuality;
            var better = probe is not null && notice is not { Failed: true } ? StreamQuality.Suggest(probe, media.Length, sent, slowest.Kbps, hevc) : null;
            return new(slowest.Name, slowest.Kbps, slowest.Others, probe is null ? fileKbps : StreamQuality.NeededKbps(probe, media.Length, sent, hevc), better,
                better is null ? 0 : better.Kbps(hevc) + StreamQuality.AudioKbps);
        }
        var known = Advise(60000);
        var speed = known is null ? "" : $" {known.Name} is receiving about {Mbps(known.Kbps)} Mbps{(known.Others ? ", the slowest connection in the room" : "")}." +
            (known.Suggestion is { } better ? $" What is sent now needs about {Mbps(known.NeededKbps)}; {better.Name} {(known.SuggestionFits ? "would keep up" : "is the smallest there is")}." : "");
        QualityHint.Text = !streaming ? (stream is { Preparing: true } ? $"{StreamerName} is preparing the video in {stream.Quality}." : $"{StreamerName} chooses the quality for the room.") +
                (room!.ReceiveRate is { AgeMs: < 60000 } own ? $" You are receiving about {Mbps(own.Kbps)} Mbps." : "")
            : (notice?.Text ?? (streamPreparing ? "Preparing the converted video… " : "") +
                "A lower quality is converted on this PC while the video plays. Everyone else receives the picture with the selected audio track, and the selected subtitles drawn into it.") + speed;
        RefreshQualityOffer(media, Advise(10000));

        if (!streaming) { if (convertedFrom is not null || streamPreparation is not null) StopConversion(); return; }
        var now = Environment.TickCount64;
        if (streamedVideo.MediaId != media.Id) streamedVideo = (media.Id, now, false);
        // As a video starts, viewers are told a converted stream is coming, so they wait for it instead of starting on
        // the file and switching a moment later. If it takes more than a few seconds they start on the file after all.
        if (!streamedVideo.Announced && now - streamedVideo.Since < 1500 && stream is null && streamConverter is null && notice is null && SavedStreamQuality != StreamQuality.OriginalId)
        {
            streamedVideo.Announced = true;
            SendStream(room!, new RoomStream("preparing", media.Id, SavedStreamQuality, 0, 0, 0, 0, 0, Preparing: true));
        }
        else if (stream is { Preparing: true } && now - streamedVideo.Since > 5000) SendStream(room!, null);
        // Which encoder this PC has is found while the video is read, and a conversion is planned for it.
        if (!ready || loadedMedia != media.Id || player is null || nativeStopCount > 0 || probingVideo == hostedPath) return;
        var wanted = WantedConversion(media, notice is { Failed: true } ? StreamQuality.OriginalId : SavedStreamQuality, hevc);
        if (wanted == convertedFrom) { convertedWanted = wanted; return; }
        // Tracks settle over the first moments of a video; conversion starts once they have.
        if (wanted != convertedWanted) { convertedWanted = wanted; convertedWantedSince = now; }
        if (now - convertedWantedSince < 800 || wanted is not null && (TrackLanguagesPending(LibVLCSharp.Shared.TrackType.Audio) || TrackLanguagesPending())) return;
        _ = Guard(() => ConvertRoomVideo(room!, media, wanted));
    }

    private static string Mbps(long kbps) => (kbps / 1000.0).ToString("0.#");

    // The slowest viewer's connection against what the room is sent, and the quality that would suit it better.
    private sealed record QualityAdvice(string Name, int Kbps, bool Others, long NeededKbps, StreamQuality? Suggestion, int SuggestionKbps)
    {
        public bool SuggestionFits => Suggestion is not null && StreamQuality.Fits(SuggestionKbps, Kbps);
    }
    // The quality being offered in the player, for which video.
    private (string MediaId, string Quality)? qualityOffered;
    // The video whose offer was answered or left alone: it is not offered twice. The menu goes on saying what would fit.
    private string? qualityOfferAnswered;
    private long qualitySlowSince, qualityOfferSince, qualityFineSince;
    private const int OfferAfterMs = 10000, OfferForMs = 45000, OfferGraceMs = 5000;

    // Offers whoever plays the video a lower quality once the slowest viewer has been receiving less than is sent
    // for a while. The measurement moves, so the card neither appears nor leaves on a single reading.
    private void RefreshQualityOffer(SharedMedia? media, QualityAdvice? advice)
    {
        var now = Environment.TickCount64;
        var better = media is not null && qualityOfferAnswered != media.Id && !pictureInPicture ? advice?.Suggestion : null;
        if (better is null) qualitySlowSince = 0; else if (qualitySlowSince == 0) qualitySlowSince = now;
        var shown = qualityOffered is { } offer && offer.MediaId == media?.Id && qualityOfferAnswered != media.Id && !pictureInPicture;
        if (better is not null) qualityFineSince = 0; else if (qualityFineSince == 0) qualityFineSince = now;
        if (shown && better is null && now - qualityFineSince < OfferGraceMs) { PlaceUpNextCard(); return; }
        var show = better is not null && (shown || now - qualitySlowSince >= OfferAfterMs);
        if (show && !shown)
        {
            qualityOfferSince = now;
            PlaybackDiagnostics.Record("stream-quality-offer", new { receivedKbps = advice!.Kbps, neededKbps = advice.NeededKbps, offered = better!.Id, fits = advice.SuggestionFits });
        }
        if (show && now - qualityOfferSince > OfferForMs) { qualityOfferAnswered = media!.Id; show = false; }
        qualityOffered = show ? (media!.Id, better!.Id) : null;
        if (show)
        {
            QualityOfferText.Text = $"{advice!.Name} is receiving about {Mbps(advice.Kbps)} Mbps, and what is sent now needs about {Mbps(advice.NeededKbps)}. " +
                $"{better!.Name} (up to {Mbps(advice.SuggestionKbps)} Mbps) {(advice.SuggestionFits ? "would keep up." : "is the smallest there is.")}";
            QualityOfferSwitchText.Text = "Switch to " + better.Name;
        }
        var visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (QualityOffer.Visibility != visibility) QualityOffer.Visibility = visibility;
        PlaceUpNextCard();
    }

    private void QualityOfferAccepted(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (qualityOffered is not { } offer || !StreamsRoomVideo || room?.Snapshot?.Media?.Id != offer.MediaId) return;
        qualityOfferAnswered = offer.MediaId;
        PlaybackDiagnostics.Record("stream-quality-offer-answer", new { accepted = true, quality = offer.Quality });
        library.Setting(StreamQuality.SettingKey, offer.Quality);
        qualityNotice = null;
        RefreshStreamQuality();
    }

    private void QualityOfferDeclined(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (qualityOffered is not { } offer) return;
        qualityOfferAnswered = offer.MediaId;
        PlaybackDiagnostics.Record("stream-quality-offer-answer", new { accepted = false, quality = offer.Quality });
        RefreshStreamQuality();
    }

    // The video this app streams to the room, when it was first seen, and whether its converted stream was announced.
    private (string? MediaId, long Since, bool Announced) streamedVideo;
    private static void SendStream(RoomClient client, RoomStream? stream)
    {
        try { client.Send(new("stream", Data: stream is null ? null : Wire.Serialize(stream))); }
        catch (IOException) { }
    }

    // What the room's stream should be converted from right now, or null for the file itself.
    private ConvertedFrom? WantedConversion(SharedMedia media, string quality, bool hevc)
    {
        if (quality == StreamQuality.OriginalId || player is null) return null;
        var audio = DescribeRoomTrack(player.AudioTrack, AudioTracks.ItemsSource as TrackChoice[] ?? [], false);
        var subtitles = DescribeRoomTrack(player.Spu, SubtitleTracks.ItemsSource as TrackChoice[] ?? [], true);
        BurnedSubtitle? burned = subtitles is null or { Index: < 0 } ? null
            : subtitles.AssetId is { } asset ? room?.HostedAssetPath(asset) is { } file ? new(File: file) : null : new(Track: subtitles.Index);
        return new(media.Id, quality, audio is { Index: >= 0 } ? audio.Index : null, burned, hevc);
    }

    private string? probingVideo;
    private async Task ProbeHostedVideo(string ffmpeg, string path)
    {
        probingVideo = path;
        try { var probe = await VideoProbe.ReadAsync(ffmpeg, path, lifetime.Token); await Ffmpeg.HevcEncoderAsync(ffmpeg).WaitAsync(lifetime.Token); probedVideo = (path, probe); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { if (probingVideo == path) probingVideo = null; }
    }

    private async Task ConvertRoomVideo(RoomClient client, SharedMedia media, ConvertedFrom? wanted)
    {
        streamPreparation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        streamPreparation = cancellation; var token = cancellation.Token;
        convertedFrom = wanted;
        var previous = (streamConverter, streamConverterStore, streamConverterDirectory);
        (streamConverter, streamConverterStore, streamConverterDirectory) = (null, null, null);
        bool Current() => !token.IsCancellationRequested && room == client && client.IsConnected && convertedFrom == wanted;
        void Offer(StreamConverter? converter, RoomStream? stream)
        {
            client.HostedStream = converter;
            SendStream(client, stream);
        }
        string? directory = null; SegmentStore? store = null; StreamConverter? converter = null;
        try
        {
            if (wanted is null || client.HostedMedia is not ILocalMediaSource local) { if (previous.streamConverter is not null || client.Snapshot?.Stream is not null) Offer(null, null); return; }
            streamPreparing = true;
            if (Ffmpeg.Find() is not { } ffmpeg) { Decline(media, "The converter is missing from this copy of BlackPearl, so the file is streamed as it is.", failed: true); return; }
            if (probedVideo.Path != local.LocalPath || probedVideo.Probe is null) probedVideo = (local.LocalPath, await VideoProbe.ReadAsync(ffmpeg, local.LocalPath, token));
            if (!Current()) return;
            if (probedVideo.Probe is not { } probe) { Decline(media, "This video cannot be converted, so the file is streamed as it is.", failed: true); return; }
            // The stream is planned for the encoder it was wanted with: when that changes, another conversion replaces this one.
            var hevcEncoder = wanted.Hevc ? await Ffmpeg.HevcEncoderAsync(ffmpeg).WaitAsync(token) : null;
            if (!Current()) return;
            var plan = StreamQuality.Plan(probe, media.Length, StreamQuality.Find(wanted.Quality)!, hevcEncoder is not null);
            var quality = plan.Quality;
            if (!plan.Smaller)
            {
                Decline(media, $"This file is already smaller than {quality.Name} may be ({plan.FileKbps / 1000.0:0.0} Mbps, against up to {(plan.VideoKbps + StreamQuality.AudioKbps) / 1000.0:0.0}), so it is streamed as it is.", failed: false);
                return;
            }
            directory = Path.Combine(StreamCacheDirectory, "convert-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var subtitle = await Ffmpeg.PrepareSubtitleAsync(ffmpeg, local.LocalPath, probe, wanted.Subtitle, directory, token);
            if (!Current()) return;
            var segments = (int)((probe.DurationMs + StreamConverter.SegmentMs - 1) / StreamConverter.SegmentMs);
            store = SegmentStore.TryCreate(directory, segments);
            if (store is null) { Decline(media, "There is no room on the disk to convert the video, so the file is streamed as it is.", failed: true); return; }
            var audio = probe.AudioTracks == 0 || wanted.Audio is null ? (int?)null : Math.Min(wanted.Audio.Value, probe.AudioTracks - 1);
            var stream = new RoomStream(Guid.NewGuid().ToString("N"), media.Id, quality.Id, plan.Height, plan.VideoKbps, probe.DurationMs, StreamConverter.SegmentMs, segments);
            converter = new StreamConverter(ffmpeg, new StreamInput(local.LocalPath, probe, audio, subtitle), stream, store, hevcEncoder);
            var started = converter;
            converter.Failed += message => Dispatcher.BeginInvoke(() =>
            {
                if (streamConverter != started) return;
                Decline(media, message + " The file is streamed as it is.", failed: true);
            });
            qualityNotice = null;
            (streamConverter, streamConverterStore, streamConverterDirectory) = (converter, store, directory);
            (converter, store, directory) = (null, null, null);
            Offer(streamConverter, stream);
            PlaybackDiagnostics.Record("stream-quality", new { quality = quality.Id, height = stream.Height, kbps = stream.VideoKbps, encoder = streamConverter.Encoder, segments, audio, subtitles = subtitle is null ? "none" : subtitle.ImageTrack is null ? "text" : "picture" });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (streamPreparation == cancellation) { streamPreparation = null; streamPreparing = false; }
            CloseConversion(previous.streamConverter, previous.streamConverterStore, previous.streamConverterDirectory);
            CloseConversion(converter, store, directory);
        }

        // The chosen quality is kept; this video is streamed as the file, and the menu says why.
        void Decline(SharedMedia video, string text, bool failed)
        {
            qualityNotice = (video.Id, text, failed);
            if (failed) SetStatus(text);
            if (client.Snapshot?.Stream is not null || client.HostedStream is not null) Offer(null, null);
            var failedConverter = (streamConverter, streamConverterStore, streamConverterDirectory);
            (streamConverter, streamConverterStore, streamConverterDirectory) = (null, null, null);
            CloseConversion(failedConverter.streamConverter, failedConverter.streamConverterStore, failedConverter.streamConverterDirectory);
            // Nothing is converted for this video until the quality or the tracks change.
            convertedFrom = wanted;
        }
    }

    private static void CloseConversion(StreamConverter? converter, SegmentStore? store, string? directory)
    {
        if (converter is null && store is null && directory is null) return;
        converter?.Dispose();
        // The converted video and its subtitle files can be gigabytes: they go on another thread.
        _ = Task.Run(() =>
        {
            store?.Dispose();
            try { if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        });
    }

    private void StopConversion()
    {
        streamPreparation?.Cancel(); streamPreparation = null; streamPreparing = false;
        convertedFrom = convertedWanted = null;
        if (room is { } client && client.HostedStream == streamConverter) client.HostedStream = null;
        var closing = (streamConverter, streamConverterStore, streamConverterDirectory);
        (streamConverter, streamConverterStore, streamConverterDirectory) = (null, null, null);
        CloseConversion(closing.streamConverter, closing.streamConverterStore, closing.streamConverterDirectory);
    }

    private void QualityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (qualityUpdating || !StreamsRoomVideo || QualityChoices.SelectedItem is not QualityChoice choice) return;
        library.Setting(StreamQuality.SettingKey, choice.Id);
        qualityNotice = null;
        RefreshStreamQuality();
    }
}
