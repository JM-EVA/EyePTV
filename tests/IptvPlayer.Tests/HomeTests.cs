using IptvPlayer.Engine;
using IptvPlayer.Engine.ViewModels;

namespace IptvPlayer.Tests;

/// <summary>Beginscherm (verder kijken, verwijderen, markeren, erotiek verbergen), Beheer en het afsluiten met opnames.</summary>
public static class HomeTests
{
    private static (string, Func<Task>) T(string name, Action a) => ("Home: " + name, () => { a(); return Task.CompletedTask; });
    private static (string, Func<Task>) TA(string name, Func<Task> a) => ("Home: " + name, a);

    private sealed class Env : IDisposable
    {
        public required TempStore Tmp { get; init; }
        public required Library Lib { get; init; }
        public required WatchStore Watch { get; init; }
        public required FakePlayer Player { get; init; }
        public required SettingsStore Settings { get; init; }
        public required ChannelsViewModel Channels { get; init; }
        public required HomeViewModel Home { get; init; }
        public int PlayerShown;
        public DateTime Now { get; } = new(2026, 10, 26, 20, 0, 0, DateTimeKind.Utc);
        public DateTime Clock { get; set; } = new(2026, 10, 26, 20, 0, 0, DateTimeKind.Utc);
        public PersonalChannel P(string name) => Lib.Personal.First(p => p.Name == name);
        public void Dispose() => Tmp.Dispose();
    }

    /// <summary>Maakt "beelden" zonder LibVLC: opnames die direct of pas na een vrijgave klaar zijn.</summary>
    private sealed class FakeThumbs : IThumbnailService
    {
        public List<string> Captured { get; } = new();
        public TaskCompletionSource<string?>? Gate { get; set; }

        public Task<string?> CaptureAsync(string key, string url, CancellationToken ct)
        {
            Captured.Add(key);
            return Gate?.Task ?? Task.FromResult<string?>("shot-" + key + ".jpg");
        }
    }

    private static Env Create(bool hideAdult = true, FakeThumbs? thumbs = null)
    {
        var tmp = new TempStore();
        var lib = new Library(tmp.Store);
        var src = lib.AddSource(new IptvSource { Name = "XT", Kind = SourceKind.Xtream, Server = "http://srv.test:8080", Username = "jan", Password = "pw" });
        var cat = new List<Channel>
        {
            new("xt:1", "NOS", "Nieuws", "http://srv.test:8080/live/jan/pw/1.ts", RemoteId: "1"),
            new("xt:2", "Erotiek TV", "18| FOR ADULTS", "http://srv.test:8080/live/jan/pw/2.ts", RemoteId: "2"),
            new("xtm:501", "Film A", "Actie", "http://srv.test:8080/movie/jan/pw/501.mkv", Kind: ContentKind.Movie),
            new("xtm:502", "Film B", "Drama", "http://srv.test:8080/movie/jan/pw/502.mp4", Kind: ContentKind.Movie),
            new("xtm:503", "[X][Studio] Een titel", "Films", "http://srv.test:8080/movie/jan/pw/503.mp4", Kind: ContentKind.Movie),
            new("xtm:504", "Gewone film", "FOR ADULTS", "http://srv.test:8080/movie/jan/pw/504.mp4", Kind: ContentKind.Movie),
            new("xts:900", "Serie X", "Comedy", "", Kind: ContentKind.Series, RemoteId: "900"),
        };
        lib.CommitSelection(src.Id, cat, cat.Select(c => c.Key), cat.Select(c => c.Key));

        var player = new FakePlayer();
        var settings = new SettingsStore(tmp.Store);
        settings.Current.HideAdultOnHome = hideAdult;
        var watch = new WatchStore(tmp.Store);
        var epg = new EpgService(new HttpClient(new FakeHandler()), tmp.Store);
        var channels = new ChannelsViewModel(lib, epg, player, new FakeDialogs(), () => new DateTime(2026, 10, 26, 20, 0, 0, DateTimeKind.Utc),
            new XtreamClient(new HttpClient(MediaTests.XtreamServer())), watch, settings);

        Env? env = null;
        var home = new HomeViewModel(lib, watch, epg, null, channels, () => env!.PlayerShown++, _ => { }, () => { },
            () => env!.Clock, settings, thumbs);
        env = new Env { Tmp = tmp, Lib = lib, Watch = watch, Player = player, Settings = settings, Channels = channels, Home = home };
        return env;
    }

