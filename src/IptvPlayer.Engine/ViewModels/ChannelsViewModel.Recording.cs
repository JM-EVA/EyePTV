using System.Windows.Input;

namespace IptvPlayer.Engine.ViewModels;

/// <summary>Opnemen vanuit de speler en de programmagids, en het afspelen van opnames en "verder kijken" vanaf het beginscherm.</summary>
public sealed partial class ChannelsViewModel
{
    /// <summary>Bron-id van opnames die we afspelen (ze staan niet in je lijst).</summary>
    public const string RecordingsSourceId = "recordings";

    private readonly RecordingManager? _recordings;

    public ICommand RecordCommand { get; private set; } = null!;

    private void InitRecording()
    {
        RecordCommand = new RelayCommand(o => Record(o as string));
        if (_recordings is not null) _recordings.Changed += OnRecordingsChanged;
    }

    // ---- Toestand voor het scherm ----

    /// <summary>Er staat een live zender in beeld die kan worden opgenomen.</summary>
    public bool CanRecord => _recordings is not null && Current is { IsLive: true } c && IsHttp(c.Model.Url);

    private Recording? CurrentRecording => Current is { IsLive: true } c ? _recordings?.ActiveFor(c.Model) : null;

    public bool IsRecordingCurrent => CurrentRecording?.Status == RecordingStatus.Recording;
    public bool HasPlannedRecording => CurrentRecording?.Status == RecordingStatus.Scheduled;
    public bool ShowRecordButton => CanRecord && !IsRecordingCurrent;

    /// <summary>"● Neemt op · 12:30 · 340 MB (tot 21:35)"</summary>
    public string? RecordingText
    {
        get
        {
            if (CurrentRecording is not { Status: RecordingStatus.Recording } r) return null;
            var elapsed = r.ActualStartUtc is { } s ? (long)(_utcNow() - s).TotalMilliseconds : 0;
            var until = r.EndUtc is { } e ? $" · tot {e.ToLocalTime():HH:mm}" : " · tot je stopt";
            return $"● Neemt op · {TimeText.Format(elapsed)} · {TimeText.Bytes(r.Bytes)}{until}";
        }
    }

    private void RaiseRecordingState()
    {
        Raise(nameof(CanRecord));
        Raise(nameof(IsRecordingCurrent));
        Raise(nameof(HasPlannedRecording));
        Raise(nameof(ShowRecordButton));
        Raise(nameof(RecordingText));
    }

    private void OnRecordingsChanged()
    {
        RaiseRecordingState();
        foreach (var row in _schedule)
            row.IsRecorded = Current is { IsLive: true } c && _recordings?.ForProgramme(c.Model, row.Programme) is not null;
    }

