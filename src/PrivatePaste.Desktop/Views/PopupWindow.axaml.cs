using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PrivatePaste.Desktop.ViewModels;

namespace PrivatePaste.Desktop.Views;

/// <summary>
/// The history popup (420x480, borderless, topmost). Up/Down move, Enter pastes, Esc and losing focus close, a click pastes.
/// A new window is created for every opening, so no state survives between uses.
/// <para>
/// On Windows it never takes the focus (<see cref="KeepsFocusBelow"/>): a text box in the app below stays in edit mode
/// (Explorer's address bar, a rename box, inline editors end editing when they lose the focus), so the paste lands where
/// the user was. The keyboard hook hands it the keys instead (<see cref="HandleKey"/>), and it closes when the user
/// clicks outside it or switches to another window. On macOS and Linux it is an ordinary focused window.
/// </para>
/// </summary>
public partial class PopupWindow : Window
{
    public const int WindowWidth = 420;
    public const int WindowHeight = 480;
    private static readonly TimeSpan WatchInterval = TimeSpan.FromMilliseconds(30);

    /// <summary>True on Windows: the popup is shown without taking the focus and is driven by <see cref="HandleKey"/>.</summary>
    public bool KeepsFocusBelow { get; } = OperatingSystem.IsWindows();

    private DispatcherTimer? _watch;

    public static readonly IValueConverter ThumbnailConverter = new BytesToBitmapConverter();

    /// <summary>Looks up an application resource (e.g. a platform icon geometry) by its key.</summary>
    public static readonly IValueConverter ResourceConverter = new FuncValueConverter<string?, object?>(key =>
        key is not null && Application.Current?.TryFindResource(key, out var value) == true ? value : null);

    private bool _hasBeenActive;

    public PopupWindow()
    {
        InitializeComponent();

        // Tunnel: arrows and Enter are taken before the search box (which would move the caret / swallow Enter).
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        ItemsList.Tapped += OnItemsTapped;
        AddHandler(Button.ClickEvent, OnPinClick);
        HistoryTabButton.PointerPressed += (_, _) => (DataContext as PopupViewModel)?.ShowTab(PopupTab.History);
        PinnedTabButton.PointerPressed += (_, _) => (DataContext as PopupViewModel)?.ShowTab(PopupTab.Pinned);
        if (KeepsFocusBelow)
        {
            // Nothing in it may take the keyboard focus: a focus request would try to activate the window.
            SearchBox.Focusable = false;
            SearchBox.IsHitTestVisible = false;
            SearchBox.Classes.Add("typing");
            ItemsList.Focusable = false;
            Closed += (_, _) => _watch?.Stop();
        }
        else Opened += (_, _) => SearchBox.Focus();
        Activated += (_, _) =>
        {
            _hasBeenActive = true;
            Input.PopupTrace.Write("popup activated");
        };
        // Losing focus closes, but only once we have had it (a hotkey-time activation can race with the OS).
        Deactivated += (_, _) =>
        {
            Input.PopupTrace.Write($"popup deactivated{(_hasBeenActive && IsVisible ? ": closing" : " before it was active: kept")}"
                + (OperatingSystem.IsWindows() ? $", foreground {Input.NativeWindows.Describe(Input.NativeWindows.GetForegroundWindow())}" : ""));
            if (_hasBeenActive && IsVisible) Close();
        };
        Closed += (_, _) => Input.PopupTrace.Write("popup closed");
        DataContextChanged += (_, _) => Hook(DataContext as PopupViewModel);
        Closed += (_, _) => (DataContext as PopupViewModel)?.Cancel();
    }

