using System.Windows.Input;

namespace IptvPlayer.Engine.ViewModels;

/// <summary>Een plank op het bladerscherm: een categorie met haar eerste titels (pas opgebouwd als de plank in beeld komt).</summary>
public sealed class CategoryShelf
{
    private readonly Func<IReadOnlyList<ChannelRow>> _build;
    private IReadOnlyList<ChannelRow>? _items;

    internal CategoryShelf(string group, int count, string key, Func<IReadOnlyList<ChannelRow>> build)
    {
        Group = group;
        Count = count;
        Key = key;
        _build = build;
    }

    public string Name => Group;
    public string Group { get; }
    public int Count { get; }
    public string CountText => Count == 1 ? "1 titel" : $"{Count} titels";
    public IReadOnlyList<ChannelRow> Items => _items ??= _build();

    /// <summary>Groep, aantal en de getoonde titels: verandert dit, dan moet de plank opnieuw getekend worden.</summary>
    internal string Key { get; }
}

public sealed partial class ChannelsViewModel
{
    /// <summary>Zoveel posters staan er op één plank; de rest zie je via "Alles".</summary>
    public const int ShelfSize = 18;

    private IReadOnlyList<CategoryShelf> _shelves = Array.Empty<CategoryShelf>();

    /// <summary>Laag 1: één plank met posters per categorie.</summary>
    public IReadOnlyList<CategoryShelf> Shelves => _shelves;

    /// <summary>Zoveel titels tonen we maximaal als tegels (alles tegelijk maken is te zwaar bij tienduizenden films).</summary>
    public const int BrowseLimit = 240;

    private bool _browsing;
    private string? _browseGroup; // null: laag 1 (categorieën); anders laag 2 (titels van die groep)

    private void InitBrowse()
    {
        OpenCategoryCommand = new RelayCommand(o => OpenCategory(o as string));
        BrowseCommand = new RelayCommand(ShowCategories, () => CanBrowse);
    }

    public ICommand OpenCategoryCommand { get; private set; } = null!;
    public ICommand BrowseCommand { get; private set; } = null!;

    /// <summary>
    /// Films en Series openen op een scherm met categorieën in plaats van op de speler. Alleen zolang er niets speelt:
    /// de videospeler is een eigen Windows-venster en zou over dit scherm heen blijven staan.
    /// </summary>
    public bool ShowBrowse => _browsing && _selectedKind != ContentKind.Live && _current is null && _openSeries is null
                              && _kindCounts[(int)_selectedKind] > 0;

    public bool ShowBrowseGroups => ShowBrowse && _browseGroup is null;
    public bool ShowBrowseTitles => ShowBrowse && _browseGroup is not null;

    /// <summary>Laag 2: de films of series van de gekozen groep, als posters.</summary>
    public IReadOnlyList<ChannelRow> BrowseTitles => ShowBrowseTitles ? _rows.Take(BrowseLimit).ToList() : Array.Empty<ChannelRow>();

    public bool BrowseTitlesTruncated => ShowBrowseTitles && _rows.Count > BrowseLimit;
    public bool HasNoBrowseTitles => ShowBrowseTitles && _rows.Count == 0;

    public string BrowseGroupTitle => _browseGroup is null or AllGroups ? BrowseTitle : _browseGroup;

    public string BrowseTruncatedText => $"Eerste {BrowseLimit} van {_rows.Count} getoond. Typ in het zoekvak om te verfijnen.";

    // Na het opbouwen van de lijst (Reload) de tegels van laag 2 verversen.
    private void RaiseBrowseTitles()
    {
        Raise(nameof(ShowBrowseGroups));
        Raise(nameof(HasNoCategories));
        Raise(nameof(ShowBrowseTitles));
        Raise(nameof(BrowseTitles));
        Raise(nameof(BrowseTitlesTruncated));
        Raise(nameof(HasNoBrowseTitles));
        Raise(nameof(BrowseGroupTitle));
        Raise(nameof(BrowseTruncatedText));
    }

    public bool CanBrowse => _selectedKind != ContentKind.Live && _current is null && _kindCounts[(int)_selectedKind] > 0;

    public string BrowseTitle => KindText.Plural(_selectedKind, capital: true);

    public bool HasNoCategories => ShowBrowseGroups && _shelves.Count == 0;

    private void OpenCategory(string? group)
    {
        _browseGroup = string.IsNullOrEmpty(group) ? AllGroups : group;
        _selectedGroup = _browseGroup;
        Raise(nameof(SelectedGroup));
        Reload();
    }

    private void ShowCategories()
    {
        _browsing = true;
        _browseGroup = null;
        _selectedGroup = AllGroups;
        Reload();
    }

    // Eén plank per groep, gefilterd op wat je typt.
    private void BuildCategories(List<PersonalChannel> all)
    {
        var term = _searchText.Trim();
        var items = term.Length == 0
            ? all
            : all.Where(p => p.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                             p.Name.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();

        var shelves = new List<CategoryShelf>();
        foreach (var g in items.GroupBy(p => p.Group, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var first = g.Take(ShelfSize).ToList();
            var count = g.Count();
            shelves.Add(new CategoryShelf(g.Key, count, $"{g.Key}|{count}|{string.Join(",", first.Select(p => p.Id))}",
                () => first.Select(RowFor).ToList()));
        }

        // Alleen vervangen als er echt iets veranderd is (groep, aantal of de getoonde titels),
        // anders springt het scherm bij elke verversing terug naar boven.
        if (!shelves.Select(x => x.Key).SequenceEqual(_shelves.Select(x => x.Key)))
        {
            _shelves = shelves;
            Raise(nameof(Shelves));
        }
        Raise(nameof(HasNoCategories));
        Raise(nameof(ShowBrowse));
        Raise(nameof(CanBrowse));
        Raise(nameof(BrowseTitle));
    }
}
