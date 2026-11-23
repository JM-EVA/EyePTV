using System.IO;
using System.Windows;
using System.Windows.Threading;
using IptvPlayer.Engine;
using IptvPlayer.Engine.ViewModels;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;

namespace IptvPlayer.Desktop.Services;

/// <summary>Speelt streams af met LibVLC (VLC's afspeelmotor): kan vrijwel alle IPTV-formaten en codecs aan.</summary>
public sealed class LibVlcPlayerService : IPlayerService, IDisposable
{
    private readonly LibVLC _libVlc;
    private readonly Dispatcher _dispatcher;
    private long _lastPositionTick;

    // Casten
    private readonly List<RendererItem> _renderers = new();
    private RendererDiscoverer? _discoverer;
    private RendererItem? _castItem;
    private string? _lastUrl;
    private string? _lastTitle;

    public LibVlcPlayerService()
    {
        _dispatcher = Application.Current.Dispatcher;

        // Let op: volledig gekwalificeerd, want "Core" is ook de naam van een naamruimte in andere projecten.
        LibVLCSharp.Shared.Core.Initialize();

        // Vaste keuzes, uitgeprobeerd met de zenders van de gebruiker:
        // - WaveOut als geluidsuitvoer: de standaard (WASAPI) en DirectSound klonken vervormd of haperden bij het laden;
        // - 3 seconden buffer vangt hakkelende aanvoer op;
        // - http-reconnect: kapt de provider een stream plotseling af, dan pakt LibVLC hem zelf weer op.
        // Geen eigen geluidsfilters of equalizer: die gaven een fluitend, vervormd geluid.
        // - audio-desync: WaveOut loopt een kwart seconde achter het beeld aan; een negatieve waarde laat het geluid eerder klinken.
        _libVlc = new LibVLC("--aout=waveout", "--audio-desync=-250", "--network-caching=3000", "--http-reconnect", "--no-lua");
        MediaPlayer = new MediaPlayer(_libVlc);
        MediaPlayer.EncounteredError += (_, _) =>
            _dispatcher.InvokeAsync(() => PlaybackError?.Invoke("Afspelen mislukt. De zender is mogelijk offline of de verbinding is verbroken."));

        // LibVLC meldt tijd en einde op een eigen thread; de rest van de app verwacht de UI-thread.
        // Positie-updates beperken we tot ongeveer vier per seconde.
        MediaPlayer.TimeChanged += (_, _) =>
        {
            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastPositionTick) < 250) return;
            Interlocked.Exchange(ref _lastPositionTick, now);
            _dispatcher.InvokeAsync(() => PositionChanged?.Invoke());
        };
        MediaPlayer.EndReached += (_, _) => _dispatcher.InvokeAsync(() => PlaybackEnded?.Invoke());

        // Audio- en ondertitelsporen worden pas bekend nadat het afspelen is begonnen; LibVLC meldt ze één voor één.
        MediaPlayer.Playing += (_, _) => RaiseTracksChanged();
        MediaPlayer.ESAdded += (_, _) => RaiseTracksChanged();
        MediaPlayer.ESDeleted += (_, _) => RaiseTracksChanged();
        MediaPlayer.ESSelected += (_, _) => RaiseTracksChanged();
    }

    private void RaiseTracksChanged() => _dispatcher.InvokeAsync(() => TracksChanged?.Invoke());

    /// <summary>Wordt aan de VideoView in het scherm gekoppeld.</summary>
    public MediaPlayer MediaPlayer { get; }

    /// <summary>De gedeelde LibVLC-instantie (ook voor de momentopnames van zenders).</summary>
    public LibVLC LibVlc => _libVlc;

    /// <summary>Welke lokale bestanden de speler mag openen: alleen de eigen opnames (wordt door de app ingesteld).</summary>
    public Func<string, bool>? LocalFileAllowed { get; set; }

    public event Action<string>? PlaybackError;
    public event Action? PositionChanged;
    public event Action? PlaybackEnded;
    public event Action? TracksChanged;

    public IReadOnlyList<TrackInfo> AudioTracks => ToTracks(MediaPlayer.AudioTrackDescription);
    public IReadOnlyList<TrackInfo> SubtitleTracks => ToTracks(MediaPlayer.SpuDescription);
    public int CurrentAudioTrack => MediaPlayer.AudioTrack;
    public int CurrentSubtitleTrack => MediaPlayer.Spu;

    private static IReadOnlyList<TrackInfo> ToTracks(TrackDescription[]? descriptions) =>
        descriptions?.Select(t => new TrackInfo(t.Id, t.Name ?? "")).ToList() ?? new List<TrackInfo>();

    public void SetAudioTrack(int id) => MediaPlayer.SetAudioTrack(id);

    public void SetSubtitleTrack(int id) => MediaPlayer.SetSpu(id);

    public int Volume
    {
        get => MediaPlayer.Volume;
        set => MediaPlayer.Volume = value;
    }

    public bool IsMuted
    {
        get => MediaPlayer.Mute;
        set => MediaPlayer.Mute = value;
    }

    public bool IsPaused => MediaPlayer.State == VLCState.Paused;

    public long TimeMs => Math.Max(0, MediaPlayer.Time);

    /// <summary>Bij live-tv geeft LibVLC 0 of -1: dat telt voor ons als "geen lengte".</summary>
    public long LengthMs => Math.Max(0, MediaPlayer.Length);

    public bool IsSeekable => MediaPlayer.IsSeekable;

    public void Play(string url, string title, long startMs = 0)
    {
        // Tweede slot (de viewmodels controleren dit ook): alleen streamingprotocollen, nooit smb://, file:// e.d.
        if (!PlayableUrl.IsAllowed(url) && LocalFileAllowed?.Invoke(url) != true)
        {
            PlaybackError?.Invoke("Deze link gebruikt een protocol dat de app om veiligheidsredenen niet afspeelt.");
            return;
        }

        _lastUrl = url;
        _lastTitle = title;

        Interlocked.Increment(ref _playGeneration);
        using var media = new Media(_libVlc, url, FromType.FromLocation);
        // Verder kijken: LibVLC begint direct op dit punt (in seconden, met decimalen).
        if (startMs > 0) media.AddOption(":start-time=" + (startMs / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        MediaPlayer.Play(media);
    }

    private int _playGeneration;

    public void Stop()
    {
        _lastUrl = null;
        // LibVLC wacht bij Stop tot de verbinding is afgebroken; bij een provider die niet antwoordt bevriest de app
        // dan seconden lang. Daarom op de achtergrond; begint er intussen iets nieuws, dan slaan we deze stop over.
        var generation = _playGeneration;
        var player = MediaPlayer;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            // Dispose verhoogt de teller ook: na het afsluiten raken we de speler niet meer aan.
            if (generation != Volatile.Read(ref _playGeneration)) return;
            try { player.Stop(); }
            catch (ObjectDisposedException) { /* de app sluit net af */ }
        });
    }

    // ---- Casten: Chromecast / Google TV (via LibVLC) ----

    public bool SupportsCasting => true;

    public event Action? CastTargetsChanged;

    public IReadOnlyList<CastTarget> CastTargets
    {
        get
        {
            lock (_renderers)
                return _renderers.Where(r => r.CanRenderVideo).Select(r => new CastTarget("cc:" + r.Name, r.Name, "Chromecast")).ToList();
        }
    }

    public string? CastingTo => _castItem?.Name;

    /// <summary>Zoekt Chromecasts. Pas bij het eerste gebruik van de castknop, niet bij het opstarten.</summary>
    public void StartCastDiscovery()
    {
        if (_discoverer is not null) return;
        try
        {
            var discoverer = new RendererDiscoverer(_libVlc, "microdns_renderer");
            discoverer.ItemAdded += (_, e) =>
            {
                lock (_renderers)
                    if (!_renderers.Any(r => r.Name == e.RendererItem.Name)) _renderers.Add(e.RendererItem);
                _dispatcher.InvokeAsync(() => CastTargetsChanged?.Invoke());
            };
            discoverer.ItemDeleted += (_, e) =>
            {
                lock (_renderers) _renderers.RemoveAll(r => r.Name == e.RendererItem.Name);
                _dispatcher.InvokeAsync(() => CastTargetsChanged?.Invoke());
            };
            if (discoverer.Start()) _discoverer = discoverer;
            else discoverer.Dispose();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException or VLCException)
        {
            _discoverer = null; // casten niet beschikbaar in deze LibVLC
        }
    }

    public bool SetCastTarget(string? id)
    {
        RendererItem? renderer = null;
        if (id is not null)
        {
            var name = id.StartsWith("cc:", StringComparison.Ordinal) ? id[3..] : id;
            lock (_renderers) renderer = _renderers.FirstOrDefault(r => r.Name == name && r.CanRenderVideo);
            if (renderer is null) return false;
        }

        // Het scherm kan alleen gewisseld worden terwijl de speler stilstaat: stoppen, wisselen en op dezelfde plek verder.
        var url = _lastUrl;
        var title = _lastTitle ?? "";
        var resumeAt = MediaPlayer.IsSeekable ? Math.Max(0, MediaPlayer.Time) : 0;
        var wasPlaying = url is not null && MediaPlayer.State is VLCState.Playing or VLCState.Buffering or VLCState.Paused or VLCState.Opening;
        if (wasPlaying) MediaPlayer.Stop();

        if (!MediaPlayer.SetRenderer(renderer)) return false;
        _castItem = renderer;

        if (wasPlaying) Play(url!, title, resumeAt);
        return true;
    }

    public void TogglePause() => MediaPlayer.Pause(); // Pause() schakelt tussen pauze en verder

    public void SeekTo(long ms)
    {
        if (MediaPlayer.IsSeekable) MediaPlayer.Time = Math.Max(0, ms);
    }

    public void Dispose()
    {
        Interlocked.Increment(ref _playGeneration); // een stop die nog in de wachtrij staat, vervalt
        _discoverer?.Dispose();
        MediaPlayer.Dispose();
        _libVlc.Dispose();
    }
}
