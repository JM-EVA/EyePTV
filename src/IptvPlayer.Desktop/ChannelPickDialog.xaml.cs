using System.Windows;
using System.Windows.Controls;
using IptvPlayer.Engine;
using IptvPlayer.Engine.ViewModels;

namespace IptvPlayer.Desktop;

/// <summary>Zenders aanvinken uit een lijst (bijvoorbeeld reservezenders); de volgorde van aanvinken blijft bewaard.</summary>
public partial class ChannelPickDialog : Window
{
    private readonly List<string> _order;
    private readonly List<(ChannelChoice Choice, CheckBox Box)> _items = new();

    public ChannelPickDialog(string title, string message, IReadOnlyList<ChannelChoice> options, IReadOnlyList<string> selected)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        _order = selected.Where(id => options.Any(o => o.Id == id)).ToList();

        foreach (var o in options)
        {
            var box = new CheckBox
            {
                Content = o.Detail.Length > 0 ? $"{o.Name}  ·  {o.Detail}" : o.Name,
                FontWeight = o.Suggested ? FontWeights.SemiBold : FontWeights.Normal,
                IsChecked = _order.Contains(o.Id),
                Margin = new Thickness(2, 4, 2, 4)
            };
            var id = o.Id;
            box.Checked += (_, _) => { if (!_order.Contains(id)) _order.Add(id); };
            box.Unchecked += (_, _) => _order.Remove(id);
            _items.Add((o, box));
            List.Items.Add(box);
        }
        Loaded += (_, _) => Filter.Focus();
    }

    /// <summary>De gekozen id's in volgorde van aanvinken; null als de gebruiker annuleerde.</summary>
    public IReadOnlyList<string>? Answer { get; private set; }

    private void Filter_TextChanged(object sender, TextChangedEventArgs e)
    {
        var term = TextUtil.NormalizeLoose(Filter.Text);
        foreach (var (choice, box) in _items)
            box.Visibility = term.Length == 0 || TextUtil.NormalizeLoose(choice.Name).Contains(term) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Answer = _order.ToList();
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
