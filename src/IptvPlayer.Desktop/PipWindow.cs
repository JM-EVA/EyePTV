using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using IptvPlayer.Desktop.Services;
using IptvPlayer.Engine.ViewModels;
using LibVLCSharp.WPF;

namespace IptvPlayer.Desktop;

/// <summary>
/// Klein zwevend venster (altijd bovenop, vrij van grootte te maken) waarin het beeld doorspeelt terwijl je met de app of
/// met andere programma's verder gaat. De speler is dezelfde: alleen het venster waarin hij tekent wisselt.
/// Dubbelklik op het beeld of sluiten brengt het beeld terug naar de app; stopt het afspelen, dan sluit het venster zelf.
/// </summary>
public sealed class PipWindow : Window
{
    private static PipWindow? _open;

    /// <summary>Het beeld in de app zelf (door <c>ChannelsView</c> gezet).</summary>
    public static VideoView? MainVideo { get; set; }

    public static bool IsOpen => _open is not null;

    public static void Open(ChannelsViewModel vm, LibVlcPlayerService player)
    {
        if (_open is not null || MainVideo is null || vm.Current is null) return;
        _open = new PipWindow(vm, player, MainVideo);
        _open.Show();
    }

    public static void CloseCurrent() => _open?.Close();

    private readonly ChannelsViewModel _vm;
    private readonly LibVlcPlayerService _player;
    private readonly VideoView _main;
    private readonly VideoView _video = new() { Background = Brushes.Black };

    private PipWindow(ChannelsViewModel vm, LibVlcPlayerService player, VideoView main)
    {
        _vm = vm;
        _player = player;
        _main = main;

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

        // Pas als het venster en zijn beeldvlak bestaan, kan het beeld erheen.
        Loaded += (_, _) => Dispatcher.BeginInvoke(() => _player.MoveVideo(() =>
        {
            _main.MediaPlayer = null;
            _video.MediaPlayer = _player.MediaPlayer;
        }), DispatcherPriority.ContextIdle);
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
        _open = null;
        try
        {
            _player.MoveVideo(() =>
            {
                _video.MediaPlayer = null;
                _main.MediaPlayer = _player.MediaPlayer;
            });
        }
        catch (ObjectDisposedException) { } // de app sluit af
        base.OnClosed(e);
    }
}
