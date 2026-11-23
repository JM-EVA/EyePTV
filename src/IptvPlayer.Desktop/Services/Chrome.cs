using System.Windows;
using System.Windows.Controls.Primitives;

namespace IptvPlayer.Desktop.Services;

/// <summary>Hulpmiddelen voor de eigen vensterkop (stijl <c>AppWindow</c> in App.xaml).</summary>
public static class Chrome
{
    /// <summary>Verbergt de vensterkop (bij volledig scherm).</summary>
    public static readonly DependencyProperty HideTitleBarProperty =
        DependencyProperty.RegisterAttached("HideTitleBar", typeof(bool), typeof(Chrome), new PropertyMetadata(false));

    public static bool GetHideTitleBar(DependencyObject d) => (bool)d.GetValue(HideTitleBarProperty);
    public static void SetHideTitleBar(DependencyObject d, bool value) => d.SetValue(HideTitleBarProperty, value);

    /// <summary>"Minimize", "Maximize" of "Close": wat deze knop in de vensterkop met het venster doet.</summary>
    public static readonly DependencyProperty ActionProperty =
        DependencyProperty.RegisterAttached("Action", typeof(string), typeof(Chrome), new PropertyMetadata(null, OnActionChanged));

    public static string? GetAction(DependencyObject d) => (string?)d.GetValue(ActionProperty);
    public static void SetAction(DependencyObject d, string? value) => d.SetValue(ActionProperty, value);

    private static void OnActionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase button) return;
        button.Click += (_, _) =>
        {
            if (Window.GetWindow(button) is not { } window) return;
            switch (GetAction(button))
            {
                case "Minimize": window.WindowState = WindowState.Minimized; break;
                case "Maximize": window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; break;
                case "Close": window.Close(); break;
            }
        };
    }
}
