using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;

namespace MaszynyAlert.App.Views;

/// <summary>Jednorazowy podgląd pełnego ekranu, także na komputerze odbierającym alerty bez lokalnego monitora.</summary>
public partial class ScreenPreviewWindow : Window
{
    private readonly MachinePreviewService _preview;
    private readonly string _machineId;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _closed;
    internal Task RefreshCompletion { get; private set; } = Task.CompletedTask;

    public ScreenPreviewWindow(MachinePreviewService preview, string machineId, string machineName)
    {
        InitializeComponent();

        _preview = preview ?? throw new ArgumentNullException(nameof(preview));
        _machineId = machineId ?? throw new ArgumentNullException(nameof(machineId));
        Title = $"VNC Alert — podgląd: {machineName}";

        Loaded += (_, _) => RefreshCompletion = RefreshImageAsync();
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshCompletion = RefreshImageAsync();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private async Task RefreshImageAsync()
    {
        RefreshButton.IsEnabled = false;
        PreviewImage.Source = null;
        StatusText.Text = "Pobieranie ekranu…";
        try
        {
            var frame = await _preview.CaptureAsync(_machineId, _lifetime.Token);
            if (_closed) return;
            PreviewImage.Source = ToBitmapSource(frame.Image);
            string origin = frame.Source switch
            {
                "plik" => "TEST Z PLIKU — nie jest to ekran maszyny",
                "vnc" => "lokalna obserwacja",
                _ => "odczyt VNC na żądanie",
            };
            StatusText.Text = $"Obraz z {frame.CapturedAt.ToLocalTime():HH:mm:ss} · {origin}\n"
                + $"{frame.Image.Width} × {frame.Image.Height} px · Pojedynczy obraz — Odśwież pobiera kolejny.";
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (TimeoutException) { if (!_closed) StatusText.Text = "Przekroczono czas pobierania. Sprawdź połączenie z maszyną i spróbuj ponownie."; }
        catch (InvalidOperationException ex) { if (!_closed) StatusText.Text = ex.Message; }
        catch (Exception) { if (!_closed) StatusText.Text = "Nie można pobrać ekranu. Sprawdź lokalne hasło VNC, połączenie i ustawienia maszyny."; }
        finally { if (!_closed) RefreshButton.IsEnabled = true; }
    }

    /// <summary>
    /// RgbaImage trzyma piksele w kolejności R,G,B,A; WriteableBitmap.Bgra32 oczekuje B,G,R,A — stąd zamiana kanałów R↔B.
    /// </summary>
    private static BitmapSource ToBitmapSource(RgbaImage image)
    {
        var bitmap = new WriteableBitmap(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null);
        byte[] src = image.Pixels;
        byte[] bgra = new byte[src.Length];
        for (int i = 0; i < src.Length; i += 4)
        {
            bgra[i] = src[i + 2];
            bgra[i + 1] = src[i + 1];
            bgra[i + 2] = src[i];
            bgra[i + 3] = src[i + 3];
        }

        bitmap.WritePixels(new Int32Rect(0, 0, image.Width, image.Height), bgra, image.Width * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }
}
