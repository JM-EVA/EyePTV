using IptvPlayer.Engine;
using IptvPlayer.Engine.ViewModels;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
// Op iOS bestaat ook een naamruimte MediaPlayer (Apple); deze alias maakt duidelijk dat we de LibVLC-speler bedoelen.
using VlcPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace EyePTV.Mobile.Services;

/// <summary>Speelt streams af met LibVLC (zoals op de pc): vrijwel alle IPTV-formaten, ook .mkv.</summary>
public sealed class LibVlcPlayerService : IPlayerService, IDisposable
{
    private readonly LibVLC _libVlc;
    private long _lastPositionTick;
    private int _playGeneration;

    public LibVlcPlayerService()
    {
        LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC("--network-caching=3000", "--http-reconnect", "--no-lua");
        MediaPlayer = new VlcPlayer(_libVlc);

        MediaPlayer.EncounteredError += (_, _) =>
            MainThread.BeginInvokeOnMainThread(() => PlaybackError?.Invoke("Afspelen mislukt. De zender is mogelijk offline of de verbinding is verbroken."));
        MediaPlayer.TimeChanged += (_, _) =>
        {
            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastPositionTick) < 250) return;
            Interlocked.Exchange(ref _lastPositionTick, now);
            MainThread.BeginInvokeOnMainThread(() => PositionChanged?.Invoke());
        };
        MediaPlayer.EndReached += (_, _) => MainThread.BeginInvokeOnMainThread(() => PlaybackEnded?.Invoke());
        MediaPlayer.Playing += (_, _) => RaiseTracks();
        MediaPlayer.ESAdded += (_, _) => RaiseTracks();
        MediaPlayer.ESDeleted += (_, _) => RaiseTracks();
        MediaPlayer.ESSelected += (_, _) => RaiseTracks();
    }

    private void RaiseTracks() => MainThread.BeginInvokeOnMainThread(() => TracksChanged?.Invoke());

    /// <summary>Wordt aan de VideoView in het scherm gekoppeld.</summary>
    public VlcPlayer MediaPlayer { get; }

    public event Action<string>? PlaybackError;
    public event Action? PositionChanged;
    public event Action? PlaybackEnded;
    public event Action? TracksChanged;

    public IReadOnlyList<TrackInfo> AudioTracks => ToTracks(MediaPlayer.AudioTrackDescription);
    public IReadOnlyList<TrackInfo> SubtitleTracks => ToTracks(MediaPlayer.SpuDescription);
    public int CurrentAudioTrack => MediaPlayer.AudioTrack;
    public int CurrentSubtitleTrack => MediaPlayer.Spu;

    private static IReadOnlyList<TrackInfo> ToTracks(TrackDescription[]? d) =>
        d?.Select(t => new TrackInfo(t.Id, t.Name ?? "")).ToList() ?? new List<TrackInfo>();

    public void SetAudioTrack(int id) => MediaPlayer.SetAudioTrack(id);
    public void SetSubtitleTrack(int id) => MediaPlayer.SetSpu(id);

    public int Volume { get => MediaPlayer.Volume; set => MediaPlayer.Volume = value; }
    public bool IsMuted { get => MediaPlayer.Mute; set => MediaPlayer.Mute = value; }
    public bool IsPaused => MediaPlayer.State == VLCState.Paused;
    public long TimeMs => Math.Max(0, MediaPlayer.Time);
    public long LengthMs => Math.Max(0, MediaPlayer.Length);
    public bool IsSeekable => MediaPlayer.IsSeekable;

    public void Play(string url, string title, long startMs = 0)
    {
        // Tweede slot (de viewmodels controleren dit ook): alleen streamingprotocollen.
        if (!PlayableUrl.IsAllowed(url))
        {
            PlaybackError?.Invoke("Deze link gebruikt een protocol dat de app om veiligheidsredenen niet afspeelt.");
            return;
        }

        Interlocked.Increment(ref _playGeneration);
        using var media = new Media(_libVlc, url, FromType.FromLocation);
        if (startMs > 0) media.AddOption(":start-time=" + (startMs / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        MediaPlayer.Play(media);
    }

    public void Stop()
    {
        // LibVLC wacht bij Stop tot de verbinding is afgebroken; bij een provider die niet antwoordt bevriest anders de app.
        var generation = Volatile.Read(ref _playGeneration);
        var player = MediaPlayer;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (generation != Volatile.Read(ref _playGeneration)) return;
            try { player.Stop(); }
            catch (ObjectDisposedException) { }
        });
    }

    public void TogglePause() => MediaPlayer.Pause();

    public void SeekTo(long ms)
    {
        if (MediaPlayer.IsSeekable) MediaPlayer.Time = Math.Max(0, ms);
    }

    public void Dispose()
    {
        Interlocked.Increment(ref _playGeneration);
        MediaPlayer.Dispose();
        _libVlc.Dispose();
    }
}
