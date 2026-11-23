using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using IptvPlayer.Engine.ViewModels;

namespace IptvPlayer.Desktop.Views;

public partial class PreferencesView : UserControl
{
    public PreferencesView() => InitializeComponent();

    private SettingsViewModel? Vm => DataContext as SettingsViewModel;

    private void Unlock_Click(object sender, RoutedEventArgs e)
    {
        Vm?.UnlockAdult(UnlockPin.Password);
        UnlockPin.Clear();
    }

    private void UnlockPin_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Unlock_Click(sender, e);
    }

    private void Lock_Click(object sender, RoutedEventArgs e) => Vm?.LockAdult();

    private void ChangePin_Click(object sender, RoutedEventArgs e)
    {
        Vm?.ChangeAdultPin(CurrentPin.Password, NewPin.Password, RepeatPin.Password);
        if (Vm?.PinMessage == "De code is gewijzigd.")
        {
            CurrentPin.Clear();
            NewPin.Clear();
            RepeatPin.Clear();
        }
    }
}
