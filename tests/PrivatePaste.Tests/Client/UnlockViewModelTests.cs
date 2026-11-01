using Microsoft.Extensions.Time.Testing;
using PrivatePaste.Client.Core.Vault;
using PrivatePaste.Desktop.ViewModels;
using PrivatePaste.Protocol;

namespace PrivatePaste.Tests.Client;

public class UnlockViewModelTests
{
    private const string Passphrase = "my cat eats lemons daily";
    private readonly List<string> _unlockCalls = [];
    private readonly List<(string, string)> _setupCalls = [];
    private string? _answer;

    private UnlockViewModel Create(UnlockMode mode, string? message = null) => new(
        p => { _unlockCalls.Add(p); return Task.FromResult(_answer); },
        (p, r) => { _setupCalls.Add((p, r)); return Task.FromResult(_answer); },
        mode, message);

    [Fact]
    public async Task Unlock_WrongPassphraseShowsCopy()
    {
        var vm = Create(UnlockMode.Unlock);
        var done = 0;
        vm.Done += (_, _) => done++;
        _answer = VaultCopy.WrongPassphrase;
        vm.Passphrase = "not it";

        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.Equal("That passphrase doesn't unlock this server's history.", vm.Error);
        Assert.Equal(["not it"], _unlockCalls);
        Assert.Equal(0, done);
        Assert.Equal("not it", vm.Passphrase);   // kept, to fix a typo

        _answer = null;
        await vm.SubmitCommand.ExecuteAsync(null);
        Assert.Equal(1, done);
        Assert.Null(vm.Error);
        Assert.Equal("", vm.Passphrase);
    }

    [Fact]
    public async Task Setup_MismatchedRepeatShows()
    {
        var vm = Create(UnlockMode.Setup);
        vm.Passphrase = Passphrase;
        vm.Repeat = Passphrase + " ";

        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.Equal("The passphrases don't match.", vm.Error);
        Assert.Empty(_setupCalls);   // checked here, before any Argon2 or server call
    }

    [Fact]
    public async Task Setup_ShortShowsRule()
    {
        var vm = Create(UnlockMode.Setup);
        vm.Passphrase = "short";
        vm.Repeat = "different";

        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.Equal("Use at least 12 characters. A short sentence works well.", vm.Error);
        Assert.Equal(PassphraseWrap.RuleMessage, UnlockViewModel.Rule);
        Assert.Empty(_setupCalls);
    }

    [Fact]
    public async Task Setup_passes_both_fields_and_a_throwing_host_shows_plain_copy()
    {
        var vm = new UnlockViewModel(_ => Task.FromResult<string?>(null),
            (_, _) => throw new HttpRequestException("boom"), UnlockMode.Setup);
        vm.Passphrase = Passphrase;
        vm.Repeat = Passphrase;

        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.Equal("Couldn't set up encryption. Try again.", vm.Error);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Modes_title_button_and_show()
    {
        var vm = Create(UnlockMode.Setup);
        Assert.Equal("Set up encryption", vm.Title);
        Assert.Equal("Set up", vm.ButtonText);
        Assert.True(vm.IsSetup);
        vm.Error = VaultCopy.SetUpElsewhere;

        vm.Show(UnlockMode.Unlock, VaultCopy.WasReset);   // set up elsewhere meanwhile: unlock instead

        Assert.Equal("Unlock your history", vm.Title);
        Assert.Equal("Unlock", vm.ButtonText);
        Assert.False(vm.IsSetup);
        Assert.Equal("Encryption was reset. Enter the new passphrase.", vm.Message);
        Assert.Equal(VaultCopy.SetUpElsewhere, vm.Error);   // the reason stays
    }

    [Fact]
    public void Submit_needs_the_fields()
    {
        var vm = Create(UnlockMode.Setup);
        Assert.False(vm.SubmitCommand.CanExecute(null));
        vm.Passphrase = Passphrase;
        Assert.False(vm.SubmitCommand.CanExecute(null));
        vm.Repeat = Passphrase;
        Assert.True(vm.SubmitCommand.CanExecute(null));
    }

    [Fact]
    public void Vault_copy_for_states()
    {
        Assert.Equal("Update your PrivatePaste server to keep syncing.", VaultCopy.For(VaultState.ServerTooOld, false));
        Assert.Equal("Update PrivatePaste to keep syncing.", VaultCopy.For(VaultState.UpdateRequired, false));
        Assert.Equal("Ask the owner to set up encryption.", VaultCopy.For(VaultState.WaitingForOwner, false));
        Assert.Equal("Encryption was reset. Enter the new passphrase.", VaultCopy.For(VaultState.Locked, true));
        Assert.Null(VaultCopy.For(VaultState.Locked, false));
        Assert.Null(VaultCopy.For(VaultState.Unlocked, false));
        Assert.Null(VaultCopy.For(VaultState.Unknown, false));
    }

    // ---- Add a device ----

    [Fact]
    public async Task Add_device_shows_the_code_counts_down_and_expires()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        var calls = 0;
        var vm = new AddDeviceViewModel(_ =>
        {
            calls++;
            return Task.FromResult((new HandoverCreated(Guid.NewGuid(), time.GetUtcNow().AddMinutes(10)), "privatepaste:handover?x"));
        }, time);

        await vm.ShowNewCodeCommand.ExecuteAsync(null);

        Assert.True(vm.HasCode);
        Assert.Equal("10:00 left", vm.TimeLeft);
        time.Advance(TimeSpan.FromSeconds(61));
        vm.Tick();
        Assert.Equal("8:59 left", vm.TimeLeft);
        time.Advance(TimeSpan.FromMinutes(9));
        vm.Tick();
        Assert.False(vm.HasCode);
        Assert.Null(vm.QrText);
        Assert.Equal("That code has expired. Show a new one.", vm.Problem);
        Assert.Equal("Show a new code", vm.ButtonText);

        await vm.ShowNewCodeCommand.ExecuteAsync(null);
        Assert.Equal(2, calls);
        Assert.True(vm.HasCode);
        Assert.Null(vm.Problem);
    }

    [Fact]
    public async Task Add_device_failure_shows_plain_copy_and_a_server_expiry_past_ten_minutes_is_capped()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        var fail = true;
        var vm = new AddDeviceViewModel(_ => fail
            ? throw new HttpRequestException("offline")
            : Task.FromResult((new HandoverCreated(Guid.NewGuid(), time.GetUtcNow().AddHours(1)), "qr")), time);

        await vm.ShowNewCodeCommand.ExecuteAsync(null);
        Assert.Equal("Couldn't make a code. Check your connection.", vm.Problem);
        Assert.Equal("Try again", vm.ButtonText);
        Assert.False(vm.HasCode);

        fail = false;
        await vm.ShowNewCodeCommand.ExecuteAsync(null);
        Assert.Equal("10:00 left", vm.TimeLeft);
    }
}
