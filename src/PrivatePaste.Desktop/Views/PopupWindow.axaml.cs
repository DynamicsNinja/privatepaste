using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using PrivatePaste.Desktop.ViewModels;

namespace PrivatePaste.Desktop.Views;

/// <summary>
/// The history popup (420x480, borderless, topmost). Up/Down move, Enter pastes, Esc and losing focus close, a click pastes.
/// A new window is created for every opening, so no state survives between uses.
/// </summary>
public partial class PopupWindow : Window
{
    public const int WindowWidth = 420;
    public const int WindowHeight = 480;

    public static readonly IValueConverter ThumbnailConverter = new BytesToBitmapConverter();

    private bool _hasBeenActive;

    public PopupWindow()
    {
        InitializeComponent();

        // Tunnel: arrows and Enter are taken before the search box (which would move the caret / swallow Enter).
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        ItemsList.Tapped += OnItemsTapped;
        Opened += (_, _) => SearchBox.Focus();
        Activated += (_, _) => _hasBeenActive = true;
        // Losing focus closes, but only once we have had it (a hotkey-time activation can race with the OS).
        Deactivated += (_, _) => { if (_hasBeenActive && IsVisible) Close(); };
        DataContextChanged += (_, _) => Hook(DataContext as PopupViewModel);
        Closed += (_, _) => (DataContext as PopupViewModel)?.Cancel();
    }

    private void Hook(PopupViewModel? vm)
    {
        if (vm is null) return;
        vm.Items.CollectionChanged += OnItemsChanged;
        vm.PropertyChanged += OnViewModelChanged;
        UpdateEmpty(vm);
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmpty(DataContext as PopupViewModel);

    private void UpdateEmpty(PopupViewModel? vm)
    {
        if (vm is null) return;
        EmptyText.IsVisible = vm.Items.Count == 0;
        EmptyText.Text = vm.Query.Trim().Length == 0 ? "Nothing to paste yet." : "No matches.";
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PopupViewModel.SelectedIndex) && sender is PopupViewModel { SelectedIndex: >= 0 } vm)
            ItemsList.ScrollIntoView(vm.SelectedIndex);
        else if (e.PropertyName == nameof(PopupViewModel.Query)) UpdateEmpty(sender as PopupViewModel);
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
            case Key.Escape: Close(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void OnItemsTapped(object? sender, TappedEventArgs e)
    {
        // Only a click on a row (not the scrollbar or empty space) pastes. The press has already selected the row.
        if (e.Source is Visual v && v.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not null
            && DataContext is PopupViewModel vm)
            _ = vm.ActivateSelectedAsync();
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
        Show();
        Activate();
        // A hotkey-triggered window is often refused the foreground (Windows focus-stealing lock): force it so typing works.
        if (OperatingSystem.IsWindows() && TryGetPlatformHandle() is { } handle)
            Input.NativeWindows.ForceForeground(handle.Handle);
        SearchBox.Focus();
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
