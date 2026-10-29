using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PrivatePaste.Protocol;
using PrivatePaste.Server.Clips;
using PrivatePaste.Server.Storage;

namespace PrivatePaste.Tests.Clips;

public class AutoDeleteServiceTests
{
    private static readonly DeviceIdentity Device = new(null, "test", "windows");

    private sealed record Rig(PrivatePasteFactory F, SettingsRepository Settings, ClipService Clips, AutoDeleteService Service)
    {
        // Backfills older than the auto-delete age are refused, so the setting is lifted while seeding old clips.
        public async Task<ClipDto> AddAsync(string text, TimeSpan age)
        {
            var days = await Settings.GetAutoDeleteDaysAsync();
            await Settings.SetAutoDeleteDaysAsync(null);
            try { return await Clips.AddTextAsync(new TextClipUpload(text, F.Clock.GetUtcNow() - age, true), Device); }
            finally { await Settings.SetAutoDeleteDaysAsync(days); }
        }

        public async Task<HashSet<string?>> TextsAsync() =>
            (await F.Services.GetRequiredService<ClipRepository>().ListAsync(null, null, 100)).Items.Select(c => c.Text).ToHashSet();
    }

    private static Rig NewRig(PrivatePasteFactory f)
    {
        _ = f.Server; // boots the host so services exist
        var sp = f.Services;
        var settings = sp.GetRequiredService<SettingsRepository>();
        var clips = sp.GetRequiredService<ClipService>();
        return new Rig(f, settings, clips,
            new AutoDeleteService(settings, clips, f.Clock, NullLogger<AutoDeleteService>.Instance));
    }

    [Fact]
    public async Task Off_deletes_nothing()
    {
        using var f = new PrivatePasteFactory();
        var r = NewRig(f);
        await r.AddAsync("ancient", TimeSpan.FromDays(400));

        Assert.Equal(0, await r.Service.RunOnceAsync());
        Assert.Contains("ancient", await r.TextsAsync());
    }

    [Fact]
    public async Task Thirty_days_deletes_old_unpinned_and_keeps_recent_and_pinned()
    {
        using var f = new PrivatePasteFactory();
        var r = NewRig(f);
        await r.Settings.SetAutoDeleteDaysAsync(30);
        await r.AddAsync("old", TimeSpan.FromDays(31));
        await r.AddAsync("recent", TimeSpan.FromDays(29));
        var pinned = await r.AddAsync("pinned", TimeSpan.FromDays(365));
        await r.Clips.SetPinnedAsync(pinned.Id, true);

        Assert.Equal(1, await r.Service.RunOnceAsync());
        Assert.Equal(new HashSet<string?> { "recent", "pinned" }, await r.TextsAsync());
    }

    [Fact]
    public async Task A_year_old_clip_unpinned_now_survives()
    {
        using var f = new PrivatePasteFactory();
        var r = NewRig(f);
        await r.Settings.SetAutoDeleteDaysAsync(30);
        var clip = await r.AddAsync("was pinned", TimeSpan.FromDays(365));
        await r.Clips.SetPinnedAsync(clip.Id, true);
        await r.Clips.SetPinnedAsync(clip.Id, false);

        Assert.Equal(0, await r.Service.RunOnceAsync());
        Assert.Contains("was pinned", await r.TextsAsync());
    }

    [Fact]
    public async Task Runs_at_start_and_again_every_hour()
    {
        using var f = new PrivatePasteFactory();
        var r = NewRig(f);
        await r.Settings.SetAutoDeleteDaysAsync(30);
        await r.AddAsync("first", TimeSpan.FromDays(40));

        await r.Service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(async () => !(await r.TextsAsync()).Contains("first"));

            await r.AddAsync("second", TimeSpan.FromDays(40));
            Assert.Contains("second", await r.TextsAsync());
            f.Clock.Advance(TimeSpan.FromHours(1));
            await WaitUntilAsync(async () => !(await r.TextsAsync()).Contains("second"));
        }
        finally { await r.Service.StopAsync(CancellationToken.None); }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await done())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met.");
            await Task.Delay(20);
        }
    }
}
