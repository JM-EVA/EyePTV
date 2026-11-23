namespace IptvPlayer.Engine;

/// <summary>Voorkeuren van de gebruiker (tabblad Instellingen). Staat in settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>Taalcode (ISO 639-1) van de ondertiteling die je het liefst hebt, bijvoorbeeld "nl".</summary>
    public string SubtitleLanguage { get; set; } = "nl";

    /// <summary>Voorkeur voor het audiospoor; leeg = laat het zoals de video het aanbiedt.</summary>
    public string AudioLanguage { get; set; } = "";

    /// <summary>Zet de ondertiteling in de voorkeurstaal automatisch aan als de video die heeft.</summary>
    public bool AutoSubtitles { get; set; } = true;

    /// <summary>Zo veel seconden mag een live zender nodig hebben om te starten voordat de app naar een reservezender schakelt.</summary>
    public int FailoverSeconds { get; set; } = 10;

    /// <summary>Hoeveel minuten voor het begin van een programma een herinnering afgaat (geldt voor nieuwe herinneringen).</summary>
    public int ReminderMinutes { get; set; } = 5;

    /// <summary>Erotiek (zie <see cref="AdultContent"/>) niet tonen op het beginscherm.</summary>
    public bool HideAdultOnHome { get; set; } = true;

    /// <summary>Op het beginscherm een beeld van de zender tonen in plaats van het logo (de app opent de zenders daarvoor even).</summary>
    public bool ChannelPreviews { get; set; } = true;

    /// <summary>Zout en hash van de code van 4 cijfers voor erotiek (zie <see cref="AdultLock"/>); leeg = de standaardcode.</summary>
    public string AdultPinSalt { get; set; } = "";
    public string AdultPinHash { get; set; } = "";


    /// <summary>Map voor downloads van films en series; leeg = Video's\EyePTV\Downloads.</summary>
    public string DownloadFolder { get; set; } = "";

    // Opnemen
    /// <summary>Map voor opnames; leeg = Video's\EyePTV.</summary>
    public string RecordingFolder { get; set; } = "";

    /// <summary>Hoeveel minuten vóór het begin van een programma de opname start (zodat je niets mist).</summary>
    public int RecordBeforeMinutes { get; set; } = 2;

    /// <summary>Hoeveel minuten na het einde van een programma de opname doorloopt (uitloop).</summary>
    public int RecordAfterMinutes { get; set; } = 5;
}

public sealed class SettingsStore
{
    private const string FileName = "settings.json";
    private readonly AppStore _store;

    public SettingsStore(AppStore store)
    {
        _store = store;
        Current = store.Load(FileName, () => new AppSettings());
    }

    public AppSettings Current { get; }

    public event Action? Changed;

    public void Save()
    {
        _store.Save(FileName, Current);
        Changed?.Invoke();
    }
}

public sealed record LanguageInfo(string Code, string Dutch, string English, string Native, params string[] Iso3);

/// <summary>De talen die we kennen, en het herkennen van een taal in de naam van een audio- of ondertitelspoor.</summary>
public static class Languages
{
    public static readonly IReadOnlyList<LanguageInfo> All = new LanguageInfo[]
    {
        new("nl", "Nederlands", "Dutch", "Nederlands", "nld", "dut", "vls"),
        new("en", "Engels", "English", "English", "eng"),
        new("de", "Duits", "German", "Deutsch", "ger", "deu"),
        new("fr", "Frans", "French", "Français", "fre", "fra"),
        new("es", "Spaans", "Spanish", "Español", "spa"),
        new("it", "Italiaans", "Italian", "Italiano", "ita"),
        new("pt", "Portugees", "Portuguese", "Português", "por"),
        new("tr", "Turks", "Turkish", "Türkçe", "tur"),
        new("ar", "Arabisch", "Arabic", "العربية", "ara"),
        new("pl", "Pools", "Polish", "Polski", "pol"),
        new("ru", "Russisch", "Russian", "Русский", "rus"),
        new("sv", "Zweeds", "Swedish", "Svenska", "swe"),
        new("da", "Deens", "Danish", "Dansk", "dan"),
        new("no", "Noors", "Norwegian", "Norsk", "nor", "nob"),
        new("fi", "Fins", "Finnish", "Suomi", "fin"),
        new("el", "Grieks", "Greek", "Ελληνικά", "gre", "ell"),
        new("ro", "Roemeens", "Romanian", "Română", "rum", "ron"),
        new("hu", "Hongaars", "Hungarian", "Magyar", "hun"),
        new("cs", "Tsjechisch", "Czech", "Čeština", "cze", "ces"),
        new("ja", "Japans", "Japanese", "日本語", "jpn"),
        new("ko", "Koreaans", "Korean", "한국어", "kor"),
        new("zh", "Chinees", "Chinese", "中文", "chi", "zho"),
    };

    public static LanguageInfo? Find(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : All.FirstOrDefault(l => l.Code.Equals(code.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Komt deze taal voor in de naam van het spoor ("Track 2 - [Dutch]", "nld", "Nederlands (SDH)")?</summary>
    public static bool Matches(string? trackName, string? code)
    {
        var lang = Find(code);
        if (lang is null || string.IsNullOrWhiteSpace(trackName)) return false;

        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var ch in trackName)
        {
            if (char.IsLetter(ch)) current.Append(char.ToLowerInvariant(ch));
            else if (current.Length > 0) { words.Add(current.ToString()); current.Clear(); }
        }
        if (current.Length > 0) words.Add(current.ToString());

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { lang.Code, lang.Dutch, lang.English, lang.Native };
        foreach (var iso in lang.Iso3) names.Add(iso);
        return words.Any(names.Contains);
    }

    /// <summary>Leesbare naam voor in de interface ("Nederlands"); valt terug op de code zelf.</summary>
    public static string DisplayName(string? code) => Find(code)?.Dutch ?? (code ?? "");
}
