using System.Windows.Input;

namespace IptvPlayer.Engine.ViewModels;

public sealed class ChannelRow : ObservableObject
{
    private readonly ChannelsViewModel _owner;
    private string? _nowTitle;
    private bool _isPlaying;
    private bool _isWatched;

    internal ChannelRow(ChannelsViewModel owner, PersonalChannel model)
    {
        _owner = owner;
        Model = model;
        PlayCommand = new RelayCommand(() => _owner.Play(this));
        MoveUpCommand = new RelayCommand(() => _owner.Move(this, -1));
        MoveDownCommand = new RelayCommand(() => _owner.Move(this, +1));
        MoveToTopCommand = new RelayCommand(() => _owner.MoveToTop(this));
        RemoveCommand = new RelayCommand(() => _owner.Remove(this));
        RenameCommand = new RelayCommand(() => _owner.Rename(this));
        ResetNameCommand = new RelayCommand(() => _owner.ResetName(this));
        DownloadCommand = new RelayCommand(() => _owner.Download(this), () => CanDownload);
        SetBackupsCommand = new RelayCommand(() => _owner.SetBackups(this));
        ClearBackupsCommand = new RelayCommand(() => _owner.ClearBackups(this));
    }

    public ICommand SetBackupsCommand { get; }
    public ICommand ClearBackupsCommand { get; }

    /// <summary>Alleen live zenders kunnen een reservezender hebben.</summary>
    public bool CanHaveBackups => IsLive;
    public bool HasBackups => Model.BackupIds.Count > 0;

    internal void BackupsChanged() => Raise(nameof(HasBackups));

    public ICommand DownloadCommand { get; }

    /// <summary>Een film die je kunt downloaden (nog niet gedownload of in de wachtrij).</summary>
    public bool CanDownload => Kind == ContentKind.Movie && _owner.CanDownload(Model.Id, Model.Url);

    /// <summary>"Downloaden… 45%", "✓ Gedownload"…; null als dit item niet gedownload wordt.</summary>
    public string? DownloadText => Kind == ContentKind.Movie ? _owner.DownloadTextFor(Model.Id) : null;

    internal void DownloadChanged()
    {
        Raise(nameof(CanDownload));
        Raise(nameof(DownloadText));
        ((RelayCommand)DownloadCommand).RaiseCanExecuteChanged();
    }

    public ICommand RenameCommand { get; }
    public ICommand ResetNameCommand { get; }

    /// <summary>De naam is door de gebruiker aangepast (de officiële naam blijft gekoppeld aan de bron).</summary>
    public bool IsRenamed => !string.IsNullOrWhiteSpace(Model.CustomName);

    internal void NameChanged()
    {
        Raise(nameof(Name));
        Raise(nameof(IsRenamed));
    }

    public ICommand PlayCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand MoveToTopCommand { get; }
    public ICommand RemoveCommand { get; }

    public PersonalChannel Model { get; }
    public string Name => Model.DisplayName;
    public string Group => Model.Group;
    public string? LogoUrl => Model.LogoUrl;
    public bool IsMissing => Model.MissingAtSource;
    public ContentKind Kind => Model.Kind;
    public bool IsLive => Model.Kind == ContentKind.Live;
    public bool IsSeries => Model.Kind == ContentKind.Series;

    /// <summary>Kort stukje tekst onder de naam (de plot van een film of serie).</summary>
    public string? Info => Model.Info;

    /// <summary>Deze zender heeft terugkijken (en we weten hoe we dat moeten aanvragen).</summary>
    public bool HasCatchup => CatchupUrl.IsAvailable(Model);
    public string CatchupText => $"Terugkijken mogelijk ({Model.CatchupDays} {(Model.CatchupDays == 1 ? "dag" : "dagen")})";

    /// <summary>"Film" of "Serie" (leeg bij zenders), zodat je in de lijst ziet wat je opent.</summary>
    public string KindLabel => Kind switch { ContentKind.Movie => "Film", ContentKind.Series => "Serie", _ => "" };

    public bool IsFavorite
    {
        get => Model.IsFavorite;
        set
        {
            if (Model.IsFavorite == value) return;
            _owner.SetFavorite(this, value);
            Raise();
        }
    }

    public string? NowTitle
    {
        get => _nowTitle;
        internal set => Set(ref _nowTitle, value);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        internal set => Set(ref _isPlaying, value);
    }

    /// <summary>Film is helemaal bekeken.</summary>
    public bool IsWatched
    {
        get => _isWatched;
        internal set => Set(ref _isWatched, value);
    }
}

