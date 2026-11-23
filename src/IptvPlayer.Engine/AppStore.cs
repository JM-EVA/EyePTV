using System.Security.Cryptography;
using System.Text.Json;

namespace IptvPlayer.Engine;

/// <summary>
/// Bewaart alles als JSON-bestanden in %APPDATA%\IptvPlayer (of een opgegeven map).
/// Schrijven gebeurt atomair: eerst een tijdelijk bestand, daarna vervangen.
/// Bestanden met wachtwoorden of inlog-links (bronnen, instellingen, je lijst en de catalogus) worden versleuteld
/// opgeslagen als er een <see cref="FileCipher"/> is; oude leesbare bestanden worden bij het eerste lezen omgezet.
/// </summary>
public sealed class AppStore
{
    private static readonly JsonSerializerOptions Pretty = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions Compact = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private readonly FileCipher? _cipher;

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IptvPlayer");

    public string Root { get; }

    public AppStore(string? root = null, FileCipher? cipher = null)
    {
        Root = root ?? DefaultRoot;
        _cipher = cipher;
        Directory.CreateDirectory(Root);
    }

    public string PathOf(string file) => Path.Combine(Root, file);

    /// <summary>Bestanden waar inloggegevens in kunnen staan (ook als onderdeel van stream-links).</summary>
    private static bool IsSensitive(string file) =>
        file is "sources.json" or "settings.json" or "personal.json" or "recordings.json" or "downloads.json" || file.StartsWith("catalog-", StringComparison.Ordinal);

    public T Load<T>(string file, Func<T> fallback)
    {
        var path = PathOf(file);
        if (!File.Exists(path)) return fallback();
        try
        {
            if (IsSensitive(file)) return LoadSensitive(file, path, fallback);

            using var fs = File.OpenRead(path);
            var value = JsonSerializer.Deserialize<T>(fs, Pretty);
            return value is null ? fallback() : value;
        }
        catch (JsonException)
        {
            // Beschadigd bestand: bewaar het als .corrupt zodat er niets verloren gaat, en begin opnieuw.
            try { File.Move(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss")); } catch { }
            return fallback();
        }
        catch (CryptographicException)
        {
            // Versleuteld met een andere sleutel (ander Windows-account) of aangepast: niet overschrijven, maar apart zetten.
            try { File.Move(path, path + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss")); } catch { }
            return fallback();
        }
    }

    private T LoadSensitive<T>(string file, string path, Func<T> fallback)
    {
        var raw = File.ReadAllBytes(path);
        var encrypted = FileCipher.IsEncrypted(raw);
        if (encrypted && _cipher is null) throw new CryptographicException("Versleuteld bestand zonder sleutel.");

        var json = encrypted ? _cipher!.Decrypt(raw) : raw;
        var value = JsonSerializer.Deserialize<T>(json, Pretty);
        if (value is null) return fallback();

        // Oud, leesbaar bestand: meteen versleuteld terugschrijven.
        if (!encrypted && _cipher is not null)
        {
            try { Save(file, value, indented: !file.StartsWith("catalog-", StringComparison.Ordinal)); } catch (IOException) { }
        }
        return value;
    }

    public void Save<T>(string file, T value, bool indented = true)
    {
        var path = PathOf(file);
        var tmp = path + ".tmp";

        if (_cipher is not null && IsSensitive(file))
        {
            var plain = JsonSerializer.SerializeToUtf8Bytes(value, indented ? Pretty : Compact);
            File.WriteAllBytes(tmp, _cipher.Encrypt(plain));
        }
        else
        {
            using var fs = File.Create(tmp);
            JsonSerializer.Serialize(fs, value, indented ? Pretty : Compact);
        }

        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    public void Delete(string file)
    {
        var path = PathOf(file);
        if (File.Exists(path)) File.Delete(path);
    }
}
