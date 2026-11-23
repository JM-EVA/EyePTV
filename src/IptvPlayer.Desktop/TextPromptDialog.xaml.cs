using System.Windows;

namespace IptvPlayer.Desktop;

/// <summary>Vraagt om één regel tekst (bijvoorbeeld een eigen naam voor een zender).</summary>
public partial class TextPromptDialog : Window
{
    public TextPromptDialog(string title, string message, string initial)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        Input.Text = initial;
        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    /// <summary>De ingevoerde tekst; null als de gebruiker annuleerde.</summary>
    public string? Answer { get; private set; }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Answer = Input.Text;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
