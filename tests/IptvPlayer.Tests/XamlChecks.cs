using System.Reflection;
using System.Text.RegularExpressions;
using IptvPlayer.Engine.ViewModels;

namespace IptvPlayer.Tests;

/// <summary>
/// De WPF-laag kan alleen op Windows gecompileerd worden. Deze controles vangen de meest voorkomende fouten
/// in de XAML toch al op: bindings naar eigenschappen die niet bestaan, ontbrekende event-handlers en onbekende resources.
/// </summary>
public static class XamlChecks
{
    private static (string, Func<Task>) T(string name, Action a) => ("XAML: " + name, () => { a(); return Task.CompletedTask; });

    private static readonly (string File, string CodeBehind, Type[] Types)[] Views =
    {
        ("MainWindow.xaml", "MainWindow.xaml.cs", new[] { typeof(MainViewModel) }),
        ("Views/ChannelsView.xaml", "Views/ChannelsView.xaml.cs", new[] { typeof(ChannelsViewModel), typeof(ChannelRow), typeof(ProgrammeRow), typeof(EpisodeRow), typeof(KindOption), typeof(ScheduleDay), typeof(SeasonOption), typeof(TrackOption), typeof(CategoryShelf) }),
        ("Views/SelectionView.xaml", "Views/SelectionView.xaml.cs", new[] { typeof(SelectionViewModel), typeof(SelectableChannel), typeof(GroupItem), typeof(RemovedItem), typeof(KindTab) }),
        ("Views/SourcesView.xaml", "Views/SourcesView.xaml.cs", new[] { typeof(SourcesViewModel), typeof(SourceRow) }),
        ("Views/BeheerView.xaml", "Views/BeheerView.xaml.cs", new[] { typeof(BeheerViewModel), typeof(BeheerTab) }),
        ("Views/PreferencesView.xaml", "Views/PreferencesView.xaml.cs", new[] { typeof(SettingsViewModel), typeof(LanguageOption) }),
        ("Views/RecordingsView.xaml", "Views/RecordingsView.xaml.cs", new[] { typeof(SettingsViewModel), typeof(RecordingsViewModel), typeof(RecordingRow) }),
        ("Views/HomeView.xaml", "Views/HomeView.xaml.cs", new[] { typeof(HomeViewModel), typeof(HomeCard), typeof(HomeTile) }),
        ("Views/GuideView.xaml", "Views/GuideView.xaml.cs", new[] { typeof(GuideViewModel), typeof(GuideChannelRow), typeof(GuideProgrammeRow), typeof(ScheduleDay), typeof(GuideGridRow), typeof(GuideBlock), typeof(GuideHourMark), typeof(GuideHitRow), typeof(ReminderRow) }),
    };

