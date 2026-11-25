using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BlackPearl.Core;

namespace BlackPearl.Desktop;

// One category of Home recommendations: a spotlight title followed by poster tiles.
public sealed class HomeRowView(string kind, string label, int category) : INotifyPropertyChanged
{
    public string Kind { get; } = kind;
    public string Label { get; } = label;
    public int Category { get; } = category;
    public string OpenLabel => $"Open {Label} in library";
    public string ReshuffleLabel => $"New {Label} suggestions";
    public LibraryCardView? Spotlight { get; private set; }
    public LibraryCardView[] Tiles { get; private set; } = [];
    public string CountText { get; private set; } = "";
    public Visibility Visibility => Spotlight is null ? Visibility.Collapsed : Visibility.Visible;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string property) => PropertyChanged?.Invoke(this, new(property));
    // Keep the existing tile array when its members are unchanged so loaded posters stay put.
    public void Update(LibraryCardView? spotlight, LibraryCardView[] tiles, string count)
    {
        if (!ReferenceEquals(Spotlight, spotlight)) { Spotlight = spotlight; Changed(nameof(Spotlight)); Changed(nameof(Visibility)); }
        if (!Tiles.SequenceEqual(tiles)) { Tiles = tiles; Changed(nameof(Tiles)); }
        if (CountText != count) { CountText = count; Changed(nameof(CountText)); }
    }
}

