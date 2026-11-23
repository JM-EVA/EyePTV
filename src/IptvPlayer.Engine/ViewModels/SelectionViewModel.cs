using System.Windows.Input;

namespace IptvPlayer.Engine.ViewModels;

public enum SelectionPurpose
{
    /// <summary>Eerste import: de gebruiker kiest welke zenders, films en series in zijn lijst komen.</summary>
    InitialImport,
    /// <summary>Update: alleen het nieuwe aanbod wordt getoond; de gebruiker kiest wat erbij mag.</summary>
    Review,
    /// <summary>Later nog eens door de hele lijst van de bron bladeren om iets toe te voegen of weg te halen.</summary>
    Browse
}

public sealed class SelectableChannel : ObservableObject
{
    private readonly SelectionViewModel _owner;
    private bool _isSelected;

    internal SelectableChannel(SelectionViewModel owner, Channel channel, bool selected)
    {
        _owner = owner;
        Channel = channel;
        HasCatchup = CatchupUrl.IsAvailable(channel);
        _isSelected = selected;
    }

    public Channel Channel { get; }
    public string Name => Channel.Name;
    public string Group => Channel.Group;
    public string? LogoUrl => Channel.LogoUrl;
    public ContentKind Kind => Channel.Kind;

    /// <summary>Terugkijken mogelijk (en bruikbaar in deze app).</summary>
    public bool HasCatchup { get; }
    public string CatchupText => HasCatchup ? $"↺ {Channel.CatchupDays} {(Channel.CatchupDays == 1 ? "dag" : "dagen")}" : "";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value)) _owner.OnItemToggled(this);
        }
    }

    internal void SetSilently(bool value)
    {
        if (_isSelected == value) return;
        _isSelected = value;
        Raise(nameof(IsSelected));
    }
}

public sealed class GroupItem : ObservableObject
{
    private int _selectedCount;

    public GroupItem(ContentKind kind, string? name, int total)
    {
        Kind = kind;
        Name = name;
        Total = total;
    }

    public ContentKind Kind { get; }

    /// <summary>Null = "Alle zenders" / "Alle films" / "Alle series".</summary>
    public string? Name { get; }
    public int Total { get; internal set; }

    public int SelectedCount
    {
        get => _selectedCount;
        internal set
        {
            if (Set(ref _selectedCount, value)) Raise(nameof(Label));
        }
    }

    public string Label => $"{Name ?? "Alle " + KindText.Plural(Kind)}  ({SelectedCount}/{Total})";
}

/// <summary>Een tab bovenin: Zenders, Films of Series, met hoeveel er van gekozen is.</summary>
public sealed class KindTab : ObservableObject
{
    private int _selectedCount;

    public KindTab(ContentKind kind, int total)
    {
        Kind = kind;
        Total = total;
    }

    public ContentKind Kind { get; }
    public int Total { get; }

    public int SelectedCount
    {
        get => _selectedCount;
        internal set
        {
            if (Set(ref _selectedCount, value)) Raise(nameof(Label));
        }
    }

    public string Label => $"{KindText.Plural(Kind, capital: true)}  ({SelectedCount}/{Total})";
}

/// <summary>Een zender/film/serie uit de lijst die de bron niet meer aanbiedt. Standaard staat hij aangevinkt om te verwijderen.</summary>
public sealed class RemovedItem : ObservableObject
{
    private bool _remove = true;

    public RemovedItem(PersonalChannel channel) => Channel = channel;

    public PersonalChannel Channel { get; }
    public string Name => Channel.Name;
    public string Group => Channel.Group;
    public ContentKind Kind => Channel.Kind;

    public bool Remove
    {
        get => _remove;
        set => Set(ref _remove, value);
    }
}

public sealed class SelectionViewModel : ObservableObject
{
    private readonly List<SelectableChannel> _all;
    private readonly List<ContentKind> _kinds;
    private readonly Dictionary<ContentKind, KindTab> _tabByKind = new();
    private readonly Dictionary<ContentKind, GroupItem> _allGroupByKind = new();
    private readonly Dictionary<ContentKind, List<GroupItem>> _groupsByKind = new();
    private readonly Dictionary<(ContentKind, string), GroupItem> _groupLookup = new();
    private bool _suspend;

    private ContentKind _selectedKind;
    private GroupItem _selectedGroup;
    private string _searchText = "";
    private bool _showOnlySelected;
    private bool _onlyCatchup;
    private IReadOnlyList<SelectableChannel> _visible = Array.Empty<SelectableChannel>();
    private int _selectedCount;

