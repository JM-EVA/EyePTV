using System.Windows.Input;

namespace IptvPlayer.Engine.ViewModels;

public sealed partial class ChannelsViewModel
{
    private readonly DownloadManager? _downloads;

    private void InitDownloads()
    {
        DownloadSeasonCommand = new RelayCommand(DownloadSeason, () => CanDownloadSeason);
        if (_downloads is not null) _downloads.Changed += RefreshDownloadState;
    }

    /// <summary>Downloaden is beschikbaar (de app heeft een downloadbeheer).</summary>
    public bool CanDownloadAnything => _downloads is not null;

    public ICommand DownloadSeasonCommand { get; private set; } = null!;

    /// <summary>Er is een geopende serie met afleveringen: dan kan een heel seizoen in één keer gedownload worden.</summary>
    public bool CanDownloadSeason => _downloads is not null && _openSeries is not null && _episodes.Count > 0;

    /// <summary>Kan dit item nog gedownload worden? (Niet als het al klaar is of in de wachtrij staat.)</summary>
    internal bool CanDownload(string key, string url) =>
        _downloads is not null && IsHttp(url) && _downloads.ForKey(key) is null or { Status: DownloadStatus.Failed };

    internal string? DownloadTextFor(string key) => _downloads?.ForKey(key) is { } d ? DownloadLabel(d) : null;

    private static string DownloadLabel(DownloadItem d) => d.Status switch
    {
        DownloadStatus.Queued => "In de wachtrij",
        DownloadStatus.Downloading => d.TotalBytes > 0 ? $"Downloaden… {(int)(100.0 * d.Bytes / d.TotalBytes)}%" : "Downloaden…",
        DownloadStatus.Paused => "Gepauzeerd",
        DownloadStatus.Done => "✓ Gedownload",
        _ => "Download mislukt"
    };

    // ---- Films ----

    internal void Download(ChannelRow row)
    {
        if (_downloads is null || row.Kind != ContentKind.Movie) return;
        Enqueue(new DownloadItem
        {
            Key = row.Model.Id, SourceId = row.Model.SourceId, Group = row.Model.Group, Title = row.Name, Info = row.Info,
            LogoUrl = row.LogoUrl, Url = row.Model.Url
        }, row.Name);
    }

    // ---- Afleveringen ----

    private string EpisodeDownloadKey(Episode e) => (_openSeries?.Model.SourceId ?? "") + "|ep:" + e.Id;

    internal bool CanDownloadEpisode(Episode e) => _openSeries is not null && CanDownload(EpisodeDownloadKey(e), e.Url);

    internal string? EpisodeDownloadText(Episode e) => _openSeries is null ? null : DownloadTextFor(EpisodeDownloadKey(e));

    internal void DownloadEpisode(EpisodeRow row)
    {
        if (_downloads is null || _openSeries is null) return;
        Enqueue(NewEpisodeItem(_openSeries, row.Episode), $"{_openSeries.Name} – {row.Title}");
    }

    private static DownloadItem NewEpisodeItem(ChannelRow series, Episode e) => new()
    {
        Key = series.Model.SourceId + "|ep:" + e.Id, SourceId = series.Model.SourceId, Group = series.Model.Group,
        IsEpisode = true, SeriesId = series.Model.Id, SeriesTitle = series.Name, EpisodeId = e.Id,
        Season = e.Season, Number = e.Number, Title = e.Title, Info = e.Plot ?? series.Info,
        LogoUrl = e.LogoUrl ?? series.LogoUrl, Url = e.Url
    };

    /// <summary>Download alle afleveringen van het gekozen seizoen die nog niet gedownload zijn.</summary>
    public void DownloadSeason()
    {
        if (_downloads is null || _openSeries is null || _selectedSeason is null) return;
        var series = _openSeries;
        var todo = _openEpisodes.Where(e => e.Season == _selectedSeason.Number && CanDownload(EpisodeDownloadKey(e), e.Url)).ToList();
        if (todo.Count == 0)
        {
            StatusText = "Alle afleveringen van dit seizoen worden al gedownload of staan al op je schijf.";
            return;
        }

        if (todo.Count > 1 && !_dialogs.Confirm("Seizoen downloaden",
                $"{todo.Count} afleveringen van '{series.Name}' (seizoen {_selectedSeason.Number}) downloaden?\n\nZe worden één voor één gedownload."))
            return;

        var failed = 0;
        foreach (var e in todo)
        {
            try { _downloads.Enqueue(NewEpisodeItem(series, e)); }
            catch (InvalidOperationException) { failed++; }
        }
        StatusText = failed == 0
            ? $"{todo.Count} afleveringen staan in de wachtrij. Bij Beheer → Downloads zie je de voortgang."
            : $"{todo.Count - failed} van {todo.Count} afleveringen staan in de wachtrij; de rest heeft geen bruikbare link.";
    }

    private void Enqueue(DownloadItem item, string name)
    {
        try
        {
            _downloads!.Enqueue(item);
            StatusText = $"'{name}' wordt gedownload. Bij Beheer → Downloads zie je de voortgang.";
        }
        catch (InvalidOperationException ex)
        {
            StatusText = ex.Message;
        }
    }

    private void RefreshDownloadState()
    {
        foreach (var r in _rows) r.DownloadChanged();
        foreach (var e in _episodes) e.DownloadChanged();
        Raise(nameof(CanDownloadSeason));
        ((RelayCommand)DownloadSeasonCommand).RaiseCanExecuteChanged();
    }

    // ---- Afspelen van een download (ook zonder internet) ----

    public void PlayDownload(DownloadItem d)
    {
        if (d.FilePath is not { } path || !File.Exists(path))
        {
            StatusText = "Het bestand van deze download is niet meer te vinden.";
            return;
        }

        var local = new Uri(path).AbsoluteUri;
        var prefix = d.SourceId + "|";

        if (!d.IsEpisode)
        {
            var movie = _library.Personal.FirstOrDefault(p => p.Id == d.Key) ?? new PersonalChannel
            {
                SourceId = d.SourceId, Key = d.Key.StartsWith(prefix, StringComparison.Ordinal) ? d.Key[prefix.Length..] : d.Key,
                Name = d.Title, Group = d.Group, Url = local, LogoUrl = d.LogoUrl, Kind = ContentKind.Movie, Info = d.Info
            };
            SelectKind(ContentKind.Movie);
            Play(RowFor(movie));
            return;
        }

        var seriesModel = _library.Personal.FirstOrDefault(p => p.Id == d.SeriesId) ?? new PersonalChannel
        {
            SourceId = d.SourceId, Key = d.SeriesId is { } sid && sid.StartsWith(prefix, StringComparison.Ordinal) ? sid[prefix.Length..] : d.EpisodeId ?? d.Key,
            Name = d.SeriesTitle ?? d.Title, Group = d.Group, Url = local, LogoUrl = d.LogoUrl, Kind = ContentKind.Series, Info = d.Info
        };
        var episode = new Episode(d.EpisodeId ?? d.Key, d.Season, d.Number, d.Title, local, d.Info, null, d.LogoUrl);
        SelectKind(ContentKind.Series);
        StartEpisode(RowFor(seriesModel), new[] { episode }, episode);
    }
}
