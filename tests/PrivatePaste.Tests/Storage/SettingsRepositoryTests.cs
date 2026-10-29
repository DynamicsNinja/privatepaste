using Microsoft.Data.Sqlite;
using PrivatePaste.Server.Config;
using PrivatePaste.Server.Storage;

namespace PrivatePaste.Tests.Storage;

public sealed class SettingsRepositoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "privatepaste-settings-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsRepository _settings;

    public SettingsRepositoryTests()
    {
        var db = new PrivatePasteDb(new ServerOptions("key-0123456789abcdef", "pw", _dir, 1024, 1024));
        db.Initialize();
        _settings = new SettingsRepository(db);
    }

    [Fact]
    public async Task Defaults_are_off_and_never_cleared()
    {
        Assert.Null(await _settings.GetAutoDeleteDaysAsync());
        Assert.Null(await _settings.GetHistoryClearedAtAsync());
    }

    [Theory]
    [InlineData(7)]
    [InlineData(30)]
    [InlineData(90)]
    public async Task Auto_delete_days_round_trips_and_null_turns_it_off(int days)
    {
        await _settings.SetAutoDeleteDaysAsync(days);
        Assert.Equal(days, await _settings.GetAutoDeleteDaysAsync());

        await _settings.SetAutoDeleteDaysAsync(null);
        Assert.Null(await _settings.GetAutoDeleteDaysAsync());
    }

    [Fact]
    public async Task Other_auto_delete_days_are_rejected()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _settings.SetAutoDeleteDaysAsync(14));
    }

    [Fact]
    public async Task History_cleared_at_round_trips()
    {
        var at = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        await _settings.SetHistoryClearedAtAsync(at);
        Assert.Equal(at, await _settings.GetHistoryClearedAtAsync());

        await _settings.SetHistoryClearedAtAsync(at.AddDays(1));
        Assert.Equal(at.AddDays(1), await _settings.GetHistoryClearedAtAsync());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }
}
