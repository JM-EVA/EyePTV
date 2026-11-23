using System.Windows.Input;

namespace IptvPlayer.Engine.ViewModels;

public sealed class SourceRow : ObservableObject
{
    private bool _isBusy;
    private string _status = "";

    public SourceRow(IptvSource source) => Source = source;

    public IptvSource Source { get; }

    /// <summary>Een bron die via http (niet versleuteld) wordt benaderd: je inlog is dan op het netwerk te onderscheppen.</summary>
    public static bool IsInsecure(IptvSource s) =>
        (s.Kind == SourceKind.Xtream ? s.Server : s.M3uUrl)?.TrimStart().StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true;

    public string? InsecureNote => IsInsecure(Source)
        ? "Let op: deze bron gebruikt http. Je inlog wordt onversleuteld verstuurd; vraag je provider of https mogelijk is."
        : null;

    public string Name => Source.Name;
    public int ChannelsInList { get; internal set; }
    public int PendingNew { get; internal set; }
    public int PendingRemoved { get; internal set; }

    public Dictionary<ContentKind, int> CountsByKind { get; } = new();
    public Dictionary<ContentKind, int> PendingNewByKind { get; } = new();
    public Dictionary<ContentKind, int> PendingRemovedByKind { get; } = new();
    public bool NeedsInitialSelection => !Source.InitialImportDone;
    public bool HasPending => Source.InitialImportDone && (PendingNew > 0 || PendingRemoved > 0);

    public string Description
    {
        get
        {
            if (Source.Kind == SourceKind.Xtream)
                return $"Xtream Codes · {Source.Server} · {Source.Username}";
            return Uri.TryCreate(Source.M3uUrl, UriKind.Absolute, out var u) ? $"M3U · {u.Host}" : "M3U";
        }
    }

    public string InfoText
    {
        get
        {
            if (NeedsInitialSelection) return "Je hebt nog niets gekozen.";

            var parts = new List<string>();
            foreach (var kind in Enum.GetValues<ContentKind>())
            {
                var n = CountsByKind.GetValueOrDefault(kind);
                if (n > 0) parts.Add($"{n} {KindText.Plural(kind)}");
            }
            if (parts.Count == 0) parts.Add("0 zenders");

            var text = string.Join(" · ", parts) + " in je lijst";
            if (PendingNew > 0) text += $" · {PendingNew} nieuw";
            if (PendingRemoved > 0) text += $" · {PendingRemoved} verdwenen";
            return text;
        }
    }

    public string LastSyncText => Source.LastSyncUtc is { } t
        ? $"Laatst bijgewerkt: {t.ToLocalTime():dd-MM-yyyy HH:mm}"
        : "Nog niet bijgewerkt";

    /// <summary>Tekst van de hoofdknop; hangt af van wat er met deze bron moet gebeuren.</summary>
    public string PrimaryActionText =>
        NeedsInitialSelection ? "Inhoud kiezen" :
        HasPending ? "Wijzigingen bekijken" :
        "Inhoud beheren";

    public bool IsBusy { get => _isBusy; internal set => Set(ref _isBusy, value); }
    public string Status { get => _status; internal set => Set(ref _status, value); }

    internal void RaiseAll()
    {
        Raise(nameof(Name));
        Raise(nameof(Description));
        Raise(nameof(InfoText));
        Raise(nameof(LastSyncText));
        Raise(nameof(PrimaryActionText));
        Raise(nameof(HasPending));
        Raise(nameof(NeedsInitialSelection));
    }
}

/// <summary>De pagina "Bronnen": bronnen toevoegen, synchroniseren en de selectie/review starten.</summary>
public sealed class SourcesViewModel : ObservableObject
{
    private readonly Library _library;
    private readonly CatalogService _catalogService;
    private readonly INavigator _nav;
    private readonly IDialogService _dialogs;
    private readonly Func<Task> _refreshEpg;

    private IReadOnlyList<SourceRow> _rows = Array.Empty<SourceRow>();
    private bool _isBusy;
    private string _statusText = "";

    // formulier
    private string _newName = "";
    private bool _isM3u = true;
    private string _newM3uUrl = "";
    private string _newServer = "";
    private string _newUser = "";
    private string _newPassword = "";
    private string _newEpgUrl = "";

