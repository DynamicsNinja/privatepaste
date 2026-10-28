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
}
