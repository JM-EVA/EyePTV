using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using IptvPlayer.Engine.ViewModels;

namespace IptvPlayer.Desktop.Views;

public partial class GuideView : UserControl
{
    public GuideView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is GuideViewModel old) old.PropertyChanged -= OnViewModelChanged;
            if (e.NewValue is GuideViewModel vm) vm.PropertyChanged += OnViewModelChanged;
        };
        Loaded += (_, _) => ScrollToNow();
    }

    private TranslateTransform Shift => (TranslateTransform)Resources["GridShift"];

    // Het rooster is breder dan het scherm: de schuifbalk bepaalt hoever kop, rijen en "nu"-lijn zijn opgeschoven.
    private void UpdateRange()
    {
        if (DataContext is not GuideViewModel vm) return;
        var viewport = TimeArea.ActualWidth;
        HScroll.ViewportSize = viewport;
        HScroll.Maximum = Math.Max(0, vm.GridWidth - viewport);
        HScroll.LargeChange = Math.Max(100, viewport * 0.8);
    }

    private void SetOffset(double value)
    {
        HScroll.Value = Math.Clamp(value, 0, HScroll.Maximum);
        Shift.X = -HScroll.Value;
    }

    private void ScrollToNow()
    {
        if (DataContext is not GuideViewModel vm) return;
        UpdateRange();
        SetOffset(vm.NowX - 150); // net voor "nu" beginnen
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GuideViewModel.GridWidth)) Dispatcher.BeginInvoke(ScrollToNow, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void TimeArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateRange();
        SetOffset(HScroll.Value);
    }

    private void HScroll_Scroll(object sender, System.Windows.Controls.Primitives.ScrollEventArgs e) => SetOffset(e.NewValue);

    // Shift + muiswiel (of een kantelwiel) schuift het rooster zijwaarts; het gewone wiel scrolt de zenders.
    private void GridList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        SetOffset(HScroll.Value - e.Delta);
        e.Handled = true;
    }

    private void Now_Click(object sender, RoutedEventArgs e) => ScrollToNow();

    private void Back_Click(object sender, RoutedEventArgs e) => SetOffset(HScroll.Value - HScroll.LargeChange);

    private void Forward_Click(object sender, RoutedEventArgs e) => SetOffset(HScroll.Value + HScroll.LargeChange);

    // Ochtend / middag / avond / nacht: naar dat uur vandaag (lokale tijd).
    private void Part_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not GuideViewModel vm || sender is not Button { Tag: string hour }) return;
        var target = DateTime.Today.AddHours(int.Parse(hour));
        SetOffset(vm.XFor(target.ToUniversalTime()));
    }
}
