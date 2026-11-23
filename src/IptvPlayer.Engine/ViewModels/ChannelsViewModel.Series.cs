namespace IptvPlayer.Engine.ViewModels;

public sealed partial class ChannelsViewModel
{
    private readonly Dictionary<string, IReadOnlyList<Episode>> _episodeCache = new();

    private ChannelRow? _openSeries;
    private IReadOnlyList<Episode> _openEpisodes = Array.Empty<Episode>();
    private IReadOnlyList<SeasonOption> _seasons = Array.Empty<SeasonOption>();
    private SeasonOption? _selectedSeason;
    private IReadOnlyList<EpisodeRow> _episodes = Array.Empty<EpisodeRow>();
    private bool _isLoadingSeries;
    private string? _seriesError;
    private int _seriesRequest;

    // Wat er speelt (kan een andere serie zijn dan de geopende).
    private IReadOnlyList<Episode> _playingEpisodes = Array.Empty<Episode>();
    private Episode? _playingEpisode;

    /// <summary>De serie waarvan de afleveringen getoond worden (of null).</summary>
    public ChannelRow? OpenSeries => _openSeries;

    public bool IsSeriesOpen => _openSeries is not null;

    /// <summary>De gewone lijst is zichtbaar (er is geen serie geopend).</summary>
    public bool ShowList => _openSeries is null;

    public string? SeriesTitle => _openSeries?.Name;
    public string? SeriesInfo => _openSeries?.Info;

    public IReadOnlyList<SeasonOption> Seasons { get => _seasons; private set => Set(ref _seasons, value); }

    public SeasonOption? SelectedSeason
    {
        get => _selectedSeason;
        set
        {
            if (value is null) return; // zie SelectedGroup
            if (Set(ref _selectedSeason, value)) BuildEpisodeRows();
        }
    }

    public IReadOnlyList<EpisodeRow> Episodes { get => _episodes; private set => Set(ref _episodes, value); }

    public bool IsLoadingSeries { get => _isLoadingSeries; private set => Set(ref _isLoadingSeries, value); }

    public string? SeriesError
    {
        get => _seriesError;
        private set
        {
            if (Set(ref _seriesError, value)) Raise(nameof(HasSeriesError));
        }
    }

    public bool HasSeriesError => _seriesError is not null;

    private string EpisodeKey(Episode e) => (_openSeries?.Model.SourceId ?? Current?.Model.SourceId) + "|ep:" + e.Id;

    /// <summary>Opent een serie en haalt de afleveringen op (de eerste keer van de server, daarna uit het geheugen).</summary>
    public async Task OpenSeriesAsync(ChannelRow row)
    {
        var request = ++_seriesRequest;
        SetOpenSeries(row);
        SeriesError = null;
        Seasons = Array.Empty<SeasonOption>();
        Episodes = Array.Empty<EpisodeRow>();
        _openEpisodes = Array.Empty<Episode>();

        if (_episodeCache.TryGetValue(row.Model.Id, out var cached))
        {
            ShowEpisodes(cached);
            return;
        }

        var source = _library.Sources.FirstOrDefault(s => s.Id == row.Model.SourceId);
        if (source is null || source.Kind != SourceKind.Xtream || _xtream is null || row.Model.RemoteId is null)
        {
            SeriesError = "Afleveringen kunnen alleen worden opgehaald bij een Xtream Codes-bron.";
            return;
        }

        IsLoadingSeries = true;
        try
        {
            var episodes = await _xtream.GetEpisodesAsync(source, row.Model.RemoteId);
            if (request != _seriesRequest) return; // intussen is er een andere serie geopend (of deze gesloten)
            _episodeCache[row.Model.Id] = episodes;
            ShowEpisodes(episodes);
        }
        catch (Exception ex)
        {
            if (request == _seriesRequest) SeriesError = $"De afleveringen konden niet worden opgehaald: {ex.Message}";
        }
        finally
        {
            if (request == _seriesRequest) IsLoadingSeries = false;
        }
    }

    public void CloseSeries()
    {
        if (_openSeries is null) return;
        _seriesRequest++;
        IsLoadingSeries = false;
        SetOpenSeries(null);
        Seasons = Array.Empty<SeasonOption>();
        Episodes = Array.Empty<EpisodeRow>();
        _openEpisodes = Array.Empty<Episode>();
        _selectedSeason = null;
        SeriesError = null;
    }

    private void SetOpenSeries(ChannelRow? row)
    {
        _openSeries = row;
        Raise(nameof(OpenSeries));
        Raise(nameof(IsSeriesOpen));
        Raise(nameof(ShowList));
        Raise(nameof(SeriesTitle));
        Raise(nameof(SeriesInfo));
    }

    private void ShowEpisodes(IReadOnlyList<Episode> episodes)
    {
        _openEpisodes = episodes.OrderBy(e => e.Season).ThenBy(e => e.Number).ToList();
        if (_openEpisodes.Count == 0)
        {
            SeriesError = "Er zijn geen afleveringen gevonden voor deze serie.";
            return;
        }

        var seasons = _openEpisodes.Select(e => e.Season).Distinct().OrderBy(n => n)
            .Select(n => new SeasonOption(n, "Seizoen " + n)).ToList();
        Seasons = seasons;

        // Begin bij het seizoen waar je gebleven bent: het eerste met een aflevering die je nog niet helemaal hebt gezien.
        var start = _openEpisodes.FirstOrDefault(e => !_watch.IsFinished(EpisodeKey(e)))?.Season ?? seasons[0].Number;
        _selectedSeason = seasons.First(s => s.Number == start);
        Raise(nameof(SelectedSeason));
        BuildEpisodeRows();
    }

    private void BuildEpisodeRows()
    {
        if (_selectedSeason is null) return;
        Episodes = _openEpisodes.Where(e => e.Season == _selectedSeason.Number)
            .Select(e => new EpisodeRow(e, PlayEpisode, this)).ToList();
        RefreshWatchState();
    }

    private void PlayEpisode(EpisodeRow row)
    {
        if (_openSeries is null) return;
        StartEpisode(_openSeries, _openEpisodes, row.Episode);
    }

    private void StartEpisode(ChannelRow series, IReadOnlyList<Episode> all, Episode episode)
    {
        if (!CheckPlayable(episode.Url)) return;
        FlushProgress();
        SetCurrent(series);
        ResetTransport();

        SetMode(PlayMode.Episode);
        _playingEpisodes = all;
        _playingEpisode = episode;
        _watchKey = series.Model.SourceId + "|ep:" + episode.Id;
        _vodTitle = $"{series.Name} – S{episode.Season} A{episode.Number}: {episode.Title}";
        _vodDescription = episode.Plot ?? series.Info;

        var resume = _watch.GetResumePoint(_watchKey);
        _lastSavedMs = resume;
        _player.Play(_downloads?.LocalUrlFor(_watchKey) ?? episode.Url, _vodTitle, resume);
        StatusText = resume > 0 ? $"{_vodTitle} – verder bij {TimeText.Format(resume)}" : _vodTitle;

        UpdateNowNext();
        RefreshWatchState();
    }
}
