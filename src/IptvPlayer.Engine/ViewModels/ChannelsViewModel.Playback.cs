namespace IptvPlayer.Engine.ViewModels;

public sealed partial class ChannelsViewModel
{
    private enum PlayMode { None, Live, Movie, Episode, Replay }

    private const long SaveEveryMs = 15_000;

    private PlayMode _mode = PlayMode.None;
    private string? _watchKey;
    private string? _vodTitle;
    private string? _vodDescription;
    private bool _positionSeen;
    private bool _seeking;
    private long _lastSavedMs;
    private long _positionMs;
    private long _replayOffsetMs; // waar in het programma de huidige terugkijk-stream begon
    private long _lengthMs;
    private double _positionFraction;
    private string _positionText = "";

    // ---- Toestand voor de schermen ----

    /// <summary>Er speelt iets (zender, film, aflevering of terugkijken).</summary>
    public bool IsPlaying => _mode != PlayMode.None;

    /// <summary>Er speelt een live zender.</summary>
    public bool IsLive => _mode == PlayMode.Live;

    /// <summary>Er wordt een programma teruggekeken.</summary>
    public bool IsReplay => _mode == PlayMode.Replay;

    /// <summary>Film of aflevering (met voortgang en verder kijken).</summary>
    public bool IsVod => _mode is PlayMode.Movie or PlayMode.Episode;

    /// <summary>Pauze- en spoelknoppen tonen: alles behalve live-tv.</summary>
    public bool ShowTransport => _mode is PlayMode.Movie or PlayMode.Episode or PlayMode.Replay;

    /// <summary>
    /// Bij terugkijken kan altijd worden gespoeld: de stream heeft vaak geen lengte, dus starten we hem opnieuw op het
    /// gekozen punt (de tijdlijn is de lengte van het programma). Bij films en afleveringen spoelt de speler zelf.
    /// </summary>
    public bool CanSeek => _mode == PlayMode.Replay
        ? _replayProgramme is not null
        : ShowTransport && _player.IsSeekable && _lengthMs > 0;
    // De speler meldt een pauze pas na even; daarom onthouden we zelf wat de gebruiker net deed (de knop wisselt direct).
    private bool? _pausedByUser;

    public bool IsPaused => _pausedByUser ?? _player.IsPaused;

    /// <summary>0..1 voor de spoelbalk.</summary>
    public double PositionFraction { get => _positionFraction; private set => Set(ref _positionFraction, value); }

    /// <summary>"12:05 / 1:42:10" (of alleen de tijd als de lengte onbekend is).</summary>
    public string PositionText { get => _positionText; private set => Set(ref _positionText, value); }

    private void SetMode(PlayMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        if (mode != PlayMode.Replay) _replayProgramme = null;
        Raise(nameof(IsLive));
        Raise(nameof(IsReplay));
        Raise(nameof(IsVod));
        Raise(nameof(ShowTracks));
        Raise(nameof(ShowTrackRow));
        Raise(nameof(ShowTransport));
        Raise(nameof(CanSeek));
        Raise(nameof(IsPaused));
        RaiseAlternatives();
        MarkReplaying();
    }

    // ---- Afspelen ----

    /// <summary>Speelt een zender of film af; bij een serie opent het de lijst met afleveringen.</summary>
    public void Play(ChannelRow row)
    {
        if (row.IsSeries)
        {
            _ = OpenSeriesAsync(row);
            return;
        }

        if (!CheckPlayable(row.Model.Url)) return;

        FlushProgress();
        _playingEpisode = null;
        SetCurrent(row);
        ResetTransport();

        if (row.IsLive)
        {
            SetMode(PlayMode.Live);
            ResetFailover(row.Model);
            OnLivePlayStarted();
            if (!_reconnecting && !_switching)
            {
                _alternativeUrl = null;
                StartAlternativesLookup(row.Model); // andere streams van deze zender, op de achtergrond gezocht
            }
            _watchKey = null;
            StatusText = $"Bezig met laden: {row.Name}";
            _player.Play(_alternativeUrl ?? row.Model.Url, row.Name);
            StatusText = row.IsMissing
                ? $"{row.Name} (let op: deze zender wordt niet meer aangeboden door de bron)"
                : row.Name;
        }
        else
        {
            SetMode(PlayMode.Movie);
            _watchKey = row.Model.Id;
            _vodTitle = row.Name;
            _vodDescription = row.Info;
            var resume = _watch.GetResumePoint(_watchKey);
            _lastSavedMs = resume;
            _player.Play(_downloads?.LocalUrlFor(_watchKey) ?? row.Model.Url, row.Name, resume); // gedownload? dan van de schijf
            StatusText = (resume > 0 ? $"{row.Name} – verder bij {TimeText.Format(resume)}" : row.Name) +
                         (row.IsMissing ? " (let op: deze film wordt niet meer aangeboden door de bron)" : "");
        }

        UpdateNowNext();
        RefreshWatchState();
    }

