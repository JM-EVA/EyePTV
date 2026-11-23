using IptvPlayer.Engine;
using IptvPlayer.Engine.ViewModels;

namespace IptvPlayer.Tests;

/// <summary>De pincode voor erotiek en het hernoemen van zenders.</summary>
public static class AdultTests
{
    private static (string, Func<Task>) T(string name, Action a) => ("Erotiek/namen: " + name, () => { a(); return Task.CompletedTask; });

    private sealed class Env : IDisposable
    {
        public required TempStore Tmp { get; init; }
        public required Library Lib { get; init; }
        public required SettingsStore Settings { get; init; }
        public required AdultLock Lock { get; init; }
        public required FakePlayer Player { get; init; }
        public required FakeDialogs Dialogs { get; init; }
        public required ChannelsViewModel Vm { get; init; }
        public DateTime Clock { get; set; } = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
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
        var cat = new List<Channel>
        {
            new("k:1", "NPO 1", "Nederland", "http://srv.test/1.ts", Kind: ContentKind.Live),
            new("k:2", "Spice Night", "18| FOR ADULTS", "http://srv.test/2.ts", Kind: ContentKind.Live),
            new("k:3", "Gewone Film", "Actie", "http://srv.test/3.mp4", Kind: ContentKind.Movie),
            new("k:4", "[X] Studio Titel", "Films", "http://srv.test/4.mp4", Kind: ContentKind.Movie),
        };
        lib.CommitSelection(src.Id, cat, cat.Select(c => c.Key), cat.Select(c => c.Key));

