using System.Security.Cryptography;

namespace IptvPlayer.Engine;

/// <summary>
/// Welke links de speler mag openen. Een afspeellijst komt van een derde: een link als smb://aanvaller/share laat Windows
/// inloggen bij de aanvaller (NTLM-hash lekt) en file:// opent lokale bestanden. We staan daarom alleen streamingprotocollen toe.
/// </summary>
public static class PlayableUrl
{
    private static readonly HashSet<string> Schemes = new(StringComparer.OrdinalIgnoreCase)
        { "http", "https", "rtsp", "rtmp", "rtp", "udp" };

    public static bool IsAllowed(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var i = url.IndexOf("://", StringComparison.Ordinal);
        return i > 0 && Schemes.Contains(url[..i].Trim());
    }
}

/// <summary>Beschermt een kleine geheime waarde (de sleutel) voor de huidige Windows-gebruiker. In de app: DPAPI.</summary>
public interface IKeyProtector
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}

/// <summary>
/// Versleutelt bestanden met wachtwoorden en inlog-links (AES-256-GCM). De sleutel zelf staat in key.dpapi,
/// beschermd door Windows (DPAPI): alleen jouw Windows-account op deze pc kan hem lezen.
/// Formaat van een bestand: magic (8) | nonce (12) | tag (16) | versleutelde data.
/// </summary>
public sealed class FileCipher
{
    private static readonly byte[] Magic = { (byte)'E', (byte)'Y', (byte)'E', (byte)'P', (byte)'T', (byte)'V', (byte)'1', 0 };
    private const int NonceSize = 12, TagSize = 16;
    private readonly byte[] _key;

    public FileCipher(byte[] key)
    {
        if (key.Length != 32) throw new ArgumentException("De sleutel moet 32 bytes zijn.", nameof(key));
        _key = key;
    }

    /// <summary>Opent de sleutel in de opslagmap, of maakt een nieuwe aan. Een sleutel die niet te lezen is (ander account) wordt vervangen.</summary>
    public static FileCipher Open(string root, IKeyProtector protector)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "key.dpapi");

        if (File.Exists(path))
        {
            try { return new FileCipher(protector.Unprotect(File.ReadAllBytes(path))); }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
                // Andere gebruiker of andere pc: versleutelde bestanden zijn dan niet meer te lezen en worden apart gezet.
                File.Move(path, path + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
            }
        }

        var key = RandomNumberGenerator.GetBytes(32);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, protector.Protect(key));
        File.Move(tmp, path, overwrite: true);
        return new FileCipher(key);
    }

    public static bool IsEncrypted(ReadOnlySpan<byte> data) =>
        data.Length >= Magic.Length + NonceSize + TagSize && data[..Magic.Length].SequenceEqual(Magic);

    public byte[] Encrypt(byte[] plain)
    {
        var result = new byte[Magic.Length + NonceSize + TagSize + plain.Length];
        Magic.CopyTo(result, 0);
        var nonce = result.AsSpan(Magic.Length, NonceSize);
        var tag = result.AsSpan(Magic.Length + NonceSize, TagSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, result.AsSpan(Magic.Length + NonceSize + TagSize), tag);
        return result;
    }

    public byte[] Decrypt(byte[] data)
    {
        if (!IsEncrypted(data)) throw new CryptographicException("Geen versleuteld EyePTV-bestand.");
        var nonce = data.AsSpan(Magic.Length, NonceSize);
        var tag = data.AsSpan(Magic.Length + NonceSize, TagSize);
        var cipher = data.AsSpan(Magic.Length + NonceSize + TagSize);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain); // gooit CryptographicException bij een verkeerde sleutel of een aangepast bestand
        return plain;
    }
}

/// <summary>Leest hooguit een vast aantal bytes; daarna een fout. Beschermt tegen oneindig grote downloads en "zip-bommen".</summary>
public sealed class LimitedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _limit;
    private long _read;

    public LimitedReadStream(Stream inner, long limit)
    {
        _inner = inner;
        _limit = limit;
    }

    private void Count(int n)
    {
        _read += n;
        if (_read > _limit) throw new InvalidDataException($"Het bestand is groter dan de toegestane {_limit / (1024 * 1024)} MB.");
    }

    public override int Read(byte[] buffer, int offset, int count) { var n = _inner.Read(buffer, offset, count); Count(n); return n; }
    public override int Read(Span<byte> buffer) { var n = _inner.Read(buffer); Count(n); return n; }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var n = await _inner.ReadAsync(buffer, ct);
        Count(n);
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _read; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
