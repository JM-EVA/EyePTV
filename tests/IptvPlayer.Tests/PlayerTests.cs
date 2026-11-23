using IptvPlayer.Engine;
using IptvPlayer.Engine.ViewModels;

namespace IptvPlayer.Tests;

/// <summary>Afspeelpagina: tabs per soort, films met verder kijken, series, spoelen en terugkijken.</summary>
public static class PlayerTests
{
    private static (string, Func<Task>) T(string name, Action a) => ("Speler: " + name, () => { a(); return Task.CompletedTask; });
    private static (string, Func<Task>) TA(string name, Func<Task> a) => ("Speler: " + name, a);

    private sealed class Env : IDisposable
    {
        public required TempStore Tmp { get; init; }
        public required Library Lib { get; init; }
        public required FakePlayer Player { get; init; }
        public required FakeDialogs Dialogs { get; init; }
        public required EpgService Epg { get; init; }
        public required ChannelsViewModel Vm { get; set; }
        public required IptvSource Source { get; init; }
        public required DateTime Now { get; init; }
        public void Dispose() => Tmp.Dispose();

        public ChannelsViewModel NewVm(bool withXtream = true, FakeHandler? server = null) =>
            new(Lib, Epg, Player, Dialogs, () => Now,
                withXtream ? new XtreamClient(new HttpClient(server ?? MediaTests.XtreamServer())) : null);
    }

    private static string Xmltv(DateTime t) => t.ToString("yyyyMMddHHmmss") + " +0000";

    /// <summary>Een Xtream-bron met twee zenders (NOS heeft 3 dagen terugkijken), twee films en één serie; en een EPG voor NOS.</summary>
    private static async Task<Env> CreateAsync(bool withEpg = false)
    {
        var tmp = new TempStore();
        var lib = new Library(tmp.Store);
        var src = lib.AddSource(new IptvSource
        {
            Name = "XT", Kind = SourceKind.Xtream, Server = "http://srv.test:8080", Username = "jan", Password = "pw", ServerTimeZone = "UTC"
        });
        var cat = new List<Channel>
        {
            new("xt:1", "NOS", "Nieuws", "http://srv.test:8080/live/jan/pw/1.ts", EpgId: "nos", Kind: ContentKind.Live, CatchupDays: 3, RemoteId: "1"),
            new("xt:2", "RTL Z", "Nieuws", "http://srv.test:8080/live/jan/pw/2.ts", EpgId: "nos", Kind: ContentKind.Live, RemoteId: "2"),
            new("xtm:501", "Film A", "Actie", "http://srv.test:8080/movie/jan/pw/501.mkv", Kind: ContentKind.Movie, Info: "Een plot."),
            new("xtm:502", "Film B", "Drama", "http://srv.test:8080/movie/jan/pw/502.mp4", Kind: ContentKind.Movie),
            new("xts:900", "Serie X", "Comedy", "", Kind: ContentKind.Series, RemoteId: "900", Info: "Over mensen."),
        };
        lib.CommitSelection(src.Id, cat, cat.Select(c => c.Key), cat.Select(c => c.Key));

        // Vaste "nu": vandaag 12:00 UTC, zodat de dagindeling niet van het tijdstip van de testrun afhangt.
        var now = DateTime.UtcNow.Date.AddHours(12);
        var epg = new EpgService(new HttpClient(new FakeHandler()), tmp.Store);
        if (withEpg)
        {
            string P(DateTime a, DateTime b, string title) =>
                $"<programme start=\"{Xmltv(a)}\" stop=\"{Xmltv(b)}\" channel=\"nos\"><title>{title}</title><desc>Over {title}</desc></programme>";
            var xml = "<tv>" +
                P(now.AddHours(-24), now.AddHours(-23), "Gisteren") +
                P(now.AddHours(-2), now.AddHours(-1), "Eerder") +
                P(now.AddMinutes(-10), now.AddMinutes(20), "Nu") +
                P(now.AddMinutes(20), now.AddMinutes(80), "Straks") +
                "</tv>";
            epg = new EpgService(new HttpClient(new FakeHandler().Add("epg.test", xml)), tmp.Store);
            src.EpgUrl = "http://epg.test/guide.xml";
            await epg.RefreshAsync(lib.Sources, lib.Personal);
        }

        var player = new FakePlayer();
        var dialogs = new FakeDialogs();
        var env = new Env { Tmp = tmp, Lib = lib, Player = player, Dialogs = dialogs, Epg = epg, Source = src, Now = now, Vm = null! };
        env.Vm = env.NewVm();
        return env;
    }

