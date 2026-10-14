using MaszynyAlert.Core.Imaging;
using MaszynyAlert.Core.Rfb;

namespace MaszynyAlert.Probe.Commands;

public sealed class SharedTestCommand : ICommand
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private const int ObservationSeconds = 10;
    private const int ConnectSecondClientAtSecond = 2;

    public async Task<int> RunAsync(IReadOnlyList<string> args, GlobalOptions global, Report report, ProbeSession session, CancellationToken ct)
    {
        if (args.Count == 0)
        {
            throw new ProbeUsageException(
                "Brakuje adresu maszyny. Użycie: probe.exe shared-test <adres:port> " +
                "(np. probe.exe shared-test 192.168.1.108:9100).");
        }

        (string host, int port) = HostPortArg.Parse(args[0], "probe.exe shared-test 192.168.1.108:9100");
        report.Line($"Cel: {host}:{port}");
        report.Line();

        string machineId = $"{host}:{port}";

        report.Line("Łączę klienta A...");
        ConnectOutcome outcomeA = await session.Connections.ConnectAsync(machineId, host, host, port, ConnectTimeout, ct).ConfigureAwait(false);
        if (!outcomeA.Success)
        {
            report.Line($"Nie udało się połączyć klientem A: {outcomeA.SkipReason}");
            return 1;
        }

        await using RfbClient clientA = outcomeA.Client!;
        RfbServerInfo infoA = outcomeA.Info!;
        report.Line($"Klient A połączony: RFB {infoA.ProtocolVersion}, {infoA.Width}x{infoA.Height}.");

        var fullRect = new RectInt(0, 0, infoA.Width, infoA.Height);
        await clientA.CaptureAsync(fullRect, incremental: false, ct).ConfigureAwait(false);
        report.Line("Klient A pobrał pierwszą (pełną) klatkę — zaczynam co 1 s odświeżenia przyrostowe.");
        report.Line();

        bool firstDisconnected = false;
        bool secondRejected = false;
        int successA = 0;
        int successB = 0;
        RfbClient? clientB = null;

        for (int second = 1; second <= ObservationSeconds; second++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);

            if (!firstDisconnected)
            {
                try
                {
                    await clientA.CaptureAsync(fullRect, incremental: true, ct).ConfigureAwait(false);
                    successA++;
                }
                catch (RfbException ex)
                {
                    firstDisconnected = true;
                    report.Line($"[{second} s] klient A: serwer rozłączył połączenie — {ex.Message}");
                }
            }

            if (clientB is null && second >= ConnectSecondClientAtSecond)
            {
                report.Line($"[{second} s] łączę klienta B (drugi jednoczesny klient)...");
                ConnectOutcome outcomeB = await session.Connections.ConnectAsync(machineId, host, host, port, ConnectTimeout, ct).ConfigureAwait(false);
                if (outcomeB.Success)
                {
                    clientB = outcomeB.Client!;
                    report.Line($"[{second} s] klient B połączony.");
                    try
                    {
                        await clientB.CaptureAsync(fullRect, incremental: false, ct).ConfigureAwait(false);
                        successB++;
                    }
                    catch (RfbException ex)
                    {
                        report.Line($"[{second} s] klient B: rozłączony zaraz po połączeniu — {ex.Message}");
                    }
                }
                else
                {
                    secondRejected = true;
                    report.Line($"[{second} s] klient B odrzucony: {outcomeB.SkipReason}");
                }

                continue;
            }

            if (clientB is not null)
            {
                try
                {
                    await clientB.CaptureAsync(fullRect, incremental: true, ct).ConfigureAwait(false);
                    successB++;
                }
                catch (RfbException ex)
                {
                    report.Line($"[{second} s] klient B: serwer rozłączył połączenie — {ex.Message}");
                    await clientB.DisposeAsync().ConfigureAwait(false);
                    clientB = null;
                }
            }
        }

        if (clientB is not null)
        {
            await clientB.DisposeAsync().ConfigureAwait(false);
        }

        report.Line();
        report.Line("Wynik:");
        report.Line($"serwer rozłączył pierwszego klienta: {(firstDisconnected ? "TAK" : "NIE")}");
        report.Line($"drugi klient odrzucony: {(secondRejected ? "TAK" : "NIE")}");
        report.Line($"udane aktualizacje klienta A: {successA}");
        report.Line($"udane aktualizacje klienta B: {successB}");

        if (firstDisconnected)
        {
            report.Line("→ dla tej maszyny zalecany tryb polling.");
        }

        report.Line();
        report.Line(
            "Pytanie do Ciebie: czy okno RealVNC Viewer na tej maszynie zniknęło lub się rozłączyło " +
            "w czasie testu? Napisz to w odpowiedzi.");

        return 0;
    }
}
