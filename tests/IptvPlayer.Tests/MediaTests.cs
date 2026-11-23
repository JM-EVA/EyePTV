using System.Net;
using System.Text.Json;
using IptvPlayer.Engine;
using IptvPlayer.Engine.ViewModels;

namespace IptvPlayer.Tests;

/// <summary>Films, series en terugkijken (catch-up): bronnen, sync-bescherming, links en selectiescherm.</summary>
public static class MediaTests
{
    private static (string, Func<Task>) T(string name, Action a) => ("Media: " + name, () => { a(); return Task.CompletedTask; });
    private static (string, Func<Task>) TA(string name, Func<Task> a) => ("Media: " + name, a);

    private static IptvSource Xt(string tz = "Europe/Amsterdam") => new()
    {
        Name = "XT", Kind = SourceKind.Xtream, Server = "http://srv.test:8080", Username = "jan", Password = "pw", ServerTimeZone = tz
    };

    /// <summary>Nep-Xtream-server met zenders, films en series. Later toegevoegde routes gaan voor.</summary>
    public static FakeHandler XtreamServer(bool vodWorks = true, Func<bool>? vodEmpty = null)
    {
        var h = new FakeHandler()
            .Add("player_api.php?username=jan&password=pw", "{\"user_info\":{\"auth\":1,\"status\":\"Active\"},\"server_info\":{\"timezone\":\"Europe/Amsterdam\"}}")
            .Add("action=get_live_categories", "[{\"category_id\":\"1\",\"category_name\":\"Nieuws\"}]")
            .Add("action=get_live_streams",
                "[{\"stream_id\":1,\"name\":\"NOS\",\"category_id\":\"1\",\"tv_archive\":1,\"tv_archive_duration\":\"3\"}," +
                "{\"stream_id\":2,\"name\":\"RTL Z\",\"category_id\":\"1\",\"tv_archive\":0}]");

        if (vodWorks)
        {
            h.Add("action=get_vod_categories", "[{\"category_id\":\"10\",\"category_name\":\"Actie\"},{\"category_id\":\"11\",\"category_name\":\"Drama\"}]")
             .Add("action=get_vod_streams", () => vodEmpty?.Invoke() == true ? "[]" :
                "[{\"stream_id\":501,\"name\":\"Film A\",\"category_id\":\"10\",\"container_extension\":\"mkv\",\"stream_icon\":\"http://i/a.jpg\"}," +
                "{\"stream_id\":502,\"name\":\"Film B\",\"category_id\":\"11\"}]")
             .Add("action=get_series", "[{\"series_id\":900,\"name\":\"Serie X\",\"category_id\":\"20\",\"cover\":\"http://i/x.jpg\",\"plot\":\"Over mensen.\"}]")
             .Add("action=get_series_categories", "[{\"category_id\":\"20\",\"category_name\":\"Comedy\"}]")
             .Add("action=get_series_info",
                "{\"episodes\":{\"1\":[{\"id\":\"7001\",\"episode_num\":1,\"title\":\"Pilot\",\"container_extension\":\"mp4\",\"info\":{\"duration_secs\":1500,\"plot\":\"Begin\"}}," +
                "{\"id\":\"7002\",\"episode_num\":2,\"title\":\"Tweede\",\"container_extension\":\"mkv\"}],\"2\":[{\"id\":\"7101\",\"episode_num\":1,\"title\":\"Terug\",\"container_extension\":\"mp4\"}]}}");
        }
        return h;
    }

