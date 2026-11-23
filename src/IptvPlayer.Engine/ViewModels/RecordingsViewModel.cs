using System.Windows.Input;

namespace IptvPlayer.Engine.ViewModels;

/// <summary>Een opname in het overzicht bij Instellingen.</summary>
public sealed class RecordingRow : ObservableObject
{
    private readonly RecordingsViewModel _owner;

    internal RecordingRow(RecordingsViewModel owner, Recording model)
    {
        _owner = owner;
        Model = model;
        StopCommand = new RelayCommand(() => _owner.Stop(this), () => Model.IsActive);
        DeleteCommand = new RelayCommand(() => _owner.Delete(this));
        PlayCommand = new RelayCommand(() => _owner.Play(this), () => CanPlay);
    }

    public Recording Model { get; }
    public string Id => Model.Id;
    public string Title => Model.Title;
    public string ChannelName => Model.ChannelName;
    public string? LogoUrl => Model.LogoUrl;

    public ICommand StopCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand PlayCommand { get; }

    public bool IsActive => Model.IsActive;
    public bool IsRecording => Model.Status == RecordingStatus.Recording;
    public bool IsProblem => Model.Status is RecordingStatus.Failed or RecordingStatus.Missed;
    public bool CanPlay => Model.Status is RecordingStatus.Done or RecordingStatus.Failed && Model.Bytes > 0 && Model.FilePath is { } f && File.Exists(f);

    /// <summary>Knoptekst: "Stoppen" bij een lopende opname, "Annuleren" bij een geplande.</summary>
    public string StopText => Model.Status == RecordingStatus.Recording ? "Stoppen" : "Annuleren";

    /// <summary>"ma 26 okt 20:58 – 22:05" of "… · tot je stopt".</summary>
    public string WhenText
    {
        get
        {
            var nl = new System.Globalization.CultureInfo("nl-NL");
            var start = (Model.ActualStartUtc ?? Model.StartUtc).ToLocalTime();
            var end = Model.ActualEndUtc ?? Model.EndUtc;
            var text = start.ToString("ddd d MMM HH:mm", nl);
            return end is { } e ? $"{text} – {e.ToLocalTime():HH:mm}" : $"{text} · tot je stopt";
        }
    }

    public string StatusText => Model.Status switch
    {
        RecordingStatus.Scheduled => $"Gepland · begint {Model.StartUtc.ToLocalTime():HH:mm}",
        RecordingStatus.Recording => $"● Neemt op · {TimeText.Bytes(Model.Bytes)}",
        RecordingStatus.Done => $"Klaar · {TimeText.Bytes(Model.Bytes)}",
        RecordingStatus.Cancelled => "Geannuleerd",
        _ => string.IsNullOrEmpty(Model.Error) ? "Mislukt" : Model.Error!
    };

    internal void Refresh()
    {
        Raise(nameof(IsActive));
        Raise(nameof(IsRecording));
        Raise(nameof(IsProblem));
        Raise(nameof(CanPlay));
        Raise(nameof(StopText));
        Raise(nameof(WhenText));
        Raise(nameof(StatusText));
        ((RelayCommand)StopCommand).RaiseCanExecuteChanged();
        ((RelayCommand)PlayCommand).RaiseCanExecuteChanged();
    }
}

/// <summary>Beheer van opnames (bij Instellingen): wat gepland staat, wat loopt en wat klaar is.</summary>
public sealed class RecordingsViewModel : ObservableObject
{
    private readonly RecordingManager _manager;
    private readonly IDialogService? _dialogs;
    private readonly Action<Recording>? _play;
    private readonly Dictionary<string, RecordingRow> _rows = new();
    private IReadOnlyList<RecordingRow> _list = Array.Empty<RecordingRow>();

    private readonly AdultLock? _lock;

    public RecordingsViewModel(RecordingManager manager, IDialogService? dialogs, Action<Recording>? play, AdultLock? adultLock = null)
    {
        _lock = adultLock;
        if (_lock is not null) _lock.Changed += Refresh;
        _manager = manager;
        _dialogs = dialogs;
        _play = play;
        _manager.Changed += Refresh;
        Refresh();
    }

    public IReadOnlyList<RecordingRow> Rows { get => _list; private set => Set(ref _list, value); }
    public bool HasRows => _list.Count > 0;
    public bool IsEmpty => _list.Count == 0;

    public string Summary
    {
        get
        {
            var active = _list.Count(r => r.IsRecording);
            var planned = _list.Count(r => r.Model.Status == RecordingStatus.Scheduled);
            var done = _list.Count(r => r.Model.Status == RecordingStatus.Done);
            var parts = new List<string>();
            if (active > 0) parts.Add($"{active} bezig");
            if (planned > 0) parts.Add($"{planned} gepland");
            if (done > 0) parts.Add($"{done} klaar");
            return parts.Count == 0 ? "Nog geen opnames." : string.Join(" · ", parts);
        }
    }

    public int ActiveCount => _manager.ActiveCount;

    /// <summary>Wordt door de manager aangeroepen bij elke wijziging; werkt de bestaande rijen bij (zonder de lijst te laten springen).</summary>
    public void Refresh()
    {
        var all = _lock is null ? _manager.All : _manager.All.Where(r => !_manager.IsHidden(r, _lock)).ToList();
        var ids = new HashSet<string>(all.Select(r => r.Id));
        foreach (var gone in _rows.Keys.Where(k => !ids.Contains(k)).ToList()) _rows.Remove(gone);

        var added = false;
        var ordered = new List<RecordingRow>();
        foreach (var r in all)
        {
            if (_rows.TryGetValue(r.Id, out var row)) row.Refresh();
            else { row = new RecordingRow(this, r); _rows[r.Id] = row; added = true; }
            ordered.Add(row);
        }

        if (added || ordered.Count != _list.Count || !ordered.SequenceEqual(_list))
            Rows = ordered;

        Raise(nameof(HasRows));
        Raise(nameof(IsEmpty));
        Raise(nameof(Summary));
        Raise(nameof(ActiveCount));
    }

    internal void Stop(RecordingRow row) => _manager.Stop(row.Id);

    internal void Play(RecordingRow row) => _play?.Invoke(row.Model);

    internal void Delete(RecordingRow row)
    {
        var active = row.Model.IsActive;
        var hasFile = row.Model.FilePath is { } f && File.Exists(f);
        var message = active
            ? $"'{row.Title}' stoppen en verwijderen?" + (hasFile ? "\n\nHet tot nu toe opgenomen bestand wordt ook van je schijf gehaald." : "")
            : $"'{row.Title}' verwijderen?" + (hasFile ? "\n\nHet opgenomen bestand wordt ook van je schijf gehaald." : "");
        if (_dialogs is not null && !_dialogs.Confirm("Opname verwijderen", message)) return;
        _manager.Delete(row.Id, deleteFile: true);
    }
}
