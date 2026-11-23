using IptvPlayer.Engine;
using IptvPlayer.Engine.ViewModels;

namespace IptvPlayer.Tests;

public static class ViewModelTests
{
    private static (string, Func<Task>) T(string name, Action a) => ("ViewModel: " + name, () => { a(); return Task.CompletedTask; });
    private static (string, Func<Task>) TA(string name, Func<Task> a) => ("ViewModel: " + name, a);

    private static Channel Ch(string key, string name, string group) => new(key, name, group, "http://u/" + key);

    private static List<Channel> Catalog() => new()
    {
        Ch("1", "NPO 1", "Nederland"),
        Ch("2", "NPO 2", "Nederland"),
        Ch("3", "RTL 4", "Nederland"),
        Ch("4", "BBC One", "UK"),
        Ch("5", "BBC Two", "UK"),
        Ch("6", "Eurosport 1", "Sport"),
    };

    private static SelectionViewModel Selection(SelectionPurpose purpose = SelectionPurpose.InitialImport, IEnumerable<string>? pre = null,
        IEnumerable<PersonalChannel>? removed = null, IReadOnlyList<Channel>? cat = null) =>
        new(new IptvSource { Name = "Prov" }, cat ?? Catalog(), pre ?? Array.Empty<string>(), purpose, removed);

