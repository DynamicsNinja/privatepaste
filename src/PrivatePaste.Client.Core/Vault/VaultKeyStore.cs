using System.Security.Cryptography;
using PrivatePaste.Client.Core.Security;
using PrivatePaste.Protocol.Crypto;

namespace PrivatePaste.Client.Core.Vault;

/// <summary>The vault key kept on this device, protected by the OS the same way the sign-in token is.</summary>
public sealed class VaultKeyStore(string path, ISecretProtector protector)
{
    private readonly SecretFile _file = new(path, protector);

    /// <summary>The stored key, or null if there is none or it can't be read (corrupt, other user).</summary>
    public VaultKey? Load()
    {
        var text = _file.Read();
        if (text is null) return null;
        byte[]? raw = null;
        try
        {
            raw = Convert.FromBase64String(text);
            return VaultKey.FromBytes(raw);
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (raw is not null) CryptographicOperations.ZeroMemory(raw);
        }
    }

    public void Save(VaultKey key)
    {
        var raw = key.Export();
        try { _file.Write(Convert.ToBase64String(raw)); }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }

    public void Delete() => _file.Delete();
}
