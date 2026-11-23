using System.IO.Compression;
using System.Net;
using System.Text;
using IptvPlayer.Engine;

namespace IptvPlayer.Tests;

public static class TestRegistry
{
    public static IEnumerable<(string, Func<Task>)> All() =>
        ParserTests.Tests()
            .Concat(LibraryTests.Tests())
            .Concat(ViewModelTests.Tests())
            .Concat(FlowTests.Tests())
            .Concat(MediaTests.Tests())
            .Concat(PlayerTests.Tests())
            .Concat(SubtitleTests.Tests())
            .Concat(SecurityTests.Tests())
            .Concat(RecordingTests.Tests())
            .Concat(HomeTests.Tests())
            .Concat(AdultTests.Tests())
            .Concat(DownloadTests.Tests())
            .Concat(ReconnectTests.Tests())
            .Concat(FailoverTests.Tests())
            .Concat(BrowseTests.Tests())
            .Concat(GuideGridTests.Tests())
            .Concat(AlternativesTests.Tests())
            .Concat(CastTests.Tests())
            .Concat(XamlChecks.Tests());
}

public static class ParserTests
{
    private static (string, Func<Task>) T(string name, Action a) => ("Parser: " + name, () => { a(); return Task.CompletedTask; });
    private static (string, Func<Task>) TA(string name, Func<Task> a) => ("Parser: " + name, a);