    public static IEnumerable<(string, Func<Task>)> Tests()
    {
        yield return T("selectie: groepen met aantallen, 'Alle zenders' eerst", () =>
        {
            var vm = Selection();
            Assert.Sequence(new[] { "Alle zenders  (0/6)", "Nederland  (0/3)", "Sport  (0/1)", "UK  (0/2)" }, vm.Groups.Select(g => g.Label));
            Assert.Equal(6, vm.VisibleChannels.Count);
        });

        yield return T("selectie: groep kiezen en zoeken filtert de lijst", () =>
        {
            var vm = Selection();
            vm.SelectedGroup = vm.Groups.First(g => g.Name == "UK");
            Assert.Sequence(new[] { "BBC One", "BBC Two" }, vm.VisibleChannels.Select(c => c.Name));
            vm.SearchText = "two";
            Assert.Sequence(new[] { "BBC Two" }, vm.VisibleChannels.Select(c => c.Name));
            vm.SelectedGroup = vm.Groups[0];
            vm.SearchText = "npo";
            Assert.Equal(2, vm.VisibleChannels.Count);
            vm.SearchText = "sport"; // zoekt ook in groepsnaam
            Assert.Sequence(new[] { "Eurosport 1" }, vm.VisibleChannels.Select(c => c.Name));
        });

        yield return T("selectie: vinkje houdt tellers per groep en totaal bij", () =>
        {
            var vm = Selection();
            vm.VisibleChannels[0].IsSelected = true; // NPO 1
            vm.VisibleChannels[3].IsSelected = true; // BBC One
            Assert.Equal(2, vm.SelectedCount);
            Assert.Equal("2 van 6 zenders geselecteerd", vm.SummaryText);
            Assert.Equal("Nederland  (1/3)", vm.Groups.First(g => g.Name == "Nederland").Label);
            Assert.Equal("UK  (1/2)", vm.Groups.First(g => g.Name == "UK").Label);
            vm.VisibleChannels[0].IsSelected = false;
            Assert.Equal(1, vm.SelectedCount);
            Assert.Equal("Nederland  (0/3)", vm.Groups.First(g => g.Name == "Nederland").Label);
        });

        yield return T("selectie: alles in het zichtbare filter (de)selecteren", () =>
        {
            var vm = Selection();
            vm.SelectedGroup = vm.Groups.First(g => g.Name == "Nederland");
            vm.SelectVisibleCommand.Execute(null);
            Assert.Equal(3, vm.SelectedCount);
            vm.SelectedGroup = vm.Groups[0];
            vm.SearchText = "npo";
            vm.DeselectVisibleCommand.Execute(null);
            Assert.Equal(1, vm.SelectedCount, "alleen RTL 4 over");
            Assert.Sequence(new[] { "3" }, vm.SelectedKeys);
            Assert.Equal("Nederland  (1/3)", vm.Groups.First(g => g.Name == "Nederland").Label);
        });

        yield return T("selectie: 'alleen geselecteerde' toont enkel gekozen zenders", () =>
        {
            var vm = Selection();
            vm.VisibleChannels[1].IsSelected = true;
            vm.ShowOnlySelected = true;
            Assert.Sequence(new[] { "NPO 2" }, vm.VisibleChannels.Select(c => c.Name));
        });

        yield return T("selectie: voorgeselecteerde zenders (beheren) staan aangevinkt", () =>
        {
            var vm = Selection(SelectionPurpose.Browse, pre: new[] { "1", "6" });
            Assert.Equal(2, vm.SelectedCount);
            Assert.Equal("Sport  (1/1)", vm.Groups.First(g => g.Name == "Sport").Label);
        });

        yield return T("selectie: review toont verdwenen zenders, standaard aangevinkt om te verwijderen", () =>
        {
            var gone = new PersonalChannel { Key = "x", Name = "Weg", Group = "G" };
            var vm = Selection(SelectionPurpose.Review, removed: new[] { gone });
            Assert.True(vm.HasRemoved);
            Assert.Sequence(new[] { "x" }, vm.RemoveKeys);
            vm.RemovedAtSource[0].Remove = false;
            Assert.Equal(0, vm.RemoveKeys.Count, "behouden");
        });

        yield return T("selectie: titels verschillen per doel en bevestigen/annuleren geven events", () =>
        {
            Assert.True(Selection(SelectionPurpose.InitialImport).Title.StartsWith("Kies je zenders"));
            Assert.True(Selection(SelectionPurpose.Review).Title.StartsWith("Wijzigingen"));
            var vm = Selection();
            var confirmed = 0; var cancelled = 0;
            vm.Confirmed += _ => confirmed++;
            vm.Cancelled += _ => cancelled++;
            vm.ConfirmCommand.Execute(null);
            vm.CancelCommand.Execute(null);
            Assert.Equal(1, confirmed);
            Assert.Equal(1, cancelled);
        });

        yield return T("selectie: werkt vlot met 50.000 zenders", () =>
        {
            var big = Enumerable.Range(0, 50_000).Select(i => Ch("k" + i, "Zender " + i, "Groep " + (i % 200))).ToList();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var vm = Selection(cat: big);
            vm.SelectVisibleCommand.Execute(null);
            vm.SearchText = "Zender 4999";
            Assert.Equal(50_000, vm.SelectedCount);
            Assert.True(sw.ElapsedMilliseconds < 3000, $"te traag: {sw.ElapsedMilliseconds} ms");
        });

        // ---- ChannelsViewModel ----

        yield return T("zenderlijst: filteren op groep, favorieten en zoekterm", () =>
        {
            var env = ChannelsEnv.Create();
            Assert.Equal(6, env.Vm.Rows.Count);
            env.Vm.SelectedGroup = "UK";
            Assert.Sequence(new[] { "BBC One", "BBC Two" }, env.Vm.Rows.Select(r => r.Name));
            env.Vm.SelectedGroup = ChannelsViewModel.AllGroups;
            env.Vm.Rows.First(r => r.Name == "RTL 4").IsFavorite = true;
            env.Vm.FavoritesOnly = true;
            Assert.Sequence(new[] { "RTL 4" }, env.Vm.Rows.Select(r => r.Name));
            env.Vm.FavoritesOnly = false;
            env.Vm.SearchText = "bbc";
            Assert.Equal(2, env.Vm.Rows.Count);
            env.Vm.SearchText = "bestaatniet";
            Assert.True(env.Vm.HasNoMatches, "melding 'niets gevonden'");
            Assert.False(env.Vm.IsEmpty, "lijst zelf is niet leeg");
        });

        yield return T("zenderlijst: afspelen, volgende en vorige (met doorlopen)", () =>
        {
            var env = ChannelsEnv.Create();
            env.Vm.Rows[0].PlayCommand.Execute(null);
            Assert.Equal("http://u/1", env.Player.Played.Last());
            Assert.True(env.Vm.Rows[0].IsPlaying, "markering");
            env.Vm.NextCommand.Execute(null);
            Assert.Equal("http://u/2", env.Player.Played.Last());
            env.Vm.PreviousCommand.Execute(null);
            env.Vm.PreviousCommand.Execute(null);
            Assert.Equal("http://u/6", env.Player.Played.Last(), "vorige van de eerste = laatste");
            Assert.Equal("Eurosport 1", env.Vm.Current!.Name);
        });

        yield return T("zenderlijst: volume, dempen en stoppen gaan naar de speler", () =>
        {
            var env = ChannelsEnv.Create();
            env.Vm.Volume = 150;
            Assert.Equal(100, env.Player.Volume, "volume begrensd");
            env.Vm.ToggleMuteCommand.Execute(null);
            Assert.True(env.Player.IsMuted);
            env.Vm.Rows[0].PlayCommand.Execute(null);
            env.Vm.StopCommand.Execute(null);
            Assert.Equal(1, env.Player.StopCount);
            Assert.Equal(null, env.Vm.Current);
        });

        yield return T("zenderlijst: omhoog/omlaag schuiven past de bewaarde volgorde aan", () =>
        {
            var env = ChannelsEnv.Create();
            env.Vm.Rows[0].MoveDownCommand.Execute(null);
            Assert.Sequence(new[] { "NPO 2", "NPO 1" }, env.Vm.Rows.Take(2).Select(r => r.Name));
            env.Vm.Rows[5].MoveUpCommand.Execute(null);
            Assert.Equal("Eurosport 1", env.Vm.Rows[4].Name);
            env.Vm.Rows[0].MoveUpCommand.Execute(null); // bovenaan: niets
            Assert.Equal("NPO 2", env.Vm.Rows[0].Name);
            var lib2 = new Library(new AppStore(env.Tmp.Path));
            Assert.Equal("NPO 2", lib2.Ordered()[0].Name, "volgorde bewaard op schijf");
        });

        yield return T("zenderlijst: favoriet zetten of schuiven laat de lijst 'ter plekke' bijwerken (scrollpositie blijft)", () =>
        {
            var env = ChannelsEnv.Create();
            var before = env.Vm.Rows;
            var rowObjects = before.ToList();

            env.Vm.Rows[2].IsFavorite = true;
            Assert.True(ReferenceEquals(before, env.Vm.Rows), "zelfde lijst na favoriet");

            env.Vm.Rows[0].MoveDownCommand.Execute(null);
            Assert.True(ReferenceEquals(before, env.Vm.Rows), "zelfde lijst na schuiven");
            Assert.True(ReferenceEquals(rowObjects[0], env.Vm.Rows[1]), "dezelfde rij-objecten, alleen verplaatst");

            env.Dialogs.ConfirmAnswer = true;
            env.Vm.Rows[3].RemoveCommand.Execute(null);
            Assert.True(ReferenceEquals(before, env.Vm.Rows), "zelfde lijst na verwijderen");
            Assert.Equal(5, env.Vm.Rows.Count);

            env.Vm.SearchText = "npo"; // filteren verwijdert alleen rijen: ook ter plekke
            Assert.True(ReferenceEquals(before, env.Vm.Rows), "zelfde lijst na filteren");
            Assert.Equal(2, env.Vm.Rows.Count);
        });

        yield return T("zenderlijst: groepsfilter blijft staan als de lijst opnieuw wordt opgebouwd en negeert null van de ListBox", () =>
        {
            var env = ChannelsEnv.Create();
            env.Vm.SelectedGroup = "UK";
            env.Vm.SelectedGroup = null!; // wat een ListBox doet als zijn items worden vervangen
            Assert.Equal("UK", env.Vm.SelectedGroup);
            env.Vm.Rows[0].IsFavorite = true;
            env.Vm.Rows[0].MoveDownCommand.Execute(null);
            Assert.Equal("UK", env.Vm.SelectedGroup);
            Assert.Sequence(new[] { "BBC One", "BBC Two" }, env.Vm.Rows.Select(r => r.Name).OrderBy(x => x));
        });

        yield return T("zenderlijst: favorieten-filter laat een rij verdwijnen zodra de ster uitgaat", () =>
        {
            var env = ChannelsEnv.Create();
            env.Vm.Rows[1].IsFavorite = true;
            env.Vm.FavoritesOnly = true;
            Assert.Equal(1, env.Vm.Rows.Count);
            env.Vm.Rows[0].IsFavorite = false;
            Assert.Equal(0, env.Vm.Rows.Count);
            Assert.True(env.Vm.HasNoMatches);
        });

        yield return T("zenderlijst: verwijderen vraagt bevestiging", () =>
        {
            var env = ChannelsEnv.Create();
            env.Dialogs.ConfirmAnswer = false;
            env.Vm.Rows[0].RemoveCommand.Execute(null);
            Assert.Equal(6, env.Vm.Rows.Count, "geannuleerd");
            env.Dialogs.ConfirmAnswer = true;
            env.Vm.Rows[0].PlayCommand.Execute(null);
            env.Vm.Rows[0].RemoveCommand.Execute(null);
            Assert.Equal(5, env.Vm.Rows.Count);
            Assert.Equal(null, env.Vm.Current, "speler stopt als de huidige zender wordt verwijderd");
        });

        yield return T("zenderlijst: afspeelfout verschijnt in de statusregel", () =>
        {
            var env = ChannelsEnv.Create();
            env.Player.RaiseError("Kan stream niet openen");
            Assert.Equal("Kan stream niet openen", env.Vm.StatusText);
        });

        yield return TA("zenderlijst: toont 'nu bezig' en 'hierna' uit de EPG", async () =>
        {
            using var tmp = new TempStore();
            var env = ChannelsEnv.Create(tmp);
            var now = DateTime.UtcNow;
            string F(DateTime t) => t.ToString("yyyyMMddHHmmss") + " +0000";
            var xml = $"<tv><programme start=\"{F(now.AddMinutes(-10))}\" stop=\"{F(now.AddMinutes(20))}\" channel=\"npo1\"><title>Journaal</title><desc>Het nieuws</desc></programme>" +
                      $"<programme start=\"{F(now.AddMinutes(20))}\" stop=\"{F(now.AddMinutes(80))}\" channel=\"npo1\"><title>Film</title></programme></tv>";
            var epg = new EpgService(new HttpClient(new FakeHandler().Add("epg.test", xml)), tmp.Store);
            var src = env.Lib.Sources[0];
            src.EpgUrl = "http://epg.test/guide.xml";
            await epg.RefreshAsync(env.Lib.Sources, env.Lib.Personal);

            var vm = new ChannelsViewModel(env.Lib, epg, env.Player, env.Dialogs);
            var row = vm.Rows.First(r => r.Name == "NPO 1");
            Assert.Equal("Journaal", row.NowTitle, "in de lijst");
            row.PlayCommand.Execute(null);
            Assert.Equal("Journaal", vm.NowTitle);
            Assert.Equal("Het nieuws", vm.NowDescription);
            Assert.True(vm.NextText!.EndsWith("Film"), "hierna");
            Assert.True(vm.Progress > 0.2 && vm.Progress < 0.5, $"voortgang {vm.Progress:0.00}");
            Assert.True(vm.Schedule.Count >= 1 && vm.Schedule.Count <= 2, "programmalijst van vandaag");
            Assert.True(vm.Schedule.Single(p => p.IsNow).Title == "Journaal", "huidige gemarkeerd");
        });
    }
}

