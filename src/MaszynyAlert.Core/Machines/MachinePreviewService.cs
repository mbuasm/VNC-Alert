using MaszynyAlert.Core.Abstractions;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Rfb;

namespace MaszynyAlert.Core.Machines;

/// <summary>Pełny obraz, czas jego lokalnego odczytu i pochodzenie, publikowane atomowo między wątkami.</summary>
public sealed record MachinePreviewFrame(RgbaImage Image, DateTimeOffset CapturedAt, long MonotonicMs, string Source);

public sealed class MachinePreviewService(
    Func<AppConfig> configuration,
    Func<string, MachinePreviewFrame?> localFrame,
    Func<AppConfig, MachineConfig, IScreenSource> openSource,
    IClock clock,
    Func<string, string?>? lastDesktopName = null,
    Func<AppConfig, MachineConfig, IScreenSource?>? testFile = null,
    Func<string, MachineStatusSnapshot?>? observedSnapshot = null)
{
    internal static readonly TimeSpan LocalFrameMaxAge = TimeSpan.FromSeconds(5);
    internal TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(12);

    public const string QtPreviewUnavailable =
        "Podgląd niedostępny: maszyna obsługuje jedno połączenie VNC, a monitoring tego komputera nie ma jeszcze obrazu " +
        "(w grupie podgląd tej maszyny działa na komputerze-obserwatorze).";

    public const string QtPreviewNoConnection =
        "Podgląd niedostępny: maszyna obsługuje jedno połączenie VNC, a monitoring tego komputera nie ma teraz połączenia z tą maszyną.";

    public const string QtPreviewConnecting =
        "Podgląd niedostępny: maszyna obsługuje jedno połączenie VNC, a monitoring tego komputera właśnie się z nią łączy — odśwież za chwilę.";

    /// <summary>Zegar limitu <see cref="Timeout"/>; testy podmieniają go, żeby limit minął dokładnie wtedy, kiedy chcą.</summary>
    internal TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public async Task<MachinePreviewFrame> CaptureAsync(string machineId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);
        ct.ThrowIfCancellationRequested();
        var config = configuration();
        if (!config.Enabled)
            throw new InvalidOperationException("Monitorowanie maszyn jest wyłączone.");

        var machine = config.Machines.FirstOrDefault(m => m.Id == machineId && m.Enabled)
            ?? throw new InvalidOperationException("Maszyna nie jest włączona w bieżącej konfiguracji.");
        var local = localFrame(machineId);
        if (IsSingleClientQt(machine))
        {
            if (local is not null)
            {
                EnsureConfigurationUnchanged(config);
                return local;
            }

            if (testFile?.Invoke(config, machine) is { } file)
                return await ReadOnceAsync(config, () => file, ct).ConfigureAwait(false);

            throw new InvalidOperationException(QtUnavailableMessage(observedSnapshot?.Invoke(machineId)));
        }

        if (local is not null && clock.MonotonicMs >= local.MonotonicMs
            && clock.MonotonicMs - local.MonotonicMs <= LocalFrameMaxAge.TotalMilliseconds)
        {
            EnsureConfigurationUnchanged(config);
            return local;
        }

        return await ReadOnceAsync(config, () => openSource(config, machine with { Mode = ConnectionMode.Persistent }), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Jeden odczyt pełnego ekranu z nowego źródła (limit <see cref="Timeout"/>); źródło zawsze zwalniane.</summary>
    private async Task<MachinePreviewFrame> ReadOnceAsync(AppConfig config, Func<IScreenSource> open, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(Timeout, TimeProvider);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await using var source = open();
            await source.ConnectAsync(deadline.Token).ConfigureAwait(false);
            var image = await source.CaptureFullAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            EnsureConfigurationUnchanged(config);
            return new(image, clock.UtcNow, clock.MonotonicMs,
                source.Info?.Kind == "plik" ? "plik" : "vnc-na-zadanie");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Nie udało się pobrać ekranu w ciągu 12 sekund.");
        }
    }

    private static string QtUnavailableMessage(MachineStatusSnapshot? observed) => observed switch
    {
        null => QtPreviewUnavailable,
        { State: MachineConnectionState.Error, LastError: { } error } when !string.IsNullOrWhiteSpace(error) =>
            QtPreviewNoConnection + " " + error.Trim(),
        { State: MachineConnectionState.Error } => QtPreviewNoConnection,
        _ => QtPreviewConnecting,
    };

    private bool IsSingleClientQt(MachineConfig machine) =>
        QtEmbeddedVncServer.IsQtEmbedded(lastDesktopName?.Invoke(machine.Id))
        || QtEmbeddedVncServer.IsQtProfile(machine.Profile);

    private void EnsureConfigurationUnchanged(AppConfig expected)
    {
        if (!ReferenceEquals(expected, configuration()))
            throw new InvalidOperationException("Ustawienia zmieniły się podczas pobierania. Odśwież podgląd.");
    }
}