    public SourcesViewModel(Library library, CatalogService catalogService, INavigator nav, IDialogService dialogs, Func<Task> refreshEpg)
    {
        _library = library;
        _catalogService = catalogService;
        _nav = nav;
        _dialogs = dialogs;
        _refreshEpg = refreshEpg;

        AddSourceCommand = new AsyncCommand(AddSourceAsync, ShowError);
        SyncAllCommand = new AsyncCommand(() => SyncAllAsync(interactive: true), ShowError, () => _library.Sources.Count > 0);
        PrimaryActionCommand = new AsyncCommand(p => p is SourceRow r ? PrimaryActionAsync(r) : Task.CompletedTask, ShowError);
        SyncOneCommand = new AsyncCommand(p => p is SourceRow r ? SyncOneAsync(r, interactive: true) : Task.CompletedTask, ShowError);
        RemoveCommand = new RelayCommand(p => { if (p is SourceRow r) RemoveSource(r); });

        RefreshRows();
    }

    /// <summary>Wordt afgevuurd als het aantal bronnen met openstaande wijzigingen kan zijn veranderd.</summary>
    public event Action? PendingChanged;

    public IReadOnlyList<SourceRow> Rows { get => _rows; private set { if (Set(ref _rows, value)) Raise(nameof(HasSources)); } }
    public bool HasSources => _rows.Count > 0;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    public ICommand AddSourceCommand { get; }
    public ICommand SyncAllCommand { get; }
    public ICommand PrimaryActionCommand { get; }
    public ICommand SyncOneCommand { get; }
    public ICommand RemoveCommand { get; }

    // ---- Formulier ----

    public string NewName { get => _newName; set => Set(ref _newName, value ?? ""); }
    public bool IsM3u
    {
        get => _isM3u;
        set
        {
            if (Set(ref _isM3u, value)) Raise(nameof(IsXtream));
        }
    }
    public bool IsXtream { get => !_isM3u; set => IsM3u = !value; }
    public string NewM3uUrl { get => _newM3uUrl; set => Set(ref _newM3uUrl, value ?? ""); }
    public string NewServer { get => _newServer; set => Set(ref _newServer, value ?? ""); }
    public string NewUser { get => _newUser; set => Set(ref _newUser, value ?? ""); }
    public string NewPassword { get => _newPassword; set => Set(ref _newPassword, value ?? ""); }
    public string NewEpgUrl { get => _newEpgUrl; set => Set(ref _newEpgUrl, value ?? ""); }

    // ---- Rijen ----

    public void RefreshRows()
    {
        var rows = new List<SourceRow>();
        foreach (var s in _library.Sources)
        {
            var mine = _library.PersonalOf(s.Id).ToList();
            var row = new SourceRow(s) { ChannelsInList = mine.Count };
            foreach (var g in mine.GroupBy(p => p.Kind)) row.CountsByKind[g.Key] = g.Count();

            var diff = _library.PendingDiff(s.Id);
            if (diff is not null)
            {
                row.PendingNew = diff.New.Count;
                row.PendingRemoved = diff.Removed.Count;
                foreach (var g in diff.New.GroupBy(c => c.Kind)) row.PendingNewByKind[g.Key] = g.Count();
                foreach (var g in diff.Removed.GroupBy(p => p.Kind)) row.PendingRemovedByKind[g.Key] = g.Count();
            }
            rows.Add(row);
        }
        Rows = rows;
        PendingChanged?.Invoke();
        (SyncAllCommand as AsyncCommand)?.RaiseCanExecuteChanged();
    }

    public IReadOnlyList<SourceRow> RowsWithPending => _rows.Where(r => r.HasPending).ToList();

    // ---- Bron toevoegen ----

