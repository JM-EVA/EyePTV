using IptvPlayer.Engine;
using IptvPlayer.Engine.ViewModels;

namespace IptvPlayer.Tests;

/// <summary>Dezelfde zender via een andere stream: uit andere groepen, ook uit groepen die je niet hebt gekozen.</summary>
public static class AlternativesTests
{
    private static (string, Func<Task>) TA(string name, Func<Task> a) => ("Alternatieve streams: " + name, a);

    private sealed class Env : IDisposable
    {
        public required TempStore Tmp { get; init; }
        public required FakePlayer Player { get; init; }
        public required ChannelsViewModel Vm { get; init; }
        public required AdultLock Lock { get; init; }
        public void Dispose() => Tmp.Dispose();
    }

    /// <summary>De url die de nep-afspeellijst voor een zender maakt.</summary>
    private static string Url(string name) => $"http://stream.test/{Uri.EscapeDataString(name)}.ts";

    private static Task<Env> CreateAsync() => CreateAsync(
        new[] { ("npo1.nl", "NPO 1", "NL Algemeen"), ("b", "NL: NPO 1 HD", "NL HD"), ("c", "NPO 1 FHD [backup]", "NL Backup"),
                ("d", "NPO 1 Extra", "NL Algemeen"),       // een andere zender
                ("npo1.nl", "Npo-1", "NL Adults"),          // zelfde EPG-id, maar in een erotiekgroep
                ("f", "RTL 4", "NL Algemeen") },
        "NPO 1", "RTL 4");

    /// <param name="channels">Wat de bron aanbiedt.</param>
    /// <param name="mineNames">Alleen deze staan in je lijst; de rest staat alleen in de catalogus (niet gekozen).</param>
    private static async Task<Env> CreateAsync((string, string, string)[] channels, params string[] mineNames)
    {
        var tmp = new TempStore();
        var lib = new Library(tmp.Store);
        var src = lib.AddSource(new IptvSource { Name = "Prov", Kind = SourceKind.M3uUrl, M3uUrl = "http://x/list.m3u" });
        var m3u = Samples.M3u(channels);
        var outcome = await lib.SyncAsync(src, new CatalogService(new HttpClient(new FakeHandler().Add("list.m3u", m3u))));

        var all = outcome.Catalog.Channels;
        var mine = all.Where(c => mineNames.Contains(c.Name)).Select(c => c.Key).ToList();
        lib.CommitSelection(src.Id, all, mine, all.Select(c => c.Key));

        var player = new FakePlayer();
        var epg = new EpgService(new HttpClient(new FakeHandler()), tmp.Store);
        var settings = new SettingsStore(tmp.Store);
        var adult = new AdultLock(settings);
        var vm = new ChannelsViewModel(lib, epg, player, new FakeDialogs(), settings: settings, adultLock: adult);
        return new Env { Tmp = tmp, Player = player, Vm = vm, Lock = adult };
    }

