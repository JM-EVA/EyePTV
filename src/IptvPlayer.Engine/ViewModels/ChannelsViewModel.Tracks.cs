namespace IptvPlayer.Engine.ViewModels;

/// <summary>Een keuze in de lijst met audiosporen of ondertitels.</summary>
public sealed record TrackOption(int Id, string Name)
{
    public override string ToString() => Name;
}

public sealed partial class ChannelsViewModel
{
    private readonly SettingsStore? _settings;

    private IReadOnlyList<TrackOption> _audioTracks = Array.Empty<TrackOption>();
    private IReadOnlyList<TrackOption> _subtitleTracks = Array.Empty<TrackOption>();
    private TrackOption? _selectedAudio;
    private TrackOption? _selectedSubtitle;
    private bool _tracksApplied;
    private string? _subtitleHint;

    private static readonly TrackOption SubtitlesOff = new(-1, "Uit");

    // ---- Sporen in de video ----

    /// <summary>De spoor-keuzes tonen we bij films en afleveringen.</summary>
    public bool ShowTracks => IsVod;

    /// <summary>De rij met audio en ondertitels: alleen als er iets te kiezen valt (anders blijft de plek leeg, zonder uitleg).</summary>
    public bool ShowTrackRow => IsVod && (HasAudioChoice || HasSubtitleChoice);

    public IReadOnlyList<TrackOption> AudioTracks { get => _audioTracks; private set => Set(ref _audioTracks, value); }
    public IReadOnlyList<TrackOption> SubtitleTracks { get => _subtitleTracks; private set => Set(ref _subtitleTracks, value); }

    /// <summary>Er is iets te kiezen: meer dan één audiospoor.</summary>
    public bool HasAudioChoice => _audioTracks.Count > 1;

    /// <summary>Er zijn ondertitels in de video (naast "Uit").</summary>
    public bool HasSubtitleChoice => _subtitleTracks.Count > 1;

    public TrackOption? SelectedAudioTrack
    {
        get => _selectedAudio;
        set
        {
            if (value is null || Equals(value, _selectedAudio)) return; // een ComboBox stuurt null terug als de lijst wisselt
            _selectedAudio = value;
            _player.SetAudioTrack(value.Id);
            Raise();
        }
    }

    public TrackOption? SelectedSubtitleTrack
    {
        get => _selectedSubtitle;
        set
        {
            if (value is null || Equals(value, _selectedSubtitle)) return;
            _selectedSubtitle = value;
            _player.SetSubtitleTrack(value.Id);
            if (value.Id >= 0) SubtitleHint = null;
            Raise();
        }
    }

    /// <summary>"Geen ondertitel in het Nederlands in deze video", zodat duidelijk is waarom er niets te zien is.</summary>
    public string? SubtitleHint
    {
        get => _subtitleHint;
        private set
        {
            if (Set(ref _subtitleHint, value)) Raise(nameof(HasSubtitleHint));
        }
    }

    public bool HasSubtitleHint => !string.IsNullOrEmpty(_subtitleHint);

    /// <summary>Kort overzicht van wat de video aanbiedt: "2 audiosporen · 3 ondertitels".</summary>
    public string TracksSummary
    {
        get
        {
            var subs = Math.Max(0, _subtitleTracks.Count - 1);
            var a = _audioTracks.Count == 1 ? "1 audiospoor" : $"{_audioTracks.Count} audiosporen";
            var s = subs == 0 ? "geen ondertitels" : subs == 1 ? "1 ondertitel" : $"{subs} ondertitels";
            return $"{a} · {s}";
        }
    }

    private void ResetTracks()
    {
        _tracksApplied = false;
        _audioTracks = Array.Empty<TrackOption>();
        _subtitleTracks = Array.Empty<TrackOption>();
        _selectedAudio = _selectedSubtitle = null;
        _subtitleHint = null;
        RaiseTracks();
        Raise(nameof(SubtitleHint));
        Raise(nameof(HasSubtitleHint));
    }

    private void RaiseTracks()
    {
        Raise(nameof(AudioTracks));
        Raise(nameof(SubtitleTracks));
        Raise(nameof(SelectedAudioTrack));
        Raise(nameof(SelectedSubtitleTrack));
        Raise(nameof(HasAudioChoice));
        Raise(nameof(HasSubtitleChoice));
        Raise(nameof(TracksSummary));
        Raise(nameof(ShowTrackRow));
    }

    private void InitTracks() => _player.TracksChanged += OnTracksChanged;

    /// <summary>De speler heeft de sporen van de video gemeld (of ze zijn veranderd): lijst bijwerken en voorkeuren toepassen.</summary>
    private void OnTracksChanged()
    {
        if (!IsVod) return;

        var audio = _player.AudioTracks.Where(t => t.Id >= 0).Select(t => new TrackOption(t.Id, t.Name)).ToList();
        var subs = new List<TrackOption> { SubtitlesOff };
        subs.AddRange(_player.SubtitleTracks.Where(t => t.Id >= 0).Select(t => new TrackOption(t.Id, t.Name)));

        _audioTracks = audio;
        _subtitleTracks = subs;
        _selectedAudio = audio.FirstOrDefault(t => t.Id == _player.CurrentAudioTrack) ?? audio.FirstOrDefault();
        _selectedSubtitle = subs.FirstOrDefault(t => t.Id == _player.CurrentSubtitleTrack) ?? SubtitlesOff;

        // Voorkeuren één keer per item toepassen, zodra de speler de sporen kent (en niet steeds opnieuw
        // als de gebruiker daarna zelf iets kiest).
        if (!_tracksApplied && audio.Count > 0)
        {
            _tracksApplied = true;
            ApplyPreferences();
        }

        RaiseTracks();
    }

    private void ApplyPreferences()
    {
        var s = _settings?.Current;
        if (s is null) return;

        if (!string.IsNullOrEmpty(s.AudioLanguage) && _audioTracks.Count > 1)
        {
            var match = _audioTracks.FirstOrDefault(t => Languages.Matches(t.Name, s.AudioLanguage));
            if (match is not null && !Equals(match, _selectedAudio))
            {
                _selectedAudio = match;
                _player.SetAudioTrack(match.Id);
            }
        }

        if (!s.AutoSubtitles || string.IsNullOrEmpty(s.SubtitleLanguage)) return;

        var audioIsSubtitleLanguage = _selectedAudio is not null && Languages.Matches(_selectedAudio.Name, s.SubtitleLanguage);
        var sub = _subtitleTracks.Where(t => t.Id >= 0).FirstOrDefault(t => Languages.Matches(t.Name, s.SubtitleLanguage));
        if (sub is not null && !audioIsSubtitleLanguage)
        {
            _selectedSubtitle = sub;
            _player.SetSubtitleTrack(sub.Id);
            SubtitleHint = null;
        }
        else if (sub is null && !audioIsSubtitleLanguage)
        {
            SubtitleHint = $"Geen ondertitel in het {Languages.DisplayName(s.SubtitleLanguage)} in deze video.";
        }
    }
}
