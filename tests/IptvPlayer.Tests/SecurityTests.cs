using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using IptvPlayer.Engine;
using IptvPlayer.Engine.ViewModels;

namespace IptvPlayer.Tests;

/// <summary>Beveiliging: versleutelde opslag, toegestane protocollen, grenzen op downloads en veilige bestandsnamen.</summary>
public static class SecurityTests
{
    private static (string, Func<Task>) T(string name, Action a) => ("Beveiliging: " + name, () => { a(); return Task.CompletedTask; });
    private static (string, Func<Task>) TA(string name, Func<Task> a) => ("Beveiliging: " + name, a);

    /// <summary>Nep-DPAPI: draait de bytes om, genoeg om te zien dat de sleutel niet leesbaar op schijf staat.</summary>
    private sealed class ReverseProtector : IKeyProtector
    {
        public bool Fail { get; set; }
        public byte[] Protect(byte[] data) => data.Reverse().ToArray();
        public byte[] Unprotect(byte[] data) => Fail ? throw new CryptographicException("ander account") : data.Reverse().ToArray();
    }

    private static string RawText(AppStore store, string file) => Encoding.UTF8.GetString(File.ReadAllBytes(store.PathOf(file)));

    public static IEnumerable<(string, Func<Task>)> Tests()
    {
        // ---------- Versleutelde opslag ----------

        yield return T("FileCipher: versleutelen en ontsleutelen geeft hetzelfde terug; aanpassen wordt opgemerkt", () =>
        {
            var cipher = new FileCipher(RandomNumberGenerator.GetBytes(32));
            var plain = Encoding.UTF8.GetBytes("{\"password\":\"geheim\"}");
            var enc = cipher.Encrypt(plain);
            Assert.True(FileCipher.IsEncrypted(enc));
            Assert.False(Encoding.UTF8.GetString(enc).Contains("geheim"), "geen leesbare tekst");
            Assert.Sequence(plain, cipher.Decrypt(enc));
            Assert.False(enc.SequenceEqual(cipher.Encrypt(plain)), "elke keer een nieuwe nonce");

            enc[^1] ^= 0xFF;
            var failed = false;
            try { cipher.Decrypt(enc); } catch (CryptographicException) { failed = true; }
            Assert.True(failed, "een aangepast bestand wordt geweigerd");

            Assert.False(FileCipher.IsEncrypted(plain), "gewone JSON is niet versleuteld");
        });

        yield return T("sleutel: wordt aangemaakt, beschermd opgeslagen en hergebruikt", () =>
        {
            using var tmp = new TempStore();
            var prot = new ReverseProtector();
            var a = FileCipher.Open(tmp.Store.Root, prot);
            Assert.Equal(32, File.ReadAllBytes(tmp.Store.PathOf("key.dpapi")).Length);

            var enc = a.Encrypt(new byte[] { 1, 2, 3 });
            var b = FileCipher.Open(tmp.Store.Root, prot); // tweede start: zelfde sleutel
            Assert.Sequence(new byte[] { 1, 2, 3 }, b.Decrypt(enc));
        });

        yield return T("opslag: bronnen, instellingen, lijst en catalogus staan versleuteld op schijf; de rest niet", () =>
        {
            using var tmp = new TempStore();
            var store = new AppStore(tmp.Store.Root, FileCipher.Open(tmp.Store.Root, new ReverseProtector()));

            var lib = new Library(store);
            var src = lib.AddSource(new IptvSource { Name = "XT", Kind = SourceKind.Xtream, Server = "https://srv.test", Username = "jan", Password = "SUPERGEHEIM" });
            var cat = new List<Channel> { new("xt:1", "NOS", "Nieuws", "https://srv.test/live/jan/SUPERGEHEIM/1.ts", RemoteId: "1") };
            lib.CommitSelection(src.Id, cat, cat.Select(c => c.Key), cat.Select(c => c.Key));

            var settings = new SettingsStore(store);
            settings.Current.AudioLanguage = "en";
            settings.Save();

            foreach (var f in new[] { "sources.json", "settings.json", "personal.json" })
            {
                Assert.True(FileCipher.IsEncrypted(File.ReadAllBytes(store.PathOf(f))), f + " is versleuteld");
                Assert.False(RawText(store, f).Contains("GEHEIM"), f + " bevat geen leesbaar wachtwoord");
            }
            store.Save("catalog-abc.json", new { channels = cat }, indented: false); // zoals Library.SaveCatalog dat doet
            var catalogFile = store.PathOf("catalog-abc.json");
            Assert.True(FileCipher.IsEncrypted(File.ReadAllBytes(catalogFile)), "catalogus versleuteld");
            Assert.False(Encoding.UTF8.GetString(File.ReadAllBytes(catalogFile)).Contains("GEHEIM"), "catalogus bevat geen leesbare inlog");
            Assert.False(Directory.GetFiles(store.Root, "state-*.json").Any(f => FileCipher.IsEncrypted(File.ReadAllBytes(f))), "voortgang hoeft niet versleuteld");

            // En alles komt er bij een volgende start weer goed uit.
            var again = new Library(new AppStore(tmp.Store.Root, FileCipher.Open(tmp.Store.Root, new ReverseProtector())));
            Assert.Equal("SUPERGEHEIM", again.Sources.Single().Password);
            Assert.Equal(1, again.Personal.Count);
            var settingsAgain = new SettingsStore(new AppStore(tmp.Store.Root, FileCipher.Open(tmp.Store.Root, new ReverseProtector())));
            Assert.Equal("en", settingsAgain.Current.AudioLanguage);
        });

        yield return T("opslag: een oud, leesbaar bestand wordt bij het lezen meteen versleuteld", () =>
        {
            using var tmp = new TempStore();
            File.WriteAllText(tmp.Store.PathOf("sources.json"),
                "[{\"id\":\"a\",\"name\":\"Oud\",\"kind\":1,\"server\":\"http://x\",\"username\":\"u\",\"password\":\"OUDWACHTWOORD\"}]");

            var store = new AppStore(tmp.Store.Root, FileCipher.Open(tmp.Store.Root, new ReverseProtector()));
            var lib = new Library(store);
            Assert.Equal("OUDWACHTWOORD", lib.Sources.Single().Password, "inhoud blijft behouden");
            Assert.True(FileCipher.IsEncrypted(File.ReadAllBytes(store.PathOf("sources.json"))), "nu versleuteld");
            Assert.False(RawText(store, "sources.json").Contains("OUDWACHTWOORD"));
        });

        yield return T("opslag: een bestand van een ander Windows-account wordt apart gezet, niet overschreven", () =>
        {
            using var tmp = new TempStore();
            new AppStore(tmp.Store.Root, new FileCipher(RandomNumberGenerator.GetBytes(32)))
                .Save("settings.json", new AppSettings { AudioLanguage = "en" });

            var other = new AppStore(tmp.Store.Root, new FileCipher(RandomNumberGenerator.GetBytes(32))); // andere sleutel
            var loaded = other.Load("settings.json", () => new AppSettings { AudioLanguage = "leeg" });
            Assert.Equal("leeg", loaded.AudioLanguage, "terugval");
            Assert.Equal(1, Directory.GetFiles(tmp.Store.Root, "settings.json.unreadable-*").Length, "origineel bewaard");
        });

        yield return T("sleutel: een onleesbare sleutel (ander account) wordt vervangen en het oude bestand bewaard", () =>
        {
            using var tmp = new TempStore();
            FileCipher.Open(tmp.Store.Root, new ReverseProtector());
            var cipher = FileCipher.Open(tmp.Store.Root, new ReverseProtector { Fail = true });
            Assert.True(cipher is not null);
            Assert.Equal(1, Directory.GetFiles(tmp.Store.Root, "key.dpapi.unreadable-*").Length, "oude sleutel apart gezet");
        });

        // ---------- Protocollen ----------

        yield return T("links: alleen streamingprotocollen zijn toegestaan", () =>
        {
            foreach (var ok in new[] { "http://a/b.ts", "https://a/b", "RTSP://cam/1", "rtmp://x/live", "udp://@239.1.1.1:1234", "rtp://@1.2.3.4:5000" })
                Assert.True(PlayableUrl.IsAllowed(ok), ok);
            foreach (var bad in new[] { "smb://aanvaller/share/x.mp4", "file:///C:/Windows/win.ini", @"\\aanvaller\share\x.mp4", "ftp://x/y", "dvd://", "vlc://quit", "screen://", "", "x.ts", "javascript:alert(1)" })
                Assert.False(PlayableUrl.IsAllowed(bad), bad);
        });

        yield return T("M3U: zenders met smb://, file:// e.d. komen niet in de lijst", () =>
        {
            const string m3u = "#EXTM3U\n" +
                "#EXTINF:-1,Goed\nhttp://srv/live/1.ts\n" +
                "#EXTINF:-1,Aanval smb\nsmb://aanvaller/share/x.mp4\n" +
                "#EXTINF:-1,Aanval file\nfile:///C:/Windows/win.ini\n" +
                "#EXTINF:-1,Ook goed\nrtsp://cam/1\n";
            var list = M3uParser.Parse(m3u);
            Assert.Sequence(new[] { "Goed", "Ook goed" }, list.Channels.Select(c => c.Name));
        });

        yield return T("speler: een link met een onveilig protocol wordt niet afgespeeld, met een melding", () =>
        {
            using var tmp = new TempStore();
            var lib = new Library(tmp.Store);
            var src = lib.AddSource(new IptvSource { Name = "M", Kind = SourceKind.M3uUrl, M3uUrl = "https://m/x.m3u" });
            var cat = new List<Channel> { new("a", "Slecht", "G", "smb://aanvaller/share/x.ts"), new("b", "Goed", "G", "https://srv/1.ts") };
            lib.CommitSelection(src.Id, cat, cat.Select(c => c.Key), cat.Select(c => c.Key));
            var player = new FakePlayer();
            var vm = new ChannelsViewModel(lib, new EpgService(new HttpClient(new FakeHandler()), tmp.Store), player, new FakeDialogs());

            vm.Rows.First(r => r.Name == "Slecht").PlayCommand.Execute(null);
            Assert.Equal(0, player.Played.Count, "niet gestart");
            Assert.True(vm.StatusText.Contains("veiligheidsredenen"), vm.StatusText);
            Assert.True(vm.ShowStartScreen, "er is niets gekozen");

            vm.Rows.First(r => r.Name == "Goed").PlayCommand.Execute(null);
            Assert.Equal(1, player.Played.Count);
        });

        // ---------- Grenzen ----------

        yield return TA("LimitedReadStream: stopt met een duidelijke fout boven de grens", async () =>
        {
            await using var ok = new LimitedReadStream(new MemoryStream(new byte[1000]), 1000);
            using var sink = new MemoryStream();
            await ok.CopyToAsync(sink);
            Assert.Equal(1000, (int)sink.Length);

            var failed = false;
            try
            {
                await using var big = new LimitedReadStream(new MemoryStream(new byte[2000]), 1000);
                await big.CopyToAsync(new MemoryStream());
            }
            catch (InvalidDataException) { failed = true; }
            Assert.True(failed, "te groot");
        });

        yield return T("EPG: een gzip-bom wordt niet helemaal uitgepakt", () =>
        {
            // 3 MB aan nullen wordt tot een paar kB; met een grens van 1 MB moet het uitpakken stoppen.
            using var packed = new MemoryStream();
            using (var gz = new GZipStream(packed, CompressionLevel.SmallestSize, leaveOpen: true))
                gz.Write(new byte[3 * 1024 * 1024]);
            packed.Position = 0;
            var failed = false;
            try
            {
                using var limited = new LimitedReadStream(new GZipStream(packed, CompressionMode.Decompress), 1024 * 1024);
                limited.CopyTo(Stream.Null);
            }
            catch (InvalidDataException) { failed = true; }
            Assert.True(failed);
        });

        // ---------- Bronnen ----------

        yield return T("bronnen: http wordt als onveilig gemarkeerd, https niet", () =>
        {
            Assert.True(SourceRow.IsInsecure(new IptvSource { Kind = SourceKind.Xtream, Server = "http://srv:8080" }));
            Assert.False(SourceRow.IsInsecure(new IptvSource { Kind = SourceKind.Xtream, Server = "https://srv" }));
            Assert.True(SourceRow.IsInsecure(new IptvSource { Kind = SourceKind.M3uUrl, M3uUrl = "http://x/y.m3u" }));
            Assert.True(new SourceRow(new IptvSource { Kind = SourceKind.Xtream, Server = "http://srv" }).InsecureNote!.Contains("onversleuteld"));
            Assert.True(new SourceRow(new IptvSource { Kind = SourceKind.Xtream, Server = "https://srv" }).InsecureNote is null);
        });
    }
}