        var settings = new SettingsStore(tmp.Store);
        Env? env = null;
        var adult = new AdultLock(settings, () => env!.Clock);
        var player = new FakePlayer();
        var dialogs = new FakeDialogs();
        var epg = new EpgService(new HttpClient(new FakeHandler()), tmp.Store);
        var vm = new ChannelsViewModel(lib, epg, player, dialogs, () => new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc),
            settings: settings, adultLock: adult);
        return env = new Env { Tmp = tmp, Lib = lib, Settings = settings, Lock = adult, Player = player, Dialogs = dialogs, Vm = vm };
    }

    public static IEnumerable<(string, Func<Task>)> Tests()
    {
        yield return T("standaardcode is 0000, een verkeerde code ontgrendelt niet", () =>
        {
            using var env = Create();
            Assert.False(env.Lock.IsUnlocked, "begint vergrendeld");
            Assert.True(env.Lock.UsesDefaultPin);
            Assert.Equal(UnlockResult.Wrong, env.Lock.TryUnlock("1234"));
            Assert.Equal(UnlockResult.Wrong, env.Lock.TryUnlock("12"));
            Assert.Equal(UnlockResult.Wrong, env.Lock.TryUnlock("abcd"));
            Assert.False(env.Lock.IsUnlocked);
            Assert.Equal(UnlockResult.Unlocked, env.Lock.TryUnlock("0000"));
            Assert.True(env.Lock.IsUnlocked);
        });

        yield return T("vijf keer fout geeft een halve minuut wachttijd", () =>
        {
            using var env = Create();
            for (var i = 0; i < 4; i++) Assert.Equal(UnlockResult.Wrong, env.Lock.TryUnlock("1111"));
            Assert.Equal(UnlockResult.Blocked, env.Lock.TryUnlock("1111"));
            Assert.Equal(UnlockResult.Blocked, env.Lock.TryUnlock("0000"), "ook de goede code wacht");
            env.Clock = env.Clock.AddSeconds(31);
            Assert.Equal(UnlockResult.Unlocked, env.Lock.TryUnlock("0000"));
        });

        yield return T("code wijzigen: bewaard als hash, oude code werkt niet meer, ook niet na herstart", () =>
        {
            using var env = Create();
            Assert.Equal("De huidige code klopt niet.", env.Lock.ChangePin("9999", "4321"));
            Assert.True(env.Lock.ChangePin("0000", "12") is not null, "te kort");
            Assert.True(env.Lock.ChangePin("0000", "12ab") is not null, "geen cijfers");
            Assert.Equal(null, env.Lock.ChangePin("0000", "4321"));
            Assert.False(env.Lock.UsesDefaultPin);
            Assert.False(env.Settings.Current.AdultPinHash.Contains("4321"), "geen leesbare code in de instellingen");

            Assert.Equal(UnlockResult.Wrong, env.Lock.TryUnlock("0000"));
            Assert.Equal(UnlockResult.Unlocked, env.Lock.TryUnlock("4321"));

            var again = new AdultLock(new SettingsStore(env.Tmp.Store));
            Assert.Equal(UnlockResult.Wrong, again.TryUnlock("0000"));
            Assert.Equal(UnlockResult.Unlocked, again.TryUnlock("4321"));
        });

        yield return T("vergrendeld: erotiek staat nergens in de lijsten of zoekresultaten", () =>
        {
            using var env = Create();
            Assert.Sequence(new[] { "NPO 1" }, env.Vm.Rows.Select(r => r.Name));
            env.Vm.SearchText = "spice";
            Assert.Equal(0, env.Vm.Rows.Count);
            env.Vm.SearchText = "";
            Assert.False(env.Vm.Groups.Any(g => g.Contains("ADULT", StringComparison.OrdinalIgnoreCase)), "ook de groep is weg");

            env.Vm.SelectKind(ContentKind.Movie);
            Assert.Sequence(new[] { "Gewone Film" }, env.Vm.Rows.Select(r => r.Name));
        });

        yield return T("ontgrendelen toont erotiek, vergrendelen verbergt het weer en stopt wat speelt", () =>
        {
            using var env = Create();
            env.Lock.TryUnlock("0000");
            Assert.Sequence(new[] { "NPO 1", "Spice Night" }, env.Vm.Rows.Select(r => r.Name));

            env.Vm.Rows.First(r => r.Name == "Spice Night").PlayCommand.Execute(null);
            Assert.True(env.Vm.Current?.Name == "Spice Night", "speelt");

            env.Lock.Lock();
            Assert.True(env.Vm.Current is null, "gestopt bij vergrendelen");
            Assert.Sequence(new[] { "NPO 1" }, env.Vm.Rows.Select(r => r.Name));
        });

        yield return T("naam wijzigen: eigen weergavenaam, officiële naam blijft, terugzetten kan", () =>
        {
            using var env = Create();
            var row = env.Vm.Rows.First(r => r.Name == "NPO 1");
            env.Dialogs.TextAnswer = "  Mijn Een ";
            row.RenameCommand.Execute(null);
            Assert.Equal("NPO 1", env.Dialogs.TextQuestions.Single(), "het dialoogvenster begint bij de huidige naam");
            Assert.Equal("Mijn Een", row.Name);
            Assert.True(row.IsRenamed);
            Assert.Equal("NPO 1", row.Model.Name, "officiële naam blijft");

            // Zoeken werkt op beide namen.
            env.Vm.SearchText = "mijn";
            Assert.Equal(1, env.Vm.Rows.Count);
            env.Vm.SearchText = "npo";
            Assert.Equal(1, env.Vm.Rows.Count);
            env.Vm.SearchText = "";

            // Bewaard.
            var reloaded = new Library(env.Tmp.Store);
            Assert.Equal("Mijn Een", reloaded.Personal.First(p => p.Key == "k:1").DisplayName);

            // Annuleren verandert niets; leeg of de officiële naam zet terug.
            env.Dialogs.TextAnswer = null;
            row.RenameCommand.Execute(null);
            Assert.Equal("Mijn Een", row.Name);
            row.ResetNameCommand.Execute(null);
            Assert.Equal("NPO 1", row.Name);
            Assert.False(row.IsRenamed);
            env.Dialogs.TextAnswer = "NPO 1";
            row.RenameCommand.Execute(null);
            Assert.False(row.IsRenamed, "dezelfde naam als het origineel telt niet als eigen naam");
        });

        yield return T("een update van de bron wijzigt de officiële naam maar laat de eigen naam staan", () =>
        {
            using var env = Create();
            var p = env.Lib.Personal.First(x => x.Key == "k:1");
            env.Lib.Rename(p, "Mijn Een");
            p.UpdateFrom(new Channel("k:1", "NPO 1 HD", "Nederland", "http://srv.test/1.ts", Kind: ContentKind.Live));
            Assert.Equal("NPO 1 HD", p.Name);
            Assert.Equal("Mijn Een", p.DisplayName);
        });
    }
}
