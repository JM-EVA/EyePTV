namespace IptvPlayer.Engine.ViewModels;

public sealed partial class ChannelsViewModel
{
    private static readonly string[] DayNames = { "zo", "ma", "di", "wo", "do", "vr", "za" };
    private static readonly string[] MonthNames = { "jan", "feb", "mrt", "apr", "mei", "jun", "jul", "aug", "sep", "okt", "nov", "dec" };

    private IReadOnlyList<ProgrammeRow> _schedule = Array.Empty<ProgrammeRow>();
    private IReadOnlyList<ScheduleDay> _scheduleDays = Array.Empty<ScheduleDay>();
    private DateTime? _selectedDay;
    private Programme? _replayProgramme;

    /// <summary>Het programma van de gekozen dag (standaard vandaag).</summary>
    public IReadOnlyList<ProgrammeRow> Schedule { get => _schedule; private set => Set(ref _schedule, value); }

    /// <summary>Dagen met programma's: bij een zender met terugkijken ook de dagen daarvoor.</summary>
    public IReadOnlyList<ScheduleDay> ScheduleDays
    {
        get => _scheduleDays;
        private set
        {
            if (Set(ref _scheduleDays, value)) Raise(nameof(HasScheduleDays));
        }
    }

    public bool HasScheduleDays => _scheduleDays.Count > 1;

    public ScheduleDay? SelectedScheduleDay
    {
        get => _scheduleDays.FirstOrDefault(d => d.Date == _selectedDay);
        set
        {
            if (value is null || value.Date == _selectedDay) return; // zie SelectedGroup
            _selectedDay = value.Date;
            Raise();
            RebuildSchedule(_utcNow());
        }
    }

    /// <summary>De zender die nu in beeld is heeft terugkijken.</summary>
    public bool CurrentHasCatchup => Current is { } c && c.HasCatchup;

    /// <summary>Er staat een live-zender in beeld die geen terugkijken heeft (dan tonen we dat in de gids).</summary>
    public bool ShowNoCatchupHint => Current is { IsLive: true } c && !c.HasCatchup;

    private static string DayTitle(DateTime day, DateTime today)
    {
        var diff = (day.Date - today.Date).Days;
        return diff switch
        {
            0 => "Vandaag",
            -1 => "Gisteren",
            1 => "Morgen",
            _ => $"{DayNames[(int)day.DayOfWeek]} {day.Day} {MonthNames[day.Month - 1]}"
        };
    }

    private void RebuildSchedule(DateTime now)
    {
        Raise(nameof(CurrentHasCatchup));
        Raise(nameof(ShowNoCatchupHint));

        if (Current is null || !Current.IsLive)
        {
            ScheduleDays = Array.Empty<ScheduleDay>();
            Schedule = Array.Empty<ProgrammeRow>();
            return;
        }

        var channel = Current.Model;
        var today = now.ToLocalTime().Date;
        var back = CatchupUrl.IsAvailable(channel) ? Math.Min(channel.CatchupDays, 14) : 0;
        var from = new[] { now.AddDays(-back), today.ToUniversalTime() }.Min();
        var programmes = _epg.Data.Schedule(channel, from, now.AddDays(3));

        bool IsCurrent(Programme p) => p.StartUtc <= now && p.StopUtc > now;
        // Het programma dat nu loopt hoort bij vandaag, ook als het gisteravond begon.
        DateTime DayOf(Programme p) => IsCurrent(p) ? today : p.StartUtc.ToLocalTime().Date;

        var dates = programmes.Select(DayOf).Distinct().OrderBy(d => d).ToList();
        if (dates.Count == 0)
        {
            ScheduleDays = Array.Empty<ScheduleDay>();
            Schedule = Array.Empty<ProgrammeRow>();
            return;
        }

        var days = dates.Select(d => new ScheduleDay(d, DayTitle(d, today))).ToList();
        if (!days.SequenceEqual(_scheduleDays)) ScheduleDays = days;

        var selected = _selectedDay is { } s && dates.Contains(s) ? s : dates.Contains(today) ? today : dates[0];
        if (_selectedDay != selected)
        {
            _selectedDay = selected;
            Raise(nameof(SelectedScheduleDay));
        }

        var rows = programmes.Where(p => DayOf(p) == selected)
            .Select(p => new ProgrammeRow(p, IsCurrent(p), p.StopUtc <= now, CatchupUrl.CanReplay(channel, p, now), PlayProgramme,
                canRecord: CanRecordProgramme(channel, p, now),
                isRecorded: _recordings?.ForProgramme(channel, p) is not null,
                record: RecordProgramme))
            .ToList();

        // Geen nieuwe lijst als er niets veranderd is: anders springt de lijst op het scherm bij elke controle terug.
        var same = rows.Count == _schedule.Count;
        for (var i = 0; same && i < rows.Count; i++)
            same = rows[i].Programme == _schedule[i].Programme && rows[i].IsNow == _schedule[i].IsNow &&
                   rows[i].IsPast == _schedule[i].IsPast && rows[i].CanReplay == _schedule[i].CanReplay &&
                   rows[i].CanRecord == _schedule[i].CanRecord && rows[i].IsRecorded == _schedule[i].IsRecorded;
        if (!same) Schedule = rows;

        MarkReplaying();
    }

    private void MarkReplaying()
    {
        foreach (var r in _schedule)
            r.IsReplaying = _mode == PlayMode.Replay && _replayProgramme is not null && r.Programme == _replayProgramme;
    }

    /// <summary>Speelt een programma van de gids terug (vanaf het begin).</summary>
    public void PlayProgramme(ProgrammeRow row)
    {
        if (Current is null || !Current.IsLive) return;

        var now = _utcNow();
        var source = _library.Sources.FirstOrDefault(s => s.Id == Current.Model.SourceId);
        var url = CatchupUrl.CanReplay(Current.Model, row.Programme, now)
            ? CatchupUrl.Build(source, Current.Model, row.Programme.StartUtc, row.Programme.StopUtc, now)
            : null;
        if (url is null)
        {
            StatusText = "Dit programma kan niet worden teruggekeken.";
            return;
        }

        if (!CheckPlayable(url)) return;

        FlushProgress();
        ResetTransport();
        SetMode(PlayMode.Replay);
        _watchKey = null;
        _replayProgramme = row.Programme;

        var p = row.Programme;
        NowTitle = p.Title;
        NowTime = $"{p.StartUtc.ToLocalTime():HH:mm} – {p.StopUtc.ToLocalTime():HH:mm}";
        NowDescription = p.Description;
        NextText = "Je kijkt terug.";
        Progress = 0;
        _replayOffsetMs = 0;
        _lengthMs = (long)(p.StopUtc - p.StartUtc).TotalMilliseconds;
        Raise(nameof(CanSeek));

        _player.Play(url, $"{Current.Name} – {p.Title}");
        StatusText = $"Terugkijken: {Current.Name} – {p.Title}";
        MarkReplaying();
    }
}
