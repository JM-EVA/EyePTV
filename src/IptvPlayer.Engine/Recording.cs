namespace IptvPlayer.Engine;

public enum RecordingMode
{
    /// <summary>Tot je zelf stopt.</summary>
    UntilStopped,
    /// <summary>Tot een eindtijd.</summary>
    UntilTime,
    /// <summary>Een programma uit de EPG: met wat extra vooraf en achteraf, en de eindtijd volgt de EPG.</summary>
    Programme
}

public enum RecordingStatus
{
    Scheduled,
    Recording,
    Done,
    Failed,
    Cancelled,
    /// <summary>Het geplande moment is voorbij gegaan terwijl de app niet openstond.</summary>
    Missed
}

/// <summary>Een opname: gepland, bezig of klaar. Staat versleuteld in recordings.json (de link bevat je inlog).</summary>
public sealed class Recording
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary><see cref="PersonalChannel.Id"/> van de zender.</summary>
    public string ChannelId { get; set; } = "";
    public string ChannelName { get; set; } = "";
    public string? LogoUrl { get; set; }
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";

    public RecordingMode Mode { get; set; }

    /// <summary>Wanneer het opnemen begint (bij een programma: een paar minuten vóór het begin).</summary>
    public DateTime StartUtc { get; set; }

    /// <summary>Wanneer het stopt; null = tot je zelf stopt.</summary>
    public DateTime? EndUtc { get; set; }

    /// <summary>Alleen bij een programma uit de EPG: de tijden van het programma zelf.</summary>
    public DateTime? ProgrammeStartUtc { get; set; }
    public DateTime? ProgrammeStopUtc { get; set; }

    public RecordingStatus Status { get; set; }
    public string? FilePath { get; set; }
    public long Bytes { get; set; }
    public string? Error { get; set; }
    public DateTime? ActualStartUtc { get; set; }
    public DateTime? ActualEndUtc { get; set; }

    public bool IsActive => Status is RecordingStatus.Scheduled or RecordingStatus.Recording;
}

/// <summary>Maakt van een opname (.ts) een .mp4 zonder opnieuw te coderen.</summary>
public interface IRecordingConverter
{
    Task<bool> ConvertAsync(string sourcePath, string targetPath, CancellationToken ct);
}

/// <summary>
/// Plant opnames in en voert ze uit: de stream van de zender wordt rechtstreeks naar een .ts-bestand geschreven
/// (daarna omgezet naar .mp4)
/// (onafhankelijk van de speler). Een opname loopt door als de verbinding even wegvalt en stopt op de eindtijd.
/// De app moet openstaan om op te nemen; <see cref="Tick"/> wordt regelmatig aangeroepen (elke paar seconden).
/// </summary>
public sealed class RecordingManager
{
    private const string FileName = "recordings.json";
    private const long MinFreeBytes = 500L * 1024 * 1024;
    private const int MaxConsecutiveFailures = 8;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    private readonly Library _library;
    private readonly EpgService _epg;
    private readonly HttpClient _http;
    private readonly SettingsStore _settings;
    private readonly AppStore _store;
    private readonly Func<DateTime> _now;
    private readonly SynchronizationContext? _ui;
    private readonly object _gate = new();
    private readonly List<Recording> _items;
    private readonly Dictionary<string, (CancellationTokenSource Cts, Task Task)> _running = new();
    private readonly HashSet<string> _stoppedByUser = new();
    private DateTime _lastNotify;
    private DateTime _lastSave;

    /// <summary>Wachttijd tussen twee pogingen als de verbinding wegvalt (in tests korter).</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    public event Action? Changed;

    /// <summary>Verpakt klare opnamen als .mp4 (wordt door de app ingesteld; zonder converter blijft het een .ts).</summary>
    public IRecordingConverter? Converter { get; set; }