public partial class MainWindow
{
    private int homeSeed = Random.Shared.Next();
    private readonly Dictionary<string, int> homeRowSeeds = [];
    private LibraryCardView? homeHero, homeMystery, homeMysteryFallback;
    private LibraryCardView[] homeContinueAll = [], homeUnwatched = [];
    private HomeTitle[] homeUnseen = [], homeSeen = [];
    private Dictionary<string, HomeTitle> homeFacts = [];
    private bool homeStripExpanded;
    private string? mysteryKind;
    private MysteryLength mysteryLength;
    // Draws from the titles already watched or started instead of the ones not started; only in your own library.
    private bool mysteryRewatch;
    private int mysteryDraw = Random.Shared.Next();
    private HomeRowView[]? homeRows;
    private long homeGeneration;
    private bool homeShowsGuestLibrary;
    private HomeRowView[] HomeRowViews => homeRows ??= [new("Anime", "Anime", 3), new("Show", "Shows", 2), new("Movie", "Movies", 1)];
    private async void RefreshHome()
    {
        if (HomeContinue is null || library is null || currentPage != "Home") return;
        homeShowsGuestLibrary = GuestLibrary;
        System.Windows.Automation.AutomationProperties.SetName(HomePage, GuestLibrary ? "Home · " + HostLibraryName : "Home");
        if (GuestLibrary) { RenderGuestHome(); return; }
        if (!libraryLoaded && items.Count == 0)
        {
            HomeStatus.Text = "Loading your library…";
            homeUnseen = []; homeSeen = []; RenderHome([]); return;
        }
        var generation = ++homeGeneration;
        var source = items.ToArray(); var seed = homeSeed; var rowSeeds = new Dictionary<string, int>(homeRowSeeds);
        HomeOverview overview;
        try { overview = await Task.Run(() => LibraryHome.Overview(source, library.PlaybackSettings(), seed, rowSeeds)); }
        catch (Exception ex)
        {
            if (generation == homeGeneration && currentPage == "Home" && !closing) HomeStatus.Text = "Could not load Home: " + ex.Message;
            return;
        }
        if (generation != homeGeneration || currentPage != "Home" || GuestLibrary || closing) return;
        var existing = HomeViews().Where(view => !view.IsRemote).GroupBy(view => (view.HomeSection, view.Key))
            .ToDictionary(group => group.Key, group => group.First());
        var views = overview.Entries.Select(entry =>
        {
            var key = (entry.Section, LibraryCardView.Identity(entry.Card));
            var view = existing.GetValueOrDefault(key) ?? new LibraryCardView(entry.Card) { HomeSection = entry.Section };
            view.Update(entry.Card);
            view.HomePosition = entry.PositionMs;
            view.SetPlayback(entry.PositionMs, false);
            if (entry.Section == "continue" && entry.PositionMs < 10000) view.SetPlaybackLabel("Next episode");
            return view;
        }).ToArray();
        homeUnseen = overview.Unseen; homeSeen = overview.Seen;
        homeFacts = overview.Unseen.Concat(overview.Seen).GroupBy(title => LibraryCardView.Identity(title.Card)).ToDictionary(group => group.Key, group => group.First());
        HomeStatus.Text = items.Count == 0 ? "Add a folder to find something to watch." : "";
        RenderHome(views);
    }
    private IEnumerable<LibraryCardView> HomeViews() =>
        new[] { homeHero, homeMystery, homeMysteryFallback }.OfType<LibraryCardView>().Concat(homeContinueAll).Concat(homeUnwatched)
            .Concat(HomeRowViews.SelectMany(row => row.Tiles.Prepend(row.Spotlight).OfType<LibraryCardView>()));
    private void RenderHome(IEnumerable<LibraryCardView> source)
    {
        var views = source.ToArray();
        var continuing = views.Where(view => view.HomeSection == "continue").ToArray();
        foreach (var view in continuing) view.SetHomeText(ContinueDetail(view));
        homeHero = continuing.FirstOrDefault();
        HomeHero.DataContext = homeHero;
        HomeHero.Visibility = homeHero is null ? Visibility.Collapsed : Visibility.Visible;
        HomeIntro.Visibility = homeHero is null ? Visibility.Visible : Visibility.Collapsed;
        if (homeHero is not null)
        {
            HomeHeroKicker.Text = "Continue watching · " + homeHero.HomeDetail;
            HomeHeroTitle.Text = homeHero.HomeTitle;
            HomeHeroOverview.Text = homeHero.Card.Media.DetailOverview ?? "";
            HomeHeroOverview.Visibility = HomeHeroOverview.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            HomeHeroResumeLabel.Text = homeHero.HomePosition >= 10000 ? "Resume" : homeHero.IsEpisode ? "Play next episode" : "Play";
            HomeHeroResumeLeft.Text = RemainingMinutes(homeHero) is { } left ? Duration(Math.Max(1, left)) : "";
            System.Windows.Automation.AutomationProperties.SetName(HomeHeroResume, HomeHeroResumeLabel.Text + " " + homeHero.HomeTitle +
                (HomeHeroResumeLeft.Text.Length > 0 ? ", " + HomeHeroResumeLeft.Text + " left" : ""));
            // Each card's artwork is decoded for the frame its template draws it in; a title can move between frames.
            homeHero.ShowIn(620, 340); _ = homeHero.LoadPosterAsync();
        }
        homeContinueAll = continuing.Skip(1).ToArray();
        foreach (var view in homeContinueAll) view.ShowIn(36, 52);
        RenderHomeStrip();
        RenderHomeUnwatched(views);
        var suggestions = views.Where(view => view.HomeSection == "suggestions").ToArray();
        foreach (var view in suggestions) view.SetHomeText(TitleFacts(view), view.Card.Media.Genres?.FirstOrDefault(genre => !string.IsNullOrWhiteSpace(genre)) ?? "");
        if (HomeRows.ItemsSource is null) HomeRows.ItemsSource = HomeRowViews;
        foreach (var row in HomeRowViews)
        {
            var members = suggestions.Where(view => view.Card.Media.Kind == row.Kind).ToArray();
            var unseen = homeUnseen.Count(title => title.Card.Media.Kind == row.Kind);
            row.Update(members.FirstOrDefault(), members.Skip(1).ToArray(), GuestLibrary || unseen == 0 ? "" : $"{unseen} not started");
            // The spotlight card is reused when its title changes, so its image's Loaded event does not fire again.
            foreach (var tile in row.Tiles) tile.ShowIn(140, 210);
            if (row.Spotlight is { } spotlight) { spotlight.ShowIn(250, 210); _ = spotlight.LoadPosterAsync(); }
        }
        HomeSuggestionsEmpty.Visibility = suggestions.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        homeMysteryFallback = views.FirstOrDefault(view => view.HomeSection == "mystery");
        RenderMystery(redraw: false);
    }
    private void RenderGuestHome()
    {
        ++homeGeneration;
        homeShowsGuestLibrary = true;
        homeUnseen = []; homeSeen = []; homeFacts = [];
        System.Windows.Automation.AutomationProperties.SetName(HomePage, "Home · " + HostLibraryName);
        HomeStatus.Text = GuestLibraryStatus.Text == HostLibraryName ? "" : GuestLibraryStatus.Text;
        RenderHome(OwnsLibrary && guestBrowse.Home ? guestLibraryCards : []);
    }
    private static string Duration(int minutes) => minutes >= 60 ? $"{minutes / 60} h {minutes % 60:00} m" : $"{minutes} min";
    private static int? RemainingMinutes(LibraryCardView view) => view.Card.Media.RuntimeMinutes is > 0 and var runtime && view.HomePosition >= 10000
        ? (int)Math.Ceiling(Math.Max(0, runtime * 60000L - view.HomePosition) / 60000d) : null;
    private static string ContinueDetail(LibraryCardView view)
    {
        var media = view.Card.Media;
        var episode = view.IsEpisode && media.Episode is { } number ? (media.Season is { } season ? $"S{season} E{number}" : $"Episode {number}") : null;
        var remaining = RemainingMinutes(view);
        var state = view.HomePosition < 10000 ? episode is null ? "Not started" : "Up next" : remaining is { } left ? Duration(Math.Max(1, left)) + " left" : "In progress";
        return episode is null ? state : episode + " · " + state;
    }
    private string TitleFacts(LibraryCardView view)
    {
        var media = view.Card.Media; var parts = new List<string>();
        if (view.Card.Level is "series" or "season")
        {
            var episodes = homeFacts.GetValueOrDefault(view.Key)?.Episodes ?? view.RemoteEpisodes;
            parts.Add(episodes > 0 ? episodes == 1 ? "1 episode" : $"{episodes} episodes" : media.Kind == "Anime" ? "Anime" : "Series");
        }
        else if (media.RuntimeMinutes is > 0 and var runtime) parts.Add(Duration(runtime));
        if (media.Year is { } year) parts.Add(year.ToString());
        return string.Join(" · ", parts);
    }
    private void RenderHomeStrip()
    {
        var columns = HomeStrip.Tag as int? ?? 4;
        var shown = homeStripExpanded ? homeContinueAll : homeContinueAll.Take(columns).ToArray();
        if (HomeContinue.ItemsSource is not LibraryCardView[] previous || !previous.SequenceEqual(shown)) HomeContinue.ItemsSource = shown;
        HomeStrip.Visibility = homeContinueAll.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        HomeStripCount.Text = homeContinueAll.Length == 1 ? "1 title" : $"{homeContinueAll.Length} titles";
        HomeStripMore.Visibility = homeContinueAll.Length > columns ? Visibility.Visible : Visibility.Collapsed;
        HomeStripMore.Content = homeStripExpanded ? "Show less" : $"All {homeContinueAll.Length}";
    }
    // Unwatched: the titles not started yet, newest first. A streamer's app from before the row sends none, and it hides.
    private void RenderHomeUnwatched(LibraryCardView[] views)
    {
        var unwatched = views.Where(view => view.HomeSection == "unwatched").ToArray();
        foreach (var view in unwatched) { view.SetHomeText(TitleFacts(view)); view.ShowIn(140, 210); }
        if (!homeUnwatched.SequenceEqual(unwatched)) { homeUnwatched = unwatched; HomeUnwatched.ItemsSource = unwatched; }
        HomeUnwatchedSection.Visibility = unwatched.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        // The whole list is the library's Unwatched filter, which someone else's library doesn't offer.
        var all = homeUnseen.Length;
        HomeUnwatchedCount.Text = GuestLibrary || all == 0 ? "Newest first" : $"Newest first · {all} not started";
        HomeUnwatchedAll.Visibility = GuestLibrary ? Visibility.Collapsed : Visibility.Visible;
    }
    // See all: the library's search, listing every title with an unwatched status.
    private void OpenHomeUnwatched(object sender, RoutedEventArgs e)
    {
        if (GuestLibrary) return;
        OpenSearch(focus: false);
        if (!librarySearch || currentPage != "Library") return;
        ApplySearchFilters(new(), 3); SearchBox.Clear();
        FilterLibrary();
    }
    private void HomeStripResized(object sender, SizeChangedEventArgs e)
    {
        var columns = Math.Clamp((int)((e.NewSize.Width - 150) / 210), 1, 4);
        if (HomeStrip.Tag as int? == columns) return;
        HomeStrip.Tag = columns; RenderHomeStrip();
    }
    private double homeStripCollapsedHeight;
    // The bar grows to its new height while the extra titles fade in, and shrinks before they leave.
    private void ToggleHomeStrip(object sender, RoutedEventArgs e)
    {
        var from = HomeStrip.ActualHeight;
        homeStripExpanded = !homeStripExpanded;
        HomeStrip.BeginAnimation(HeightProperty, null);
        if (!SystemParameters.ClientAreaAnimation || !HomeStrip.IsLoaded) { RenderHomeStrip(); return; }
        // Clipped while its height animates, then let go, so the pebbles' shadows are not cut off once it settles.
        HomeStrip.ClipToBounds = true;
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        if (homeStripExpanded)
        {
            homeStripCollapsedHeight = from;
            RenderHomeStrip(); HomeStrip.UpdateLayout();
            var grow = new DoubleAnimation(from, HomeStrip.ActualHeight, TimeSpan.FromMilliseconds(340)) { EasingFunction = ease };
            grow.Completed += (_, _) => { if (homeStripExpanded) { HomeStrip.BeginAnimation(HeightProperty, null); HomeStrip.ClipToBounds = false; } };
            HomeStrip.BeginAnimation(HeightProperty, grow);
        }
        else
        {
            HomeStripMore.Content = $"All {homeContinueAll.Length}";
            var shrink = new DoubleAnimation(from, homeStripCollapsedHeight > 0 ? homeStripCollapsedHeight : from, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease };
            shrink.Completed += (_, _) => { if (homeStripExpanded) return; RenderHomeStrip(); HomeStrip.BeginAnimation(HeightProperty, null); HomeStrip.ClipToBounds = false; };
            HomeStrip.BeginAnimation(HeightProperty, shrink);
        }
    }
    private Action? mysteryReveal;
    private void RenderMystery(bool redraw, bool animate = false)
    {
        RequestGuestDraw(animate);
        // In someone else's library their app draws from their unstarted titles; apps from before that send one fixed pick.
        var remote = GuestLibrary ? guestDraw : null;
        // Until their first answer the filters keep their place and the card stays blank, so the deck does not jump.
        var waiting = remote is null && guestDrawPending is not null;
        var filtered = GuestLibrary ? remote is { Unseen: > 0 } : homeUnseen.Length + homeSeen.Length > 0;
        HomeMysteryFilters.Visibility = filtered || waiting ? Visibility.Visible : Visibility.Collapsed;
        // Your own library can also draw something to watch again; someone else's app draws only titles not started.
        var rewatch = mysteryRewatch && !GuestLibrary;
        MysteryPoolBar.Visibility = GuestLibrary ? Visibility.Collapsed : Visibility.Visible;
        LibraryCardView? pick = waiting ? null : homeMysteryFallback; var note = ""; var empty = "Try another type or length.";
        if (waiting) HomeMysteryDraw.IsEnabled = false;
        else if (remote is { Unseen: > 0 })
        {
            pick = remote.Pick;
            if (pick is not null) note = "Not started yet";
            HomeMysteryDraw.IsEnabled = remote.Matches > 1;
        }
        else if (filtered)
        {
            var pool = rewatch ? homeSeen : homeUnseen;
            var current = homeMystery is { HomeSection: "draw" } shown ? LibraryIdentity.TitleKey(shown.Card.Media) : null;
            var title = LibraryHome.Pick(pool, mysteryKind, mysteryLength, mysteryDraw, current, redraw);
            pick = title is null ? null : homeMystery is { HomeSection: "draw" } same && same.Key == LibraryCardView.Identity(title.Card) ? same : new LibraryCardView(title.Card) { HomeSection = "draw" };
            if (title is not null) { pick!.Update(title.Card); note = !rewatch ? "Not started yet" : title.Finished ? "Watched" : "In progress"; }
            HomeMysteryDraw.IsEnabled = pool.Count(candidate => LibraryHome.Matches(candidate, mysteryKind, mysteryLength)) > 1;
            if (pool.Length == 0) empty = rewatch ? "You haven't watched anything yet." : "You've started everything. Try Watched.";
        }
        else HomeMysteryDraw.IsEnabled = pick is not null;
        var changed = !ReferenceEquals(homeMystery, pick);
        homeMystery = pick;
        HomeMysteryPlay.DataContext = pick;
        HomeMysteryPlay.IsEnabled = pick is not null;
        var heading = pick?.DisplayTitle ?? (filtered ? "Nothing matches" : "");
        var meta = pick is null ? filtered ? empty : "" :
            string.Join(" · ", new[] { pick.Card.Media.Kind, TitleFacts(pick), note }.Where(part => part.Length > 0));
        if (pick is not null) { pick.ShowIn(128, 190); _ = pick.LoadPosterAsync(); }
        void Reveal()
        {
            HomeMysteryCard.DataContext = pick;
            HomeMysteryCard.Visibility = pick is null ? Visibility.Hidden : Visibility.Visible;
            HomeMysteryCard.IsEnabled = pick is not null;
            HomeMysteryTitle.Text = heading; HomeMysteryMeta.Text = meta;
        }
        if (animate && changed) FlipMysteryCard(Reveal); else { mysteryReveal = null; Reveal(); }
    }
    // The streamer's latest draw and the filters, draw and Home visit it answers; a change of any of them asks again.
    private sealed record GuestDraw(string? Kind, MysteryLength Length, int Seed, int Visit, LibraryCardView? Pick, int Matches, int Unseen);
    private sealed record GuestDrawRequest(long Number, string? Kind, MysteryLength Length, int Seed, int Visit, bool Animate, long Sent);
    private GuestDraw? guestDraw;
    private GuestDrawRequest? guestDrawPending;
    private long guestDrawSequence;
    private void RequestGuestDraw(bool animate)
    {
        if (!GuestLibrary || !OwnsLibrary) { guestDraw = null; guestDrawPending = null; return; }
        if (room?.StreamerDrawsTitles != true) return;
        bool Current(string? kind, MysteryLength length, int seed, int visit) =>
            kind == mysteryKind && length == mysteryLength && seed == mysteryDraw && visit == homeSeed;
        // An unanswered request is given up after the time the library itself waits.
        if (guestDrawPending is { } waiting && Environment.TickCount64 - waiting.Sent > 15000) guestDrawPending = null;
        if (guestDraw is { } shown && Current(shown.Kind, shown.Length, shown.Seed, shown.Visit) ||
            guestDrawPending is { } pending && Current(pending.Kind, pending.Length, pending.Seed, pending.Visit)) return;
        // Above the library's own request numbers, and positive unlike artwork's, so replies cannot be confused.
        var request = new GuestDrawRequest((1L << 40) + ++guestDrawSequence, mysteryKind, mysteryLength, mysteryDraw, homeSeed,
            animate || guestDrawPending?.Animate == true, Environment.TickCount64);
        guestDrawPending = request;
        // A new draw number is Draw another; a new filter or visit keeps the current pick while it still fits.
        // One that could not be sent is not waited on: the card shows what it had, and the next refresh asks again.
        try
        {
            room.Send(new("catalog-draw", Number: request.Number, Data: Wire.Serialize(new LibraryDrawRequest(mysteryKind, mysteryLength, mysteryDraw,
                guestDraw?.Pick?.Card.Media.Id, guestDraw is { } previous && previous.Seed != mysteryDraw))));
        }
        catch (System.IO.IOException ex) { guestDrawPending = null; SetStatus(ex.Message); }
    }
    private void ReceiveGuestDraw(WireMessage message)
    {
        if (guestDrawPending is not { } request || message.Number != request.Number) return;
        guestDrawPending = null;
        if (!GuestLibrary || !OwnsLibrary) return;
        LibraryDraw draw;
        try { draw = Wire.Read<LibraryDraw>(message.Data ?? ""); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or System.IO.InvalidDataException)
        {
            guestDrawPending = request; ReceiveGuestDrawError(message with { Type = "catalog-error", Text = "Could not draw a title." }); return;
        }
        var shown = guestDraw?.Pick;
        var pick = draw.Entry is not { } entry ? null : shown is not null && shown.Card.Media.Id == entry.Id && shown.Card.Level == entry.Level ? shown : LibraryCardView.FromSharedEntry(entry);
        if (pick is not null && !ReferenceEquals(pick, shown)) pick.RemoteArtworkLoader = () => LoadHostArtwork(pick.Card);
        guestDraw = new(request.Kind, request.Length, request.Seed, request.Visit, pick, draw.Matches, draw.Unseen);
        if (currentPage == "Home") RenderMystery(redraw: false, animate: request.Animate);
    }
    // A failed draw keeps the card as it was, answered for these filters, so it is not asked again on every refresh.
    private bool ReceiveGuestDrawError(WireMessage message)
    {
        if (message.Type != "catalog-error" || guestDrawPending is not { } request || message.Number != request.Number) return false;
        guestDrawPending = null;
        guestDraw = guestDraw is { } shown ? shown with { Kind = request.Kind, Length = request.Length, Seed = request.Seed, Visit = request.Visit }
            : new(request.Kind, request.Length, request.Seed, request.Visit, null, 0, 0);
        SetStatus(message.Text ?? "Could not draw a title.");
        if (currentPage == "Home") RenderMystery(redraw: false);
        return true;
    }
    // Turns the drawn card over: it folds to its edge, swaps title, and opens again.
    private void FlipMysteryCard(Action reveal)
    {
        if (!SystemParameters.ClientAreaAnimation || !HomeMysteryPanel.IsVisible) { mysteryReveal = null; reveal(); return; }
        if (HomeMysteryCard.RenderTransform is not ScaleTransform scale)
        {
            HomeMysteryCard.RenderTransformOrigin = new(0.5, 0.5);
            HomeMysteryCard.RenderTransform = scale = new ScaleTransform();
        }
        var flipping = mysteryReveal is not null;
        mysteryReveal = reveal;
        if (flipping) return;
        var text = new DoubleAnimation(0.25, TimeSpan.FromMilliseconds(130));
        HomeMysteryTitle.BeginAnimation(OpacityProperty, text); HomeMysteryMeta.BeginAnimation(OpacityProperty, text);
        FanDeck(gather: true);
        var fold = new DoubleAnimation(0, TimeSpan.FromMilliseconds(170)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        fold.Completed += (_, _) =>
        {
            var show = mysteryReveal; mysteryReveal = null; show?.Invoke();
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300))
                { EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut } });
            FanDeck(gather: false);
            var restore = new DoubleAnimation(1, TimeSpan.FromMilliseconds(220));
            HomeMysteryTitle.BeginAnimation(OpacityProperty, restore); HomeMysteryMeta.BeginAnimation(OpacityProperty, restore);
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, fold);
    }
    private void FanDeck(bool gather)
    {
        IEasingFunction ease = gather ? new QuadraticEase { EasingMode = EasingMode.EaseIn } : new BackEase { Amplitude = 0.45, EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(gather ? 170 : 420);
        void Move(Animatable target, DependencyProperty property, double to) =>
            target.BeginAnimation(property, new DoubleAnimation(to, duration) { EasingFunction = ease });
        Move(MysteryLeftTilt, RotateTransform.AngleProperty, gather ? -3 : -11);
        Move(MysteryRightTilt, RotateTransform.AngleProperty, gather ? 3 : 11);
        Move(MysteryLeftShift, TranslateTransform.XProperty, gather ? -22 : -78);
        Move(MysteryRightShift, TranslateTransform.XProperty, gather ? 22 : 78);
        Move(MysteryLeftShift, TranslateTransform.YProperty, gather ? 2 : 8);
        Move(MysteryRightShift, TranslateTransform.YProperty, gather ? 2 : 8);
    }
    private void MysteryFilterChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } button || HomeMysteryCard is null) return;
        if (button.GroupName == "MysteryPool") mysteryRewatch = tag == "Seen";
        else if (button.GroupName == "MysteryKind") mysteryKind = tag.Length == 0 ? null : tag;
        else mysteryLength = Enum.Parse<MysteryLength>(tag);
        RenderMystery(redraw: false, animate: true);
    }
    private void DrawMystery(object sender, RoutedEventArgs e)
    {
        if (GuestLibrary ? guestDraw is not { Unseen: > 0 } : homeUnseen.Length + homeSeen.Length == 0) { RefreshHomePicks(sender, e); return; }
        mysteryDraw = Random.Shared.Next(); RenderMystery(redraw: true, animate: true);
    }
    private void RefreshHomePicks(object sender, RoutedEventArgs e)
    {
        homeSeed = Random.Shared.Next(); homeRowSeeds.Clear();
        if (GuestLibrary)
        {
            guestBrowse = new(Home: true, Seed: homeSeed);
            guestLibraryNotice = null; guestCurrentBrowse = null; guestLibraryCards.Clear();
            RequestHostLibrary(); RenderGuestHome();
        }
        else RefreshHome();
    }
    private void ReshuffleHomeRow(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: HomeRowView row }) return;
        if (GuestLibrary) { RefreshHomePicks(sender, e); return; }
        homeRowSeeds[row.Kind] = Random.Shared.Next(); RefreshHome();
    }
    private void OpenHomeCategory(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HomeRowView row }) NavigateLibrary(row.Category);
    }
    private async void HomeArtworkLoaded(object sender, RoutedEventArgs e)
    {
        PosterLease.Watch((Image)sender);
        if (((Image)sender).DataContext is LibraryCardView view) await view.LoadPosterAsync();
    }
    // Artwork that arrives after its card is on screen fades in; artwork present at creation rides the card entrance.
    private void HomeArtworkUpdated(object? sender, DataTransferEventArgs e)
    {
        if (sender is not Image { IsLoaded: true, Source: not null } image || e.Property != Image.SourceProperty || !SystemParameters.ClientAreaAnimation) return;
        image.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
    // New cards drift in, staggered along their row.
    private void HomeItemLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement item) return;
        var container = VisualTreeHelper.GetParent(item) as DependencyObject;
        var owner = container is null ? null : ItemsControl.ItemsControlFromItemContainer(container);
        var index = owner?.ItemContainerGenerator.IndexFromContainer(container!) ?? -1;
        var pill = owner == HomeContinue;
        Entrance(item, TimeSpan.FromMilliseconds(Math.Clamp(index + (pill ? 0 : 1), 0, 9) * 45), pill ? 0 : 22, pill ? 8 : 0);
    }
    private void HomeSpotlightChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement { IsLoaded: true } card && e.NewValue is not null) Entrance(card, TimeSpan.Zero, 22, 0);
    }
    private static void Entrance(FrameworkElement item, TimeSpan delay, double fromX, double fromY)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var duration = TimeSpan.FromMilliseconds(420);
        var ease = new QuarticEase { EasingMode = EasingMode.EaseOut };
        DoubleAnimationUsingKeyFrames Track(double from, double to) => new()
        {
            KeyFrames = { new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)), new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(delay)),
                new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(delay + duration), ease) }
        };
        if (item.RenderTransform is not TranslateTransform shift) item.RenderTransform = shift = new TranslateTransform();
        var fade = Track(0, 1);
        fade.Completed += (_, _) => { item.BeginAnimation(OpacityProperty, null); shift.BeginAnimation(TranslateTransform.XProperty, null); shift.BeginAnimation(TranslateTransform.YProperty, null); };
        item.BeginAnimation(OpacityProperty, fade);
        shift.BeginAnimation(TranslateTransform.XProperty, Track(fromX, 0));
        shift.BeginAnimation(TranslateTransform.YProperty, Track(fromY, 0));
    }
    private void OpenHomeCard(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: LibraryCardView view }) OpenHomeTitle(view);
    }
    // Plays straight from Home: a series starts at its first episode. Someone looking through the streamer's library
    // goes through the details page, which asks that person's app to start playback.
    private void PlayHomeCard(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: LibraryCardView view }) return;
        var item = GuestLibrary ? null : view.Card.Level is "series" or "season" ? FirstEpisode(view.Card.Media) : view.Card.Media;
        if (item is null) { OpenHomeTitle(view); return; }
        Select(item); PlayLocal(sender, e);
    }
    private MediaItem? FirstEpisode(MediaItem show)
    {
        var key = LibraryIdentity.ShowKey(show);
        return LibraryCatalog.PreferredFiles(items.Where(item => item.Available && !item.IsExtra && !item.IsVirtual && LibraryIdentity.ShowKey(item) == key), PreferredVersion(key))
            .OrderBy(item => item.Season is null or 0 ? int.MaxValue : item.Season).ThenBy(item => item.Episode ?? int.MaxValue).ThenBy(item => item.Id).FirstOrDefault();
    }
    private void OpenHomeTitle(LibraryCardView view)
    {
        var card = view.Card;
        if (GuestLibrary)
        {
            if (!OwnsLibrary) return;
            if (card.Level == "series")
            {
                guestParentTitle = card.DisplayTitle;
                guestBrowse = new(Category: card.Media.Kind == "Anime" ? 3 : 2, ParentId: card.Media.Id);
                librarySearch = false; ShowPage("Library");
            }
            else
            {
                guestBrowse = new(Category: card.Media.Kind == "Anime" ? 3 : card.Media.Kind == "Show" ? 2 : 1);
                OpenGuestVideo(card, view.HomeSection == "continue");
            }
        }
        else if (card.Level == "series") NavigateLibrary(CategoryFor(card.Media), LibraryIdentity.ShowKey(card.Media), card.Media.Kind);
        else Select(card.Media);
    }
}
