namespace IptvPlayer.Engine.ViewModels;

/// <summary>Een taal in een keuzelijst. Code leeg = "geen voorkeur".</summary>
public sealed record LanguageOption(string Code, string Name)
{
    /// <summary>Een keuzelijst toont dit als er geen sjabloon is (ook in het geselecteerde vak).</summary>
    public override string ToString() => Name;
}

/// <summary>Het tabblad Instellingen. Elke wijziging wordt direct bewaard.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private string _status = "Wijzigingen worden direct bewaard.";
    private readonly RecordingManager? _recordingManager;
    private string? _folderError;

    public SettingsViewModel(SettingsStore store, RecordingManager? recordings = null, IDialogService? dialogs = null,
        Action<Recording>? playRecording = null, AdultLock? adultLock = null, DownloadManager? downloads = null,
        Action<DownloadItem>? playDownload = null)
    {
        if (downloads is not null) Downloads = new DownloadsViewModel(downloads, dialogs, playDownload, adultLock);
        _store = store;
        _lock = adultLock ?? new AdultLock(store);
        _lock.Changed += () => { Raise(nameof(IsAdultUnlocked)); Raise(nameof(IsAdultLocked)); Raise(nameof(AdultLockText)); };
        _recordingManager = recordings;
        if (recordings is not null) Recordings = new RecordingsViewModel(recordings, dialogs, playRecording, _lock);

        SubtitleLanguages = Languages.All.Select(l => new LanguageOption(l.Code, l.Dutch)).ToList();
        AudioLanguages = new[] { new LanguageOption("", "Standaard (zoals de video het aanbiedt)") }
            .Concat(SubtitleLanguages).ToList();
    }

    // ---- Erotiek en pincode ----

    private readonly AdultLock _lock;
    private string _pinMessage = "";

    public bool IsAdultUnlocked => _lock.IsUnlocked;
    public bool IsAdultLocked => !_lock.IsUnlocked;

    public string AdultLockText => _lock.IsUnlocked
        ? "Erotiek is nu zichtbaar. Het wordt weer verborgen als je vergrendelt, het venster minimaliseert of de app sluit."
        : "Erotiek is verborgen. Het staat nergens in de app tot je het ontgrendelt met je code.";

    /// <summary>Uitkomst van de laatste actie met de code (ontgrendelen of wijzigen).</summary>
    public string PinMessage { get => _pinMessage; private set => Set(ref _pinMessage, value); }

    public bool UsesDefaultPin => _lock.UsesDefaultPin;

    public void UnlockAdult(string? pin)
    {
        PinMessage = _lock.TryUnlock(pin) switch
        {
            UnlockResult.Unlocked => "",
            UnlockResult.Blocked => "Te vaak een verkeerde code. Probeer het over een halve minuut opnieuw.",
            _ => "Die code klopt niet.",
        };
    }

    public void LockAdult()
    {
        _lock.Lock();
        PinMessage = "";
    }

    public void ChangeAdultPin(string? current, string? next, string? again)
    {
        if (next != again) { PinMessage = "De nieuwe code en de herhaling zijn niet gelijk."; return; }
        var error = _lock.ChangePin(current, next);
        PinMessage = error ?? "De code is gewijzigd.";
        Raise(nameof(UsesDefaultPin));
    }

    public IReadOnlyList<LanguageOption> SubtitleLanguages { get; }
    public IReadOnlyList<LanguageOption> AudioLanguages { get; }

    public string Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>Erotiek niet tonen op het beginscherm (bij "Verder kijken", zenders en opnames).</summary>
    public bool HideAdultOnHome
    {
        get => _store.Current.HideAdultOnHome;
        set
        {
            if (_store.Current.HideAdultOnHome == value) return;
            _store.Current.HideAdultOnHome = value;
            Saved();
            Raise();
        }
    }

    /// <summary>Seconden wachten op beeld voordat de app naar een reservezender schakelt (3-120).</summary>
    public int FailoverSeconds
    {
        get => _store.Current.FailoverSeconds;
        set
        {
            var v = Math.Clamp(value, 3, 120);
            if (_store.Current.FailoverSeconds == v) return;
            _store.Current.FailoverSeconds = v;
            Saved();
            Raise();
        }
    }

    /// <summary>Minuten voor het begin van een programma waarop een herinnering afgaat (0-120).</summary>
    public int ReminderMinutes
    {
        get => _store.Current.ReminderMinutes;
        set
        {
            var v = Math.Clamp(value, 0, 120);
            if (_store.Current.ReminderMinutes == v) return;
            _store.Current.ReminderMinutes = v;
            Saved();
            Raise();
        }
    }

    /// <summary>Kanaalkaarten op het beginscherm tonen een beeld van de zender zelf.</summary>
    public bool ChannelPreviews
    {
        get => _store.Current.ChannelPreviews;
        set
        {
            if (_store.Current.ChannelPreviews == value) return;
            _store.Current.ChannelPreviews = value;
            Saved();
            Raise();
        }
    }

    public LanguageOption SelectedSubtitleLanguage
    {
        get => SubtitleLanguages.FirstOrDefault(l => l.Code == _store.Current.SubtitleLanguage) ?? SubtitleLanguages[0];
        set
        {
            if (value is null || value.Code == _store.Current.SubtitleLanguage) return; // zie SelectedGroup: de ListBox/ComboBox stuurt null
            _store.Current.SubtitleLanguage = value.Code;
            Saved();
            Raise();
        }
    }

    public LanguageOption SelectedAudioLanguage
    {
        get => AudioLanguages.FirstOrDefault(l => l.Code == _store.Current.AudioLanguage) ?? AudioLanguages[0];
        set
        {
            if (value is null || value.Code == _store.Current.AudioLanguage) return;
            _store.Current.AudioLanguage = value.Code;
            Saved();
            Raise();
        }
    }

    public bool AutoSubtitles
    {
        get => _store.Current.AutoSubtitles;
        set
        {
            if (_store.Current.AutoSubtitles == value) return;
            _store.Current.AutoSubtitles = value;
            Saved();
            Raise();
        }
    }

    // ---- Opnemen ----

    /// <summary>Beheer van opnames; null als opnemen niet beschikbaar is.</summary>
    public RecordingsViewModel? Recordings { get; }
    public bool HasRecordings => Recordings is not null;

    /// <summary>Hoeveel minuten je kunt kiezen voor de marges.</summary>
    public IReadOnlyList<int> MarginOptions { get; } = new[] { 0, 1, 2, 3, 5, 10, 15, 30 };

    public int RecordBeforeMinutes
    {
        get => _store.Current.RecordBeforeMinutes;
        set
        {
            if (_store.Current.RecordBeforeMinutes == value) return;
            _store.Current.RecordBeforeMinutes = value;
            Saved();
            Raise();
        }
    }

    public int RecordAfterMinutes
    {
        get => _store.Current.RecordAfterMinutes;
        set
        {
            if (_store.Current.RecordAfterMinutes == value) return;
            _store.Current.RecordAfterMinutes = value;
            Saved();
            Raise();
        }
    }

    /// <summary>De map voor opnames. Leeg = de standaardmap (Video's\EyePTV).</summary>
    public string RecordingFolder
    {
        get => string.IsNullOrWhiteSpace(_store.Current.RecordingFolder) ? RecordingManager.DefaultFolder : _store.Current.RecordingFolder;
        set
        {
            value = (value ?? "").Trim();
            if (value.Length == 0 || string.Equals(value, RecordingManager.DefaultFolder, StringComparison.OrdinalIgnoreCase)) value = "";

            var target = value.Length == 0 ? RecordingManager.DefaultFolder : value;
            try
            {
                if (!Path.IsPathFullyQualified(target)) throw new ArgumentException("Geef een volledig pad op, bijvoorbeeld D:\\Opnames.");
                Directory.CreateDirectory(target);
                FolderError = null;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
            {
                FolderError = "Deze map is niet bruikbaar: " + ex.Message;
                Raise();
                return;
            }

            _store.Current.RecordingFolder = value;
            Saved();
            Raise();
        }
    }

    public string? FolderError
    {
        get => _folderError;
        private set
        {
            if (Set(ref _folderError, value)) Raise(nameof(HasFolderError));
        }
    }

    public bool HasFolderError => _folderError is not null;

    // ---- Downloads ----

    private string? _downloadFolderError;

    /// <summary>Het overzicht van downloads (Beheer → Downloads); null zonder downloadbeheer.</summary>
    public DownloadsViewModel? Downloads { get; }

    public bool HasDownloads => Downloads is not null;

    public string DownloadFolder
    {
        get => string.IsNullOrWhiteSpace(_store.Current.DownloadFolder) ? DownloadManager.DefaultFolder : _store.Current.DownloadFolder;
        set
        {
            value = (value ?? "").Trim();
            if (value.Length == 0 || string.Equals(value, DownloadManager.DefaultFolder, StringComparison.OrdinalIgnoreCase)) value = "";

            var target = value.Length == 0 ? DownloadManager.DefaultFolder : value;
            try
            {
                if (!Path.IsPathFullyQualified(target)) throw new ArgumentException("Geef een volledig pad op, bijvoorbeeld D:\\Films.");
                Directory.CreateDirectory(target);
                DownloadFolderError = null;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
            {
                DownloadFolderError = "Deze map is niet bruikbaar: " + ex.Message;
                Raise();
                return;
            }

            _store.Current.DownloadFolder = value;
            Saved();
            Raise();
        }
    }

    public string? DownloadFolderError
    {
        get => _downloadFolderError;
        private set
        {
            if (Set(ref _downloadFolderError, value)) Raise(nameof(HasDownloadFolderError));
        }
    }

    public bool HasDownloadFolderError => _downloadFolderError is not null;

    private void Saved()
    {
        _store.Save();
        Status = "Bewaard.";
    }
}