    private static async Task Until(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TestFailure("Time-out: " + what);
            await Task.Delay(10);
        }
    }

    public static IEnumerable<(string, Func<Task>)> Tests()
    {
        yield return TA("zelfde zender onder een andere naam of groep wordt gevonden, ook als die groep niet gekozen is", async () =>
        {
            using var env = await CreateAsync();
            env.Vm.Rows.First(r => r.Name == "NPO 1").PlayCommand.Execute(null);
            await Until(() => env.Vm.HasAlternatives, "alternatieven gevonden");

            var urls = env.Vm.Alternatives.Select(a => a.Url).OrderBy(u => u).ToList();
            Assert.Sequence(new[] { Url("NL: NPO 1 HD"), Url("NPO 1 FHD [backup]") }.OrderBy(u => u),
                urls, "HD en backup horen erbij; Extra en erotiek niet");
            Assert.True(env.Vm.AlternativesText.Contains('2'));
            Assert.True(env.Vm.Alternatives.All(a => a.Title.Contains("·")), "met de groep erbij, zodat je ze uit elkaar houdt");
        });

        yield return TA("een andere stream kiezen speelt die, de zender in je lijst blijft staan, en je kunt terug", async () =>
        {
            using var env = await CreateAsync();
            env.Vm.Rows.First(r => r.Name == "NPO 1").PlayCommand.Execute(null);
            await Until(() => env.Vm.HasAlternatives, "gevonden");
            var played = env.Player.Played.Count;

            env.Vm.PlayAlternative(env.Vm.Alternatives.First(a => a.Url == Url("NL: NPO 1 HD")));
            Assert.Equal(played + 1, env.Player.Played.Count);
            Assert.Equal(Url("NL: NPO 1 HD"), env.Player.Played.Last());
            Assert.Equal("NPO 1", env.Vm.Current!.Name, "dezelfde zender");
            Assert.True(env.Vm.Alternatives.Any(a => a.Url == Url("NPO 1")), "de oorspronkelijke stream is nu een keuze");
            Assert.False(env.Vm.Alternatives.Any(a => a.Url == Url("NL: NPO 1 HD")), "wat speelt staat er niet bij");

            env.Vm.PlayAlternative(env.Vm.Alternatives.First(a => a.Url == Url("NPO 1")));
            Assert.Equal(Url("NPO 1"), env.Player.Played.Last());
        });

        yield return TA("een automatische reconnect blijft bij de gekozen stream", async () =>
        {
            using var env = await CreateAsync();
            env.Vm.Rows.First(r => r.Name == "NPO 1").PlayCommand.Execute(null);
            await Until(() => env.Vm.HasAlternatives, "gevonden");
            env.Vm.PlayAlternative(env.Vm.Alternatives.First(a => a.Url == Url("NPO 1 FHD [backup]")));

            env.Player.End(); // de stream valt weg: meteen opnieuw
            Assert.Equal(Url("NPO 1 FHD [backup]"), env.Player.Played.Last(), "niet terug naar de oorspronkelijke");
        });

        yield return TA("erotiek verschijnt alleen als alternatief na ontgrendelen; bij stoppen zijn er geen", async () =>
        {
            using var env = await CreateAsync();
            env.Lock.TryUnlock("0000");
            env.Vm.Rows.First(r => r.Name == "NPO 1").PlayCommand.Execute(null);
            await Until(() => env.Vm.Alternatives.Any(a => a.Url == Url("Npo-1")), "inclusief de erotiekgroep");

            env.Vm.Stop();
            Assert.False(env.Vm.HasAlternatives, "niets speelt");
        });

        yield return TA("tv's kunnen meestal geen 4K: een 4K-zender gaat bij casten vanzelf naar de HD-versie", async () =>
        {
            var channels = new[] { ("a", "NPO 1 4K", "NL 4K"), ("b", "NPO 1 HD", "NL HD"), ("c", "NPO 1 FHD", "NL FHD"), ("d", "NPO 1 UHD HEVC", "NL UHD") };

            using (var env = await CreateAsync(channels, "NPO 1 4K"))
            {
                env.Player.Targets.Add(new CastTarget("cc:Slaapkamer", "Slaapkamer", "Chromecast"));
                env.Vm.Rows.First().PlayCommand.Execute(null);
                await Until(() => env.Vm.HasAlternatives, "gevonden");
                env.Vm.CastTo("cc:Slaapkamer");

                Assert.Equal(Url("NPO 1 FHD"), env.Player.Played.Last(), "FHD gaat voor HD; 4K en UHD niet");
                Assert.True(env.Vm.StatusText.Contains("HD-versie"), env.Vm.StatusText);
                Assert.Equal("NPO 1 4K", env.Vm.Current!.Name, "de zender in je lijst blijft dezelfde");
            }

            // Zonder HD-versie blijft het zoals het is.
            using (var env = await CreateAsync(new[] { ("a", "RTL 4 4K", "NL 4K") }, "RTL 4 4K"))
            {
                env.Player.Targets.Add(new CastTarget("cc:Slaapkamer", "Slaapkamer", "Chromecast"));
                env.Vm.Rows.First().PlayCommand.Execute(null);
                await Task.Delay(200);
                var played = env.Player.Played.Count;
                env.Vm.CastTo("cc:Slaapkamer");
                Assert.Equal(played, env.Player.Played.Count);
            }
        });

        yield return TA("een zender zonder tweelingen heeft geen knop", async () =>
        {
            using var env = await CreateAsync();
            env.Vm.Rows.First(r => r.Name == "RTL 4").PlayCommand.Execute(null);
            await Task.Delay(300);
            Assert.False(env.Vm.HasAlternatives);
        });
    }
}
