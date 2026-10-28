namespace PrivatePaste.Client.Core.Settings;

public static class ServerUrl
{
    public static Uri Normalize(string input)
    {
        var text = (input ?? "").Trim().TrimEnd('/');
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("Enter a full server address starting with https://");

        if (uri.Scheme == Uri.UriSchemeHttp && !IsLoopback(uri.Host))
            throw new ArgumentException("The server address must use https:// (http:// is only allowed for localhost).");

        return uri;
    }

    private static bool IsLoopback(string host) =>
        host is "localhost" or "127.0.0.1" or "[::1]" or "::1";
}