public sealed class ProgrammeRow : ObservableObject
{
    private bool _isReplaying;
    private bool _isRecorded;

    public ProgrammeRow(Programme programme, bool isNow, bool isPast = false, bool canReplay = false, Action<ProgrammeRow>? replay = null,
        bool canRecord = false, bool isRecorded = false, Action<ProgrammeRow>? record = null)
    {
        Programme = programme;
        IsNow = isNow;
        IsPast = isPast;
        CanReplay = canReplay;
        CanRecord = canRecord;
        _isRecorded = isRecorded;
        ReplayCommand = new RelayCommand(() => replay?.Invoke(this), () => canReplay);
        RecordCommand = new RelayCommand(() => record?.Invoke(this), () => canRecord);
    }

    /// <summary>Dit programma kan nog worden opgenomen (het is niet afgelopen en de zender heeft een http(s)-stream).</summary>
    public bool CanRecord { get; }
    public ICommand RecordCommand { get; }

    /// <summary>Dit programma staat gepland of wordt opgenomen.</summary>
    public bool IsRecorded
    {
        get => _isRecorded;
        internal set
        {
            if (Set(ref _isRecorded, value)) Raise(nameof(RecordText));
        }
    }

    public string RecordText => _isRecorded ? "Opname gepland" : "Opnemen";

    public Programme Programme { get; }
    public bool IsNow { get; }
    public bool IsPast { get; }
    public bool CanReplay { get; }
    public ICommand ReplayCommand { get; }
    public string Title => Programme.Title;
    public string? Description => Programme.Description;
    public string TimeText => $"{Programme.StartUtc.ToLocalTime():HH:mm} – {Programme.StopUtc.ToLocalTime():HH:mm}";

    /// <summary>Dit programma wordt nu teruggekeken.</summary>
    public bool IsReplaying
    {
        get => _isReplaying;
        internal set => Set(ref _isReplaying, value);
    }
}

public sealed record KindOption(ContentKind Kind, string Title);

public sealed record ScheduleDay(DateTime Date, string Title);

public sealed record SeasonOption(int Number, string Title);

public sealed class EpisodeRow : ObservableObject
{
    private bool _isPlaying;
    private bool _isWatched;
    private string? _resumeText;

    private readonly ChannelsViewModel? _owner;

    internal EpisodeRow(Episode episode, Action<EpisodeRow> play, ChannelsViewModel? owner = null)
    {
        Episode = episode;
        _owner = owner;
        PlayCommand = new RelayCommand(() => play(this));
        DownloadCommand = new RelayCommand(() => _owner?.DownloadEpisode(this), () => CanDownload);
    }

    public Episode Episode { get; }
    public ICommand PlayCommand { get; }
    public ICommand DownloadCommand { get; }

    public bool CanDownload => _owner?.CanDownloadEpisode(Episode) == true;

    /// <summary>"Downloaden… 45%", "✓ Gedownload"…; null als deze aflevering niet gedownload wordt.</summary>
    public string? DownloadText => _owner?.EpisodeDownloadText(Episode);

    internal void DownloadChanged()
    {
        Raise(nameof(CanDownload));
        Raise(nameof(DownloadText));
        ((RelayCommand)DownloadCommand).RaiseCanExecuteChanged();
    }
    public string Title => $"{Episode.Number}. {Episode.Title}";
    public string? Plot => Episode.Plot;
    public string? LogoUrl => Episode.LogoUrl;
    public string? DurationText => Episode.DurationSeconds is > 0 and var s ? TimeText.Format(s * 1000L) : null;

    public bool IsPlaying { get => _isPlaying; internal set => Set(ref _isPlaying, value); }
    public bool IsWatched { get => _isWatched; internal set => Set(ref _isWatched, value); }

    /// <summary>Bijvoorbeeld "Verder bij 12:30"; leeg als je nog niet bent begonnen.</summary>
    public string? ResumeText { get => _resumeText; internal set => Set(ref _resumeText, value); }
}

public static class TimeText
{
    /// <summary>"340 MB", "1,2 GB".</summary>
    public static string Bytes(long b)
    {
        if (b < 1024 * 1024) return $"{b / 1024} kB";
        if (b < 1024L * 1024 * 1024) return $"{b / 1048576.0:0} MB";
        return $"{b / 1073741824.0:0.0} GB";
    }

    /// <summary>12:05 of 1:02:03.</summary>
    public static string Format(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }
}
