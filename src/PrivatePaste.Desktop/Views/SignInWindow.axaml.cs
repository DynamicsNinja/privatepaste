using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PrivatePaste.Desktop.Views;

public partial class SignInWindow : Window
{
    public SignInWindow()
    {
        InitializeComponent();
        TitleBar.Prepare(this);
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        // Put the caret after the prefilled "https://" so the user can type the host straight away.
        ServerBox.Focus();
        ServerBox.CaretIndex = ServerBox.Text?.Length ?? 0;
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Activate();
    }
}
