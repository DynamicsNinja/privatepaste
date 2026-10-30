using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PrivatePaste.Desktop.ViewModels;

public enum SignInResult { Ok, WrongPassword, InvalidUrl, Unreachable, RateLimited, UpdateNeeded }

/// <summary>Result of a sign-in attempt. <see cref="Message"/> carries the <c>ServerUrl.Normalize</c> text for InvalidUrl.</summary>
public sealed record SignInOutcome(SignInResult Result, string? Message);

public sealed partial class SignInViewModel : ObservableObject
{
    public const string WrongPasswordMessage = "Wrong name or password.";
    public const string UpdateNeededMessage = "Update PrivatePaste to sign in.";
    public const string UnreachableMessage = "Can't reach the server.";
    public const string RateLimitedMessage = "Too many attempts. Wait a minute and try again.";

    private readonly Func<string, string, string, string, Task<SignInOutcome>> _signIn;

    /// <param name="signIn">(serverUrl, username, password, deviceName) → outcome.</param>
    /// <param name="lastUsername">The name last signed in with, to prefill the Name field.</param>
    public SignInViewModel(Func<string, string, string, string, Task<SignInOutcome>> signIn, string lastUsername = "")
    {
        _signIn = signIn;
        _username = lastUsername;
        _deviceName = Environment.MachineName;
    }

    /// <summary>Raised after a successful sign-in.</summary>
    public event EventHandler? SignedIn;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private string _serverUrl = "https://";

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private string _username;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private string _password = "";

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private string _deviceName;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private bool _isBusy;

    private bool CanSignIn() =>
        !IsBusy && !string.IsNullOrWhiteSpace(ServerUrl) && !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrEmpty(Password) && !string.IsNullOrWhiteSpace(DeviceName);

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        IsBusy = true;
        Error = null;
        SignInOutcome outcome;
        try
        {
            outcome = await _signIn(ServerUrl.Trim(), Username.Trim(), Password, DeviceName.Trim());
        }
        catch (Exception)
        {
            outcome = new SignInOutcome(SignInResult.Unreachable, null);
        }
        finally
        {
            IsBusy = false;
        }

        Error = outcome.Result switch
        {
            SignInResult.Ok => null,
            SignInResult.WrongPassword => WrongPasswordMessage,
            SignInResult.InvalidUrl => outcome.Message ?? "Enter a full server address starting with https://",
            SignInResult.RateLimited => RateLimitedMessage,
            SignInResult.UpdateNeeded => outcome.Message ?? UpdateNeededMessage,
            _ => UnreachableMessage,
        };
        if (outcome.Result == SignInResult.Ok)
        {
            Password = "";
            SignedIn?.Invoke(this, EventArgs.Empty);
        }
    }
}