    private static ChannelRow Row(ChannelsViewModel vm, string name) => vm.Rows.First(r => r.Name == name);

    private static void Pick(ChannelsViewModel vm, ContentKind kind) =>
        vm.SelectedKindOption = vm.KindOptions.First(k => k.Kind == kind);

    public static IEnumerable<(string, Func<Task>)> Tests()
    {
        // ---------- Tabs ----------

        yield return TA("tabs: zenders, films en series; kiezen wisselt de lijst en het groepsfilter", async () =>
        {
            using var env = await CreateAsync();
            var vm = env.Vm;
            Assert.Sequence(new[] { "Zenders", "Films", "Series" }, vm.KindOptions.Select(k => k.Title));
            Assert.True(vm.HasKindOptions);
            Assert.Sequence(new[] { "NOS", "RTL Z" }, vm.Rows.Select(r => r.Name), "begint bij zenders");

            Pick(vm, ContentKind.Movie);
            Assert.Sequence(new[] { "Film A", "Film B" }, vm.Rows.Select(r => r.Name));
            Assert.Sequence(new[] { ChannelsViewModel.AllGroups, "Actie", "Drama" }, vm.Groups, "groepen van films");

            vm.SelectedGroup = "Drama";
            Assert.Sequence(new[] { "Film B" }, vm.Rows.Select(r => r.Name));

            Pick(vm, ContentKind.Live);
            Assert.Equal(ChannelsViewModel.AllGroups, vm.SelectedGroup, "groepsfilter is gereset");

            vm.SelectedKindOption = null;
            Assert.Equal(ContentKind.Live, vm.SelectedKind, "null wist de keuze niet");
        });

        yield return T("tabs: bij alleen zenders geen tabbladen", () =>
        {
            using var tmp = new TempStore();
            var env = ChannelsEnv.Create(tmp);
            Assert.False(env.Vm.HasKindOptions);
            Assert.Equal(1, env.Vm.KindOptions.Count);
        });

        // ---------- Films ----------

        yield return TA("film: begint vooraan, onthoudt de plek bij stoppen en gaat de volgende keer verder", async () =>
        {
            using var env = await CreateAsync();
            var vm = env.Vm;
            Pick(vm, ContentKind.Movie);
            var film = Row(vm, "Film A");
            film.PlayCommand.Execute(null);
            Assert.Equal("http://srv.test:8080/movie/jan/pw/501.mkv", env.Player.Played.Last());
            Assert.Equal(0L, env.Player.StartPositions.Last(), "eerste keer vanaf het begin");
            Assert.True(vm.IsVod && vm.ShowTransport && !vm.IsLive, "film heeft de spoelbalk");
            Assert.Equal("Film A", vm.NowTitle);
            Assert.Equal("Een plot.", vm.NowDescription);

            env.Player.Tick(125_000, 6_000_000);
            Assert.True(vm.PositionFraction > 0.02 && vm.PositionFraction < 0.03, "spoelbalk");
            Assert.Equal("2:05 / 1:40:00", vm.PositionText);
            vm.StopCommand.Execute(null);

            film.PlayCommand.Execute(null);
            Assert.Equal(125_000L, env.Player.StartPositions.Last(), "verder waar je was");
            Assert.True(vm.StatusText.Contains("verder bij 2:05"), vm.StatusText);
        });

        yield return TA("film: de plek blijft bewaard na herstarten van de app", async () =>
        {
            using var env = await CreateAsync();
            Pick(env.Vm, ContentKind.Movie);
            Row(env.Vm, "Film A").PlayCommand.Execute(null);
            env.Player.Tick(300_000, 6_000_000);
            env.Vm.StopCommand.Execute(null);

            var vm2 = env.NewVm();
            Pick(vm2, ContentKind.Movie);
            Row(vm2, "Film A").PlayCommand.Execute(null);
            Assert.Equal(300_000L, env.Player.StartPositions.Last());
        });

        yield return TA("film: de eerste halve minuut telt niet, en bijna uit telt als bekeken", async () =>
        {
            using var env = await CreateAsync();
            var vm = env.Vm;
            Pick(vm, ContentKind.Movie);
            var film = Row(vm, "Film A");

            film.PlayCommand.Execute(null);
            env.Player.Tick(20_000, 6_000_000);
            vm.StopCommand.Execute(null);
            film.PlayCommand.Execute(null);
            Assert.Equal(0L, env.Player.StartPositions.Last(), "te kort om verder te gaan");

            env.Player.Tick(5_800_000, 6_000_000);
            vm.StopCommand.Execute(null);
            Assert.True(film.IsWatched, "bijna uitgekeken = bekeken");
            film.PlayCommand.Execute(null);
            Assert.Equal(0L, env.Player.StartPositions.Last(), "een bekeken film begint opnieuw");
        });

        yield return TA("film: tijdens het kijken wordt de plek regelmatig bewaard (niet alleen bij stoppen)", async () =>
        {
            using var env = await CreateAsync();
            Pick(env.Vm, ContentKind.Movie);
            var film = Row(env.Vm, "Film A");
            film.PlayCommand.Execute(null);

            env.Player.Tick(40_000, 6_000_000);
            Assert.Equal(40_000L, new WatchStore(env.Tmp.Store).GetResumePoint(film.Model.Id), "eerste meting wordt bewaard");
            env.Player.Tick(45_000, 6_000_000);
            Assert.Equal(40_000L, new WatchStore(env.Tmp.Store).GetResumePoint(film.Model.Id), "geen schrijfacties bij elke tik");
            env.Player.Tick(60_000, 6_000_000);
            Assert.Equal(60_000L, new WatchStore(env.Tmp.Store).GetResumePoint(film.Model.Id), "na 15 seconden weer");
        });

        yield return TA("film: stoppen voordat de speler een positie meldde wist de bewaarde plek niet", async () =>
        {
            using var env = await CreateAsync();
            Pick(env.Vm, ContentKind.Movie);
            var film = Row(env.Vm, "Film A");
            film.PlayCommand.Execute(null);
            env.Player.Tick(500_000, 6_000_000);
            env.Vm.StopCommand.Execute(null);

            film.PlayCommand.Execute(null);          // hervat bij 500 s, maar de speler meldt nog niets
            env.Vm.StopCommand.Execute(null);
            film.PlayCommand.Execute(null);
            Assert.Equal(500_000L, env.Player.StartPositions.Last());
        });

        yield return TA("film: uitgespeeld telt als bekeken en wordt zo getoond", async () =>
        {
            using var env = await CreateAsync();
            Pick(env.Vm, ContentKind.Movie);
            var film = Row(env.Vm, "Film A");
            film.PlayCommand.Execute(null);
            env.Player.Tick(1_000_000, 6_000_000);
            env.Player.End();
            Assert.True(film.IsWatched);
            Assert.True(env.Vm.StatusText.Contains("afgelopen"), env.Vm.StatusText);
            Assert.False(Row(env.Vm, "Film B").IsWatched);
        });

        // ---------- Pauze en spoelen ----------

        yield return TA("spoelen: +15 s en -15 s, en loslaten van de balk springt naar dat punt", async () =>
        {
            using var env = await CreateAsync();
            Pick(env.Vm, ContentKind.Movie);
            Row(env.Vm, "Film A").PlayCommand.Execute(null);
            env.Player.Tick(100_000, 6_000_000);

            env.Vm.SeekForwardCommand.Execute(null);
            Assert.Equal(115_000L, env.Player.Seeks.Last());
            env.Vm.SeekBackCommand.Execute(null);
            Assert.Equal(100_000L, env.Player.Seeks.Last());

            env.Vm.BeginSeek();
            env.Player.Tick(121_000, 6_000_000); // tijdens het slepen niet teruggezet
            Assert.True(env.Vm.PositionFraction < 0.03);
            env.Vm.EndSeek(0.5);
            Assert.Equal(3_000_000L, env.Player.Seeks.Last());
            Assert.True(Math.Abs(env.Vm.PositionFraction - 0.5) < 0.001);

            env.Vm.SeekRelative(-99_999_999);
            Assert.Equal(0L, env.Player.Seeks.Last(), "niet voor het begin");
            env.Vm.SeekRelative(+99_999_999);
            Assert.True(env.Player.Seeks.Last() < 6_000_000, "niet voorbij het einde");
        });

        yield return TA("spoelen: niet mogelijk bij live-tv of als de speler niet kan spoelen", async () =>
        {
            using var env = await CreateAsync();
            Row(env.Vm, "NOS").PlayCommand.Execute(null);
            Assert.False(env.Vm.ShowTransport, "live heeft geen spoelbalk");
            Assert.False(env.Vm.CanSeek);
            env.Vm.SeekForwardCommand.Execute(null);
            env.Vm.TogglePauseCommand.Execute(null);
            Assert.Equal(0, env.Player.Seeks.Count);
            Assert.False(env.Player.IsPaused, "pauzeren hoort niet bij live");

            Pick(env.Vm, ContentKind.Movie);
            env.Player.IsSeekable = false;
            Row(env.Vm, "Film A").PlayCommand.Execute(null);
            env.Player.Tick(10_000, 6_000_000);
            Assert.False(env.Vm.CanSeek);
            env.Vm.SeekForwardCommand.Execute(null);
            Assert.Equal(0, env.Player.Seeks.Count);
        });

        yield return TA("pauze: schakelt de speler en de status", async () =>
        {
            using var env = await CreateAsync();
            Pick(env.Vm, ContentKind.Movie);
            Row(env.Vm, "Film A").PlayCommand.Execute(null);
            env.Vm.TogglePauseCommand.Execute(null);
            Assert.True(env.Player.IsPaused && env.Vm.IsPaused);
            env.Vm.TogglePauseCommand.Execute(null);
            Assert.False(env.Vm.IsPaused);
        });

        // ---------- Series ----------

        yield return TA("serie: openen toont seizoenen en de afleveringen van het eerste seizoen", async () =>
        {
            using var env = await CreateAsync();
            var vm = env.Vm;
            Pick(vm, ContentKind.Series);
            var serie = Row(vm, "Serie X");
            await vm.OpenSeriesAsync(serie);

            Assert.True(vm.IsSeriesOpen && !vm.ShowList);
            Assert.Equal("Serie X", vm.SeriesTitle);
            Assert.Sequence(new[] { "Seizoen 1", "Seizoen 2" }, vm.Seasons.Select(s => s.Title));
            Assert.Equal("Seizoen 1", vm.SelectedSeason!.Title);
            Assert.Sequence(new[] { "1. Pilot", "2. Tweede" }, vm.Episodes.Select(e => e.Title));
            Assert.Equal("25:00", vm.Episodes[0].DurationText);
            Assert.False(vm.IsLoadingSeries);
            Assert.Equal(0, env.Player.Played.Count, "openen speelt nog niets af");

            vm.SelectedSeason = vm.Seasons[1];
            Assert.Sequence(new[] { "1. Terug" }, vm.Episodes.Select(e => e.Title));

            vm.CloseSeriesCommand.Execute(null);
            Assert.True(vm.ShowList && !vm.IsSeriesOpen);
            Assert.Equal(0, vm.Episodes.Count);
        });

        yield return TA("serie: klikken op een serie in de lijst opent hem; afleveringen worden maar één keer opgehaald", async () =>
        {
            using var env = await CreateAsync();
            var server = MediaTests.XtreamServer();
            var vm = env.NewVm(server: server);
            Pick(vm, ContentKind.Series);
            await vm.OpenSeriesAsync(Row(vm, "Serie X"));
            vm.CloseSeries();
            await vm.OpenSeriesAsync(Row(vm, "Serie X"));
            Assert.Equal(1, server.Requests.Count(u => u.ToString().Contains("get_series_info")), "tweede keer uit het geheugen");
            Assert.Equal(2, vm.Episodes.Count);
        });

        yield return TA("serie: een aflevering afspelen, verder kijken, en volgende/vorige aflevering", async () =>
        {
            using var env = await CreateAsync();
            var vm = env.Vm;
            Pick(vm, ContentKind.Series);
            await vm.OpenSeriesAsync(Row(vm, "Serie X"));

            vm.Episodes[0].PlayCommand.Execute(null);
            Assert.Equal("http://srv.test:8080/series/jan/pw/7001.mp4", env.Player.Played.Last());
            Assert.True(vm.NowTitle!.Contains("Serie X") && vm.NowTitle.Contains("S1 A1"), vm.NowTitle);
            Assert.Equal("Begin", vm.NowDescription);
            Assert.True(vm.Episodes[0].IsPlaying && !vm.Episodes[1].IsPlaying);
            Assert.True(vm.ShowTransport);

            env.Player.Tick(600_000, 1_500_000);
            vm.NextCommand.Execute(null);
            Assert.Equal("http://srv.test:8080/series/jan/pw/7002.mkv", env.Player.Played.Last(), "volgende aflevering");
            Assert.Equal("Over mensen.", vm.NowDescription, "zonder eigen plot de plot van de serie");
            vm.NextCommand.Execute(null);
            Assert.Equal("http://srv.test:8080/series/jan/pw/7101.mp4", env.Player.Played.Last(), "ook over het seizoen heen");
            var count = env.Player.Played.Count;
            vm.NextCommand.Execute(null);
            Assert.Equal(count, env.Player.Played.Count, "na de laatste aflevering stopt het");
            vm.PreviousCommand.Execute(null);
            Assert.Equal("http://srv.test:8080/series/jan/pw/7002.mkv", env.Player.Played.Last());

            // De eerste aflevering was bij 10:00 verlaten: daar gaat hij verder.
            vm.SelectedSeason = vm.Seasons[0];
            Assert.Equal("Verder bij 10:00", vm.Episodes[0].ResumeText);
            vm.Episodes[0].PlayCommand.Execute(null);
            Assert.Equal(600_000L, env.Player.StartPositions.Last());
        });

        yield return TA("serie: na het einde van een aflevering start automatisch de volgende; bij de laatste stopt het", async () =>
        {
            using var env = await CreateAsync();
            var vm = env.Vm;
            Pick(vm, ContentKind.Series);
            await vm.OpenSeriesAsync(Row(vm, "Serie X"));
            vm.Episodes[0].PlayCommand.Execute(null);
            env.Player.Tick(1_490_000, 1_500_000);
            env.Player.End();

            Assert.Equal("http://srv.test:8080/series/jan/pw/7002.mkv", env.Player.Played.Last());
            Assert.True(vm.Episodes[0].IsWatched, "eerste aflevering is bekeken");
            Assert.Equal(0L, env.Player.StartPositions.Last());

            env.Player.Tick(100_000, 1_000_000);
            env.Player.End();
            env.Player.Tick(100_000, 1_000_000);
            env.Player.End();
            var count = env.Player.Played.Count;
            env.Player.End();
            Assert.Equal(count, env.Player.Played.Count, "geen aflevering meer");
            Assert.True(vm.StatusText.Contains("afgelopen"), vm.StatusText);
        });

        yield return TA("serie: een volgende keer begint bij het seizoen waar je gebleven bent", async () =>
        {
            using var env = await CreateAsync();
            var vm = env.Vm;
            Pick(vm, ContentKind.Series);
            await vm.OpenSeriesAsync(Row(vm, "Serie X"));
            vm.Episodes[0].PlayCommand.Execute(null);
            env.Player.Tick(1_490_000, 1_500_000); env.Player.End();   // S1A1 uit
            env.Player.Tick(990_000, 1_000_000); env.Player.End();     // S1A2 uit; S2A1 start
            vm.StopCommand.Execute(null);

            var vm2 = env.NewVm();
            Pick(vm2, ContentKind.Series);
            await vm2.OpenSeriesAsync(Row(vm2, "Serie X"));
            Assert.Equal("Seizoen 2", vm2.SelectedSeason!.Title);
        });

        yield return TA("serie: een mislukte opvraag geeft een melding in plaats van een fout", async () =>
        {
            using var env = await CreateAsync();
            var vm = env.NewVm(server: new FakeHandler());   // elke aanvraag geeft 404
            Pick(vm, ContentKind.Series);
            await vm.OpenSeriesAsync(Row(vm, "Serie X"));
            Assert.True(vm.HasSeriesError, "foutmelding");
            Assert.False(vm.IsLoadingSeries);
            Assert.Equal(0, vm.Episodes.Count);
            Assert.True(vm.IsSeriesOpen, "je kunt terug naar de lijst");
        });

        yield return TA("serie: zonder Xtream-verbinding een duidelijke melding", async () =>
        {
            using var env = await CreateAsync();
            var vm = env.NewVm(withXtream: false);
            Pick(vm, ContentKind.Series);
            await vm.OpenSeriesAsync(Row(vm, "Serie X"));
            Assert.True(vm.SeriesError!.Contains("Xtream"), vm.SeriesError);
        });

        yield return TA("serie: wisselen van tabblad sluit de serie, en een serie kiezen in de lijst opent hem", async () =>
        {
            using var env = await CreateAsync();
            var vm = env.Vm;
            Pick(vm, ContentKind.Series);
            Row(vm, "Serie X").PlayCommand.Execute(null);   // doet hetzelfde als openen
            Assert.True(vm.IsSeriesOpen);
            Pick(vm, ContentKind.Movie);
            Assert.False(vm.IsSeriesOpen);
        });

        yield return TA("serie: een serie alleen is niet afspeelbaar bij volgende/vorige", async () =>
        {
            using var env = await CreateAsync();
            Pick(env.Vm, ContentKind.Series);
            env.Vm.NextCommand.Execute(null);
            Assert.Equal(0, env.Player.Played.Count);
        });

        // ---------- Terugkijken ----------

        yield return TA("gids: dagen, verleden en terugkijkbare programma's voor een zender met archief", async () =>
        {
            using var env = await CreateAsync(withEpg: true);
            var vm = env.Vm;
            Row(vm, "NOS").PlayCommand.Execute(null);

            Assert.True(vm.CurrentHasCatchup);
            Assert.True(vm.HasScheduleDays);
            Assert.Sequence(new[] { "Gisteren", "Vandaag" }, vm.ScheduleDays.Select(d => d.Title));
            Assert.Equal("Vandaag", vm.SelectedScheduleDay!.Title);
            Assert.Sequence(new[] { "Eerder", "Nu", "Straks" }, vm.Schedule.Select(p => p.Title));

            var eerder = vm.Schedule[0]; var nu = vm.Schedule[1]; var straks = vm.Schedule[2];
            Assert.True(eerder.IsPast && eerder.CanReplay, "afgelopen programma kan terug");
            Assert.True(nu.IsNow && nu.CanReplay && !nu.IsPast, "lopend programma kan vanaf het begin");
            Assert.False(straks.CanReplay, "toekomst kan niet terug");

            vm.SelectedScheduleDay = vm.ScheduleDays[0];
            Assert.Sequence(new[] { "Gisteren" }, vm.Schedule.Select(p => p.Title));
            Assert.True(vm.Schedule[0].CanReplay);
        });

        yield return TA("terugkijken: een programma vanaf het begin afspelen en terug naar live", async () =>
        {
            using var env = await CreateAsync(withEpg: true);
            var vm = env.Vm;
            Row(vm, "NOS").PlayCommand.Execute(null);
            var eerder = vm.Schedule.First(p => p.Title == "Eerder");

            eerder.ReplayCommand.Execute(null);
            var url = env.Player.Played.Last();
            Assert.True(url.StartsWith("http://srv.test:8080/timeshift/jan/pw/60/"), url);
            Assert.True(url.Contains(env.Now.AddHours(-2).ToString("yyyy-MM-dd:HH-mm")), "starttijd in de link: " + url);
            Assert.True(url.EndsWith("/1.ts"), url);
            Assert.True(vm.IsReplay && !vm.IsLive && vm.ShowTransport);
            Assert.Equal("Eerder", vm.NowTitle);
            Assert.True(vm.Schedule.First(p => p.Title == "Eerder").IsReplaying, "gemarkeerd in de gids");
            Assert.False(vm.Schedule.First(p => p.Title == "Nu").IsReplaying);

            vm.Tick(); // de minuutklok mag het teruggekeken programma niet overschrijven
            Assert.Equal("Eerder", vm.NowTitle);

            vm.BackToLiveCommand.Execute(null);
            Assert.Equal("http://srv.test:8080/live/jan/pw/1.ts", env.Player.Played.Last());
            Assert.True(vm.IsLive && !vm.IsReplay);
            Assert.Equal("Nu", vm.NowTitle);
            Assert.False(vm.Schedule.Any(p => p.IsReplaying));
        });

        yield return TA("terugkijken: de voortgang volgt de tijd binnen het programma", async () =>
        {
            using var env = await CreateAsync(withEpg: true);
            Row(env.Vm, "NOS").PlayCommand.Execute(null);
            env.Vm.Schedule.First(p => p.Title == "Eerder").ReplayCommand.Execute(null);
            env.Player.Tick(1_800_000, 0); // 30 min van 60
            Assert.True(Math.Abs(env.Vm.Progress - 0.5) < 0.01, $"voortgang {env.Vm.Progress:0.00}");
            Assert.Equal("30:00 / 1:00:00", env.Vm.PositionText, "de tijdlijn is het hele programma");
            Assert.True(env.Vm.CanSeek, "bij terugkijken kan altijd worden gespoeld");
        });

        yield return TA("terugkijken: spoelen start de stream opnieuw op het gekozen punt van het programma", async () =>
        {
            using var env = await CreateAsync(withEpg: true);
            var vm = env.Vm;
            Row(vm, "NOS").PlayCommand.Execute(null);
            vm.Schedule.First(p => p.Title == "Eerder").ReplayCommand.Execute(null);
            var played = env.Player.Played.Count;

            // Loslaten van de balk halverwege: nieuwe link die 30 minuten later begint.
            vm.BeginSeek();
            vm.EndSeek(0.5);
            Assert.Equal(played + 1, env.Player.Played.Count, "nieuwe stream gestart");
            var url = env.Player.Played.Last();
            Assert.True(url.Contains(env.Now.AddHours(-2).AddMinutes(30).ToString("yyyy-MM-dd:HH-mm")), "start op +30 min: " + url);
            Assert.True(vm.IsReplay, "blijft terugkijken");

            // De speler telt nu vanaf 0 in de nieuwe stream; de tijdlijn rekent het offset erbij.
            env.Player.Tick(10_000, 0);
            Assert.Equal("30:10 / 1:00:00", vm.PositionText);

            // 15 seconden terug en vooruit.
            vm.SeekForwardCommand.Execute(null);
            Assert.Equal(played + 2, env.Player.Played.Count);
            Assert.Equal("30:25 / 1:00:00", vm.PositionText);
            vm.SeekBackCommand.Execute(null);
            Assert.Equal("30:10 / 1:00:00", vm.PositionText);

            // Niet voor het begin en niet tot aan het einde.
            vm.SeekRelative(-99_999_999);
            Assert.Equal("0:00 / 1:00:00", vm.PositionText.Replace("00:00 /", "0:00 /"));
        });

        yield return TA("startscherm: zichtbaar zolang er niets speelt, weg zodra er iets speelt", async () =>
        {
            using var env = await CreateAsync();
            Assert.True(env.Vm.ShowStartScreen);
            Assert.False(env.Vm.ShowLoading);
            Row(env.Vm, "NOS").PlayCommand.Execute(null);
            Assert.False(env.Vm.ShowStartScreen);
            Assert.True(env.Vm.ShowLoading && !env.Vm.ShowVideo && env.Vm.ShowCover, "laadscherm tot het eerste beeld (geen wit vlak)");
            env.Player.Tick(1_000, 0);
            Assert.True(env.Vm.ShowVideo && !env.Vm.ShowLoading && !env.Vm.ShowCover, "beeld zodra de speler tikt");
            Row(env.Vm, "NOS").PlayCommand.Execute(null);
            Assert.True(env.Vm.ShowLoading, "opnieuw laden bij een nieuw item");
            env.Vm.StopCommand.Execute(null);
            Assert.True(env.Vm.ShowStartScreen && !env.Vm.ShowVideo);
        });

        yield return TA("menu: Live, Films en Series zijn los te kiezen, met tellers", async () =>
        {
            using var env = await CreateAsync();
            var vm = env.Vm;
            vm.SelectKind(ContentKind.Movie);
            Assert.Equal(ContentKind.Movie, vm.SelectedKind);
            Assert.True(vm.MovieCount > 0 && vm.LiveCount > 0);
            Assert.True(vm.Rows.All(r => r.Kind == ContentKind.Movie));
            Assert.Equal("Film", vm.Rows[0].KindLabel);
            vm.SelectKind(ContentKind.Live);
            Assert.Equal("", vm.Rows[0].KindLabel);
        });

        yield return TA("terugkijken: niet voor zenders zonder archief, toekomst of na wisselen van zender", async () =>
        {
            using var env = await CreateAsync(withEpg: true);
            var vm = env.Vm;

            Row(vm, "RTL Z").PlayCommand.Execute(null);
            Assert.False(vm.CurrentHasCatchup);
            Assert.True(vm.Schedule.Count > 0 && vm.Schedule.All(p => !p.CanReplay), "geen terugkijkknoppen");
            var played = env.Player.Played.Count;
            vm.Schedule[0].ReplayCommand.Execute(null);
            Assert.Equal(played, env.Player.Played.Count, "niets afgespeeld");

            Row(vm, "NOS").PlayCommand.Execute(null);
            vm.PlayProgramme(vm.Schedule.First(p => p.Title == "Straks"));
            Assert.Equal(played + 1, env.Player.Played.Count, "toekomst is niet terug te kijken (alleen live gestart)");
            Assert.True(vm.StatusText.Contains("niet"), vm.StatusText);
        });

        yield return TA("terugkijken: een andere zender kiezen stopt het terugkijken en toont weer de nieuwe zender", async () =>
        {
            using var env = await CreateAsync(withEpg: true);
            var vm = env.Vm;
            Row(vm, "NOS").PlayCommand.Execute(null);
            vm.Schedule.First(p => p.Title == "Eerder").ReplayCommand.Execute(null);
            vm.SelectedScheduleDay = vm.ScheduleDays[0];

            Row(vm, "RTL Z").PlayCommand.Execute(null);
            Assert.True(vm.IsLive);
            Assert.Equal("Vandaag", vm.SelectedScheduleDay!.Title, "gids begint weer bij vandaag");
            Assert.False(vm.Schedule.Any(p => p.IsReplaying));
        });

        yield return TA("lijst: films en series tonen geen 'nu bezig' en de gids is leeg", async () =>
        {
            using var env = await CreateAsync(withEpg: true);
            var vm = env.Vm;
            Pick(vm, ContentKind.Movie);
            Row(vm, "Film A").PlayCommand.Execute(null);
            Assert.Equal(0, vm.Schedule.Count);
            Assert.Equal(0, vm.ScheduleDays.Count);
            Assert.True(vm.Rows.All(r => r.NowTitle == null));
            Assert.False(vm.CurrentHasCatchup);
        });

        // ---------- Verwijderen ----------

        yield return TA("verwijderen: een film uit de lijst halen stopt hem en vraagt bevestiging met 'film'", async () =>
        {
            using var env = await CreateAsync();
            Pick(env.Vm, ContentKind.Movie);
            var film = Row(env.Vm, "Film A");
            film.PlayCommand.Execute(null);
            env.Dialogs.ConfirmAnswer = false;
            film.RemoveCommand.Execute(null);
            Assert.Equal(2, env.Vm.Rows.Count, "niet verwijderd");
            env.Dialogs.ConfirmAnswer = true;
            film.RemoveCommand.Execute(null);
            Assert.Equal(1, env.Vm.Rows.Count);
            Assert.Equal(1, env.Player.StopCount);
            Assert.Equal(ContentKind.Movie, env.Vm.SelectedKind);
        });
    }
}
