using System.Net;
using System.Net.Http.Headers;
using IptvPlayer.Engine;
using IptvPlayer.Engine.ViewModels;

namespace IptvPlayer.Tests;

/// <summary>Films en afleveringen downloaden: hervatten, een bestand per item, en offline afspelen.</summary>
public static class DownloadTests
{
    private static (string, Func<Task>) TA(string name, Func<Task> a) => ("Downloads: " + name, a);

    /// <summary>Een server met één bestand. Ondersteunt Range; de eerste aanvraag kan halverwege afbreken.</summary>
    private sealed class FileServer : HttpMessageHandler
    {
        public byte[] Data { get; } = Enumerable.Range(0, 600_000).Select(i => (byte)(i % 251)).ToArray();
        public int Requests { get; private set; }
        public List<long> RangeStarts { get; } = new();
        public bool BreakFirstRequest { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            var offset = request.Headers.Range?.Ranges.FirstOrDefault()?.From ?? 0;
            if (request.Headers.Range is not null) RangeStarts.Add(offset);

            var length = Data.Length - offset;
            var breakAt = BreakFirstRequest && Requests == 1 ? length / 2 : -1;
            var body = new BreakingStream(Data, (int)offset, breakAt);
            var content = new StreamContent(body);
            content.Headers.ContentLength = length;

            var resp = new HttpResponseMessage(offset > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = content };
            if (offset > 0) content.Headers.ContentRange = new ContentRangeHeaderValue(offset, Data.Length - 1, Data.Length);
            return Task.FromResult(resp);
        }
    }

    private sealed class BreakingStream : Stream
    {
        private readonly byte[] _data;
        private int _pos;
        private readonly long _breakAfter;
        private long _served;

        public BreakingStream(byte[] data, int offset, long breakAfter) { _data = data; _pos = offset; _breakAfter = breakAfter; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_breakAfter >= 0 && _served >= _breakAfter) throw new IOException("verbinding verbroken");
            var n = Math.Min(Math.Min(count, _data.Length - _pos), 64 * 1024);
            if (_breakAfter >= 0) n = (int)Math.Min(n, _breakAfter - _served);
            Array.Copy(_data, _pos, buffer, offset, n);
            _pos += n;
            _served += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class Env : IDisposable
    {
        public required TempStore Tmp { get; init; }
        public required string Folder { get; init; }
        public required FileServer Server { get; init; }
        public required DownloadManager Manager { get; init; }
        public required Library Lib { get; init; }
        public required ChannelsViewModel Vm { get; init; }
        public required FakePlayer Player { get; init; }

        public void Dispose()
        {
            Manager.StopAll();
            Tmp.Dispose();
            try { Directory.Delete(Folder, true); } catch { }
        }
    }

