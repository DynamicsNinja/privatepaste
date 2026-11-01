using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivatePaste.Client.Core.Vault;

namespace PrivatePaste.Desktop.ViewModels;

/// <summary>Where this computer stands with the server's end-to-end encryption (see <see cref="DesktopHost.VaultState"/>).</summary>
public enum VaultState { Unknown, ServerTooOld, UpdateRequired, NeedsOwnerSetup, WaitingForOwner, Locked, Unlocked }

/// <summary>The encryption copy, the same on the desktop and Android. Never the server's own text.</summary>
public static class VaultCopy
{
    public const string UnlockTitle = "Unlock your history";
    public const string SetupTitle = "Set up encryption";
    public const string WrongPassphrase = "That passphrase doesn't unlock this server's history.";
    /// <summary>Change passphrase: the current one didn't open the server's wrap (the web Settings page says the same).</summary>
    public const string WrongCurrentPassphrase = "The current passphrase is wrong.";
    public const string Unsupported = "This server's encryption settings aren't supported. Update PrivatePaste.";
    public const string UnlockFailed = "Couldn't unlock. Try again.";
    public const string SetupFailed = "Couldn't set up encryption. Try again.";
    public const string SetUpElsewhere = "Encryption was already set up on another device. Enter its passphrase.";
    public const string Mismatch = "The passphrases don't match.";
    public const string ServerTooOld = "Update your PrivatePaste server to keep syncing.";
    public const string UpdateRequired = "Update PrivatePaste to keep syncing.";
    public const string WaitingForOwner = "Ask the owner to set up encryption.";
    public const string WasReset = "Encryption was reset. Enter the new passphrase.";
    public const string OwnerOnly = "Only the owner can change the passphrase.";
    public const string ChangeFailed = "Couldn't change the passphrase. Try again.";
    public const string SaveFailed = "Couldn't save the key on this computer. Try again.";
    public const string PassphraseChanged = "Passphrase changed.";

    /// <summary>
    /// What to tell the user about <paramref name="state"/> (tray, popup banner), or null when there is nothing to say
    /// there: unlocked, not known yet, or a state the unlock window handles.
    /// </summary>
    public static string? For(VaultState state, bool keyWasReset) => state switch
    {
        VaultState.ServerTooOld => ServerTooOld,
        VaultState.UpdateRequired => UpdateRequired,
        VaultState.WaitingForOwner => WaitingForOwner,
        VaultState.Locked when keyWasReset => WasReset,
        _ => null,
    };

    /// <summary>The local checks of a new passphrase (no server, no Argon2): the rule first, then the repeat.</summary>
    public static string? NewPassphraseProblem(string passphrase, string repeat) =>
        PassphraseWrap.Problem(passphrase) ?? (passphrase == repeat ? null : Mismatch);
}

public enum UnlockMode { Unlock, Setup }

/// <summary>
/// The unlock window: the passphrase (Unlock), or a new passphrase twice with the rule (Setup, the owner only). The
/// host does the work and returns the copy to show; the new passphrase is checked here first, without a round trip.
/// Never log what is typed here.
/// </summary>
public sealed partial class UnlockViewModel : ObservableObject
{
    private readonly Func<string, Task<string?>> _unlock;
    private readonly Func<string, string, Task<string?>> _setUp;

    /// <param name="unlock">passphrase → null when unlocked, otherwise the copy to show.</param>
    /// <param name="setUp">(passphrase, repeat) → null when set up, otherwise the copy to show.</param>
    public UnlockViewModel(Func<string, Task<string?>> unlock, Func<string, string, Task<string?>> setUp, UnlockMode mode,
        string? message = null)
    {
        _unlock = unlock;
        _setUp = setUp;
        _mode = mode;
        _message = message;
    }

    /// <summary>Raised once the vault is unlocked or set up.</summary>
    public event EventHandler? Done;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(ButtonText), nameof(IsSetup))]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private UnlockMode _mode;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private string _passphrase = "";

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private string _repeat = "";

    /// <summary>Why the last attempt failed.</summary>
    [ObservableProperty]
    private string? _error;

    /// <summary>Context shown above the fields, such as <see cref="VaultCopy.WasReset"/>.</summary>
    [ObservableProperty]
    private string? _message;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private bool _isBusy;

    public string Title => Mode == UnlockMode.Setup ? VaultCopy.SetupTitle : VaultCopy.UnlockTitle;
    public string ButtonText => Mode == UnlockMode.Setup ? "Set up" : "Unlock";
    public bool IsSetup => Mode == UnlockMode.Setup;
    public static string Rule => PassphraseWrap.RuleMessage;

    /// <summary>The host's state moved on while the window is open (e.g. set up elsewhere: unlock instead). Keeps the error.</summary>
    public void Show(UnlockMode mode, string? message)
    {
        Mode = mode;
        Message = message;
    }

    private bool CanSubmit() => !IsBusy && Passphrase.Length > 0 && (Mode == UnlockMode.Unlock || Repeat.Length > 0);

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SubmitAsync()
    {
        var setup = Mode == UnlockMode.Setup;
        if (setup && VaultCopy.NewPassphraseProblem(Passphrase, Repeat) is { } problem)
        {
            Error = problem;
            return;
        }
        IsBusy = true;
        Error = null;
        string? result;
        try
        {
            result = setup ? await _setUp(Passphrase, Repeat) : await _unlock(Passphrase);
        }
        catch (Exception)
        {
            result = setup ? VaultCopy.SetupFailed : VaultCopy.UnlockFailed;
        }
        finally
        {
            IsBusy = false;
        }
        if (result is not null)
        {
            Error = result;
            return;
        }
        Passphrase = "";
        Repeat = "";
        Done?.Invoke(this, EventArgs.Empty);
    }
}
