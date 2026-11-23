using System.Net;
using System.Net.Http.Headers;

namespace IptvPlayer.Engine;

public enum DownloadStatus
{
    /// <summary>Wacht tot het aan de beurt is (er wordt er één tegelijk gedownload).</summary>
    Queued,
    Downloading,
    /// <summary>Door de gebruiker gepauzeerd; het deel dat er al is blijft staan.</summary>
    Paused,
    Done,
    Failed
}

/// <summary>Een film of aflevering die naar de schijf wordt gedownload. Staat versleuteld in downloads.json (de link bevat je inlog).</summary>
public sealed class DownloadItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Sleutel van het item in de kijkgeschiedenis: <see cref="PersonalChannel.Id"/> bij een film, bronId|ep:afleveringId bij een aflevering.
    /// Daarmee deelt een download de kijkvoortgang met het streamen.
    /// </summary>
    public string Key { get; set; } = "";

    public string SourceId { get; set; } = "";

    /// <summary>Groep van de film of serie bij de bron (voor het afschermen van erotiek).</summary>
    public string Group { get; set; } = "";
    public bool IsEpisode { get; set; }

    /// <summary>Bij een aflevering: <see cref="PersonalChannel.Id"/> van de serie, en de gegevens van de aflevering.</summary>
    public string? SeriesId { get; set; }
    public string? SeriesTitle { get; set; }
    public string? EpisodeId { get; set; }
    public int Season { get; set; }
    public int Number { get; set; }

    public string Title { get; set; } = "";
    public string? Info { get; set; }
    public string? LogoUrl { get; set; }
    public string Url { get; set; } = "";

    public DownloadStatus Status { get; set; }
    public string? FilePath { get; set; }
    public long Bytes { get; set; }
    public long TotalBytes { get; set; }
    public string? Error { get; set; }
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;

    public bool IsActive => Status is DownloadStatus.Queued or DownloadStatus.Downloading;
}

/// <summary>
/// Downloadt films en afleveringen rechtstreeks naar een bestand (zonder omzetten: het formaat van de provider blijft, meestal .mkv of .mp4).
/// Eén download tegelijk (veel providers staan weinig verbindingen toe), met hervatten waar het bestand gebleven was.
/// <see cref="Tick"/> wordt regelmatig aangeroepen en start de volgende in de wachtrij.
/// </summary>
public sealed class DownloadManager
{
    private const string FileName = "downloads.json";
    private const long MinFreeBytes = 500L * 1024 * 1024;
    private const int MaxAttempts = 6;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly SettingsStore _settings;
    private readonly AppStore _store;
    private readonly SynchronizationContext? _ui;
    private readonly object _gate = new();
    private readonly List<DownloadItem> _items;
    private readonly Dictionary<string, (CancellationTokenSource Cts, Task Task)> _running = new();
    private readonly HashSet<string> _pausedByUser = new();
    private DateTime _lastNotify;
    private bool _stopping;

    /// <summary>Wachttijd tussen twee pogingen als de verbinding wegvalt (in tests korter).</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(3);

    public event Action? Changed;

    public DownloadManager(HttpClient http, SettingsStore settings, AppStore store)
    {
        _http = http;
        _settings = settings;
        _store = store;
        _ui = SynchronizationContext.Current;
        _items = store.Load(FileName, () => new List<DownloadItem>());

        // Na een herstart: wat nog liep, wacht weer op zijn beurt (en gaat verder waar het bestand was).
        foreach (var d in _items.Where(d => d.Status == DownloadStatus.Downloading)) d.Status = DownloadStatus.Queued;
    }

