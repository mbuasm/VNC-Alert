using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Machines;
using MaszynyAlert.Core.Rfb;

namespace MaszynyAlert.Core.Tests.TestUtil;

/// <summary>
/// Źródło obrazu ekranu na potrzeby testów: zwraca zadaną klatkę pełnego ekranu (ROI wycinany tak
/// samo jak w prawdziwym źródle) albo rzuca zadany wyjątek. Liczy wywołania Connect/Capture/Dispose.
/// </summary>
public sealed class FakeScreenSource : IScreenSource
{
    private int _connectCount;
    private int _captureCount;
    private int _captureFullCount;
    private int _tryCaptureCount;
    private int _disposeCount;

    public FakeScreenSource(string machineId, RgbaImage? frame = null)
    {
        MachineId = machineId;
        Frame = frame;
    }

    /// <inheritdoc />
    public string MachineId { get; }

    /// <summary>Pełna klatka „ekranu maszyny” zwracana przez to źródło (podmienialna w trakcie testu).</summary>
    public RgbaImage? Frame { get; set; }

    /// <summary>Gdy ustawione — <see cref="ConnectAsync"/> rzuca wyjątek zwrócony przez tę funkcję.</summary>
    public Func<Exception>? ConnectFailure { get; set; }

    /// <summary>Gdy ustawione — <see cref="CaptureAsync"/>/<see cref="CaptureFullAsync"/> rzucają wyjątek zwrócony przez tę funkcję.</summary>
    public Func<Exception>? CaptureFailure { get; set; }

    /// <summary>
    /// Gdy true — <see cref="TryCaptureAsync"/> zwraca <see langword="null"/> („serwer nie zgłosił żadnej
    /// zmiany w oknie oczekiwania”) zamiast pobierać obraz. Odpowiednik serwera WinCE/OpenCnc
    /// milczącego na żądanie przyrostowe, dopóki ekran się nie zmieni.
    /// </summary>
    public bool TryCaptureReturnsNull { get; set; }

    public string DesktopName { get; set; } = "test";

    /// <summary>Wołane na początku każdego <see cref="ConnectAsync"/> (np. zapis chwili próby połączenia na sztucznym zegarze).</summary>
    public Action? OnConnect { get; set; }

    /// <inheritdoc />
    public bool IsConnected { get; private set; }

    /// <inheritdoc />
    public ScreenSourceInfo? Info { get; private set; }

    public int ConnectCount => Volatile.Read(ref _connectCount);

    public int CaptureCount => Volatile.Read(ref _captureCount);

    public int CaptureFullCount => Volatile.Read(ref _captureFullCount);

    /// <summary>Ile razy wywołano <see cref="TryCaptureAsync"/> (żądanie przyrostowe z limitem bezczynności).</summary>
    public int TryCaptureCount => Volatile.Read(ref _tryCaptureCount);

    /// <summary>
    /// Obszary (ROI), o które prosił obserwator w kolejnych wywołaniach <see cref="CaptureAsync"/>
    /// i <see cref="TryCaptureAsync"/> — pozwala testom sprawdzić, że pobierany jest dokładnie ten
    /// prostokąt, o który chodzi (np. suma paska statusu i paska postępu przy obserwacji postoju).
    /// </summary>
    public List<RectInt> RequestedRois { get; } = [];

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    /// <summary>Fabryka wyjątku typowego dla zerwanego połączenia VNC.</summary>
    public static Func<Exception> RfbFailure(string message = "Nie udało się połączyć z maszyną (test).")
        => () => new RfbException(message);

    /// <summary>Fabryka wyjątku typowego dla chwilowo nieczytelnego pliku ekranu.</summary>
    public static Func<Exception> ScreenFailure(string message = "Nie można odczytać obrazu (test).")
        => () => new ScreenSourceException(message);

    /// <inheritdoc />
    public Task<ScreenSourceInfo> ConnectAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _connectCount);
        OnConnect?.Invoke();
        if (ConnectFailure is not null)
        {
            IsConnected = false;
            throw ConnectFailure();
        }

        RgbaImage frame = RequireFrame();
        IsConnected = true;
        Info = new ScreenSourceInfo("vnc", "3.8", "None", "32bpp BGRA LE", frame.Width, frame.Height, DesktopName);
        return Task.FromResult(Info);
    }

    /// <inheritdoc />
    public Task<RgbaImage> CaptureAsync(RectInt roi, bool incremental, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _captureCount);
        lock (RequestedRois)
        {
            RequestedRois.Add(roi);
        }

        ThrowIfCaptureFails();
        RgbaImage frame = RequireFrame();
        return Task.FromResult(frame.Crop(roi.Clamp(frame.Width, frame.Height)));
    }

    /// <inheritdoc />
    public async Task<RgbaImage?> TryCaptureAsync(RectInt roi, bool incremental, TimeSpan idleTimeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _tryCaptureCount);
        if (TryCaptureReturnsNull)
        {
            lock (RequestedRois)
            {
                RequestedRois.Add(roi);
            }

            ThrowIfCaptureFails();
            return null;
        }

        // Poza trybem „brak zmian” zachowuje się jak zwykłe pobranie ROI (i tak samo je liczy).
        return await CaptureAsync(roi, incremental, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<RgbaImage> CaptureFullAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _captureFullCount);
        ThrowIfCaptureFails();
        return Task.FromResult(RequireFrame());
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        IsConnected = false;
        return ValueTask.CompletedTask;
    }

    private void ThrowIfCaptureFails()
    {
        if (CaptureFailure is not null)
        {
            Exception failure = CaptureFailure();
            // Jak VncScreenSource: zmniejszony ekran (DesktopSize) nie zrywa sesji, każdy inny błąd — tak.
            if (failure is not RfbScreenResizedException) IsConnected = false;
            throw failure;
        }
    }

    private RgbaImage RequireFrame() =>
        Frame ?? throw new InvalidOperationException("FakeScreenSource: nie ustawiono klatki (Frame).");
}
