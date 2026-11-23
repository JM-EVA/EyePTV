using System.Collections.ObjectModel;
using System.Windows.Input;

namespace IptvPlayer.Engine.ViewModels;

/// <summary>
/// De pagina "Mijn zenders": persoonlijke lijst (zenders, films, series), speler en programma-informatie.
/// Verdeeld over meerdere bestanden: lijst/filters (dit bestand), afspelen en voortgang (.Playback),
/// series (.Series) en programmagids met terugkijken (.Schedule).
/// </summary>
public sealed partial class ChannelsViewModel : ObservableObject
{
    public const string AllGroups = "Alle groepen";

    /// <summary>Stapgrootte van de knoppen en pijltjestoetsen bij spoelen.</summary>
    public const long SeekStepMs = 15_000;

    private readonly Library _library;
    private readonly EpgService _epg;
    private readonly IPlayerService _player;
    private readonly IDialogService _dialogs;
    private readonly Func<DateTime> _utcNow;
    private readonly XtreamClient? _xtream;
    private readonly WatchStore _watch;

    private IReadOnlyList<ChannelRow> _rows = new ObservableCollection<ChannelRow>();
    private bool _suppressReload;
    private IReadOnlyList<string> _groups = new[] { AllGroups };
    private string _selectedGroup = AllGroups;
    private string _searchText = "";
    private bool _favoritesOnly;
    private IReadOnlyList<KindOption> _kindOptions = Array.Empty<KindOption>();
    private ContentKind _selectedKind = ContentKind.Live;
    private bool _kindChosen; // de gebruiker koos zelf een soort in het menu: dan schakelen we niet meer automatisch
    private readonly int[] _kindCounts = new int[3];
    private readonly AdultLock _lock;
    private ChannelRow? _current;
    private string _statusText = "Kies een zender om te kijken.";
    private string? _nowTitle;
    private string? _nowTime;
    private string? _nowDescription;
    private string? _nextText;
    private double _progress;
    private bool _isFullscreen;
    private int _volume = 80;
    private bool _isMuted;

    public ChannelsViewModel(Library library, EpgService epg, IPlayerService player, IDialogService dialogs,
        Func<DateTime>? utcNow = null, XtreamClient? xtream = null, WatchStore? watch = null,
        SettingsStore? settings = null, RecordingManager? recordings = null, AdultLock? adultLock = null,
        DownloadManager? downloads = null)
    {
        _downloads = downloads;
        _lock = adultLock ?? AdultLock.Open();
        _lock.Changed += OnLockChanged;
        _settings = settings;
        _recordings = recordings;
        _library = library;
        _epg = epg;
        _player = player;
        _dialogs = dialogs;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _xtream = xtream;
        _watch = watch ?? new WatchStore(library.Store);

        _player.Volume = _volume;
        _player.PlaybackError += OnPlaybackError;
        _player.PositionChanged += OnPositionChanged;
        _player.PlaybackEnded += OnPlaybackEnded;
        _library.Changed += OnLibraryChanged;

        NextCommand = new RelayCommand(() => Step(+1));
        PreviousCommand = new RelayCommand(() => Step(-1));
        StopCommand = new RelayCommand(Stop);
        ToggleMuteCommand = new RelayCommand(() => IsMuted = !IsMuted);
        ToggleFullscreenCommand = new RelayCommand(() => IsFullscreen = !IsFullscreen);
        TogglePauseCommand = new RelayCommand(TogglePause);
        SeekBackCommand = new RelayCommand(() => SeekRelative(-SeekStepMs));
        SeekForwardCommand = new RelayCommand(() => SeekRelative(+SeekStepMs));
        BackToLiveCommand = new RelayCommand(BackToLive);
        CloseSeriesCommand = new RelayCommand(CloseSeries);
        InitTracks();
        InitRecording();
        InitDownloads();
        InitCast();
        InitBrowse();

        Reload();
    }

    // ---- Lijst ----

    public IReadOnlyList<ChannelRow> Rows
    {
        get => _rows;
        private set
        {
            if (Set(ref _rows, value))
            {
                Raise(nameof(IsEmpty));
                Raise(nameof(HasNoMatches));
            }
        }
    }

    /// <summary>De tabbladen Zenders / Films / Series; alleen soorten waar je iets van in je lijst hebt.</summary>
    public IReadOnlyList<KindOption> KindOptions
    {
        get => _kindOptions;
        private set
        {
            if (Set(ref _kindOptions, value)) Raise(nameof(HasKindOptions));
        }
    }

    /// <summary>De tabbladen tonen we pas als er meer dan één soort is.</summary>
    public bool HasKindOptions => _kindOptions.Count > 1;

