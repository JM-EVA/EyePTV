using System.Windows.Input;

namespace IptvPlayer.Engine.ViewModels;

/// <summary>Een kaart op het beginscherm: een film, aflevering, zender of opname.</summary>
public sealed class HomeCard : ObservableObject
{
    private string? _previewPath;

    public HomeCard(string title, string? subtitle, string? imageUrl, string badge, ICommand open, double progress = 0, bool landscape = false,
        ICommand? remove = null, ICommand? markWatched = null)
    {
        Title = title;
        Subtitle = subtitle;
        ImageUrl = imageUrl;
        Badge = badge;
        OpenCommand = open;
        Progress = Math.Clamp(progress, 0, 1);
        IsLandscape = landscape;
        RemoveCommand = remove;
        MarkWatchedCommand = markWatched;
    }

    public string Title { get; }
    public string? Subtitle { get; }
    public string? ImageUrl { get; }

    /// <summary>Pad naar een momentopname van de uitzending (bij zenders); de kaart toont die in plaats van het logo.</summary>
    public string? PreviewPath { get => _previewPath; set { if (Set(ref _previewPath, value)) Raise(nameof(HasPreview)); } }
    public bool HasPreview => _previewPath is not null;

    /// <summary>De stream van een zender (alleen bij zenders): daarmee kan de kaart live meekijken als je hem aanwijst.</summary>
    public string? LiveUrl { get; init; }

    /// <summary>"FILM", "SERIE", "LIVE" of "OPNAME".</summary>
    public string Badge { get; }

    public ICommand OpenCommand { get; }

    /// <summary>Haalt dit item uit "Verder kijken" (alleen bij items die je half hebt bekeken).</summary>
    public ICommand? RemoveCommand { get; }

    /// <summary>Markeert dit item als bekeken, waarna het niet meer bij "Verder kijken" staat.</summary>
    public ICommand? MarkWatchedCommand { get; }

    /// <summary>Er is een menu (⋯) met verwijderen en markeren als bekeken.</summary>
    public bool HasMenu => RemoveCommand is not null;

    /// <summary>0..1: hoever je al bent (alleen bij "verder kijken").</summary>
    public double Progress { get; }
    public bool HasProgress => Progress > 0;

    /// <summary>Liggende kaart (zenders en opnames) in plaats van een staande poster.</summary>
    public bool IsLandscape { get; }
}

/// <summary>Een grote tegel om naar Kanalen, Films, Series of de Gids te gaan.</summary>
public sealed record HomeTile(string Title, string Subtitle, ICommand Command, string Icon);

/// <summary>
/// Het beginscherm (in de stijl van Apple TV): vooraan wat je verder kunt kijken, daarna wat er nu op is,
/// je opnames en grote tegels om naar Kanalen, Films, Series en de Gids te gaan.
/// Erotiek wordt hier standaard niet getoond (zie <see cref="AdultContent"/>).
/// </summary>
public sealed class HomeViewModel : ObservableObject
{
    private const int MaxContinue = 20;
    private const int MaxShelf = 24; // posters per plank (films, series)

    private readonly AdultLock? _lock;
    private readonly Library _library;
    private readonly WatchStore _watch;
    private readonly EpgService _epg;
    private readonly RecordingManager? _recordings;
    private readonly ChannelsViewModel _channels;
    private readonly SettingsStore? _settings;
    private readonly Action _showPlayer;
    private readonly Func<DateTime> _utcNow;
    private readonly IThumbnailService? _thumbnails;
    private CancellationTokenSource? _previewCts;
    private IReadOnlyList<PersonalChannel> _previewChannels = Array.Empty<PersonalChannel>();
    private IReadOnlyList<HomeCard> _previewCards = Array.Empty<HomeCard>();

    private HomeCard? _hero;
    private IReadOnlyList<HomeCard> _continue = Array.Empty<HomeCard>();
    private IReadOnlyList<HomeCard> _live = Array.Empty<HomeCard>();
    private IReadOnlyList<HomeCard> _recent = Array.Empty<HomeCard>();
    private IReadOnlyList<HomeTile> _tiles = Array.Empty<HomeTile>();
    private IReadOnlyList<HomeCard> _movies = Array.Empty<HomeCard>();
    private IReadOnlyList<HomeCard> _series = Array.Empty<HomeCard>();
    private string _liveTitle = "Nu op tv";
    private string _greeting = "";