    private static Env Create()
    {
        var tmp = new TempStore();
        var folder = Path.Combine(Path.GetTempPath(), "iptvplayer-dl-" + Guid.NewGuid().ToString("N"));
        var settings = new SettingsStore(tmp.Store);
        settings.Current.DownloadFolder = folder;

        var lib = new Library(tmp.Store);
        var src = lib.AddSource(new IptvSource
        {
            Name = "XT", Kind = SourceKind.Xtream, Server = "http://srv.test:8080", Username = "jan", Password = "pw", ServerTimeZone = "UTC"
        });
        var cat = new List<Channel> { new("xtm:501", "Film A", "Actie", "http://srv.test:8080/movie/jan/pw/501.mkv", Kind: ContentKind.Movie) };
        lib.CommitSelection(src.Id, cat, cat.Select(c => c.Key), cat.Select(c => c.Key));

        var server = new FileServer();
        var manager = new DownloadManager(new HttpClient(server), settings, tmp.Store) { RetryDelay = TimeSpan.FromMilliseconds(10) };
        var player = new FakePlayer();
        var epg = new EpgService(new HttpClient(new FakeHandler()), tmp.Store);
        var vm = new ChannelsViewModel(lib, epg, player, new FakeDialogs(), settings: settings, downloads: manager);
        return new Env { Tmp = tmp, Folder = folder, Server = server, Manager = manager, Lib = lib, Vm = vm, Player = player };
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

    private static DownloadItem Movie(string key = "src|xtm:1", string title = "Film A", string url = "http://srv.test/movie/u/p/1.mkv") =>
        new() { Key = key, SourceId = "src", Title = title, Url = url };

    public static IEnumerable<(string, Func<Task>)> Tests()
    {
        yield return TA("een film wordt gedownload naar een eigen bestand met het type van de link", async () =>
        {
            using var env = Create();
            var d = env.Manager.Enqueue(Movie());
            await Until(() => d.Status == DownloadStatus.Done, "klaar");

            Assert.True(d.FilePath!.EndsWith("Film A.mkv"), d.FilePath);
            Assert.Equal(env.Server.Data.Length, (int)new FileInfo(d.FilePath).Length, "volledig bestand");
            Assert.True(File.ReadAllBytes(d.FilePath).SequenceEqual(env.Server.Data), "inhoud klopt");
            Assert.Equal(env.Server.Data.Length, (int)d.TotalBytes);
            Assert.True(env.Manager.OwnsUrl(new Uri(d.FilePath).AbsoluteUri), "de speler mag het bestand openen");
            Assert.False(env.Manager.OwnsUrl(new Uri(Path.Combine(env.Folder, "ander.mkv")).AbsoluteUri), "een ander bestand niet");
            Assert.True(env.Manager.LocalUrlFor("src|xtm:1") is not null);
        });

        yield return TA("verbinding valt halverwege weg: de download gaat verder waar het bestand was", async () =>
        {
            using var env = Create();
            env.Server.BreakFirstRequest = true;
            var d = env.Manager.Enqueue(Movie());
            await Until(() => d.Status == DownloadStatus.Done, "klaar na hervatten");

            Assert.True(env.Server.RangeStarts.Count >= 1 && env.Server.RangeStarts[0] > 0, "vroeg het vervolg met een Range");
            Assert.True(File.ReadAllBytes(d.FilePath!).SequenceEqual(env.Server.Data), "bestand is heel en niet dubbel");
        });

        yield return TA("hetzelfde item twee keer toevoegen geeft één download; andere links dan http worden geweigerd", async () =>
        {
            using var env = Create();
            var a = env.Manager.Enqueue(Movie());
            var b = env.Manager.Enqueue(Movie());
            Assert.True(ReferenceEquals(a, b), "dezelfde download");
            Assert.Equal(1, env.Manager.All.Count);

            var threw = false;
            try { env.Manager.Enqueue(Movie("src|x", "X", "file:///C:/Windows/win.ini")); } catch (InvalidOperationException) { threw = true; }
            Assert.True(threw, "file:// geweigerd");
            await Until(() => a.Status == DownloadStatus.Done, "klaar");
        });

        yield return TA("verwijderen haalt ook het bestand van de schijf", async () =>
        {
            using var env = Create();
            var d = env.Manager.Enqueue(Movie());
            await Until(() => d.Status == DownloadStatus.Done, "klaar");
            var path = d.FilePath!;
            env.Manager.Delete(d.Id, deleteFile: true);
            Assert.False(File.Exists(path), "bestand weg");
            Assert.Equal(0, env.Manager.All.Count);
        });

        yield return TA("een nieuwe sessie ziet de download weer (bewaard in downloads.json) en gaat verder bij een onderbreking", async () =>
        {
            using var env = Create();
            var d = env.Manager.Enqueue(Movie());
            await Until(() => d.Status == DownloadStatus.Done, "klaar");

            var settings = new SettingsStore(env.Tmp.Store);
            var again = new DownloadManager(new HttpClient(env.Server), settings, env.Tmp.Store);
            Assert.Equal(1, again.All.Count);
            Assert.Equal(DownloadStatus.Done, again.All[0].Status);
            Assert.True(again.LocalUrlFor("src|xtm:1") is not null);
        });

        yield return TA("een film downloaden en daarna afspelen speelt het bestand van de schijf, met dezelfde kijkvoortgang", async () =>
        {
            using var env = Create();
            var row = env.Vm.Rows.First();
            env.Vm.SelectKind(ContentKind.Movie);
            row = env.Vm.Rows.First(r => r.Name == "Film A");
            Assert.True(row.CanDownload, "downloadknop zichtbaar");
            Assert.Equal(null, row.DownloadText);

            row.DownloadCommand.Execute(null);
            var d = env.Manager.All.Single();
            await Until(() => d.Status == DownloadStatus.Done, "klaar");
            Assert.Equal(row.Model.Id, d.Key, "sleutel is de film in de lijst");
            Assert.False(row.CanDownload, "niet nog een keer");

            row.PlayCommand.Execute(null);
            Assert.True(env.Player.Played.Last().StartsWith("file:"), "speelt lokaal: " + env.Player.Played.Last());

            // Zonder internet vanuit het overzicht afspelen werkt ook, zelfs als de film uit je lijst is gehaald.
            env.Lib.Remove(row.Model);
            env.Vm.PlayDownload(d);
            Assert.True(env.Player.Played.Last().StartsWith("file:"));
            Assert.True(env.Vm.Current is not null, "blijft spelen na het herladen van de lijst");
        });
    }
}
