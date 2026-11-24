using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IptvPlayer.Engine.ViewModels;
using LibVLCSharp.WPF;
using VlcPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace IptvPlayer.Desktop;

/// <summary>
/// Klein zwevend venster (altijd bovenop, vrij van grootte te maken) waarin het beeld doorspeelt terwijl je met de app of
/// met andere programma's verder gaat. De speler is dezelfde: alleen het venster waarin hij tekent wisselt.
/// Dubbelklik op het beeld of sluiten brengt het beeld terug naar de app; stopt het afspelen, dan sluit het venster zelf.
/// </summary>
public sealed class PipWindow : Window
{
    private readonly ChannelsViewModel _vm;
    private readonly VlcPlayer _player;
    private readonly VideoView _video = new() { Background = Brushes.Black };

    public PipWindow(ChannelsViewModel vm, VlcPlayer player)
    {
        _vm = vm;
        _player = player;

        Topmost = true;
        Width = 480;
        Height = 300;
        MinWidth = 240;
        MinHeight = 160;
        Background = Brushes.Black;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 24;
        Top = area.Bottom - Height - 24;
        UpdateTitle();

        // Het overlay-venster van LibVLCSharp vangt de muis op boven het beeld; daar zit de dubbelklik.
        var hit = new Grid { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), ToolTip = "Dubbelklik: terug naar de app" };
        hit.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) Close(); };
        _video.Content = hit;
        Content = _video;

        Loaded += (_, _) => _video.MediaPlayer = _player;
        vm.PropertyChanged += OnViewModelChanged;
    }

    private void UpdateTitle() => Title = _vm.Current?.Name ?? "EyePTV";

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ChannelsViewModel.Current)) return;
        Dispatcher.Invoke(() => { if (_vm.Current is null) Close(); else UpdateTitle(); });
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.PropertyChanged -= OnViewModelChanged;
        _video.MediaPlayer = null; // laat het beeld los, zodat de app het weer kan overnemen
        base.OnClosed(e);
    }
}