    public static IEnumerable<(string, Func<Task>)> Tests()
    {
        yield return T("M3U leest attributen, groep en EPG-url", () =>
        {
            var pl = M3uParser.Parse(Samples.M3u(("npo1.nl", "NPO 1", "Nederland"), ("bbc1.uk", "BBC One", "UK")));
            Assert.Equal(2, pl.Channels.Count, "aantal");
            Assert.Equal("NPO 1", pl.Channels[0].Name);
            Assert.Equal("Nederland", pl.Channels[0].Group);
            Assert.Equal("npo1.nl", pl.Channels[0].EpgId);
            Assert.Equal("http://logo.test/npo1.nl.png", pl.Channels[0].LogoUrl);
            Assert.Sequence(new[] { "http://epg.test/guide.xml" }, pl.EpgUrls);
        });

        yield return T("M3U: komma in groepsnaam breekt de zendernaam niet", () =>
        {
            var pl = M3uParser.Parse("#EXTM3U\n#EXTINF:-1 tvg-id=\"x\" group-title=\"Sport, Live\",Eurosport 1\nhttp://a/1\n");
            Assert.Equal("Eurosport 1", pl.Channels[0].Name);
            Assert.Equal("Sport, Live", pl.Channels[0].Group);
        });

        yield return T("M3U: #EXTGRP, BOM, CRLF en ontbrekende groep", () =>
        {
            var text = "﻿#EXTM3U\r\n#EXTINF:-1,Een\r\n#EXTGRP:Nieuws\r\nhttp://a/1\r\n#EXTINF:-1,Twee\r\nhttp://a/2\r\n";
            var pl = M3uParser.Parse(text);
            Assert.Equal("Nieuws", pl.Channels[0].Group);
            Assert.Equal(M3uParser.DefaultGroup, pl.Channels[1].Group);
            Assert.Equal("http://a/2", pl.Channels[1].Url);
        });

        yield return T("M3U: #EXTVLCOPT-regels en regels zonder EXTINF worden genegeerd", () =>
        {
            var pl = M3uParser.Parse("#EXTM3U\nhttp://los/url\n#EXTINF:-1,Echt\n#EXTVLCOPT:http-user-agent=x\nhttp://a/1\n");
            Assert.Equal(1, pl.Channels.Count);
            Assert.Equal("http://a/1", pl.Channels[0].Url);
        });

        yield return T("M3U: sleutels zijn uniek, ook bij dubbele tvg-id of naam", () =>
        {
            var pl = M3uParser.Parse(
                "#EXTM3U\n" +
                "#EXTINF:-1 tvg-id=\"npo1\",NPO 1 HD\nhttp://a/1\n" +
                "#EXTINF:-1 tvg-id=\"npo1\",NPO 1 FHD\nhttp://a/2\n" +
                "#EXTINF:-1,Zelfde\nhttp://a/3\n" +
                "#EXTINF:-1,Zelfde\nhttp://a/4\n");
            Assert.Equal(4, pl.Channels.Select(c => c.Key).Distinct().Count(), "unieke sleutels");
        });

        yield return T("M3U: sleutel blijft gelijk als alleen de URL verandert", () =>
        {
            var a = M3uParser.Parse("#EXTM3U\n#EXTINF:-1 tvg-id=\"x\",Zender\nhttp://a/token1\n").Channels[0];
            var b = M3uParser.Parse("#EXTM3U\n#EXTINF:-1 tvg-id=\"x\",Zender\nhttp://a/token2\n").Channels[0];
            Assert.Equal(a.Key, b.Key);
        });

        yield return T("M3U: zender zonder naam krijgt tvg-name of een standaardnaam", () =>
        {
            var pl = M3uParser.Parse("#EXTM3U\n#EXTINF:-1 tvg-name=\"Fallback\",\nhttp://a/1\n#EXTINF:-1,\nhttp://a/2\n");
            Assert.Equal("Fallback", pl.Channels[0].Name);
            Assert.Equal("Onbekende zender", pl.Channels[1].Name);
        });

        yield return T("M3uWriter schrijft een bruikbare lijst terug", () =>
        {
            var p = new PersonalChannel { Name = "NPO 1", Group = "NL", Url = "http://a/1", EpgId = "npo1", LogoUrl = "http://l/1.png" };
            var back = M3uParser.Parse(M3uWriter.Write(new[] { p })).Channels.Single();
            Assert.Equal("NPO 1", back.Name);
            Assert.Equal("NL", back.Group);
            Assert.Equal("http://a/1", back.Url);
            Assert.Equal("npo1", back.EpgId);
        });

        yield return T("Xtream: get.php-link wordt herkend", () =>
        {
            Assert.True(XtreamClient.TryParseUrl("http://srv.example:8080/get.php?username=jan&password=p%40ss&type=m3u_plus&output=ts", out var s, out var u, out var p));
            Assert.Equal("http://srv.example:8080", s);
            Assert.Equal("jan", u);
            Assert.Equal("p@ss", p);
        });

        yield return T("Xtream: gewone M3U-link wordt niet als Xtream gezien", () =>
        {
            Assert.False(XtreamClient.TryParseUrl("http://srv.example/list.m3u", out _, out _, out _));
            Assert.False(XtreamClient.TryParseUrl("geen url", out _, out _, out _));
            Assert.False(XtreamClient.TryParseUrl("http://srv/get.php?type=m3u", out _, out _, out _));
        });

        yield return T("Xtream: serveradres wordt genormaliseerd", () =>
        {
            Assert.Equal("http://srv:80", XtreamClient.NormalizeServer("srv:80/"));
            Assert.Equal("https://srv", XtreamClient.NormalizeServer(" https://srv/ "));
        });

        yield return TA("Xtream: zenders en categorieën worden ingelezen (getal én tekst als id)", async () =>
        {
            var h = new FakeHandler()
                .Add("action=get_live_categories", "[{\"category_id\":\"1\",\"category_name\":\"Nieuws\"},{\"category_id\":2,\"category_name\":\"Sport\"}]")
                .Add("action=get_live_streams",
                    "[{\"stream_id\":101,\"name\":\"NOS Journaal\",\"category_id\":\"1\",\"stream_icon\":\"http://i/1.png\",\"epg_channel_id\":\"nos.nl\"}," +
                    "{\"stream_id\":\"102\",\"name\":\"Sport 1\",\"category_id\":2,\"stream_icon\":\"\",\"epg_channel_id\":null}," +
                    "{\"stream_id\":103,\"name\":\"Zonder groep\",\"category_id\":null}]");
            var src = new IptvSource { Kind = SourceKind.Xtream, Server = "http://srv:8080", Username = "jan", Password = "pw" };
            var list = await new XtreamClient(new HttpClient(h)).GetLiveChannelsAsync(src);

            Assert.Equal(3, list.Count, "aantal");
            Assert.Equal("xt:101", list[0].Key);
            Assert.Equal("Nieuws", list[0].Group);
            Assert.Equal("nos.nl", list[0].EpgId);
            Assert.Equal("http://srv:8080/live/jan/pw/101.ts", list[0].Url);
            Assert.Equal("Sport", list[1].Group);
            Assert.Equal(null, list[1].LogoUrl);
            Assert.Equal(M3uParser.DefaultGroup, list[2].Group);
        });

        yield return TA("Xtream: inloggen controleert auth en status", async () =>
        {
            var src = new IptvSource { Kind = SourceKind.Xtream, Server = "http://srv", Username = "u", Password = "p" };

            var ok = new FakeHandler().Add("player_api.php", "{\"user_info\":{\"auth\":1,\"status\":\"Active\",\"exp_date\":\"1893456000\"}}");
            var r1 = await new XtreamClient(new HttpClient(ok)).AuthenticateAsync(src);
            Assert.True(r1.Ok, "auth ok");
            Assert.True(r1.ExpiresUtc is not null, "vervaldatum");

            var bad = new FakeHandler().Add("player_api.php", "{\"user_info\":{\"auth\":0}}");
            Assert.False((await new XtreamClient(new HttpClient(bad)).AuthenticateAsync(src)).Ok, "auth fout");

            var expired = new FakeHandler().Add("player_api.php", "{\"user_info\":{\"auth\":\"1\",\"status\":\"Expired\"}}");
            var r3 = await new XtreamClient(new HttpClient(expired)).AuthenticateAsync(src);
            Assert.False(r3.Ok, "verlopen");
            Assert.True(r3.Message!.Contains("Expired"), "bericht noemt status");
        });

        yield return TA("Xtream: HTTP-fout geeft duidelijke melding", async () =>
        {
            var h = new FakeHandler().Add("player_api.php", "nee", HttpStatusCode.Forbidden);
            var src = new IptvSource { Kind = SourceKind.Xtream, Server = "http://srv", Username = "u", Password = "p" };
            await Assert.ThrowsAsync<HttpRequestException>(() => new XtreamClient(new HttpClient(h)).GetLiveChannelsAsync(src));
        });

        yield return T("XMLTV: tijden met tijdzone en zonder", () =>
        {
            Assert.True(XmltvParser.TryParseTime("20261026203000 +0100", out var a));
            Assert.Equal(new DateTime(2026, 10, 26, 19, 30, 0, DateTimeKind.Utc), a);
            Assert.True(XmltvParser.TryParseTime("20261026203000 -0500", out var b));
            Assert.Equal(new DateTime(2026, 10, 27, 1, 30, 0, DateTimeKind.Utc), b);
            Assert.True(XmltvParser.TryParseTime("20261026203000", out var c));
            Assert.Equal(new DateTime(2026, 10, 26, 20, 30, 0, DateTimeKind.Utc), c);
            Assert.True(XmltvParser.TryParseTime("202610262030", out var d));
            Assert.Equal(new DateTime(2026, 10, 26, 20, 30, 0, DateTimeKind.Utc), d);
            Assert.False(XmltvParser.TryParseTime("kapot", out _));
            Assert.False(XmltvParser.TryParseTime(null, out _));
        });

        yield return T("XMLTV: filtert op kanaal-id en op zendernaam, binnen tijdvenster", () =>
        {
            var xml = """
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE tv SYSTEM "xmltv.dtd">
                <tv>
                  <channel id="npo1.nl"><display-name>NPO 1</display-name></channel>
                  <channel id="x.2"><display-name>Sport &amp; Co 2</display-name></channel>
                  <channel id="ander"><display-name>Niet gewenst</display-name></channel>
                  <programme start="20261026200000 +0000" stop="20261026210000 +0000" channel="npo1.nl"><title lang="nl">Journaal</title><desc>Nieuws</desc></programme>
                  <programme start="20261026210000 +0000" stop="20261026220000 +0000" channel="x.2"><title>Voetbal</title></programme>
                  <programme start="20261026200000 +0000" stop="20261026210000 +0000" channel="ander"><title>Nee</title></programme>
                  <programme start="20250101200000 +0000" stop="20250101210000 +0000" channel="npo1.nl"><title>Oud</title></programme>
                </tv>
                """;
            var data = XmltvParser.Parse(
                new MemoryStream(Encoding.UTF8.GetBytes(xml)),
                new HashSet<string> { "npo1.nl" },
                new HashSet<string> { TextUtil.NormalizeLoose("Sport & Co 2") },
                new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc));

            Assert.Sequence(new[] { "npo1.nl", "x.2" }, data.Programmes.Keys.OrderBy(k => k));
            Assert.Equal(1, data.Programmes["npo1.nl"].Count, "oud programma valt buiten venster");
            Assert.Equal("Journaal", data.Programmes["npo1.nl"][0].Title);
            Assert.Equal("Nieuws", data.Programmes["npo1.nl"][0].Description);
            Assert.Equal("x.2", data.Aliases[TextUtil.NormalizeLoose("Sport & Co 2")]);
        });

