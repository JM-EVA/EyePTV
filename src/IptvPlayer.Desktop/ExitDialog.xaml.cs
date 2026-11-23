using System.Windows;
using IptvPlayer.Engine.ViewModels;

namespace IptvPlayer.Desktop;

/// <summary>"Er staat nog een opname": echt afsluiten, alleen minimaliseren of terug naar de app.</summary>
public partial class ExitDialog : Window
{
    public ExitDialog(string message)
    {
        InitializeComponent();
        MessageText.Text = message;
        Choice = ExitChoice.Stay; // sluit je het venster met het kruisje, dan blijf je in de app
    }

    public ExitChoice Choice { get; private set; }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        Choice = ExitChoice.Quit;
        Close();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        Choice = ExitChoice.Minimize;
        Close();
    }

    private void Stay_Click(object sender, RoutedEventArgs e)
    {
        Choice = ExitChoice.Stay;
        Close();
    }
}