    public KindOption? SelectedKindOption
    {
        get => _kindOptions.FirstOrDefault(k => k.Kind == _selectedKind);
        set
        {
            // Een ListBox stuurt null terug als zijn lijst wordt vervangen; dat mag de keuze niet wissen.
            if (value is null || value.Kind == _selectedKind) return;
            _selectedKind = value.Kind;
            CloseSeries();
            _selectedGroup = AllGroups;
            Raise();
            Reload();
        }
    }

    public ContentKind SelectedKind => _selectedKind;

    /// <summary>Kiest Live / Films / Series (vanuit het menu), ook als daar nog niets van in je lijst staat.</summary>
    public void SelectKind(ContentKind kind)
    {
        _kindChosen = true;
        if (kind != _selectedKind)
        {
            _selectedKind = kind;
            CloseSeries();
            _selectedGroup = AllGroups;
        }
        _browsing = kind != ContentKind.Live && _current is null;
        _browseGroup = null;
        Reload();
    }

    /// <summary>Hoeveel zenders / films / series er in je lijst staan (voor het menu).</summary>
    public int LiveCount => _kindCounts[(int)ContentKind.Live];
    public int MovieCount => _kindCounts[(int)ContentKind.Movie];
    public int SeriesCount => _kindCounts[(int)ContentKind.Series];

    /// <summary>De gekozen soort heeft nog niets in je lijst (terwijl je lijst zelf niet leeg is).</summary>
    public bool HasNoKindItems => !IsEmpty && _kindCounts[(int)_selectedKind] == 0;

    public string KindEmptyText =>
        $"Nog geen {KindText.Plural(_selectedKind)} geselecteerd.\nVoeg ze toe via Beheer → Bronnen → Inhoud beheren.";

    public IReadOnlyList<string> Groups
    {
        get => _groups;
        private set => Set(ref _groups, value);
    }

