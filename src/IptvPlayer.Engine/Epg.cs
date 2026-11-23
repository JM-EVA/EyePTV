using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace IptvPlayer.Engine;

/// <summary>EPG-gegevens (alleen voor zenders in de persoonlijke lijst).</summary>
public sealed class EpgData
{
    public DateTime FetchedUtc { get; set; }
    public Dictionary<string, List<Programme>> Programmes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Genormaliseerde zendernaam (alleen letters/cijfers) → kanaal-id, om zenders zonder tvg-id te koppelen.</summary>
    public Dictionary<string, string> Aliases { get; } = new();

    public static EpgData Empty => new();

    public string? Resolve(PersonalChannel c)
    {
        if (!string.IsNullOrWhiteSpace(c.EpgId) && Programmes.ContainsKey(c.EpgId)) return c.EpgId;
        var loose = TextUtil.NormalizeLoose(c.Name);
        if (loose.Length > 0 && Aliases.TryGetValue(loose, out var id) && Programmes.ContainsKey(id)) return id;
        return null;
    }

    public (Programme? Now, Programme? Next) NowNext(PersonalChannel c, DateTime nowUtc)
    {
        var id = Resolve(c);
        if (id == null) return (null, null);
        Programme? now = null, next = null;
        foreach (var p in Programmes[id]) // gesorteerd op starttijd
        {
            if (p.StartUtc <= nowUtc && p.StopUtc > nowUtc) now = p;
            else if (p.StartUtc > nowUtc) { next = p; break; }
        }
        return (now, next);
    }

    public IReadOnlyList<Programme> Schedule(PersonalChannel c, DateTime fromUtc, DateTime toUtc)
    {
        var id = Resolve(c);
        if (id == null) return Array.Empty<Programme>();
        return Programmes[id].Where(p => p.StopUtc > fromUtc && p.StartUtc < toUtc).ToList();
    }

    public void Merge(EpgData other)
    {
        foreach (var (id, list) in other.Programmes)
        {
            if (!Programmes.TryGetValue(id, out var existing)) Programmes[id] = existing = new List<Programme>();
            existing.AddRange(list);
            existing.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
        }
        foreach (var (name, id) in other.Aliases) Aliases[name] = id;
        if (other.FetchedUtc > FetchedUtc) FetchedUtc = other.FetchedUtc;
    }
}

public static class XmltvParser
{
    /// <summary>
    /// Leest een XMLTV-bestand en bewaart alleen programma's voor de gevraagde kanalen
    /// (op id of op zendernaam) binnen het opgegeven tijdvenster.
    /// </summary>
    public static EpgData Parse(Stream stream, ISet<string> wantedIds, ISet<string> wantedLooseNames,
        DateTime windowStartUtc, DateTime windowEndUtc)
    {
        var data = new EpgData { FetchedUtc = DateTime.UtcNow };
        var accepted = new HashSet<string>(wantedIds, StringComparer.OrdinalIgnoreCase);

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            XmlResolver = null,
            CheckCharacters = false
        };
        using var xr = XmlReader.Create(stream, settings);
        xr.MoveToContent();

        while (!xr.EOF)
        {
            if (xr.NodeType == XmlNodeType.Element && xr.Name == "channel")
            {
                var el = (XElement)XNode.ReadFrom(xr);
                var id = (string?)el.Attribute("id");
                if (string.IsNullOrEmpty(id)) continue;
                foreach (var dn in el.Elements("display-name"))
                {
                    var loose = TextUtil.NormalizeLoose(dn.Value);
                    if (loose.Length == 0) continue;
                    if (wantedLooseNames.Contains(loose))
                    {
                        accepted.Add(id);
                        data.Aliases[loose] = id;
                    }
                }
            }
            else if (xr.NodeType == XmlNodeType.Element && xr.Name == "programme")
            {
                var el = (XElement)XNode.ReadFrom(xr);
                var channel = (string?)el.Attribute("channel");
                if (string.IsNullOrEmpty(channel) || !accepted.Contains(channel)) continue;

                if (!TryParseTime((string?)el.Attribute("start"), out var start)) continue;
                if (!TryParseTime((string?)el.Attribute("stop"), out var stop)) continue;
                if (stop <= windowStartUtc || start >= windowEndUtc) continue;

                var title = el.Element("title")?.Value?.Trim();
                if (string.IsNullOrEmpty(title)) title = "(geen titel)";
                var desc = el.Element("desc")?.Value?.Trim();

                if (!data.Programmes.TryGetValue(channel, out var list))
                    data.Programmes[channel] = list = new List<Programme>();
                list.Add(new Programme(channel, start, stop, title, string.IsNullOrEmpty(desc) ? null : desc));
            }
            else
            {
                xr.Read();
            }
        }

