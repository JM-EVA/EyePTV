namespace IptvPlayer.Engine;

/// <summary>Nederlandse woorden per soort (zender, film, serie), zodat teksten overal hetzelfde zijn.</summary>
public static class KindText
{
    public static string Singular(ContentKind kind) => kind switch
    {
        ContentKind.Movie => "film",
        ContentKind.Series => "serie",
        _ => "zender"
    };

    public static string Plural(ContentKind kind, bool capital = false)
    {
        var word = kind switch
        {
            ContentKind.Movie => "films",
            ContentKind.Series => "series",
            _ => "zenders"
        };
        return capital ? char.ToUpperInvariant(word[0]) + word.Substring(1) : word;
    }

    /// <summary>Bijvoorbeeld "1 nieuwe zender", "3 nieuwe films", "2 verdwenen series".</summary>
    public static string Count(ContentKind kind, int count, string adjectiveSingular, string adjectivePlural)
    {
        var adj = count == 1 ? adjectiveSingular : adjectivePlural;
        return $"{count} {adj} {(count == 1 ? Singular(kind) : Plural(kind))}";
    }

    /// <summary>"a", "a en b", "a, b en c".</summary>
    public static string JoinNatural(IReadOnlyList<string> parts) => parts.Count switch
    {
        0 => "",
        1 => parts[0],
        _ => string.Join(", ", parts.Take(parts.Count - 1)) + " en " + parts[parts.Count - 1]
    };
}