        yield return T("EpgData: nu/straks en koppeling op naam als tvg-id ontbreekt", () =>
        {
            var d = new EpgData();
            var now = new DateTime(2026, 10, 26, 20, 30, 0, DateTimeKind.Utc);
            d.Programmes["id1"] = new List<Programme>
            {
                new("id1", now.AddHours(-1), now.AddMinutes(-30), "Vorige", null),
                new("id1", now.AddMinutes(-30), now.AddMinutes(30), "Nu", "Beschrijving"),
                new("id1", now.AddMinutes(30), now.AddHours(1), "Straks", null)
            };
            d.Aliases[TextUtil.NormalizeLoose("Mijn Zender")] = "id1";

            var viaId = new PersonalChannel { Name = "x", EpgId = "ID1" };
            var viaName = new PersonalChannel { Name = "mijn  zender" };
            var none = new PersonalChannel { Name = "onbekend" };

            Assert.Equal("Nu", d.NowNext(viaId, now).Now!.Title);
            Assert.Equal("Straks", d.NowNext(viaId, now).Next!.Title);
            Assert.Equal("Nu", d.NowNext(viaName, now).Now!.Title);
            Assert.Equal(null, d.NowNext(none, now).Now);
            Assert.Equal(2, d.Schedule(viaId, now, now.AddHours(1)).Count, "schedule");
        });

