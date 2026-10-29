using Avalonia.Controls;
using Avalonia.Input;
using PrivatePaste.Desktop.ViewModels;

namespace PrivatePaste.Desktop.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        TitleBar.Prepare(this);
        // Enter or leaving the box applies the hotkey, like the Apply button.
        HotkeyBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            (DataContext as SettingsViewModel)?.ApplyHotkeyCommand.Execute(null);
        };
        AddAppButton.Click += (_, _) => ShowAddMenu();
        TypedAppBox.KeyDown += (_, e) =>
        {
            if (DataContext is not SettingsViewModel vm) return;
            if (e.Key == Key.Enter) vm.ConfirmTypedAppCommand.Execute(null);
            else if (e.Key == Key.Escape) vm.CancelTypingAppCommand.Execute(null);
            else return;
            e.Handled = true;
        };
        // Opening the "Type a name…" box puts the caret in it.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm)
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(SettingsViewModel.IsTypingApp) && vm.IsTypingApp)
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => TypedAppBox.Focus());
                };
        };
        HotkeyBox.LostFocus += (_, _) => (DataContext as SettingsViewModel)?.ApplyHotkeyCommand.Execute(null);
    }

    /// <summary>The "Add" menu: apps that copied recently and are not blocked yet, then "Type a name…".</summary>
    private void ShowAddMenu()
    {
        if (DataContext is not SettingsViewModel vm) return;
        var menu = new MenuFlyout();
        var recent = vm.AddableApps;
        foreach (var app in recent)
            menu.Items.Add(new MenuItem { Header = app, Command = vm.AddBlockedAppCommand, CommandParameter = app });
        if (recent.Count > 0) menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Type a name…", Command = vm.StartTypingAppCommand });
        menu.ShowAt(AddAppButton);
    }
}
