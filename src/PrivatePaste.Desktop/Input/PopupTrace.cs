using System.Diagnostics;

namespace PrivatePaste.Desktop.Input;

/// <summary>
/// One log line per step of the hotkey -> popup -> paste flow (which window was in front, whether the popup got the
/// focus, why it closed, whether the paste was sent), with milliseconds since the hotkey. Focus problems depend on the
/// other apps and can't be reproduced in tests, so this is what a report is diagnosed from.
/// </summary>
internal static class PopupTrace
{
    private static readonly Stopwatch Since = new();

    /// <summary>Where lines go (the app log); null writes nothing.</summary>
    public static Action<string>? Sink { get; set; }

    /// <summary>Starts a new trace (the hotkey was pressed).</summary>
    public static void Start(string what)
    {
        Since.Restart();
        Write(what);
    }

    public static void Write(string what) => Sink?.Invoke($"popup +{Since.ElapsedMilliseconds}ms: {what}");
}