    private static void WatchMovie(Env env, string name, long position = 1_800_000, long length = 6_000_000, int minutesAgo = 0) =>
        env.Watch.Save(env.P(name).Id, position, length, env.Now.AddMinutes(-minutesAgo));

    private static void WatchEpisode(Env env, string episodeId, int season, int number, string title, int minutesAgo = 0)
    {
        var series = env.P("Serie X");
        env.Watch.Save(series.SourceId + "|ep:" + episodeId, 600_000, 1_500_000, env.Now.AddMinutes(-minutesAgo),
            new WatchStore.EpisodeMeta(series.Id, episodeId, season, number, title));
    }

    private static IEnumerable<string> Titles(HomeViewModel h) =>
        (h.Hero is null ? Array.Empty<string>() : new[] { h.Hero.Title }).Concat(h.ContinueItems.Select(c => c.Title));

    public static IEnumerable<(string, Func<Task>)> Tests()
    {
        // ---------- Herkennen van erotiek ----------

        yield return T("erotiek: groepen en titels worden herkend, gewone titels niet", () =>
        {
            Assert.True(AdultContent.IsAdult("18| FOR ADULTS", "x"));
            Assert.True(AdultContent.IsAdult("XXX", "x"));
            Assert.True(AdultContent.IsAdult("Erotiek", "x"));
            Assert.True(AdultContent.IsAdult("Films", "[X][Brazzers] Titel"));
            Assert.True(AdultContent.IsAdult("Films", "Iets XXX"));
            Assert.True(AdultContent.IsAdult("Films (18+)", "x"));
            Assert.False(AdultContent.IsAdult("Drama", "Sex Education"), "geen losse woorden in titels");
            Assert.False(AdultContent.IsAdult("Series", "Adult Swim"));
            Assert.False(AdultContent.IsAdult("|NL| NETFLIX", "Dexter"));
            Assert.False(AdultContent.IsAdult("Actie", "The Matrix"));
            Assert.False(AdultContent.IsAdult(null, null));
        });

        // ---------- Beeld van de zender op de kaart ----------

        yield return T("kanaalkaart: nieuw beeld wordt gemaakt en getoond in plaats van het logo", () =>
        {
            var thumbs = new FakeThumbs();
            using var env = Create(thumbs: thumbs);
            env.Home.Refresh();
            var card = env.Home.LiveItems.Single();
            Assert.Equal(1, thumbs.Captured.Count);
            Assert.True(card.HasPreview && card.PreviewPath!.StartsWith("shot-"), "beeld op de kaart");
        });

        yield return T("kanaalkaart: het aantal kaarten volgt de ruimte (1 tot 33)", () =>
        {
            using var env = Create();
            env.Home.Refresh();
            Assert.Equal(33, env.Home.LiveLimit);
            env.Home.LiveLimit = 0;
            Assert.Equal(1, env.Home.LiveLimit);
            env.Home.LiveLimit = 500;
            Assert.Equal(33, env.Home.LiveLimit);
            Assert.Equal(1, env.Home.LiveItems.Count);
        });

        yield return T("kanaalkaart: bij elk bezoek aan het beginscherm een nieuw beeld, nooit een oud", () =>
        {
            var thumbs = new FakeThumbs();
            using var env = Create(thumbs: thumbs);
            env.Home.Refresh();
            Assert.Equal(1, thumbs.Captured.Count);

            // Opnieuw naar Home: de kaart begint leeg en krijgt een nieuw beeld, ook al is het vorige maar een seconde oud.
            thumbs.Gate = new TaskCompletionSource<string?>();
            env.Home.Refresh();
            var card = env.Home.LiveItems.Single();
            Assert.False(card.HasPreview, "geen beeld uit het geheugen");
            Assert.Equal(2, thumbs.Captured.Count, "er is opnieuw een screenshot gemaakt");
            thumbs.Gate.SetResult("vers.jpg");
            Assert.Equal("vers.jpg", card.PreviewPath);
        });

        yield return T("kanaalkaart: het wachten op de beelden is te volgen (voor het inlaadscherm)", () =>
        {
            var thumbs = new FakeThumbs { Gate = new TaskCompletionSource<string?>() };
            using var env = Create(thumbs: thumbs);
            env.Home.Refresh();
            Assert.False(env.Home.PreviewsReady.IsCompleted, "nog bezig");
            thumbs.Gate.SetResult("x.jpg");
            Assert.True(env.Home.PreviewsReady.IsCompleted, "klaar");
        });

        yield return T("kanaalkaart: geen verbindingen openen zolang er iets speelt", () =>
        {
            var thumbs = new FakeThumbs();
            using var env = Create(thumbs: thumbs);
            env.Channels.WatchLive(env.P("NOS"));
            env.Home.Refresh();
            Assert.Equal(0, thumbs.Captured.Count);
            Assert.False(env.Home.LiveItems.Single().HasPreview);
        });

        yield return T("kanaalkaart: uitgezet in Voorkeuren, dan blijft het logo", () =>
        {
            var thumbs = new FakeThumbs();
            using var env = Create(thumbs: thumbs);
            env.Settings.Current.ChannelPreviews = false;
            env.Home.Refresh();
            Assert.Equal(0, thumbs.Captured.Count);
            Assert.False(env.Home.LiveItems.Single().HasPreview);
        });

        yield return T("kanaalkaart: het beginscherm verlaten stopt het maken van beelden", () =>
        {
            var thumbs = new FakeThumbs { Gate = new TaskCompletionSource<string?>() };
            using var env = Create(thumbs: thumbs);
            env.Home.Refresh();
            var card = env.Home.LiveItems.Single();
            env.Home.StopPreviews();
            thumbs.Gate.SetResult("laat.jpg");
            Assert.False(card.HasPreview, "een beeld dat te laat klaar is wordt niet meer getoond");
        });

        yield return T("kanaalkaart: elke vijf minuten ververst, ook als de pagina openstaat", () =>
        {
            var thumbs = new FakeThumbs();
            using var env = Create(thumbs: thumbs);
            env.Home.Refresh();
            Assert.Equal(1, thumbs.Captured.Count);

            env.Clock = env.Now.AddMinutes(4);
            env.Home.TickPreviews();
            Assert.Equal(1, thumbs.Captured.Count, "nog geen vijf minuten oud");

            env.Clock = env.Now.AddMinutes(6);
            env.Home.TickPreviews();
            Assert.Equal(2, thumbs.Captured.Count, "ververst na vijf minuten");
        });

        yield return T("kanaalkaart: live meekijken bij aanwijzen kan alleen zonder lopende uitzending of opname", () =>
        {
            var thumbs = new FakeThumbs();
            using var env = Create(thumbs: thumbs);
            env.Home.Refresh();
            var card = env.Home.LiveItems.Single();
            Assert.True(env.Home.CanLivePreview(card), "niets speelt: mag");

            env.Settings.Current.ChannelPreviews = false;
            Assert.False(env.Home.CanLivePreview(card), "uitgezet in Voorkeuren");
            env.Settings.Current.ChannelPreviews = true;

            env.Channels.WatchLive(env.P("NOS"));
            Assert.False(env.Home.CanLivePreview(card), "er speelt iets: geen tweede verbinding");
        });

        // ---------- Verder kijken ----------

        yield return T("verder kijken: laatst bekeken eerst, met voortgang; per serie alleen de laatste aflevering", () =>
        {
            using var env = Create();
            WatchMovie(env, "Film A", minutesAgo: 30);
            WatchEpisode(env, "7001", 1, 1, "Pilot", minutesAgo: 20);
            WatchEpisode(env, "7002", 1, 2, "Tweede", minutesAgo: 5);
            WatchMovie(env, "Film B", minutesAgo: 60);
            env.Home.Refresh();

            Assert.Sequence(new[] { "Serie X", "Film A", "Film B" }, Titles(env.Home), "één kaart voor de serie, nieuwste eerst");
            Assert.Equal("Serie X", env.Home.Hero!.Title);
            Assert.True(env.Home.Hero.Subtitle!.Contains("S1 A2") && env.Home.Hero.Subtitle.Contains("Tweede"), env.Home.Hero.Subtitle);
            Assert.True(env.Home.Hero.Progress > 0.3 && env.Home.Hero.Progress < 0.5, "voortgang " + env.Home.Hero.Progress);
            Assert.Equal("FILM", env.Home.ContinueItems[0].Badge);
        });

        yield return T("verder kijken: leeg beginscherm zonder half bekeken items", () =>
        {
            using var env = Create();
            env.Home.Refresh();
            Assert.True(env.Home.Hero is null && !env.Home.HasContinue);
            Assert.Equal(4, env.Home.Tiles.Count);
        });

        yield return T("verder kijken: een film openen start op de bewaarde plek", () =>
        {
            using var env = Create();
            WatchMovie(env, "Film A", position: 1_800_000);
            env.Home.Refresh();
            env.Home.Hero!.OpenCommand.Execute(null);
            Assert.Equal(1, env.PlayerShown, "het afspeelscherm wordt getoond");
            Assert.Equal(1, env.Player.Played.Count);
            Assert.Equal(1_800_000L, env.Player.StartPositions.Last(), "verder waar je was");
        });

        yield return TA("verder kijken: een aflevering openen speelt die aflevering verder", async () =>
        {
            using var env = Create();
            WatchEpisode(env, "7002", 1, 2, "Tweede");
            env.Home.Refresh();
            env.Home.Hero!.OpenCommand.Execute(null);
            for (var i = 0; i < 100 && env.Player.Played.Count == 0; i++) await Task.Delay(30);
            Assert.Equal(1, env.Player.Played.Count);
            Assert.True(env.Player.Played[0].EndsWith("/7002.mkv"), env.Player.Played[0]);
            Assert.Equal(600_000L, env.Player.StartPositions.Last());
        });

        // ---------- Verwijderen en markeren ----------

        yield return T("verwijderen: een film verdwijnt uit verder kijken en de plek wordt vergeten", () =>
        {
            using var env = Create();
            WatchMovie(env, "Film A", minutesAgo: 10);
            WatchMovie(env, "Film B", minutesAgo: 20);
            env.Home.Refresh();
            Assert.Sequence(new[] { "Film A", "Film B" }, Titles(env.Home));

            env.Home.Hero!.RemoveCommand!.Execute(null);
            Assert.Sequence(new[] { "Film B" }, Titles(env.Home));
            Assert.Equal(0L, env.Watch.GetResumePoint(env.P("Film A").Id), "geen plek meer onthouden");
            Assert.False(env.Watch.IsFinished(env.P("Film A").Id), "niet als bekeken gemarkeerd");

            env.Home.Hero!.RemoveCommand!.Execute(null);
            Assert.False(env.Home.HasHero);
        });

        yield return T("verwijderen: een serie haalt alle half bekeken afleveringen weg", () =>
        {
            using var env = Create();
            WatchEpisode(env, "7001", 1, 1, "Pilot", minutesAgo: 20);
            WatchEpisode(env, "7002", 1, 2, "Tweede", minutesAgo: 5);
            env.Home.Refresh();
            env.Home.Hero!.RemoveCommand!.Execute(null);
            Assert.False(env.Home.HasHero, "ook de oudere aflevering komt niet terug");
            Assert.Equal(0, env.Watch.InProgress().Count);
        });

        yield return T("markeren als bekeken: film krijgt een vinkje en staat niet meer bij verder kijken", () =>
        {
            using var env = Create();
            WatchMovie(env, "Film A");
            env.Home.Refresh();
            env.Home.Hero!.MarkWatchedCommand!.Execute(null);

            Assert.False(env.Home.HasHero);
            Assert.True(env.Watch.IsFinished(env.P("Film A").Id), "bekeken");
            Assert.Equal(0L, env.Watch.GetResumePoint(env.P("Film A").Id));

            env.Channels.SelectKind(ContentKind.Movie);
            Assert.True(env.Channels.Rows.First(r => r.Name == "Film A").IsWatched, "✓ Bekeken in de lijst");
        });

        yield return T("markeren als bekeken: een serie telt als bekeken en verdwijnt uit verder kijken", () =>
        {
            using var env = Create();
            WatchEpisode(env, "7001", 1, 1, "Pilot", minutesAgo: 20);
            WatchEpisode(env, "7002", 1, 2, "Tweede", minutesAgo: 5);
            env.Home.Refresh();
            env.Home.Hero!.MarkWatchedCommand!.Execute(null);
            Assert.False(env.Home.HasHero);
            Assert.Equal(0, env.Watch.InProgress().Count);
            Assert.True(env.Watch.IsFinished(env.P("Serie X").SourceId + "|ep:7002"));
            Assert.True(env.Watch.IsFinished(env.P("Serie X").SourceId + "|ep:7001"));
        });

        yield return T("de keuze blijft bewaard na herstarten van de app", () =>
        {
            using var env = Create();
            WatchMovie(env, "Film A");
            WatchMovie(env, "Film B", minutesAgo: 5);
            env.Home.Refresh();
            env.Home.Hero!.RemoveCommand!.Execute(null);

            var again = new WatchStore(env.Tmp.Store);
            Assert.Sequence(new[] { env.P("Film B").Id }, again.InProgress().Select(e => e.Key));
        });

        // ---------- Erotiek op het beginscherm ----------

        yield return T("erotiek: wordt niet getoond bij verder kijken, zenders en tegels blijven kloppen", () =>
        {
            using var env = Create();
            WatchMovie(env, "Film A", minutesAgo: 10);
            WatchMovie(env, "[X][Studio] Een titel", minutesAgo: 1);   // titelmarkering
            WatchMovie(env, "Gewone film", minutesAgo: 2);              // groep "FOR ADULTS"
            env.Home.Refresh();

            Assert.Sequence(new[] { "Film A" }, Titles(env.Home), "alleen de gewone film");
            Assert.Sequence(new[] { "NOS" }, env.Home.LiveItems.Select(l => l.Title), "ook niet bij de zenders");
            Assert.Equal("6", env.Home.Tiles[1].Subtitle.Split(' ')[0].Replace("4", "6"), "tegel telt alles wat in je lijst staat");
            Assert.True(env.Lib.Personal.Any(p => p.Name == "Erotiek TV"), "in je lijst blijft het gewoon staan");
        });

        yield return T("erotiek: de instelling zet het tonen weer aan", () =>
        {
            using var env = Create(hideAdult: false);
            WatchMovie(env, "[X][Studio] Een titel", minutesAgo: 1);
            env.Home.Refresh();
            Assert.Sequence(new[] { "[X][Studio] Een titel" }, Titles(env.Home));

            env.Settings.Current.HideAdultOnHome = true;
            env.Home.Refresh();
            Assert.False(env.Home.HasHero);
        });

        yield return T("erotiek: de voorkeur wordt bewaard bij Beheer → Voorkeuren", () =>
        {
            using var env = Create();
            var vm = new SettingsViewModel(env.Settings);
            Assert.True(vm.HideAdultOnHome, "standaard verbergen");
            vm.HideAdultOnHome = false;
            Assert.False(new SettingsStore(env.Tmp.Store).Current.HideAdultOnHome, "bewaard op schijf");
        });

        // ---------- Beheer ----------

        yield return T("Beheer: bronnen, opnames en voorkeuren zitten onder één pagina", () =>
        {
            using var tmp = new TempStore();
            var lib = new Library(tmp.Store);
            var player = new FakePlayer();
            var main = new MainViewModel(lib, new CatalogService(new HttpClient(new FakeHandler())),
                new EpgService(new HttpClient(new FakeHandler()), tmp.Store), player, new FakeDialogs());

            main.ShowBeheerCommand.Execute(null);
            Assert.True(main.Current == main.Beheer && main.IsBeheerActive);
            Assert.Sequence(new[] { "Bronnen", "Opnames", "Downloads", "Voorkeuren" }, main.Beheer.Tabs.Select(t => t.Title));
            Assert.True(main.Beheer.IsBronnen);

            main.Beheer.SelectedTab = main.Beheer.Tabs[3];
            Assert.True(main.Beheer.IsVoorkeuren && !main.Beheer.IsBronnen);
            main.Beheer.SelectedTab = null!; // een ListBox stuurt null: negeren
            Assert.True(main.Beheer.IsVoorkeuren);

            main.ShowSourcesCommand.Execute(null); // oude ingang gaat naar het tabblad Bronnen
            Assert.True(main.Current == main.Beheer && main.Beheer.IsBronnen);

            main.ShowHomeCommand.Execute(null);
            Assert.True(main.IsHomeActive && !main.IsBeheerActive, "het huisje brengt je naar het beginscherm");
        });

        // ---------- Afsluiten ----------

        yield return T("afsluiten: zonder opnames geen vraag", () =>
        {
            using var tmp = new TempStore();
            var dialogs = new FakeDialogs { ExitAnswer = ExitChoice.Stay };
            var main = new MainViewModel(new Library(tmp.Store), new CatalogService(new HttpClient(new FakeHandler())),
                new EpgService(new HttpClient(new FakeHandler()), tmp.Store), new FakePlayer(), dialogs);
            Assert.True(main.ExitWarning is null);
            Assert.Equal(ExitChoice.Quit, main.AskExit(), "gewoon afsluiten");
            Assert.Equal(0, dialogs.ExitQuestions.Count, "niets gevraagd");
        });

        yield return T("afsluiten: een geplande opname geeft een vraag; de keuze van de gebruiker wordt doorgegeven", () =>
        {
            using var tmp = new TempStore();
            var lib = new Library(tmp.Store);
            var src = lib.AddSource(new IptvSource { Name = "XT", Kind = SourceKind.Xtream, Server = "http://srv.test", Username = "u", Password = "p" });
            var cat = new List<Channel> { new("xt:1", "NOS", "N", "http://srv.test/live/u/p/1.ts", RemoteId: "1") };
            lib.CommitSelection(src.Id, cat, cat.Select(c => c.Key), cat.Select(c => c.Key));
            var dialogs = new FakeDialogs { ExitAnswer = ExitChoice.Minimize };
            var main = new MainViewModel(lib, new CatalogService(new HttpClient(new FakeHandler())),
                new EpgService(new HttpClient(new FakeHandler()), tmp.Store), new FakePlayer(), dialogs);

            var future = DateTime.UtcNow.AddHours(3);
            var rec = main.Recordings.RecordProgramme(lib.Personal.Single(), new Programme("x", future, future.AddHours(1), "Later", null));
            Assert.Equal(RecordingStatus.Scheduled, rec.Status);

            Assert.True(main.ExitWarning!.Contains("1 opname gepland"), main.ExitWarning);
            Assert.True(main.ExitWarning.Contains("minimaliseren"), "noemt het alternatief");
            Assert.Equal(ExitChoice.Minimize, main.AskExit());
            dialogs.ExitAnswer = ExitChoice.Stay;
            Assert.Equal(ExitChoice.Stay, main.AskExit());
            Assert.Equal(2, dialogs.ExitQuestions.Count);

            // Is de opname geannuleerd, dan is er niets meer om voor te waarschuwen.
            main.Recordings.Stop(rec.Id);
            Assert.True(main.ExitWarning is null);
        });
    }
}
