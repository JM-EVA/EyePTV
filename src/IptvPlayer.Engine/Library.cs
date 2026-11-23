namespace IptvPlayer.Engine;

/// <summary>Verschil tussen wat de bron nu aanbiedt en wat de gebruiker al kent.</summary>
public sealed class SyncDiff
{
    /// <summary>Zenders die de gebruiker nog nooit heeft gezien: hierover moet hij beslissen.</summary>
    public List<Channel> New { get; } = new();

    /// <summary>Zenders uit de persoonlijke lijst die de bron niet meer aanbiedt.</summary>
    public List<PersonalChannel> Removed { get; } = new();

    /// <summary>Zenders uit de persoonlijke lijst waarvan naam/link/logo is veranderd (wordt automatisch bijgewerkt).</summary>
    public List<(PersonalChannel Existing, Channel Fresh)> Updated { get; } = new();

    public bool NeedsReview => New.Count > 0 || Removed.Count > 0;

    public static SyncDiff Compute(IReadOnlyList<Channel> catalog, SourceState state, IEnumerable<PersonalChannel> personalOfSource)
    {
        var diff = new SyncDiff();
        var byKey = new Dictionary<string, Channel>(catalog.Count);
        foreach (var c in catalog) byKey[c.Key] = c;

        foreach (var c in catalog)
            if (!state.KnownKeys.Contains(c.Key)) diff.New.Add(c);

        foreach (var p in personalOfSource)
        {
            if (!byKey.TryGetValue(p.Key, out var fresh)) diff.Removed.Add(p);
            else if (p.DiffersFrom(fresh)) diff.Updated.Add((p, fresh));
        }
        return diff;
    }
}

public enum SyncOutcomeKind
{
    /// <summary>Eerste keer: de gebruiker moet een selectie maken.</summary>
    InitialImport,
    /// <summary>Er zijn nieuwe of verdwenen zenders om over te beslissen.</summary>
    ReviewNeeded,
    UpToDate
}

public sealed record SyncOutcome(SyncOutcomeKind Kind, Catalog Catalog, SyncDiff? Diff);

/// <summary>
/// De persoonlijke zenderlijst, de bronnen en wat er per bron al gezien is.
/// Alles wordt direct opgeslagen via <see cref="AppStore"/>.
/// </summary>
public sealed class Library
{
    private const string SourcesFile = "sources.json";
    private const string PersonalFile = "personal.json";

    private readonly AppStore _store;
    private readonly Dictionary<string, SourceState> _states = new();

    public AppStore Store => _store;

    public List<IptvSource> Sources { get; }
    public List<PersonalChannel> Personal { get; }

    public event Action? Changed;

    public Library(AppStore store)
    {
        _store = store;
        Sources = _store.Load(SourcesFile, () => new List<IptvSource>());
        Personal = _store.Load(PersonalFile, () => new List<PersonalChannel>());
        foreach (var s in Sources)
            _states[s.Id] = _store.Load(StateFile(s.Id), () => new SourceState { SourceId = s.Id });
        Renumber();
    }

    private static string StateFile(string id) => $"state-{id}.json";
    private static string CatalogFile(string id) => $"catalog-{id}.json";

    // ---------- Bronnen ----------

    public IptvSource AddSource(IptvSource source)
    {
        Sources.Add(source);
        _states[source.Id] = new SourceState { SourceId = source.Id };
        SaveAll();
        return source;
    }

    public void UpdateSource(IptvSource source)
    {
        SaveSources();
        Changed?.Invoke();
    }

    public void RemoveSource(string sourceId)
    {
        Sources.RemoveAll(s => s.Id == sourceId);
        Personal.RemoveAll(p => p.SourceId == sourceId);
        _states.Remove(sourceId);
        _store.Delete(StateFile(sourceId));
        _store.Delete(CatalogFile(sourceId));
        Renumber();
        SaveAll();
    }

    public SourceState GetState(string sourceId)
    {
        if (!_states.TryGetValue(sourceId, out var st))
            _states[sourceId] = st = new SourceState { SourceId = sourceId };
        return st;
    }

    public IEnumerable<PersonalChannel> PersonalOf(string sourceId) => Personal.Where(p => p.SourceId == sourceId);

    // ---------- Catalogus-cache ----------

    public Catalog? LoadCatalog(string sourceId)
    {
        var dto = _store.Load<CatalogDto?>(CatalogFile(sourceId), () => null);
        return dto is null ? null : new Catalog(dto.Channels, dto.EpgUrl);
    }

    private void SaveCatalog(string sourceId, Catalog catalog) =>
        _store.Save(CatalogFile(sourceId), new CatalogDto { EpgUrl = catalog.EpgUrl, Channels = catalog.Channels.ToList() }, indented: false);

    private sealed class CatalogDto
    {
        public string? EpgUrl { get; set; }
        public List<Channel> Channels { get; set; } = new();
    }

    // ---------- Synchroniseren ----------

