using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace IptvPlayer.Desktop.Services;

/// <summary>Maakt van een logo-URL een klein plaatje. Een kapotte of lege URL geeft gewoon geen plaatje (nooit een fout).</summary>
public sealed class UrlToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s)) return null;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (IsLocalOrPrivate(uri)) return null; // een lijst mag je app geen plaatjes laten ophalen bij apparaten in je eigen netwerk

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = uri;
            // Kleine logo's in de lijst: 64 px. Grotere plaatjes (posters) geven hun breedte op via ConverterParameter.
            bmp.DecodePixelWidth = parameter is string p && int.TryParse(p, out var w) && w > 0 ? Math.Min(w, 800) : 64;
            bmp.CacheOption = BitmapCacheOption.OnDemand;
            bmp.EndInit();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsLocalOrPrivate(Uri uri)
    {
        var host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var ip)) return false;
        if (System.Net.IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
        var b = ip.GetAddressBytes();
        if (b.Length == 16) return (b[0] & 0xFE) == 0xFC; // fc00::/7 (uniek lokaal)
        return b[0] == 10 || b[0] == 127 || b[0] == 0 ||
               (b[0] == 172 && b[1] is >= 16 and <= 31) ||
               (b[0] == 192 && b[1] == 168) ||
               (b[0] == 169 && b[1] == 254);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
