using PrivatePaste.Protocol;

namespace PrivatePaste.Server.Auth;

/// <summary>Reads <see cref="ProtocolHeaders.AppVersion"/>. Only a short version-like string is kept; anything else is ignored.</summary>
public static class AppVersionHeader
{
    public const int MaxLength = 32;

    public static string? Read(HttpRequest request) => Parse(request.Headers[ProtocolHeaders.AppVersion].ToString());

    /// <summary>The trimmed version, or null unless it is 1-32 letters, digits, '.', '-' or '+'.</summary>
    public static string? Parse(string? value)
    {
        var version = value?.Trim();
        if (string.IsNullOrEmpty(version) || version.Length > MaxLength)
            return null;
        foreach (var c in version)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '+'))
                return null;
        return version;
    }
}