    public string SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            // Een ListBox stuurt null terug als zijn lijst wordt vervangen; dat mag het filter niet resetten.
            if (string.IsNullOrEmpty(value)) return;
            if (Set(ref _selectedGroup, value)) { _browsing = false; Reload(); }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value ?? "")) Reload();
        }
    }

    public bool FavoritesOnly
    {
        get => _favoritesOnly;
        set
        {
            if (Set(ref _favoritesOnly, value)) Reload();
        }
    }

    private bool _sortNewest;

    /// <summary>Films en series met de meest recent toegevoegde eerst (zonder datum van de bron komen ze achteraan).</summary>
    public bool SortNewest
    {
        get => _sortNewest;
        set
        {
            if (Set(ref _sortNewest, value)) Reload();
        }
    }

    /// <summary>Alleen bij films en series: zenders hebben geen toevoegdatum.</summary>
    public bool CanSortNewest => _selectedKind != ContentKind.Live;

    /// <summary>Er staat nog niets in de persoonlijke lijst.</summary>
    public bool IsEmpty => _library.Personal.Count == 0;

    /// <summary>Er is wel een lijst, maar het filter laat niets over.</summary>
    public bool HasNoMatches => !IsEmpty && _rows.Count == 0 && !HasNoKindItems;

    // ---- Huidige zender ----

    public ChannelRow? Current
    {
        get => _current;
        private set
        {
            if (Set(ref _current, value))
            {
                if (value is not null) _browsing = false;
                Raise(nameof(ShowBrowse));
                Raise(nameof(CanBrowse));
                RaiseVideoState();
                RaiseRecordingState();
            }
        }
    }

    /// <summary>Er speelt niets: dan tonen we het startscherm in plaats van een zwart videovlak.</summary>
    public bool ShowStartScreen => _current is null;

    private bool _videoStarted;

    /// <summary>Er is gekozen, maar de speler heeft nog geen beeld: dan tonen we "Laden…" in plaats van een wit vlak.</summary>
    public bool ShowLoading => _current is not null && !_videoStarted;

    /// <summary>De videospeler (een eigen Windows-venster) tonen we pas als er echt beeld is.</summary>
    public bool ShowVideo => _current is not null && _videoStarted && !IsCasting;

    /// <summary>Het startscherm of het laadscherm staat over de speler heen.</summary>
    public bool ShowCover => !ShowVideo;

    private void SetVideoStarted(bool value)
    {
        if (_videoStarted == value) return;
        _videoStarted = value;
        RaiseVideoState();
    }

    private void RaiseVideoState()
    {
        Raise(nameof(ShowStartScreen));
        Raise(nameof(ShowLoading));
        Raise(nameof(ShowVideo));
        Raise(nameof(ShowCover));
        Raise(nameof(ShowCastButton));
        Raise(nameof(ShowCastCover));
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string? NowTitle { get => _nowTitle; private set => Set(ref _nowTitle, value); }
    public string? NowTime { get => _nowTime; private set => Set(ref _nowTime, value); }
    public string? NowDescription { get => _nowDescription; private set => Set(ref _nowDescription, value); }
    public string? NextText { get => _nextText; private set => Set(ref _nextText, value); }

    /// <summary>0..1: hoever het huidige programma, de film of de aflevering is.</summary>
    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    public bool IsFullscreen
    {
        get => _isFullscreen;
        set
        {
            if (Set(ref _isFullscreen, value)) Raise(nameof(ShowChrome));
        }
    }

    /// <summary>In volledig scherm verbergen we lijst en menu.</summary>
    public bool ShowChrome => !_isFullscreen;

    public int Volume
    {
        get => _volume;
        set
        {
            var v = Math.Clamp(value, 0, 100);
            if (Set(ref _volume, v)) _player.Volume = v;
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (Set(ref _isMuted, value)) _player.IsMuted = value;
        }
    }

    // ---- Commands ----

    public ICommand NextCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ToggleMuteCommand { get; }
    public ICommand ToggleFullscreenCommand { get; }
    public ICommand TogglePauseCommand { get; }
    public ICommand SeekBackCommand { get; }
    public ICommand SeekForwardCommand { get; }
    public ICommand BackToLiveCommand { get; }
    public ICommand CloseSeriesCommand { get; }

    // ---- Lijst bewerken ----

    private int IndexOfRow(ChannelRow row)
    {
        for (var i = 0; i < _rows.Count; i++)
            if (_rows[i].Model.Id == row.Model.Id) return i;
        return -1;
    }

    internal void Move(ChannelRow row, int delta)
    {
        var idx = IndexOfRow(row);
        var target = idx + delta;
        if (idx < 0 || target < 0 || target >= _rows.Count) return;
        _library.Swap(row.Model, _rows[target].Model);
    }

    internal void MoveToTop(ChannelRow row) => _library.MoveToTop(row.Model);

    internal void Remove(ChannelRow row)
    {
        var word = KindText.Singular(row.Kind);
        if (!_dialogs.Confirm($"{char.ToUpperInvariant(word[0])}{word.Substring(1)} verwijderen",
                $"'{row.Name}' uit je persoonlijke lijst halen?\n\nBij een volgende update wordt deze {word} niet opnieuw aangeboden."))
            return;
        if (Current?.Model.Id == row.Model.Id) Stop();
        if (_openSeries?.Model.Id == row.Model.Id) CloseSeries();
        _library.Remove(row.Model);
    }

    /// <summary>Vraagt om een eigen weergavenaam; de officiële naam uit de import blijft gekoppeld aan de bron.</summary>
    internal void Rename(ChannelRow row)
    {
        var answer = _dialogs.AskText("Naam wijzigen",
            $"Welke naam wil je in de app zien?\n\nOfficiële naam: {row.Model.Name}\nLaat het veld leeg om de officiële naam terug te zetten.",
            row.Name);
        if (answer is null) return;
        SetName(row, answer);
    }

    internal void ResetName(ChannelRow row) => SetName(row, null);

    private void SetName(ChannelRow row, string? name)
    {
        _library.Rename(row.Model, name);
        row.NameChanged();
        if (Current?.Model.Id == row.Model.Id && !ReferenceEquals(Current, row)) Current.NameChanged();
    }

    /// <summary>Na vergrendelen verdwijnt erotiek uit de lijsten, en stopt wat daarvan nog speelt.</summary>
    private void OnLockChanged()
    {
        if (Current is { } playing && _lock.Hides(playing.Model))
        {
            if (_openSeries?.Model.Id == playing.Model.Id) CloseSeries();
            Stop();
        }
        if (_openSeries is { } open && _lock.Hides(open.Model)) CloseSeries();
        Reload();
    }

    internal void SetFavorite(ChannelRow row, bool value)
    {
        // Geen volledige herbouw van de lijst (dan springt de scrollpositie naar boven), tenzij het filter dat nodig heeft.
        _suppressReload = true;
        try { _library.SetFavorite(row.Model, value); }
        finally { _suppressReload = false; }
        if (_favoritesOnly) Reload();
    }

    private void OnLibraryChanged()
    {
        if (!_suppressReload) Reload();
    }

    // ---- Lijst opbouwen ----

    /// <summary>Bouwt de lijst opnieuw op (na wijziging van lijst, filter of EPG).</summary>
    public void Reload()
    {
        var everything = _library.Ordered().Where(p => !_lock.Hides(p)).ToList();

        // Tabbladen: alleen soorten waarvan er iets in de lijst staat.
        var kinds = everything.Select(p => p.Kind).Distinct().OrderBy(k => (int)k).ToList();
        foreach (var k in Enum.GetValues<ContentKind>()) _kindCounts[(int)k] = everything.Count(p => p.Kind == k);
        Raise(nameof(LiveCount));
        Raise(nameof(MovieCount));
        Raise(nameof(SeriesCount));
        Raise(nameof(HasNoKindItems));
        Raise(nameof(KindEmptyText));

        if (!_kindChosen && kinds.Count > 0 && !kinds.Contains(_selectedKind))
        {
            _selectedKind = kinds[0];
            _selectedGroup = AllGroups;
        }
        var options = kinds.Select(k => new KindOption(k, KindText.Plural(k, capital: true))).ToList();
        if (!options.SequenceEqual(_kindOptions)) KindOptions = options;
        Raise(nameof(SelectedKindOption));
        Raise(nameof(SelectedKind));

        var all = everything.Where(p => p.Kind == _selectedKind).ToList();
        // Stabiel sorteren: wat dezelfde (of geen) datum heeft houdt de lijstvolgorde.
        if (_sortNewest && _selectedKind != ContentKind.Live) all = all.OrderByDescending(p => p.Added ?? 0).ToList();
        Raise(nameof(CanSortNewest));
        BuildCategories(all);

        var groups = new List<string> { AllGroups };
        groups.AddRange(all.Select(p => p.Group).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase));
        if (!groups.Contains(_selectedGroup, StringComparer.OrdinalIgnoreCase))
            _selectedGroup = AllGroups;
        if (!groups.SequenceEqual(_groups)) Groups = groups;
        Raise(nameof(SelectedGroup)); // houdt de selectie in de lijst gelijk met het filter

        IEnumerable<PersonalChannel> q = all;
        if (!string.Equals(_selectedGroup, AllGroups, StringComparison.OrdinalIgnoreCase))
            q = q.Where(p => string.Equals(p.Group, _selectedGroup, StringComparison.OrdinalIgnoreCase));
        if (_favoritesOnly) q = q.Where(p => p.IsFavorite);
        var term = _searchText.Trim();
        if (term.Length > 0)
            q = q.Where(p => p.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                             p.Name.Contains(term, StringComparison.OrdinalIgnoreCase));

        var currentId = Current?.Model.Id;
        var rows = SyncRows(q.ToList());
        foreach (var r in rows) r.IsPlaying = currentId != null && r.Model.Id == currentId;

        Raise(nameof(IsEmpty));
        Raise(nameof(HasNoMatches));

        // De speler blijft bij het huidige item, ook als dat door het filter niet in de lijst staat.
        if (Current is not null)
        {
            var still = _library.Personal.FirstOrDefault(p => p.Id == currentId);
            // Een eigen opname staat niet in je lijst; die blijft gewoon spelen.
            if (still is null && Current.Model.SourceId != RecordingsSourceId && _downloads?.OwnsUrl(Current.Model.Url) != true) Current = null;
            else Current = rows.FirstOrDefault(r => r.Model.Id == currentId) ?? Current;
        }

        RefreshWatchState();
        UpdateNowNext();
        RaiseBrowseTitles();
    }

    /// <summary>
    /// Brengt de zichtbare lijst naar de gewenste inhoud. Als er alleen rijen verdwijnen of van plek wisselen
    /// gebeurt dat "ter plekke", zodat de lijst in beeld niet naar boven springt; bij toevoegingen wordt een nieuwe lijst gezet.
    /// </summary>
    private IReadOnlyList<ChannelRow> SyncRows(List<PersonalChannel> wanted)
    {
        var current = (ObservableCollection<ChannelRow>)_rows;
        var byModel = new Dictionary<PersonalChannel, ChannelRow>(ReferenceEqualityComparer.Instance);
        foreach (var r in current) byModel[r.Model] = r;

        var target = new List<ChannelRow>(wanted.Count);
        var hasAdditions = false;
        foreach (var p in wanted)
        {
            if (byModel.TryGetValue(p, out var existing)) target.Add(existing);
            else
            {
                target.Add(new ChannelRow(this, p));
                hasAdditions = true;
            }
        }

        if (hasAdditions)
        {
            Rows = new ObservableCollection<ChannelRow>(target);
            return target;
        }

        var keep = new HashSet<ChannelRow>(target);
        for (var i = current.Count - 1; i >= 0; i--)
            if (!keep.Contains(current[i])) current.RemoveAt(i);

        for (var i = 0; i < target.Count; i++)
        {
            if (ReferenceEquals(current[i], target[i])) continue;
            current.Move(current.IndexOf(target[i]), i);
        }
        return target;
    }

    /// <summary>Elke halve minuut aanroepen om "nu bezig" bij te werken.</summary>
    public void Tick() => UpdateNowNext();
}
