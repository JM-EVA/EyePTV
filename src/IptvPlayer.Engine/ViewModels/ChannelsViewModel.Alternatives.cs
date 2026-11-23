using System.Text.RegularExpressions;

namespace IptvPlayer.Engine.ViewModels;

/// <summary>Een andere stream van (dezelfde) zender.</summary>
public sealed record StreamOption(string Title, string Url);

public sealed partial class ChannelsViewModel
{
    private const int MaxAlternatives = 12;
    private static readonly TimeSpan IndexMaxAge = TimeSpan.FromMinutes(10);

    private sealed record NameIndex(DateTime BuiltUtc, Dictionary<string, List<Channel>> ByName, Dictionary<string, List<Channel>> ByEpg);

    private readonly Dictionary<string, NameIndex> _nameIndex = new();
    private IReadOnlyList<StreamOption> _candidates = Array.Empty<StreamOption>();
    private string? _alternativeUrl;
    private bool _switching;
    private int _alternativesRequest;

    /// <summary>De andere streams van de zender die nu speelt (niet degene die nu speelt zelf).</summary>
    public IReadOnlyList<StreamOption> Alternatives
    {
        get
        {
            if (_mode != PlayMode.Live || Current is not { } row) return Array.Empty<StreamOption>();
            var playing = _alternativeUrl ?? row.Model.Url;
            return _candidates.Where(c => c.Url != playing).Take(MaxAlternatives).ToList();
        }
    }

    public bool HasAlternatives => Alternatives.Count > 0;

    /// <summary>Het aantal andere streams, voor de subtiele knop.</summary>
    public string AlternativesText => "⇄ " + Alternatives.Count;

    private void RaiseAlternatives()
    {
        Raise(nameof(Alternatives));
        Raise(nameof(HasAlternatives));
        Raise(nameof(AlternativesText));
    }

    // ---- Zoeken ----

    private static readonly Regex Brackets = new(@"[\(\[\{][^\)\]\}]*[\)\]\}]", RegexOptions.Compiled);
    private static readonly Regex CountryPrefix = new(@"^\s*[a-z]{2,3}\s*[:|\-]\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly HashSet<string> QualityWords = new(StringComparer.OrdinalIgnoreCase)
        { "hd", "fhd", "uhd", "sd", "4k", "hq", "lq", "hevc", "h265", "h264", "raw", "50fps", "60fps", "1080p", "720p", "backup", "alt" };

    /// <summary>
    /// De naam van een zender zonder kwaliteitslabels, landvoorvoegsel en haakjes, zodat "NL: NPO 1 HD" en "NPO 1" gelijk zijn.
    /// </summary>
    internal static string ChannelKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var s = Brackets.Replace(name, " ");
        s = CountryPrefix.Replace(s, "");
        var words = s.Split(new[] { ' ', '\t', '-', '_', '|', '.' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !QualityWords.Contains(w));
        return TextUtil.NormalizeLoose(string.Join(" ", words));
    }

    private NameIndex IndexFor(IptvSource source)
    {
        lock (_nameIndex)
        {
            if (_nameIndex.TryGetValue(source.Id, out var cached) && _utcNow() - cached.BuiltUtc < IndexMaxAge) return cached;
        }

        var byName = new Dictionary<string, List<Channel>>();
        var byEpg = new Dictionary<string, List<Channel>>();
        foreach (var c in _library.LoadCatalog(source.Id)?.Channels ?? Array.Empty<Channel>())
        {
            if (c.Kind != ContentKind.Live) continue;
            var key = ChannelKey(c.Name);
            if (key.Length > 0) (byName.TryGetValue(key, out var l) ? l : byName[key] = new()).Add(c);
            if (!string.IsNullOrWhiteSpace(c.EpgId))
            {
                var epg = TextUtil.NormalizeLoose(c.EpgId);
                (byEpg.TryGetValue(epg, out var e) ? e : byEpg[epg] = new()).Add(c);
            }
        }

        var index = new NameIndex(_utcNow(), byName, byEpg);
        lock (_nameIndex) _nameIndex[source.Id] = index;
        return index;
    }

    private List<StreamOption> FindCandidates(PersonalChannel model)
    {
        var result = new List<StreamOption> { new(OptionTitle(model.DisplayName, model.Group), model.Url) };
        var seen = new HashSet<string> { model.Url };
        var key = ChannelKey(model.Name);
        var epg = string.IsNullOrWhiteSpace(model.EpgId) ? null : TextUtil.NormalizeLoose(model.EpgId);

        void Add(Channel c)
        {
            if (c.Kind != ContentKind.Live || !PlayableUrl.IsAllowed(c.Url) || !seen.Add(c.Url)) return;
            if (!_lock.IsUnlocked && AdultContent.IsAdult(c.Group, c.Name)) return;
            result.Add(new StreamOption(OptionTitle(c.Name, c.Group), c.Url));
        }

        // Eerst wat al in je eigen lijst staat (zelfde zender in een andere groep), daarna de hele catalogus van elke bron.
        foreach (var p in _library.Personal.Where(p => p.Kind == ContentKind.Live && p.Id != model.Id))
            if (key.Length > 0 && ChannelKey(p.Name) == key || epg is not null && TextUtil.NormalizeLoose(p.EpgId) == epg)
                Add(new Channel(p.Key, p.Name, p.Group, p.Url, Kind: p.Kind));

        foreach (var source in _library.Sources)
        {
            var index = IndexFor(source);
            if (key.Length > 0 && index.ByName.TryGetValue(key, out var named)) foreach (var c in named) Add(c);
            if (epg is not null && index.ByEpg.TryGetValue(epg, out var epgMatches)) foreach (var c in epgMatches) Add(c);
        }
        return result;
    }

    private static string OptionTitle(string name, string group) => string.IsNullOrWhiteSpace(group) ? name : $"{name}  ·  {group}";

    /// <summary>Zoekt (op de achtergrond) de andere streams van de zender die net begon te spelen.</summary>
    private void StartAlternativesLookup(PersonalChannel model)
    {
        var request = ++_alternativesRequest;
        _candidates = Array.Empty<StreamOption>();
        RaiseAlternatives();
        _ = LookupAsync(model, request);
    }

    private async Task LookupAsync(PersonalChannel model, int request)
    {
        List<StreamOption> found;
        try { found = await Task.Run(() => FindCandidates(model)); }
        catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException or InvalidOperationException)
        {
            return; // geen catalogus te lezen: dan zijn er geen alternatieven
        }

        if (request != _alternativesRequest) return; // intussen een andere zender
        _candidates = found;
        RaiseAlternatives();
        PreferHdWhenCasting();
    }

