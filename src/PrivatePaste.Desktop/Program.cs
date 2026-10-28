using Avalonia;
using PrivatePaste.Desktop.Logging;

namespace PrivatePaste.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var options = StartupOptions.Parse(args, Environment.GetEnvironmentVariable);

        // Single instance per user (per data directory for isolated runs): a second launch exits at once.
        using var mutex = new Mutex(initiallyOwned: true, options.MutexName, out var createdNew);
        if (!createdNew) return 0;

        var log = new FileLog(Path.Combine(options.DataDirectory, "logs", "privatepaste.log"));
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.Error("Unhandled exception.", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.Error("Unobserved task exception.", e.Exception);
            e.SetObserved();
        };

        App.Options = options;
        App.Log = log;
        log.Info($"Starting (minimized: {options.Minimized}, isolated: {options.Isolated}).");
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            log.Info("Exited.");
            return 0;
        }
        catch (Exception e)
        {
            log.Error("Crashed.", e);
            return 1;
        }
    }

    // Also used by the Avalonia designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