    /// <summary>
    /// Haalt de zenderlijst van een bron op en vergelijkt die met wat de gebruiker kent.
    /// Gewijzigde links/namen van zenders in de persoonlijke lijst worden meteen bijgewerkt;
    /// nieuwe en verdwenen zenders worden teruggegeven zodat de gebruiker erover kan beslissen.
    /// Er wordt níets toegevoegd of verwijderd totdat <see cref="CommitSelection"/> wordt aangeroepen.
    /// </summary>
    public async Task<SyncOutcome> SyncAsync(IptvSource source, CatalogService service, CancellationToken ct = default)
    {
        var fresh = await service.FetchAsync(source, ct);

        // Beveiliging: een lege lijst is vrijwel altijd een storing bij de provider, geen "alles is weg".
        if (fresh.Channels.Count == 0)
            throw new InvalidDataException("De bron gaf geen enkele zender terug. Er is niets gewijzigd.");

        var catalog = KeepPreviousForIncompleteKinds(source, fresh);

        SaveCatalog(source.Id, catalog);
        source.DiscoveredEpgUrl = catalog.EpgUrl;
        if (!string.IsNullOrWhiteSpace(catalog.ServerTimeZone)) source.ServerTimeZone = catalog.ServerTimeZone;
        source.LastSyncUtc = DateTime.UtcNow;

        if (!source.InitialImportDone)
        {
            SaveSources();
            Changed?.Invoke();
            return new SyncOutcome(SyncOutcomeKind.InitialImport, catalog, null);
        }

        var diff = ApplyCatalogToPersonal(source, catalog);
        SaveAll();
        return new SyncOutcome(diff.NeedsReview ? SyncOutcomeKind.ReviewNeeded : SyncOutcomeKind.UpToDate, catalog, diff);
    }

    /// <summary>
    /// Als zenders, films of series bij deze sync niet (volledig) zijn opgehaald, of plotseling helemaal leeg zijn,
    /// blijft de vorige lijst voor dat soort gelden. Zo worden je films niet als "verdwenen" gemarkeerd
    /// doordat de provider even haperde.
    /// </summary>
    private Catalog KeepPreviousForIncompleteKinds(IptvSource source, Catalog fresh)
    {
        var old = LoadCatalog(source.Id);
        var incomplete = new HashSet<ContentKind>(fresh.IncompleteKinds ?? Array.Empty<ContentKind>());
        var warnings = new List<string>(fresh.Warnings ?? Array.Empty<string>());

        if (old is not null)
        {
            foreach (var kind in Enum.GetValues<ContentKind>())
            {
                if (incomplete.Contains(kind)) continue;
                var had = old.Channels.Count(c => c.Kind == kind);
                var has = fresh.Channels.Count(c => c.Kind == kind);
                if (had > 0 && has == 0)
                {
                    incomplete.Add(kind);
                    warnings.Add($"{KindText.Plural(kind, capital: true)} waren leeg bij deze update; je vorige lijst is behouden.");
                }
            }
        }

        if (incomplete.Count == 0 || old is null)
            return fresh with { Warnings = warnings };

        var merged = fresh.Channels.Where(c => !incomplete.Contains(c.Kind))
            .Concat(old.Channels.Where(c => incomplete.Contains(c.Kind)))
            .ToList();
        return fresh with { Channels = merged, Warnings = warnings, IncompleteKinds = incomplete.ToList() };
    }

    /// <summary>Wat er nog openstaat voor deze bron, op basis van de laatst opgehaalde lijst (ook na herstart).</summary>
    public SyncDiff? PendingDiff(string sourceId)
    {
        var source = Sources.FirstOrDefault(s => s.Id == sourceId);
        if (source is null || !source.InitialImportDone) return null;
        var catalog = LoadCatalog(sourceId);
        if (catalog is null) return null;
        return SyncDiff.Compute(catalog.Channels, GetState(sourceId), PersonalOf(sourceId));
    }

    private SyncDiff ApplyCatalogToPersonal(IptvSource source, Catalog catalog)
    {
        var mine = PersonalOf(source.Id).ToList();
        var diff = SyncDiff.Compute(catalog.Channels, GetState(source.Id), mine);

        foreach (var (existing, fresh) in diff.Updated) existing.UpdateFrom(fresh);
        foreach (var p in mine) p.MissingAtSource = false;
        foreach (var p in diff.Removed) p.MissingAtSource = true;
        return diff;
    }

    // ---------- De keuze van de gebruiker vastleggen ----------

