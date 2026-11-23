using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace IptvPlayer.Desktop.Services;

/// <summary>Laadt een momentopname uit de map met zenderbeelden. Bestanden buiten die map worden nooit geladen.</summary>
public sealed class PreviewImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || path.Length == 0) return null;

        try
        {
            var folder = Path.GetFullPath(LibVlcThumbnailService.DefaultFolder) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(folder, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return null;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(full);
            bmp.DecodePixelWidth = 500;
            bmp.CacheOption = BitmapCacheOption.OnLoad; // het bestand niet vasthouden (het wordt later vervangen)
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
