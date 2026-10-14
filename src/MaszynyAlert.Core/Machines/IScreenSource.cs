using MaszynyAlert.Core.Imaging;

namespace MaszynyAlert.Core.Machines;

public interface IScreenSource : IAsyncDisposable
{
    /// <summary>Identyfikator maszyny, do której należy to źródło.</summary>
    string MachineId { get; }

    /// <summary>Czy ostatnia próba połączenia/odczytu się powiodła.</summary>
    bool IsConnected { get; }

    /// <summary>Informacje o źródle z ostatniego udanego połączenia/odczytu (null przed pierwszym udanym <see cref="ConnectAsync"/>).</summary>
    ScreenSourceInfo? Info { get; }

    /// <summary>Nawiązuje połączenie (VNC) albo sprawdza dostępność pliku (tryb testowy) i zwraca zebrane informacje.</summary>
    Task<ScreenSourceInfo> ConnectAsync(CancellationToken ct);

    /// <summary>Pobiera obraz wskazanego obszaru (ROI), przycięty do granic ekranu/pliku.</summary>
    /// <param name="incremental">
    /// Dla VNC w trybie Persistent: przekazywane jako flaga "incremental" do <c>FramebufferUpdateRequest</c>
    /// (tylko zmiany od ostatniej aktualizacji). Ignorowane przez źródła, dla których nie ma to znaczenia
    /// (tryb Polling — zawsze pełne pobranie; źródło plikowe).
    /// </param>
    Task<RgbaImage> CaptureAsync(RectInt roi, bool incremental, CancellationToken ct);

    /// <summary>Pobiera obraz całego ekranu/pliku (zawsze pełna klatka, nigdy przyrostowa).</summary>
    Task<RgbaImage> CaptureFullAsync(CancellationToken ct);

    async Task<RgbaImage?> TryCaptureAsync(RectInt roi, bool incremental, TimeSpan idleTimeout, CancellationToken ct) =>
        await CaptureAsync(roi, incremental, ct).ConfigureAwait(false);
}
