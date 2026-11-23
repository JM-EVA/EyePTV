using IptvPlayer.Engine;
using IptvPlayer.Engine.ViewModels;

namespace IptvPlayer.Tests;

/// <summary>Casten naar een tv (Chromecast / Google TV).</summary>
public static class CastTests
{
    private static (string, Func<Task>) T(string name, Action a) => ("Casten: " + name, () => { a(); return Task.CompletedTask; });

    private sealed class Env : IDisposable
    {
        public required TempStore Tmp { get; init; }
        public required FakePlayer Player { get; init; }
        public required ChannelsViewModel Vm { get; init; }
        public void Dispose() => Tmp.Dispose();
    }

    private static Env Create()
    {
        var tmp = new TempStore();
        var lib = new Library(tmp.Store);
        var src = lib.AddSource(new IptvSource
        {
            Name = "XT", Kind = SourceKind.Xtream, Server = "http://srv.test:8080", Username = "jan", Password = "pw", ServerTimeZone = "UTC"
        });
        var cat = new List<Channel> { new("k:1", "NPO 1", "Nederland", "http://srv.test/1.ts", Kind: ContentKind.Live) };
        lib.CommitSelection(src.Id, cat, cat.Select(c => c.Key), cat.Select(c => c.Key));

        var player = new FakePlayer();
        var epg = new EpgService(new HttpClient(new FakeHandler()), tmp.Store);
        var vm = new ChannelsViewModel(lib, epg, player, new FakeDialogs());
        return new Env { Tmp = tmp, Player = player, Vm = vm };
    }

    public static IEnumerable<(string, Func<Task>)> Tests()
    {
        yield return T("de castknop is er pas als er iets speelt; zoeken naar apparaten begint pas na een klik", () =>
        {
            using var env = Create();
            Assert.False(env.Vm.ShowCastButton, "niets speelt");
            env.Vm.Rows.First().PlayCommand.Execute(null);
            Assert.True(env.Vm.ShowCastButton);
            Assert.False(env.Player.DiscoveryStarted, "nog niets op het netwerk gedaan");

            env.Vm.StartCastDiscovery();
            Assert.True(env.Player.DiscoveryStarted);
        });

        yield return T("naar een tv casten: het beeld staat op de tv, op de pc een bericht; terug naar de pc kan", () =>
        {
            using var env = Create();
            env.Player.Targets.Add(new CastTarget("tv1", "Woonkamer TV"));
            env.Vm.Rows.First().PlayCommand.Execute(null);
            env.Player.Tick(1000, 0); // er is beeld
            Assert.True(env.Vm.ShowVideo && !env.Vm.ShowCastCover);

            env.Vm.CastTo("tv1");
            Assert.True(env.Vm.IsCasting);
            Assert.Equal("Woonkamer TV", env.Vm.CastingTo);
            Assert.False(env.Vm.ShowVideo, "geen lokaal beeld");
            Assert.True(env.Vm.ShowCover);
            Assert.Equal("Casten naar Woonkamer TV…", env.Vm.StatusText);

            env.Player.Tick(2000, 0); // de tv begint
            Assert.True(env.Vm.ShowCastCover, "bericht op de pc");
            Assert.Equal("Je kijkt op Woonkamer TV", env.Vm.CastCoverText);

            env.Vm.CastTo(null);
            Assert.False(env.Vm.IsCasting);
            env.Player.Tick(3000, 0);
            Assert.True(env.Vm.ShowVideo, "weer beeld op de pc");
        });

        yield return T("een apparaat dat weg is: melding en geen casten", () =>
        {
            using var env = Create();
            env.Vm.Rows.First().PlayCommand.Execute(null);
            env.Vm.CastTo("bestaat-niet");
            Assert.False(env.Vm.IsCasting);
            Assert.True(env.Vm.StatusText.Contains("Casten lukt niet"), env.Vm.StatusText);
        });

        yield return T("nieuwe apparaten komen door naar een open menu", () =>
        {
            using var env = Create();
            var raised = 0;
            env.Vm.CastTargetsChanged += () => raised++;
            env.Player.Targets.Add(new CastTarget("tv1", "Woonkamer TV"));
            env.Player.RaiseTargets();
            Assert.Equal(1, raised);
            Assert.Equal("Woonkamer TV", env.Vm.CastTargets.Single().Name);
        });
    }
}
