using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PrivatePaste.Server.Storage;

/// <summary>Server-wide settings: a small name/value table.</summary>
public sealed class SettingsRepository(PrivatePasteDb db)
{
    private const string AutoDeleteDays = "auto_delete_days";
    private const string HistoryClearedAt = "history_cleared_at";

    private static readonly int[] AllowedAutoDeleteDays = [7, 30, 90];

    public async Task<int?> GetAutoDeleteDaysAsync() =>
        await GetAsync(AutoDeleteDays) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : null;

    /// <summary>Null turns auto-delete off; otherwise 7, 30 or 90 days.</summary>
    public async Task SetAutoDeleteDaysAsync(int? days)
    {
        if (days is { } d && !AllowedAutoDeleteDays.Contains(d))
            throw new ArgumentOutOfRangeException(nameof(days), days, "Auto-delete is off or 7, 30 or 90 days.");
        if (days is null)
            await DeleteAsync(AutoDeleteDays);
        else
            await SetAsync(AutoDeleteDays, days.Value.ToString(CultureInfo.InvariantCulture));
    }

    public async Task<DateTimeOffset?> GetHistoryClearedAtAsync() =>
        await GetAsync(HistoryClearedAt) is { } v
            ? DateTimeOffset.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            : null;

    public Task SetHistoryClearedAtAsync(DateTimeOffset at) =>
        SetAsync(HistoryClearedAt, at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));

    public async Task<string?> GetAsync(string name)
    {
        await using var conn = db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE name = @n";
        cmd.Parameters.AddWithValue("@n", name);
        return await cmd.ExecuteScalarAsync() as string;
    }

    public async Task SetAsync(string name, string value)
    {
        await using var conn = db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO settings(name, value) VALUES(@n, @v) " +
                          "ON CONFLICT(name) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("@n", name);
        cmd.Parameters.AddWithValue("@v", value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task DeleteAsync(string name)
    {
        await using var conn = db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM settings WHERE name = @n";
        cmd.Parameters.AddWithValue("@n", name);
        await cmd.ExecuteNonQueryAsync();
    }
}