    private WatchStore.EpisodeMeta? CurrentEpisodeMeta() =>
        _mode == PlayMode.Episode && _playingEpisode is { } e && Current is { } c
            ? new WatchStore.EpisodeMeta(c.Model.Id, e.Id, e.Season, e.Number, e.Title)
            : null;

    /// <summary>Alleen streamingprotocollen openen (zie <see cref="PlayableUrl"/>); anders een melding en niets starten.</summary>
    private bool CheckPlayable(string url)
    {
        if (PlayableUrl.IsAllowed(url) || _recordings?.OwnsUrl(url) == true || _downloads?.OwnsUrl(url) == true) return true;
        StatusText = "Deze link gebruikt een protocol dat de app om veiligheidsredenen niet afspeelt.";
        return false;
    }

    public void Stop()
    {
        FlushProgress();
        _player.Stop();
        if (Current is not null) Current.IsPlaying = false;
        Current = null;
        SetMode(PlayMode.None);
        ResetReconnect();
        _alternativeUrl = null;
        _watchKey = null;
        _playingEpisode = null;
        _playingEpisodes = Array.Empty<Episode>();
        ResetTransport();
        StatusText = "Gestopt.";
        UpdateNowNext();
        RefreshWatchState();
    }

    private void SetCurrent(ChannelRow row)
    {
        if (Current is not null) Current.IsPlaying = false;
        if (Current?.Model.Id != row.Model.Id) _selectedDay = null; // nieuwe zender: de gids begint weer bij vandaag
        Current = row;
        row.IsPlaying = true;
    }

    private void Step(int delta)
    {
        // Bij een serie: vorige/volgende aflevering.
        if (_mode == PlayMode.Episode && _playingEpisode is not null)
        {
            var i = _playingEpisodes.ToList().FindIndex(e => e.Id == _playingEpisode.Id);
            var n = i + delta;
            if (i >= 0 && n >= 0 && n < _playingEpisodes.Count && Current is not null)
                StartEpisode(Current, _playingEpisodes, _playingEpisodes[n]);
            return;
        }

        // Anders: door de lijst heen (series zelf zijn niet afspeelbaar).
        var playable = _rows.Where(r => !r.IsSeries).ToList();
        if (playable.Count == 0) return;
        var idx = Current is null ? -1 : playable.FindIndex(r => r.Model.Id == Current.Model.Id);
        var next = idx < 0 ? (delta > 0 ? 0 : playable.Count - 1) : (idx + delta + playable.Count) % playable.Count;
        Play(playable[next]);
    }

    private void BackToLive()
    {
        if (_mode == PlayMode.Replay && Current is not null) Play(Current);
    }

    // ---- Pauze en spoelen ----

    public void TogglePause()
    {
        if (!ShowTransport) return;
        var wasPaused = IsPaused;
        _player.TogglePause();
        _pausedByUser = !wasPaused;
        Raise(nameof(IsPaused));
    }

    public void SeekRelative(long deltaMs)
    {
        if (!CanSeek) return;
        var from = _mode == PlayMode.Replay ? _positionMs : _player.TimeMs;
        SeekCore(Math.Clamp(from + deltaMs, 0, SeekLimitMs()));
    }

    /// <summary>Het laatste punt waar naartoe gespoeld mag worden (iets voor het einde; bij terugkijken ook niet voorbij "nu").</summary>
    private long SeekLimitMs()
    {
        var length = _lengthMs;
        if (_mode == PlayMode.Replay && _replayProgramme is { } p)
            length = Math.Min(length, (long)(_utcNow() - p.StartUtc).TotalMilliseconds);
        return Math.Max(0, length - 2000);
    }

