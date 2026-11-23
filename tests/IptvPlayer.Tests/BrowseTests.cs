using IptvPlayer.Engine;
using IptvPlayer.Engine.ViewModels;

namespace IptvPlayer.Tests;

/// <summary>Films en Series openen op een bladerscherm met categorietegels.</summary>
public static class BrowseTests
{
    private static (string, Func<Task>) T(string name, Action a) => ("Bladeren: " + name, () => { a(); return Task.CompletedTask; });

    private static (TempStore, ChannelsViewModel, Library) Create()
    {
        var tmp = new TempStore();
        var lib = new Library(tmp.Store);
        var src = lib.AddSource(new IptvSource { Name = "XT", Kind = SourceKind.Xtream, Server = "http://srv.test:8080", Username = "jan", Password = "pw" });
        var cat = new List<Channel>
        {
            new("xtm:1", "Heat", "Actie", "http://srv.test/1.mp4", Kind: ContentKind.Movie, Added: 100),
            new("xtm:2", "Die Hard", "Actie", "http://srv.test/2.mp4", Kind: ContentKind.Movie, Added: 300),
            new("xtm:3", "Alien", "Horror", "http://srv.test/3.mp4", Kind: ContentKind.Movie, Added: 200),
            new("k:1", "NPO 1", "NL", "http://srv.test/4.ts", Kind: ContentKind.Live),
        };
        lib.CommitSelection(src.Id, cat, cat.Select(c => c.Key), cat.Select(c => c.Key));
        var vm = new ChannelsViewModel(lib, new EpgService(new HttpClient(new FakeHandler()), tmp.Store), new FakePlayer(), new FakeDialogs(), settings: new SettingsStore(tmp.Store));
        return (tmp, vm, lib);
    }

    public static IEnumerable<(string, Func<Task>)> Tests()
    {
        yield return T("Films toont een plank per categorie, Kanalen niet", () =>
        {
            var (tmp, vm, _) = Create();
            using var _t = tmp;
            vm.SelectKind(ContentKind.Live);
            Assert.True(!vm.ShowBrowse, "live start op de speler");
            vm.SelectKind(ContentKind.Movie);
            Assert.True(vm.ShowBrowse);
            Assert.Equal(2, vm.Shelves.Count, "een plank per groep");
            Assert.Equal(2, vm.Shelves.First(s => s.Group == "Actie").Count);
            Assert.Equal(2, vm.Shelves.First(s => s.Group == "Actie").Items.Count);
        });

        yield return T("een categorie toont titels als tegels, een titel opent de speler", () =>
        {
            var (tmp, vm, _) = Create();
            using var _t = tmp;
            vm.SelectKind(ContentKind.Movie);
            vm.OpenCategoryCommand.Execute("Horror");
            Assert.True(vm.ShowBrowseTitles, "laag 2: titels als tegels");
            Assert.Equal(1, vm.BrowseTitles.Count);
            vm.BrowseCommand.Execute(null);
            Assert.True(vm.ShowBrowseGroups);
            vm.OpenCategoryCommand.Execute("Actie");
            vm.BrowseTitles[0].PlayCommand.Execute(null);
            Assert.True(!vm.ShowBrowse, "een titel kiezen opent de speler");
        });

        yield return T("zoeken filtert de planken", () =>
        {
            var (tmp, vm, _) = Create();
            using var _t = tmp;
            vm.SelectKind(ContentKind.Movie);
            vm.SearchText = "alien";
            Assert.True(vm.ShowBrowse, "blijft staan tijdens typen");
            Assert.Equal(1, vm.Shelves.Count);
            vm.SearchText = "zzz";
            Assert.True(vm.HasNoCategories);
        });

        yield return T("een andere zoektekst met evenveel treffers ververst de plank", () =>
        {
            var (tmp, vm, _) = Create();
            using var _t = tmp;
            vm.SelectKind(ContentKind.Movie);
            vm.SearchText = "heat";
            Assert.Equal("Heat", vm.Shelves.Single().Items.Single().Name);
            vm.SearchText = "hard";
            Assert.Equal("Die Hard", vm.Shelves.Single().Items.Single().Name, "zelfde groep en aantal, andere film");
        });

        yield return T("nieuwste eerst sorteert films op toevoegdatum, zenders niet", () =>
        {
            var (tmp, vm, _) = Create();
            using var _t = tmp;
            vm.SelectKind(ContentKind.Movie);
            vm.OpenCategoryCommand.Execute("Alle groepen");
            Assert.Equal("Heat", vm.BrowseTitles[0].Name, "standaard de lijstvolgorde");
            vm.SortNewest = true;
            Assert.Equal("Die Hard", vm.BrowseTitles[0].Name);
            Assert.Equal("Alien", vm.BrowseTitles[1].Name);
            Assert.Equal("Heat", vm.BrowseTitles[2].Name);
            vm.SortNewest = false;
            Assert.Equal("Heat", vm.BrowseTitles[0].Name, "terug naar de lijstvolgorde");
            vm.SelectKind(ContentKind.Live);
            Assert.True(!vm.CanSortNewest);
        });

        yield return T("tijdens afspelen geen bladerscherm (de speler zou erover heen staan)", () =>
        {
            var (tmp, vm, _) = Create();
            using var _t = tmp;
            vm.SelectKind(ContentKind.Movie);
            vm.OpenCategoryCommand.Execute("Actie");
            vm.Rows.First().PlayCommand.Execute(null);
            vm.SelectKind(ContentKind.Movie);
            Assert.True(!vm.ShowBrowse);
        });
    }
}
