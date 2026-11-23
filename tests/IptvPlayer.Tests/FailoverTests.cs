using IptvPlayer.Engine;
using IptvPlayer.Engine.ViewModels;

namespace IptvPlayer.Tests;

/// <summary>Reservezenders: bij een fout of een zender die niet start, schakelt de app over.</summary>
public static class FailoverTests
{
    private static (string, Func<Task>) T(string name, Action a) => ("Reservezender: " + name, () => { a(); return Task.CompletedTask; });

    private sealed class Clock { public DateTime Now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc); }

    private sealed class Env : IDisposable
    {
        public required TempStore Tmp { get; init; }
        public required Library Lib { get; init; }
        public required FakePlayer Player { get; init; }
        public required FakeDialogs Dialogs { get; init; }
        public required ChannelsViewModel Vm { get; init; }
        public required Clock Clock { get; init; }
        public void Dispose() => Tmp.Dispose();

        public PersonalChannel Ch(string name) => Lib.Personal.First(p => p.Name == name);
        public void Play(string name) => Vm.Rows.First(r => r.Name == name).PlayCommand.Execute(null);
        public void Advance(double seconds) { Clock.Now = Clock.Now.AddSeconds(seconds); Vm.CheckConnection(); }
        public string Last => Player.Played.Last();
    }

    private static Env Create()
    {
        var tmp = new TempStore();
        var lib = new Library(tmp.Store);
        var src = lib.AddSource(new IptvSource { Name = "XT", Kind = SourceKind.Xtream, Server = "http://srv.test:8080", Username = "jan", Password = "pw", ServerTimeZone = "UTC" });
        var cat = new List<Channel>
        {
            new("k:1", "NPO 1", "NL", "http://srv.test/1.ts", Kind: ContentKind.Live),
            new("k:2", "NPO 1 HD", "NL HD", "http://srv.test/2.ts", Kind: ContentKind.Live),
            new("k:3", "NPO 1 FHD", "NL FHD", "http://srv.test/3.ts", Kind: ContentKind.Live),
            new("k:4", "RTL 4", "NL", "http://srv.test/4.ts", Kind: ContentKind.Live),
            new("k:5", "Een Film", "Actie", "http://srv.test/5.mp4", Kind: ContentKind.Movie),
        };
        lib.CommitSelection(src.Id, cat, cat.Select(c => c.Key), cat.Select(c => c.Key));

        var clock = new Clock();
        var player = new FakePlayer();
        var dialogs = new FakeDialogs();
        var settings = new SettingsStore(tmp.Store);
        var vm = new ChannelsViewModel(lib, new EpgService(new HttpClient(new FakeHandler()), tmp.Store), player, dialogs, () => clock.Now, settings: settings);
        return new Env { Tmp = tmp, Lib = lib, Player = player, Dialogs = dialogs, Vm = vm, Clock = clock };
    }

    private static Env WithBackups()
    {
        var env = Create();
        env.Lib.SetBackups(env.Ch("NPO 1"), new[] { env.Ch("NPO 1 HD").Id, env.Ch("NPO 1 FHD").Id });
        return env;
    }

    public static IEnumerable<(string, Func<Task>)> Tests()
    {
        yield return T("een fout schakelt over naar de eerste reserve en meldt dat", () =>
        {
            using var env = WithBackups();
            env.Play("NPO 1");
            env.Player.RaiseError("kapot");
            Assert.Equal("http://srv.test/2.ts", env.Last);
            Assert.True(env.Vm.StatusText.Contains("Overgeschakeld naar NPO 1 HD"), env.Vm.StatusText);
        });

        yield return T("een zender die niet start: na 10 seconden naar de reserve, eerder niet", () =>
        {
            using var env = WithBackups();
            env.Play("NPO 1");
            env.Advance(8);
            Assert.Equal("http://srv.test/1.ts", env.Last, "nog wachten");
            env.Advance(3);
            Assert.Equal("http://srv.test/2.ts", env.Last);
            env.Advance(11);
            Assert.Equal("http://srv.test/3.ts", env.Last, "ook de reserve krijgt zijn tijd en daarna de volgende");
        });

        yield return T("de wachttijd is instelbaar", () =>
        {
            using var env = WithBackups();
            var settings = new SettingsStore(env.Tmp.Store);
            settings.Current.FailoverSeconds = 30;
            var vm = new ChannelsViewModel(env.Lib, new EpgService(new HttpClient(new FakeHandler()), env.Tmp.Store), env.Player, env.Dialogs, () => env.Clock.Now, settings: settings);
            vm.Rows.First(r => r.Name == "NPO 1").PlayCommand.Execute(null);
            env.Clock.Now = env.Clock.Now.AddSeconds(20);
            vm.CheckConnection();
            Assert.Equal("http://srv.test/1.ts", env.Last, "na 20 s nog niet bij 30 s instelling");
            env.Clock.Now = env.Clock.Now.AddSeconds(11);
            vm.CheckConnection();
            Assert.Equal("http://srv.test/2.ts", env.Last);
        });

        yield return T("elke reserve hooguit één keer; daarna stopt de app met een duidelijke melding", () =>
        {
            using var env = WithBackups();
            env.Play("NPO 1");
            env.Player.RaiseError("1");
            env.Player.RaiseError("2");
            var played = env.Player.Played.Count;
            env.Player.RaiseError("3");
            Assert.Equal(played, env.Player.Played.Count, "niet nog eens proberen");
            Assert.True(env.Vm.StatusText.Contains("werken niet"), env.Vm.StatusText);
            env.Advance(60);
            Assert.Equal(played, env.Player.Played.Count, "ook niet via de herverbinding");
        });

        yield return T("zelf een zender kiezen begint weer bij de hoofdzender", () =>
        {
            using var env = WithBackups();
            env.Play("NPO 1");
            env.Player.RaiseError("1");
            env.Play("NPO 1");
            Assert.Equal("http://srv.test/1.ts", env.Last);
            env.Player.RaiseError("2");
            Assert.Equal("http://srv.test/2.ts", env.Last, "weer de eerste reserve");
        });

        yield return T("een zender zonder reserves wordt gewoon herverbonden", () =>
        {
            using var env = WithBackups();
            env.Play("RTL 4");
            var n = env.Player.Played.Count;
            env.Player.RaiseError("x");
            Assert.Equal(n + 1, env.Player.Played.Count);
            Assert.Equal("http://srv.test/4.ts", env.Last);
        });

        yield return T("films krijgen geen reservezender", () =>
        {
            using var env = WithBackups();
            env.Vm.SelectKind(ContentKind.Movie);
            Assert.False(env.Vm.Rows.First(r => r.Name == "Een Film").CanHaveBackups);
        });

        yield return T("een verwijderde reserve verdwijnt uit de lijst en wordt overgeslagen", () =>
        {
            using var env = WithBackups();
            env.Lib.Remove(env.Ch("NPO 1 HD"));
            Assert.Equal(1, env.Ch("NPO 1").BackupIds.Count);
            env.Play("NPO 1");
            env.Player.RaiseError("x");
            Assert.Equal("http://srv.test/3.ts", env.Last);
        });

        yield return T("oude bestanden zonder reserves blijven werken", () =>
        {
            using var env = Create();
            var json = "{\"SourceId\":\"s\",\"Key\":\"k\",\"Name\":\"X\",\"Url\":\"http://x/1.ts\"}";
            var p = System.Text.Json.JsonSerializer.Deserialize<PersonalChannel>(json)!;
            Assert.Equal(0, p.BackupIds.Count);
        });

        yield return T("instellen: dezelfde namen staan bovenaan, de keuze wordt bewaard", () =>
        {
            using var env = Create();
            env.Dialogs.PickAnswer = new[] { env.Ch("NPO 1 FHD").Id };
            env.Vm.Rows.First(r => r.Name == "NPO 1").SetBackupsCommand.Execute(null);
            var options = env.Dialogs.PickQuestions.Single();
            Assert.True(options[0].Suggested && options[0].Name.StartsWith("NPO 1"), "voorstel bovenaan");
            Assert.False(options.Any(o => o.Name == "Een Film"), "geen films");
            Assert.Equal(1, env.Ch("NPO 1").BackupIds.Count);

            env.Vm.Rows.First(r => r.Name == "NPO 1").ClearBackupsCommand.Execute(null);
            Assert.Equal(0, env.Ch("NPO 1").BackupIds.Count);
        });
    }
}
