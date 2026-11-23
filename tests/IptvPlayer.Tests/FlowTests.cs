using IptvPlayer.Engine;
using IptvPlayer.Engine.ViewModels;

namespace IptvPlayer.Tests;

/// <summary>De complete gebruikersreis, met een nep-provider: eerste import → selectie → update → review.</summary>
public static class FlowTests
{
    private static (string, Func<Task>) TA(string name, Func<Task> a) => ("Flow: " + name, a);

    private sealed class App
    {
        public TempStore Tmp = new();
        public Library Lib = null!;
        public FakeHandler Http = new();
        public FakePlayer Player = new();
        public FakeDialogs Dialogs = new();
        public MainViewModel Main = null!;
        public string PlaylistText = "";
        public FakeNavigatorProxy Nav = null!;

        public static App Start(string initialPlaylist, TempStore? tmp = null)
        {
            var a = new App { PlaylistText = initialPlaylist };
            if (tmp != null) a.Tmp = tmp;
            a.Http.Add("provider.test/list.m3u", () => a.PlaylistText);
            a.Boot();
            return a;
        }

        /// <summary>Start de app (opnieuw) vanaf wat er op schijf staat.</summary>
        public void Boot()
        {
            var client = new HttpClient(Http);
            Lib = new Library(new AppStore(Tmp.Path));
            var epg = new EpgService(client, new AppStore(Tmp.Path));
            Main = new MainViewModel(Lib, new CatalogService(client), epg, Player, Dialogs);
        }
    }

    private sealed class FakeNavigatorProxy { }

    private static string Playlist(params string[] names) =>
        Samples.M3u(names.Select(n => (n.ToLowerInvariant().Replace(" ", ""), n, n.StartsWith("Sport") ? "Sport" : "Algemeen")).ToArray());

