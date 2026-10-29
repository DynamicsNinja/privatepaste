using PrivatePaste.Server.Storage;

namespace PrivatePaste.Server.Clips;

/// <summary>Hourly sweep that removes unpinned clips older than the "auto-delete after N days" setting.</summary>
public sealed class AutoDeleteService(SettingsRepository settings, ClipService clips, TimeProvider time,
    ILogger<AutoDeleteService> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Never log clip contents; the exception type and message are enough to diagnose.
                log.LogError(ex, "Auto-delete run failed; retrying in an hour.");
            }

            try { await Task.Delay(Interval, time, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task<int> RunOnceAsync()
    {
        if (await settings.GetAutoDeleteDaysAsync() is not { } days)
            return 0;

        var removed = await clips.DeleteOlderThanAsync(time.GetUtcNow().AddDays(-days));
        if (removed > 0)
            log.LogInformation("Auto-delete removed {Count} clips.", removed);
        return removed;
    }
}
