namespace PrivatePaste.Client.Core.Settings;

/// <summary>Matches a copy's source app against the user's blocklist (<see cref="ClientSettings.BlockedApps"/>).</summary>
public static class BlockedApps
{
    /// <summary>
    /// True when <paramref name="sourceApp"/> is on the list. Case-insensitive, ignoring surrounding spaces and a typed
    /// ".exe" (source apps come without it); bundle ids such as "com.agilebits.onepassword7" match whole.
    /// </summary>
    public static bool Matches(IEnumerable<string> blocked, string sourceApp)
    {
        var app = Normalize(sourceApp);
        return app.Length > 0 && blocked.Any(b => Normalize(b) == app);
    }

    /// <summary>The stored form of an app name: trimmed, lowercase, without a typed ".exe" (empty when nothing is left).</summary>
    public static string Normalize(string name)
    {
        var n = name.Trim();
        if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
        return n.ToLowerInvariant();
    }
}
