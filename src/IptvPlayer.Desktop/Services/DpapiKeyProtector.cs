using System.Security.Cryptography;
using IptvPlayer.Engine;

namespace IptvPlayer.Desktop.Services;

/// <summary>Beschermt de bestandssleutel met Windows DPAPI: alleen dit Windows-account op deze pc kan hem openen.</summary>
public sealed class DpapiKeyProtector : IKeyProtector
{
    // Extra "zout" zodat andere programma's van dezelfde gebruiker de sleutel niet per ongeluk met DPAPI kunnen openen.
    private static readonly byte[] Entropy = System.Text.Encoding.UTF8.GetBytes("EyePTV.FileKey.v1");

    public byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
}
