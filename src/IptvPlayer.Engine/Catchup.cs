using System.Globalization;
using System.Text.RegularExpressions;

namespace IptvPlayer.Engine;

/// <summary>
/// Maakt de link om een programma terug te kijken ("catch-up"/"timeshift").
/// Ondersteund: Xtream Codes (de gebruikelijke /timeshift/-link) en M3U-bronnen met
/// catchup="shift", "append", "default" of "xc". Andere varianten (bijv. Flussonic) geven null.
/// </summary>
public static class CatchupUrl
{
    private static readonly Regex XtreamLiveUrl = new(
        @"^(?<base>https?://[^/]+)/(?:live/)?(?<user>[^/]+)/(?<pass>[^/]+)/(?<id>\d+)(?:\.\w+)?(?:\?.*)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Heeft deze zender terugkijken, en weten we hoe we de link moeten maken?</summary>
    public static bool IsAvailable(PersonalChannel channel) =>
        channel.Kind == ContentKind.Live && channel.CatchupDays > 0 && IsSupported(channel);

    /// <summary>Hetzelfde voor een zender zoals de bron die aanbiedt (nog niet in de lijst), bijvoorbeeld in het kiesscherm.</summary>
    public static bool IsAvailable(Channel channel) =>
        channel.Kind == ContentKind.Live && channel.CatchupDays > 0 && IsSupported(PersonalChannel.From("", channel, 0));

    private static bool IsSupported(PersonalChannel c)
    {
        var type = (c.CatchupType ?? "").ToLowerInvariant();
        if (c.RemoteId != null && type is "" or "xc" or "xtream") return true; // Xtream-bron
        return type switch
        {
            "shift" or "timeshift" => true,
            "append" => !string.IsNullOrWhiteSpace(c.CatchupSource),
            "default" => !string.IsNullOrWhiteSpace(c.CatchupSource) || XtreamLiveUrl.IsMatch(c.Url),
            "xc" or "xtream" => XtreamLiveUrl.IsMatch(c.Url),
            _ => false
        };
    }

    /// <summary>Kan dit programma nu worden teruggekeken (begonnen, en binnen het archief)?</summary>
    public static bool CanReplay(PersonalChannel channel, Programme programme, DateTime nowUtc) =>
        IsAvailable(channel) &&
        programme.StartUtc < nowUtc &&
        programme.StartUtc >= nowUtc.AddDays(-channel.CatchupDays);

    /// <summary>Geeft de afspeellink, of null als terugkijken voor deze zender niet kan.</summary>
    public static string? Build(IptvSource? source, PersonalChannel channel, DateTime startUtc, DateTime stopUtc, DateTime nowUtc)
    {
        if (!IsAvailable(channel)) return null;

        var minutes = Math.Max(1, (int)Math.Ceiling((stopUtc - startUtc).TotalMinutes));
        var type = (channel.CatchupType ?? "").ToLowerInvariant();

        // Xtream-bron: gewone zender met stream_id.
        if (source is { Kind: SourceKind.Xtream } && channel.RemoteId != null && type is "" or "xc" or "xtream")
            return XtreamClient.TimeshiftUrl(source, channel.RemoteId, minutes, ToServerTime(startUtc, source.ServerTimeZone));

        switch (type)
        {
            case "shift":
            case "timeshift":
            {
                var sep = channel.Url.Contains('?') ? "&" : "?";
                return $"{channel.Url}{sep}utc={Unix(startUtc)}&lutc={Unix(nowUtc)}";
            }
            case "append":
                return channel.Url + Fill(channel.CatchupSource!, startUtc, stopUtc, nowUtc);
            case "default" when !string.IsNullOrWhiteSpace(channel.CatchupSource):
                return Fill(channel.CatchupSource!, startUtc, stopUtc, nowUtc);
            case "default":
            case "xc":
            case "xtream":
            {
                // Xtream-link in een M3U: .../live/gebruiker/wachtwoord/123.ts → .../timeshift/gebruiker/wachtwoord/minuten/start/123.ts
                var m = XtreamLiveUrl.Match(channel.Url);
                if (!m.Success) return null;
                var start = ToServerTime(startUtc, source?.ServerTimeZone).ToString("yyyy-MM-dd':'HH-mm", CultureInfo.InvariantCulture);
                return $"{m.Groups["base"].Value}/timeshift/{m.Groups["user"].Value}/{m.Groups["pass"].Value}/{minutes}/{start}/{m.Groups["id"].Value}.ts";
            }
            default:
                return null;
        }
    }

    /// <summary>Zet een UTC-tijd om naar de lokale tijd van de provider (valt terug op UTC als de tijdzone onbekend is).</summary>
    public static DateTime ToServerTime(DateTime utc, string? timeZoneId)
    {
        var utcTime = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            try { return TimeZoneInfo.ConvertTimeFromUtc(utcTime, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
        return utcTime;
    }

    private static long Unix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    /// <summary>
    /// Vult de gangbare plaatshouders in, zowel als {utc} en als ${utc}:
    /// utc/start (begin, unix), utcend/end (einde), duration (seconden), offset (seconden geleden),
    /// timestamp/lutc (nu), en datumdelen Y m d H M S van de starttijd (UTC).
    /// </summary>
    internal static string Fill(string template, DateTime startUtc, DateTime stopUtc, DateTime nowUtc)
    {
        var start = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["utc"] = Unix(start).ToString(CultureInfo.InvariantCulture),
            ["start"] = Unix(start).ToString(CultureInfo.InvariantCulture),
            ["utcend"] = Unix(stopUtc).ToString(CultureInfo.InvariantCulture),
            ["end"] = Unix(stopUtc).ToString(CultureInfo.InvariantCulture),
            ["duration"] = ((long)(stopUtc - startUtc).TotalSeconds).ToString(CultureInfo.InvariantCulture),
            ["offset"] = Math.Max(0, (long)(nowUtc - startUtc).TotalSeconds).ToString(CultureInfo.InvariantCulture),
            ["timestamp"] = Unix(nowUtc).ToString(CultureInfo.InvariantCulture),
            ["lutc"] = Unix(nowUtc).ToString(CultureInfo.InvariantCulture),
            ["Y"] = start.ToString("yyyy", CultureInfo.InvariantCulture),
            ["m"] = start.ToString("MM", CultureInfo.InvariantCulture),
            ["d"] = start.ToString("dd", CultureInfo.InvariantCulture),
            ["H"] = start.ToString("HH", CultureInfo.InvariantCulture),
            ["M"] = start.ToString("mm", CultureInfo.InvariantCulture),
            ["S"] = start.ToString("ss", CultureInfo.InvariantCulture),
        };
        return Regex.Replace(template, @"\$?\{(\w+)\}", m =>
            values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
    }
}
