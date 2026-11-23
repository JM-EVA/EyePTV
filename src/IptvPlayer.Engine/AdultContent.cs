using System.Text.RegularExpressions;

namespace IptvPlayer.Engine;

/// <summary>
/// Herkent erotiek aan de naam van de groep ("18| FOR ADULTS", "XXX") of de titel ("[X][Studio] Titel"),
/// zodat het niet op het beginscherm verschijnt. Bewust voorzichtig met losse woorden in titels
/// (een serie als "Sex Education" of "Adult Swim" is geen erotiek).
/// </summary>
public static class AdultContent
{
    private static readonly Regex GroupRx = new(
        @"(?<!\w)(adults?|xxx|porn\w*|erotic\w*|erotiek\w*|erotisch\w*|playboy|hustler|18\s*\+)(?!\w)|(?<!\d)18\s*\|",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex TitleRx = new(
        @"^\s*\[\s*x\s*\]|(?<!\w)(xxx|porn\w*)(?!\w)|(?<!\d)18\s*\+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsAdult(string? group, string? name) =>
        (!string.IsNullOrEmpty(group) && GroupRx.IsMatch(group)) || (!string.IsNullOrEmpty(name) && TitleRx.IsMatch(name));

    public static bool IsAdult(PersonalChannel channel) => IsAdult(channel.Group, channel.Name);
}