    /// <summary>De gebruiker pakt de spoelbalk vast: stop met bijwerken tot hij loslaat.</summary>
    public void BeginSeek() => _seeking = true;

    /// <summary>De gebruiker laat de spoelbalk los op dit punt (0..1).</summary>
    public void EndSeek(double fraction)
    {
        _seeking = false;
        if (!CanSeek) return;
        SeekCore(Math.Clamp((long)(Math.Clamp(fraction, 0, 1) * _lengthMs), 0, SeekLimitMs()));
    }

    private void SeekCore(long ms)
    {
        if (_mode == PlayMode.Replay)
        {
            if (!RestartReplayAt(ms)) return;
        }
        else
        {
            _player.SeekTo(ms);
        }
        _positionMs = ms;
        UpdateTransport();
    }

    /// <summary>Terugkijken op een ander punt: de provider levert een nieuwe stream die op dat tijdstip begint.</summary>
    private bool RestartReplayAt(long offsetMs)
    {
        if (_replayProgramme is not { } p || Current is null) return false;
        var now = _utcNow();
        var source = _library.Sources.FirstOrDefault(s => s.Id == Current.Model.SourceId);
        var url = CatchupUrl.Build(source, Current.Model, p.StartUtc.AddMilliseconds(offsetMs), p.StopUtc, now);
        if (url is null)
        {
            StatusText = "Op dit punt terugkijken lukt niet.";
            return false;
        }
        if (!CheckPlayable(url)) return false;
        _replayOffsetMs = offsetMs;
        _pausedByUser = null; // een nieuwe stream speelt meteen
        _player.Play(url, $"{Current.Name} – {p.Title}");
        return true;
    }

    // ---- Voortgang van de speler ----

    private void ResetTransport()
    {
        SetVideoStarted(false); // nieuw item: het laadscherm tot de speler het eerste beeld meldt
        ResetTracks();
        _positionMs = 0;
        _lengthMs = 0;
        _positionSeen = false;
        _seeking = false;
        _lastSavedMs = 0;
        _pausedByUser = null;
        PositionFraction = 0;
        PositionText = "";
        Progress = 0;
        Raise(nameof(CanSeek));
        Raise(nameof(IsPaused));
    }

    private void OnPositionChanged()
    {
        if (_mode == PlayMode.None) return;
        SetVideoStarted(true); // de speler tikt: er is beeld
        if (_mode == PlayMode.Live) LiveSignal();
        if (_mode == PlayMode.Live || _seeking) return;

        if (_mode == PlayMode.Replay && _replayProgramme is { } rp)
        {
            // De tijdlijn is het hele programma; de speler telt vanaf het punt waar de stream opnieuw begon.
            _positionMs = _replayOffsetMs + _player.TimeMs;
            _lengthMs = (long)(rp.StopUtc - rp.StartUtc).TotalMilliseconds;
        }
        else
        {
            _positionMs = _player.TimeMs;
            _lengthMs = _player.LengthMs;
        }
        if (_lengthMs > 0 && _positionMs > 0) _positionSeen = true;
        UpdateTransport();

        if (IsVod && _positionSeen && Math.Abs(_positionMs - _lastSavedMs) >= SaveEveryMs)
            SaveProgress();
    }

    private void UpdateTransport()
    {
        PositionFraction = _lengthMs > 0 ? Math.Clamp((double)_positionMs / _lengthMs, 0, 1) : 0;
        PositionText = _lengthMs > 0
            ? $"{TimeText.Format(_positionMs)} / {TimeText.Format(_lengthMs)}"
            : TimeText.Format(_positionMs);

        if (_mode == PlayMode.Replay && _replayProgramme is { } p)
            Progress = Math.Clamp(_positionMs / Math.Max(1000.0, (p.StopUtc - p.StartUtc).TotalMilliseconds), 0, 1);
        else
            Progress = PositionFraction;

        Raise(nameof(CanSeek));
        Raise(nameof(IsPaused));
    }

