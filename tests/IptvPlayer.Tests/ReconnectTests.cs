using IptvPlayer.Engine;
using IptvPlayer.Engine.ViewModels;

namespace IptvPlayer.Tests;

/// <summary>Een zender die vastloopt of wegvalt wordt automatisch opnieuw verbonden.</summary>
public static class ReconnectTests
{
    private static (string, Func<Task>) T(string name, Action a) => ("Reconnect: " + name, () => { a(); return Task.CompletedTask; });

    private sealed class ClockBox
    {
        public DateTime Now { get; set; } = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
    }

    private sealed class Env : IDisposable
    {
        public required TempStore Tmp { get; init; }
        public required FakePlayer Player { get; init; }
        public required ChannelsViewModel Vm { get; init; }
        public required ClockBox Box { get; init; }
        public DateTime Clock { get => Box.Now; set => Box.Now = value; }
        public void Dispose() => Tmp.Dispose();

        public void Advance(double seconds)
        {
            Clock = Clock.AddSeconds(seconds);
            Vm.CheckConnection();
        }

        /// <summary>De speler laat horen dat er beeld is.</summary>
        public void Alive() => Player.Tick(1000, 0);
    }

    private static Env Create()
    {
        var tmp = new TempStore();
        var lib = new Library(tmp.Store);
        var src = lib.AddSource(new IptvSource
        {
            Name = "XT", Kind = SourceKind.Xtream, Server = "http://srv.test:8080", Username = "jan", Password = "pw", ServerTimeZone = "UTC"
        });
        var cat = new List<Channel>
        {
            new("k:1", "NPO 1", "Nederland", "http://srv.test/1.ts", Kind: ContentKind.Live),
            new("k:3", "Gewone Film", "Actie", "http://srv.test/3.mp4", Kind: ContentKind.Movie),
        };
        lib.CommitSelection(src.Id, cat, cat.Select(c => c.Key), cat.Select(c => c.Key));

        var clock = new ClockBox();
        var player = new FakePlayer();
        var epg = new EpgService(new HttpClient(new FakeHandler()), tmp.Store);
        var vm = new ChannelsViewModel(lib, epg, player, new FakeDialogs(), () => clock.Now);
        return new Env { Tmp = tmp, Player = player, Vm = vm, Box = clock };
    }

    private static void PlayLive(Env env)
    {
        env.Vm.Rows.First(r => r.Name == "NPO 1").PlayCommand.Execute(null);
        env.Alive();
    }

