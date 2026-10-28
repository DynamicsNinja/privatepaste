using PrivatePaste.Client.Core.Api;
using PrivatePaste.Protocol;

namespace PrivatePaste.Desktop.Input;

/// <summary>
/// What happens when a popup row is picked (the popup is already hidden by the caller):
/// restore on the server (re-sync) -> write the clipboard through the coordinator -> refocus the previous window ->
/// 60 ms -> Ctrl+V (Cmd+V). Any failure toasts and sends NO keystroke, so nothing is ever pasted into the wrong place.
/// <para>
/// One deadline, <see cref="Deadline"/> from the pick, covers the restore, the coordinator's gate, the image download and
/// the write. Past it nothing is pasted (the user has moved on): a write that still completes toasts "Copied", anything
/// else "Couldn't copy the clip." Nothing is pasted either when the coordinator reports it wrote nothing.
/// </para>
/// <para>
/// Offline (HttpRequestException on restore): the cached clip is used without the restore. Paused: pause means nothing
/// leaves or enters the machine, so text/links are copied and pasted locally with no restore call and no coordinator
/// (which ignores clips while paused); images need a download and are refused with a toast.
/// </para>
/// </summary>
public sealed class PopupActivation(
    Func<bool> isSignedIn,
    Func<bool> isPaused,
    Func<Guid, CancellationToken, Task<ClipDto>> restore,
    Func<ClipDto, CancellationToken, Task<bool>> applyRemote,
    Func<string, CancellationToken, Task> writeLocalText,
    IPasteInjector injector,
    Action<string> toast,
    TimeProvider time,
    bool isMacOs)
{
    public const string AccessibilityToast = "Copied. Allow PrivatePaste in Accessibility settings to paste automatically.";
    public const string CopiedToast = "Copied";
    public const string CouldNotCopyToast = "Couldn't copy the clip.";
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan RestoreTimeout = TimeSpan.FromMilliseconds(2500);
    public static readonly TimeSpan PasteDelay = TimeSpan.FromMilliseconds(60);

    private bool _accessibilityToastShown;

    public async Task ActivateAsync(ClipDto clip, CancellationToken ct = default)
    {
        try
        {
            if (!isSignedIn())
            {
                toast("Signed out. Sign in to paste from history.");
                return;
            }

            if (isPaused())
            {
                if (clip.Kind == ClipKind.Image || clip.Text is null)
                {
                    toast("Sync is paused. Resume it to paste images.");
                    return;
                }
                await writeLocalText(clip.Text, ct);
            }
            else
            {
                using var deadline = new CancellationTokenSource(Deadline, time);
                using var picked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
                var wanted = clip;
                // A slow server must not leave the user waiting (or typing elsewhere): after 2.5 s use the cached copy, like offline.
                using var timeout = new CancellationTokenSource(RestoreTimeout, time);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(picked.Token, timeout.Token);
                try { wanted = await restore(clip.Id, linked.Token); }
                catch (HttpRequestException) { }   // unreachable: paste the cached copy; the restore is skipped
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !picked.IsCancellationRequested) { }

                bool written;
                try { written = await applyRemote(wanted, picked.Token); }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    toast(CouldNotCopyToast);   // never reached the clipboard in time
                    return;
                }
                if (!written)
                {
                    // Paused meanwhile, or nothing usable (e.g. an image that does not decode): the clipboard is stale.
                    toast(isPaused() ? "Sync was paused. Nothing was pasted." : CouldNotCopyToast);
                    return;
                }
                if (deadline.IsCancellationRequested)
                {
                    toast(CopiedToast);   // written, but too late to paste where the user was
                    return;
                }
            }

            // Copy-only unless we are sure where the paste would land: the window we came from must be known and still in front.
            if (!injector.RefocusPrevious())
            {
                toast(CopiedToast);
                return;
            }
            await Task.Delay(PasteDelay, time, ct);
            if (!injector.SendPaste() && isMacOs && !_accessibilityToastShown)
            {
                _accessibilityToastShown = true;
                toast(AccessibilityToast);
            }
        }
        catch (OperationCanceledException) { }
        catch (UnauthorizedApiException) { toast("Signed out. Sign in to paste from history."); }
        catch (ApiException e) when (e.Status == System.Net.HttpStatusCode.NotFound) { toast("That clip no longer exists."); }
        catch (Exception) { toast("Could not copy that item to the clipboard."); }
    }
}
