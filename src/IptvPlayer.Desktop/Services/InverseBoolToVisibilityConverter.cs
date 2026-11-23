using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace IptvPlayer.Desktop.Services;

/// <summary>Zichtbaar als de waarde onwaar is (het omgekeerde van BooleanToVisibilityConverter).</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
