using System.Security.Cryptography;

namespace IptvPlayer.Engine;

public enum UnlockResult { Unlocked, Wrong, Blocked }

/// <summary>
/// Schermt erotiek (zie <see cref="AdultContent"/>) af met een code van 4 cijfers. Zolang de app vergrendeld is
/// (bij elke start, en na minimaliseren) verschijnt erotiek nergens: niet in lijsten, zoeken, gids, beginscherm of opnames.
/// De code staat (versleuteld, met zout) in de instellingen; zonder eigen code is die 0000.
/// </summary>
public sealed class AdultLock
{
    public const string DefaultPin = "0000";
    private const int MaxFailures = 5;
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

    private readonly SettingsStore? _store;
    private readonly Func<DateTime> _now;
    private bool _unlocked;
    private int _failures;
    private DateTime _blockedUntil;

    public AdultLock(SettingsStore? store, Func<DateTime>? utcNow = null, bool unlocked = false)
    {
        _store = store;
        _now = utcNow ?? (() => DateTime.UtcNow);
        _unlocked = unlocked;
    }

    /// <summary>Een slot dat nooit iets verbergt (voor losse onderdelen en tests).</summary>
    public static AdultLock Open() => new(null, unlocked: true);

    /// <summary>Wordt afgevuurd als de app vergrendeld of ontgrendeld wordt.</summary>
    public event Action? Changed;

    public bool IsUnlocked => _unlocked;

    /// <summary>De code is nog 0000 (er is nog geen eigen code ingesteld).</summary>
    public bool UsesDefaultPin => string.IsNullOrEmpty(_store?.Current.AdultPinHash);

    /// <summary>Moet dit item nu verborgen blijven?</summary>
    public bool Hides(PersonalChannel channel) => !_unlocked && AdultContent.IsAdult(channel);

    /// <summary>Moet deze opname nu verborgen blijven? (Op de zender of op de titel.)</summary>
    public bool HidesRecording(Recording recording, IEnumerable<PersonalChannel> personal)
    {
        if (_unlocked) return false;
        var channel = personal.FirstOrDefault(p => p.Id == recording.ChannelId);
        return AdultContent.IsAdult(channel?.Group, recording.Title) || (channel is not null && AdultContent.IsAdult(channel));
    }

    /// <summary>Moet deze download nu verborgen blijven? (Op de groep, de titel of de naam van de serie.)</summary>
    public bool HidesDownload(DownloadItem d) =>
        !_unlocked && (AdultContent.IsAdult(d.Group, d.Title) || (d.SeriesTitle is not null && AdultContent.IsAdult(d.Group, d.SeriesTitle)));

    public static bool IsValidPin(string? pin) => pin is { Length: 4 } && pin.All(char.IsAsciiDigit);

    public UnlockResult TryUnlock(string? pin)
    {
        var result = Check(pin);
        if (result != UnlockResult.Unlocked) return result;
        if (_unlocked) return result;
        _unlocked = true;
        Changed?.Invoke();
        return result;
    }

    public void Lock()
    {
        if (!_unlocked) return;
        _unlocked = false;
        Changed?.Invoke();
    }

    /// <summary>Stelt een nieuwe code in. Geeft een foutmelding terug, of null als het gelukt is.</summary>
    public string? ChangePin(string? current, string? next)
    {
        switch (Check(current))
        {
            case UnlockResult.Blocked: return "Te vaak een verkeerde code. Probeer het over een halve minuut opnieuw.";
            case UnlockResult.Wrong: return "De huidige code klopt niet.";
        }
        if (!IsValidPin(next)) return "De nieuwe code moet uit precies 4 cijfers bestaan.";
        if (_store is null) return "De code kan niet worden bewaard.";

        var salt = RandomNumberGenerator.GetBytes(16);
        _store.Current.AdultPinSalt = Convert.ToBase64String(salt);
        _store.Current.AdultPinHash = Convert.ToBase64String(Hash(next!, salt));
        _store.Save();
        return null;
    }

    private UnlockResult Check(string? pin)
    {
        if (_now() < _blockedUntil) return UnlockResult.Blocked;

        if (Matches(pin))
        {
            _failures = 0;
            return UnlockResult.Unlocked;
        }

        if (++_failures >= MaxFailures)
        {
            _failures = 0;
            _blockedUntil = _now() + Cooldown;
            return UnlockResult.Blocked;
        }
        return UnlockResult.Wrong;
    }

    private bool Matches(string? pin)
    {
        if (!IsValidPin(pin)) return false;
        var s = _store?.Current;
        if (s is null || string.IsNullOrEmpty(s.AdultPinHash) || string.IsNullOrEmpty(s.AdultPinSalt))
            return pin == DefaultPin;

        try
        {
            var expected = Convert.FromBase64String(s.AdultPinHash);
            return CryptographicOperations.FixedTimeEquals(Hash(pin!, Convert.FromBase64String(s.AdultPinSalt)), expected);
        }
        catch (FormatException)
        {
            return pin == DefaultPin; // beschadigde instelling: terug naar de standaardcode
        }
    }

    private static byte[] Hash(string pin, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(pin, salt, 100_000, HashAlgorithmName.SHA256, 32);
}
