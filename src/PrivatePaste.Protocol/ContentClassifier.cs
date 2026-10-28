namespace PrivatePaste.Protocol;

public static class ContentClassifier
{
    public static ClipKind ClassifyText(string text)
    {
        var t = text.Trim();
        if (t.Length == 0 || t.Any(char.IsWhiteSpace)) return ClipKind.Text;
        return Uri.TryCreate(t, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? ClipKind.Link : ClipKind.Text;
    }
}