    // ---- Instellingen ----

    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "EyePTV", "Downloads");

    public string Folder => string.IsNullOrWhiteSpace(_settings.Current.DownloadFolder) ? DefaultFolder : _settings.Current.DownloadFolder.Trim();

    // ---- Overzicht ----

    public IReadOnlyList<DownloadItem> All
    {
        get { lock (_gate) return _items.OrderByDescending(d => d.AddedUtc).ToList(); }
    }

    public int ActiveCount { get { lock (_gate) return _items.Count(d => d.IsActive); } }

    public DownloadItem? Find(string id) { lock (_gate) return _items.FirstOrDefault(d => d.Id == id); }

    /// <summary>Het item met deze kijk-sleutel (een film of aflevering), als het al is toegevoegd.</summary>
    public DownloadItem? ForKey(string key)
    {
        lock (_gate)
            return _items.OrderByDescending(d => d.AddedUtc).FirstOrDefault(d => d.Key == key && d.Status != DownloadStatus.Failed)
                   ?? _items.OrderByDescending(d => d.AddedUtc).FirstOrDefault(d => d.Key == key);
    }

    /// <summary>De link naar het bestand op de schijf als dit item helemaal is gedownload (anders null).</summary>
    public string? LocalUrlFor(string key)
    {
        var d = ForKey(key);
        if (d is not { Status: DownloadStatus.Done, FilePath: { } path } || !File.Exists(path)) return null;
        return new Uri(path).AbsoluteUri;
    }

    /// <summary>Mag de speler dit bestand openen? Alleen bestanden van onze eigen downloads.</summary>
    public bool OwnsUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsFile) return false;
        var path = Path.GetFullPath(uri.LocalPath);
        lock (_gate)
            return _items.Any(d => d.FilePath is { } f && string.Equals(Path.GetFullPath(f), path, StringComparison.OrdinalIgnoreCase));
    }

    // ---- Toevoegen ----

    /// <summary>Zet een download in de wachtrij. Staat het item er al in (of is het klaar), dan komt er geen tweede bij.</summary>
    public DownloadItem Enqueue(DownloadItem item)
    {
        if (!PlayableUrl.IsAllowed(item.Url) || !(item.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                                   item.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Alleen films en afleveringen met een http(s)-link kunnen worden gedownload.");

        lock (_gate)
        {
            var existing = _items.FirstOrDefault(d => d.Key == item.Key && d.Status != DownloadStatus.Failed &&
                                                      !(d.Status == DownloadStatus.Done && (d.FilePath is null || !File.Exists(d.FilePath))));
            if (existing is not null) return existing;

            // Een eerder mislukte poging voor hetzelfde item vervangen we.
            foreach (var old in _items.Where(d => d.Key == item.Key).ToList()) _items.Remove(old);

            item.Status = DownloadStatus.Queued;
            item.AddedUtc = DateTime.UtcNow;
            _items.Add(item);
        }
        Save();
        Notify(force: true);
        Tick();
        return item;
    }

    // ---- Pauzeren, hervatten, verwijderen ----

    public void Pause(string id)
    {
        DownloadItem? d;
        lock (_gate)
        {
            d = _items.FirstOrDefault(x => x.Id == id);
            if (d is null) return;
            if (d.Status == DownloadStatus.Queued) { d.Status = DownloadStatus.Paused; }
            else if (d.Status == DownloadStatus.Downloading && _running.TryGetValue(id, out var run))
            {
                _pausedByUser.Add(id);
                run.Cts.Cancel();
                return;
            }
            else return;
        }
        Save();
        Notify(force: true);
    }

    /// <summary>Hervat een gepauzeerde of mislukte download (het bestand gaat verder waar het was).</summary>
    public void Resume(string id)
    {
        lock (_gate)
        {
            var d = _items.FirstOrDefault(x => x.Id == id);
            if (d is null || d.Status is not (DownloadStatus.Paused or DownloadStatus.Failed)) return;
            d.Status = DownloadStatus.Queued;
            d.Error = null;
        }
        Save();
        Notify(force: true);
        Tick();
    }

    /// <summary>Haalt een download uit de lijst. Het bestand verwijderen we ook als dat gevraagd wordt.</summary>
    public void Delete(string id, bool deleteFile)
    {
        DownloadItem? d;
        Task? running = null;
        lock (_gate)
        {
            d = _items.FirstOrDefault(x => x.Id == id);
            if (d is null) return;
            if (_running.TryGetValue(id, out var run)) { _pausedByUser.Add(id); run.Cts.Cancel(); running = run.Task; }
        }

        try { running?.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }

        lock (_gate) _items.Remove(d);
        if (deleteFile && d.FilePath is { } f) TryDelete(f);
        Save();
        Notify(force: true);
        Tick();
    }

    /// <summary>Pauzeert alles wat loopt (bij het afsluiten van de app); bij de volgende start gaat het verder.</summary>
    public void StopAll()
    {
        Task[] tasks;
        string[] ids;
        lock (_gate)
        {
            _stopping = true; // geen nieuwe download starten
            ids = _running.Keys.ToArray();
            foreach (var (id, run) in _running) { _pausedByUser.Add(id); run.Cts.Cancel(); }
            tasks = _running.Values.Select(v => v.Task).ToArray();
        }
        try { Task.WaitAll(tasks, TimeSpan.FromSeconds(5)); } catch (AggregateException) { }

        // Wat gepauzeerd werd om af te sluiten, hoort bij de volgende start gewoon verder te gaan.
        lock (_gate)
            foreach (var d in _items.Where(d => ids.Contains(d.Id) && d.Status == DownloadStatus.Paused)) d.Status = DownloadStatus.Queued;
        Save();
    }

    // ---- Wachtrij ----

    /// <summary>Start de volgende download uit de wachtrij als er geen loopt.</summary>
    public void Tick()
    {
        DownloadItem? next;
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_stopping || _running.Count > 0) return;
            next = _items.OrderBy(d => d.AddedUtc).FirstOrDefault(d => d.Status == DownloadStatus.Queued);
            if (next is null) return;

            next.Status = DownloadStatus.Downloading;
            next.Error = null;
            cts = new CancellationTokenSource();
            var item = next;
            _running[next.Id] = (cts, Task.Run(() => RunAsync(item, cts.Token)));
        }
        Save();
        Notify(force: true);
    }

    private async Task RunAsync(DownloadItem d, CancellationToken ct)
    {
        string? error = null;
        try
        {
            d.FilePath ??= NextFilePath(d);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var finished = await TransferAsync(d, ct);
                    if (finished) break;
                    throw new IOException("De verbinding werd te vroeg verbroken.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (DiskWriteException ex) { error = "Opslaan lukt niet: " + ex.Message; break; }
                catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or TaskCanceledException)
                {
                    // Een IOException die niet van het schrijven komt, is een wegvallende verbinding: opnieuw proberen.
                    if (ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Unauthorized })
                    {
                        error = ex.Message;
                        break;
                    }
                    if (attempt >= MaxAttempts) { error = "De verbinding viel steeds weg: " + ex.Message; break; }
                }

                await Task.Delay(RetryDelay, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = "De download kon niet worden opgeslagen: " + ex.Message;
        }
        finally
        {
            Finish(d, error);
        }
    }

    /// <summary>Haalt (de rest van) het bestand op. True als het helemaal binnen is.</summary>
    private async Task<bool> TransferAsync(DownloadItem d, CancellationToken ct)
    {
        var path = d.FilePath!;
        var offset = File.Exists(path) ? new FileInfo(path).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, d.Url);
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);

        using var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // Het bestand is al even groot als het origineel.
            if (offset > 0) { d.Bytes = offset; d.TotalBytes = Math.Max(d.TotalBytes, offset); return true; }
        }
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"De server antwoordde met {(int)resp.StatusCode}.", null, resp.StatusCode);

        var append = resp.StatusCode == HttpStatusCode.PartialContent && offset > 0;
        if (!append) offset = 0; // de server hervat niet: opnieuw beginnen

        var total = resp.Content.Headers.ContentRange?.Length ?? (resp.Content.Headers.ContentLength is { } len ? len + offset : 0);
        d.TotalBytes = total;
        if (total > 0 && FreeSpace(path) is { } free && free < total - offset + MinFreeBytes)
            throw new DiskWriteException("Er is niet genoeg ruimte op de schijf voor deze download.");

        await using var net = await resp.Content.ReadAsStreamAsync(ct);
        FileStream file;
        try { file = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 128 * 1024, useAsync: true); }
        catch (IOException ex) { throw new DiskWriteException(ex.Message); }

        await using (file)
        {
            var buffer = new byte[256 * 1024];
            var written = offset;
            d.Bytes = written;

            while (true)
            {
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(ReadTimeout);
                var n = await net.ReadAsync(buffer, readCts.Token);
                if (n <= 0) break;

                try { await file.WriteAllAsync(buffer, n, ct); }
                catch (IOException ex) { throw new DiskWriteException(ex.Message); }

                written += n;
                d.Bytes = written;
                Notify(force: false);
            }

            try { await file.FlushAsync(ct); }
            catch (IOException ex) { throw new DiskWriteException(ex.Message); }
            return total == 0 || written >= total;
        }
    }

    private void Finish(DownloadItem d, string? error)
    {
        bool byUser;
        lock (_gate)
        {
            byUser = _pausedByUser.Remove(d.Id);
            _running.Remove(d.Id);
            if (!_items.Contains(d)) return; // intussen verwijderd
        }

        if (error is not null) { d.Status = DownloadStatus.Failed; d.Error = error; }
        else if (byUser) d.Status = DownloadStatus.Paused;
        else if (d.Bytes < 1024) { d.Status = DownloadStatus.Failed; d.Error = "Er kwam niets binnen van de server."; }
        else { d.Status = DownloadStatus.Done; d.Error = null; if (d.TotalBytes == 0) d.TotalBytes = d.Bytes; }

        Save();
        Notify(force: true);
        Tick();
    }

    // ---- Bestanden ----

    private string NextFilePath(DownloadItem d)
    {
        Directory.CreateDirectory(Folder);
        var name = d.IsEpisode
            ? $"{Clean(d.SeriesTitle ?? "Serie")} - S{d.Season:00}E{d.Number:00} - {Clean(d.Title)}"
            : Clean(d.Title);
        if (name.Length > 120) name = name[..120].TrimEnd();
        var ext = ExtensionOf(d.Url);

        var path = Path.Combine(Folder, name + ext);
        for (var i = 2; File.Exists(path) || OwnsPath(path); i++) path = Path.Combine(Folder, $"{name} ({i}){ext}");

        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Path.GetFullPath(Folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Ongeldige bestandsnaam voor de download.");
        return full;
    }

    private bool OwnsPath(string path)
    {
        lock (_gate) return _items.Any(d => d.FilePath is { } f && string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly string[] KnownExtensions = { ".mkv", ".mp4", ".avi", ".mov", ".m4v", ".ts", ".wmv", ".webm", ".mpg", ".mpeg", ".flv" };

    /// <summary>Het bestandstype van de link (".mkv", ".mp4", …); onbekend wordt ".mp4".</summary>
    public static string ExtensionOf(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return KnownExtensions.Contains(ext) ? ext : ".mp4";
    }

    private static string Clean(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = s.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        var t = new string(chars).Trim().TrimEnd('.');
        return t.Length == 0 ? "download" : t;
    }

    /// <summary>Het schrijven naar de schijf mislukte (vol, geen rechten): opnieuw proberen heeft geen zin.</summary>
    private sealed class DiskWriteException : Exception
    {
        public DiskWriteException(string message) : base(message) { }
    }

    private static long? FreeSpace(string? path)
    {
        try { return path is null ? null : new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---- Opslag en meldingen ----

    private void Save()
    {
        lock (_gate)
        {
            try { _store.Save(FileName, _items); } catch (IOException) { }
        }
    }

    private void Notify(bool force)
    {
        var now = DateTime.UtcNow;
        if (!force && now - _lastNotify < TimeSpan.FromMilliseconds(500)) return;
        _lastNotify = now;
        if (_ui is not null) _ui.Post(_ => Changed?.Invoke(), null);
        else Changed?.Invoke();
    }
}

internal static class StreamExtensions
{
    public static ValueTask WriteAllAsync(this Stream s, byte[] buffer, int count, CancellationToken ct) =>
        s.WriteAsync(buffer.AsMemory(0, count), ct);
}