    // ---- Casten naar een tv: de meeste kunnen geen 4K/HEVC, dan de HD-versie van dezelfde zender ----

    private static readonly Regex FourK = new(@"(?<![a-z0-9])(4k|uhd|2160p?|hevc|h\.?265)(?![a-z0-9])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FullHd = new(@"(?<![a-z0-9])(fhd|1080[ip]?)(?![a-z0-9])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Hd = new(@"(?<![a-z0-9])(hd|720p?)(?![a-z0-9])", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// De meeste tv's en een gewone Chromecast spelen geen 4K of HEVC, en omzetten kost je pc veel moeite. Staat dezelfde zender er ook in HD,
    /// dan schakelen we vanzelf naar die stream.
    /// </summary>
    private void PreferHdWhenCasting()
    {
        if (_mode != PlayMode.Live || Current is not { } row || CastingTo is null) return;

        var playingUrl = _alternativeUrl ?? row.Model.Url;
        var playingTitle = _candidates.FirstOrDefault(c => c.Url == playingUrl)?.Title ?? $"{row.Model.Name} {row.Model.Group}";
        if (!FourK.IsMatch(playingTitle)) return;

        var pick = _candidates
            .Where(c => c.Url != playingUrl && !FourK.IsMatch(c.Title))
            .OrderBy(c => FullHd.IsMatch(c.Title) ? 0 : Hd.IsMatch(c.Title) ? 1 : 2)
            .FirstOrDefault();
        if (pick is null) return;

        PlayAlternative(pick);
        StatusText = $"{row.Name} is 4K; voor het casten is de HD-versie gekozen.";
    }

    // ---- Wisselen ----

    /// <summary>Speelt dezelfde zender via een andere stream. De zender in je lijst blijft ongewijzigd.</summary>
    public void PlayAlternative(StreamOption option)
    {
        if (_mode != PlayMode.Live || Current is not { IsLive: true } row) return;
        if (!PlayableUrl.IsAllowed(option.Url))
        {
            StatusText = "Deze link gebruikt een protocol dat de app om veiligheidsredenen niet afspeelt.";
            return;
        }

        _alternativeUrl = option.Url == row.Model.Url ? null : option.Url;
        _switching = true;
        try { Play(row); }
        finally { _switching = false; }

        StatusText = $"{row.Name} via een andere stream";
        RaiseAlternatives();
    }
}