    private static string DesktopDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "IptvPlayer.Desktop"))) dir = dir.Parent;
        if (dir == null) throw new TestFailure("Map src/IptvPlayer.Desktop niet gevonden.");
        return Path.Combine(dir.FullName, "src", "IptvPlayer.Desktop");
    }

    public static IEnumerable<(string, Func<Task>)> Tests()
    {
        yield return T("elke {Binding ...} wijst naar een bestaande eigenschap van het viewmodel", () =>
        {
            var problems = new List<string>();
            foreach (var (file, _, types) in Views)
            {
                var xaml = File.ReadAllText(Path.Combine(DesktopDir(), file));
                var names = new HashSet<string>(types.SelectMany(t =>
                    t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name)));

                foreach (Match m in Regex.Matches(xaml, @"\{Binding\s*([^}]*)\}"))
                {
                    var first = m.Groups[1].Value.Split(',')[0].Trim();
                    if (first.Length == 0 || first == ".") continue;
                    if (first.StartsWith("Path=")) first = first.Substring(5).Trim();
                    else if (first.Contains('=')) continue; // geen pad, bijv. {Binding Converter=...}

                    // Een binding op een ander element (RelativeSource) wijst niet naar het viewmodel, tenzij via DataContext.
                    if (m.Groups[1].Value.Contains("RelativeSource") && !first.StartsWith("DataContext.")) continue;

                    if (first.StartsWith("DataContext.")) first = first.Substring("DataContext.".Length);
                    var head = Regex.Match(first, @"^[A-Za-z_]\w*").Value;
                    if (head.Length > 0 && !names.Contains(head))
                        problems.Add($"{file}: {{Binding {first}}} bestaat niet op {string.Join("/", types.Select(t => t.Name))}");
                }
            }
            if (problems.Count > 0) throw new TestFailure(string.Join("\n", problems.Distinct()));
        });

        yield return T("elke event-handler in de XAML bestaat in de code-behind", () =>
        {
            var problems = new List<string>();
            foreach (var (file, code, _) in Views)
            {
                var xaml = File.ReadAllText(Path.Combine(DesktopDir(), file));
                var cs = File.ReadAllText(Path.Combine(DesktopDir(), code));
                foreach (Match m in Regex.Matches(xaml, @"\b(Loaded|Unloaded|MouseDoubleClick|MouseLeftButtonUp|MouseLeftButtonDown|PreviewMouseLeftButtonDown|PreviewMouseLeftButtonUp|KeyDown|PreviewKeyDown|PasswordChanged|Click|SelectionChanged|TextChanged|ValueChanged)=""(\w+)"""))
                    if (!Regex.IsMatch(cs, $@"\bvoid\s+{m.Groups[2].Value}\s*\("))
                        problems.Add($"{file}: handler '{m.Groups[2].Value}' ({m.Groups[1].Value}) ontbreekt in {code}");
            }
            if (problems.Count > 0) throw new TestFailure(string.Join("\n", problems));
        });

        yield return T("elke StaticResource is gedefinieerd (App.xaml of in hetzelfde bestand)", () =>
        {
            var app = File.ReadAllText(Path.Combine(DesktopDir(), "App.xaml"));
            var problems = new List<string>();
            foreach (var (file, _, _) in Views)
            {
                var xaml = File.ReadAllText(Path.Combine(DesktopDir(), file));
                foreach (Match m in Regex.Matches(xaml, @"\{StaticResource\s+(\w+)\}"))
                {
                    var key = m.Groups[1].Value;
                    var needle = $"x:Key=\"{key}\"";
                    if (!app.Contains(needle) && !xaml.Contains(needle))
                        problems.Add($"{file}: StaticResource '{key}' is nergens gedefinieerd");
                    // Een StaticResource moet vóór het gebruik zijn gedefinieerd (ook binnen een sjabloon); anders start de app niet.
                    else if (!app.Contains(needle) && xaml.IndexOf(needle, StringComparison.Ordinal) > m.Index)
                        problems.Add($"{file}: StaticResource '{key}' wordt gebruikt vóór hij is gedefinieerd (verplaats de definitie omhoog)");
                }
            }
            // Zelfde volgorde-eis in App.xaml zelf.
            foreach (Match m in Regex.Matches(app, @"\{StaticResource\s+(\w+)\}"))
            {
                var needle = $"x:Key=\"{m.Groups[1].Value}\"";
                var at = app.IndexOf(needle, StringComparison.Ordinal);
                if (at < 0) problems.Add($"App.xaml: StaticResource '{m.Groups[1].Value}' is nergens gedefinieerd");
                else if (at > m.Index) problems.Add($"App.xaml: StaticResource '{m.Groups[1].Value}' wordt gebruikt vóór hij is gedefinieerd");
            }
            if (problems.Count > 0) throw new TestFailure(string.Join("\n", problems.Distinct()));
        });

        yield return T("x:Class komt overeen met de klasse in de code-behind", () =>
        {
            var problems = new List<string>();
            foreach (var (file, code, _) in Views)
            {
                var xaml = File.ReadAllText(Path.Combine(DesktopDir(), file));
                var cs = File.ReadAllText(Path.Combine(DesktopDir(), code));
                var full = Regex.Match(xaml, @"x:Class=""([\w\.]+)""").Groups[1].Value;
                var cls = full.Substring(full.LastIndexOf('.') + 1);
                var ns = full.Substring(0, full.LastIndexOf('.'));
                if (!Regex.IsMatch(cs, $@"partial\s+class\s+{cls}\b")) problems.Add($"{file}: klasse {cls} niet gevonden in {code}");
                if (!Regex.IsMatch(cs, $@"namespace\s+{Regex.Escape(ns)}\s*;")) problems.Add($"{file}: namespace {ns} komt niet overeen in {code}");
            }
            if (problems.Count > 0) throw new TestFailure(string.Join("\n", problems));
        });

        yield return T("elk element dat de code-behind met x:Name aanspreekt, bestaat in de XAML", () =>
        {
            var checks = new[] { ("Views/ChannelsView.xaml", "Views/ChannelsView.xaml.cs", new[] { "VideoView", "ScheduleList", "SeekSlider", "EpisodeList", "ChannelList" }),
                                 ("Views/SourcesView.xaml", "Views/SourcesView.xaml.cs", new[] { "PasswordInput" }) };
            var problems = new List<string>();
            foreach (var (xamlFile, _, names) in checks)
            {
                var xaml = File.ReadAllText(Path.Combine(DesktopDir(), xamlFile));
                foreach (var n in names)
                    if (!xaml.Contains($"x:Name=\"{n}\"")) problems.Add($"{xamlFile}: x:Name=\"{n}\" ontbreekt");
            }
            if (problems.Count > 0) throw new TestFailure(string.Join("\n", problems));
        });
    }
}