    public SelectionViewModel(
        IptvSource source,
        IReadOnlyList<Channel> candidates,
        IEnumerable<string> preselectedKeys,
        SelectionPurpose purpose,
        IEnumerable<PersonalChannel>? removedAtSource = null)
    {
        Source = source;
        Purpose = purpose;

        var pre = new HashSet<string>(preselectedKeys);
        _all = candidates.Select(c => new SelectableChannel(this, c, pre.Contains(c.Key))).ToList();

        _kinds = _all.Select(c => c.Kind).Distinct().OrderBy(k => (int)k).ToList();
        if (_kinds.Count == 0) _kinds.Add(ContentKind.Live);

        foreach (var kind in _kinds)
        {
            var ofKind = _all.Where(c => c.Kind == kind).ToList();
            _tabByKind[kind] = new KindTab(kind, ofKind.Count);

            var all = new GroupItem(kind, null, ofKind.Count);
            _allGroupByKind[kind] = all;
            var list = new List<GroupItem> { all };
            foreach (var grp in ofKind.GroupBy(c => c.Group, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var item = new GroupItem(kind, grp.First().Group, grp.Count());
                list.Add(item);
                _groupLookup[(kind, grp.Key.ToLowerInvariant())] = item;
            }
            _groupsByKind[kind] = list;
        }

        KindTabs = _kinds.Select(k => _tabByKind[k]).ToList();
        _selectedKind = _kinds[0];
        _selectedGroup = _allGroupByKind[_selectedKind];

        RemovedAtSource = (removedAtSource ?? Enumerable.Empty<PersonalChannel>()).Select(p => new RemovedItem(p)).ToList();

        RecountAll();
        RefreshVisible();

        SelectVisibleCommand = new RelayCommand(() => SetVisible(true));
        DeselectVisibleCommand = new RelayCommand(() => SetVisible(false));
        ConfirmCommand = new RelayCommand(() => Confirmed?.Invoke(this));
        CancelCommand = new RelayCommand(() => Cancelled?.Invoke(this));
    }

    public IptvSource Source { get; }
    public SelectionPurpose Purpose { get; }

    public event Action<SelectionViewModel>? Confirmed;
    public event Action<SelectionViewModel>? Cancelled;

    public ICommand SelectVisibleCommand { get; }
    public ICommand DeselectVisibleCommand { get; }
    public ICommand ConfirmCommand { get; }
    public ICommand CancelCommand { get; }

    private string Wanted => KindText.JoinNatural(_kinds.Select(k => KindText.Plural(k)).ToList());

    public string Title => Purpose switch
    {
        SelectionPurpose.InitialImport => $"Kies je {Wanted} van {Source.Name}",
        SelectionPurpose.Review => $"Wijzigingen bij {Source.Name}",
        _ => $"{Source.Name} beheren"
    };

    public string Subtitle => Purpose switch
    {
        SelectionPurpose.InitialImport =>
            $"Vink aan wat je in je persoonlijke lijst wilt ({Wanted}). Alles wat je niet aanvinkt blijft buiten je lijst en wordt bij updates niet opnieuw aangeboden.",
        SelectionPurpose.Review =>
            "Dit is nieuw bij je provider. Vink aan wat je aan je lijst wilt toevoegen; de rest wordt overgeslagen en komt niet meer terug.",
        _ =>
            "Dit is het volledige aanbod van je provider. Aangevinkt staat in je persoonlijke lijst; haal een vinkje weg om iets te verwijderen."
    };

    public string ConfirmText => Purpose switch
    {
        SelectionPurpose.InitialImport => "Lijst opslaan",
        SelectionPurpose.Review => "Toepassen",
        _ => "Opslaan"
    };

    // ---- Soort (tabs) ----

    public IReadOnlyList<KindTab> KindTabs { get; }
    public bool HasKindTabs => KindTabs.Count > 1;

    public KindTab SelectedKindTab
    {
        get => _tabByKind[_selectedKind];
        set
        {
            if (value is null || value.Kind == _selectedKind) return;
            _selectedKind = value.Kind;
            _selectedGroup = _allGroupByKind[_selectedKind];
            Raise(nameof(SelectedKindTab));
            Raise(nameof(Groups));
            Raise(nameof(SelectedGroup));
            RefreshVisible();
        }
    }

    // ---- Groepen, zoeken, lijst ----

    public IReadOnlyList<GroupItem> Groups => _groupsByKind[_selectedKind];
    public IReadOnlyList<RemovedItem> RemovedAtSource { get; }
    public bool HasRemoved => RemovedAtSource.Count > 0;
    public int TotalCount => _all.Count;

    public GroupItem SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (value is null) return;
            if (Set(ref _selectedGroup, value)) RefreshVisible();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value ?? "")) RefreshVisible();
        }
    }

    public bool ShowOnlySelected
    {
        get => _showOnlySelected;
        set
        {
            if (Set(ref _showOnlySelected, value)) RefreshVisible();
        }
    }

    /// <summary>Aantal zenders in het aanbod met terugkijken; het filter is alleen zinvol als dit &gt; 0 is.</summary>
    public int CatchupAvailableCount => _all.Count(c => c.HasCatchup);
    public bool HasCatchupChannels => CatchupAvailableCount > 0;

    /// <summary>Toon alleen zenders met terugkijken.</summary>
    public bool OnlyCatchup
    {
        get => _onlyCatchup;
        set
        {
            if (Set(ref _onlyCatchup, value)) RefreshVisible();
        }
    }

    /// <summary>"3 met terugkijken" of "geen met terugkijken": dat moet bij het kiezen meteen duidelijk zijn.</summary>
    public string CatchupSummary
    {
        get
        {
            var picked = _all.Count(c => c.IsSelected && c.HasCatchup);
            var total = CatchupAvailableCount;
            return total == 0
                ? "Geen enkele zender in dit aanbod heeft terugkijken"
                : $"↺ Terugkijken: {picked} gekozen, {total} beschikbaar in dit aanbod";
        }
    }

    public IReadOnlyList<SelectableChannel> VisibleChannels
    {
        get => _visible;
        private set => Set(ref _visible, value);
    }

    public int SelectedCount
    {
        get => _selectedCount;
        private set
        {
            if (Set(ref _selectedCount, value))
            {
                Raise(nameof(SummaryText));
                Raise(nameof(CatchupSummary));
            }
        }
    }

    public string SummaryText
    {
        get
        {
            var word = _kinds.Count == 1 ? KindText.Plural(_kinds[0]) : "items";
            return $"{SelectedCount} van {TotalCount} {word} geselecteerd";
        }
    }

    // ---- Resultaat ----

    public IReadOnlyList<string> CandidateKeys => _all.Select(c => c.Channel.Key).ToList();
    public IReadOnlyList<string> SelectedKeys => _all.Where(c => c.IsSelected).Select(c => c.Channel.Key).ToList();
    public IReadOnlyList<string> RemoveKeys => RemovedAtSource.Where(r => r.Remove).Select(r => r.Channel.Key).ToList();

    // ---- Intern ----

    private GroupItem? GroupOf(SelectableChannel c) =>
        _groupLookup.TryGetValue((c.Kind, c.Group.ToLowerInvariant()), out var g) ? g : null;

    internal void OnItemToggled(SelectableChannel item)
    {
        if (_suspend) return;
        var delta = item.IsSelected ? 1 : -1;
        SelectedCount += delta;
        _tabByKind[item.Kind].SelectedCount += delta;
        _allGroupByKind[item.Kind].SelectedCount += delta;
        var g = GroupOf(item);
        if (g != null) g.SelectedCount += delta;
    }

    private void SetVisible(bool value)
    {
        _suspend = true;
        try
        {
            foreach (var item in _visible) item.SetSilently(value);
        }
        finally { _suspend = false; }
        RecountAll();
    }

    private void RecountAll()
    {
        var total = 0;
        var perTab = new Dictionary<ContentKind, int>();
        var perGroup = new Dictionary<GroupItem, int>();
        foreach (var c in _all)
        {
            if (!c.IsSelected) continue;
            total++;
            perTab[c.Kind] = perTab.GetValueOrDefault(c.Kind) + 1;
            var g = GroupOf(c);
            if (g != null) perGroup[g] = perGroup.GetValueOrDefault(g) + 1;
        }

        SelectedCount = total;
        foreach (var (kind, tab) in _tabByKind)
        {
            tab.SelectedCount = perTab.GetValueOrDefault(kind);
            _allGroupByKind[kind].SelectedCount = perTab.GetValueOrDefault(kind);
        }
        foreach (var g in _groupLookup.Values) g.SelectedCount = perGroup.GetValueOrDefault(g);
    }

    private void RefreshVisible()
    {
        IEnumerable<SelectableChannel> q = _all.Where(c => c.Kind == _selectedKind);

        if (_selectedGroup.Name is not null)
            q = q.Where(c => string.Equals(c.Group, _selectedGroup.Name, StringComparison.OrdinalIgnoreCase));

        var term = _searchText.Trim();
        if (term.Length > 0)
            q = q.Where(c => c.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                          || c.Group.Contains(term, StringComparison.OrdinalIgnoreCase));

        if (_showOnlySelected)
            q = q.Where(c => c.IsSelected);

        if (_onlyCatchup)
            q = q.Where(c => c.HasCatchup);

        VisibleChannels = q.ToList();
    }
}