    private void Hook(PopupViewModel? vm)
    {
        if (vm is null) return;
        vm.Items.CollectionChanged += OnItemsChanged;
        vm.PropertyChanged += OnViewModelChanged;
        ApplyTab(vm);
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmpty(DataContext as PopupViewModel);

    private void UpdateEmpty(PopupViewModel? vm)
    {
        if (vm is null) return;
        EmptyText.IsVisible = vm.Items.Count == 0;
        EmptyText.Text = vm.Query.Trim().Length > 0 ? "No matches."
            : vm.Tab == PopupTab.Pinned ? "Nothing pinned yet." : "Nothing to paste yet.";
    }

    private void ApplyTab(PopupViewModel vm)
    {
        SearchBox.PlaceholderText = vm.Tab == PopupTab.Pinned ? "Search pinned" : "Search history";
        UpdateEmpty(vm);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PopupViewModel.SelectedIndex) && sender is PopupViewModel { SelectedIndex: >= 0 } vm)
            ItemsList.ScrollIntoView(vm.SelectedIndex);
        else if (e.PropertyName == nameof(PopupViewModel.Query)) UpdateEmpty(sender as PopupViewModel);
        else if (e.PropertyName == nameof(PopupViewModel.Tab) && sender is PopupViewModel tabbed) ApplyTab(tabbed);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not PopupViewModel vm) return;
        switch (e.Key)
        {
            case Key.Down: vm.MoveSelection(1); break;
            case Key.Up: vm.MoveSelection(-1); break;
            case Key.PageDown: vm.MoveSelection(5); break;
            case Key.PageUp: vm.MoveSelection(-5); break;
            case Key.Enter: _ = vm.ActivateSelectedAsync(); break;
            case Key.Tab: vm.SwitchTab(); break;
            case Key.P when e.KeyModifiers == KeyModifiers.Control: _ = vm.TogglePinSelectedAsync(); break;
            case Key.Escape:
                Input.PopupTrace.Write("Esc");
                Close();
                break;
            default: return;
        }
        e.Handled = true;
    }

    private void OnItemsTapped(object? sender, TappedEventArgs e)
    {
        // Only a click on a row (not the scrollbar or empty space) pastes. The press has already selected the row.
        if (e.Source is Visual v && v.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not null
            && v.FindAncestorOfType<Button>(includeSelf: true) is null   // the pin button toggles, it does not paste
            && DataContext is PopupViewModel vm)
            _ = vm.ActivateSelectedAsync();
    }

    private void OnPinClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Button { DataContext: PopupItem item } button && button.Classes.Contains("pin")
            && DataContext is PopupViewModel vm)
        {
            e.Handled = true;
            _ = vm.TogglePinAsync(item);
        }
    }

    /// <summary>Shows the popup with its top-left corner at the cursor, kept inside that screen's working area.</summary>
    public void ShowAt(PixelPoint cursor)
    {
        var screen = Screens.ScreenFromPoint(cursor) ?? Screens.Primary;
        if (screen is not null)
        {
            var scale = screen.Scaling;
            var size = new PixelSize((int)Math.Ceiling(WindowWidth * scale), (int)Math.Ceiling(WindowHeight * scale));
            Position = Place(cursor, size, screen.WorkingArea);
        }
        if (KeepsFocusBelow)
        {
            ShowWithoutFocus(screen);
            return;
        }
        Show();
        Activate();
        // A hotkey-triggered window is often refused the foreground (Windows focus-stealing lock): force it so typing works.
        if (OperatingSystem.IsWindows() && TryGetPlatformHandle() is { } handle)
        {
            Input.NativeWindows.ForceForeground(handle.Handle);
            var foreground = Input.NativeWindows.GetForegroundWindow();
            Input.PopupTrace.Write($"shown at {Position} on screen {screen?.Bounds}, foreground "
                + (foreground == handle.Handle ? "is the popup" : Input.NativeWindows.Describe(foreground)));
        }
        SearchBox.Focus();
    }

    private void ShowWithoutFocus(Screen? screen)
    {
        ShowActivated = false;
        Show();
        if (TryGetPlatformHandle() is not { } handle) return;
        var hwnd = handle.Handle;
        Input.NativeWindows.MakeNonActivatingTopmost(hwnd);
        var below = Input.NativeWindows.GetForegroundWindow();
        Input.PopupTrace.Write($"shown without focus at {Position} on screen {screen?.Bounds}, focus stays in "
            + Input.NativeWindows.Describe(below));

        // No Deactivated event without activation: close when the user clicks outside or switches to another window.
        _watch = new DispatcherTimer(WatchInterval, DispatcherPriority.Input, (_, _) =>
        {
            var foreground = Input.NativeWindows.GetForegroundWindow();
            string? reason = foreground != below && foreground != hwnd ? $"switched to {Input.NativeWindows.Describe(foreground)}"
                : Input.NativeWindows.IsMouseButtonDown() && !Input.NativeWindows.IsCursorOver(hwnd) ? "clicked outside"
                : null;
            if (reason is null) return;
            Input.PopupTrace.Write($"closing: {reason}");
            Close();
        });
        _watch.Start();
    }

    /// <summary>A key the hook took while the popup is open without the focus (UI thread).</summary>
    public void HandleKey(Input.PopupKey key)
    {
        if (DataContext is not PopupViewModel vm) return;
        switch (key.Action)
        {
            case Input.PopupKeyAction.Up: vm.MoveSelection(-1); break;
            case Input.PopupKeyAction.Down: vm.MoveSelection(1); break;
            case Input.PopupKeyAction.PageUp: vm.MoveSelection(-5); break;
            case Input.PopupKeyAction.PageDown: vm.MoveSelection(5); break;
            case Input.PopupKeyAction.Paste: _ = vm.ActivateSelectedAsync(); break;
            case Input.PopupKeyAction.SwitchTab: vm.SwitchTab(); break;
            case Input.PopupKeyAction.TogglePin: _ = vm.TogglePinSelectedAsync(); break;
            case Input.PopupKeyAction.Close:
                Input.PopupTrace.Write("Esc");
                Close();
                break;
            case Input.PopupKeyAction.Backspace: vm.Backspace(); break;
            case Input.PopupKeyAction.Type when key.Text is { } text: vm.Type(text); break;
        }
    }

    /// <summary>The top-left for a window of <paramref name="size"/> opened at <paramref name="cursor"/>, kept inside <paramref name="work"/>.</summary>
    public static PixelPoint Place(PixelPoint cursor, PixelSize size, PixelRect work)
    {
        var x = Math.Max(work.X, Math.Min(cursor.X, work.Right - size.Width));
        var y = Math.Max(work.Y, Math.Min(cursor.Y, work.Bottom - size.Height));
        return new PixelPoint(x, y);
    }

    private sealed class BytesToBitmapConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not byte[] { Length: > 0 } bytes) return null;
            try { return new Bitmap(new MemoryStream(bytes)); }
            catch (Exception) { return null; }
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
    }
}
