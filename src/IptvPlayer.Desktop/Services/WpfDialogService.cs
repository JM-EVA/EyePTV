using System.Windows;
using IptvPlayer.Engine.ViewModels;

namespace IptvPlayer.Desktop.Services;

public sealed class WpfDialogService : IDialogService
{
    public ExitChoice AskExit(string message)
    {
        var dialog = new ExitDialog(message);
        if (Application.Current?.MainWindow is { IsLoaded: true } owner && owner != dialog) dialog.Owner = owner;
        dialog.ShowDialog();
        return dialog.Choice;
    }

    public string? AskText(string title, string message, string initial)
    {
        var dialog = new TextPromptDialog(title, message, initial);
        if (Application.Current?.MainWindow is { IsLoaded: true } owner && owner != dialog) dialog.Owner = owner;
        dialog.ShowDialog();
        return dialog.Answer;
    }

    public IReadOnlyList<string>? PickChannels(string title, string message, IReadOnlyList<ChannelChoice> options, IReadOnlyList<string> selected)
    {
        var dialog = new ChannelPickDialog(title, message, options, selected);
        if (Application.Current?.MainWindow is { IsLoaded: true } owner && owner != dialog) dialog.Owner = owner;
        dialog.ShowDialog();
        return dialog.Answer;
    }

    public void ShowInfo(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowError(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public bool Confirm(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}
