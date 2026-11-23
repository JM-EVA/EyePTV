using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IptvPlayer.Engine;

public static class HttpFactory
{
    /// <summary>HttpClient met een VLC-achtige User-Agent (sommige providers blokkeren onbekende agents).</summary>
    public static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(20)
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("VLC/3.0.21 LibVLC/3.0.21");
        return http;
    }
}

public sealed record XtreamAuth(bool Ok, string? Status, DateTime? ExpiresUtc, string? Message, string? TimeZone = null);

public sealed class XtreamClient
{
    private readonly HttpClient _http;

    public XtreamClient(HttpClient http) => _http = http;

    public static string NormalizeServer(string server)
    {
        var s = (server ?? "").Trim().TrimEnd('/');
        if (s.Length == 0) return s;
        if (!s.Contains("://")) s = "http://" + s;
        return s;
    }

    /// <summary>
    /// Herkent een link als http://host:poort/get.php?username=..&amp;password=..  (of player_api.php / xmltv.php)
    /// en haalt server + inloggegevens eruit.
    /// </summary>
    public static bool TryParseUrl(string? text, out string server, out string username, out string password)
    {
        server = username = password = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        var path = uri.AbsolutePath.ToLowerInvariant();
        if (!(path.EndsWith("/get.php") || path.EndsWith("/player_api.php") || path.EndsWith("/xmltv.php"))) return false;

        string? u = null, p = null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx <= 0) continue;
            var k = pair.Substring(0, idx).ToLowerInvariant();
            var v = Uri.UnescapeDataString(pair.Substring(idx + 1).Replace('+', ' '));
            if (k == "username") u = v;
            else if (k == "password") p = v;
        }
        if (string.IsNullOrEmpty(u) || string.IsNullOrEmpty(p)) return false;

        server = uri.GetLeftPart(UriPartial.Authority);
        username = u;
        password = p;
        return true;
    }

    public static string EpgUrl(IptvSource s) =>
        $"{NormalizeServer(s.Server ?? "")}/xmltv.php?username={Uri.EscapeDataString(s.Username ?? "")}&password={Uri.EscapeDataString(s.Password ?? "")}";

    public static string StreamUrl(IptvSource s, string streamId) =>
        $"{NormalizeServer(s.Server ?? "")}/live/{Uri.EscapeDataString(s.Username ?? "")}/{Uri.EscapeDataString(s.Password ?? "")}/{streamId}.ts";

    public static string MovieUrl(IptvSource s, string id, string? ext) =>
        $"{NormalizeServer(s.Server ?? "")}/movie/{Uri.EscapeDataString(s.Username ?? "")}/{Uri.EscapeDataString(s.Password ?? "")}/{id}.{SafeExt(ext)}";

    public static string EpisodeUrl(IptvSource s, string id, string? ext) =>
        $"{NormalizeServer(s.Server ?? "")}/series/{Uri.EscapeDataString(s.Username ?? "")}/{Uri.EscapeDataString(s.Password ?? "")}/{id}.{SafeExt(ext)}";

    /// <summary>Terugkijk-link van Xtream Codes. De starttijd moet in de tijdzone van de server staan.</summary>
    public static string TimeshiftUrl(IptvSource s, string streamId, int durationMinutes, DateTime startInServerTime) =>
        $"{NormalizeServer(s.Server ?? "")}/timeshift/{Uri.EscapeDataString(s.Username ?? "")}/{Uri.EscapeDataString(s.Password ?? "")}/{durationMinutes}/{startInServerTime.ToString("yyyy-MM-dd':'HH-mm", CultureInfo.InvariantCulture)}/{streamId}.ts";

    private static string SafeExt(string? ext) =>
        !string.IsNullOrWhiteSpace(ext) && Regex.IsMatch(ext, "^[A-Za-z0-9]{2,5}$") ? ext!.ToLowerInvariant() : "mp4";

    private static string ApiUrl(IptvSource s, string? action)
    {
        var url = $"{NormalizeServer(s.Server ?? "")}/player_api.php?username={Uri.EscapeDataString(s.Username ?? "")}&password={Uri.EscapeDataString(s.Password ?? "")}";
        return action is null ? url : url + "&action=" + action;
    }

    public async Task<XtreamAuth> AuthenticateAsync(IptvSource s, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(ApiUrl(s, null), ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("user_info", out var info) ||
            info.ValueKind != JsonValueKind.Object)
            return new XtreamAuth(false, null, null, "Onverwacht antwoord van de server.");

        var auth = GetString(info, "auth");
        var status = GetString(info, "status");
        DateTime? exp = null;
        if (long.TryParse(GetString(info, "exp_date"), out var secs) && secs > 0)
            exp = DateTimeOffset.FromUnixTimeSeconds(secs).UtcDateTime;

        string? tz = null;
        if (doc.RootElement.TryGetProperty("server_info", out var serverInfo))
            tz = GetString(serverInfo, "timezone");

        var ok = auth == "1";
        string? message = ok ? null : "Inloggen mislukt: controleer server, gebruikersnaam en wachtwoord.";
        if (ok && !string.IsNullOrEmpty(status) && !status.Equals("Active", StringComparison.OrdinalIgnoreCase))
        {
            ok = false;
            message = $"Het account heeft status '{status}'.";
        }
        return new XtreamAuth(ok, status, exp, message, string.IsNullOrWhiteSpace(tz) ? null : tz);
    }

    private async Task<Dictionary<string, string>> GetCategoriesAsync(IptvSource s, string action, CancellationToken ct)
    {
        var map = new Dictionary<string, string>();
        using var doc = await GetJsonAsync(ApiUrl(s, action), ct);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var id = GetString(el, "category_id");
                var name = GetString(el, "category_name");
                if (id != null && name != null) map[id] = name;
            }
        return map;
    }

    private static string GroupOf(Dictionary<string, string> categories, JsonElement el)
    {
        var catId = GetString(el, "category_id");
        return catId != null && categories.TryGetValue(catId, out var g) ? g.Trim() : M3uParser.DefaultGroup;
    }

    public async Task<IReadOnlyList<Channel>> GetLiveChannelsAsync(IptvSource s, CancellationToken ct = default)
    {
        var categories = await GetCategoriesAsync(s, "get_live_categories", ct);

        using var doc = await GetJsonAsync(ApiUrl(s, "get_live_streams"), ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("De server gaf geen zenderlijst terug (controleer je inloggegevens).");

        var list = new List<Channel>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var streamId = GetString(el, "stream_id");
            if (string.IsNullOrEmpty(streamId)) continue;

            var name = GetString(el, "name");
            if (string.IsNullOrWhiteSpace(name)) name = "Zender " + streamId;

            var logo = GetString(el, "stream_icon");
            var epgId = GetString(el, "epg_channel_id");

            // Terugkijken: tv_archive=1 en tv_archive_duration = aantal dagen.
            var catchupDays = 0;
            if (GetString(el, "tv_archive") == "1")
                catchupDays = int.TryParse(GetString(el, "tv_archive_duration"), out var d) && d > 0 ? d : 7;

            list.Add(new Channel(
                "xt:" + streamId,
                name.Trim(),
                GroupOf(categories, el),
                StreamUrl(s, streamId),
                string.IsNullOrWhiteSpace(logo) ? null : logo,
                string.IsNullOrWhiteSpace(epgId) ? null : epgId,
                ContentKind.Live,
                catchupDays,
                RemoteId: streamId));
        }
        return list;
    }

    public async Task<IReadOnlyList<Channel>> GetMoviesAsync(IptvSource s, CancellationToken ct = default)
    {
        var categories = await GetCategoriesAsync(s, "get_vod_categories", ct);

        using var doc = await GetJsonAsync(ApiUrl(s, "get_vod_streams"), ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("De server gaf geen filmlijst terug.");

        var list = new List<Channel>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var id = GetString(el, "stream_id");
            if (string.IsNullOrEmpty(id)) continue;

            var name = GetString(el, "name");
            if (string.IsNullOrWhiteSpace(name)) name = "Film " + id;
            var logo = GetString(el, "stream_icon");

            list.Add(new Channel(
                "xtm:" + id,
                name.Trim(),
                GroupOf(categories, el),
                MovieUrl(s, id, GetString(el, "container_extension")),
                string.IsNullOrWhiteSpace(logo) ? null : logo,
                Kind: ContentKind.Movie,
                RemoteId: id,
                Added: UnixSeconds(GetString(el, "added"))));
        }
        return list;
    }

    public async Task<IReadOnlyList<Channel>> GetSeriesAsync(IptvSource s, CancellationToken ct = default)
    {
        var categories = await GetCategoriesAsync(s, "get_series_categories", ct);

        using var doc = await GetJsonAsync(ApiUrl(s, "get_series"), ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("De server gaf geen serielijst terug.");

        var list = new List<Channel>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var id = GetString(el, "series_id");
            if (string.IsNullOrEmpty(id)) continue;

            var name = GetString(el, "name");
            if (string.IsNullOrWhiteSpace(name)) name = "Serie " + id;
            var cover = GetString(el, "cover");
            var plot = GetString(el, "plot");

            list.Add(new Channel(
                "xts:" + id,
                name.Trim(),
                GroupOf(categories, el),
                "", // een serie zelf is niet afspeelbaar; de afleveringen worden pas opgehaald als je de serie opent
                string.IsNullOrWhiteSpace(cover) ? null : cover,
                Kind: ContentKind.Series,
                RemoteId: id,
                Info: string.IsNullOrWhiteSpace(plot) ? null : plot.Trim(),
                Added: UnixSeconds(GetString(el, "last_modified"))));
        }
        return list;
    }

    // Een serie heeft geen "added"; last_modified (laatste nieuwe aflevering) komt daar het dichtst bij.
    private static long? UnixSeconds(string? s) => long.TryParse(s, out var n) && n > 0 ? n : null;

    /// <summary>Haalt de afleveringen van één serie op (pas wanneer de gebruiker de serie opent).</summary>
    public async Task<IReadOnlyList<Episode>> GetEpisodesAsync(IptvSource s, string seriesId, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(ApiUrl(s, "get_series_info") + "&series_id=" + Uri.EscapeDataString(seriesId), ct);
        var result = new List<Episode>();
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("episodes", out var episodes))
            return result;

        // De server stuurt { "1": [..], "2": [..] } of een lijst van lijsten.
        var seasons = new List<(int Season, JsonElement List)>();
        if (episodes.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in episodes.EnumerateObject())
                seasons.Add((int.TryParse(prop.Name, out var n) ? n : seasons.Count + 1, prop.Value));
        }
        else if (episodes.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var item in episodes.EnumerateArray()) seasons.Add((++i, item));
        }

        foreach (var (seasonNumber, list) in seasons)
        {
            if (list.ValueKind != JsonValueKind.Array) continue;
            foreach (var ep in list.EnumerateArray())
            {
                if (ep.ValueKind != JsonValueKind.Object) continue;
                var id = GetString(ep, "id");
                if (string.IsNullOrEmpty(id)) continue;

                var season = int.TryParse(GetString(ep, "season"), out var sn) && sn > 0 ? sn : seasonNumber;
                var number = int.TryParse(GetString(ep, "episode_num"), out var en) ? en : result.Count + 1;
                var title = GetString(ep, "title");
                if (string.IsNullOrWhiteSpace(title)) title = "Aflevering " + number;

                string? plot = null, image = null;
                int? duration = null;
                if (ep.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object)
                {
                    plot = GetString(info, "plot");
                    image = GetString(info, "movie_image");
                    if (int.TryParse(GetString(info, "duration_secs"), out var secs) && secs > 0) duration = secs;
                }

                result.Add(new Episode(
                    id, season, number, title.Trim(),
                    EpisodeUrl(s, id, GetString(ep, "container_extension")),
                    string.IsNullOrWhiteSpace(plot) ? null : plot.Trim(),
                    duration,
                    string.IsNullOrWhiteSpace(image) ? null : image));
            }
        }

        return result.OrderBy(e => e.Season).ThenBy(e => e.Number).ToList();
    }

    /// <summary>
    /// Haalt zenders, films en series op. Zenders zijn essentieel (mislukt dat, dan mislukt de sync);
    /// films en series mogen afzonderlijk mislukken zonder dat je bestaande lijst daardoor beschadigt.
    /// </summary>
    public async Task<Catalog> GetCatalogAsync(IptvSource s, CancellationToken ct = default)
    {
        string? timeZone = null;
        try { timeZone = (await AuthenticateAsync(s, ct)).TimeZone; }
        catch (OperationCanceledException) { throw; }
        catch { /* niet essentieel: zonder tijdzone werkt alleen terugkijken minder precies */ }

        var all = new List<Channel>(await GetLiveChannelsAsync(s, ct));
        var warnings = new List<string>();
        var incomplete = new List<ContentKind>();

        async Task Optional(ContentKind kind, string label, Func<Task<IReadOnlyList<Channel>>> fetch)
        {
            try { all.AddRange(await fetch()); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                incomplete.Add(kind);
                warnings.Add($"{label} konden niet worden opgehaald ({ex.Message}).");
            }
        }

        await Optional(ContentKind.Movie, "Films", () => GetMoviesAsync(s, ct));
        await Optional(ContentKind.Series, "Series", () => GetSeriesAsync(s, ct));

        return new Catalog(all, EpgUrl(s), timeZone, warnings, incomplete);
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"De server antwoordde met {(int)resp.StatusCode} {resp.ReasonPhrase}.");
        await using var stream = new LimitedReadStream(await resp.Content.ReadAsStreamAsync(ct), 600L * 1024 * 1024);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    /// <summary>Xtream-servers sturen getallen soms als tekst en soms als getal; dit vangt beide op.</summary>
    private static string? GetString(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "1",
            JsonValueKind.False => "0",
            _ => null
        };
    }
}

/// <summary>Haalt de volledige zenderlijst van een bron op.</summary>
public sealed class CatalogService
{
    private readonly HttpClient _http;

    public CatalogService(HttpClient http) => _http = http;

    public HttpClient Http => _http;

    public async Task<Catalog> FetchAsync(IptvSource source, CancellationToken ct = default)
    {
        if (source.Kind == SourceKind.Xtream)
        {
            return await new XtreamClient(_http).GetCatalogAsync(source, ct);
        }

        if (string.IsNullOrWhiteSpace(source.M3uUrl))
            throw new InvalidOperationException("Deze bron heeft geen M3U-link.");

        using var resp = await _http.GetAsync(source.M3uUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"De server antwoordde met {(int)resp.StatusCode} {resp.ReasonPhrase}.");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var playlist = await Task.Run(() => M3uParser.Parse(reader), ct);
        return new Catalog(playlist.Channels, playlist.EpgUrls.FirstOrDefault());
    }
}