    public static IEnumerable<(string, Func<Task>)> Tests()
    {
        // ---------- M3U ----------

        yield return T("M3U: films worden herkend aan /movie/ of videobestand, de rest is live", () =>
        {
            var pl = M3uParser.Parse("#EXTM3U\n" +
                "#EXTINF:-1,Live\nhttp://a/live/u/p/1.ts\n" +
                "#EXTINF:-1,Film 1\nhttp://a/movie/u/p/2.mkv\n" +
                "#EXTINF:-1,Aflevering\nhttp://a/series/u/p/3.mp4\n" +
                "#EXTINF:-1,Los bestand\nhttp://a/files/film.AVI?token=1\n" +
                "#EXTINF:-1,Stream\nhttp://a/stream/abc.m3u8\n");
            Assert.Sequence(new[] { ContentKind.Live, ContentKind.Movie, ContentKind.Movie, ContentKind.Movie, ContentKind.Live },
                pl.Channels.Select(c => c.Kind));
        });

        yield return T("M3U: catchup-attributen worden ingelezen", () =>
        {
            var pl = M3uParser.Parse("#EXTM3U\n" +
                "#EXTINF:-1 catchup=\"shift\" catchup-days=\"5\",Met archief\nhttp://a/1\n" +
                "#EXTINF:-1 catchup=\"append\" catchup-source=\"?t={utc}\",Zonder dagen\nhttp://a/2\n" +
                "#EXTINF:-1 tvg-rec=\"2\" catchup=\"default\" catchup-source=\"http://a/x/{utc}\",Rec\nhttp://a/3\n" +
                "#EXTINF:-1 catchup=\"none\",Geen\nhttp://a/4\n" +
                "#EXTINF:-1,Niets\nhttp://a/5\n");
            var c = pl.Channels;
            Assert.Equal(5, c[0].CatchupDays);
            Assert.Equal("shift", c[0].CatchupType);
            Assert.Equal(7, c[1].CatchupDays, "standaard aantal dagen");
            Assert.Equal("?t={utc}", c[1].CatchupSource);
            Assert.Equal(2, c[2].CatchupDays);
            Assert.Equal(0, c[3].CatchupDays);
            Assert.Equal(0, c[4].CatchupDays);
        });

        yield return T("M3U: een film heeft nooit terugkijken", () =>
        {
            var pl = M3uParser.Parse("#EXTM3U\n#EXTINF:-1 catchup=\"shift\" catchup-days=\"5\",Film\nhttp://a/movie/u/p/2.mkv\n");
            Assert.Equal(0, pl.Channels[0].CatchupDays);
        });

        yield return T("opslag: oude lijsten zonder de nieuwe velden blijven leesbaar (alles is dan live)", () =>
        {
            var old = "{\"key\":\"k\",\"name\":\"Oud\",\"group\":\"G\",\"url\":\"http://u\",\"logoUrl\":null,\"epgId\":\"x\"}";
            var ch = JsonSerializer.Deserialize<Channel>(old, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.Equal(ContentKind.Live, ch.Kind);
            Assert.Equal(0, ch.CatchupDays);
            Assert.Equal("x", ch.EpgId);
        });

        // ---------- Xtream ----------

        yield return TA("Xtream: zenders met archief krijgen catchup-dagen en stream_id", async () =>
        {
            var list = await new XtreamClient(new HttpClient(XtreamServer())).GetLiveChannelsAsync(Xt());
            Assert.Equal(3, list.First(c => c.Name == "NOS").CatchupDays, "NOS heeft 3 dagen archief");
            Assert.Equal(0, list.First(c => c.Name == "RTL Z").CatchupDays);
            Assert.Equal("1", list.First(c => c.Name == "NOS").RemoteId);
        });

        yield return TA("Xtream: films krijgen een /movie/-link met de juiste extensie", async () =>
        {
            var list = await new XtreamClient(new HttpClient(XtreamServer())).GetMoviesAsync(Xt());
            Assert.Equal(2, list.Count);
            Assert.Equal("xtm:501", list[0].Key);
            Assert.Equal(ContentKind.Movie, list[0].Kind);
            Assert.Equal("Actie", list[0].Group);
            Assert.Equal("http://srv.test:8080/movie/jan/pw/501.mkv", list[0].Url);
            Assert.Equal("http://srv.test:8080/movie/jan/pw/502.mp4", list[1].Url, "standaard mp4");
        });

        yield return TA("Xtream: een serie heeft geen eigen link maar wel een id en beschrijving", async () =>
        {
            var list = await new XtreamClient(new HttpClient(XtreamServer())).GetSeriesAsync(Xt());
            var s = list.Single();
            Assert.Equal("xts:900", s.Key);
            Assert.Equal(ContentKind.Series, s.Kind);
            Assert.Equal("", s.Url);
            Assert.Equal("900", s.RemoteId);
            Assert.Equal("Over mensen.", s.Info);
            Assert.Equal("http://i/x.jpg", s.LogoUrl);
        });

        yield return TA("Xtream: afleveringen komen gesorteerd per seizoen met /series/-links", async () =>
        {
            var eps = await new XtreamClient(new HttpClient(XtreamServer())).GetEpisodesAsync(Xt(), "900");
            Assert.Equal(3, eps.Count);
            Assert.Sequence(new[] { "S1E1", "S1E2", "S2E1" }, eps.Select(e => $"S{e.Season}E{e.Number}"));
            Assert.Equal("http://srv.test:8080/series/jan/pw/7001.mp4", eps[0].Url);
            Assert.Equal("http://srv.test:8080/series/jan/pw/7002.mkv", eps[1].Url);
            Assert.Equal(1500, eps[0].DurationSeconds);
            Assert.Equal("Begin", eps[0].Plot);
        });

        yield return TA("Xtream: afleveringen als lijst-van-lijsten of zonder 'episodes' geven geen fout", async () =>
        {
            var h = new FakeHandler()
                .Add("player_api.php", "{\"episodes\":[[{\"id\":1,\"episode_num\":\"1\",\"title\":\"A\"}],[{\"id\":2,\"episode_num\":\"1\",\"title\":\"B\",\"season\":\"2\"}]]}");
            var eps = await new XtreamClient(new HttpClient(h)).GetEpisodesAsync(Xt(), "9");
            Assert.Sequence(new[] { 1, 2 }, eps.Select(e => e.Season));

            var none = new FakeHandler().Add("player_api.php", "{\"info\":{}}");
            Assert.Equal(0, (await new XtreamClient(new HttpClient(none)).GetEpisodesAsync(Xt(), "9")).Count);
        });

        yield return TA("Xtream: catalogus bevat alles en kent de tijdzone van de server", async () =>
        {
            var cat = await new XtreamClient(new HttpClient(XtreamServer())).GetCatalogAsync(Xt());
            Assert.Equal(2, cat.Channels.Count(c => c.Kind == ContentKind.Live));
            Assert.Equal(2, cat.Channels.Count(c => c.Kind == ContentKind.Movie));
            Assert.Equal(1, cat.Channels.Count(c => c.Kind == ContentKind.Series));
            Assert.Equal("Europe/Amsterdam", cat.ServerTimeZone);
            Assert.Equal(0, cat.Warnings?.Count ?? 0, "geen waarschuwingen");
        });

        yield return TA("Xtream: als films niet lukken, werkt de rest en krijg je een waarschuwing", async () =>
        {
            var cat = await new XtreamClient(new HttpClient(XtreamServer(vodWorks: false))).GetCatalogAsync(Xt());
            Assert.Equal(2, cat.Channels.Count(c => c.Kind == ContentKind.Live));
            Assert.True(cat.IncompleteKinds is { } inc && inc.Contains(ContentKind.Movie) && inc.Contains(ContentKind.Series), "incompleet");
            Assert.Equal(2, cat.Warnings!.Count);
            Assert.True(cat.Warnings[0].StartsWith("Films"), cat.Warnings[0]);
        });

        // ---------- Sync-bescherming ----------

        yield return TA("sync: een tijdelijk mislukte filmlijst markeert je films niet als verdwenen", async () =>
        {
            using var tmp = new TempStore();
            var lib = new Library(tmp.Store);
            var good = XtreamServer();
            var handler = good;
            var svc = new CatalogService(new HttpClient(new SwitchHandler(() => handler)));
            var src = lib.AddSource(Xt());

            var first = await lib.SyncAsync(src, svc);
            lib.CommitSelection(src.Id, first.Catalog.Channels, first.Catalog.Channels.Select(c => c.Key), first.Catalog.Channels.Select(c => c.Key));
            Assert.Equal(5, lib.Personal.Count);

            handler = XtreamServer(vodWorks: false); // provider haperde
            var second = await lib.SyncAsync(src, svc);
            Assert.Equal(SyncOutcomeKind.UpToDate, second.Kind);
            Assert.Equal(0, lib.Personal.Count(p => p.MissingAtSource), "niets als verdwenen gemarkeerd");
            Assert.True(second.Catalog.Warnings!.Count > 0, "gebruiker krijgt een waarschuwing");
            Assert.Equal(3, second.Catalog.Channels.Count(c => c.Kind != ContentKind.Live), "films/series uit de vorige lijst behouden");
            Assert.Equal("Europe/Amsterdam", src.ServerTimeZone);
        });

        yield return TA("sync: een filmlijst die plotseling leeg is wordt niet geloofd", async () =>
        {
            using var tmp = new TempStore();
            var lib = new Library(tmp.Store);
            var empty = false;
            var svc = new CatalogService(new HttpClient(XtreamServer(vodEmpty: () => empty)));
            var src = lib.AddSource(Xt());
            var first = await lib.SyncAsync(src, svc);
            lib.CommitSelection(src.Id, first.Catalog.Channels, first.Catalog.Channels.Select(c => c.Key), first.Catalog.Channels.Select(c => c.Key));

            empty = true;
            var second = await lib.SyncAsync(src, svc);
            Assert.Equal(0, lib.Personal.Count(p => p.MissingAtSource));
            Assert.Equal(2, second.Catalog.Channels.Count(c => c.Kind == ContentKind.Movie), "vorige films behouden");
            Assert.True(second.Catalog.Warnings!.Any(w => w.StartsWith("Films")), "waarschuwing");
        });

        yield return TA("sync: nieuwe films komen als 'nieuw' in de review, films die je negeerde niet", async () =>
        {
            using var tmp = new TempStore();
            var lib = new Library(tmp.Store);
            var moreMovies = false;
            var server = XtreamServer();
            server.Add("action=get_vod_streams", () => moreMovies
                ? "[{\"stream_id\":501,\"name\":\"Film A\",\"category_id\":\"10\"},{\"stream_id\":502,\"name\":\"Film B\",\"category_id\":\"11\"},{\"stream_id\":503,\"name\":\"Film C\",\"category_id\":\"10\"}]"
                : "[{\"stream_id\":501,\"name\":\"Film A\",\"category_id\":\"10\"},{\"stream_id\":502,\"name\":\"Film B\",\"category_id\":\"11\"}]");
            var svc = new CatalogService(new HttpClient(server));
            var src = lib.AddSource(Xt());
            var first = await lib.SyncAsync(src, svc);
            lib.CommitSelection(src.Id, first.Catalog.Channels, first.Catalog.Channels.Select(c => c.Key),
                first.Catalog.Channels.Where(c => c.Key != "xtm:502").Select(c => c.Key)); // Film B bewust niet

            moreMovies = true;
            var outcome = await lib.SyncAsync(src, svc);
            Assert.Equal(SyncOutcomeKind.ReviewNeeded, outcome.Kind);
            Assert.Sequence(new[] { "xtm:503" }, outcome.Diff!.New.Select(c => c.Key));
        });

        // ---------- Terugkijken ----------

        yield return T("terugkijken (Xtream): link met starttijd in de tijdzone van de server", () =>
        {
            var src = Xt("Europe/Amsterdam");
            var ch = new PersonalChannel { SourceId = src.Id, Kind = ContentKind.Live, RemoteId = "1", CatchupDays = 3, Url = "http://x" };
            var start = new DateTime(2026, 10, 25, 20, 0, 0, DateTimeKind.Utc); // 25 okt 2026: nog zomertijd-einde? DST eindigt 25 okt 03:00 -> 20:00 UTC is CET (+1)
            var stop = start.AddMinutes(45);
            var url = CatchupUrl.Build(src, ch, start, stop, new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc));
            var expectedLocal = TimeZoneInfo.ConvertTimeFromUtc(start, TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam"));
            Assert.Equal($"http://srv.test:8080/timeshift/jan/pw/45/{expectedLocal:yyyy-MM-dd}:{expectedLocal:HH-mm}/1.ts", url);
            Assert.True(url!.Contains("2026-10-25:21-00"), "21:00 lokale tijd (UTC+1): " + url);
        });

        yield return T("terugkijken: zomertijd wordt goed meegenomen en onbekende tijdzone valt terug op UTC", () =>
        {
            var summer = new DateTime(2026, 7, 1, 18, 30, 0, DateTimeKind.Utc);
            Assert.Equal(new DateTime(2026, 7, 1, 20, 30, 0), CatchupUrl.ToServerTime(summer, "Europe/Amsterdam"));
            Assert.Equal(new DateTime(2026, 7, 1, 18, 30, 0), CatchupUrl.ToServerTime(summer, "Bestaat/Niet"));
            Assert.Equal(new DateTime(2026, 7, 1, 18, 30, 0), CatchupUrl.ToServerTime(summer, null));
        });

        yield return T("terugkijken: alleen programma's die begonnen zijn en binnen het archief liggen", () =>
        {
            var ch = new PersonalChannel { Kind = ContentKind.Live, RemoteId = "1", CatchupDays = 3, Url = "http://x" };
            var now = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
            Programme P(DateTime start) => new("c", start, start.AddHours(1), "t", null);

            Assert.True(CatchupUrl.CanReplay(ch, P(now.AddHours(-5)), now), "gisteren/vanochtend");
            Assert.True(CatchupUrl.CanReplay(ch, P(now.AddMinutes(-20)), now), "bezig: vanaf het begin");
            Assert.False(CatchupUrl.CanReplay(ch, P(now.AddHours(1)), now), "nog niet begonnen");
            Assert.False(CatchupUrl.CanReplay(ch, P(now.AddDays(-4)), now), "buiten archief");
            Assert.False(CatchupUrl.CanReplay(new PersonalChannel { Kind = ContentKind.Live, RemoteId = "1", CatchupDays = 0, Url = "x" }, P(now.AddHours(-2)), now), "zender zonder archief");
            Assert.False(CatchupUrl.CanReplay(new PersonalChannel { Kind = ContentKind.Movie, CatchupDays = 3 }, P(now.AddHours(-2)), now), "film");
        });

        yield return T("terugkijken (M3U): shift, append, default en xc", () =>
        {
            var now = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
            var start = new DateTime(2026, 10, 26, 10, 0, 0, DateTimeKind.Utc);
            var stop = start.AddMinutes(30);
            var unix = new DateTimeOffset(start).ToUnixTimeSeconds();
            var unixEnd = new DateTimeOffset(stop).ToUnixTimeSeconds();
            var nowUnix = new DateTimeOffset(now).ToUnixTimeSeconds();

            var shift = new PersonalChannel { Kind = ContentKind.Live, CatchupDays = 5, CatchupType = "shift", Url = "http://a/live.m3u8" };
            Assert.Equal($"http://a/live.m3u8?utc={unix}&lutc={nowUnix}", CatchupUrl.Build(null, shift, start, stop, now));
            shift.Url = "http://a/live.m3u8?token=1";
            Assert.Equal($"http://a/live.m3u8?token=1&utc={unix}&lutc={nowUnix}", CatchupUrl.Build(null, shift, start, stop, now));

            var append = new PersonalChannel { Kind = ContentKind.Live, CatchupDays = 5, CatchupType = "append", CatchupSource = "?start=${start}&end=${end}&d={duration}", Url = "http://a/1" };
            Assert.Equal($"http://a/1?start={unix}&end={unixEnd}&d=1800", CatchupUrl.Build(null, append, start, stop, now));

            var def = new PersonalChannel { Kind = ContentKind.Live, CatchupDays = 5, CatchupType = "default", CatchupSource = "http://b/{Y}/{m}/{d}/{H}{M}{S}.ts?o={offset}", Url = "http://a/1" };
            Assert.Equal("http://b/2026/10/26/100000.ts?o=7200", CatchupUrl.Build(null, def, start, stop, now));

            var xc = new PersonalChannel { Kind = ContentKind.Live, CatchupDays = 5, CatchupType = "xc", Url = "http://prov.test:80/live/jan/pw/123.ts" };
            Assert.Equal("http://prov.test:80/timeshift/jan/pw/30/2026-10-26:10-00/123.ts", CatchupUrl.Build(null, xc, start, stop, now));
        });

        yield return T("terugkijken: niet-ondersteund type of ontbrekend sjabloon geeft geen (kapotte) link", () =>
        {
            var now = DateTime.UtcNow;
            var flussonic = new PersonalChannel { Kind = ContentKind.Live, CatchupDays = 5, CatchupType = "flussonic", Url = "http://a/1" };
            Assert.False(CatchupUrl.IsAvailable(flussonic));
            Assert.Equal(null, CatchupUrl.Build(null, flussonic, now.AddHours(-2), now.AddHours(-1), now));

            var noTemplate = new PersonalChannel { Kind = ContentKind.Live, CatchupDays = 5, CatchupType = "append", Url = "http://a/1" };
            Assert.False(CatchupUrl.IsAvailable(noTemplate));
        });

        // ---------- Selectiescherm met tabs ----------

        yield return T("selectie: tabs per soort met eigen groepen en tellers", () =>
        {
            var cat = new List<Channel>
            {
                new("a", "NPO 1", "NL", "u"),
                new("b", "Film A", "Actie", "u", Kind: ContentKind.Movie),
                new("c", "Film B", "Drama", "u", Kind: ContentKind.Movie),
                new("d", "Serie X", "Comedy", "", Kind: ContentKind.Series),
            };
            var vm = new SelectionViewModel(new IptvSource { Name = "P" }, cat, Array.Empty<string>(), SelectionPurpose.InitialImport);

            Assert.True(vm.HasKindTabs);
            Assert.Sequence(new[] { "Zenders  (0/1)", "Films  (0/2)", "Series  (0/1)" }, vm.KindTabs.Select(t => t.Label));
            Assert.Sequence(new[] { "NPO 1" }, vm.VisibleChannels.Select(c => c.Name), "begint bij zenders");

            vm.SelectedKindTab = vm.KindTabs.First(t => t.Kind == ContentKind.Movie);
            Assert.Sequence(new[] { "Alle films  (0/2)", "Actie  (0/1)", "Drama  (0/1)" }, vm.Groups.Select(g => g.Label));
            Assert.Sequence(new[] { "Film A", "Film B" }, vm.VisibleChannels.Select(c => c.Name));

            vm.SelectVisibleCommand.Execute(null); // alleen de films in beeld
            Assert.Equal(2, vm.SelectedCount);
            Assert.Equal("Films  (2/2)", vm.KindTabs[1].Label);
            Assert.Equal("Zenders  (0/1)", vm.KindTabs[0].Label);
            Assert.Equal("2 van 4 items geselecteerd", vm.SummaryText);
            Assert.Sequence(new[] { "b", "c" }, vm.SelectedKeys);

            vm.SelectedKindTab = null!; // ListBox die null terugstuurt: negeren
            Assert.Equal(ContentKind.Movie, vm.SelectedKindTab.Kind);
        });

        yield return T("selectie: begint bij de soort waar iets te kiezen valt en laat tabs weg bij één soort", () =>
        {
            var onlyMovies = new List<Channel> { new("b", "Film A", "Actie", "u", Kind: ContentKind.Movie) };
            var vm = new SelectionViewModel(new IptvSource { Name = "P" }, onlyMovies, Array.Empty<string>(), SelectionPurpose.Review);
            Assert.False(vm.HasKindTabs);
            Assert.Equal(1, vm.VisibleChannels.Count);
            Assert.Equal("0 van 1 films geselecteerd", vm.SummaryText);
        });

        yield return T("selectie: terugkijken is zichtbaar, filterbaar en telt mee in de samenvatting", () =>
        {
            var cat = new List<Channel>
            {
                new("a", "Met archief", "NL", "http://s/u/p/1", RemoteId: "1", CatchupDays: 3),
                new("b", "Zonder archief", "NL", "http://s/u/p/2", RemoteId: "2"),
            };
            var vm = new SelectionViewModel(new IptvSource { Name = "P" }, cat, Array.Empty<string>(), SelectionPurpose.InitialImport);

            Assert.True(vm.HasCatchupChannels);
            Assert.Equal("↺ 3 dagen", vm.VisibleChannels.First(c => c.Name == "Met archief").CatchupText);
            Assert.False(vm.VisibleChannels.First(c => c.Name == "Zonder archief").HasCatchup);
            Assert.Equal("↺ Terugkijken: 0 gekozen, 1 beschikbaar in dit aanbod", vm.CatchupSummary);

            vm.OnlyCatchup = true;
            Assert.Sequence(new[] { "Met archief" }, vm.VisibleChannels.Select(c => c.Name));

            vm.VisibleChannels[0].IsSelected = true;
            Assert.Equal("↺ Terugkijken: 1 gekozen, 1 beschikbaar in dit aanbod", vm.CatchupSummary);

            var none = new SelectionViewModel(new IptvSource { Name = "P" }, new List<Channel> { new("c", "X", "NL", "u") },
                Array.Empty<string>(), SelectionPurpose.Review);
            Assert.False(none.HasCatchupChannels);
        });

        yield return T("selectie: zoeken geldt binnen de gekozen soort", () =>
        {
            var cat = new List<Channel>
            {
                new("a", "Kijk Zender", "NL", "u"),
                new("b", "Kijk Film", "Actie", "u", Kind: ContentKind.Movie),
            };
            var vm = new SelectionViewModel(new IptvSource { Name = "P" }, cat, Array.Empty<string>(), SelectionPurpose.InitialImport);
            vm.SearchText = "kijk";
            Assert.Sequence(new[] { "Kijk Zender" }, vm.VisibleChannels.Select(c => c.Name));
            vm.SelectedKindTab = vm.KindTabs[1];
            Assert.Sequence(new[] { "Kijk Film" }, vm.VisibleChannels.Select(c => c.Name));
        });

        yield return TA("lijst: Library onthoudt soort en catch-up van een zender in je lijst", async () =>
        {
            using var tmp = new TempStore();
            var lib = new Library(tmp.Store);
            var svc = new CatalogService(new HttpClient(XtreamServer()));
            var src = lib.AddSource(Xt());
            var first = await lib.SyncAsync(src, svc);
            lib.CommitSelection(src.Id, first.Catalog.Channels, first.Catalog.Channels.Select(c => c.Key), first.Catalog.Channels.Select(c => c.Key));

            var lib2 = new Library(new AppStore(tmp.Path));
            var nos = lib2.Personal.First(p => p.Name == "NOS");
            Assert.Equal(3, nos.CatchupDays);
            Assert.Equal("1", nos.RemoteId);
            Assert.Equal(ContentKind.Series, lib2.Personal.First(p => p.Name == "Serie X").Kind);
            Assert.Equal("Over mensen.", lib2.Personal.First(p => p.Name == "Serie X").Info);
        });
    }
}

/// <summary>Laat een test het gedrag van de nep-server tussen twee syncs wisselen.</summary>
public sealed class SwitchHandler : HttpMessageHandler
{
    private readonly Func<HttpMessageHandler> _current;
    public SwitchHandler(Func<HttpMessageHandler> current) => _current = current;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var inner = _current();
        var invoker = new HttpMessageInvoker(inner, disposeHandler: false);
        return invoker.SendAsync(request, ct);
    }
}
