namespace IptvPlayer.Engine.ViewModels;

public enum BeheerSection
{
    Bronnen,
    Opnames,
    Downloads,
    Voorkeuren
}

/// <summary>Een tabblad binnen Beheer.</summary>
public sealed record BeheerTab(BeheerSection Section, string Title);

/// <summary>
/// De pagina Beheer: alles om je app in te richten op één plek. Daarin zitten je bronnen, je opnames
/// (met de instellingen om op te nemen) en je voorkeuren (taal, beginscherm).
/// </summary>
public sealed class BeheerViewModel : ObservableObject
{
    private BeheerSection _section = BeheerSection.Bronnen;

    public BeheerViewModel(SourcesViewModel sources, SettingsViewModel settings)
    {
        Sources = sources;
        Settings = settings;
        Tabs = new[]
        {
            new BeheerTab(BeheerSection.Bronnen, "Bronnen"),
            new BeheerTab(BeheerSection.Opnames, "Opnames"),
            new BeheerTab(BeheerSection.Downloads, "Downloads"),
            new BeheerTab(BeheerSection.Voorkeuren, "Voorkeuren"),
        };
    }

    public SourcesViewModel Sources { get; }
    public SettingsViewModel Settings { get; }

    public IReadOnlyList<BeheerTab> Tabs { get; }

    public BeheerSection Section => _section;

    public BeheerTab SelectedTab
    {
        get => Tabs.First(t => t.Section == _section);
        set
        {
            if (value is null) return; // een ListBox stuurt null als zijn lijst wisselt
            Select(value.Section);
        }
    }

    public bool IsBronnen => _section == BeheerSection.Bronnen;
    public bool IsOpnames => _section == BeheerSection.Opnames;
    public bool IsDownloads => _section == BeheerSection.Downloads;
    public bool IsVoorkeuren => _section == BeheerSection.Voorkeuren;

    public void Select(BeheerSection section)
    {
        if (_section == section) return;
        _section = section;
        Raise(nameof(Section));
        Raise(nameof(SelectedTab));
        Raise(nameof(IsBronnen));
        Raise(nameof(IsOpnames));
        Raise(nameof(IsDownloads));
        Raise(nameof(IsVoorkeuren));
        if (section == BeheerSection.Bronnen) Sources.RefreshRows();
    }
}
