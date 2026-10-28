using Avalonia.Controls;
using Avalonia.Input;
using PrivatePaste.Desktop.ViewModels;

namespace PrivatePaste.Desktop.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        // Enter or leaving the box applies the hotkey, like the Apply button.
        HotkeyBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            (DataContext as SettingsViewModel)?.ApplyHotkeyCommand.Execute(null);
        };
        HotkeyBox.LostFocus += (_, _) => (DataContext as SettingsViewModel)?.ApplyHotkeyCommand.Execute(null);
    }
}
