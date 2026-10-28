using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using PrivatePaste.Desktop.Tray;

namespace PrivatePaste.Desktop.Views;

/// <summary>The Night vault toast: either a one-line message or a "clip arrived" card. Shown without taking focus.</summary>
public partial class ToastWindow : Window
{
    public ToastWindow() => InitializeComponent();

    /// <summary>Shows a plain message (and hides the clip card).</summary>
    public string Message
    {
        get => MessageText.Text ?? "";
        set
        {
            ClipCard = null;
            MessageText.Text = value;
            MessagePanel.IsVisible = true;
            ClipPanel.IsVisible = false;
        }
    }

    /// <summary>The clip card currently shown; null while showing a message.</summary>
    public ClipToast? ClipCard { get; private set; }

    /// <summary>Shows the "clip arrived" card (replacing whatever was shown).</summary>
    public void ShowClip(ClipToast toast)
    {
        ClipCard = toast;
        ClipTitle.Text = toast.Title;
        ClipPreview.Text = toast.Preview;
        ClipPreview.IsVisible = toast.Preview.Length > 0;
        ClipPlatformPath.Data = Application.Current?.TryFindResource(toast.Platform.ResourceKey, out var g) == true
            ? g as Geometry
            : null;
        AutomationProperties.SetName(ClipPlatformIcon, toast.Platform.Label);
        MessagePanel.IsVisible = false;
        ClipPanel.IsVisible = true;
    }
}
