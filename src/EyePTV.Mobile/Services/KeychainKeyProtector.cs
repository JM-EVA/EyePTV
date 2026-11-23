using System.Security.Cryptography;
using IptvPlayer.Engine;

namespace EyePTV.Mobile.Services;

/// <summary>
/// Bewaart de bestandssleutel in de iOS-keychain (SecureStorage); in key.dpapi staat alleen een verwijzing.
/// Weigert de keychain (bijvoorbeeld bij een opnieuw ondertekende app zonder keychain-rechten), dan valt hij terug op een
/// bestand in de eigen app-map: dat is door de iOS-sandbox alleen voor deze app leesbaar.
/// </summary>
public sealed class KeychainKeyProtector : IKeyProtector
{
    private const string Name = "eyeptv.filekey";
    private static readonly byte[] Marker = { (byte)'K', (byte)'C', 1 };
    private readonly string _fallbackFile;

    public KeychainKeyProtector(string root) => _fallbackFile = Path.Combine(root, "key.local");

    public byte[] Protect(byte[] data)
    {
        var text = Convert.ToBase64String(data);
        try
        {
            Task.Run(() => SecureStorage.Default.SetAsync(Name, text)).GetAwaiter().GetResult();
            if (File.Exists(_fallbackFile)) File.Delete(_fallbackFile);
        }
        catch (Exception)
        {
            File.WriteAllText(_fallbackFile, text);
        }
        return Marker;
    }

    public byte[] Unprotect(byte[] data)
    {
        if (!data.AsSpan().SequenceEqual(Marker)) throw new CryptographicException("Onbekend sleutelformaat.");

        string? text = null;
        try { text = Task.Run(() => SecureStorage.Default.GetAsync(Name)).GetAwaiter().GetResult(); }
        catch (Exception) { /* keychain niet beschikbaar: het bestand hieronder */ }
        if (text is null && File.Exists(_fallbackFile)) text = File.ReadAllText(_fallbackFile);
        if (text is null) throw new CryptographicException("De sleutel staat niet meer in de keychain.");

        try { return Convert.FromBase64String(text); }
        catch (FormatException ex) { throw new CryptographicException("De bewaarde sleutel is beschadigd.", ex); }
    }
}
