using IptvPlayer.Engine;

namespace IptvPlayer.Tests;

public static class LibraryTests
{
    private static (string, Func<Task>) T(string name, Action a) => ("Library: " + name, () => { a(); return Task.CompletedTask; });
    private static (string, Func<Task>) TA(string name, Func<Task> a) => ("Library: " + name, a);

    private static Channel Ch(string key, string name, string group = "G", string url = "http://u/") =>
        new(key, name, group, url + key);

    private static IptvSource NewSource(Library lib, bool done = false)
    {
        var s = lib.AddSource(new IptvSource { Name = "Prov", Kind = SourceKind.M3uUrl, M3uUrl = "http://x/list.m3u" });
        s.InitialImportDone = done;
        return s;
    }

    private static CatalogService ServiceFor(Func<string> m3u) =>
        new(new HttpClient(new FakeHandler().Add("list.m3u", m3u)));

    public static IEnumerable<(string, Func<Task>)> Tests()
    {
        yield return T("eerste selectie voegt alleen aangevinkte zenders toe en markeert alles als gezien", () =>
        {
            using var tmp = new TempStore();
            var lib = new Library(tmp.Store);
            var s = NewSource(lib);
            var catalog = new[] { Ch("a", "A"), Ch("b", "B"), Ch("c", "C") };

            lib.CommitSelection(s.Id, catalog, catalog.Select(c => c.Key), new[] { "a", "c" });

            Assert.Sequence(new[] { "a", "c" }, lib.Ordered().Select(p => p.Key));
            Assert.True(s.InitialImportDone, "import gemarkeerd");
            Assert.Equal(3, lib.GetState(s.Id).KnownKeys.Count, "alles gezien, ook b");
        });

        yield return T("diff: alleen echt nieuwe zenders worden aangeboden, afgewezen zenders komen niet terug", () =>
        {
            using var tmp = new TempStore();
            var lib = new Library(tmp.Store);
            var s = NewSource(lib);
            var v1 = new[] { Ch("a", "A"), Ch("b", "B") };
            lib.CommitSelection(s.Id, v1, v1.Select(c => c.Key), new[] { "a" }); // b bewust niet

            var v2 = v1.Concat(new[] { Ch("c", "C"), Ch("d", "D") }).ToList();
            var diff = SyncDiff.Compute(v2, lib.GetState(s.Id), lib.PersonalOf(s.Id));

            Assert.Sequence(new[] { "c", "d" }, diff.New.Select(c => c.Key), "nieuw");
            Assert.Equal(0, diff.Removed.Count, "verdwenen");
        });

        yield return T("review: alleen aangevinkte nieuwe zenders komen erbij; de rest wordt 'gezien' en komt niet meer terug", () =>
        {
            using var tmp = new TempStore();
            var lib = new Library(tmp.Store);
            var s = NewSource(lib);
            var v1 = new[] { Ch("a", "A") };
            lib.CommitSelection(s.Id, v1, v1.Select(c => c.Key), new[] { "a" });

            var v2 = new[] { Ch("a", "A"), Ch("c", "C"), Ch("d", "D") };
            var diff = SyncDiff.Compute(v2, lib.GetState(s.Id), lib.PersonalOf(s.Id));
            lib.CommitSelection(s.Id, v2, diff.New.Select(c => c.Key), new[] { "c" });

            Assert.Sequence(new[] { "a", "c" }, lib.Ordered().Select(p => p.Key), "lijst");
            var again = SyncDiff.Compute(v2, lib.GetState(s.Id), lib.PersonalOf(s.Id));
            Assert.Equal(0, again.New.Count, "d wordt niet opnieuw aangeboden");
        });

        yield return T("verdwenen zenders: gemarkeerd, en verwijderbaar via review", () =>
        {
            using var tmp = new TempStore();
            var lib = new Library(tmp.Store);
            var s = NewSource(lib, done: true);
            var v1 = new[] { Ch("a", "A"), Ch("b", "B") };
            lib.CommitSelection(s.Id, v1, v1.Select(c => c.Key), new[] { "a", "b" });

            var v2 = new[] { Ch("a", "A") }; // b is weg
            var diff = SyncDiff.Compute(v2, lib.GetState(s.Id), lib.PersonalOf(s.Id));
            Assert.Sequence(new[] { "b" }, diff.Removed.Select(p => p.Key));

            lib.CommitSelection(s.Id, v2, Array.Empty<string>(), Array.Empty<string>(), removeKeys: new[] { "b" });
            Assert.Sequence(new[] { "a" }, lib.Ordered().Select(p => p.Key));
        });

        yield return T("browse: aanvinken voegt toe, uitvinken verwijdert uit de lijst", () =>
        {
            using var tmp = new TempStore();
            var lib = new Library(tmp.Store);
            var s = NewSource(lib);
            var cat = new[] { Ch("a", "A"), Ch("b", "B"), Ch("c", "C") };
            lib.CommitSelection(s.Id, cat, cat.Select(c => c.Key), new[] { "a", "b" });

            lib.CommitSelection(s.Id, cat, cat.Select(c => c.Key), new[] { "b", "c" });
            Assert.Sequence(new[] { "b", "c" }, lib.Ordered().Select(p => p.Key));
        });

        yield return T("favoriet en volgorde blijven behouden als de bron naam/link wijzigt", () =>
        {
            using var tmp = new TempStore();
            var lib = new Library(tmp.Store);
            var s = NewSource(lib);
            var v1 = new[] { Ch("a", "A"), Ch("b", "B") };
            lib.CommitSelection(s.Id, v1, v1.Select(c => c.Key), new[] { "a", "b" });
            var b = lib.Personal.First(p => p.Key == "b");
            lib.SetFavorite(b, true);
            lib.MoveToTop(b);

            var v2 = new[] { new Channel("a", "A nieuw", "G", "http://nieuw/a"), new Channel("b", "B", "G2", "http://nieuw/b") };
            lib.CommitSelection(s.Id, v2, Array.Empty<string>(), Array.Empty<string>());

            Assert.Sequence(new[] { "b", "a" }, lib.Ordered().Select(p => p.Key), "volgorde");
            Assert.True(lib.Personal.First(p => p.Key == "b").IsFavorite, "favoriet");
            Assert.Equal("http://nieuw/a", lib.Personal.First(p => p.Key == "a").Url);
            Assert.Equal("G2", lib.Personal.First(p => p.Key == "b").Group);
        });

        yield return T("volgorde: wisselen en naar boven verplaatsen", () =>
        {
            using var tmp = new TempStore();
            var lib = new Library(tmp.Store);
            var s = NewSource(lib);
            var cat = new[] { Ch("a", "A"), Ch("b", "B"), Ch("c", "C") };
            lib.CommitSelection(s.Id, cat, cat.Select(c => c.Key), cat.Select(c => c.Key));

            var o = lib.Ordered();
            lib.Swap(o[0], o[1]);
            Assert.Sequence(new[] { "b", "a", "c" }, lib.Ordered().Select(p => p.Key));
            lib.MoveToTop(lib.Personal.First(p => p.Key == "c"));
            Assert.Sequence(new[] { "c", "b", "a" }, lib.Ordered().Select(p => p.Key));
        });

        yield return T("alles blijft bewaard na herstart van de app", () =>
        {
            using var tmp = new TempStore();
            var lib = new Library(tmp.Store);
            var s = NewSource(lib);
            var cat = new[] { Ch("a", "A"), Ch("b", "B") };
            lib.CommitSelection(s.Id, cat, cat.Select(c => c.Key), new[] { "a" });
            lib.SetFavorite(lib.Personal[0], true);

            var lib2 = new Library(new AppStore(tmp.Path));
            Assert.Equal(1, lib2.Sources.Count, "bronnen");
            Assert.True(lib2.Sources[0].InitialImportDone, "import-vlag");
            Assert.Sequence(new[] { "a" }, lib2.Ordered().Select(p => p.Key), "lijst");
            Assert.True(lib2.Personal[0].IsFavorite, "favoriet");
            Assert.True(lib2.GetState(s.Id).KnownKeys.Contains("b"), "afgewezen zender onthouden");
            Assert.True(lib2.LoadCatalog(s.Id) is null, "geen catalogus-cache zonder sync");
        });

        yield return T("bron verwijderen haalt ook zijn zenders en bestanden weg", () =>
        {
            using var tmp = new TempStore();
            var lib = new Library(tmp.Store);
            var s = NewSource(lib);
            var cat = new[] { Ch("a", "A") };
            lib.CommitSelection(s.Id, cat, cat.Select(c => c.Key), new[] { "a" });
            lib.RemoveSource(s.Id);
            Assert.Equal(0, lib.Personal.Count);
            Assert.Equal(0, lib.Sources.Count);
            Assert.False(File.Exists(tmp.Store.PathOf($"state-{s.Id}.json")), "state-bestand");
        });

        yield return TA("sync: eerste keer geeft InitialImport en past de lijst niet aan", async () =>
        {
            using var tmp = new TempStore();
            var lib = new Library(tmp.Store);
            var s = NewSource(lib);
            var svc = ServiceFor(() => Samples.M3u(("a", "A", "G"), ("b", "B", "G")));

            var outcome = await lib.SyncAsync(s, svc);
            Assert.Equal(SyncOutcomeKind.InitialImport, outcome.Kind);
            Assert.Equal(2, outcome.Catalog.Channels.Count);
            Assert.Equal(0, lib.Personal.Count, "nog niets toegevoegd");
            Assert.Equal("http://epg.test/guide.xml", s.DiscoveredEpgUrl);
            Assert.True(lib.LoadCatalog(s.Id) is { } c && c.Channels.Count == 2, "catalogus gecached");
        });

        yield return TA("sync: gewijzigde links worden stil bijgewerkt, nieuwe zenders vragen om review", async () =>
        {
            using var tmp = new TempStore();
            var lib = new Library(tmp.Store);
            var s = NewSource(lib);
            var version = 1;
            var svc = ServiceFor(() => version == 1
                ? "#EXTM3U\n#EXTINF:-1 tvg-id=\"a\" group-title=\"G\",A\nhttp://old/a\n"
                : "#EXTM3U\n#EXTINF:-1 tvg-id=\"a\" group-title=\"G\",A\nhttp://new/a\n#EXTINF:-1 tvg-id=\"n\" group-title=\"G\",Nieuw\nhttp://new/n\n");

            var first = await lib.SyncAsync(s, svc);
            lib.CommitSelection(s.Id, first.Catalog.Channels, first.Catalog.Channels.Select(c => c.Key), first.Catalog.Channels.Select(c => c.Key));

            version = 2;
            var second = await lib.SyncAsync(s, svc);
            Assert.Equal(SyncOutcomeKind.ReviewNeeded, second.Kind);
            Assert.Sequence(new[] { "n" }.Select(x => x), second.Diff!.New.Select(c => c.EpgId!));
            Assert.Equal("http://new/a", lib.Personal.Single().Url, "link stil bijgewerkt");
            Assert.Equal(1, lib.Personal.Count, "nieuwe zender niet automatisch toegevoegd");
            Assert.Equal(1, lib.PendingDiff(s.Id)!.New.Count, "openstaande wijziging blijft staan tot de gebruiker kiest");
        });

        yield return TA("sync: geen wijzigingen = UpToDate", async () =>
        {
            using var tmp = new TempStore();
            var lib = new Library(tmp.Store);
            var s = NewSource(lib);
            var svc = ServiceFor(() => Samples.M3u(("a", "A", "G")));
            var first = await lib.SyncAsync(s, svc);
            lib.CommitSelection(s.Id, first.Catalog.Channels, first.Catalog.Channels.Select(c => c.Key), first.Catalog.Channels.Select(c => c.Key));
            Assert.Equal(SyncOutcomeKind.UpToDate, (await lib.SyncAsync(s, svc)).Kind);
        });

        yield return TA("sync: een lege lijst van de provider wordt geweigerd en laat je lijst met rust", async () =>
        {
            using var tmp = new TempStore();
            var lib = new Library(tmp.Store);
            var s = NewSource(lib);
            var empty = false;
            var svc = ServiceFor(() => empty ? "#EXTM3U\n" : Samples.M3u(("a", "A", "G")));
            var first = await lib.SyncAsync(s, svc);
            lib.CommitSelection(s.Id, first.Catalog.Channels, first.Catalog.Channels.Select(c => c.Key), first.Catalog.Channels.Select(c => c.Key));

            empty = true;
            await Assert.ThrowsAsync<InvalidDataException>(() => lib.SyncAsync(s, svc));
            Assert.Equal(1, lib.Personal.Count, "lijst intact");
            Assert.False(lib.Personal[0].MissingAtSource, "niet als verdwenen gemarkeerd");
        });

        yield return TA("sync: verdwenen zender wordt gemarkeerd maar niet zomaar verwijderd", async () =>
        {
            using var tmp = new TempStore();
            var lib = new Library(tmp.Store);
            var s = NewSource(lib);
            var both = true;
            var svc = ServiceFor(() => both ? Samples.M3u(("a", "A", "G"), ("b", "B", "G")) : Samples.M3u(("a", "A", "G")));
            var first = await lib.SyncAsync(s, svc);
            lib.CommitSelection(s.Id, first.Catalog.Channels, first.Catalog.Channels.Select(c => c.Key), first.Catalog.Channels.Select(c => c.Key));

            both = false;
            var outcome = await lib.SyncAsync(s, svc);
            Assert.Equal(SyncOutcomeKind.ReviewNeeded, outcome.Kind);
            Assert.Equal(2, lib.Personal.Count, "nog steeds in lijst");
            Assert.True(lib.Personal.First(p => p.Name == "B").MissingAtSource, "gemarkeerd");

            both = true; // komt terug
            await lib.SyncAsync(s, svc);
            Assert.False(lib.Personal.First(p => p.Name == "B").MissingAtSource, "markering weer weg");
        });
    }
}
