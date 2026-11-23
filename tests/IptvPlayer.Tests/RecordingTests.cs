using System.Net;
using IptvPlayer.Engine;

namespace IptvPlayer.Tests;

/// <summary>Opnemen: plannen, starten, stoppen, uitloop, EPG volgen, herstarten en foutafhandeling.</summary>
public static class RecordingTests
{
    private static (string, Func<Task>) T(string name, Action a) => ("Opnemen: " + name, () => { a(); return Task.CompletedTask; });
    private static (string, Func<Task>) TA(string name, Func<Task> a) => ("Opnemen: " + name, a);

    /// <summary>Nep-zender: geeft bij elke aanvraag een stukje "uitzending" en sluit daarna de verbinding (zodat opnieuw verbinden wordt getest).</summary>
    private sealed class StreamHandler : HttpMessageHandler
    {
        public int Requests;
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public int BytesPerRequest { get; set; } = 200_000;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            if (Status != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(Status));
            var data = new byte[BytesPerRequest];
            Array.Fill(data, (byte)0x47);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
        }
    }

    private sealed class Env : IDisposable
    {
        public required TempStore Tmp { get; init; }
        public required Library Lib { get; init; }
        public required EpgService Epg { get; init; }
        public required SettingsStore Settings { get; init; }
        public required StreamHandler Server { get; init; }
        public required PersonalChannel Nos { get; init; }
        public required Func<RecordingManager> NewManager { get; init; }
        public required RecordingManager Rec { get; set; }
        public DateTime Now { get; set; }
        public void Dispose() { Rec.StopAll(); Tmp.Dispose(); }
    }

    private static Env Create()
    {
        var tmp = new TempStore();
        var lib = new Library(tmp.Store);
        var src = lib.AddSource(new IptvSource { Name = "XT", Kind = SourceKind.Xtream, Server = "http://srv.test", Username = "jan", Password = "pw" });
        var cat = new List<Channel>
        {
            new("xt:1", "NOS", "Nieuws", "http://srv.test/live/jan/pw/1.ts", EpgId: "nos", RemoteId: "1"),
            new("xtm:5", "Film", "Actie", "http://srv.test/movie/jan/pw/5.mkv", Kind: ContentKind.Movie),
            new("m:1", "Camera", "Live", "rtsp://cam/1"),
        };
        lib.CommitSelection(src.Id, cat, cat.Select(c => c.Key), cat.Select(c => c.Key));

        var epg = new EpgService(new HttpClient(new FakeHandler()), tmp.Store);
        var settings = new SettingsStore(tmp.Store);
        settings.Current.RecordingFolder = Path.Combine(tmp.Store.Root, "opnames");
        var server = new StreamHandler();
        var now = new DateTime(2026, 10, 26, 20, 0, 0, DateTimeKind.Utc);

        Env? env = null;
        RecordingManager Make() => new(lib, epg, new HttpClient(server), settings, tmp.Store, () => env!.Now) { ReconnectDelay = TimeSpan.FromMilliseconds(10) };
        env = new Env
        {
            Tmp = tmp, Lib = lib, Epg = epg, Settings = settings, Server = server, Now = now,
            Nos = lib.Personal.First(p => p.Name == "NOS"), NewManager = Make, Rec = null!
        };
        env.Rec = Make();
        return env;
    }

    private sealed class FakeConverter : IRecordingConverter
    {
        public bool Succeed { get; set; } = true;

        public async Task<bool> ConvertAsync(string sourcePath, string targetPath, CancellationToken ct)
        {
            if (!Succeed)
            {
                await File.WriteAllBytesAsync(targetPath, new byte[10], ct); // een half bestand
                return false;
            }
            File.Copy(sourcePath, targetPath);
            return true;
        }
    }

    private static async Task Until(Func<bool> condition, string what, int timeoutMs = 8000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TestFailure("Time-out: " + what);
            await Task.Delay(15);
        }
    }

    private static Programme Prog(Env env, double startMinutes, double stopMinutes, string title = "Journaal") =>
        new("nos", env.Now.AddMinutes(startMinutes), env.Now.AddMinutes(stopMinutes), title, null);

    public static IEnumerable<(string, Func<Task>)> Tests()
    {
        yield return TA("nu opnemen tot je stopt: bestand wordt geschreven, stoppen bewaart het", async () =>
        {
            using var env = Create();
            var r = env.Rec.RecordNow(env.Nos);
            Assert.Equal(RecordingMode.UntilStopped, r.Mode);
            Assert.True(r.EndUtc is null, "geen eindtijd");

            await Until(() => r.Bytes > 300_000, "er komt data binnen (en er wordt opnieuw verbonden)");
            Assert.True(env.Server.Requests >= 2, "opnieuw verbonden na het sluiten van de verbinding");
            Assert.Equal(RecordingStatus.Recording, r.Status);
            Assert.Equal(1, env.Rec.ActiveCount);

            env.Rec.Stop(r.Id);
            await Until(() => r.Status == RecordingStatus.Done, "gestopt");
            Assert.True(File.Exists(r.FilePath), "bestand bestaat");
            Assert.True(r.FilePath!.EndsWith(".ts") && Path.GetFileName(r.FilePath).StartsWith("NOS - "), r.FilePath);
            Assert.True(new FileInfo(r.FilePath).Length >= r.Bytes - 1, "alles wat binnenkwam staat in het bestand");
            Assert.Equal(0, env.Rec.ActiveCount);
        });

        yield return TA("opname wordt na afloop als .mp4 bewaard en de .ts verdwijnt", async () =>
        {
            using var env = Create();
            env.Rec.Converter = new FakeConverter();
            var r = env.Rec.RecordNow(env.Nos);
            await Until(() => r.Bytes > 1000, "data");
            var ts = r.FilePath!;
            env.Rec.Stop(r.Id);
            await Until(() => r.FilePath!.EndsWith(".mp4"), "omgezet naar mp4");
            Assert.True(File.Exists(r.FilePath), "mp4 bestaat");
            Assert.False(File.Exists(ts), ".ts is opgeruimd");
            Assert.Equal(RecordingStatus.Done, r.Status);
            Assert.True(env.Rec.OwnsUrl(new Uri(r.FilePath!).AbsoluteUri), "de speler mag de mp4 openen");
        });

        yield return TA("mislukte omzetting laat de .ts gewoon staan", async () =>
        {
            using var env = Create();
            env.Rec.Converter = new FakeConverter { Succeed = false };
            var r = env.Rec.RecordNow(env.Nos);
            await Until(() => r.Bytes > 1000, "data");
            env.Rec.Stop(r.Id);
            await Until(() => r.Status == RecordingStatus.Done, "klaar");
            await Task.Delay(200);
            Assert.True(r.FilePath!.EndsWith(".ts") && File.Exists(r.FilePath), "ts blijft");
            Assert.False(File.Exists(Path.ChangeExtension(r.FilePath, ".mp4")), "geen half mp4-bestand");
        });

        yield return TA("nu opnemen tot een eindtijd: stopt zodra de tijd om is", async () =>
        {
            using var env = Create();
            var r = env.Rec.RecordNow(env.Nos, env.Now.AddMinutes(30));
            Assert.Equal(RecordingMode.UntilTime, r.Mode);
            await Until(() => r.Bytes > 1000, "data");

            env.Now = env.Now.AddMinutes(31);
            env.Rec.Tick();
            await Until(() => r.Status == RecordingStatus.Done, "klaar na de eindtijd");
        });

        yield return T("eindtijd in het verleden en dubbel opnemen worden geweigerd", () =>
        {
            using var env = Create();
            Assert.True(Throws(() => env.Rec.RecordNow(env.Nos, env.Now.AddMinutes(-1))), "verleden");
            env.Rec.RecordNow(env.Nos);
            Assert.True(Throws(() => env.Rec.RecordNow(env.Nos)), "tweede opname van dezelfde zender");
        });

        yield return T("programma plannen: begint 2 minuten eerder, eindigt 5 minuten later, en wacht tot het zover is", () =>
        {
            using var env = Create();
            var p = Prog(env, 60, 120); // over een uur, een uur lang
            var r = env.Rec.RecordProgramme(env.Nos, p);
            Assert.Equal(RecordingMode.Programme, r.Mode);
            Assert.Equal(RecordingStatus.Scheduled, r.Status);
            Assert.Equal(p.StartUtc.AddMinutes(-2), r.StartUtc, "iets eerder");
            Assert.Equal(p.StopUtc.AddMinutes(5), r.EndUtc!.Value, "wat langer");
            Assert.Equal(0, env.Server.Requests, "nog niets gestart");
            Assert.True(ReferenceEquals(r, env.Rec.ForProgramme(env.Nos, p)), "herkenbaar in de gids");
            Assert.True(ReferenceEquals(r, env.Rec.RecordProgramme(env.Nos, p)), "twee keer plannen geeft dezelfde opname");
            Assert.Equal(1, env.Rec.All.Count);
        });

        yield return T("de marges komen uit de instellingen", () =>
        {
            using var env = Create();
            env.Settings.Current.RecordBeforeMinutes = 10;
            env.Settings.Current.RecordAfterMinutes = 20;
            var p = Prog(env, 60, 90);
            var r = env.Rec.RecordProgramme(env.Nos, p);
            Assert.Equal(p.StartUtc.AddMinutes(-10), r.StartUtc);
            Assert.Equal(p.StopUtc.AddMinutes(20), r.EndUtc!.Value);
        });

        yield return TA("een geplande opname start op tijd en stopt na de uitloop", async () =>
        {
            using var env = Create();
            var p = Prog(env, 60, 90);
            var r = env.Rec.RecordProgramme(env.Nos, p);

            env.Now = env.Now.AddMinutes(59); // 1 minuut vóór het programma: de opname begon al 2 minuten eerder
            env.Rec.Tick();
            Assert.Equal(RecordingStatus.Recording, r.Status);
            await Until(() => r.Bytes > 1000, "data");

            env.Now = p.StopUtc.AddMinutes(4); // nog binnen de uitloop
            env.Rec.Tick();
            Assert.Equal(RecordingStatus.Recording, r.Status, "loopt nog door");

            env.Now = p.StopUtc.AddMinutes(6);
            env.Rec.Tick();
            await Until(() => r.Status == RecordingStatus.Done, "klaar");
        });

        yield return T("een programma dat al bezig is begint meteen; een afgelopen programma kan niet", () =>
        {
            using var env = Create();
            var running = env.Rec.RecordProgramme(env.Nos, Prog(env, -10, 20));
            Assert.Equal(RecordingStatus.Recording, running.Status, "meteen gestart");
            Assert.True(Throws(() => env.Rec.RecordProgramme(env.Nos, Prog(env, -120, -60, "Oud"))), "afgelopen");
        });

        yield return T("de opname volgt de EPG als het programma uitloopt", () =>
        {
            using var env = Create();
            var p = Prog(env, 60, 90);
            env.Epg.Data.Programmes["nos"] = new List<Programme> { p };
            var r = env.Rec.RecordProgramme(env.Nos, p);
            Assert.Equal(p.StopUtc.AddMinutes(5), r.EndUtc!.Value);

            // De EPG meldt later een nieuwe eindtijd (live sport die uitloopt).
            env.Epg.Data.Programmes["nos"] = new List<Programme> { p with { StopUtc = p.StopUtc.AddMinutes(40) } };
            env.Rec.Tick();
            Assert.Equal(p.StopUtc.AddMinutes(45), r.EndUtc!.Value, "eindtijd + 40 min uitloop + 5 min marge");
            Assert.Equal(p.StopUtc.AddMinutes(40), r.ProgrammeStopUtc!.Value);
        });

        yield return T("geplande opname annuleren", () =>
        {
            using var env = Create();
            var r = env.Rec.RecordProgramme(env.Nos, Prog(env, 60, 90));
            env.Rec.Stop(r.Id);
            Assert.Equal(RecordingStatus.Cancelled, r.Status);
            Assert.True(env.Rec.ForProgramme(env.Nos, Prog(env, 60, 90)) is null, "niet meer gepland");
            Assert.Equal(0, env.Server.Requests);
        });

        yield return T("planning blijft bewaard na herstarten; een gemist moment wordt als gemist gemeld", () =>
        {
            using var env = Create();
            var p1 = Prog(env, 60, 90, "Straks");
            var p2 = Prog(env, 120, 150, "Later");
            var r1 = env.Rec.RecordProgramme(env.Nos, p1);
            var r2 = env.Rec.RecordProgramme(env.Nos, p2);

            env.Now = env.Now.AddMinutes(100); // p1 is voorbij terwijl de app uit stond
            var again = env.NewManager();
            Assert.Equal(2, again.All.Count);
            again.Tick();
            Assert.Equal(RecordingStatus.Missed, again.Find(r1.Id)!.Status);
            Assert.Equal(RecordingStatus.Scheduled, again.Find(r2.Id)!.Status);
            Assert.True(again.Find(r1.Id)!.Error!.Contains("niet open"), again.Find(r1.Id)!.Error);
        });

        yield return TA("een opname die door een crash werd onderbroken wordt bij de volgende start netjes afgesloten of hervat", async () =>
        {
            using var env = Create();
            var open = env.Rec.RecordNow(env.Nos);                      // zonder eindtijd
            await Until(() => open.Status == RecordingStatus.Recording, "gestart");

            // "Crash": een tweede manager leest wat er op schijf staat terwijl de eerste nog niet netjes is gestopt.
            var after = env.NewManager();
            Assert.True(after.Find(open.Id)!.Status is RecordingStatus.Done or RecordingStatus.Failed, "zonder eindtijd: afgesloten");

            env.Rec.Stop(open.Id);
            await Until(() => open.Status == RecordingStatus.Done, "gestopt");

            var timed = env.Rec.RecordNow(env.Lib.Personal.First(p => p.Name == "NOS"), env.Now.AddMinutes(30));
            await Until(() => timed.Status == RecordingStatus.Recording, "tweede opname gestart");
            var again = env.NewManager();
            Assert.Equal(RecordingStatus.Scheduled, again.Find(timed.Id)!.Status, "eindtijd ligt nog voor ons: opnieuw inplannen");
        });

        yield return TA("een zender die niet antwoordt geeft een duidelijke fout na een aantal pogingen", async () =>
        {
            using var env = Create();
            env.Server.Status = HttpStatusCode.Forbidden;
            var r = env.Rec.RecordNow(env.Nos);
            await Until(() => r.Status == RecordingStatus.Failed, "mislukt", 15000);
            Assert.True(r.Error!.Contains("verbinding"), r.Error);
            Assert.True(env.Server.Requests >= 8, "meerdere pogingen: " + env.Server.Requests);
        });

        yield return T("alleen zenders met een http(s)-stream kunnen worden opgenomen", () =>
        {
            using var env = Create();
            Assert.True(Throws(() => env.Rec.RecordNow(env.Lib.Personal.First(p => p.Name == "Film"))), "film");
            Assert.True(Throws(() => env.Rec.RecordNow(env.Lib.Personal.First(p => p.Name == "Camera"))), "rtsp");
        });

        yield return TA("verwijderen met bestand, en de speler mag alleen eigen opnames openen", async () =>
        {
            using var env = Create();
            var r = env.Rec.RecordNow(env.Nos);
            await Until(() => r.Bytes > 1000, "data");
            env.Rec.Stop(r.Id);
            await Until(() => r.Status == RecordingStatus.Done, "klaar");

            var uri = new Uri(r.FilePath!).AbsoluteUri;
            Assert.True(env.Rec.OwnsUrl(uri), "eigen opname");
            Assert.False(env.Rec.OwnsUrl(new Uri(Path.Combine(env.Tmp.Store.Root, "sources.json")).AbsoluteUri), "een ander bestand");
            Assert.False(env.Rec.OwnsUrl("file:///C:/Windows/win.ini"));
            Assert.False(env.Rec.OwnsUrl("http://x/y.ts"));

            env.Rec.Delete(r.Id, deleteFile: true);
            Assert.False(File.Exists(r.FilePath), "bestand weg");
            Assert.Equal(0, env.Rec.All.Count);
        });

        yield return TA("bestandsnamen: rare tekens in titel of zender kunnen niet uit de map ontsnappen", async () =>
        {
            using var env = Create();
            var p = new Programme("nos", env.Now.AddMinutes(-1), env.Now.AddMinutes(30), @"..\..\Wind*ows: ""film""?", null);
            var r = env.Rec.RecordProgramme(env.Nos, p);
            await Until(() => r.FilePath is not null && r.Bytes > 1000, "bestand");
            var folder = Path.GetFullPath(env.Settings.Current.RecordingFolder) + Path.DirectorySeparatorChar;
            Assert.True(Path.GetFullPath(r.FilePath!).StartsWith(folder, StringComparison.OrdinalIgnoreCase), r.FilePath);
            Assert.False(Path.GetFileName(r.FilePath!).IndexOfAny(Path.GetInvalidFileNameChars()) >= 0);
        });

        yield return TA("echte HTTP-stream: een langlopende uitzending zonder einde wordt opgenomen en bij stoppen netjes afgesloten", async () =>
        {
            // Een lokale server die blijft sturen (zoals een live-zender), zonder Content-Length.
            using var listener = new HttpListener();
            var port = 20000 + Random.Shared.Next(20000);
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            var cts = new CancellationTokenSource();
            var served = 0L;
            _ = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await listener.GetContextAsync(); } catch { break; }
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            ctx.Response.SendChunked = true;
                            ctx.Response.ContentType = "video/mp2t";
                            var chunk = new byte[32 * 1024];
                            Array.Fill(chunk, (byte)0x47);
                            while (!cts.IsCancellationRequested)
                            {
                                await ctx.Response.OutputStream.WriteAsync(chunk);
                                Interlocked.Add(ref served, chunk.Length);
                                await Task.Delay(10);
                            }
                        }
                        catch { /* de opname sloot de verbinding */ }
                        finally { try { ctx.Response.Abort(); } catch { } }
                    });
                }
            });

            using var tmp = new TempStore();
            var lib = new Library(tmp.Store);
            var src = lib.AddSource(new IptvSource { Name = "L", Kind = SourceKind.M3uUrl, M3uUrl = "http://x/y.m3u" });
            var cat = new List<Channel> { new("a", "Lokaal", "G", $"http://127.0.0.1:{port}/live.ts") };
            lib.CommitSelection(src.Id, cat, cat.Select(c => c.Key), cat.Select(c => c.Key));
            var settings = new SettingsStore(tmp.Store);
            settings.Current.RecordingFolder = Path.Combine(tmp.Store.Root, "opnames");
            var rec = new RecordingManager(lib, new EpgService(new HttpClient(new FakeHandler()), tmp.Store), new HttpClient(), settings, tmp.Store);

            var r = rec.RecordNow(lib.Personal.Single());
            await Until(() => r.Bytes > 400_000, "er stroomt data binnen");
            Assert.Equal(RecordingStatus.Recording, r.Status);

            rec.Stop(r.Id);
            await Until(() => r.Status == RecordingStatus.Done, "gestopt");
            cts.Cancel();
            listener.Stop();

            Assert.True(File.Exists(r.FilePath));
            var size = new FileInfo(r.FilePath!).Length;
            Assert.True(size >= 400_000 && size == r.Bytes, $"bestand {size} B, geteld {r.Bytes} B");
            Assert.True(File.ReadAllBytes(r.FilePath!).All(b => b == 0x47), "inhoud ongewijzigd");
        });

        yield return T("opnamegegevens (met inlog in de link) staan versleuteld op schijf", () =>
        {
            using var tmp = new TempStore();
            var store = new AppStore(tmp.Store.Root, FileCipher.Open(tmp.Store.Root, new XorProtector()));
            var lib = new Library(store);
            var src = lib.AddSource(new IptvSource { Name = "XT", Kind = SourceKind.Xtream, Server = "http://srv.test", Username = "jan", Password = "pw" });
            var cat = new List<Channel> { new("xt:1", "NOS", "N", "http://srv.test/live/jan/GEHEIMWACHTWOORD/1.ts", RemoteId: "1") };
            lib.CommitSelection(src.Id, cat, cat.Select(c => c.Key), cat.Select(c => c.Key));
            var settings = new SettingsStore(store);
            settings.Current.RecordingFolder = Path.Combine(tmp.Store.Root, "o");
            var rec = new RecordingManager(lib, new EpgService(new HttpClient(new FakeHandler()), store), new HttpClient(new StreamHandler()), settings, store,
                () => new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
            rec.RecordProgramme(lib.Personal.Single(), new Programme("x", new DateTime(2026, 1, 1, 14, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 1, 15, 0, 0, DateTimeKind.Utc), "T", null));

            var raw = File.ReadAllBytes(store.PathOf("recordings.json"));
            Assert.True(FileCipher.IsEncrypted(raw), "versleuteld");
            Assert.False(System.Text.Encoding.UTF8.GetString(raw).Contains("GEHEIMWACHTWOORD"));
        });
    }

    private sealed class XorProtector : IKeyProtector
    {
        public byte[] Protect(byte[] data) => data.Select(b => (byte)(b ^ 0x5A)).ToArray();
        public byte[] Unprotect(byte[] data) => Protect(data);
    }

    private static bool Throws(Action a)
    {
        try { a(); return false; }
        catch (InvalidOperationException) { return true; }
    }
}
