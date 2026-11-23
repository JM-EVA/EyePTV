using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using IptvPlayer.Engine.ViewModels;

namespace IptvPlayer.Desktop.Views;

public partial class SourcesView : UserControl
{
    private SourcesViewModel? _vm;
    private bool _updatingPassword;

    public SourcesView() => InitializeComponent();

    private void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SourcesViewModel vm) return;

        if (_vm != null) _vm.PropertyChanged -= OnViewModelChanged;
        _vm = vm;
        _vm.PropertyChanged += OnViewModelChanged;
        SetPasswordBox(vm.NewPassword);
    }

    // Een PasswordBox kan niet gebonden worden; daarom houden we hem hier handmatig gelijk aan het viewmodel.
    private void PasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingPassword || _vm is null) return;
        _vm.NewPassword = PasswordInput.Password;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SourcesViewModel.NewPassword) && _vm is not null)
            SetPasswordBox(_vm.NewPassword);
    }

    private void SetPasswordBox(string value)
    {
        if (PasswordInput.Password == value) return;
        _updatingPassword = true;
        PasswordInput.Password = value;
        _updatingPassword = false;
    }
}
