using System.Text;
using System.Text.RegularExpressions;

namespace IptvPlayer.Engine;

public sealed record M3uPlaylist(IReadOnlyList<Channel> Channels, IReadOnlyList<string> EpgUrls);

public static class M3uParser
{
    public const string DefaultGroup = "Overig";

    private static readonly Regex AttrRx = new(@"([\w\-\.]+)\s*=\s*""([^""]*)""", RegexOptions.Compiled);

    public static M3uPlaylist Parse(string text)
    {
        using var reader = new StringReader(text);
        return Parse(reader);
    }

    public static M3uPlaylist Parse(TextReader reader)
    {
        var channels = new List<Channel>();
        var epgUrls = new List<string>();
        var usedKeys = new Dictionary<string, int>();

        ExtInf? pending = null;
        string? extGroup = null;

        string? raw;
        while ((raw = reader.ReadLine()) != null)
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Match m in AttrRx.Matches(line))
                {
                    var name = m.Groups[1].Value.ToLowerInvariant();
                    if (name is "url-tvg" or "x-tvg-url" or "tvg-url")
                        foreach (var u in m.Groups[2].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            if (!epgUrls.Contains(u)) epgUrls.Add(u);
                }
                continue;
            }

            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                pending = ParseExtInf(line);
                extGroup = null;
                continue;
            }

            if (line.StartsWith("#EXTGRP:", StringComparison.OrdinalIgnoreCase))
            {
                extGroup = line.Substring(8).Trim();
                continue;
            }

            if (line[0] == '#') continue; // #EXTVLCOPT e.d. negeren

            // Dit is een URL-regel.
            if (pending is null) continue;

            // Alleen streamingprotocollen: een afspeellijst van een derde mag geen smb://, file:// e.d. in je lijst zetten.
            if (!PlayableUrl.IsAllowed(line))
            {
                pending = null;
                extGroup = null;
                continue;
            }

            var group = !string.IsNullOrWhiteSpace(pending.Group) ? pending.Group!
                      : !string.IsNullOrWhiteSpace(extGroup) ? extGroup!
                      : DefaultGroup;

            var key = BuildKey(pending.TvgId, pending.Name);
            if (usedKeys.TryGetValue(key, out var n))
            {
                usedKeys[key] = n + 1;
                key = key + "#" + (n + 1);
            }
            else usedKeys[key] = 1;

            var kind = DetectKind(line);
            var catchupDays = kind == ContentKind.Live ? pending.CatchupDays : 0;

            channels.Add(new Channel(
                key,
                pending.Name,
                group.Trim(),
                line,
                string.IsNullOrWhiteSpace(pending.Logo) ? null : pending.Logo,
                string.IsNullOrWhiteSpace(pending.TvgId) ? null : pending.TvgId,
                kind,
                catchupDays,
                catchupDays > 0 ? pending.CatchupType : null,
                catchupDays > 0 ? pending.CatchupSource : null));

            pending = null;
            extGroup = null;
        }

        return new M3uPlaylist(channels, epgUrls);
    }

    private static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".flv", ".mpg", ".mpeg" };

    /// <summary>
    /// Een gewone M3U zegt niet of iets een film is. We herkennen het aan de link: Xtream-achtige
    /// /movie/ en /series/ paden, of een videobestand als bestandsextensie. Al het andere is live-tv.
    /// (Series staan in een M3U als losse afleveringen en tellen hier dus als film.)
    /// </summary>
    public static ContentKind DetectKind(string url)
    {
        var path = url;
        var q = path.IndexOfAny(new[] { '?', '#' });
        if (q >= 0) path = path.Substring(0, q);

        if (path.Contains("/movie/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/series/", StringComparison.OrdinalIgnoreCase))
            return ContentKind.Movie;

        foreach (var ext in VideoExtensions)
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return ContentKind.Movie;

        return ContentKind.Live;
    }

    public static string BuildKey(string? tvgId, string name) =>
        (tvgId ?? "").Trim().ToLowerInvariant() + "|" + TextUtil.Normalize(name);

    private sealed record ExtInf(string Name, string? TvgId, string? Logo, string? Group,
        int CatchupDays, string? CatchupType, string? CatchupSource);

    private static ExtInf ParseExtInf(string line)
    {
        // #EXTINF:-1 tvg-id="x" group-title="A, B",Zendernaam
        var body = line.Substring(line.IndexOf(':') + 1);

        // De eerste komma buiten aanhalingstekens scheidt attributen van de naam.
        var inQuote = false;
        var comma = -1;
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] == '"') inQuote = !inQuote;
            else if (body[i] == ',' && !inQuote) { comma = i; break; }
        }

        var attrPart = comma >= 0 ? body.Substring(0, comma) : body;
        var name = comma >= 0 ? body.Substring(comma + 1).Trim() : "";

        string? tvgId = null, tvgName = null, logo = null, group = null;
        string? catchupType = null, catchupSource = null;
        var catchupDays = 0;
        foreach (Match m in AttrRx.Matches(attrPart))
        {
            var v = m.Groups[2].Value;
            switch (m.Groups[1].Value.ToLowerInvariant())
            {
                case "tvg-id": tvgId = v; break;
                case "tvg-name": tvgName = v; break;
                case "tvg-logo": logo = v; break;
                case "group-title": group = v; break;
                case "catchup": case "catchup-type": catchupType = v.Trim().ToLowerInvariant(); break;
                case "catchup-source": catchupSource = v; break;
                case "catchup-days": case "tvg-rec":
                    if (int.TryParse(v, out var d) && d > 0) catchupDays = d;
                    break;
            }
        }

        // "catchup" zonder aantal dagen: neem een gangbare standaard.
        if (catchupDays == 0 && !string.IsNullOrEmpty(catchupType) && catchupType != "none") catchupDays = 7;
        if (catchupType == "none") catchupDays = 0;

        if (string.IsNullOrWhiteSpace(name)) name = tvgName ?? "";
        if (string.IsNullOrWhiteSpace(name)) name = "Onbekende zender";

        return new ExtInf(name, tvgId, logo, group, catchupDays, catchupType, catchupSource);
    }
}

public static class M3uWriter
{
    public static string Write(IEnumerable<PersonalChannel> channels)
    {
        var sb = new StringBuilder("#EXTM3U\n");
        foreach (var c in channels)
        {
            sb.Append("#EXTINF:-1");
            if (!string.IsNullOrEmpty(c.EpgId)) sb.Append(" tvg-id=\"").Append(Esc(c.EpgId)).Append('"');
            if (!string.IsNullOrEmpty(c.LogoUrl)) sb.Append(" tvg-logo=\"").Append(Esc(c.LogoUrl)).Append('"');
            sb.Append(" group-title=\"").Append(Esc(c.Group)).Append("\",").Append(c.Name).Append('\n');
            sb.Append(c.Url).Append('\n');
        }
        return sb.ToString();
    }

    private static string Esc(string s) => s.Replace('"', '\'');
}
