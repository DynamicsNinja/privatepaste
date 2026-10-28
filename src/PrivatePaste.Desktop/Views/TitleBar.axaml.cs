using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace PrivatePaste.Desktop.Views;

/// <summary>Custom title bar: drag to move, double-click to maximize, minimize / maximize / close buttons.</summary>
public partial class TitleBar : UserControl
{
    private const string MaximizeGlyph = "M0.5,0.5 L9.5,0.5 L9.5,9.5 L0.5,9.5 Z";
    private const string RestoreGlyph = "M2.5,2.5 L9.5,2.5 L9.5,9.5 L2.5,9.5 Z M0.5,7.5 L0.5,0.5 L7.5,0.5 L7.5,2.5";

    private Window? _window;
    private IDisposable? _stateSub;
    private IDisposable? _titleSub;

    public TitleBar()
    {
        InitializeComponent();
        Bar.PointerPressed += OnBarPointerPressed;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is null) return;

        if (OperatingSystem.IsMacOS())
        {
            // Keep the native traffic lights; leave room for them and hide our own buttons.
            _window.WindowDecorations = WindowDecorations.Full;
            Buttons.IsVisible = false;
            TitlePanel.Margin = new Thickness(80, 0, 0, 0);
        }

        _titleSub = _window.GetObservable(Window.TitleProperty).Subscribe(new Observer<string?>(t => TitleText.Text = t));
        _stateSub = _window.GetObservable(Window.WindowStateProperty).Subscribe(new Observer<WindowState>(_ => UpdateMaximizeVisuals()));
        UpdateMaximizeVisuals();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _stateSub?.Dispose();
        _titleSub?.Dispose();
        _stateSub = _titleSub = null;
        _window = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void UpdateMaximizeVisuals()
    {
        var maximized = _window?.WindowState == WindowState.Maximized;
        MaximizeIcon.Data = Avalonia.Media.Geometry.Parse(maximized ? RestoreGlyph : MaximizeGlyph);
        MaximizeIcon.Classes.Set("restored", maximized);
        var text = maximized ? "Restore" : "Maximize";
        ToolTip.SetTip(MaximizeButton, text);
        Avalonia.Automation.AutomationProperties.SetName(MaximizeButton, text);
    }

    private void OnBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_window is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        // Clicks on the caption buttons are theirs.
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(true) is not null) return;

        if (e.ClickCount == 2) ToggleMaximize();
        else _window.BeginMoveDrag(e);
    }

    public void ToggleMaximize()
    {
        if (_window is null || !_window.CanResize) return;
        _window.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnMinimize(object? sender, RoutedEventArgs e)
    {
        if (_window is not null) _window.WindowState = WindowState.Minimized;
    }

    private void OnMaximize(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnClose(object? sender, RoutedEventArgs e) => _window?.Close();

    private sealed class Observer<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