    /// <summary>
    /// Legt de keuze van de gebruiker vast.
    /// <list type="bullet">
    /// <item>Van de <paramref name="candidateKeys"/> worden de aangevinkte toegevoegd en de niet-aangevinkte (indien aanwezig) verwijderd.</item>
    /// <item><paramref name="removeKeys"/> zijn zenders die uit de lijst gaan (bijv. omdat de bron ze niet meer heeft).</item>
    /// <item>Alle zenders van de bron gelden daarna als "gezien", zodat ze bij een volgende update niet opnieuw als nieuw verschijnen.</item>
    /// </list>
    /// </summary>
    public void CommitSelection(string sourceId, IReadOnlyList<Channel> catalog,
        IEnumerable<string> candidateKeys, IEnumerable<string> selectedKeys, IEnumerable<string>? removeKeys = null)
    {
        var source = Sources.FirstOrDefault(s => s.Id == sourceId)
                     ?? throw new ArgumentException("Onbekende bron.", nameof(sourceId));

        var byKey = new Dictionary<string, Channel>(catalog.Count);
        foreach (var c in catalog) byKey[c.Key] = c;

        var mine = PersonalOf(sourceId).ToDictionary(p => p.Key);
        var selected = selectedKeys as ISet<string> ?? new HashSet<string>(selectedKeys);
        var nextOrder = Personal.Count == 0 ? 0 : Personal.Max(p => p.SortOrder) + 1;
        var toRemove = new HashSet<string>(removeKeys ?? Enumerable.Empty<string>());

        foreach (var key in candidateKeys)
        {
            mine.TryGetValue(key, out var existing);
            if (selected.Contains(key))
            {
                if (existing is null && byKey.TryGetValue(key, out var channel))
                {
                    var added = PersonalChannel.From(sourceId, channel, nextOrder++);
                    Personal.Add(added);
                    mine[key] = added;
                }
            }
            else if (existing is not null)
            {
                toRemove.Add(key);
            }
        }

        if (toRemove.Count > 0)
            Personal.RemoveAll(p => p.SourceId == sourceId && toRemove.Contains(p.Key));

        var state = GetState(sourceId);
        foreach (var c in catalog) state.KnownKeys.Add(c.Key);

        foreach (var p in PersonalOf(sourceId))
            if (byKey.TryGetValue(p.Key, out var fresh)) p.UpdateFrom(fresh);

        source.InitialImportDone = true;
        Renumber();
        SaveAll();
    }

    // ---------- Persoonlijke lijst bewerken ----------

    public IReadOnlyList<PersonalChannel> Ordered() => Personal.OrderBy(p => p.SortOrder).ToList();

    public void SetFavorite(PersonalChannel channel, bool value)
    {
        channel.IsFavorite = value;
        SavePersonal();
        Changed?.Invoke();
    }

    /// <summary>Geeft een zender, film of serie een eigen naam in de app (leeg of gelijk aan het origineel = terug naar de officiële naam).</summary>
    public void Rename(PersonalChannel channel, string? name)
    {
        var clean = name?.Trim();
        channel.CustomName = string.IsNullOrEmpty(clean) || clean == channel.Name ? null : clean;
        SavePersonal();
        Changed?.Invoke();
    }

    /// <summary>Stelt de reservezenders van een zender in (live zenders uit de lijst, niet de zender zelf).</summary>
    public void SetBackups(PersonalChannel channel, IEnumerable<string> ids)
    {
        var valid = Personal.Where(p => p.Kind == ContentKind.Live && p.Id != channel.Id).Select(p => p.Id).ToHashSet();
        channel.BackupIds = ids.Where(valid.Contains).Distinct().ToList();
        SavePersonal();
        Changed?.Invoke();
    }

    /// <summary>De reservezenders van een zender, in volgorde, voor zover ze nog in de lijst staan.</summary>
    public IReadOnlyList<PersonalChannel> BackupsOf(PersonalChannel channel)
    {
        var byId = Personal.ToDictionary(p => p.Id);
        return channel.BackupIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }

    public void Remove(PersonalChannel channel)
    {
        Personal.Remove(channel);
        Renumber();
        SavePersonal();
        Changed?.Invoke();
    }

    /// <summary>Wisselt de plek van twee zenders in de persoonlijke volgorde.</summary>
    public void Swap(PersonalChannel a, PersonalChannel b)
    {
        (a.SortOrder, b.SortOrder) = (b.SortOrder, a.SortOrder);
        SavePersonal();
        Changed?.Invoke();
    }

    public void MoveToTop(PersonalChannel channel)
    {
        foreach (var p in Personal) p.SortOrder++;
        channel.SortOrder = 0;
        Renumber();
        SavePersonal();
        Changed?.Invoke();
    }

    // ---------- Opslaan ----------

    private void Renumber()
    {
        var i = 0;
        foreach (var p in Personal.OrderBy(p => p.SortOrder).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList())
            p.SortOrder = i++;
    }

    private void SaveSources() => _store.Save(SourcesFile, Sources);
    private void SavePersonal()
    {
        // Reservezenders die niet meer in de lijst staan, verdwijnen vanzelf.
        var ids = Personal.Select(p => p.Id).ToHashSet();
        foreach (var p in Personal)
            if (p.BackupIds.Count > 0) p.BackupIds.RemoveAll(id => !ids.Contains(id));
        _store.Save(PersonalFile, Personal);
    }

    private void SaveAll()
    {
        SaveSources();
        SavePersonal();
        foreach (var s in Sources) _store.Save(StateFile(s.Id), GetState(s.Id), indented: false);
        Changed?.Invoke();
    }
}
