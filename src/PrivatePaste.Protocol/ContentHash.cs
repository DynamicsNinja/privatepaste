using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PrivatePaste.Protocol;

public static class ContentHash
{
    /// <summary>Converts CRLF and lone CR to LF; nothing else changes.</summary>
    public static string NormalizeText(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>Lowercase hex of SHA256(UTF8("t:" + NormalizeText(text))).</summary>
    public static string ForText(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("t:" + NormalizeText(text))));

    /// <summary>
    /// Canonical image identity across all clients: lowercase hex of SHA256(UTF8("i:{width}x{height}:") ++ rgba).
    /// <paramref name="rgba"/> must be RGBA8888, unpremultiplied, row-major, with no row padding.
    /// </summary>
    public static string ForPixels(ReadOnlySpan<byte> rgba, int width, int height)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(Encoding.UTF8.GetBytes($"i:{width}x{height}:"));
        h.AppendData(rgba);
        return Convert.ToHexStringLower(h.GetHashAndReset());
    }

    /// <summary>
    /// Identity of a file clip: lowercase hex of SHA256(UTF8("f:") ++ for each file in order UTF8(name + "\0" + size + "\0" + sha256hex + "\n")).
    /// <c>size</c> is the decimal byte count; <c>Sha256Hex</c> is the lowercase hex SHA-256 of the file's content, which callers compute.
    /// </summary>
    public static string ForFiles(IReadOnlyList<(string Name, long Size, string Sha256Hex)> files)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(Encoding.UTF8.GetBytes("f:"));
        foreach (var (name, size, sha) in files)
            h.AppendData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{name}\0{size}\0{sha}\n")));
        return Convert.ToHexStringLower(h.GetHashAndReset());
    }
}
