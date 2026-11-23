using System.Text;

namespace IptvPlayer.Engine;

public static class TextUtil
{
    /// <summary>Trim, spaties samenvoegen, kleine letters.</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder(s.Length);
        var lastSpace = false;
        foreach (var ch in s.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastSpace) sb.Append(' ');
                lastSpace = true;
            }
            else
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastSpace = false;
            }
        }
        return sb.ToString();
    }

    /// <summary>Alleen letters en cijfers, kleine letters. Om zendernamen te matchen met EPG-namen.</summary>
    public static string NormalizeLoose(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    /// <summary>Zit <paramref name="query"/> in <paramref name="text"/>, zonder op hoofdletters of accenten te letten?</summary>
    public static bool ContainsLoose(string? text, string query) =>
        !string.IsNullOrEmpty(text) &&
        System.Globalization.CultureInfo.InvariantCulture.CompareInfo.IndexOf(text, query,
            System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace) >= 0;
}
