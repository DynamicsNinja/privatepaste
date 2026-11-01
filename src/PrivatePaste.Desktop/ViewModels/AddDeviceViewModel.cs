using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivatePaste.Protocol;

namespace PrivatePaste.Desktop.ViewModels;

/// <summary>
/// "Add a device": a one-time QR code that hands this computer's vault key to a new device, shown until it expires
/// (10 minutes), with the time left. <see cref="QrText"/> holds the key that opens the handover: never log it.
/// </summary>
public sealed partial class AddDeviceViewModel : ObservableObject
{
    public const string Body = "Scan this with PrivatePaste on your phone. It works once, for 10 minutes.";
    public const string Failed = "Couldn't make a code. Check your connection.";
    public const string Expired = "That code has expired. Show a new one.";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly Func<CancellationToken, Task<(HandoverCreated Created, string QrText)>> _create;
    private readonly TimeProvider _time;
    private DateTimeOffset _expiresAt;

    public AddDeviceViewModel(Func<CancellationToken, Task<(HandoverCreated Created, string QrText)>> create, TimeProvider time)
    {
        _create = create;
        _time = time;
    }

    /// <summary>What the QR code shows; null while there is no live code (loading, failed or expired).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCode))]
    private string? _qrText;

    /// <summary>"9:59 left" while a code is shown.</summary>
    [ObservableProperty]
    private string? _timeLeft;

    /// <summary><see cref="Failed"/> or <see cref="Expired"/>; null while a code is shown or being made.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem), nameof(ButtonText))]
    private string? _problem;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ShowNewCodeCommand))]
    private bool _isLoading;

    public bool HasCode => QrText is not null;
    public bool HasProblem => Problem is not null;
    public string ButtonText => Problem == Failed ? "Try again" : "Show a new code";

    /// <summary>Asks the server for a new code. Never throws: a failure shows <see cref="Failed"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanShowNewCode))]
    private async Task ShowNewCodeAsync()
    {
        IsLoading = true;
        QrText = null;
        TimeLeft = null;
        Problem = null;
        try
        {
            var (created, qr) = await _create(CancellationToken.None);
            // The server's expiry, but never more than the lifetime from now (a client clock behind the server's).
            var latest = _time.GetUtcNow() + Lifetime;
            _expiresAt = created.ExpiresAt < latest ? created.ExpiresAt : latest;
            QrText = qr;
            Tick();
        }
        catch (Exception)
        {
            Problem = Failed;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanShowNewCode() => !IsLoading;

    /// <summary>Updates the time left (call every second); at 0 the code is hidden and <see cref="Expired"/> shown.</summary>
    public void Tick()
    {
        if (QrText is null) return;
        var left = _expiresAt - _time.GetUtcNow();
        if (left <= TimeSpan.Zero)
        {
            QrText = null;
            TimeLeft = null;
            Problem = Expired;
            return;
        }
        var seconds = (int)Math.Ceiling(left.TotalSeconds);
        TimeLeft = string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00} left");
    }
}
