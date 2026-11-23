using System.Net;
using System.Text;
using IptvPlayer.Engine;
using IptvPlayer.Engine.ViewModels;

namespace IptvPlayer.Tests;

/// <summary>Nep-provider: antwoordt op URL's met vooraf ingestelde tekst of bytes.</summary>
public sealed class FakeHandler : HttpMessageHandler
{
    private readonly List<(Func<Uri, bool> Match, Func<byte[]> Body, HttpStatusCode Status)> _routes = new();
    public List<Uri> Requests { get; } = new();

    public FakeHandler Add(string urlContains, string body, HttpStatusCode status = HttpStatusCode.OK) =>
        Add(urlContains, () => Encoding.UTF8.GetBytes(body), status);

    public FakeHandler Add(string urlContains, Func<string> body, HttpStatusCode status = HttpStatusCode.OK) =>
        Add(urlContains, () => Encoding.UTF8.GetBytes(body()), status);

    public FakeHandler Add(string urlContains, Func<byte[]> body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes.Insert(0, (u => u.ToString().Contains(urlContains, StringComparison.Ordinal), body, status));
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request.RequestUri!);
        foreach (var r in _routes)
            if (r.Match(request.RequestUri!))
                return Task.FromResult(new HttpResponseMessage(r.Status) { Content = new ByteArrayContent(r.Body()) });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

public sealed class FakePlayer : IPlayerService
{
    public List<string> Played { get; } = new();
    public List<string> Titles { get; } = new();
    public List<long> StartPositions { get; } = new();
    public List<long> Seeks { get; } = new();
    public int StopCount { get; private set; }
    public int Volume { get; set; }
    public bool IsMuted { get; set; }
    public bool IsPaused { get; private set; }
    public long TimeMs { get; set; }
    public long LengthMs { get; set; }
    public bool IsSeekable { get; set; } = true;

    public event Action? PositionChanged;
    public event Action? PlaybackEnded;
    public event Action<string>? PlaybackError;
    public event Action? TracksChanged;

    // Audio- en ondertitelsporen
    public List<TrackInfo> Audio { get; } = new();
    public List<TrackInfo> Subs { get; } = new();
    public int AudioChoice { get; private set; } = -1;
    public int SubChoice { get; private set; } = -1;
    public List<string> AddedSubtitles { get; } = new();
    public IReadOnlyList<TrackInfo> AudioTracks => Audio;
    public IReadOnlyList<TrackInfo> SubtitleTracks => Subs;
    public int CurrentAudioTrack => AudioChoice;
    public int CurrentSubtitleTrack => SubChoice;
    public void SetAudioTrack(int id) => AudioChoice = id;
    public void SetSubtitleTrack(int id) => SubChoice = id;
    public void AddSubtitle(string path)
    {
        AddedSubtitles.Add(path);
        Subs.Add(new TrackInfo(100 + Subs.Count, "Toegevoegd"));
        SubChoice = Subs[^1].Id;
        TracksChanged?.Invoke();
    }

    /// <summary>Simuleert dat de speler zijn sporen heeft gemeld.</summary>
    public void RaiseTracks(int audioChoice = 0, int subChoice = -1)
    {
        AudioChoice = audioChoice;
        SubChoice = subChoice;
        TracksChanged?.Invoke();
    }

    public void Play(string url, string title, long startMs = 0)
    {
        Played.Add(url);
        Titles.Add(title);
        StartPositions.Add(startMs);
        TimeMs = startMs;
        IsPaused = false;
    }

    // Casten
    public bool SupportsCasting => true;
    public List<CastTarget> Targets { get; } = new();
    public bool DiscoveryStarted { get; private set; }
    public string? CastingTo { get; private set; }
    public IReadOnlyList<CastTarget> CastTargets => Targets;
    public event Action? CastTargetsChanged;
    public void StartCastDiscovery() => DiscoveryStarted = true;
    public void RaiseTargets() => CastTargetsChanged?.Invoke();
    public bool SetCastTarget(string? id)
    {
        if (id is null) { CastingTo = null; return true; }
        var target = Targets.FirstOrDefault(t => t.Id == id);
        if (target is null) return false;
        CastingTo = target.Name;
        return true;
    }

    public void Stop() => StopCount++;
    public void TogglePause() => IsPaused = !IsPaused;
    public void SeekTo(long ms) { Seeks.Add(ms); TimeMs = ms; }

    public void RaiseError(string msg) => PlaybackError?.Invoke(msg);

    /// <summary>Simuleert dat de speler een nieuwe positie meldt.</summary>
    public void Tick(long timeMs, long lengthMs)
    {
        TimeMs = timeMs;
        LengthMs = lengthMs;
        PositionChanged?.Invoke();
    }

    public void End() => PlaybackEnded?.Invoke();
}

public sealed class FakeDialogs : IDialogService
{
    public bool ConfirmAnswer { get; set; } = true;
    public List<string> Errors { get; } = new();
    public List<string> Infos { get; } = new();

    public ExitChoice ExitAnswer { get; set; } = ExitChoice.Quit;
    public List<string> ExitQuestions { get; } = new();
    public ExitChoice AskExit(string message) { ExitQuestions.Add(message); return ExitAnswer; }

    public void ShowInfo(string title, string message) => Infos.Add(message);
    public void ShowError(string title, string message) => Errors.Add(message);
    public bool Confirm(string title, string message) => ConfirmAnswer;

    public string? TextAnswer { get; set; }
    public List<string> TextQuestions { get; } = new();
    public string? AskText(string title, string message, string initial) { TextQuestions.Add(initial); return TextAnswer; }

    public IReadOnlyList<string>? PickAnswer { get; set; }
    public List<IReadOnlyList<ChannelChoice>> PickQuestions { get; } = new();
    public IReadOnlyList<string>? PickChannels(string title, string message, IReadOnlyList<ChannelChoice> options, IReadOnlyList<string> selected)
    { PickQuestions.Add(options); return PickAnswer; }
}

public sealed class FakeNavigator : INavigator
{
    public string Last { get; private set; } = "";
    public SelectionViewModel? Selection { get; private set; }

    public void ShowChannels() => Last = "channels";
    public void ShowSources() => Last = "sources";
    public void ShowSelection(SelectionViewModel selection)
    {
        Last = "selection";
        Selection = selection;
    }
}

/// <summary>Tijdelijke opslagmap die na afloop wordt opgeruimd.</summary>
public sealed class TempStore : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "iptvplayer-test-" + Guid.NewGuid().ToString("N"));
    public AppStore Store { get; }

    public TempStore() => Store = new AppStore(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { }
    }
}

public static class Samples
{
    public static string M3u(params (string id, string name, string group)[] channels)
    {
        var sb = new StringBuilder("#EXTM3U url-tvg=\"http://epg.test/guide.xml\"\n");
        foreach (var (id, name, group) in channels)
        {
            sb.Append($"#EXTINF:-1 tvg-id=\"{id}\" tvg-logo=\"http://logo.test/{id}.png\" group-title=\"{group}\",{name}\n");
            sb.Append($"http://stream.test/{Uri.EscapeDataString(name)}.ts\n");
        }
        return sb.ToString();
    }
}
