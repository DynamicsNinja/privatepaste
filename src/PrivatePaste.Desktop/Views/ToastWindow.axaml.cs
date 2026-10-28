using Avalonia.Controls;

namespace PrivatePaste.Desktop.Views;

public partial class ToastWindow : Window
{
    public ToastWindow() => InitializeComponent();

    public string Message
    {
        get => MessageText.Text ?? "";
        set => MessageText.Text = value;
    }
}
