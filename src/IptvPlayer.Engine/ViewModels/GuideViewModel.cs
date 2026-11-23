using System.Windows.Input;

namespace IptvPlayer.Engine.ViewModels;

/// <summary>Een zender in de gids (links).</summary>
public sealed class GuideChannelRow : ObservableObject
{
    private string? _nowTitle;

    internal GuideChannelRow(PersonalChannel model) => Model = model;

    public PersonalChannel Model { get; }
    public string Name => Model.DisplayName;
    public string? LogoUrl => Model.LogoUrl;
    public bool IsFavorite => Model.IsFavorite;
    public bool HasCatchup => CatchupUrl.IsAvailable(Model);

    /// <summary>Wat er nu op is.</summary>
    public string? NowTitle { get => _nowTitle; internal set => Set(ref _nowTitle, value); }
}

/// <summary>Een programma in de gids (rechts) met opnemen, terugkijken en live kijken.</summary>
public sealed class GuideProgrammeRow : ObservableObject
{
    private bool _isRecorded;
    private bool _isRecordingNow;
    private bool _isReminded;

    internal GuideProgrammeRow(Programme programme, bool isNow, bool isPast, bool canReplay, bool canRecord, bool isRecorded,
        Action<GuideProgrammeRow> record, Action<GuideProgrammeRow> replay, Action<GuideProgrammeRow> watch,
        Action<GuideProgrammeRow>? remind = null, bool isReminded = false)
    {
        _isReminded = isReminded;
        CanRemind = remind is not null && !isPast && !isNow;
        RemindCommand = new RelayCommand(() => remind?.Invoke(this), () => CanRemind);
        Programme = programme;
        IsNow = isNow;
        IsPast = isPast;
        CanReplay = canReplay;
        CanRecord = canRecord;
        _isRecorded = isRecorded;
        _isRecordingNow = false;
        RecordCommand = new RelayCommand(() => record(this), () => canRecord);
        ReplayCommand = new RelayCommand(() => replay(this), () => canReplay);
        WatchCommand = new RelayCommand(() => watch(this));
    }

    public Programme Programme { get; }
    public string Title => Programme.Title;
    public string? Description => Programme.Description;
    public bool IsNow { get; }
    public bool IsPast { get; }
    public bool CanReplay { get; }
    public bool CanRecord { get; }
    public string TimeText => $"{Programme.StartUtc.ToLocalTime():HH:mm} – {Programme.StopUtc.ToLocalTime():HH:mm}";

    /// <summary>Dit programma staat gepland of wordt opgenomen.</summary>
    public bool IsRecorded => _isRecorded;

    /// <summary>De opname loopt nu.</summary>
    public bool IsRecordingNow => _isRecordingNow;

    internal void SetRecording(Recording? recording)
    {
        _isRecorded = recording is not null;
        _isRecordingNow = recording?.Status == RecordingStatus.Recording;
        Raise(nameof(IsRecorded));
        Raise(nameof(IsRecordingNow));
        Raise(nameof(RecordText));
    }

    public string RecordText => _isRecordingNow ? "Neemt op" : _isRecorded ? "Opname gepland" : "Opnemen";

    /// <summary>Het programma moet nog beginnen, dus er kan een herinnering voor gezet worden.</summary>
    public bool CanRemind { get; }
    public bool IsReminded => _isReminded;
    public string RemindText => _isReminded ? "Herinnering aan" : "Herinner mij";
    public ICommand RemindCommand { get; }

    internal void SetReminded(bool value)
    {
        _isReminded = value;
        Raise(nameof(IsReminded));
        Raise(nameof(RemindText));
    }

    public ICommand RecordCommand { get; }
    public ICommand ReplayCommand { get; }
    public ICommand WatchCommand { get; }
}

/// <summary>
/// Het tabblad Gids: kies een zender en een dag, zie het programma, en neem programma's op, kijk ze terug of kijk live.
/// Alleen voor zenders in je persoonlijke lijst.
/// </summary>
public sealed partial class GuideViewModel : ObservableObject
{
    private readonly AdultLock _lock;
    private readonly Library _library;
    private readonly EpgService _epg;
    private readonly RecordingManager? _recordings;
    private readonly ChannelsViewModel _channels;
    private readonly Action _showPlayer;
    private readonly Func<DateTime> _utcNow;
    private readonly ReminderManager? _reminders;

