using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PrivatePaste.Desktop.Views;

/// <summary>Unlock with the passphrase, or set one up (the owner). An ordinary focused window: typing needs the focus.</summary>
public partial class UnlockWindow : Window
{
    public UnlockWindow()
    {
        InitializeComponent();
        TitleBar.Prepare(this);
        Opened += (_, _) => PassphraseBox.Focus();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Activate();
    }
}
