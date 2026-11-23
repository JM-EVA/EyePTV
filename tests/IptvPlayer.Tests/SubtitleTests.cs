using System.Net;
using System.Text;
using IptvPlayer.Engine;
using IptvPlayer.Engine.ViewModels;

namespace IptvPlayer.Tests;

/// <summary>Audio- en ondertitelsporen, voorkeurstaal, instellingen en het online zoeken van ondertitels.</summary>
public static class SubtitleTests
{
    private static (string, Func<Task>) T(string name, Action a) => ("Ondertitels: " + name, () => { a(); return Task.CompletedTask; });
    private static (string, Func<Task>) TA(string name, Func<Task> a) => ("Ondertitels: " + name, a);

    private sealed class Env : IDisposable
    {
        public required TempStore Tmp { get; init; }
        public required FakePlayer Player { get; init; }
        public required SettingsStore Settings { get; init; }
        public required ChannelsViewModel Vm { get; init; }
        public void Dispose() => Tmp.Dispose();
    }

    private static Env Create()
    {
        var tmp = new TempStore();
        var lib = new Library(tmp.Store);
        var src = lib.AddSource(new IptvSource { Name = "XT", Kind = SourceKind.Xtream, Server = "http://srv.test:8080", Username = "jan", Password = "pw" });
        var cat = new List<Channel>
        {
            new("xtm:501", "|NL| Dune (2021) 4K", "Actie", "http://srv.test:8080/movie/jan/pw/501.mkv", Kind: ContentKind.Movie),
            new("xts:900", "Serie X", "Comedy", "", Kind: ContentKind.Series, RemoteId: "900"),
        };
        lib.CommitSelection(src.Id, cat, cat.Select(c => c.Key), cat.Select(c => c.Key));

        var player = new FakePlayer();
        var settings = new SettingsStore(tmp.Store);
        var vm = new ChannelsViewModel(lib, new EpgService(new HttpClient(new FakeHandler()), tmp.Store), player, new FakeDialogs(),
            xtream: new XtreamClient(new HttpClient(MediaTests.XtreamServer())), settings: settings);
        vm.SelectKind(ContentKind.Movie);
        return new Env { Tmp = tmp, Player = player, Settings = settings, Vm = vm };
    }

    private static void PlayMovie(Env env) => env.Vm.Rows.First().PlayCommand.Execute(null);