        foreach (var list in data.Programmes.Values) list.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
        return data;
    }

    private static readonly Regex TimeRx = new(@"^\s*(\d{12,14})\s*([+-]\d{4})?\s*$", RegexOptions.Compiled);

    /// <summary>Verwerkt tijden als "20261026203000 +0100" (zonder tijdzone wordt UTC aangenomen).</summary>
    public static bool TryParseTime(string? s, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var m = TimeRx.Match(s);
        if (!m.Success) return false;

        var digits = m.Groups[1].Value;
        var format = digits.Length == 14 ? "yyyyMMddHHmmss" : "yyyyMMddHHmm";
        if (digits.Length != 14 && digits.Length != 12) return false;
        if (!DateTime.TryParseExact(digits, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return false;

        var offset = TimeSpan.Zero;
        if (m.Groups[2].Success)
        {
            var o = m.Groups[2].Value;
            var h = int.Parse(o.Substring(1, 2), CultureInfo.InvariantCulture);
            var min = int.Parse(o.Substring(3, 2), CultureInfo.InvariantCulture);
            offset = new TimeSpan(h, min, 0);
            if (o[0] == '-') offset = -offset;
        }
        utc = DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        return true;
    }
}

public sealed class EpgService
{
    private const string CacheFile = "epg.json";
    private readonly HttpClient _http;
    private readonly AppStore _store;

    public EpgData Data { get; private set; } = EpgData.Empty;

    public EpgService(HttpClient http, AppStore store)
    {
        _http = http;
        _store = store;
    }

    public void LoadCache()
    {
        var dto = _store.Load<EpgCacheDto?>(CacheFile, () => null);
        if (dto == null) return;
        var data = new EpgData { FetchedUtc = dto.FetchedUtc };
        foreach (var (id, list) in dto.Programmes) data.Programmes[id] = list;
        foreach (var (name, id) in dto.Aliases) data.Aliases[name] = id;
        Data = data;
    }

    /// <summary>
    /// Downloadt de EPG van elke bron die zenders in de persoonlijke lijst heeft en bewaart
    /// alleen de programma's voor die zenders. Geeft de foutmeldingen per bron terug.
    /// </summary>
    public async Task<IReadOnlyList<string>> RefreshAsync(
        IEnumerable<IptvSource> sources, IReadOnlyList<PersonalChannel> personal, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var combined = new EpgData { FetchedUtc = DateTime.UtcNow };
        // Wie terugkijken heeft, moet ook de programma's van de afgelopen dagen kunnen zien (maximaal 14 dagen).
        var archiveDays = personal.Where(p => p.Kind == ContentKind.Live).Select(p => p.CatchupDays).DefaultIfEmpty(0).Max();
        var from = DateTime.UtcNow.AddHours(-Math.Max(6, Math.Min(archiveDays, 14) * 24));
        var to = DateTime.UtcNow.AddDays(3);
        var any = false;

        foreach (var source in sources)
        {
            var url = source.EffectiveEpgUrl;
            var mine = personal.Where(p => p.SourceId == source.Id && p.Kind == ContentKind.Live).ToList();
            if (string.IsNullOrWhiteSpace(url) || mine.Count == 0) continue;

            var ids = new HashSet<string>(mine.Where(p => !string.IsNullOrWhiteSpace(p.EpgId)).Select(p => p.EpgId!),
                StringComparer.OrdinalIgnoreCase);
            var names = new HashSet<string>(mine.Select(p => TextUtil.NormalizeLoose(p.Name)).Where(n => n.Length > 0));

            try
            {
                var part = await DownloadAndParseAsync(url!, ids, names, from, to, ct);
                combined.Merge(part);
                any = true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                errors.Add($"{source.Name}: {ex.Message}");
            }
        }

        if (any && combined.Programmes.Count == 0)
        {
            // Een lege EPG (server tijdelijk leeg of niets gevonden) mag een goede bewaarde EPG niet overschrijven.
            errors.Add("de EPG bevatte geen programma's voor je zenders");
            any = false;
        }

        if (any)
        {
            Data = combined;
            _store.Save(CacheFile, new EpgCacheDto
            {
                FetchedUtc = combined.FetchedUtc,
                Programmes = combined.Programmes.ToDictionary(k => k.Key, v => v.Value),
                Aliases = combined.Aliases.ToDictionary(k => k.Key, v => v.Value)
            }, indented: false);
        }
        return errors;
    }

    private const long MaxDownloadBytes = 500L * 1024 * 1024;
    private const long MaxUnpackedBytes = 1536L * 1024 * 1024;

    private async Task<EpgData> DownloadAndParseAsync(string url, ISet<string> ids, ISet<string> names,
        DateTime from, DateTime to, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "iptvplayer-epg-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!resp.IsSuccessStatusCode)
                    throw new HttpRequestException($"EPG-server antwoordde met {(int)resp.StatusCode} {resp.ReasonPhrase}.");
                await using var net = new LimitedReadStream(await resp.Content.ReadAsStreamAsync(ct), MaxDownloadBytes);
                await using var file = File.Create(tmp);
                await net.CopyToAsync(file, ct);
            }

            return await Task.Run(() =>
            {
                using var fs = File.OpenRead(tmp);
                var b1 = fs.ReadByte();
                var b2 = fs.ReadByte();
                fs.Position = 0;
                // Ook uitgepakt een grens: een klein gzip-bestand kan duizenden keren groter worden ("zip-bom").
                Stream input = (b1 == 0x1F && b2 == 0x8B)
                    ? new LimitedReadStream(new GZipStream(fs, CompressionMode.Decompress), MaxUnpackedBytes)
                    : fs;
                using (input) return XmltvParser.Parse(input, ids, names, from, to);
            }, ct);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* tijdelijk bestand, niet erg */ }
        }
    }

    private sealed class EpgCacheDto
    {
        public DateTime FetchedUtc { get; set; }
        public Dictionary<string, List<Programme>> Programmes { get; set; } = new();
        public Dictionary<string, string> Aliases { get; set; } = new();
    }
}
