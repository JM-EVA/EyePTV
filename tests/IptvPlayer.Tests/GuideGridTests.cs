using IptvPlayer.Engine;
using IptvPlayer.Engine.ViewModels;

namespace IptvPlayer.Tests;

/// <summary>Gidsrooster, zoeken in de gids en herinneringen.</summary>
public static class GuideGridTests
{
    private static (string, Func<Task>) T(string name, Action a) => ("Gids: " + name, () => { a(); return Task.CompletedTask; });

    private static readonly DateTime Now = new(2026, 11, 6, 20, 15, 0, DateTimeKind.Utc);

    private static DateTime At(double minutesFromNow) => Now.AddMinutes(minutesFromNow);

    private static Programme P(string id, double from, double to, string title, string? desc = null) => new(id, At(from), At(to), title, desc);

    private sealed class Clock { public DateTime Now = GuideGridTests.Now; }

    private sealed class Env : IDisposable
    {
        public required TempStore Tmp { get; init; }
        public required Library Lib { get; init; }
        public required EpgService Epg { get; init; }
        public required GuideViewModel Guide { get; init; }
        public required ReminderManager Reminders { get; init; }
        public required SettingsStore Settings { get; init; }
        public required FakePlayer Player { get; init; }
        public required Clock Time { get; init; }
        public DateTime Clock { get => Time.Now; set => Time.Now = value; }
        public void Dispose() => Tmp.Dispose();
        public PersonalChannel Ch(string name) => Lib.Personal.First(p => p.Name == name);
    }

    private static Env Create()
    {
        var tmp = new TempStore();
        var lib = new Library(tmp.Store);
        var src = lib.AddSource(new IptvSource { Name = "XT", Kind = SourceKind.Xtream, Server = "http://srv.test:8080", Username = "jan", Password = "pw", ServerTimeZone = "UTC" });
        var cat = new List<Channel>
        {
            new("k:1", "NPO 1", "NL", "http://srv.test/1.ts", EpgId: "npo1", Kind: ContentKind.Live),
            new("k:2", "RTL 4", "Commercieel", "http://srv.test/2.ts", EpgId: "rtl4", Kind: ContentKind.Live),
            new("k:3", "Een Film", "Actie", "http://srv.test/3.mp4", Kind: ContentKind.Movie),
        };
        lib.CommitSelection(src.Id, cat, cat.Select(c => c.Key), cat.Select(c => c.Key));
        lib.SetFavorite(lib.Personal.First(p => p.Name == "RTL 4"), true);

        var epg = new EpgService(new HttpClient(new FakeHandler()), tmp.Store);
        epg.Data.Programmes["npo1"] = new List<Programme>
        {
            P("npo1", -75, -15, "Journaal", "Het nieuws van acht uur"), P("npo1", -15, 45, "Café Weltschmerz", "Praatprogramma"), P("npo1", 45, 105, "Film: Nacht"),
        };
        epg.Data.Programmes["rtl4"] = new List<Programme> { P("rtl4", -30, 30, "Boulevard"), P("rtl4", 30, 90, "Nieuws") };

        var settings = new SettingsStore(tmp.Store);
        var player = new FakePlayer();
        var clock = new Clock();
        var reminders = new ReminderManager(tmp.Store, settings, () => clock.Now);
        var channels = new ChannelsViewModel(lib, epg, player, new FakeDialogs(), () => clock.Now, settings: settings);
        var guide = new GuideViewModel(lib, epg, null, channels, () => { }, () => clock.Now, reminders: reminders);
        return new Env { Tmp = tmp, Lib = lib, Epg = epg, Settings = settings, Player = player, Reminders = reminders, Guide = guide, Time = clock };
    }

