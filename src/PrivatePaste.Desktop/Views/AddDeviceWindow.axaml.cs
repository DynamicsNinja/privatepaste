using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PrivatePaste.Desktop.ViewModels;
using QRCoder;

namespace PrivatePaste.Desktop.Views;

/// <summary>
/// Shows a one-time code for a new device (<see cref="AddDeviceViewModel"/>): asks for one when opened, counts down every
/// second, and drops the code when closed.
/// </summary>
public partial class AddDeviceWindow : Window
{
    /// <summary>QR text → a black-on-white bitmap, one pixel per module (scaled up without smoothing by the Image).</summary>
    public static readonly IValueConverter QrConverter = new QrTextConverter();

    private readonly DispatcherTimer _tick;

    public AddDeviceWindow()
    {
        InitializeComponent();
        TitleBar.Prepare(this);
        _tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => (DataContext as AddDeviceViewModel)?.Tick());
        Opened += (_, _) =>
        {
            // Only a code that was never asked for: an expired or failed one waits for the button.
            if (DataContext is AddDeviceViewModel { HasCode: false, HasProblem: false, IsLoading: false } vm)
                vm.ShowNewCodeCommand.Execute(null);
            _tick.Start();
        };
        Closed += (_, _) =>
        {
            _tick.Stop();
            if (DataContext is AddDeviceViewModel vm) vm.QrText = null;   // the code holds the key to the handover
        };
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Activate();
    }

    /// <summary>The QR code of <paramref name="text"/> as a PNG with one pixel per module and no quiet zone (the card is it).</summary>
    public static byte[] QrPng(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(1, drawQuietZones: false);
    }

    private sealed class QrTextConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is string { Length: > 0 } text ? new Bitmap(new MemoryStream(QrPng(text))) : null;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
    }
}