    private IReadOnlyList<GuideChannelRow> _channelRows = Array.Empty<GuideChannelRow>();
    private GuideChannelRow? _selectedChannel;
    private string _searchText = "";
    private bool _favoritesOnly;
    private IReadOnlyList<ScheduleDay> _days = Array.Empty<ScheduleDay>();
    private DateTime? _selectedDay;
    private IReadOnlyList<GuideProgrammeRow> _programmes = Array.Empty<GuideProgrammeRow>();
    private string? _message;

    public GuideViewModel(Library library, EpgService epg, RecordingManager? recordings, ChannelsViewModel channels, Action showPlayer,
        Func<DateTime>? utcNow = null, AdultLock? adultLock = null, ReminderManager? reminders = null)
    {
        _reminders = reminders;
        _lock = adultLock ?? AdultLock.Open();
        _library = library;
        _epg = epg;
        _recordings = recordings;
        _channels = channels;
        _showPlayer = showPlayer;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);

        if (_recordings is not null) _recordings.Changed += RefreshRecordedFlags;
        if (_reminders is not null) _reminders.Changed += RefreshReminders;
    }

    public IReadOnlyList<GuideChannelRow> Channels { get => _channelRows; private set => Set(ref _channelRows, value); }

    public GuideChannelRow? SelectedChannel
    {
        get => _selectedChannel;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedChannel)) return; // een ListBox stuurt null als zijn lijst wisselt
            _selectedChannel = value;
            _selectedDay = null;
            Raise();
            Raise(nameof(SelectedChannelName));
            BuildDays();
        }
    }

    public string SelectedChannelName => _selectedChannel?.Name ?? "";

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value ?? "")) BuildChannels();
        }
    }

    public bool FavoritesOnly
    {
        get => _favoritesOnly;
        set
        {
            if (Set(ref _favoritesOnly, value)) BuildChannels();
        }
    }

    public IReadOnlyList<ScheduleDay> Days { get => _days; private set => Set(ref _days, value); }

    public ScheduleDay? SelectedDay
    {
        get => _days.FirstOrDefault(d => d.Date == _selectedDay);
        set
        {
            if (value is null || value.Date == _selectedDay) return;
            _selectedDay = value.Date;
            Raise();
            BuildProgrammes();
        }
    }

    public IReadOnlyList<GuideProgrammeRow> Programmes { get => _programmes; private set => Set(ref _programmes, value); }

    /// <summary>Melding bij de programma's ("Geen programma-informatie…", "Opname ingepland…").</summary>
    public string? Message
    {
        get => _message;
        private set
        {
            if (Set(ref _message, value)) Raise(nameof(HasMessage));
        }
    }

    public bool HasMessage => !string.IsNullOrEmpty(_message);
    public bool HasNoChannels => _channelRows.Count == 0;

    // ---- Opbouwen ----

    /// <summary>Wordt aangeroepen als je naar de Gids gaat: lijst van zenders en wat er nu op is bijwerken.</summary>
    public void Refresh()
    {
        BuildGroups();
        BuildChannels();
        RefreshReminders();
        Search();
        BuildDays();
    }

    private void BuildChannels()
    {
        var now = _utcNow();
        IEnumerable<PersonalChannel> q = _library.Ordered().Where(p => p.Kind == ContentKind.Live && !_lock.Hides(p));
        if (_favoritesOnly) q = q.Where(p => p.IsFavorite);
        if (_selectedGroup != AllGroups) q = q.Where(p => p.Group == _selectedGroup);
        var term = _searchText.Trim();
        if (term.Length > 0)
            q = q.Where(p => p.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                             p.Name.Contains(term, StringComparison.OrdinalIgnoreCase));

        var rows = q.Select(p => new GuideChannelRow(p) { NowTitle = _epg.Data.NowNext(p, now).Now?.Title }).ToList();
        Channels = rows;
        BuildGrid();
        Raise(nameof(HasNoChannels));

        // De keuze blijft staan (ook als de lijst opnieuw wordt opgebouwd); anders de eerste zender met gidsgegevens.
        var keep = _selectedChannel is null ? null : rows.FirstOrDefault(r => r.Model.Id == _selectedChannel.Model.Id);
        var target = keep ?? rows.FirstOrDefault(r => _epg.Data.Resolve(r.Model) is not null) ?? rows.FirstOrDefault();
        if (target is not null && !ReferenceEquals(target, _selectedChannel))
        {
            _selectedChannel = target;
            _selectedDay = keep is null ? null : _selectedDay;
            Raise(nameof(SelectedChannel));
            Raise(nameof(SelectedChannelName));
        }
        else if (target is null)
        {
            _selectedChannel = null;
            Raise(nameof(SelectedChannel));
            Raise(nameof(SelectedChannelName));
        }
    }

    private void BuildDays()
    {
        if (_selectedChannel is null)
        {
            Days = Array.Empty<ScheduleDay>();
            Programmes = Array.Empty<GuideProgrammeRow>();
            Message = "Je lijst heeft nog geen zenders.";
            return;
        }

        var ch = _selectedChannel.Model;
        var now = _utcNow();
        var today = now.ToLocalTime().Date;
        var back = CatchupUrl.IsAvailable(ch) ? Math.Min(ch.CatchupDays, 14) : 0;
        var from = new[] { now.AddDays(-back), today.ToUniversalTime() }.Min();
        var all = _epg.Data.Schedule(ch, from, now.AddDays(3));

        // Het programma dat nu loopt hoort bij vandaag, ook als het gisteravond begon.
        DateTime DayOf(Programme p) => p.StartUtc <= now && p.StopUtc > now ? today : p.StartUtc.ToLocalTime().Date;
        var dates = all.Select(DayOf).Distinct().OrderBy(d => d).ToList();

        var days = dates.Select(d => new ScheduleDay(d, DayTitle(d, today))).ToList();
        if (!days.SequenceEqual(_days)) Days = days;

        _selectedDay = _selectedDay is { } s && dates.Contains(s) ? s : dates.Contains(today) ? today : dates.Cast<DateTime?>().FirstOrDefault();
        Raise(nameof(SelectedDay));
        BuildProgrammes();
    }

    private void BuildProgrammes()
    {
        if (_selectedChannel is null || _selectedDay is null)
        {
            Programmes = Array.Empty<GuideProgrammeRow>();
            Message = _selectedChannel is null ? null : "Voor deze zender is geen programma-informatie (EPG) beschikbaar.";
            return;
        }

        var ch = _selectedChannel.Model;
        var now = _utcNow();
        var today = now.ToLocalTime().Date;
        var from = _selectedDay.Value.ToUniversalTime().AddHours(-6);
        var to = _selectedDay.Value.AddDays(1).ToUniversalTime().AddHours(6);

        DateTime DayOf(Programme p) => p.StartUtc <= now && p.StopUtc > now ? today : p.StartUtc.ToLocalTime().Date;
        var rows = _epg.Data.Schedule(ch, from, to)
            .Where(p => DayOf(p) == _selectedDay.Value)
            .Select(p => new GuideProgrammeRow(p,
                isNow: p.StartUtc <= now && p.StopUtc > now,
                isPast: p.StopUtc <= now,
                canReplay: CatchupUrl.CanReplay(ch, p, now),
                canRecord: CanRecord(ch, p, now),
                isRecorded: false,
                Record, Replay, Watch))
            .ToList();
        foreach (var row in rows) row.SetRecording(_recordings?.ForProgramme(ch, row.Programme));

        Programmes = rows;
        Message = rows.Count == 0 ? "Geen programma's op deze dag." : null;
    }

    private bool CanRecord(PersonalChannel ch, Programme p, DateTime now) =>
        _recordings is not null &&
        Uri.TryCreate(ch.Url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) &&
        p.StopUtc + _recordings.After > now;

    private static string DayTitle(DateTime day, DateTime today)
    {
        var diff = (day.Date - today.Date).Days;
        return diff switch
        {
            0 => "Vandaag",
            -1 => "Gisteren",
            1 => "Morgen",
            _ => day.ToString("ddd d MMM", new System.Globalization.CultureInfo("nl-NL"))
        };
    }

    private void RefreshRecordedFlags()
    {
        if (_selectedChannel is null || _recordings is null) return;
        foreach (var row in _programmes)
            row.SetRecording(_recordings.ForProgramme(_selectedChannel.Model, row.Programme));
    }

    // ---- Acties ----

    private void Record(GuideProgrammeRow row) { if (_selectedChannel is not null) RecordFor(_selectedChannel.Model, row); }

    private void Replay(GuideProgrammeRow row) { if (_selectedChannel is not null) ReplayFor(_selectedChannel.Model, row); }

    private void Watch(GuideProgrammeRow row) { if (_selectedChannel is not null) WatchFor(_selectedChannel.Model); }

    /// <summary>Direct de zender die links staat live kijken.</summary>
    public void WatchSelectedLive()
    {
        if (_selectedChannel is null) return;
        _showPlayer();
        _channels.WatchLive(_selectedChannel.Model);
    }
}