    private async Task AddSourceAsync()
    {
        var source = BuildSourceFromForm(out var error);
        if (source is null)
        {
            _dialogs.ShowInfo("Bron toevoegen", error!);
            return;
        }

        IsBusy = true;
        try
        {
            if (source.Kind == SourceKind.Xtream)
            {
                StatusText = "Inloggen…";
                var client = new XtreamClient(_catalogService.Http);

                // Geen schema ingetypt: eerst het versleutelde https proberen, pas dan http. Je inlog staat in elke aanvraag.
                if (!_serverHadScheme && source.Server!.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    var secure = "https://" + source.Server["http://".Length..];
                    try
                    {
                        var probe = await client.AuthenticateAsync(new IptvSource
                            { Kind = SourceKind.Xtream, Server = secure, Username = source.Username, Password = source.Password });
                        if (probe.Ok) source.Server = secure;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { }
                }

                var auth = await client.AuthenticateAsync(source);
                if (!auth.Ok)
                {
                    _dialogs.ShowError("Inloggen mislukt", auth.Message ?? "Onbekende fout.");
                    return;
                }
            }

            _library.AddSource(source);
            StatusText = "Zenderlijst ophalen…";
            SyncOutcome outcome;
            try
            {
                outcome = await _library.SyncAsync(source, _catalogService);
            }
            catch
            {
                // Mislukt: geen half werkende bron achterlaten.
                _library.RemoveSource(source.Id);
                RefreshRows();
                throw;
            }

            ClearForm();
            RefreshRows();
            StatusText = Describe(outcome.Catalog);
            if (SourceRow.IsInsecure(source))
                StatusText += " Let op: deze bron gebruikt http; je inlog wordt onversleuteld over het netwerk gestuurd.";
            OpenSelection(source, SelectionPurpose.InitialImport);
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal IptvSource? BuildSourceFromForm(out string? error)
    {
        error = null;
        var name = _newName.Trim();

        if (_isM3u)
        {
            var url = _newM3uUrl.Trim();
            if (url.Length == 0) { error = "Vul een M3U-link in."; return null; }
            if (!Uri.TryCreate(url, UriKind.Absolute, out _)) { error = "Dit is geen geldige link."; return null; }

            // Veel providers geven een Xtream-link (get.php?username=…) als "M3U-link": dan gebruiken we de betere Xtream-API.
            if (XtreamClient.TryParseUrl(url, out var server, out var user, out var pass))
                return new IptvSource
                {
                    Name = name.Length > 0 ? name : new Uri(server).Host,
                    Kind = SourceKind.Xtream,
                    Server = server,
                    Username = user,
                    Password = pass,
                    EpgUrl = NullIfEmpty(_newEpgUrl)
                };

            return new IptvSource
            {
                Name = name.Length > 0 ? name : new Uri(url).Host,
                Kind = SourceKind.M3uUrl,
                M3uUrl = url,
                EpgUrl = NullIfEmpty(_newEpgUrl)
            };
        }

        var serverText = _newServer.Trim();
        if (XtreamClient.TryParseUrl(serverText, out var s2, out var u2, out var p2))
        {
            serverText = s2;
            if (_newUser.Trim().Length == 0) NewUser = u2;
            if (_newPassword.Length == 0) NewPassword = p2;
        }
        if (serverText.Length == 0 || _newUser.Trim().Length == 0 || _newPassword.Length == 0)
        {
            error = "Vul server, gebruikersnaam en wachtwoord in.";
            return null;
        }
        _serverHadScheme = serverText.Contains("://");
        serverText = XtreamClient.NormalizeServer(serverText);
        if (!Uri.TryCreate(serverText, UriKind.Absolute, out var serverUri))
        {
            error = "Het serveradres is ongeldig (bijvoorbeeld http://server.example:8080).";
            return null;
        }

        return new IptvSource
        {
            Name = name.Length > 0 ? name : serverUri.Host,
            Kind = SourceKind.Xtream,
            Server = serverText,
            Username = _newUser.Trim(),
            Password = _newPassword,
            EpgUrl = NullIfEmpty(_newEpgUrl)
        };
    }

    private bool _serverHadScheme = true;

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private void ClearForm()
    {
        NewName = NewM3uUrl = NewServer = NewUser = NewPassword = NewEpgUrl = "";
    }

    // ---- Synchroniseren ----

    public async Task SyncAllAsync(bool interactive)
    {
        IsBusy = true;
        var failures = new List<string>();
        var warnings = new List<string>();
        try
        {
            foreach (var row in _rows.ToList())
            {
                if (!row.Source.InitialImportDone) continue; // eerst moet de gebruiker kiezen
                try
                {
                    var outcome = await SyncCoreAsync(row, interactive: false);
                    foreach (var w in outcome.Catalog.Warnings ?? Array.Empty<string>()) warnings.Add($"{row.Name}: {w}");
                }
                catch (Exception ex) { failures.Add($"{row.Name}: {ex.Message}"); }
            }
        }
        finally
        {
            IsBusy = false;
            RefreshRows();
        }

        if (failures.Count > 0)
        {
            StatusText = "Bijwerken mislukt voor: " + string.Join("; ", failures);
            if (interactive) _dialogs.ShowError("Bijwerken mislukt", string.Join("\n", failures));
        }
        else
        {
            var pending = _rows.Count(r => r.HasPending);
            StatusText = pending > 0 ? $"Er zijn wijzigingen bij {pending} bron(nen)." : "Alles is bijgewerkt.";
            if (warnings.Count > 0) StatusText += " Let op: " + string.Join(" ", warnings);
        }
    }

    private async Task SyncOneAsync(SourceRow row, bool interactive)
    {
        IsBusy = true;
        try { await SyncCoreAsync(row, interactive); }
        finally
        {
            IsBusy = false;
            RefreshRows();
        }
    }

    private async Task<SyncOutcome> SyncCoreAsync(SourceRow row, bool interactive)
    {
        row.IsBusy = true;
        row.Status = "Bijwerken…";
        try
        {
            var outcome = await _library.SyncAsync(row.Source, _catalogService);
            row.Status = string.Join(" ", outcome.Catalog.Warnings ?? Array.Empty<string>());
            if (!interactive) return outcome;

            switch (outcome.Kind)
            {
                case SyncOutcomeKind.InitialImport:
                    OpenSelection(row.Source, SelectionPurpose.InitialImport);
                    break;
                case SyncOutcomeKind.ReviewNeeded:
                    OpenSelection(row.Source, SelectionPurpose.Review);
                    break;
                default:
                    StatusText = $"{row.Name} is up-to-date.";
                    break;
            }
            return outcome;
        }
        catch (Exception ex)
        {
            row.Status = "Mislukt: " + ex.Message;
            throw;
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    // ---- Selectie / review openen ----

    private async Task PrimaryActionAsync(SourceRow row)
    {
        if (row.NeedsInitialSelection)
        {
            if (_library.LoadCatalog(row.Source.Id) is null)
            {
                await SyncOneAsync(row, interactive: true); // opent zelf de selectie
                return;
            }
            OpenSelection(row.Source, SelectionPurpose.InitialImport);
        }
        else if (row.HasPending)
        {
            OpenSelection(row.Source, SelectionPurpose.Review);
        }
        else
        {
            OpenSelection(row.Source, SelectionPurpose.Browse);
        }
    }

    /// <summary>Opent het review-scherm voor de eerste bron met openstaande wijzigingen (gebruikt door de melding bovenin).</summary>
    public async Task ReviewFirstPendingAsync()
    {
        var row = _rows.FirstOrDefault(r => r.HasPending);
        if (row is not null) await PrimaryActionAsync(row);
    }

    public void OpenSelection(IptvSource source, SelectionPurpose purpose)
    {
        var catalog = _library.LoadCatalog(source.Id);
        if (catalog is null)
        {
            _dialogs.ShowError("Zenderlijst ontbreekt", "Werk de bron eerst bij.");
            return;
        }

        SelectionViewModel vm;
        switch (purpose)
        {
            case SelectionPurpose.Review:
            {
                var diff = _library.PendingDiff(source.Id);
                if (diff is null || !diff.NeedsReview)
                {
                    StatusText = "Er zijn geen wijzigingen om te bekijken.";
                    return;
                }
                vm = new SelectionViewModel(source, diff.New, Array.Empty<string>(), purpose, diff.Removed);
                break;
            }
            case SelectionPurpose.Browse:
                vm = new SelectionViewModel(source, catalog.Channels, _library.PersonalOf(source.Id).Select(p => p.Key), purpose);
                break;
            default:
                vm = new SelectionViewModel(source, catalog.Channels, Array.Empty<string>(), purpose);
                break;
        }

        vm.Confirmed += sel =>
        {
            _library.CommitSelection(source.Id, catalog.Channels, sel.CandidateKeys, sel.SelectedKeys, sel.RemoveKeys);
            RefreshRows();
            _nav.ShowChannels();
            _ = RefreshEpgQuietlyAsync();
        };
        vm.Cancelled += _ =>
        {
            RefreshRows();
            _nav.ShowSources();
        };

        _nav.ShowSelection(vm);
    }

    private async Task RefreshEpgQuietlyAsync()
    {
        try { await _refreshEpg(); }
        catch { /* EPG is een extra; fouten hier mogen het kijken niet storen */ }
    }

    private void RemoveSource(SourceRow row)
    {
        if (!_dialogs.Confirm("Bron verwijderen",
                $"'{row.Name}' verwijderen?\n\nAlle {row.ChannelsInList} zenders van deze bron verdwijnen ook uit je persoonlijke lijst."))
            return;
        _library.RemoveSource(row.Source.Id);
        RefreshRows();
    }

    public void ReportStatus(string text) => StatusText = text;

    /// <summary>"120 zenders, 40 films en 12 series gevonden." plus eventuele waarschuwingen.</summary>
    internal static string Describe(Catalog catalog)
    {
        var parts = new List<string>();
        foreach (var kind in Enum.GetValues<ContentKind>())
        {
            var n = catalog.Channels.Count(c => c.Kind == kind);
            if (n > 0) parts.Add($"{n} {(n == 1 ? KindText.Singular(kind) : KindText.Plural(kind))}");
        }
        var text = KindText.JoinNatural(parts) + " gevonden.";
        if (catalog.Warnings is { Count: > 0 }) text += " Let op: " + string.Join(" ", catalog.Warnings);
        return text;
    }

    private void ShowError(Exception ex)
    {
        StatusText = ex.Message;
        _dialogs.ShowError("Er ging iets mis", ex.Message);
    }
}
