namespace IptvPlayer.Engine;

/// <summary>Onthoudt hoever je bent met films en afleveringen, zodat je verder kunt kijken waar je was.</summary>
public sealed class WatchStore
{
    private const string FileName = "watch.json";
    private const long MinResumeMs = 30_000;   // eerste halve minuut telt niet als "begonnen"
    private const double FinishedFraction = 0.95;
    private const int MaxEntries = 500;

    private readonly AppStore _store;
    private readonly Dictionary<string, Entry> _entries;

    public sealed class Entry
    {
        public long PositionMs { get; set; }
        public long LengthMs { get; set; }
        public bool Finished { get; set; }
        public DateTime UpdatedUtc { get; set; }

        // Alleen bij afleveringen: bij welke serie ze horen (nodig voor "verder kijken" op het beginscherm).
        public string? SeriesId { get; set; }
        public string? EpisodeId { get; set; }
        public int Season { get; set; }
        public int Number { get; set; }
        public string? EpisodeTitle { get; set; }
    }

    /// <summary>Gegevens van een aflevering die mee worden bewaard.</summary>
    public sealed record EpisodeMeta(string SeriesId, string EpisodeId, int Season, int Number, string EpisodeTitle);

    /// <summary>Alles wat je half hebt bekeken, het laatst bekeken eerst.</summary>
    public IReadOnlyList<(string Key, Entry Entry)> InProgress() =>
        _entries.Where(kv => !kv.Value.Finished && kv.Value.PositionMs >= MinResumeMs && kv.Value.LengthMs > 0)
            .OrderByDescending(kv => kv.Value.UpdatedUtc)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();

    public WatchStore(AppStore store)
    {
        _store = store;
        _entries = _store.Load(FileName, () => new Dictionary<string, Entry>());
    }

    /// <summary>Het punt waar je verder kunt kijken, of 0 als je opnieuw begint.</summary>
    public long GetResumePoint(string key) =>
        _entries.TryGetValue(key, out var e) && !e.Finished && e.PositionMs >= MinResumeMs ? e.PositionMs : 0;

    public bool IsFinished(string key) => _entries.TryGetValue(key, out var e) && e.Finished;

    public void Save(string key, long positionMs, long lengthMs, DateTime nowUtc, EpisodeMeta? meta = null)
    {
        if (lengthMs <= 0 || positionMs < 0) return;

        if (positionMs >= lengthMs * FinishedFraction)
        {
            _entries[key] = WithMeta(new Entry { PositionMs = 0, LengthMs = lengthMs, Finished = true, UpdatedUtc = nowUtc }, meta);
        }
        else if (positionMs >= MinResumeMs)
        {
            _entries[key] = WithMeta(new Entry { PositionMs = positionMs, LengthMs = lengthMs, Finished = false, UpdatedUtc = nowUtc }, meta);
        }
        else
        {
            // Net begonnen of opnieuw gestart: een eerdere "bekeken"-markering blijft staan, een half-bekeken stand vervalt.
            if (_entries.TryGetValue(key, out var e) && !e.Finished) _entries.Remove(key);
            else return;
        }

        if (_entries.Count > MaxEntries)
            foreach (var old in _entries.OrderBy(kv => kv.Value.UpdatedUtc).Take(_entries.Count - MaxEntries).Select(kv => kv.Key).ToList())
                _entries.Remove(old);

        _store.Save(FileName, _entries, indented: false);
    }

    /// <summary>Haalt een film of opname uit "verder kijken" (de plek waar je was wordt vergeten).</summary>
    public void ForgetInProgress(string key)
    {
        if (_entries.TryGetValue(key, out var e) && !e.Finished)
        {
            _entries.Remove(key);
            _store.Save(FileName, _entries, indented: false);
        }
    }

    /// <summary>Haalt alle half bekeken afleveringen van een serie uit "verder kijken".</summary>
    public void ForgetSeriesInProgress(string seriesId)
    {
        var keys = _entries.Where(kv => kv.Value.SeriesId == seriesId && !kv.Value.Finished).Select(kv => kv.Key).ToList();
        if (keys.Count == 0) return;
        foreach (var k in keys) _entries.Remove(k);
        _store.Save(FileName, _entries, indented: false);
    }

    /// <summary>Markeert een half bekeken film of opname als bekeken.</summary>
    public void MarkFinished(string key, DateTime nowUtc)
    {
        if (!_entries.TryGetValue(key, out var e) || e.Finished) return;
        e.Finished = true;
        e.PositionMs = 0;
        e.UpdatedUtc = nowUtc;
        _store.Save(FileName, _entries, indented: false);
    }

    /// <summary>Markeert alle half bekeken afleveringen van een serie als bekeken.</summary>
    public void MarkSeriesFinished(string seriesId, DateTime nowUtc)
    {
        var any = false;
        foreach (var e in _entries.Values.Where(v => v.SeriesId == seriesId && !v.Finished))
        {
            e.Finished = true;
            e.PositionMs = 0;
            e.UpdatedUtc = nowUtc;
            any = true;
        }
        if (any) _store.Save(FileName, _entries, indented: false);
    }

    private static Entry WithMeta(Entry e, EpisodeMeta? m)
    {
        if (m is null) return e;
        e.SeriesId = m.SeriesId;
        e.EpisodeId = m.EpisodeId;
        e.Season = m.Season;
        e.Number = m.Number;
        e.EpisodeTitle = m.EpisodeTitle;
        return e;
    }

    public void Forget(string key)
    {
        if (_entries.Remove(key)) _store.Save(FileName, _entries, indented: false);
    }
}