    public static IEnumerable<(string, Func<Task>)> Tests()
    {
        yield return TA("nieuwe gebruiker: bron toevoegen → selecteren → zenders staan in 'Mijn zenders'", async () =>
        {
            var app = App.Start(Playlist("NPO 1", "NPO 2", "RTL 4", "Sport 1"));
            await app.Main.StartupAsync();
            Assert.True(app.Main.Current == app.Main.Beheer && app.Main.Beheer.IsBronnen, "start op Beheer → Bronnen zolang de lijst leeg is");

            var s = app.Main.Sources;
            s.NewName = "Mijn provider";
            s.NewM3uUrl = "http://provider.test/list.m3u";
            await ((AsyncCommand)s.AddSourceCommand).ExecuteAsync(null);

            Assert.True(app.Dialogs.Errors.Count == 0, "geen fouten: " + string.Join("|", app.Dialogs.Errors));
            var sel = app.Main.Current as SelectionViewModel;
            Assert.True(sel is not null, "selectiescherm geopend");
            Assert.Equal(SelectionPurpose.InitialImport, sel!.Purpose);
            Assert.Equal(4, sel.TotalCount, "alle zenders aangeboden");
            Assert.Equal(0, app.Lib.Personal.Count, "nog niets in de lijst vóór bevestigen");

            sel.VisibleChannels.First(c => c.Name == "NPO 1").IsSelected = true;
            sel.VisibleChannels.First(c => c.Name == "Sport 1").IsSelected = true;
            sel.ConfirmCommand.Execute(null);

            Assert.True(app.Main.Current == app.Main.Channels, "na opslaan naar Mijn zenders");
            Assert.Sequence(new[] { "NPO 1", "Sport 1" }, app.Main.Channels.Rows.Select(r => r.Name).OrderBy(x => x));
            Assert.Equal(1, app.Main.Sources.Rows.Count);
            Assert.Equal(2, app.Main.Sources.Rows[0].ChannelsInList);
        });

        yield return TA("update: nieuwe zenders geven een melding en een review met alleen de nieuwe", async () =>
        {
            var app = App.Start(Playlist("NPO 1", "NPO 2", "RTL 4"));
            var s = app.Main.Sources;
            s.NewM3uUrl = "http://provider.test/list.m3u";
            await ((AsyncCommand)s.AddSourceCommand).ExecuteAsync(null);
            var first = (SelectionViewModel)app.Main.Current;
            first.VisibleChannels.First(c => c.Name == "NPO 1").IsSelected = true;
            first.ConfirmCommand.Execute(null);

            // De provider voegt twee zenders toe en haalt RTL 4 weg (die stond niet in mijn lijst).
            app.PlaylistText = Playlist("NPO 1", "NPO 2", "Sport 1", "Sport 2");
            await s.SyncAllAsync(interactive: false);

            Assert.True(app.Main.HasBanner, "melding zichtbaar");
            Assert.True(app.Main.BannerText!.Contains("2 nieuwe zenders"), "melding noemt aantal: " + app.Main.BannerText);
            Assert.Equal(1, app.Lib.Personal.Count, "lijst is niet automatisch uitgebreid");

            await ((AsyncCommand)app.Main.ReviewBannerCommand).ExecuteAsync(null);
            var review = (SelectionViewModel)app.Main.Current;
            Assert.Equal(SelectionPurpose.Review, review.Purpose);
            Assert.Sequence(new[] { "Sport 1", "Sport 2" }, review.VisibleChannels.Select(c => c.Name).OrderBy(x => x), "alleen nieuwe zenders");
            Assert.False(review.HasRemoved, "RTL 4 stond niet in mijn lijst, dus geen 'verdwenen'-vraag");

            review.VisibleChannels.First(c => c.Name == "Sport 2").IsSelected = true; // Sport 1 niet
            review.ConfirmCommand.Execute(null);

            Assert.Sequence(new[] { "NPO 1", "Sport 2" }, app.Lib.Ordered().Select(p => p.Name));
            Assert.False(app.Main.HasBanner, "melding weg na afhandelen");

            // Volgende update: Sport 1 is bewust overgeslagen en mag niet terugkomen.
            await s.SyncAllAsync(interactive: false);
            Assert.False(app.Main.HasBanner, "afgewezen zender komt niet terug");
        });

        yield return TA("update: verdwenen zender uit mijn lijst wordt in de review aangeboden", async () =>
        {
            var app = App.Start(Playlist("NPO 1", "NPO 2"));
            var s = app.Main.Sources;
            s.NewM3uUrl = "http://provider.test/list.m3u";
            await ((AsyncCommand)s.AddSourceCommand).ExecuteAsync(null);
            var first = (SelectionViewModel)app.Main.Current;
            first.SelectVisibleCommand.Execute(null);
            first.ConfirmCommand.Execute(null);
            Assert.Equal(2, app.Lib.Personal.Count);

            app.PlaylistText = Playlist("NPO 1");
            await s.SyncAllAsync(interactive: false);
            Assert.True(app.Main.BannerText!.Contains("1 verdwenen zender"), app.Main.BannerText);
            Assert.True(app.Lib.Personal.First(p => p.Name == "NPO 2").MissingAtSource, "gemarkeerd, nog niet verwijderd");

            await ((AsyncCommand)app.Main.ReviewBannerCommand).ExecuteAsync(null);
            var review = (SelectionViewModel)app.Main.Current;
            Assert.True(review.HasRemoved);
            review.ConfirmCommand.Execute(null);
            Assert.Sequence(new[] { "NPO 1" }, app.Lib.Ordered().Select(p => p.Name));
        });

        yield return TA("herstart: wijzigingen die nog open stonden zijn er na opnieuw opstarten nog steeds", async () =>
        {
            var app = App.Start(Playlist("NPO 1"));
            var s = app.Main.Sources;
            s.NewM3uUrl = "http://provider.test/list.m3u";
            await ((AsyncCommand)s.AddSourceCommand).ExecuteAsync(null);
            var first = (SelectionViewModel)app.Main.Current;
            first.SelectVisibleCommand.Execute(null);
            first.ConfirmCommand.Execute(null);

            app.PlaylistText = Playlist("NPO 1", "Nieuw 1");
            app.Http.Requests.Clear();
            await s.SyncAllAsync(interactive: false);
            Assert.True(app.Main.HasBanner);

            // App afsluiten en opnieuw starten, de provider is dan (even) niet bereikbaar.
            app.Http.Add("provider.test/list.m3u", "kapot", System.Net.HttpStatusCode.ServiceUnavailable);
            app.Boot();
            await app.Main.StartupAsync();
            Assert.True(app.Main.Current == app.Main.Home, "start op het beginscherm");
            Assert.True(app.Main.HasBanner, "melding staat er nog (uit opgeslagen gegevens)");
            Assert.Equal(1, app.Lib.Personal.Count, "lijst is intact");
        });

        yield return TA("Xtream: bron toevoegen met inloggegevens en via een get.php-link", async () =>
        {
            var app = App.Start("");
            // Let op: later toegevoegde routes gaan voor; de algemene (inlog) staat dus eerst.
            app.Http
                .Add("player_api.php?username=jan&password=pw", "{\"user_info\":{\"auth\":1,\"status\":\"Active\"}}")
                .Add("action=get_live_categories", "[{\"category_id\":\"1\",\"category_name\":\"Nieuws\"}]")
                .Add("action=get_live_streams", "[{\"stream_id\":1,\"name\":\"NOS\",\"category_id\":\"1\"},{\"stream_id\":2,\"name\":\"RTL Z\",\"category_id\":\"1\"}]");

            var s = app.Main.Sources;
            s.IsXtream = true;
            s.NewServer = "http://srv.test:8080/get.php?username=jan&password=pw&type=m3u_plus"; // geplakte link
            await ((AsyncCommand)s.AddSourceCommand).ExecuteAsync(null);

            Assert.True(app.Dialogs.Errors.Count == 0, string.Join("|", app.Dialogs.Errors));
            var src = app.Lib.Sources.Single();
            Assert.Equal(SourceKind.Xtream, src.Kind);
            Assert.Equal("http://srv.test:8080", src.Server);
            var sel = (SelectionViewModel)app.Main.Current;
            Assert.Equal(2, sel.TotalCount);
            Assert.Equal("Nieuws", sel.Groups[1].Name);
        });

        yield return TA("fout bij toevoegen: foute inlog of onbereikbare bron laat geen half werkende bron achter", async () =>
        {
            var app = App.Start("");
            app.Http.Add("player_api.php", "{\"user_info\":{\"auth\":0}}");
            var s = app.Main.Sources;
            s.IsXtream = true;
            s.NewServer = "http://srv.test";
            s.NewUser = "x";
            s.NewPassword = "y";
            await ((AsyncCommand)s.AddSourceCommand).ExecuteAsync(null);
            Assert.Equal(1, app.Dialogs.Errors.Count, "foutmelding getoond");
            Assert.Equal(0, app.Lib.Sources.Count, "geen bron opgeslagen");

            s.IsM3u = true;
            s.NewM3uUrl = "http://provider.test/kapot.m3u"; // 404 in de nep-provider
            await ((AsyncCommand)s.AddSourceCommand).ExecuteAsync(null);
            Assert.Equal(2, app.Dialogs.Errors.Count);
            Assert.Equal(0, app.Lib.Sources.Count, "ook niet na een mislukte M3U-download");
        });

        yield return TA("formulier: ontbrekende velden geven een melding en niets wordt aangemaakt", async () =>
        {
            var app = App.Start("");
            var s = app.Main.Sources;
            await ((AsyncCommand)s.AddSourceCommand).ExecuteAsync(null);
            s.IsXtream = true;
            await ((AsyncCommand)s.AddSourceCommand).ExecuteAsync(null);
            Assert.Equal(2, app.Dialogs.Infos.Count);
            Assert.Equal(0, app.Lib.Sources.Count);
        });

        yield return TA("selectie annuleren bij eerste import: bron blijft bestaan en je kunt later kiezen", async () =>
        {
            var app = App.Start(Playlist("NPO 1", "NPO 2"));
            var s = app.Main.Sources;
            s.NewM3uUrl = "http://provider.test/list.m3u";
            await ((AsyncCommand)s.AddSourceCommand).ExecuteAsync(null);
            ((SelectionViewModel)app.Main.Current).CancelCommand.Execute(null);

            Assert.True(app.Main.Current == app.Main.Beheer && app.Main.Beheer.IsBronnen, "terug naar Beheer → Bronnen");
            var row = app.Main.Sources.Rows.Single();
            Assert.True(row.NeedsInitialSelection);
            Assert.Equal("Inhoud kiezen", row.PrimaryActionText);

            await ((AsyncCommand)app.Main.Sources.PrimaryActionCommand).ExecuteAsync(row);
            Assert.Equal(SelectionPurpose.InitialImport, ((SelectionViewModel)app.Main.Current).Purpose);
        });

        yield return TA("beheren: later zenders toevoegen/verwijderen via de volledige lijst", async () =>
        {
            var app = App.Start(Playlist("NPO 1", "NPO 2", "RTL 4"));
            var s = app.Main.Sources;
            s.NewM3uUrl = "http://provider.test/list.m3u";
            await ((AsyncCommand)s.AddSourceCommand).ExecuteAsync(null);
            var first = (SelectionViewModel)app.Main.Current;
            first.VisibleChannels.First(c => c.Name == "NPO 1").IsSelected = true;
            first.ConfirmCommand.Execute(null);

            app.Main.ShowSourcesCommand.Execute(null);
            var row = app.Main.Sources.Rows.Single();
            Assert.Equal("Inhoud beheren", row.PrimaryActionText);
            await ((AsyncCommand)app.Main.Sources.PrimaryActionCommand).ExecuteAsync(row);

            var browse = (SelectionViewModel)app.Main.Current;
            Assert.Equal(SelectionPurpose.Browse, browse.Purpose);
            Assert.Equal(3, browse.TotalCount);
            Assert.Equal(1, browse.SelectedCount, "huidige lijst staat aangevinkt");
            browse.VisibleChannels.First(c => c.Name == "NPO 1").IsSelected = false;
            browse.VisibleChannels.First(c => c.Name == "RTL 4").IsSelected = true;
            browse.ConfirmCommand.Execute(null);

            Assert.Sequence(new[] { "RTL 4" }, app.Lib.Ordered().Select(p => p.Name));
        });

        yield return TA("navigatie: weggaan van Mijn zenders stopt de speler en volledig scherm", async () =>
        {
            var app = App.Start(Playlist("NPO 1"));
            var s = app.Main.Sources;
            s.NewM3uUrl = "http://provider.test/list.m3u";
            await ((AsyncCommand)s.AddSourceCommand).ExecuteAsync(null);
            var sel = (SelectionViewModel)app.Main.Current;
            sel.SelectVisibleCommand.Execute(null);
            sel.ConfirmCommand.Execute(null);

            app.Main.Channels.Rows[0].PlayCommand.Execute(null);
            app.Main.ToggleFullscreenCommand.Execute(null);
            Assert.False(app.Main.ShowNav, "menu verborgen in volledig scherm");
            Assert.False(app.Main.Channels.ShowChrome, "lijst verborgen in volledig scherm");

            Assert.True(app.Main.OtherPage is null, "geen andere pagina bovenop Mijn zenders");
            app.Main.ShowSourcesCommand.Execute(null);
            Assert.True(app.Main.OtherPage == app.Main.Beheer, "Beheer wordt de andere pagina");
            Assert.Equal(1, app.Player.StopCount, "speler gestopt");
            Assert.False(app.Main.IsFullscreen, "volledig scherm uit");
            Assert.True(app.Main.ShowNav);
        });
    }
}
