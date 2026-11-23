using System.Windows.Input;

namespace IptvPlayer.Engine.ViewModels;

/// <summary>Een programmablok in het rooster. Posities zijn in pixels (zie <see cref="GuideViewModel.PixelsPerMinute"/>).</summary>
public sealed class GuideBlock : ObservableObject
{
    private bool _isSelected;
    private bool _isReminded;

    internal GuideBlock(GridBlock block, bool isNow, bool isPast, bool isReminded, double pixelsPerMinute, Action<GuideBlock> select, Action<GuideBlock> watch, DateTime nowUtc)
    {
        Block = block;
        IsNow = isNow;
        IsPast = isPast;
        _isReminded = isReminded;
        X = block.StartMinutes * pixelsPerMinute;
        Width = Math.Max(2, block.Minutes * pixelsPerMinute - 2); // 2 px ruimte tussen blokken
        SelectCommand = new RelayCommand(() => select(this));
        WatchCommand = new RelayCommand(() => watch(this));
        var total = (block.Programme.StopUtc - block.Programme.StartUtc).TotalMinutes;
        if (isNow && total > 0) ProgressX = Width * Math.Clamp((nowUtc - block.Programme.StartUtc).TotalMinutes / total, 0, 1);
    }

    /// <summary>Hoe ver het programma dat nu loopt is (rode balk onderin), in pixels; anders 0.</summary>
    public double ProgressX { get; }
    public string? Description => Programme.Description;

    public GridBlock Block { get; }
    public Programme Programme => Block.Programme;
    public string Title => Programme.Title;
    public string TimeText => $"{Programme.StartUtc.ToLocalTime():HH:mm} – {Programme.StopUtc.ToLocalTime():HH:mm}";
    public double X { get; }
    public double Width { get; }
    public bool IsNow { get; }
    public bool IsPast { get; }
    public ICommand SelectCommand { get; }

    /// <summary>Dubbelklik: naar de zender in de speler.</summary>
    public ICommand WatchCommand { get; }

    public bool IsSelected { get => _isSelected; internal set => Set(ref _isSelected, value); }
    public bool IsReminded { get => _isReminded; internal set => Set(ref _isReminded, value); }
}

/// <summary>Een rij in het rooster: een zender met zijn blokken (die pas worden opgebouwd als de rij in beeld komt).</summary>
public sealed class GuideGridRow
{
    private readonly Func<GuideGridRow, IReadOnlyList<GuideBlock>> _build;
    private IReadOnlyList<GuideBlock>? _blocks;

    internal GuideGridRow(GuideChannelRow channel, Func<GuideGridRow, IReadOnlyList<GuideBlock>> build)
    {
        Channel = channel;
        _build = build;
    }

    public GuideChannelRow Channel { get; }
    public IReadOnlyList<GuideBlock> Blocks => _blocks ??= _build(this);
    internal IReadOnlyList<GuideBlock>? BuiltBlocks => _blocks;
}

/// <summary>Een uurstreepje boven het rooster.</summary>
public sealed record GuideHourMark(double X, string Label);

/// <summary>Een zoekresultaat: zender plus programma met dezelfde acties als in het rooster.</summary>
public sealed record GuideHitRow(string ChannelName, GuideProgrammeRow Row)
{
    public string WhenText => Row.Programme.StartUtc.ToLocalTime().ToString("ddd d MMM HH:mm", new System.Globalization.CultureInfo("nl-NL"));
}

/// <summary>Een herinnering in "Mijn herinneringen".</summary>
public sealed class ReminderRow
{
    internal ReminderRow(Reminder reminder, Action<Reminder> remove)
    {
        Title = reminder.Title;
        ChannelName = reminder.ChannelName;
        TimeText = $"{reminder.StartUtc.ToLocalTime():ddd d MMM HH:mm}";
        RemoveCommand = new RelayCommand(() => remove(reminder));
    }

    public string Title { get; }
    public string ChannelName { get; }
    public string TimeText { get; }
    public ICommand RemoveCommand { get; }
}

/// <summary>Het gidsrooster (zenders onder elkaar, tijd van links naar rechts), zoeken in de gids en herinneringen.</summary>
public sealed partial class GuideViewModel
{
    public const string AllGroups = "Alle groepen";

    /// <summary>Breedte van één minuut in het rooster: 5 px = 300 px per uur.</summary>
    public const double PixelsPerMinute = 5;

    private IReadOnlyList<string> _groups = new[] { AllGroups };
    private string _selectedGroup = AllGroups;
    private IReadOnlyList<GuideGridRow> _gridRows = Array.Empty<GuideGridRow>();
    private IReadOnlyList<GuideHourMark> _hourMarks = Array.Empty<GuideHourMark>();
    private DateTime _gridStart;
    private DateTime _gridEnd;
    private double _gridWidth;
    private double _nowX;
    private GuideBlock? _selectedBlock;
    private GuideProgrammeRow? _selected;
    private string _selectedTitle = "";
    private string _programmeQuery = "";
    private IReadOnlyList<GuideHitRow> _hits = Array.Empty<GuideHitRow>();
    private bool _showReminders;
    private IReadOnlyList<ReminderRow> _reminderRows = Array.Empty<ReminderRow>();