    public static IEnumerable<(string, Func<Task>)> Tests()
    {
        yield return T("beeld dat vastloopt: na 12 seconden stilte wordt dezelfde zender opnieuw gestart", () =>
        {
            using var env = Create();
            PlayLive(env);
            Assert.Equal(1, env.Player.Played.Count);

            env.Advance(8);
            Assert.Equal(1, env.Player.Played.Count, "nog geen reden");
            env.Advance(5); // 13 seconden zonder levensteken
            Assert.Equal(1, env.Player.Played.Count, "eerst plannen");
            Assert.True(env.Vm.StatusText.Contains("reageert niet meer"), env.Vm.StatusText);

            env.Advance(2);
            Assert.Equal(2, env.Player.Played.Count, "opnieuw gestart");
            Assert.Equal(env.Player.Played[0], env.Player.Played[1], "dezelfde zender");
            Assert.True(env.Vm.StatusText.Contains("Opnieuw verbinden"), env.Vm.StatusText);
            Assert.True(env.Vm.IsReconnecting);
        });

        yield return T("levensteken van de speler houdt de verbinding in stand", () =>
        {
            using var env = Create();
            PlayLive(env);
            for (var i = 0; i < 10; i++)
            {
                env.Clock = env.Clock.AddSeconds(5);
                env.Alive();
                env.Vm.CheckConnection();
            }
            Assert.Equal(1, env.Player.Played.Count, "50 seconden zonder problemen");
            Assert.False(env.Vm.IsReconnecting);
        });

        yield return T("de uitzending stopt of de speler meldt een fout: opnieuw verbinden", () =>
        {
            using var env = Create();
            PlayLive(env);
            env.Player.End();
            env.Advance(2);
            Assert.Equal(2, env.Player.Played.Count, "na het einde");

            env.Alive();
            env.Player.RaiseError("Afspelen mislukt.");
            env.Advance(2);
            Assert.Equal(3, env.Player.Played.Count, "na de fout");
        });

        yield return T("pogingen wachten steeds langer en na een stabiele verbinding begint het weer van voren af aan", () =>
        {
            using var env = Create();
            PlayLive(env);
            env.Player.End();
            env.Advance(2);                       // poging 1
            Assert.Equal(2, env.Player.Played.Count);

            env.Player.End();                     // meteen weer weg
            env.Advance(1);
            Assert.Equal(2, env.Player.Played.Count, "poging 2 wacht 2 seconden");
            env.Advance(1.5);
            Assert.Equal(3, env.Player.Played.Count);

            env.Player.End();
            env.Advance(3);
            Assert.Equal(3, env.Player.Played.Count, "poging 3 wacht 4 seconden");
            env.Advance(1.5);
            Assert.Equal(4, env.Player.Played.Count);

            // Nu blijft het werken: na 10 seconden is de storing voorbij.
            env.Clock = env.Clock.AddSeconds(5); env.Alive();
            env.Clock = env.Clock.AddSeconds(6); env.Alive();
            Assert.False(env.Vm.IsReconnecting, "teller is gereset");
            Assert.Equal("NPO 1", env.Vm.StatusText);
        });

        yield return T("zender die niet start wordt na 25 seconden opnieuw geprobeerd", () =>
        {
            using var env = Create();
            env.Vm.Rows.First(r => r.Name == "NPO 1").PlayCommand.Execute(null); // nooit een levensteken
            env.Advance(20);
            Assert.Equal(1, env.Player.Played.Count);
            env.Advance(6);
            env.Advance(2);
            Assert.Equal(2, env.Player.Played.Count);
        });

        yield return T("stoppen of een film: nooit automatisch opnieuw verbinden", () =>
        {
            using var env = Create();
            PlayLive(env);
            env.Vm.Stop();
            env.Player.End();
            env.Advance(60);
            Assert.Equal(1, env.Player.Played.Count, "na Stop niets meer");

            env.Vm.SelectKind(ContentKind.Movie);
            env.Vm.Rows.First(r => r.Name == "Gewone Film").PlayCommand.Execute(null);
            env.Player.Tick(5000, 600000);
            var before = env.Player.Played.Count;
            env.Advance(60);
            env.Player.RaiseError("kapot");
            env.Advance(60);
            Assert.Equal(before, env.Player.Played.Count, "een film start niet vanzelf opnieuw");
        });

        yield return T("na te veel mislukte pogingen geeft de app het op met een duidelijke melding", () =>
        {
            using var env = Create();
            PlayLive(env);
            for (var i = 0; i < ChannelsViewModel.MaxReconnects + 3; i++)
            {
                env.Player.End();
                env.Advance(31);
            }
            Assert.Equal(1 + ChannelsViewModel.MaxReconnects, env.Player.Played.Count, "niet eindeloos door");
            Assert.True(env.Vm.StatusText.Contains("lukt niet"), env.Vm.StatusText);

            // Opnieuw kiezen werkt weer.
            PlayLive(env);
            Assert.Equal(2 + ChannelsViewModel.MaxReconnects, env.Player.Played.Count);
            env.Player.End();
            env.Advance(2);
            Assert.Equal(3 + ChannelsViewModel.MaxReconnects, env.Player.Played.Count, "verbinden werkt weer na handmatig kiezen");
        });
    }
}
