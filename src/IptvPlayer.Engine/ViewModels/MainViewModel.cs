using System.Windows.Input;

namespace IptvPlayer.Engine.ViewModels;

public sealed class MainViewModel : ObservableObject, INavigator
{
    private static readonly TimeSpan AutoSyncAge = TimeSpan.FromHours(6);
    private static readonly TimeSpan EpgMaxAge = TimeSpan.FromHours(12);

    private readonly Library _library;
    private readonly EpgService _epg;
    private readonly IDialogService _dialogs;

    private ObservableObject _current;
    private bool _isFullscreen;
    private string? _bannerText;

    public MainViewModel(Library library, CatalogService catalogService, EpgService epg, IPlayerService player, IDialogService dialogs,
        SettingsStore? settings = null, IThumbnailService? thumbnails = null)
    {
        _library = library;
        _epg = epg;
        _dialogs = dialogs;

        var settingsStore = settings ?? new SettingsStore(library.Store);
        AdultLock = new AdultLock(settingsStore);
        var watch = new WatchStore(library.Store);
        Recordings = new RecordingManager(library, epg, catalogService.Http, settingsStore, library.Store);
        Recordings.Changed += () => { Raise(nameof(ActiveRecordings)); Raise(nameof(HasActiveRecordings)); Raise(nameof(RecordingBadge)); };

        Downloads = new DownloadManager(catalogService.Http, settingsStore, library.Store);

        Channels = new ChannelsViewModel(library, epg, player, dialogs, xtream: new XtreamClient(catalogService.Http),
            watch: watch, settings: settingsStore, recordings: Recordings, adultLock: AdultLock, downloads: Downloads);
        Settings = new SettingsViewModel(settingsStore, Recordings, dialogs, PlayRecording, AdultLock, Downloads, PlayDownload);
        Sources = new SourcesViewModel(library, catalogService, this, dialogs, RefreshEpgAsync);
        Sources.PendingChanged += UpdateBanner;
        Reminders = new ReminderManager(library.Store, settingsStore);
        Guide = new GuideViewModel(library, epg, Recordings, Channels, ShowChannels, adultLock: AdultLock, reminders: Reminders);
        Reminders.Due += OnReminderDue;
        Home = new HomeViewModel(library, watch, epg, Recordings, Channels, ShowChannels, ShowKind, ShowGuide, settings: settingsStore,
            thumbnails: thumbnails, adultLock: AdultLock);
        // Wisselt het slot terwijl je op het beginscherm of de gids staat, dan moet die er meteen goed uitzien.
        AdultLock.Changed += () =>
        {
            if (_current == Home) Home.Refresh();
            else if (_current == Guide) Guide.Refresh();
        };
        Beheer = new BeheerViewModel(Sources, Settings);
        _current = Channels;

        Channels.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChannelsViewModel.SelectedKind)) RaiseKindActive();
        };

        ShowChannelsCommand = new RelayCommand(ShowChannels);
        ShowHomeCommand = new RelayCommand(ShowHome);
        ShowGuideCommand = new RelayCommand(ShowGuide);
        ShowLiveCommand = new RelayCommand(() => ShowKind(ContentKind.Live));
        ShowMoviesCommand = new RelayCommand(() => ShowKind(ContentKind.Movie));
        ShowSeriesCommand = new RelayCommand(() => ShowKind(ContentKind.Series));
        ShowSourcesCommand = new RelayCommand(ShowSources);
        ShowBeheerCommand = new RelayCommand(() => ShowBeheer());
        ShowRecordingsCommand = new RelayCommand(() => ShowBeheer(BeheerSection.Opnames));
        ShowDownloadsCommand = new RelayCommand(() => ShowBeheer(BeheerSection.Downloads));
        Beheer.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(BeheerViewModel.Section)) RaiseLibraryActive(); };
        ReviewBannerCommand = new AsyncCommand(() => Sources.ReviewFirstPendingAsync(), ex => _dialogs.ShowError("Er ging iets mis", ex.Message));
        ToggleFullscreenCommand = new RelayCommand(() => IsFullscreen = !IsFullscreen);
        WatchReminderCommand = new RelayCommand(WatchReminder);
        DismissReminderCommand = new RelayCommand(DismissReminder);
    }

    /// <summary>Het slot (pincode) voor erotiek.</summary>
    public AdultLock AdultLock { get; }

    public ChannelsViewModel Channels { get; }
    public SourcesViewModel Sources { get; }
    public SettingsViewModel Settings { get; }
    public BeheerViewModel Beheer { get; }
    public HomeViewModel Home { get; }
    public GuideViewModel Guide { get; }
    public ReminderManager Reminders { get; }
    public RecordingManager Recordings { get; }
    public DownloadManager Downloads { get; }

    public ICommand ShowChannelsCommand { get; }
    public ICommand ShowHomeCommand { get; }
    public ICommand ShowGuideCommand { get; }
    public ICommand WatchReminderCommand { get; }
    public ICommand DismissReminderCommand { get; }
    public ICommand ShowLiveCommand { get; }
    public ICommand ShowMoviesCommand { get; }
    public ICommand ShowSeriesCommand { get; }
    public ICommand ShowSourcesCommand { get; }
    public ICommand ShowBeheerCommand { get; }
    public ICommand ShowRecordingsCommand { get; }
    public ICommand ShowDownloadsCommand { get; }
    public ICommand ReviewBannerCommand { get; }
    public ICommand ToggleFullscreenCommand { get; }

    /// <summary>De pagina die nu getoond wordt (ChannelsViewModel, SourcesViewModel of SelectionViewModel).</summary>
    public ObservableObject Current
    {
        get => _current;
        private set
        {
            if (Set(ref _current, value))
            {
                if (value != Home) Home.StopPreviews();
                Raise(nameof(IsChannelsActive));
                RaiseKindActive();
                Raise(nameof(IsBeheerActive));
                RaiseLibraryActive();
                Raise(nameof(IsHomeActive));
                Raise(nameof(IsGuideActive));
                Raise(nameof(OtherPage));
                Raise(nameof(IsOtherPageActive));
                Raise(nameof(ShowNav));
            }
        }
    }

    public bool IsChannelsActive => _current == Channels;

    // Menu: Live / Films / Series zijn drie ingangen op dezelfde pagina ("Mijn zenders"), elk met eigen inhoud.
    public bool IsLiveActive => IsChannelsActive && Channels.SelectedKind == ContentKind.Live;
    public bool IsMoviesActive => IsChannelsActive && Channels.SelectedKind == ContentKind.Movie;
    public bool IsSeriesActive => IsChannelsActive && Channels.SelectedKind == ContentKind.Series;

    private void RaiseKindActive()
    {
        Raise(nameof(IsLiveActive));
        Raise(nameof(IsMoviesActive));
        Raise(nameof(IsSeriesActive));
    }

    public void ShowKind(ContentKind kind)
    {
        Channels.SelectKind(kind);
        Current = Channels;
        RaiseKindActive();
    }

    /// <summary>
    /// De pagina naast "Mijn zenders" (Bronnen of selectie). "Mijn zenders" blijft bewust altijd bestaan in het venster
    /// (alleen verborgen), zodat het videoscherm niet telkens opnieuw hoeft te worden aangemaakt.
    /// </summary>
    public ObservableObject? OtherPage => _current == Channels ? null : _current;
    public bool IsOtherPageActive => _current != Channels;
    public bool IsBeheerActive => _current == Beheer || _current is SelectionViewModel;
    public bool IsHomeActive => _current == Home;

    // Menu "Bibliotheek": Opnames en Downloads zijn tabbladen van Beheer met een eigen ingang in het menu.
    public bool IsRecordingsActive => _current == Beheer && Beheer.IsOpnames;
    public bool IsDownloadsActive => _current == Beheer && Beheer.IsDownloads;
    public bool IsSettingsActive => IsBeheerActive && !IsRecordingsActive && !IsDownloadsActive;

    private void RaiseLibraryActive()
    {
        Raise(nameof(IsRecordingsActive));
        Raise(nameof(IsDownloadsActive));
        Raise(nameof(IsSettingsActive));
    }
    public bool IsGuideActive => _current == Guide;

    /// <summary>Opnames die nu lopen (voor het bolletje onderaan het menu).</summary>
    public int ActiveRecordings => Recordings.ActiveCount;
    public bool HasActiveRecordings => ActiveRecordings > 0;
    public string RecordingBadge => ActiveRecordings == 1 ? "● 1 opname bezig" : $"● {ActiveRecordings} opnames bezig";

    public bool IsFullscreen
    {
        get => _isFullscreen;
        set
        {
            if (!Set(ref _isFullscreen, value)) return;
            Channels.IsFullscreen = value;
            Raise(nameof(ShowNav));
            Raise(nameof(ShowBanner));
        }
    }

    public bool ShowNav => !_isFullscreen || _current != Channels;

    /// <summary>Melding bovenin als er nieuwe zenders te beoordelen zijn.</summary>
    public string? BannerText
    {
        get => _bannerText;
        private set
        {
            if (Set(ref _bannerText, value))
            {
                Raise(nameof(HasBanner));
                Raise(nameof(ShowBanner));
            }
        }
    }

    public bool HasBanner => !string.IsNullOrEmpty(_bannerText);
    public bool ShowBanner => HasBanner && !_isFullscreen;

    // ---- Navigatie ----

    public void ShowChannels()
    {
        Channels.Reload();
        Current = Channels;
    }

    public void ShowSources() => ShowBeheer(BeheerSection.Bronnen);

    /// <summary>Beheer openen, eventueel op een bepaald tabblad (anders blijft het laatst gekozen tabblad staan).</summary>
    public void ShowBeheer(BeheerSection? section = null)
    {
        LeaveChannels();
        if (section is { } s) Beheer.Select(s);
        if (Beheer.IsBronnen) Sources.RefreshRows();
        Current = Beheer;
    }

    public void ShowHome()
    {
        LeaveChannels();
        Home.Refresh();
        Current = Home;
    }

    public void ShowGuide()
    {
        LeaveChannels();
        Guide.Refresh();
        Current = Guide;
    }

    /// <summary>Speelt een opname af (vanuit Beheer).</summary>
    public void PlayRecording(Recording recording)
    {
        ShowChannels();
        Channels.PlayRecording(recording);
    }

    public void PlayDownload(DownloadItem download)
    {
        ShowChannels();
        Channels.PlayDownload(download);
    }

    /// <summary>
    /// Waarschuwing voor het afsluiten: er loopt een opname of er staat er nog een gepland. Null als er niets op het spel staat.
    /// </summary>
    public string? ExitWarning
    {
        get
        {
            var all = Recordings.All;
            var running = all.Count(r => r.Status == RecordingStatus.Recording);
            var planned = all.Count(r => r.Status == RecordingStatus.Scheduled);
            if (running == 0 && planned == 0) return null;

            var parts = new List<string>();
            if (running > 0) parts.Add(running == 1 ? "Er loopt nog 1 opname" : $"Er lopen nog {running} opnames");
            if (planned > 0) parts.Add(planned == 1 ? "er staat nog 1 opname gepland" : $"er staan nog {planned} opnames gepland");
            var sentence = string.Join(" en ", parts);
            sentence = char.ToUpperInvariant(sentence[0]) + sentence[1..] + ".";
            var consequences = new List<string>();
            if (running > 0) consequences.Add("stopt de lopende opname (wat al is opgenomen blijft bewaard)");
            if (planned > 0) consequences.Add("worden geplande opnames niet uitgevoerd");
            return sentence + "\n\nAls je EyePTV afsluit, " + string.Join(" en ", consequences) + ". " +
                   "Wil je de app alleen minimaliseren? Dan blijft het opnemen doorgaan.";
        }
    }

    /// <summary>Vraagt de gebruiker wat er moet gebeuren bij afsluiten; zonder opnames wordt niet gevraagd.</summary>
    public ExitChoice AskExit() => ExitWarning is { } warning ? _dialogs.AskExit(warning) : ExitChoice.Quit;

    /// <summary>Elke paar seconden aanroepen: start en stopt opnames.</summary>
    public void TickRecordings()
    {
        Recordings.Tick();
        Downloads.Tick();        if (_current == Home) Home.TickPreviews();
    }

    public void ShowSelection(SelectionViewModel selection)
    {
        LeaveChannels();
        Current = selection;
    }

    private void LeaveChannels()
    {
        if (_current != Channels) return;
        if (Channels.Current is not null) Channels.Stop();
        IsFullscreen = false;
    }

    // ---- Opstarten ----

    /// <summary>Het inlaadscherm bij het openen: staat aan tot het beginscherm klaar is (inclusief de beelden van de kanalen).</summary>
    public bool IsStarting { get => _isStarting; private set => Set(ref _isStarting, value); }
    private bool _isStarting = true;

    private static readonly TimeSpan StartScreenMax = TimeSpan.FromSeconds(12);

    public async Task StartupAsync()
    {
        try
        {
            _epg.LoadCache();

            Channels.Reload();
            if (_library.Personal.Count > 0) { Home.Refresh(); Current = Home; }
            else { Beheer.Select(BeheerSection.Bronnen); Current = Beheer; }
            UpdateBanner();

            // Even wachten tot de beelden van de kanalen binnen zijn, zodat het beginscherm er meteen compleet uitziet.
            if (Current == Home) await Task.WhenAny(Home.PreviewsReady, Task.Delay(StartScreenMax));
        }
        finally
        {
            IsStarting = false;
            Reminders.Tick(); // herinneringen waarvan het programma nog loopt, direct tonen
        }

        if (_library.Sources.Count == 0) return;

        // Op de achtergrond: bronnen bijwerken die al even niet zijn gesynchroniseerd.
        var stale = _library.Sources.Any(s => s.InitialImportDone &&
            (s.LastSyncUtc is null || DateTime.UtcNow - s.LastSyncUtc > AutoSyncAge));
        if (stale)
        {
            try { await Sources.SyncAllAsync(interactive: false); }
            catch { /* fouten staan al in de statusregel */ }
        }

        if (_library.Personal.Count > 0 && (_epg.Data.FetchedUtc == default || _epg.Data.Programmes.Count == 0 || DateTime.UtcNow - _epg.Data.FetchedUtc > EpgMaxAge))
        {
            try { await RefreshEpgAsync(); } catch { /* EPG is optioneel */ }
        }
    }

    public async Task RefreshEpgAsync()
    {
        var errors = await _epg.RefreshAsync(_library.Sources, _library.Personal);
        Channels.Reload();
        if (errors.Count > 0) Sources.ReportStatus("EPG niet geladen: " + string.Join("; ", errors));
    }

    public void Tick()
    {
        Channels.Tick();
        Reminders.Tick();
        if (_current == Guide) Guide.UpdateNow();
    }

    // ---- Herinneringen ----

    private readonly Queue<Reminder> _dueReminders = new();
    private Reminder? _activeReminder;

    /// <summary>Er gaat een herinnering af: het venster moet de aandacht trekken (taakbalkicoon knipperen).</summary>
    public event Action? ReminderRaised;

    /// <summary>De herinnering die nu getoond wordt, of null.</summary>
    public Reminder? ActiveReminder { get => _activeReminder; private set { if (Set(ref _activeReminder, value)) { Raise(nameof(ReminderText)); Raise(nameof(HasReminder)); } } }

    public bool HasReminder => _activeReminder is not null;

    public string ReminderText => _activeReminder is { } r ? $"{r.Title} op {r.ChannelName} ({r.StartUtc.ToLocalTime():HH:mm})" : "";

    private void OnReminderDue(Reminder reminder)
    {
        _dueReminders.Enqueue(reminder);
        if (_activeReminder is null) NextReminder();
        ReminderRaised?.Invoke();
    }

    private void NextReminder() => ActiveReminder = _dueReminders.Count > 0 ? _dueReminders.Dequeue() : null;

    /// <summary>"Nu kijken": de zender van de herinnering openen en afspelen.</summary>
    public void WatchReminder()
    {
        if (_activeReminder is { } r && _library.Personal.FirstOrDefault(p => p.Id == r.ChannelId) is { } channel)
        {
            ShowChannels();
            Channels.WatchLive(channel);
        }
        NextReminder();
    }

    /// <summary>"Sluiten": de melding wegdoen.</summary>
    public void DismissReminder() => NextReminder();

    private void UpdateBanner()
    {
        var pending = Sources.Rows.Where(r => r.HasPending).ToList();
        if (pending.Count == 0)
        {
            BannerText = null;
            return;
        }

        var parts = new List<string>();
        foreach (var kind in Enum.GetValues<ContentKind>())
        {
            var n = pending.Sum(r => r.PendingNewByKind.GetValueOrDefault(kind));
            if (n > 0) parts.Add(KindText.Count(kind, n, "nieuwe", "nieuwe"));
        }
        foreach (var kind in Enum.GetValues<ContentKind>())
        {
            var n = pending.Sum(r => r.PendingRemovedByKind.GetValueOrDefault(kind));
            if (n > 0) parts.Add(KindText.Count(kind, n, "verdwenen", "verdwenen"));
        }

        var who = pending.Count == 1 ? $"bij {pending[0].Name}" : $"bij {pending.Count} bronnen";
        BannerText = $"{KindText.JoinNatural(parts)} {who}. Kies wat je wilt toevoegen.";
    }
}
