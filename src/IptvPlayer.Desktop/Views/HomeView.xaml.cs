using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using IptvPlayer.Desktop.Services;
using IptvPlayer.Engine.ViewModels;

namespace IptvPlayer.Desktop.Views;

public partial class HomeView : UserControl
{
    private DispatcherTimer? _hoverTimer;
    private LiveHoverStream? _hoverStream;
    private Image? _hoverImage;

    public HomeView()
    {
        InitializeComponent();
        // Verlaat je het beginscherm (bijvoorbeeld doordat je een kanaal opent), dan moet de meekijk-verbinding direct dicht.
        IsVisibleChanged += (_, _) => { if (!IsVisible) StopHover(); };
        Unloaded += (_, _) => StopHover();
        SizeChanged += (_, _) => UpdateLiveLimit();
        DataContextChanged += (_, _) => UpdateLiveLimit();
    }

    private const double CardSlotWidth = 260; // kaart van 250 plus 10 ruimte
    private const double CardRowHeight = 215; // kaart van ongeveer 205 plus ruimte

    /// <summary>
    /// Het raster toont alleen volle rijen, nooit meer dan 33 kaarten. Een kleiner venster (smaller of lager) geeft
    /// dus minder kaarten.
    /// </summary>
    private void LiveGrid_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateLiveLimit();

    private void UpdateLiveLimit()
    {
        if (DataContext is not HomeViewModel home || !IsVisible || LiveGrid.ActualWidth <= 0 || ActualHeight <= 0) return;
        var columns = Math.Max(1, (int)(LiveGrid.ActualWidth / CardSlotWidth));
        var rowsByHeight = Math.Max(2, (int)(ActualHeight / CardRowHeight));
        var rows = Math.Max(1, Math.Min(rowsByHeight, HomeViewModel.MaxLiveCards / columns));
        home.LiveLimit = columns * rows;
    }

    /// <summary>Wijs je een kanaalkaart aan, dan kijkt de kaart na even wachten live mee (zonder geluid).</summary>
    private void LiveCard_MouseEnter(object sender, MouseEventArgs e)
    {
        StopHover();
        if (sender is not Button { DataContext: HomeCard { LiveUrl: not null } card } button) return;
        if (DataContext is not HomeViewModel home || !home.CanLivePreview(card)) return;
        if (FindFrame(button) is not { } image) return;

        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer?.Stop();
            if (!home.CanLivePreview(card) || (Application.Current as App)?.Player?.LibVlc is not { } libVlc) return;
            try
            {
                var stream = new LiveHoverStream(libVlc, card.LiveUrl!);
                _hoverStream = stream;
                _hoverImage = image;
                image.Source = stream.Image;
                stream.FirstFrame += () => { if (ReferenceEquals(_hoverStream, stream)) image.Visibility = Visibility.Visible; };
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                StopHover();
            }
        };
        _hoverTimer.Start();
    }

    private void LiveCard_MouseLeave(object sender, MouseEventArgs e) => StopHover();

    private void StopHover()
    {
        _hoverTimer?.Stop();
        _hoverTimer = null;
        if (_hoverImage is not null)
        {
            _hoverImage.Visibility = Visibility.Collapsed;
            _hoverImage.Source = null;
            _hoverImage = null;
        }

        _hoverStream?.Dispose();
        _hoverStream = null;
    }

    private static Image? FindFrame(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Image { Name: "LiveFrame" } image) return image;
            if (FindFrame(child) is { } found) return found;
        }

        return null;
    }

    /// <summary>Opent het menu van een kaart (markeren als bekeken, verwijderen uit Verder kijken).</summary>
    private void CardMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || FindResource("CardMenu") is not ContextMenu menu) return;
        e.Handled = true; // niet ook de kaart zelf openen
        menu.DataContext = button.Tag ?? button.DataContext; // een menu erft de gegevens niet vanzelf
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>
    /// Een plank scrolt opzij, maar het muiswiel hoort de pagina omhoog en omlaag te scrollen
    /// (met Shift scrolt de plank zelf).
    /// </summary>
    private void Shelf_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer shelf) return;
        e.Handled = true;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            shelf.ScrollToHorizontalOffset(shelf.HorizontalOffset - e.Delta);
            return;
        }
        PageScroll.ScrollToVerticalOffset(PageScroll.VerticalOffset - e.Delta);
    }
}