    public static IEnumerable<(string, Func<Task>)> Tests()
    {
        // ---------- Rooster ----------

        yield return T("venster: op hele uren, van het vroegste tot het laatste programma", () =>
        {
            using var env = Create();
            var (start, end) = GuideGrid.Window(env.Lib.Personal.Where(p => p.Kind == ContentKind.Live), env.Epg.Data, Now);
            Assert.Equal(new DateTime(2026, 11, 6, 19, 0, 0, DateTimeKind.Utc), start, "75 min terug = 19:00");
            Assert.Equal(new DateTime(2026, 11, 7, 1, 0, 0, DateTimeKind.Utc), end, "laatste programma 22:00, maar een venster is minstens 6 uur");
        });

        yield return T("venster: grenzen aan terugkijken en vooruitkijken, en een bruikbaar venster zonder gegevens", () =>
        {
            var epg = new EpgData();
            epg.Programmes["x"] = new List<Programme> { new("x", Now.AddDays(-40), Now.AddDays(30), "Lang", null) };
            var ch = new PersonalChannel { SourceId = "s", Key = "k", Name = "X", EpgId = "x", Kind = ContentKind.Live };
            var (start, end) = GuideGrid.Window(new[] { ch }, epg, Now);
            Assert.True(start >= Now.AddDays(-14).AddHours(-1) && end <= Now.AddDays(7).AddHours(1), $"{start} - {end}");

            var (s2, e2) = GuideGrid.Window(Array.Empty<PersonalChannel>(), new EpgData(), Now);
            Assert.True(s2 <= Now && e2 >= Now.AddHours(5), "zonder EPG een venster rond nu");
        });

        yield return T("blokken: breedte volgt de duur en alleen wat in het venster valt", () =>
        {
            using var env = Create();
            var start = new DateTime(2026, 11, 6, 20, 0, 0, DateTimeKind.Utc);
            var blocks = GuideGrid.Blocks(env.Ch("NPO 1"), env.Epg.Data, start, start.AddHours(1));
            Assert.Equal(1, blocks.Count, "Journaal eindigt precies om 20:00 en de film begint om 21:00");
            Assert.Equal(0.0, blocks[0].StartMinutes);
            Assert.Equal(60.0, blocks[0].Minutes);
            Assert.False(blocks[0].ClippedStart || blocks[0].ClippedEnd);
        });

        yield return T("blokken: programma's over de rand heen worden afgesneden", () =>
        {
            using var env = Create();
            var start = new DateTime(2026, 11, 6, 20, 30, 0, DateTimeKind.Utc);
            var blocks = GuideGrid.Blocks(env.Ch("NPO 1"), env.Epg.Data, start, start.AddHours(1));
            Assert.Equal(2, blocks.Count);
            Assert.True(blocks[0].ClippedStart && blocks[0].StartMinutes == 0 && blocks[0].Minutes == 30.0, "Café: 20:30-21:00");
            Assert.True(blocks[1].ClippedEnd && blocks[1].StartMinutes == 30.0 && blocks[1].Minutes == 30.0, "Film: 21:00-21:30");
        });

        yield return T("blokken: overlappende programma's botsen niet", () =>
        {
            var epg = new EpgData();
            epg.Programmes["x"] = new List<Programme> { new("x", Now, Now.AddMinutes(60), "A", null), new("x", Now.AddMinutes(50), Now.AddMinutes(90), "B", null) };
            var ch = new PersonalChannel { SourceId = "s", Key = "k", Name = "X", EpgId = "x", Kind = ContentKind.Live };
            var blocks = GuideGrid.Blocks(ch, epg, Now, Now.AddHours(2));
            Assert.Equal(60.0, blocks[1].StartMinutes, "B begint waar A stopt");
            Assert.Equal(30.0, blocks[1].Minutes);
        });

        // ---------- Zoeken ----------

        yield return T("zoeken: hoofdletters en accenten tellen niet, titel én omschrijving, gesorteerd op start", () =>
        {
            using var env = Create();
            var chs = env.Lib.Personal.Where(p => p.Kind == ContentKind.Live).ToList();
            var hits = GuideGrid.Search(chs, env.Epg.Data, "CAFE", Now.AddDays(-1));
            Assert.Equal(1, hits.Count);
            Assert.Equal("Café Weltschmerz", hits[0].Programme.Title);

            var byDesc = GuideGrid.Search(chs, env.Epg.Data, "nieuws", Now.AddDays(-1));
            Assert.Equal(2, byDesc.Count, "'Het nieuws van acht uur' en 'Nieuws'");
            Assert.True(byDesc[0].Programme.StartUtc < byDesc[1].Programme.StartUtc, "op starttijd");
        });

        yield return T("zoeken: alleen zenders uit de opgegeven lijst", () =>
        {
            using var env = Create();
            var only = new[] { env.Ch("RTL 4") };
            Assert.Equal(0, GuideGrid.Search(only, env.Epg.Data, "weltschmerz", Now.AddDays(-1)).Count);
        });

        // ---------- Herinneringen ----------

        yield return T("herinnering: gaat af op het ingestelde moment, één keer", () =>
        {
            using var env = Create();
            var fired = new List<Reminder>();
            env.Reminders.Due += fired.Add;
            env.Reminders.Add(env.Ch("NPO 1"), P("npo1", 45, 105, "Film: Nacht")); // begint 21:00, 5 min vooraf = 20:55
            env.Clock = At(39); env.Reminders.Tick();
            Assert.Equal(0, fired.Count, "nog te vroeg (20:54)");
            env.Clock = At(41); env.Reminders.Tick();
            Assert.Equal(1, fired.Count);
            env.Clock = At(42); env.Reminders.Tick();
            Assert.Equal(1, fired.Count, "niet dubbel");
        });

        yield return T("herinnering: minuten vooraf komen uit de instellingen en worden bij het aanmaken vastgelegd", () =>
        {
            using var env = Create();
            env.Settings.Current.ReminderMinutes = 15;
            env.Reminders.Add(env.Ch("NPO 1"), P("npo1", 45, 105, "Film: Nacht"));
            env.Settings.Current.ReminderMinutes = 1;
            Assert.Equal(15, env.Reminders.Items.Single().MinutesBefore);
        });

        yield return T("herinnering: blijft bewaard en gaat na een herstart niet nog eens af", () =>
        {
            using var env = Create();
            env.Reminders.Add(env.Ch("NPO 1"), P("npo1", 45, 105, "Film: Nacht"));
            env.Clock = At(44); env.Reminders.Tick();

            var again = new ReminderManager(env.Tmp.Store, env.Settings, () => env.Clock);
            var fired = 0;
            again.Due += _ => fired++;
            again.Tick();
            Assert.Equal(1, again.Items.Count, "bewaard");
            Assert.Equal(0, fired, "al getoond");
        });

        yield return T("herinnering: bij het opstarten komt er een melding als het programma nog loopt; verlopen wordt opgeruimd", () =>
        {
            using var env = Create();
            env.Reminders.Add(env.Ch("NPO 1"), P("npo1", 45, 105, "Film: Nacht"));
            env.Reminders.Add(env.Ch("RTL 4"), P("rtl4", 30, 90, "Nieuws"));

            env.Clock = At(95); // de app was uit; Film: Nacht loopt nog (tot At(105)), Nieuws is afgelopen (At(90))
            var started = new ReminderManager(env.Tmp.Store, env.Settings, () => env.Clock);
            var fired = new List<Reminder>();
            started.Due += fired.Add;
            started.Tick();
            Assert.Equal(1, fired.Count);
            Assert.Equal("Film: Nacht", fired[0].Title);
            Assert.Equal(1, started.Items.Count, "verlopen herinnering opgeruimd");
        });

        yield return T("herinnering: dubbel zetten en een programma dat al voorbij is, wordt genegeerd; verwijderen werkt", () =>
        {
            using var env = Create();
            var p = P("npo1", 45, 105, "Film: Nacht");
            env.Reminders.Add(env.Ch("NPO 1"), p);
            env.Reminders.Add(env.Ch("NPO 1"), p);
            env.Reminders.Add(env.Ch("NPO 1"), P("npo1", -75, -15, "Journaal"));
            Assert.Equal(1, env.Reminders.Items.Count);
            env.Reminders.Remove(env.Ch("NPO 1").Id, p.StartUtc);
            Assert.Equal(0, env.Reminders.Items.Count);
        });

        // ---------- Viewmodel ----------

        yield return T("rooster: alleen live-zenders, filter op groep en favorieten, blokken pas bij gebruik", () =>
        {
            using var env = Create();
            env.Guide.Refresh();
            Assert.Equal(2, env.Guide.GridRows.Count, "geen films");
            Assert.True(env.Guide.GridWidth > 0 && env.Guide.NowX > 0 && env.Guide.HourMarks.Count >= 3);

            env.Guide.SelectedGroup = "Commercieel";
            Assert.Equal("RTL 4", env.Guide.GridRows.Single().Channel.Name);
            env.Guide.SelectedGroup = GuideViewModel.AllGroups;
            env.Guide.FavoritesOnly = true;
            Assert.Equal("RTL 4", env.Guide.GridRows.Single().Channel.Name);
        });

        yield return T("rooster: een programma kiezen toont details; herinnering zetten en weer weghalen", () =>
        {
            using var env = Create();
            env.Guide.Refresh();
            var row = env.Guide.GridRows.First(r => r.Channel.Name == "NPO 1");
            var future = row.Blocks.First(b => b.Title == "Film: Nacht");
            Assert.True(env.Guide.Selected is null);
            future.SelectCommand.Execute(null);
            Assert.True(env.Guide.Selected!.CanRemind, "toekomst: herinnering mogelijk");
            Assert.False(future.IsReminded);

            env.Guide.Selected!.RemindCommand.Execute(null);
            Assert.True(future.IsReminded && env.Guide.Selected!.IsReminded, "gemarkeerd");
            Assert.Equal(1, env.Guide.ReminderRows.Count);

            env.Guide.Selected!.RemindCommand.Execute(null);
            Assert.False(future.IsReminded);
            Assert.Equal(0, env.Guide.ReminderRows.Count);

            var now = row.Blocks.First(b => b.IsNow);
            now.SelectCommand.Execute(null);
            Assert.False(env.Guide.Selected!.CanRemind, "wat bezig is, herinner je niet");
        });

        yield return T("rooster: kijk nu speelt de zender", () =>
        {
            using var env = Create();
            env.Guide.Refresh();
            var block = env.Guide.GridRows.First(r => r.Channel.Name == "RTL 4").Blocks.First(b => b.IsNow);
            block.SelectCommand.Execute(null);
            env.Guide.Selected!.WatchCommand.Execute(null);
            Assert.Equal("http://srv.test/2.ts", env.Player.Played.Last());
        });

        yield return T("zoeken in de viewmodel: resultaten vervangen het rooster en hebben dezelfde acties", () =>
        {
            using var env = Create();
            env.Guide.Refresh();
            Assert.True(env.Guide.ShowGrid && !env.Guide.ShowHits);
            env.Guide.ProgrammeQuery = "w";
            Assert.True(env.Guide.ShowGrid, "één teken is te weinig");
            env.Guide.ProgrammeQuery = "nacht";
            Assert.True(env.Guide.ShowHits && !env.Guide.ShowGrid);
            Assert.Equal(1, env.Guide.Hits.Count);
            Assert.True(env.Guide.Hits[0].Row.CanRemind);
            env.Guide.Hits[0].Row.RemindCommand.Execute(null);
            Assert.True(env.Guide.Hits[0].Row.IsReminded);
            env.Guide.ProgrammeQuery = "zzzzz";
            Assert.True(env.Guide.HasNoHits);
            env.Guide.ProgrammeQuery = "";
            Assert.True(env.Guide.ShowGrid);
        });

        yield return T("herinneringen: lijstje tonen en een herinnering verwijderen", () =>
        {
            using var env = Create();
            env.Guide.Refresh();
            env.Reminders.Add(env.Ch("NPO 1"), P("npo1", 45, 105, "Film: Nacht"));
            Assert.Equal(1, env.Guide.ReminderRows.Count);
            env.Guide.ShowReminders = true;
            Assert.True(env.Guide.ShowReminderList && !env.Guide.ShowGrid);
            env.Guide.ReminderRows[0].RemoveCommand.Execute(null);
            Assert.True(env.Guide.HasNoReminders);
        });

        yield return T("melding in het hoofdvenster: nu kijken speelt de zender, sluiten haalt de melding weg", () =>
        {
            using var tmp = new TempStore();
            var lib = new Library(tmp.Store);
            var src = lib.AddSource(new IptvSource { Name = "XT", Kind = SourceKind.Xtream, Server = "http://srv.test", Username = "u", Password = "p" });
            var cat = new List<Channel> { new("xt:1", "NOS", "N", "http://srv.test/live/u/p/1.ts", RemoteId: "1", Kind: ContentKind.Live) };
            lib.CommitSelection(src.Id, cat, cat.Select(c => c.Key), cat.Select(c => c.Key));
            var player = new FakePlayer();
            var main = new MainViewModel(lib, new CatalogService(new HttpClient(new FakeHandler())),
                new EpgService(new HttpClient(new FakeHandler()), tmp.Store), player, new FakeDialogs());

            var raised = 0;
            main.ReminderRaised += () => raised++;
            var start = DateTime.UtcNow.AddMinutes(3);
            main.Reminders.Add(lib.Personal.Single(), new Programme("x", start, start.AddHours(1), "Het Journaal", null));
            main.Reminders.Tick();
            Assert.True(main.HasReminder && raised == 1, "melding getoond");
            Assert.True(main.ReminderText.Contains("Het Journaal") && main.ReminderText.Contains("NOS"), main.ReminderText);

            main.WatchReminderCommand.Execute(null);
            Assert.Equal("http://srv.test/live/u/p/1.ts", player.Played.Last());
            Assert.False(main.HasReminder);

            main.Reminders.Add(lib.Personal.Single(), new Programme("x", start.AddHours(2), start.AddHours(3), "Later", null));
            main.Reminders.Items[^1].MinutesBefore = 600;
            main.Reminders.Tick();
            Assert.True(main.HasReminder);
            main.DismissReminderCommand.Execute(null);
            Assert.False(main.HasReminder);
        });
    }
}
