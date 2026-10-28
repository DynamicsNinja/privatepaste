using PrivatePaste.Client.Core.Api;
using PrivatePaste.Protocol;

namespace PrivatePaste.Desktop.Input;

/// <summary>
/// What happens when a popup row is picked (the popup is already hidden by the caller):
/// restore on the server (re-sync) -> write the clipboard through the coordinator -> refocus the previous window ->
/// 60 ms -> Ctrl+V (Cmd+V). Any failure toasts and sends NO keystroke, so nothing is ever pasted into the wrong place.
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
    Func<ClipDto, CancellationToken, Task> applyRemote,
    Func<string, CancellationToken, Task> writeLocalText,
    IPasteInjector injector,
    Action<string> toast,
    TimeProvider time,
    bool isMacOs)
{
    public const string AccessibilityToast = "Copied. Allow PrivatePaste in Accessibility settings to paste automatically.";
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
                var wanted = clip;
                try { wanted = await restore(clip.Id, ct); }
                catch (HttpRequestException) { }   // unreachable: paste the cached copy; the restore is skipped
                await applyRemote(wanted, ct);
                // ApplyRemote silently does nothing if sync was paused meanwhile; the clipboard would be stale.
                if (isPaused())
                {
                    toast("Sync was paused. Nothing was pasted.");
                    return;
                }
            }

            injector.RefocusPrevious();
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