    public static IEnumerable<(string, Func<Task>)> Tests()
    {
        // ---------- Talen en titels ----------

        yield return T("talen worden herkend in de naam van een spoor", () =>
        {
            Assert.True(Languages.Matches("Track 2 - [Dutch]", "nl"));
            Assert.True(Languages.Matches("nld", "nl"));
            Assert.True(Languages.Matches("Nederlands (SDH)", "nl"));
            Assert.True(Languages.Matches("English", "en"));
            Assert.False(Languages.Matches("English", "nl"));
            Assert.False(Languages.Matches("Track 1", "nl"), "geen taal in de naam");
            Assert.False(Languages.Matches("Dutchess", "nl"), "hele woorden, geen stukjes");
            Assert.Equal("Nederlands", Languages.DisplayName("nl"));
        });

        // ---------- Instellingen ----------

        yield return T("instellingen: voorkeurstaal wordt bewaard en weer ingelezen", () =>
        {
            using var tmp = new TempStore();
            var store = new SettingsStore(tmp.Store);
            Assert.Equal("nl", store.Current.SubtitleLanguage, "standaard Nederlands");
            var vm = new SettingsViewModel(store);
            vm.SelectedSubtitleLanguage = vm.SubtitleLanguages.First(l => l.Code == "de");
            vm.SelectedAudioLanguage = vm.AudioLanguages.First(l => l.Code == "en");
            vm.AutoSubtitles = false;

            var again = new SettingsStore(new AppStore(tmp.Store.Root));
            Assert.Equal("de", again.Current.SubtitleLanguage);
            Assert.Equal("en", again.Current.AudioLanguage);
            Assert.False(again.Current.AutoSubtitles);

            vm.SelectedSubtitleLanguage = null!; // een ComboBox stuurt null als zijn lijst wisselt: negeren
            Assert.Equal("de", vm.SelectedSubtitleLanguage.Code);
        });

        // ---------- Sporen in de video ----------

        yield return T("sporen: audio en ondertitels worden gescand en samengevat", () =>
        {
            using var env = Create();
            PlayMovie(env);
            Assert.True(env.Vm.ShowTracks);
            Assert.False(env.Vm.HasAudioChoice);

            env.Player.Audio.AddRange(new[] { new TrackInfo(-1, "Disable"), new TrackInfo(1, "Track 1 - [English]"), new TrackInfo(2, "Track 2 - [Dutch]") });
            env.Player.Subs.AddRange(new[] { new TrackInfo(-1, "Disable"), new TrackInfo(5, "Track 1 - [English]") });
            env.Settings.Current.AutoSubtitles = false;
            env.Player.RaiseTracks(audioChoice: 1);

            Assert.True(env.Vm.HasAudioChoice && env.Vm.HasSubtitleChoice);
            Assert.Sequence(new[] { "Track 1 - [English]", "Track 2 - [Dutch]" }, env.Vm.AudioTracks.Select(t => t.Name));
            Assert.Sequence(new[] { "Uit", "Track 1 - [English]" }, env.Vm.SubtitleTracks.Select(t => t.Name));
            Assert.Equal("2 audiosporen · 1 ondertitel", env.Vm.TracksSummary);
            Assert.Equal("Uit", env.Vm.SelectedSubtitleTrack!.Name);

            env.Vm.SelectedAudioTrack = env.Vm.AudioTracks[1];
            Assert.Equal(2, env.Player.AudioChoice);
            env.Vm.SelectedSubtitleTrack = env.Vm.SubtitleTracks[1];
            Assert.Equal(5, env.Player.SubChoice);
            env.Vm.SelectedSubtitleTrack = env.Vm.SubtitleTracks[0];
            Assert.Equal(-1, env.Player.SubChoice, "uitzetten");
        });

        yield return T("sporen: ondertitel in de voorkeurstaal gaat automatisch aan, audio volgt de voorkeur", () =>
        {
            using var env = Create();
            env.Settings.Current.SubtitleLanguage = "nl";
            env.Settings.Current.AudioLanguage = "en";
            PlayMovie(env);
            env.Player.Audio.AddRange(new[] { new TrackInfo(1, "Track 1 - [Dutch]"), new TrackInfo(2, "Track 2 - [English]") });
            env.Player.Subs.AddRange(new[] { new TrackInfo(-1, "Disable"), new TrackInfo(5, "Track 1 - [English]"), new TrackInfo(6, "Track 2 - [Dutch]") });
            env.Player.RaiseTracks(audioChoice: 1);

            Assert.Equal(2, env.Player.AudioChoice, "Engelse audio gekozen");
            Assert.Equal(6, env.Player.SubChoice, "Nederlandse ondertitel aan");
            Assert.Equal("Track 2 - [Dutch]", env.Vm.SelectedSubtitleTrack!.Name);
            Assert.False(env.Vm.HasSubtitleHint);

            // De gebruiker kiest zelf iets anders: bij een volgende melding van de speler niet opnieuw overschrijven.
            env.Vm.SelectedSubtitleTrack = env.Vm.SubtitleTracks[0];
            env.Player.RaiseTracks(audioChoice: 2, subChoice: -1);
            Assert.Equal(-1, env.Player.SubChoice);
        });

        yield return T("sporen: zonder ondertitel in de voorkeurstaal komt er een hint, en niet als de audio al in die taal is", () =>
        {
            using var env = Create();
            PlayMovie(env);
            env.Player.Audio.Add(new TrackInfo(1, "Track 1 - [English]"));
            env.Player.Subs.AddRange(new[] { new TrackInfo(-1, "Disable"), new TrackInfo(5, "Track 1 - [English]") });
            env.Player.RaiseTracks();
            Assert.Equal(-1, env.Player.SubChoice, "niets aangezet");
            Assert.True(env.Vm.HasSubtitleHint);
            Assert.True(env.Vm.SubtitleHint!.Contains("Nederlands"), env.Vm.SubtitleHint);

            // Nieuw item: lijst en hint worden gewist.
            PlayMovie(env);
            Assert.False(env.Vm.HasSubtitleHint);
            Assert.Equal(0, env.Vm.AudioTracks.Count);

            // Audio is al Nederlands: ondertitel niet nodig.
            env.Player.Audio.Clear();
            env.Player.Audio.Add(new TrackInfo(1, "Nederlands"));
            env.Player.RaiseTracks();
            Assert.False(env.Vm.HasSubtitleHint);
        });

        yield return T("sporen: bij live-tv worden de spoorkeuzes niet getoond", () =>
        {
            using var env = Create();
            env.Vm.SelectKind(ContentKind.Live);
            Assert.False(env.Vm.ShowTracks);
        });
    }
}