    private void SaveProgress()
    {
        if (_watchKey is null || _lengthMs <= 0) return;
        _watch.Save(_watchKey, _positionMs, _lengthMs, _utcNow(), CurrentEpisodeMeta());
        _lastSavedMs = _positionMs;
    }

    /// <summary>Bewaart de stand van de film/aflevering die nu speelt (voor "verder kijken").</summary>
    private void FlushProgress()
    {
        if (IsVod && _positionSeen) SaveProgress();
        _positionSeen = false;
    }

    private void OnPlaybackEnded()
    {
        switch (_mode)
        {
            case PlayMode.Movie:
            case PlayMode.Episode:
                if (_watchKey is not null && _lengthMs > 0)
                    _watch.Save(_watchKey, _lengthMs, _lengthMs, _utcNow(), CurrentEpisodeMeta()); // uitgekeken: telt als bekeken
                _positionSeen = false;
                RefreshWatchState();

                if (_mode == PlayMode.Episode && _playingEpisode is not null && Current is not null)
                {
                    var i = _playingEpisodes.ToList().FindIndex(e => e.Id == _playingEpisode.Id);
                    if (i >= 0 && i + 1 < _playingEpisodes.Count)
                    {
                        StartEpisode(Current, _playingEpisodes, _playingEpisodes[i + 1]); // automatisch de volgende aflevering
                        return;
                    }
                }
                StatusText = $"{_vodTitle} is afgelopen.";
                break;

            case PlayMode.Replay:
                StatusText = "Het programma is afgelopen. Kies 'Terug naar live' om verder te kijken.";
                break;

            case PlayMode.Live:
                if (_reconnectGaveUp) break; // de melding dat opnieuw verbinden niet lukt, blijft staan
                if (Current is not null) ScheduleReconnect("De uitzending is gestopt of de verbinding is verbroken", immediate: true);
                else StatusText = "De uitzending is gestopt of de verbinding is verbroken.";
                break;
        }
    }

    // ---- Wat er nu speelt ----

    private void UpdateNowNext()
    {
        var now = _utcNow();

        foreach (var r in _rows)
            r.NowTitle = r.IsLive ? _epg.Data.NowNext(r.Model, now).Now?.Title : null;

        if (Current is null)
        {
            NowTitle = NowTime = NowDescription = NextText = null;
            Progress = 0;
            RebuildSchedule(now);
            return;
        }

        switch (_mode)
        {
            case PlayMode.Movie:
            case PlayMode.Episode:
                NowTitle = _vodTitle;
                NowTime = null;
                NowDescription = _vodDescription;
                NextText = null;
                break;

            case PlayMode.Replay:
                break; // blijft bij het programma dat wordt teruggekeken

            default:
            {
                var (cur, next) = _epg.Data.NowNext(Current.Model, now);
                NowTitle = cur?.Title;
                NowTime = cur is null ? null : $"{cur.StartUtc.ToLocalTime():HH:mm} – {cur.StopUtc.ToLocalTime():HH:mm}";
                NowDescription = cur?.Description;
                NextText = next is null ? null : $"Hierna ({next.StartUtc.ToLocalTime():HH:mm}): {next.Title}";
                Progress = cur is null ? 0 : Math.Clamp((now - cur.StartUtc).TotalSeconds / Math.Max(1, (cur.StopUtc - cur.StartUtc).TotalSeconds), 0, 1);
                break;
            }
        }

        RebuildSchedule(now);
    }

    /// <summary>Werkt "bekeken" en "verder bij" bij in de lijst met films en afleveringen.</summary>
    private void RefreshWatchState()
    {
        foreach (var r in _rows)
            r.IsWatched = r.Kind == ContentKind.Movie && _watch.IsFinished(r.Model.Id);

        foreach (var e in _episodes)
        {
            var key = EpisodeKey(e.Episode);
            var resume = _watch.GetResumePoint(key);
            e.IsWatched = _watch.IsFinished(key);
            e.ResumeText = resume > 0 ? $"Verder bij {TimeText.Format(resume)}" : null;
            e.IsPlaying = _mode == PlayMode.Episode && _playingEpisode?.Id == e.Episode.Id &&
                          Current?.Model.Id == _openSeries?.Model.Id;
        }
    }
}
