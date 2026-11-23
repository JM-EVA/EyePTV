using System.Text.Json.Serialization;

namespace IptvPlayer.Engine;

public enum SourceKind
{
    M3uUrl,
    Xtream
}

/// <summary>Een IPTV-bron (M3U-link of Xtream Codes-account).</summary>
public sealed class IptvSource
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public SourceKind Kind { get; set; }

    // M3U
    public string? M3uUrl { get; set; }

    // Xtream Codes
    public string? Server { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary>Optioneel: eigen XMLTV-link (heeft voorrang op wat de bron zelf aangeeft).</summary>
    public string? EpgUrl { get; set; }

    /// <summary>EPG-link die de bron zelf meegaf (url-tvg in de M3U of xmltv.php bij Xtream).</summary>
    public string? DiscoveredEpgUrl { get; set; }

    /// <summary>False totdat de gebruiker voor het eerst zijn selectie heeft gemaakt.</summary>
    public bool InitialImportDone { get; set; }

    public DateTime? LastSyncUtc { get; set; }

    /// <summary>Tijdzone van de provider (nodig om "terugkijken"-links met de juiste starttijd te maken).</summary>
    public string? ServerTimeZone { get; set; }

    [JsonIgnore]
    public string? EffectiveEpgUrl =>
        !string.IsNullOrWhiteSpace(EpgUrl) ? EpgUrl :
        !string.IsNullOrWhiteSpace(DiscoveredEpgUrl) ? DiscoveredEpgUrl :
        Kind == SourceKind.Xtream ? XtreamClient.EpgUrl(this) : null;
}

public enum ContentKind
{
    Live = 0,
    Movie = 1,
    Series = 2
}

/// <summary>Een zender, film of serie zoals de bron die aanbiedt.</summary>
/// <param name="Key">Stabiele sleutel binnen één bron; hiermee herkennen we items bij een update.</param>
/// <param name="Url">Afspeellink. Leeg bij een serie: die heeft afleveringen die apart worden opgehaald.</param>
/// <param name="CatchupDays">Hoeveel dagen terugkijken mogelijk is (0 = niet).</param>
/// <param name="CatchupType">Alleen M3U: "shift", "append", "default", "xc", ...</param>
/// <param name="CatchupSource">Alleen M3U: sjabloon voor de terugkijk-link.</param>
/// <param name="RemoteId">Id bij de provider (Xtream: stream_id of series_id).</param>
/// <param name="Info">Korte beschrijving (bijv. de inhoud van een serie).</param>
public sealed record Channel(
    string Key,
    string Name,
    string Group,
    string Url,
    string? LogoUrl = null,
    string? EpgId = null,
    ContentKind Kind = ContentKind.Live,
    int CatchupDays = 0,
    string? CatchupType = null,
    string? CatchupSource = null,
    string? RemoteId = null,
    string? Info = null,
    long? Added = null);

/// <summary>Een aflevering van een serie.</summary>
public sealed record Episode(
    string Id,
    int Season,
    int Number,
    string Title,
    string Url,
    string? Plot = null,
    int? DurationSeconds = null,
    string? LogoUrl = null);

/// <summary>Een zender in de persoonlijke lijst van de gebruiker.</summary>
public sealed class PersonalChannel
{
    public string SourceId { get; set; } = "";
    public string Key { get; set; } = "";
    /// <summary>De officiële naam uit de import (blijft bij elke update gekoppeld aan de bron).</summary>
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public string Url { get; set; } = "";
    public string? LogoUrl { get; set; }
    public string? EpgId { get; set; }

    /// <summary>Eigen naam van de gebruiker; alleen voor de weergave. Leeg = de officiële naam.</summary>
    public string? CustomName { get; set; }

    /// <summary>De naam zoals die in de app getoond wordt.</summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(CustomName) ? Name : CustomName;

    public ContentKind Kind { get; set; }
    public int CatchupDays { get; set; }
    public string? CatchupType { get; set; }
    public string? CatchupSource { get; set; }
    public string? RemoteId { get; set; }
    public string? Info { get; set; }

    /// <summary>Wanneer de provider deze film/serie toevoegde (unix-seconden); leeg als de bron dat niet meldt.</summary>
    public long? Added { get; set; }

    /// <summary>Id's van zenders in de lijst die het overnemen als deze zender niet werkt (in volgorde van proberen).</summary>
    public List<string> BackupIds { get; set; } = new();

    public bool IsFavorite { get; set; }
    public int SortOrder { get; set; }

    /// <summary>De bron biedt deze zender niet meer aan (bij de laatste sync).</summary>
    public bool MissingAtSource { get; set; }

    [JsonIgnore]
    public string Id => SourceId + "|" + Key;

    public static PersonalChannel From(string sourceId, Channel c, int sortOrder) => new()
    {
        SourceId = sourceId,
        Key = c.Key,
        Name = c.Name,
        Group = c.Group,
        Url = c.Url,
        LogoUrl = c.LogoUrl,
        EpgId = c.EpgId,
        Kind = c.Kind,
        CatchupDays = c.CatchupDays,
        CatchupType = c.CatchupType,
        CatchupSource = c.CatchupSource,
        RemoteId = c.RemoteId,
        Info = c.Info,
        Added = c.Added,
        SortOrder = sortOrder
    };

    public bool DiffersFrom(Channel c) =>
        Name != c.Name || Group != c.Group || Url != c.Url || LogoUrl != c.LogoUrl || EpgId != c.EpgId ||
        Kind != c.Kind || CatchupDays != c.CatchupDays || CatchupType != c.CatchupType ||
        CatchupSource != c.CatchupSource || RemoteId != c.RemoteId || Info != c.Info || Added != c.Added;

    /// <summary>Neemt de gegevens van de bron over. Favoriet en volgorde blijven van de gebruiker.</summary>
    public void UpdateFrom(Channel c)
    {
        Name = c.Name;
        Group = c.Group;
        Url = c.Url;
        LogoUrl = c.LogoUrl;
        EpgId = c.EpgId;
        Kind = c.Kind;
        CatchupDays = c.CatchupDays;
        CatchupType = c.CatchupType;
        CatchupSource = c.CatchupSource;
        RemoteId = c.RemoteId;
        Info = c.Info;
        Added = c.Added;
        MissingAtSource = false;
    }
}

/// <summary>Wat we per bron onthouden over eerdere syncs.</summary>
public sealed class SourceState
{
    public string SourceId { get; set; } = "";

    /// <summary>
    /// Alle zenders die de gebruiker al heeft "gezien" (gekozen én bewust niet gekozen).
    /// Alleen zenders die hier niet in staan worden bij een update als nieuw aangeboden.
    /// </summary>
    public HashSet<string> KnownKeys { get; set; } = new();
}

/// <param name="Warnings">Meldingen voor de gebruiker, bijv. "films konden niet worden opgehaald".</param>
/// <param name="IncompleteKinds">Soorten die niet (volledig) zijn opgehaald; de vorige lijst blijft daarvoor gelden.</param>
public sealed record Catalog(
    IReadOnlyList<Channel> Channels,
    string? EpgUrl,
    string? ServerTimeZone = null,
    IReadOnlyList<string>? Warnings = null,
    IReadOnlyCollection<ContentKind>? IncompleteKinds = null);

public sealed record Programme(string ChannelId, DateTime StartUtc, DateTime StopUtc, string Title, string? Description);