    public HomeViewModel(Library library, WatchStore watch, EpgService epg, RecordingManager? recordings, ChannelsViewModel channels,
        Action showPlayer, Action<ContentKind> showKind, Action showGuide, Func<DateTime>? utcNow = null, SettingsStore? settings = null, IThumbnailService? thumbnails = null,
        AdultLock? adultLock = null)
    {
        _lock = adultLock;
        _thumbnails = thumbnails;
        _library = library;
        _watch = watch;
        _epg = epg;
        _recordings = recordings;
        _channels = channels;
        _settings = settings;
        _showPlayer = showPlayer;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);

        LiveCommand = new RelayCommand(() => showKind(ContentKind.Live));
        MoviesCommand = new RelayCommand(() => showKind(ContentKind.Movie));
        SeriesCommand = new RelayCommand(() => showKind(ContentKind.Series));
        GuideCommand = new RelayCommand(showGuide);

        if (_recordings is not null) _recordings.Changed += RefreshRecent;
    }

    public ICommand LiveCommand { get; }
    public ICommand MoviesCommand { get; }
    public ICommand SeriesCommand { get; }
    public ICommand GuideCommand { get; }

    /// <summary>Het grote vlak bovenaan: het laatste dat je bekeek, of een welkomstekst.</summary>
    public HomeCard? Hero { get => _hero; private set { if (Set(ref _hero, value)) Raise(nameof(HasHero)); } }
    public bool HasHero => _hero is not null;

    public IReadOnlyList<HomeCard> ContinueItems { get => _continue; private set { if (Set(ref _continue, value)) Raise(nameof(HasContinue)); } }
    public bool HasContinue => _continue.Count > 0;

    public IReadOnlyList<HomeCard> LiveItems { get => _live; private set { if (Set(ref _live, value)) Raise(nameof(HasLive)); } }
    public bool HasLive => _live.Count > 0;
    public string LiveTitle { get => _liveTitle; private set => Set(ref _liveTitle, value); }

    public IReadOnlyList<HomeCard> RecentRecordings { get => _recent; private set { if (Set(ref _recent, value)) Raise(nameof(HasRecordings)); } }
    public bool HasRecordings => _recent.Count > 0;

    /// <summary>Planken met posters: favorieten eerst, daarna de volgorde van je lijst.</summary>
    public IReadOnlyList<HomeCard> MovieItems { get => _movies; private set { if (Set(ref _movies, value)) Raise(nameof(HasMovies)); } }
    public bool HasMovies => _movies.Count > 0;
    public IReadOnlyList<HomeCard> SeriesItems { get => _series; private set { if (Set(ref _series, value)) Raise(nameof(HasSeries)); } }
    public bool HasSeries => _series.Count > 0;

    public IReadOnlyList<HomeTile> Tiles { get => _tiles; private set => Set(ref _tiles, value); }

    public string Greeting { get => _greeting; private set => Set(ref _greeting, value); }

    /// <summary>Er is nog niets in je lijst: dan wijzen we naar Bron toevoegen.</summary>
    public bool IsEmptyLibrary => _library.Personal.Count == 0;

    // Met een slot (de app) bepaalt de pincode of erotiek zichtbaar is; zonder slot de oude instelling.
    private bool HideAdult => _lock is { } l ? !l.IsUnlocked : _settings?.Current.HideAdultOnHome ?? true;

    // ---- Opbouwen ----

    /// <summary>Wordt aangeroepen als je naar het beginscherm gaat (en na wijzigingen in je lijst).</summary>
    public void Refresh()
    {
        var hour = _utcNow().ToLocalTime().Hour;
        Greeting = hour < 6 ? "Goedenacht" : hour < 12 ? "Goedemorgen" : hour < 18 ? "Goedemiddag" : "Goedenavond";

        BuildContinue();
        BuildLive();
        BuildShelves();
        RefreshRecent();
        BuildTiles();
        Raise(nameof(IsEmptyLibrary));
    }

    private void AfterChange()
    {
        _channels.ReloadWatchState(); // "✓ Bekeken" in de lijsten
        Refresh();
    }

    private void BuildContinue()
    {
        var cards = new List<HomeCard>();
        var seenSeries = new HashSet<string>();
        var hide = HideAdult;

        foreach (var (key, e) in _watch.InProgress())
        {
            var left = Math.Max(0, e.LengthMs - e.PositionMs);
            var progress = e.LengthMs > 0 ? (double)e.PositionMs / e.LengthMs : 0;
            var remaining = $"nog {Math.Max(1, left / 60000)} min";

            if (e.SeriesId is { } seriesId)
            {
                if (!seenSeries.Add(seriesId)) continue; // per serie alleen de laatste aflevering
                var series = _library.Personal.FirstOrDefault(p => p.Id == seriesId && p.Kind == ContentKind.Series);
                if (series is null || e.EpisodeId is null) continue;
                if (hide && AdultContent.IsAdult(series)) continue;

                var episodeId = e.EpisodeId;
                var sub = $"S{e.Season} A{e.Number} · {e.EpisodeTitle} · {remaining}";
                cards.Add(new HomeCard(series.DisplayName, sub, series.LogoUrl, "SERIE",
                    new RelayCommand(() => OpenEpisode(series, episodeId)), progress,
                    remove: new RelayCommand(() => { _watch.ForgetSeriesInProgress(series.Id); AfterChange(); }),
                    markWatched: new RelayCommand(() => { _watch.MarkSeriesFinished(series.Id, _utcNow()); AfterChange(); })));
            }
            else if (key.StartsWith(ChannelsViewModel.RecordingsSourceId + "|", StringComparison.Ordinal))
            {
                var id = key[(ChannelsViewModel.RecordingsSourceId.Length + 1)..];
                var rec = _recordings?.Find(id);
                if (rec?.FilePath is null || !File.Exists(rec.FilePath)) continue;
                if (hide && IsAdultRecording(rec)) continue;

                var k = key;
                cards.Add(new HomeCard(rec.Title, $"{rec.ChannelName} · {remaining}", rec.LogoUrl, "OPNAME",
                    new RelayCommand(() => { _showPlayer(); _channels.PlayRecording(rec); }), progress, landscape: true,
                    remove: new RelayCommand(() => { _watch.ForgetInProgress(k); AfterChange(); }),
                    markWatched: new RelayCommand(() => { _watch.MarkFinished(k, _utcNow()); AfterChange(); })));
            }
            else
            {
                var movie = _library.Personal.FirstOrDefault(p => p.Id == key && p.Kind == ContentKind.Movie);
                if (movie is null) continue;
                if (hide && AdultContent.IsAdult(movie)) continue;

                var k = key;
                cards.Add(new HomeCard(movie.DisplayName, remaining, movie.LogoUrl, "FILM",
                    new RelayCommand(() => { _showPlayer(); _channels.ResumeMovie(movie); }), progress,
                    remove: new RelayCommand(() => { _watch.ForgetInProgress(k); AfterChange(); }),
                    markWatched: new RelayCommand(() => { _watch.MarkFinished(k, _utcNow()); AfterChange(); })));
            }

            if (cards.Count >= MaxContinue) break;
        }

        Hero = cards.FirstOrDefault();
        ContinueItems = cards.Skip(1).ToList();
    }

    private void BuildShelves()
    {
        MovieItems = Shelf(ContentKind.Movie, "FILM", p => { _showPlayer(); _channels.ResumeMovie(p); });
        SeriesItems = Shelf(ContentKind.Series, "SERIE", p => { _showPlayer(); _channels.ShowSeries(p); });
    }

    private List<HomeCard> Shelf(ContentKind kind, string badge, Action<PersonalChannel> open)
    {
        var hide = HideAdult;
        return _library.Ordered()
            .Where(p => p.Kind == kind && !(hide && AdultContent.IsAdult(p)))
            .OrderByDescending(p => p.IsFavorite) // stabiel: binnen favorieten/overig blijft de lijstvolgorde
            .Take(MaxShelf)
            .Select(p => new HomeCard(p.DisplayName, p.Group, p.LogoUrl, badge, new RelayCommand(() => open(p))))
            .ToList();
    }

    private bool IsAdultRecording(Recording rec)
    {
        var channel = _library.Personal.FirstOrDefault(p => p.Id == rec.ChannelId);
        return AdultContent.IsAdult(channel?.Group, rec.Title) || (channel is not null && AdultContent.IsAdult(channel));
    }

    private async void OpenEpisode(PersonalChannel series, string episodeId)
    {
        _showPlayer();
        try { await _channels.ResumeEpisodeAsync(series, episodeId); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            _channels.ReportMessage("De aflevering kon niet worden opgehaald: " + ex.Message);
        }
    }

    private void BuildLive()
    {
        var now = _utcNow();
        var live = _library.Ordered().Where(p => p.Kind == ContentKind.Live && !(HideAdult && AdultContent.IsAdult(p))).ToList();
        var favorites = live.Where(p => p.IsFavorite).ToList();
        var pick = favorites.Count > 0 ? favorites : live;
        LiveTitle = favorites.Count > 0 ? "Je favoriete zenders" : "Nu op tv";

        var channels = pick.Take(MaxLiveCards).ToList();
        var cards = channels.Select(p =>
        {
            var (cur, _) = _epg.Data.NowNext(p, now);
            double progress = cur is null ? 0 : Math.Clamp((now - cur.StartUtc).TotalSeconds / Math.Max(1, (cur.StopUtc - cur.StartUtc).TotalSeconds), 0, 1);
            return new HomeCard(p.DisplayName, cur?.Title ?? "Geen programma-informatie", p.LogoUrl, "LIVE",
                new RelayCommand(() => { _showPlayer(); _channels.WatchLive(p); }), progress, landscape: true) { LiveUrl = p.Url };
        }).ToList();
        _allLiveChannels = channels;
        _allLiveCards = cards;
        _shotAt.Clear();
        _previewTask = Task.CompletedTask;
        ApplyLiveLimit();
    }

    /// <summary>Het meeste aantal kanaalkaarten op het beginscherm.</summary>
    public const int MaxLiveCards = 33;

    private int _liveLimit = MaxLiveCards;
    private List<PersonalChannel> _allLiveChannels = new();
    private List<HomeCard> _allLiveCards = new();

    /// <summary>
    /// Hoeveel kanaalkaarten er passen (de weergave zet dit bij elke venstergrootte: drie volle rijen, nooit meer dan
    /// <see cref="MaxLiveCards"/>). Alleen voor de zichtbare kaarten worden beelden gemaakt.
    /// </summary>
    public int LiveLimit
    {
        get => _liveLimit;
        set
        {
            value = Math.Clamp(value, 1, MaxLiveCards);
            if (value == _liveLimit) return;
            _liveLimit = value;
            ApplyLiveLimit();
        }
    }

    private void ApplyLiveLimit()
    {
        var cards = _allLiveCards.Take(_liveLimit).ToList();
        var channels = _allLiveChannels.Take(_liveLimit).ToList();
        LiveItems = cards;
        _previewChannels = channels;
        _previewCards = cards;
        StartPreviews(channels, cards);
    }

    // ---- Beeld van de zender op de kaart ----

    private static readonly TimeSpan PreviewMaxAge = TimeSpan.FromMinutes(5);

    private static string PreviewKey(PersonalChannel p) => p.SourceId + "|" + p.Id;

    // Wanneer een kaart (in deze sessie) zijn beeld kreeg. Beelden uit eerdere bezoeken worden nooit getoond.
    private readonly Dictionary<HomeCard, DateTime> _shotAt = new();
    private Task _previewTask = Task.CompletedTask;

    /// <summary>Klaar zodra de beelden van de huidige ronde binnen zijn (of het maken is gestopt).</summary>
    public Task PreviewsReady => _previewTask;

    /// <summary>
    /// Maakt van elke kanaalkaart een nieuw beeld, één voor één (een paar tegelijk). Bij elk bezoek aan het beginscherm
    /// beginnen de kaarten leeg, er wordt dus nooit een oud beeld getoond. Daarna elke vijf minuten verversen.
    /// Er wordt niets geopend zolang er iets speelt of wordt opgenomen (een provider laat vaak maar een paar verbindingen toe).
    /// </summary>
    private void StartPreviews(IReadOnlyList<PersonalChannel> channels, IReadOnlyList<HomeCard> cards)
    {
        StopPreviews();
        if (_thumbnails is null || !(_settings?.Current.ChannelPreviews ?? true)) return;

        var now = _utcNow();
        var todo = new List<(PersonalChannel Channel, HomeCard Card)>();
        for (var i = 0; i < channels.Count; i++)
            if (!_shotAt.TryGetValue(cards[i], out var at) || now - at > PreviewMaxAge)
                todo.Add((channels[i], cards[i]));

        if (todo.Count == 0) return;
        var cts = _previewCts = new CancellationTokenSource();
        _previewTask = RefreshPreviewsAsync(todo, cts.Token);
    }

    /// <summary>Stopt het maken van beelden (bij het verlaten van het beginscherm of als er iets gaat spelen).</summary>
    public void StopPreviews()
    {
        _previewCts?.Cancel();
        _previewCts = null;
    }

    /// <summary>Hoeveel zenders tegelijk een beeld laten maken (snel, maar zonder de provider te veel verbindingen te laten tellen).</summary>
    private const int PreviewWorkers = 3;

    private async Task RefreshPreviewsAsync(List<(PersonalChannel Channel, HomeCard Card)> todo, CancellationToken ct)
    {
        var next = -1;

        async Task Worker()
        {
            try
            {
                while (true)
                {
                    var i = Interlocked.Increment(ref next);
                    if (i >= todo.Count) return;
                    var (channel, card) = todo[i];
                    if (ct.IsCancellationRequested || _channels.IsPlaying || (_recordings?.ActiveCount ?? 0) > 0) return;
                    if (!PlayableUrl.IsAllowed(channel.Url)) continue;

                    var path = await _thumbnails!.CaptureAsync(PreviewKey(channel), channel.Url, ct);
                    if (path is not null && !ct.IsCancellationRequested)
                    {
                        card.PreviewPath = path;
                        _shotAt[card] = _utcNow();
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) { }
        }

        await Task.WhenAll(Enumerable.Range(0, Math.Min(PreviewWorkers, todo.Count)).Select(_ => Worker()));
        if (_previewCts is { } cts && cts.Token == ct) _previewCts = null; // klaar: de volgende ronde mag starten
    }

    /// <summary>Elke paar seconden aangeroepen: ververst de beelden zodra ze ouder zijn dan vijf minuten (alleen op het beginscherm).</summary>
    public void TickPreviews()
    {
        if (_previewCts is not null || _previewChannels.Count == 0) return;
        StartPreviews(_previewChannels, _previewCards);
    }

    /// <summary>Mag er bij het aanwijzen van deze kaart live meegekeken worden? (niet als er iets speelt of wordt opgenomen)</summary>
    public bool CanLivePreview(HomeCard card) =>
        (_settings?.Current.ChannelPreviews ?? true) && card.LiveUrl is { } url && PlayableUrl.IsAllowed(url) &&
        !_channels.IsPlaying && (_recordings?.ActiveCount ?? 0) == 0;

    private void RefreshRecent()
    {
        if (_recordings is null)
        {
            RecentRecordings = Array.Empty<HomeCard>();
            return;
        }

        var hide = HideAdult;
        RecentRecordings = _recordings.All
            .Where(r => r.Status == RecordingStatus.Done && r.Bytes > 0 && r.FilePath is { } f && File.Exists(f))
            .Where(r => !(hide && IsAdultRecording(r)))
            .Take(12)
            .Select(r => new HomeCard(r.Title, $"{r.ChannelName} · {r.StartUtc.ToLocalTime():d MMM HH:mm}", r.LogoUrl, "OPNAME",
                new RelayCommand(() => { _showPlayer(); _channels.PlayRecording(r); }), landscape: true))
            .ToList();
    }

    private void BuildTiles()
    {
        int Count(ContentKind k) => _library.Personal.Count(p => p.Kind == k);
        string Text(int n, string one, string many) => n == 0 ? "Nog niets geselecteerd" : n == 1 ? $"1 {one}" : $"{n:N0} {many}";

        Tiles = new[]
        {
            new HomeTile("Kanalen", Text(Count(ContentKind.Live), "zender", "zenders"), LiveCommand, "live"),
            new HomeTile("Films", Text(Count(ContentKind.Movie), "film", "films"), MoviesCommand, "movies"),
            new HomeTile("Series", Text(Count(ContentKind.Series), "serie", "series"), SeriesCommand, "series"),
            new HomeTile("Gids", "Programma en opnemen", GuideCommand, "guide"),
        };
    }
}