public sealed class ChannelsEnv
{
    public required TempStore Tmp { get; init; }
    public required Library Lib { get; init; }
    public required FakePlayer Player { get; init; }
    public required FakeDialogs Dialogs { get; init; }
    public required ChannelsViewModel Vm { get; init; }

    public static ChannelsEnv Create(TempStore? tmp = null)
    {
        tmp ??= new TempStore();
        var lib = new Library(tmp.Store);
        var src = lib.AddSource(new IptvSource { Name = "Prov", Kind = SourceKind.M3uUrl, M3uUrl = "http://x/list.m3u" });
        var cat = new List<Channel>
        {
            new("1", "NPO 1", "Nederland", "http://u/1", null, "npo1"),
            new("2", "NPO 2", "Nederland", "http://u/2"),
            new("3", "RTL 4", "Nederland", "http://u/3"),
            new("4", "BBC One", "UK", "http://u/4"),
            new("5", "BBC Two", "UK", "http://u/5"),
            new("6", "Eurosport 1", "Sport", "http://u/6"),
        };
        lib.CommitSelection(src.Id, cat, cat.Select(c => c.Key), cat.Select(c => c.Key));
        var player = new FakePlayer();
        var dialogs = new FakeDialogs();
        var vm = new ChannelsViewModel(lib, new EpgService(new HttpClient(new FakeHandler()), tmp.Store), player, dialogs);
        return new ChannelsEnv { Tmp = tmp, Lib = lib, Player = player, Dialogs = dialogs, Vm = vm };
    }
}