        yield return TA("EpgService: downloadt (gzip) en bewaart alleen zenders uit de eigen lijst + cache werkt", async () =>
        {
            using var tmp = new TempStore();
            var start = DateTime.UtcNow.AddMinutes(-20);
            string F(DateTime t) => t.ToString("yyyyMMddHHmmss") + " +0000";
            var xml = $"<tv><channel id=\"a\"><display-name>A</display-name></channel>" +
                      $"<programme start=\"{F(start)}\" stop=\"{F(start.AddHours(1))}\" channel=\"a\"><title>Live</title></programme>" +
                      $"<programme start=\"{F(start)}\" stop=\"{F(start.AddHours(1))}\" channel=\"b\"><title>Onzin</title></programme></tv>";

            byte[] Gz()
            {
                using var ms = new MemoryStream();
                using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true))
                {
                    var bytes = Encoding.UTF8.GetBytes(xml);
                    gz.Write(bytes, 0, bytes.Length);
                }
                return ms.ToArray();
            }

            var handler = new FakeHandler().Add("guide.xml.gz", Gz);
            var source = new IptvSource { Name = "T", Kind = SourceKind.M3uUrl, M3uUrl = "http://x/l.m3u", EpgUrl = "http://epg.test/guide.xml.gz" };
            var personal = new List<PersonalChannel> { new() { SourceId = source.Id, Key = "k", Name = "A", EpgId = "a" } };

            var svc = new EpgService(new HttpClient(handler), tmp.Store);
            var errors = await svc.RefreshAsync(new[] { source }, personal);
            Assert.Equal(0, errors.Count, "fouten");
            Assert.Sequence(new[] { "a" }, svc.Data.Programmes.Keys);
            Assert.Equal("Live", svc.Data.NowNext(personal[0], DateTime.UtcNow).Now!.Title);

            var svc2 = new EpgService(new HttpClient(new FakeHandler()), tmp.Store);
            svc2.LoadCache();
            Assert.Equal("Live", svc2.Data.NowNext(personal[0], DateTime.UtcNow).Now!.Title, "uit cache");
        });

        yield return TA("EpgService: een kapotte EPG-bron geeft een foutmelding maar breekt niets", async () =>
        {
            using var tmp = new TempStore();
            var handler = new FakeHandler().Add("guide", "nee", HttpStatusCode.InternalServerError);
            var source = new IptvSource { Name = "Prov", EpgUrl = "http://epg.test/guide.xml" };
            var personal = new List<PersonalChannel> { new() { SourceId = source.Id, Key = "k", Name = "A" } };
            var errors = await new EpgService(new HttpClient(handler), tmp.Store).RefreshAsync(new[] { source }, personal);
            Assert.Equal(1, errors.Count);
            Assert.True(errors[0].StartsWith("Prov:"));
        });

        yield return T("HttpFactory: maakt een client met VLC-achtige User-Agent", () =>
        {
            using var http = HttpFactory.Create();
            Assert.True(http.DefaultRequestHeaders.UserAgent.ToString().StartsWith("VLC/"), "User-Agent: " + http.DefaultRequestHeaders.UserAgent);
        });

        yield return T("AppStore: beschadigd bestand wordt veilig gezet en vervangen door standaardwaarde", () =>
        {
            using var tmp = new TempStore();
            File.WriteAllText(tmp.Store.PathOf("x.json"), "{ dit is geen json");
            var v = tmp.Store.Load("x.json", () => new List<int> { 1 });
            Assert.Sequence(new[] { 1 }, v);
            Assert.True(Directory.GetFiles(tmp.Path, "x.json.corrupt-*").Length == 1, "back-up van beschadigd bestand");
        });
    }
}
