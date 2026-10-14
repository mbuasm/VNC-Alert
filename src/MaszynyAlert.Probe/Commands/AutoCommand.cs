using System.Runtime.Versioning;

namespace MaszynyAlert.Probe.Commands;

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class AutoCommand : ICommand
{
    internal const int DefaultBusPort = 47777;

    public async Task<int> RunAsync(IReadOnlyList<string> args, GlobalOptions global, Report report, ProbeSession session, CancellationToken ct)
    {
        int? seconds = ParseOptionalSeconds(args);
        int intervalMinutes = ParseIntervalMinutes(args);
        bool noZip = ArgsUtil.HasFlag(args, "--no-zip");
        bool noBus = ArgsUtil.HasFlag(args, "--no-bus");
        int busPort = ParsePort(args);

        session.ZipRequested = !noZip;
        session.IsAutoRun = true;

        report.Line("probe.exe auto — rozpoznanie jednym poleceniem: info -> ocr -> watch, po kolei, aż do końca.");
        report.Line("  KROK 1/3: info    — rozpoznanie wszystkich maszyn (wersja RFB, zabezpieczenia, rozmiar ekranu, zrzuty).");
        report.Line("  KROK 2/3: ocr     — sprawdzenie OCR i samoczynne znalezienie pola postępu / nazwy pliku pracy.");
        report.Line(
            "  KROK 3/3: watch   — obserwacja aż do Ctrl+C" +
            (seconds is { } sPreview ? $" (albo automatycznie po {sPreview} s — podano --seconds)" : " (zostaw okno otwarte na całą zmianę)") + ".");
        report.Line(
            noBus
                ? "  Nasłuch testu sieci między komputerami: WYŁĄCZONY (podano --no-bus)."
                : $"  Nasłuch testu sieci między komputerami: włączony w tle przez cały przebieg (port UDP {busPort}).");
        report.Line(
            noZip
                ? "  Spakowanie folderu wynikowego do zip na koniec: WYŁĄCZONE (podano --no-zip)."
                : "  Spakowanie folderu wynikowego do zip na koniec: włączone.");
        report.Line();
        report.Line("Hasła VNC zostaną zapytane tylko RAZ na cały ten przebieg (ta sama maszyna nie zapyta o hasło drugi raz w kroku 2 ani 3).");
        report.Line();

        if (!noBus)
        {
            session.Bus = await BusListener.TryStartAsync(busPort, report, ct).ConfigureAwait(false);
        }

        try
        {
            report.Line("=== KROK 1/3: probe.exe info ===");
            report.Line();
            await InfoCommand.RunCoreAsync([], global, report, session, ct).ConfigureAwait(false);
            report.Line();

            report.Line("=== KROK 2/3: probe.exe ocr ===");
            report.Line();
            await OcrCommand.RunCoreAsync([], global, report, session, ct).ConfigureAwait(false);
            report.Line();

            report.Line("=== KROK 3/3: probe.exe watch ===");
            report.Line();
            List<string> watchArgs = [];
            if (seconds is { } secondsValue)
            {
                watchArgs.Add("--seconds");
                watchArgs.Add(secondsValue.ToString());
            }

            if (intervalMinutes != WatchCommand.DefaultIntervalMinutes)
            {
                watchArgs.Add("--interval-min");
                watchArgs.Add(intervalMinutes.ToString());
            }

            int watchExit = await WatchCommand.RunCoreAsync(watchArgs, global, report, session, ct).ConfigureAwait(false);
            return watchExit;
        }
        finally
        {
            if (session.Bus is not null)
            {
                await session.Bus.StopAsync().ConfigureAwait(false);
                session.Bus.PrintSummary(report);
            }
        }
    }

    internal static int? ParseOptionalSeconds(IReadOnlyList<string> args)
    {
        string? text = ArgsUtil.FindOption(args, "--seconds");
        if (text is null)
        {
            return null;
        }

        if (!int.TryParse(text, out int seconds) || seconds <= 0)
        {
            throw new ProbeUsageException("Opcja --seconds musi być dodatnią liczbą całkowitą (sekundy).");
        }

        return seconds;
    }

    internal static int ParseIntervalMinutes(IReadOnlyList<string> args)
    {
        string? text = ArgsUtil.FindOption(args, "--interval-min");
        if (text is null)
        {
            return WatchCommand.DefaultIntervalMinutes;
        }

        if (!int.TryParse(text, out int minutes) || minutes <= 0)
        {
            throw new ProbeUsageException("Opcja --interval-min musi być dodatnią liczbą całkowitą (minuty).");
        }

        return minutes;
    }

    internal static int ParsePort(IReadOnlyList<string> args)
    {
        string? text = ArgsUtil.FindOption(args, "--port");
        if (text is null)
        {
            return DefaultBusPort;
        }

        if (!int.TryParse(text, out int port) || port is < 1 or > 65535)
        {
            throw new ProbeUsageException("Opcja --port musi być liczbą 1-65535.");
        }

        return port;
    }
}
