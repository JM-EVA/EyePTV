using System.Globalization;

namespace IptvPlayer.Engine;

/// <summary>Een programmablok in het gidsrooster. Posities zijn in minuten vanaf het begin van het venster.</summary>
public sealed record GridBlock(Programme Programme, double StartMinutes, double Minutes, bool ClippedStart, bool ClippedEnd);

/// <summary>Een gevonden programma bij het zoeken in de gids.</summary>
public sealed record GuideHit(PersonalChannel Channel, Programme Programme);

/// <summary>De logica achter het gidsrooster en het zoeken in de gids (zonder scherm, dus testbaar).</summary>
public static class GuideGrid
{
    /// <summary>Het tijdvenster van het rooster: van het vroegste tot het laatste bekende programma (op hele uren), met grenzen.</summary>
    public static (DateTime Start, DateTime End) Window(IEnumerable<PersonalChannel> channels, EpgData epg, DateTime nowUtc,
        int maxBackDays = 14, int maxAheadDays = 7)
    {
        var floor = nowUtc.AddDays(-maxBackDays);
        var ceiling = nowUtc.AddDays(maxAheadDays);
        var start = nowUtc;
        var end = nowUtc;
        foreach (var c in channels)
        {
            var id = epg.Resolve(c);
            if (id is null || epg.Programmes[id].Count == 0) continue;
            var list = epg.Programmes[id]; // gesorteerd op start
            if (list[0].StartUtc < start) start = list[0].StartUtc;
            var last = list[^1].StopUtc;
            if (last > end) end = last;
        }

        start = Hour(start < floor ? floor : start, up: false);
        end = Hour(end > ceiling ? ceiling : end, up: true);
        if (end - start < TimeSpan.FromHours(6)) end = start.AddHours(6); // zonder gidsgegevens toch een bruikbaar venster
        return (start, end);
    }

    private static DateTime Hour(DateTime t, bool up)
    {
        var floored = new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Utc);
        return up && floored < t ? floored.AddHours(1) : floored;
    }

    /// <summary>
    /// De blokken van één zender binnen het venster. Programma's die over de rand heen lopen worden afgesneden;
    /// overlappende programma's in de gids botsen niet (het volgende begint waar het vorige stopt).
    /// </summary>
    public static List<GridBlock> Blocks(PersonalChannel channel, EpgData epg, DateTime start, DateTime end)
    {
        var result = new List<GridBlock>();
        var cursor = start;
        foreach (var p in epg.Schedule(channel, start, end))
        {
            var from = p.StartUtc < cursor ? cursor : p.StartUtc;
            var to = p.StopUtc > end ? end : p.StopUtc;
            if (to <= from) continue;
            result.Add(new GridBlock(p, (from - start).TotalMinutes, (to - from).TotalMinutes, p.StartUtc < start, p.StopUtc > end));
            cursor = to;
        }
        return result;
    }

    /// <summary>Zoekt in titel en omschrijving van alle programma's van deze zenders, zonder op hoofdletters of accenten te letten.</summary>
    public static List<GuideHit> Search(IEnumerable<PersonalChannel> channels, EpgData epg, string query, DateTime fromUtc, int limit = 200)
    {
        var q = query.Trim();
        var hits = new List<GuideHit>();
        if (q.Length == 0) return hits;
        foreach (var c in channels)
            foreach (var p in epg.Schedule(c, fromUtc, DateTime.MaxValue))
                if (TextUtil.ContainsLoose(p.Title, q) || TextUtil.ContainsLoose(p.Description, q))
                    hits.Add(new GuideHit(c, p));
        return hits.OrderBy(h => h.Programme.StartUtc).ThenBy(h => h.Channel.DisplayName, StringComparer.OrdinalIgnoreCase).Take(limit).ToList();
    }
}

/// <summary>Een herinnering voor een programma dat nog moet beginnen.</summary>
public sealed class Reminder
{
    public string ChannelId { get; set; } = "";
    public DateTime StartUtc { get; set; }
    public DateTime StopUtc { get; set; }
    public string Title { get; set; } = "";
    public string ChannelName { get; set; } = "";
    public int MinutesBefore { get; set; } = 5;

    /// <summary>De melding is al getoond (zodat hij niet nog eens komt, ook niet na een herstart).</summary>
    public bool Notified { get; set; }
}

/// <summary>Bewaart herinneringen (<c>reminders.json</c>) en meldt wanneer er een af moet gaan. <see cref="Tick"/> elke halve minuut aanroepen.</summary>
public sealed class ReminderManager
{
    private const string FileName = "reminders.json";
    private readonly AppStore _store;
    private readonly SettingsStore _settings;
    private readonly Func<DateTime> _utcNow;
    private readonly List<Reminder> _items;

    public ReminderManager(AppStore store, SettingsStore settings, Func<DateTime>? utcNow = null)
    {
        _store = store;
        _settings = settings;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _items = store.Load(FileName, () => new List<Reminder>());
    }

    public IReadOnlyList<Reminder> Items => _items.OrderBy(r => r.StartUtc).ToList();

    public event Action? Changed;

    /// <summary>Een herinnering moet nu getoond worden.</summary>
    public event Action<Reminder>? Due;

    public Reminder? Find(string channelId, DateTime startUtc) =>
        _items.FirstOrDefault(r => r.ChannelId == channelId && r.StartUtc == startUtc);

    public bool Has(string channelId, DateTime startUtc) => Find(channelId, startUtc) is not null;

    public void Add(PersonalChannel channel, Programme programme)
    {
        if (Has(channel.Id, programme.StartUtc) || programme.StopUtc <= _utcNow()) return;
        _items.Add(new Reminder
        {
            ChannelId = channel.Id, ChannelName = channel.DisplayName, StartUtc = programme.StartUtc, StopUtc = programme.StopUtc,
            Title = programme.Title, MinutesBefore = Math.Clamp(_settings.Current.ReminderMinutes, 0, 120)
        });
        Save();
    }

    public void Remove(string channelId, DateTime startUtc)
    {
        if (_items.RemoveAll(r => r.ChannelId == channelId && r.StartUtc == startUtc) > 0) Save();
    }

    /// <summary>Ruimt afgelopen herinneringen op en laat de herinneringen afgaan die aan de beurt zijn (ook na een herstart, zolang het programma loopt).</summary>
    public void Tick()
    {
        var now = _utcNow();
        var changed = _items.RemoveAll(r => r.StopUtc <= now) > 0;
        var due = _items.Where(r => !r.Notified && now >= r.StartUtc.AddMinutes(-r.MinutesBefore)).OrderBy(r => r.StartUtc).ToList();
        foreach (var r in due) r.Notified = true;
        if (changed || due.Count > 0) Save();
        foreach (var r in due) Due?.Invoke(r);
    }

    private void Save()
    {
        _store.Save(FileName, _items);
        Changed?.Invoke();
    }
}