    public RecordingManager(Library library, EpgService epg, HttpClient http, SettingsStore settings, AppStore store, Func<DateTime>? utcNow = null)
    {
        _library = library;
        _epg = epg;
        _http = http;
        _settings = settings;
        _store = store;
        _now = utcNow ?? (() => DateTime.UtcNow);
        _ui = SynchronizationContext.Current;
        _items = store.Load(FileName, () => new List<Recording>());

        // Na een herstart: wat nog liep, is onderbroken.
        foreach (var r in _items.Where(r => r.Status == RecordingStatus.Recording).ToList())
        {
            if (r.EndUtc is { } end && end > _now())
            {
                r.Status = RecordingStatus.Scheduled; // het venster loopt nog: start opnieuw (in een nieuw bestand)
                r.FilePath = null;
                r.Bytes = 0;
            }
            else
            {
                r.Status = r.Bytes > 0 ? RecordingStatus.Done : RecordingStatus.Failed;
                r.Error = r.Bytes > 0 ? null : "De app werd afgesloten tijdens het opnemen.";
                r.ActualEndUtc ??= _now();
            }
        }
    }

    // ---- Instellingen ----

    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "EyePTV");

    public string Folder => string.IsNullOrWhiteSpace(_settings.Current.RecordingFolder) ? DefaultFolder : _settings.Current.RecordingFolder.Trim();

    public TimeSpan Before => TimeSpan.FromMinutes(Math.Clamp(_settings.Current.RecordBeforeMinutes, 0, 60));
    public TimeSpan After => TimeSpan.FromMinutes(Math.Clamp(_settings.Current.RecordAfterMinutes, 0, 120));

    // ---- Overzicht ----

    public IReadOnlyList<Recording> All
    {
        get { lock (_gate) return _items.OrderByDescending(r => r.StartUtc).ToList(); }
    }

    public int ActiveCount { get { lock (_gate) return _items.Count(r => r.Status == RecordingStatus.Recording); } }

    public Recording? Find(string id) { lock (_gate) return _items.FirstOrDefault(r => r.Id == id); }

    /// <summary>Moet deze opname nu verborgen blijven (erotiek terwijl de app vergrendeld is)?</summary>
    public bool IsHidden(Recording recording, AdultLock adultLock) => adultLock.HidesRecording(recording, _library.Personal);

    /// <summary>Loopt of staat er een opname voor deze zender?</summary>
    public Recording? ActiveFor(PersonalChannel channel)
    {
        lock (_gate) return _items.FirstOrDefault(r => r.ChannelId == channel.Id && r.Status == RecordingStatus.Recording)
                         ?? _items.FirstOrDefault(r => r.ChannelId == channel.Id && r.Status == RecordingStatus.Scheduled);
    }

    /// <summary>Is dit programma al ingepland of wordt het opgenomen?</summary>
    public Recording? ForProgramme(PersonalChannel channel, Programme p)
    {
        lock (_gate)
            return _items.FirstOrDefault(r => r.IsActive && r.ChannelId == channel.Id && r.ProgrammeStartUtc is { } s &&
                                              Math.Abs((s - p.StartUtc).TotalMinutes) < 1);
    }

    /// <summary>Mag de speler dit bestand openen? Alleen bestanden van onze eigen opnames.</summary>
    public bool OwnsUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsFile) return false;
        var path = Path.GetFullPath(uri.LocalPath);
        lock (_gate)
            return _items.Any(r => r.FilePath is { } f && string.Equals(Path.GetFullPath(f), path, StringComparison.OrdinalIgnoreCase));
    }

    // ---- Opnames maken ----

    /// <summary>Nu beginnen met opnemen, tot je stopt (endUtc = null) of tot een eindtijd.</summary>
    public Recording RecordNow(PersonalChannel channel, DateTime? endUtc = null)
    {
        EnsureRecordable(channel);
        var now = _now();
        if (endUtc is { } e && e <= now) throw new InvalidOperationException("De eindtijd ligt al in het verleden.");

        var existing = ActiveFor(channel);
        if (existing is { Status: RecordingStatus.Recording })
            throw new InvalidOperationException("Deze zender wordt al opgenomen.");

        var (cur, _) = _epg.Data.NowNext(channel, now);
        var r = New(channel, cur?.Title ?? channel.DisplayName, endUtc is null ? RecordingMode.UntilStopped : RecordingMode.UntilTime);
        r.StartUtc = now;
        r.EndUtc = endUtc;
        Add(r);
        Tick();
        return r;
    }

    /// <summary>
    /// Een programma uit de EPG opnemen: de opname begint iets eerder en eindigt iets later dan het programma (zie
    /// <see cref="Before"/> en <see cref="After"/>), en volgt de EPG als de eindtijd daar verschuift.
    /// </summary>
    public Recording RecordProgramme(PersonalChannel channel, Programme p)
    {
        EnsureRecordable(channel);
        var now = _now();
        if (p.StopUtc + After <= now) throw new InvalidOperationException("Dit programma is al afgelopen.");

        if (ForProgramme(channel, p) is { } already) return already;

        var r = New(channel, p.Title, RecordingMode.Programme);
        r.ProgrammeStartUtc = p.StartUtc;
        r.ProgrammeStopUtc = p.StopUtc;
        r.StartUtc = p.StartUtc - Before;
        r.EndUtc = p.StopUtc + After;
        Add(r);
        Tick();
        return r;
    }

    private static void EnsureRecordable(PersonalChannel channel)
    {
        if (channel.Kind != ContentKind.Live) throw new InvalidOperationException("Opnemen kan alleen bij zenders.");
        if (!Uri.TryCreate(channel.Url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("Opnemen kan alleen bij zenders met een http- of https-stream.");
    }

    private Recording New(PersonalChannel channel, string title, RecordingMode mode) => new()
    {
        ChannelId = channel.Id,
        ChannelName = channel.DisplayName,
        LogoUrl = channel.LogoUrl,
        Title = string.IsNullOrWhiteSpace(title) ? channel.DisplayName : title.Trim(),
        Url = channel.Url,
        Mode = mode,
        Status = RecordingStatus.Scheduled
    };

    private void Add(Recording r)
    {
        lock (_gate) _items.Add(r);
        Save();
        Notify(force: true);
    }

    // ---- Stoppen en opruimen ----

    /// <summary>Stopt een lopende opname (het bestand blijft bewaard) of annuleert een geplande.</summary>
    public void Stop(string id)
    {
        Recording? r;
        lock (_gate) r = _items.FirstOrDefault(x => x.Id == id);
        if (r is null) return;

        if (r.Status == RecordingStatus.Scheduled)
        {
            r.Status = RecordingStatus.Cancelled;
            r.ActualEndUtc = _now();
            Save();
            Notify(force: true);
        }
        else if (r.Status == RecordingStatus.Recording)
        {
            lock (_gate)
            {
                _stoppedByUser.Add(id);
                if (_running.TryGetValue(id, out var run)) run.Cts.Cancel();
            }
        }
    }

    /// <summary>Verwijdert een opname uit de lijst (en desgewenst ook het bestand). Een lopende opname wordt eerst gestopt.</summary>
    public void Delete(string id, bool deleteFile)
    {
        Recording? r;
        Task? running = null;
        lock (_gate)
        {
            r = _items.FirstOrDefault(x => x.Id == id);
            if (r is null) return;
            if (_running.TryGetValue(id, out var run)) { _stoppedByUser.Add(id); run.Cts.Cancel(); running = run.Task; }
        }

        try { running?.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }

        lock (_gate) _items.Remove(r);
        if (deleteFile && r.FilePath is { } f)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Save();
        Notify(force: true);
    }

    /// <summary>Stopt alle opnames (bij het afsluiten van de app); het opgenomen deel blijft bewaard.</summary>
    public void StopAll()
    {
        Task[] tasks;
        lock (_gate)
        {
            foreach (var (id, run) in _running) { _stoppedByUser.Add(id); run.Cts.Cancel(); }
            tasks = _running.Values.Select(v => v.Task).ToArray();
        }
        try { Task.WaitAll(tasks, TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
    }

    // ---- Klok ----

    /// <summary>Start opnames die aan de beurt zijn, volgt de EPG en stopt opnames die klaar zijn. Elke paar seconden aanroepen.</summary>
    public void Tick()
    {
        var now = _now();
        List<Recording> snapshot;
        lock (_gate) snapshot = _items.Where(r => r.IsActive).ToList();

        foreach (var r in snapshot)
        {
            if (r.Mode == RecordingMode.Programme) FollowEpg(r);

            if (r.Status == RecordingStatus.Scheduled)
            {
                if (r.EndUtc is { } end && now >= end)
                {
                    r.Status = RecordingStatus.Missed;
                    r.Error = "Het geplande moment was voorbij; de app stond niet open.";
                    r.ActualEndUtc = now;
                    Save();
                    Notify(force: true);
                }
                else if (now >= r.StartUtc) Start(r);
            }
            else if (r.Status == RecordingStatus.Recording && r.EndUtc is { } e && now >= e)
            {
                lock (_gate) { if (_running.TryGetValue(r.Id, out var run)) run.Cts.Cancel(); }
            }
        }

        // Zo nu en dan de voortgang (bestandsgrootte) bewaren en melden.
        if (now - _lastSave > TimeSpan.FromSeconds(30) && ActiveCount > 0) Save();
        Notify(force: false);
    }

    /// <summary>"Zolang het programma in de EPG staat": als de EPG een nieuwe eindtijd geeft, volgt de opname die.</summary>
    private void FollowEpg(Recording r)
    {
        if (r.ProgrammeStartUtc is not { } start) return;
        PersonalChannel? ch;
        lock (_gate) ch = _library.Personal.FirstOrDefault(p => p.Id == r.ChannelId);
        if (ch is null) return;

        var p = _epg.Data.Schedule(ch, start.AddHours(-1), start.AddHours(12))
            .FirstOrDefault(x => Math.Abs((x.StartUtc - start).TotalMinutes) < 1);
        if (p is null || p.StopUtc == r.ProgrammeStopUtc) return;

        r.ProgrammeStopUtc = p.StopUtc;
        r.EndUtc = p.StopUtc + After;
        Save();
    }

    // ---- Het opnemen zelf ----

    private void Start(Recording r)
    {
        var cts = new CancellationTokenSource();
        r.Status = RecordingStatus.Recording;
        r.ActualStartUtc = _now();
        r.Error = null;

        lock (_gate)
        {
            var task = Task.Run(() => CaptureAsync(r, cts.Token));
            _running[r.Id] = (cts, task);
        }
        Save();
        Notify(force: true);
    }

    private bool PastEnd(Recording r) => r.EndUtc is { } e && _now() >= e;

    private string NextFilePath(Recording r)
    {
        Directory.CreateDirectory(Folder);
        var local = r.StartUtc.ToLocalTime();
        var name = $"{Clean(r.ChannelName)} - {Clean(r.Title)} - {local:yyyy-MM-dd HH-mm}";
        if (name.Length > 120) name = name[..120].TrimEnd();
        var path = Path.Combine(Folder, name + ".ts");
        for (var i = 2; File.Exists(path); i++) path = Path.Combine(Folder, $"{name} ({i}).ts");

        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Path.GetFullPath(Folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Ongeldige bestandsnaam voor de opname.");
        return full;
    }

    private static string Clean(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = s.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        var t = new string(chars).Trim().TrimEnd('.');
        return t.Length == 0 ? "opname" : t;
    }

    private async Task CaptureAsync(Recording r, CancellationToken ct)
    {
        string? error = null;
        try
        {
            r.FilePath = NextFilePath(r);
            await using var file = new FileStream(r.FilePath, FileMode.Create, FileAccess.Write, FileShare.Read, 81920, useAsync: true);
            var buffer = new byte[128 * 1024];
            var failures = 0;
            var sinceDiskCheck = 0L;

            while (!ct.IsCancellationRequested && !PastEnd(r))
            {
                try
                {
                    using var resp = await _http.GetAsync(r.Url, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"De server antwoordde met {(int)resp.StatusCode}.");
                    await using var net = await resp.Content.ReadAsStreamAsync(ct);

                    while (!ct.IsCancellationRequested && !PastEnd(r))
                    {
                        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        readCts.CancelAfter(ReadTimeout);
                        var n = await net.ReadAsync(buffer, readCts.Token);
                        if (n <= 0) break; // de server sloot de verbinding: opnieuw verbinden
                        await file.WriteAsync(buffer.AsMemory(0, n), ct);
                        r.Bytes += n;
                        failures = 0;

                        sinceDiskCheck += n;
                        if (sinceDiskCheck > 64L * 1024 * 1024)
                        {
                            sinceDiskCheck = 0;
                            if (FreeSpace(r.FilePath) is { } free && free < MinFreeBytes)
                            {
                                error = "De schijf is bijna vol; de opname is gestopt.";
                                break;
                            }
                        }
                    }
                    if (error is not null) break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or TaskCanceledException)
                {
                    if (ex is IOException && ex is not HttpRequestException && IsDiskError(ex)) { error = "Opslaan lukt niet: " + ex.Message; break; }
                    if (++failures >= MaxConsecutiveFailures) { error = "De verbinding met de zender viel steeds weg."; break; }
                }

                if (ct.IsCancellationRequested || PastEnd(r)) break;
                try { await Task.Delay(ReconnectDelay, ct); } catch (OperationCanceledException) { break; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = "De opname kon niet worden opgeslagen: " + ex.Message;
        }
        finally
        {
            Finish(r, error);
        }
    }

    private static bool IsDiskError(Exception ex) => ex is IOException && !(ex.InnerException is System.Net.Sockets.SocketException);

    private static long? FreeSpace(string? path)
    {
        try { return path is null ? null : new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }

    private void Finish(Recording r, string? error)
    {
        bool byUser;
        lock (_gate)
        {
            byUser = _stoppedByUser.Remove(r.Id);
            _running.Remove(r.Id);
        }

        r.ActualEndUtc = _now();
        if (error is not null) { r.Status = RecordingStatus.Failed; r.Error = error; }
        else if (r.Bytes < 1024) { r.Status = RecordingStatus.Failed; r.Error = "Er kwam geen beeld binnen van de zender."; }
        else { r.Status = RecordingStatus.Done; r.Error = null; }
        _ = byUser;

        Save();
        Notify(force: true);

        if (r.Status == RecordingStatus.Done) _ = Task.Run(() => ConvertToMp4Async(r));
    }

    /// <summary>
    /// Zet een klare opname om naar .mp4. Opnemen gebeurt in .ts (dat overleeft een wegvallende verbinding of een crash),
    /// daarna wordt het bestand verpakt zonder opnieuw te coderen. Lukt dat niet, dan blijft de .ts gewoon staan.
    /// </summary>
    public async Task ConvertToMp4Async(Recording r)
    {
        var converter = Converter;
        var ts = r.FilePath;
        if (converter is null || ts is null || !ts.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) || !File.Exists(ts)) return;

        var mp4 = Path.ChangeExtension(ts, ".mp4");
        for (var i = 2; File.Exists(mp4); i++) mp4 = Path.ChangeExtension(ts, null) + $" ({i}).mp4";

        try
        {
            var ok = await converter.ConvertAsync(ts, mp4, CancellationToken.None);
            var length = ok && File.Exists(mp4) ? new FileInfo(mp4).Length : 0;
            if (length < 1024)
            {
                TryDelete(mp4);
                return;
            }

            lock (_gate)
            {
                r.FilePath = mp4;
                r.Bytes = length;
            }
            TryDelete(ts);
            Save();
            Notify(force: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            TryDelete(mp4);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---- Opslag en meldingen ----

    private void Save()
    {
        lock (_gate)
        {
            _lastSave = _now();
            try { _store.Save(FileName, _items); } catch (IOException) { }
        }
    }

    private void Notify(bool force)
    {
        var now = DateTime.UtcNow;
        if (!force && now - _lastNotify < TimeSpan.FromSeconds(2)) return;
        _lastNotify = now;
        if (_ui is not null) _ui.Post(_ => Changed?.Invoke(), null);
        else Changed?.Invoke();
    }
}
