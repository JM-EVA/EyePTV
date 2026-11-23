using System.Windows.Input;

namespace IptvPlayer.Engine.ViewModels;

/// <summary>Een download in het overzicht bij Beheer → Downloads.</summary>
public sealed class DownloadRow : ObservableObject
{
    private readonly DownloadsViewModel _owner;

    internal DownloadRow(DownloadsViewModel owner, DownloadItem model)
    {
        _owner = owner;
        Model = model;
        PauseCommand = new RelayCommand(() => _owner.Pause(this), () => CanPause);
        ResumeCommand = new RelayCommand(() => _owner.Resume(this), () => CanResume);
        PlayCommand = new RelayCommand(() => _owner.Play(this), () => CanPlay);
        DeleteCommand = new RelayCommand(() => _owner.Delete(this));
    }

    public DownloadItem Model { get; }
    public string Id => Model.Id;
    public string? LogoUrl => Model.LogoUrl;

    /// <summary>Titel van de film, of van de aflevering.</summary>
    public string Title => Model.IsEpisode ? $"{Model.Number}. {Model.Title}" : Model.Title;

    /// <summary>"Film", of "Serienaam · seizoen 1".</summary>
    public string Subtitle => Model.IsEpisode ? $"{Model.SeriesTitle} · seizoen {Model.Season}" : "Film";

    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand DeleteCommand { get; }

    public bool CanPause => Model.Status is DownloadStatus.Queued or DownloadStatus.Downloading;
    public bool CanResume => Model.Status is DownloadStatus.Paused or DownloadStatus.Failed;
    public bool CanPlay => Model.Status == DownloadStatus.Done && Model.FilePath is { } f && File.Exists(f);
    public bool ShowProgress => Model.Status is DownloadStatus.Downloading or DownloadStatus.Paused or DownloadStatus.Queued && Model.TotalBytes > 0;
    public bool IsProblem => Model.Status == DownloadStatus.Failed;

    /// <summary>0..1.</summary>
    public double Progress => Model.TotalBytes > 0 ? Math.Clamp((double)Model.Bytes / Model.TotalBytes, 0, 1) : 0;

    public string StatusText => Model.Status switch
    {
        DownloadStatus.Queued => "In de wachtrij" + (Model.Bytes > 0 ? $" · {TimeText.Bytes(Model.Bytes)} gedownload" : ""),
        DownloadStatus.Downloading => Model.TotalBytes > 0
            ? $"Downloaden · {TimeText.Bytes(Model.Bytes)} van {TimeText.Bytes(Model.TotalBytes)} ({(int)(Progress * 100)}%)"
            : $"Downloaden · {TimeText.Bytes(Model.Bytes)}",
        DownloadStatus.Paused => $"Gepauzeerd · {TimeText.Bytes(Model.Bytes)}" + (Model.TotalBytes > 0 ? $" van {TimeText.Bytes(Model.TotalBytes)}" : ""),
        DownloadStatus.Done => $"Klaar · {TimeText.Bytes(Model.Bytes)}",
        _ => string.IsNullOrEmpty(Model.Error) ? "Mislukt" : Model.Error!
    };

    internal void Refresh()
    {
        Raise(nameof(CanPause));
        Raise(nameof(CanResume));
        Raise(nameof(CanPlay));
        Raise(nameof(ShowProgress));
        Raise(nameof(IsProblem));
        Raise(nameof(Progress));
        Raise(nameof(StatusText));
        ((RelayCommand)PauseCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ResumeCommand).RaiseCanExecuteChanged();
        ((RelayCommand)PlayCommand).RaiseCanExecuteChanged();
    }
}

/// <summary>Beheer van downloads (bij Beheer → Downloads): wat wacht, wat loopt en wat klaar is.</summary>
public sealed class DownloadsViewModel : ObservableObject
{
    private readonly DownloadManager _manager;
    private readonly IDialogService? _dialogs;
    private readonly Action<DownloadItem>? _play;
    private readonly AdultLock? _lock;
    private readonly Dictionary<string, DownloadRow> _rows = new();
    private IReadOnlyList<DownloadRow> _list = Array.Empty<DownloadRow>();

    public DownloadsViewModel(DownloadManager manager, IDialogService? dialogs, Action<DownloadItem>? play, AdultLock? adultLock = null)
    {
        _manager = manager;
        _dialogs = dialogs;
        _play = play;
        _lock = adultLock;
        _manager.Changed += Refresh;
        if (_lock is not null) _lock.Changed += Refresh;
        Refresh();
    }

    public IReadOnlyList<DownloadRow> Rows { get => _list; private set => Set(ref _list, value); }
    public bool IsEmpty => _list.Count == 0;

    public string Summary
    {
        get
        {
            var active = _list.Count(r => r.Model.Status == DownloadStatus.Downloading);
            var waiting = _list.Count(r => r.Model.Status == DownloadStatus.Queued);
            var done = _list.Count(r => r.Model.Status == DownloadStatus.Done);
            var parts = new List<string>();
            if (active > 0) parts.Add($"{active} bezig");
            if (waiting > 0) parts.Add($"{waiting} in de wachtrij");
            if (done > 0) parts.Add($"{done} klaar");
            return parts.Count == 0 ? "Nog niets gedownload." : string.Join(" · ", parts);
        }
    }

    public void Refresh()
    {
        var all = _lock is null ? _manager.All : _manager.All.Where(d => !_lock.HidesDownload(d)).ToList();
        var ids = new HashSet<string>(all.Select(d => d.Id));
        foreach (var gone in _rows.Keys.Where(k => !ids.Contains(k)).ToList()) _rows.Remove(gone);

        var added = false;
        var ordered = new List<DownloadRow>();
        foreach (var d in all)
        {
            if (_rows.TryGetValue(d.Id, out var row)) row.Refresh();
            else { row = new DownloadRow(this, d); _rows[d.Id] = row; added = true; }
            ordered.Add(row);
        }

        if (added || ordered.Count != _list.Count || !ordered.SequenceEqual(_list)) Rows = ordered;
        Raise(nameof(IsEmpty));
        Raise(nameof(Summary));
    }

    internal void Pause(DownloadRow row) => _manager.Pause(row.Id);
    internal void Resume(DownloadRow row) => _manager.Resume(row.Id);
    internal void Play(DownloadRow row) => _play?.Invoke(row.Model);

    internal void Delete(DownloadRow row)
    {
        var hasFile = row.Model.FilePath is { } f && File.Exists(f);
        var message = $"'{row.Title}' verwijderen?" + (hasFile ? "\n\nHet gedownloade bestand wordt ook van je schijf gehaald." : "");
        if (_dialogs is not null && !_dialogs.Confirm("Download verwijderen", message)) return;
        _manager.Delete(row.Id, deleteFile: true);
    }
}
