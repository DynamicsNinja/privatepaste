using PrivatePaste.Desktop.ViewModels;

namespace PrivatePaste.Tests.Client;

public class SignInViewModelTests
{
    private static SignInViewModel Filled(Func<string, string, string, Task<SignInOutcome>> signIn) =>
        new(signIn) { ServerUrl = "http://localhost:5299", Password = "dev", DeviceName = "PC-work" };

    [Fact]
    public void Defaults_prefill_https_and_machine_name()
    {
        var vm = new SignInViewModel((_, _, _) => Task.FromResult(new SignInOutcome(SignInResult.Ok, null)));
        Assert.Equal("https://", vm.ServerUrl);
        Assert.Equal(Environment.MachineName, vm.DeviceName);
        Assert.Null(vm.Error);
        Assert.False(vm.IsBusy);
    }

    [Theory]
    [InlineData("", "dev", "PC")]
    [InlineData("https://x", "", "PC")]
    [InlineData("https://x", "dev", " ")]
    public void Cannot_sign_in_with_an_empty_field(string url, string password, string device)
    {
        var vm = new SignInViewModel((_, _, _) => Task.FromResult(new SignInOutcome(SignInResult.Ok, null)))
        { ServerUrl = url, Password = password, DeviceName = device };
        Assert.False(vm.SignInCommand.CanExecute(null));
    }

    [Fact]
    public async Task Cannot_sign_in_while_busy_and_passes_the_fields()
    {
        var gate = new TaskCompletionSource<SignInOutcome>();
        (string, string, string)? args = null;
        var vm = Filled((u, p, d) => { args = (u, p, d); return gate.Task; });
        Assert.True(vm.SignInCommand.CanExecute(null));

        var run = vm.SignInCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);
        Assert.False(vm.SignInCommand.CanExecute(null));
        Assert.Equal(("http://localhost:5299", "dev", "PC-work"), args);

        gate.SetResult(new SignInOutcome(SignInResult.WrongPassword, null));
        await run;
        Assert.False(vm.IsBusy);
        Assert.True(vm.SignInCommand.CanExecute(null));
    }

    [Fact]
    public async Task Success_forgets_the_password()
    {
        var vm = Filled((_, _, _) => Task.FromResult(new SignInOutcome(SignInResult.Ok, null)));
        await vm.SignInCommand.ExecuteAsync(null);
        Assert.Equal("", vm.Password);
    }

    [Theory]
    [InlineData(SignInResult.WrongPassword, null, "Wrong password.")]
    [InlineData(SignInResult.InvalidUrl, "Enter a full server address starting with https://", "Enter a full server address starting with https://")]
    [InlineData(SignInResult.Unreachable, null, "Can't reach the server.")]
    [InlineData(SignInResult.RateLimited, null, "Too many attempts. Wait a minute and try again.")]
    public async Task Shows_the_exact_error(SignInResult result, string? message, string expected)
    {
        var vm = Filled((_, _, _) => Task.FromResult(new SignInOutcome(result, message)));
        await vm.SignInCommand.ExecuteAsync(null);
        Assert.Equal(expected, vm.Error);
    }

    [Fact]
    public async Task Success_clears_the_error_and_raises_signed_in()
    {
        var results = new Queue<SignInOutcome>([new(SignInResult.WrongPassword, null), new(SignInResult.Ok, null)]);
        var vm = Filled((_, _, _) => Task.FromResult(results.Dequeue()));
        var signedIn = 0;
        vm.SignedIn += (_, _) => signedIn++;

        await vm.SignInCommand.ExecuteAsync(null);
        Assert.Equal(0, signedIn);
        await vm.SignInCommand.ExecuteAsync(null);
        Assert.Null(vm.Error);
        Assert.Equal(1, signedIn);
    }

    [Fact]
    public async Task An_unexpected_exception_is_shown_as_unreachable()
    {
        var vm = Filled((_, _, _) => throw new InvalidOperationException("boom"));
        await vm.SignInCommand.ExecuteAsync(null);
        Assert.Equal("Can't reach the server.", vm.Error);
        Assert.False(vm.IsBusy);
    }
}
