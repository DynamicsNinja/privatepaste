using System.Text;

namespace PrivatePaste.Client.Core.Security;

public sealed class SecretFile(string path, ISecretProtector protector)
{
    /// <summary>The secret, or null if the file is missing or cannot be unprotected (corrupt / other user).</summary>
    public string? Read()
    {
        try
        {
            if (!File.Exists(path)) return null;
            return Encoding.UTF8.GetString(protector.Unprotect(File.ReadAllBytes(path)));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Write(string value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, protector.Protect(Encoding.UTF8.GetBytes(value)));
        File.Move(tmp, path, overwrite: true);
    }

    public void Delete()
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
