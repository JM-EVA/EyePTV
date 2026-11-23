using System.Windows;
using System.Windows.Media;

namespace IptvPlayer.Desktop.Services;

/// <summary>Eigenschappen voor de knoppen in het menu: welk pictogram erbij hoort en of de pagina nu open staat.</summary>
public static class Nav
{
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.RegisterAttached("Icon", typeof(Geometry), typeof(Nav), new PropertyMetadata(null));

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.RegisterAttached("IsActive", typeof(bool), typeof(Nav), new PropertyMetadata(false));

    public static Geometry? GetIcon(DependencyObject d) => (Geometry?)d.GetValue(IconProperty);
    public static void SetIcon(DependencyObject d, Geometry? value) => d.SetValue(IconProperty, value);

    public static bool GetIsActive(DependencyObject d) => (bool)d.GetValue(IsActiveProperty);
    public static void SetIsActive(DependencyObject d, bool value) => d.SetValue(IsActiveProperty, value);
}
