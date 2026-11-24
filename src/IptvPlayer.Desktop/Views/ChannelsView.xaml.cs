using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using IptvPlayer.Engine.ViewModels;

namespace IptvPlayer.Desktop.Views;

public partial class ChannelsView : UserControl
{
    private ChannelsViewModel? _subscribedTo;

    public ChannelsView() => InitializeComponent();

    private ChannelsViewModel? Vm => DataContext as ChannelsViewModel;

    private void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        // De speler leeft in de app zelf; hier koppelen we alleen het beeld eraan.
        if (Application.Current is App app && app.Player is not null)
            VideoView.MediaPlayer = app.Player.MediaPlayer;

        // Als de programmagids verandert, scrollen we naar het programma dat nu loopt.
        if (Vm is { } vm && !ReferenceEquals(vm, _subscribedTo))
        {
            if (_subscribedTo is not null) _subscribedTo.PropertyChanged -= OnViewModelChanged;
            _subscribedTo = vm;
            vm.PropertyChanged += OnViewModelChanged;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ChannelsViewModel.Schedule) || Vm is not { } vm) return;
        var target = vm.Schedule.FirstOrDefault(p => p.IsNow) ?? vm.Schedule.FirstOrDefault();
        if (target is null) return;
        // Pas na het opbouwen van de lijst scrollen, anders bestaat het item nog niet.
        Dispatcher.InvokeAsync(() => ScheduleList.ScrollIntoView(target));
    }

    // ---- Zwevend venster (beeld-in-beeld) ----

    private PipWindow? _pip;

    private void Pip_Click(object sender, RoutedEventArgs e)
    {
        if (_pip is not null) { _pip.Close(); return; }
        if (Vm is not { Current: not null } vm || Application.Current is not App { Player: { } player }) return;

        VideoView.MediaPlayer = null; // eerst loslaten: het beeld kan maar in één venster tegelijk
        _pip = new PipWindow(vm, player.MediaPlayer);
        _pip.Closed += (_, _) =>
        {
            _pip = null;
            VideoView.MediaPlayer = player.MediaPlayer;
        };
        _pip.Show();
    }

    // ---- Opnemen ----

    private void RecordMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || FindResource("RecordMenu") is not ContextMenu menu) return;
        menu.DataContext = button.DataContext; // een menu staat buiten de visuele boom en erft de gegevens niet
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    // ---- Lijst met zenders, films en series ----

    private void ChannelList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Alleen reageren als er echt op een item is geklikt (niet op een scrollbalk of lege ruimte).
        if (sender is not ListBox list || e.OriginalSource is not DependencyObject source) return;
        if (IsInsideButton(source, list)) return; // ster en playknop regelen hun eigen klik
        if (ItemsControl.ContainerFromElement(list, source) is ListBoxItem { DataContext: ChannelRow row })
            row.PlayCommand.Execute(null);
    }

    private static bool IsInsideButton(DependencyObject source, DependencyObject stopAt)
    {
        for (var d = source; d is not null && !ReferenceEquals(d, stopAt); d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            if (d is System.Windows.Controls.Primitives.ButtonBase) return true;
        return false;
    }

    private void ChannelList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is ListBox { SelectedItem: ChannelRow row })
        {
            row.PlayCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ---- Afleveringen van een serie ----

    private void EpisodeList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list || e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(list, source) is ListBoxItem { DataContext: EpisodeRow row })
            row.PlayCommand.Execute(null);
    }

    private void EpisodeList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is ListBox { SelectedItem: EpisodeRow row })
        {
            row.PlayCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ---- Spoelbalk: tijdens het slepen niet bijwerken, bij loslaten naar dat punt springen ----

    /// <summary>Menu om naar een tv te casten (Chromecast / Google TV). De lijst vult zich terwijl het menu openstaat.</summary>
    private void Cast_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || Vm is not { } vm) return;

        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        void Fill()
        {
            menu.Items.Clear();
            menu.Items.Add(new MenuItem { Header = "Casten naar", IsEnabled = false });

            var targets = vm.CastTargets;
            foreach (var target in targets)
            {
                var label = target.Kind.Length > 0 ? $"{target.Name}  ·  {target.Kind}" : target.Name;
                var item = new MenuItem { Header = (target.Name == vm.CastingTo ? "✓  " : "") + label };
                item.Click += (_, _) => vm.CastTo(target.Id);
                menu.Items.Add(item);
            }

            if (targets.Count == 0)
                menu.Items.Add(new MenuItem { Header = "Zoeken naar apparaten…", IsEnabled = false });

            if (vm.IsCasting)
            {
                menu.Items.Add(new Separator());
                var back = new MenuItem { Header = "Weer op deze pc kijken" };
                back.Click += (_, _) => vm.CastTo(null);
                menu.Items.Add(back);
            }
        }

        Action refresh = Fill;
        vm.CastTargetsChanged += refresh;
        menu.Closed += (_, _) => vm.CastTargetsChanged -= refresh;

        vm.StartCastDiscovery();
        Fill();
        menu.IsOpen = true;
    }

    /// <summary>Opent een klein menu met de andere streams van de zender die speelt.</summary>
    private void Alternatives_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || Vm is not { } vm) return;

        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        foreach (var option in vm.Alternatives)
        {
            var item = new MenuItem { Header = option.Title };
            item.Click += (_, _) => vm.PlayAlternative(option);
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.IsOpen = true;
    }

    private void SeekSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Vm?.BeginSeek();

    private void SeekSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider slider) Vm?.EndSeek(slider.Value);
    }

    /// <summary>
    /// Een plank scrolt opzij, maar het muiswiel hoort de pagina omhoog en omlaag te scrollen
    /// (met Shift scrolt de plank zelf): het wiel wordt doorgegeven aan de lijst eromheen.
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
        if (System.Windows.Media.VisualTreeHelper.GetParent(shelf) is UIElement parent)
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = shelf });
    }
}