    // ---- Filters ----

    public IReadOnlyList<string> Groups { get => _groups; private set => Set(ref _groups, value); }

    public string SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (string.IsNullOrEmpty(value) || value == _selectedGroup) return; // een ComboBox stuurt null als zijn lijst wisselt
            _selectedGroup = value;
            Raise();
            BuildChannels();
        }
    }

    private void BuildGroups()
    {
        var groups = _library.Personal.Where(p => p.Kind == ContentKind.Live && !_lock.Hides(p) && p.Group.Length > 0)
            .Select(p => p.Group).Distinct().OrderBy(g => g, StringComparer.OrdinalIgnoreCase).Prepend(AllGroups).ToList();
        if (!groups.Contains(_selectedGroup)) { _selectedGroup = AllGroups; Raise(nameof(SelectedGroup)); }
        if (!groups.SequenceEqual(_groups)) Groups = groups;
    }

    // ---- Rooster ----

    public IReadOnlyList<GuideGridRow> GridRows { get => _gridRows; private set => Set(ref _gridRows, value); }
    public IReadOnlyList<GuideHourMark> HourMarks { get => _hourMarks; private set => Set(ref _hourMarks, value); }
    public double GridWidth { get => _gridWidth; private set => Set(ref _gridWidth, value); }

    /// <summary>De plek van de "nu"-lijn.</summary>
    public double NowX { get => _nowX; private set => Set(ref _nowX, value); }

    /// <summary>De "nu"-lijn opschuiven (elke halve minuut).</summary>
    public void UpdateNow()
    {
        NowX = XFor(_utcNow());
        NowText = _utcNow().ToLocalTime().ToString("HH:mm");
    }

    /// <summary>De plek (px) van een tijdstip in het rooster.</summary>
    public double XFor(DateTime utc) => (utc - _gridStart).TotalMinutes * PixelsPerMinute;

    /// <summary>De tijd in het rode blokje bij de nu-lijn.</summary>
    public string NowText { get => _nowText; private set => Set(ref _nowText, value); }
    private string _nowText = "";

    private void BuildGrid()
    {
        var now = _utcNow();
        var channels = _channelRows.Select(r => r.Model).ToList();
        (_gridStart, _gridEnd) = GuideGrid.Window(channels, _epg.Data, now);
        GridWidth = (_gridEnd - _gridStart).TotalMinutes * PixelsPerMinute;
        UpdateNow();

        var marks = new List<GuideHourMark>();
        var nl = new System.Globalization.CultureInfo("nl-NL");
        for (var t = _gridStart; t < _gridEnd; t = t.AddHours(1))
        {
            var local = t.ToLocalTime();
            marks.Add(new GuideHourMark((t - _gridStart).TotalMinutes * PixelsPerMinute, local.Hour == 0 ? local.ToString("ddd d MMM", nl) : local.ToString("HH:mm")));
        }
        HourMarks = marks;

        GridRows = _channelRows.Select(r => new GuideGridRow(r, BlocksFor)).ToList();
        Raise(nameof(HasNoChannels));
    }

    private IReadOnlyList<GuideBlock> BlocksFor(GuideGridRow row)
    {
        var now = _utcNow();
        var ch = row.Channel.Model;
        return GuideGrid.Blocks(ch, _epg.Data, _gridStart, _gridEnd)
            .Select(b => new GuideBlock(b, b.Programme.StartUtc <= now && b.Programme.StopUtc > now, b.Programme.StopUtc <= now,
                _reminders?.Has(ch.Id, b.Programme.StartUtc) == true, PixelsPerMinute, block => SelectBlock(row, block), _ => WatchFor(row.Channel.Model), now))
            .ToList();
    }

    // ---- Details van een gekozen programma ----

    /// <summary>Het gekozen programma met de passende acties (kijk nu, terugkijken, herinner mij, opnemen).</summary>
    public GuideProgrammeRow? Selected { get => _selected; private set { if (Set(ref _selected, value)) Raise(nameof(HasSelected)); } }

    public bool HasSelected => _selected is not null;

    /// <summary>"Zendernaam · 20:30 – 21:00".</summary>
    public string SelectedTitle { get => _selectedTitle; private set => Set(ref _selectedTitle, value); }

    private void SelectBlock(GuideGridRow row, GuideBlock block)
    {
        if (_selectedBlock is not null) _selectedBlock.IsSelected = false;
        _selectedBlock = block;
        block.IsSelected = true;
        ShowProgramme(row.Channel.Model, block.Programme);
    }

    private void ShowProgramme(PersonalChannel ch, Programme p)
    {
        Selected = MakeRow(ch, p);
        SelectedTitle = $"{ch.DisplayName}  ·  {Selected.TimeText}";
    }

    private GuideProgrammeRow MakeRow(PersonalChannel ch, Programme p)
    {
        var now = _utcNow();
        var row = new GuideProgrammeRow(p, isNow: p.StartUtc <= now && p.StopUtc > now, isPast: p.StopUtc <= now,
            canReplay: CatchupUrl.CanReplay(ch, p, now), canRecord: CanRecord(ch, p, now), isRecorded: false,
            r => RecordFor(ch, r), r => ReplayFor(ch, r), r => WatchFor(ch),
            _reminders is null ? null : r => ToggleReminder(ch, r), isReminded: _reminders?.Has(ch.Id, p.StartUtc) == true);
        row.SetRecording(_recordings?.ForProgramme(ch, p));
        return row;
    }

    private void RecordFor(PersonalChannel ch, GuideProgrammeRow row)
    {
        _channels.ToggleProgrammeRecording(ch, row.Programme);
        row.SetRecording(_recordings?.ForProgramme(ch, row.Programme));
        Message = _channels.StatusText;
    }

    private void ReplayFor(PersonalChannel ch, GuideProgrammeRow row)
    {
        _showPlayer();
        _channels.ReplayProgramme(ch, row.Programme);
    }

    private void WatchFor(PersonalChannel ch)
    {
        _showPlayer();
        _channels.WatchLive(ch);
    }

    // ---- Zoeken in de gids ----

    public string ProgrammeQuery
    {
        get => _programmeQuery;
        set
        {
            if (!Set(ref _programmeQuery, value ?? "")) return;
            Search();
            RaiseView();
        }
    }

    /// <summary>Er is minstens twee tekens ingetypt: het rooster maakt plaats voor zoekresultaten.</summary>
    public bool IsSearching => _programmeQuery.Trim().Length >= 2;

    public IReadOnlyList<GuideHitRow> Hits { get => _hits; private set => Set(ref _hits, value); }
    public bool HasNoHits => IsSearching && _hits.Count == 0;

    private void Search()
    {
        if (!IsSearching) { Hits = Array.Empty<GuideHitRow>(); return; }
        var channels = _library.Ordered().Where(p => p.Kind == ContentKind.Live && !_lock.Hides(p));
        // Wat al afgelopen is, laten we weg tenzij het terug te kijken is.
        var hits = GuideGrid.Search(channels, _epg.Data, _programmeQuery, _utcNow().AddDays(-14));
        var now = _utcNow();
        Hits = hits.Where(h => h.Programme.StopUtc > now || CatchupUrl.CanReplay(h.Channel, h.Programme, now))
            .Select(h => new GuideHitRow(h.Channel.DisplayName, MakeRow(h.Channel, h.Programme))).ToList();
        Raise(nameof(HasNoHits));
    }

    // ---- Herinneringen ----

    public bool ShowReminders
    {
        get => _showReminders;
        set
        {
            if (!Set(ref _showReminders, value)) return;
            RaiseView();
        }
    }

    public IReadOnlyList<ReminderRow> ReminderRows { get => _reminderRows; private set => Set(ref _reminderRows, value); }
    public bool HasNoReminders => _reminderRows.Count == 0;

    /// <summary>Wat het scherm toont: het rooster, de zoekresultaten of "Mijn herinneringen".</summary>
    public bool ShowGrid => !IsSearching && !_showReminders;
    public bool ShowHits => IsSearching;
    public bool ShowReminderList => !IsSearching && _showReminders;

    private void RaiseView()
    {
        Raise(nameof(IsSearching));
        Raise(nameof(ShowGrid));
        Raise(nameof(ShowHits));
        Raise(nameof(ShowReminderList));
    }

    private void ToggleReminder(PersonalChannel ch, GuideProgrammeRow row)
    {
        if (_reminders is null) return;
        if (_reminders.Has(ch.Id, row.Programme.StartUtc))
        {
            _reminders.Remove(ch.Id, row.Programme.StartUtc);
            Message = "Herinnering verwijderd.";
        }
        else
        {
            _reminders.Add(ch, row.Programme);
            Message = $"Je krijgt een herinnering voor {row.Title} ({_reminders.Find(ch.Id, row.Programme.StartUtc)?.MinutesBefore} minuten van tevoren).";
        }
    }

    /// <summary>Herinneringen zijn veranderd (hier of elders): markeringen en het lijstje bijwerken.</summary>
    private void RefreshReminders()
    {
        if (_reminders is null) return;
        foreach (var r in _gridRows)
            if (r.BuiltBlocks is { } blocks)
                foreach (var b in blocks) b.IsReminded = _reminders.Has(r.Channel.Model.Id, b.Programme.StartUtc);

        if (_selected is not null && _selectedBlock is not null)
        {
            var ch = _gridRows.FirstOrDefault(r => r.BuiltBlocks?.Contains(_selectedBlock) == true)?.Channel.Model;
            if (ch is not null) _selected.SetReminded(_reminders.Has(ch.Id, _selected.Programme.StartUtc));
        }
        foreach (var h in _hits)
        {
            var ch = _library.Personal.FirstOrDefault(p => p.DisplayName == h.ChannelName);
            if (ch is not null) h.Row.SetReminded(_reminders.Has(ch.Id, h.Row.Programme.StartUtc));
        }

        ReminderRows = _reminders.Items.Select(r => new ReminderRow(r, x => _reminders.Remove(x.ChannelId, x.StartUtc))).ToList();
        Raise(nameof(HasNoReminders));
    }
}
