namespace PrivatePaste.Server.Pages;

/// <summary>How long ago something happened, as the web UI words it ("5 min ago").</summary>
public static class TimeAgo
{
    public static string Format(DateTimeOffset at, DateTimeOffset now)
    {
        var span = now - at;
        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min ago";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} h ago";
        if (span < TimeSpan.FromDays(30)) return $"{(int)span.TotalDays} d ago";
        return at.ToString("yyyy-MM-dd");
    }
}