    private static bool IsHttp(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    private bool CanRecordProgramme(PersonalChannel channel, Programme p, DateTime now) =>
        _recordings is not null && IsHttp(channel.Url) && p.StopUtc + _recordings.After > now;

    // ---- Opnemen ----

    /// <summary>
    /// Opnemen van de zender die in beeld is. Opties: "stop", "programme" (het programma dat nu loopt, met marge),
    /// "30" / "60" / "120" (minuten) of leeg/"until-stopped" (tot je zelf stopt).
    /// </summary>
    public void Record(string? option)
    {
        if (_recordings is null || Current is not { IsLive: true } cur) return;
        var ch = cur.Model;
        var now = _utcNow();

        try
        {
            switch (option)
            {
                case "stop":
                    if (CurrentRecording is { } running)
                    {
                        _recordings.Stop(running.Id);
                        StatusText = $"Opname gestopt: {running.Title}";
                    }
                    break;

                case "programme":
                {
                    var (p, _) = _epg.Data.NowNext(ch, now);
                    if (p is null)
                    {
                        StatusText = "Geen programma-informatie voor deze zender. Kies 'tot ik stop' of een eindtijd.";
                        return;
                    }
                    var r = _recordings.RecordProgramme(ch, p);
                    StatusText = $"Opname van '{p.Title}' loopt tot {r.EndUtc!.Value.ToLocalTime():HH:mm} (met marge)";
                    break;
                }

                case "30" or "60" or "120":
                {
                    var end = now.AddMinutes(int.Parse(option));
                    _recordings.RecordNow(ch, end);
                    StatusText = $"Opname gestart tot {end.ToLocalTime():HH:mm}";
                    break;
                }

                default:
                    _recordings.RecordNow(ch);
                    StatusText = "Opname gestart; die loopt tot je stopt.";
                    break;
            }
        }
        catch (InvalidOperationException ex)
        {
            StatusText = ex.Message;
        }
        RaiseRecordingState();
    }

    /// <summary>Een programma uit de gids inplannen, of de opname weer annuleren als het al gepland staat.</summary>
    internal void RecordProgramme(ProgrammeRow row)
    {
        if (_recordings is null || Current is not { IsLive: true } cur) return;
        ToggleProgrammeRecording(cur.Model, row.Programme);
        foreach (var r in _schedule) r.IsRecorded = _recordings.ForProgramme(cur.Model, r.Programme) is not null;
    }

    /// <summary>Plant een programma in (of annuleert het) en meldt wat er gebeurde. Wordt ook door de Gids gebruikt.</summary>
    public void ToggleProgrammeRecording(PersonalChannel channel, Programme p)
    {
        if (_recordings is null) return;
        try
        {
            if (_recordings.ForProgramme(channel, p) is { } existing)
            {
                _recordings.Stop(existing.Id);
                StatusText = $"Opname geannuleerd: {p.Title}";
            }
            else
            {
                var r = _recordings.RecordProgramme(channel, p);
                StatusText = $"Opname ingepland: {p.Title} ({r.StartUtc.ToLocalTime():HH:mm}–{r.EndUtc!.Value.ToLocalTime():HH:mm}, met marge)";
            }
        }
        catch (InvalidOperationException ex)
        {
            StatusText = ex.Message;
        }
        RaiseRecordingState();
    }

    /// <summary>Werkt "bekeken" en "verder bij" bij in de lijsten (na een wijziging vanaf het beginscherm).</summary>
    public void ReloadWatchState() => RefreshWatchState();

    /// <summary>Toont een melding in de statusregel (voor andere schermen die iets in de speler starten).</summary>
    public void ReportMessage(string message) => StatusText = message;

    // ---- Afspelen vanaf het beginscherm, de gids en de opnames ----

    private ChannelRow RowFor(PersonalChannel model) =>
        _rows.FirstOrDefault(r => r.Model.Id == model.Id) ?? new ChannelRow(this, model);

    /// <summary>Speelt een zender live af (ook als hij door een filter niet in de lijst staat).</summary>
    public void WatchLive(PersonalChannel channel)
    {
        SelectKind(ContentKind.Live);
        Play(RowFor(channel));
    }

    /// <summary>Speelt een programma van een zender terug (vanaf het begin).</summary>
    public void ReplayProgramme(PersonalChannel channel, Programme programme)
    {
        WatchLive(channel);
        PlayProgramme(new ProgrammeRow(programme, isNow: false));
    }

    /// <summary>Speelt een film verder waar je was.</summary>
    public void ResumeMovie(PersonalChannel movie)
    {
        SelectKind(ContentKind.Movie);
        Play(RowFor(movie));
    }

    /// <summary>Opent een serie (de lijst met afleveringen) vanaf het beginscherm.</summary>
    public void ShowSeries(PersonalChannel series)
    {
        SelectKind(ContentKind.Series);
        Play(RowFor(series));
    }

    /// <summary>Speelt een aflevering verder waar je was.</summary>
    public async Task ResumeEpisodeAsync(PersonalChannel series, string episodeId)
    {
        SelectKind(ContentKind.Series);
        var row = RowFor(series);
        await OpenSeriesAsync(row);
        var episode = _openEpisodes.FirstOrDefault(e => e.Id == episodeId);
        if (episode is not null) StartEpisode(row, _openEpisodes, episode);
    }

    /// <summary>Speelt een eigen opname af (met verder kijken, net als een film).</summary>
    public void PlayRecording(Recording recording)
    {
        if (recording.FilePath is not { } path || !File.Exists(path))
        {
            StatusText = "Het bestand van deze opname is niet meer te vinden.";
            return;
        }

        var model = new PersonalChannel
        {
            SourceId = RecordingsSourceId,
            Key = recording.Id,
            Name = recording.Title,
            Group = "Opnames",
            Url = new Uri(path).AbsoluteUri,
            LogoUrl = recording.LogoUrl,
            Kind = ContentKind.Movie,
            Info = $"{recording.ChannelName} · {recording.StartUtc.ToLocalTime():dd-MM-yyyy HH:mm}"
        };
        Play(new ChannelRow(this, model));
    }
}
