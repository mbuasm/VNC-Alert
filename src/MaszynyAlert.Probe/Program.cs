using System.IO.Compression;
using System.Text;
using MaszynyAlert.Core.Config;
using MaszynyAlert.Core.Rfb;
using MaszynyAlert.Probe;
using MaszynyAlert.Probe.Commands;

TrySetUtf8Console();

using var cts = new CancellationTokenSource();
ConsoleCancelEventHandler? cancelHandler = (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine();
    Console.WriteLine("Przerwano (Ctrl+C) — kończę i zapisuję, co się da...");
    cts.Cancel();
};
Console.CancelKeyPress += cancelHandler;

string commandName = args.Length > 0 ? args[0].Trim() : "";
string commandKey = commandName.ToLowerInvariant();
string[] rest = args.Length > 0 ? args[1..] : [];

if (args.Length == 0 || commandKey is "help" or "--help" or "-h" or "/?")
{
    HelpCommand.Run();
    Console.CancelKeyPress -= cancelHandler;
    return 0;
}

Report? report = null;
GlobalOptions? globalForZip = null;
ProbeSession? session = null;
try
{
    (GlobalOptions global, List<string> remaining) = ArgsUtil.ExtractGlobal(rest);
    globalForZip = global;

    ICommand? command = commandKey switch
    {
        "info" => new InfoCommand(),
        "shared-test" => new SharedTestCommand(),
        "ocr" => new OcrCommand(),
        "watch" => new WatchCommand(),
        "auto" => new AutoCommand(),
        "bus-test" => new BusTestCommand(),
        "capture" => new CaptureCommand(),
        "diagnoza" => new DiagnozaCommand(),
        "haslo" => new HasloCommand(),
        _ => null,
    };

    if (command is null)
    {
        HelpCommand.Run(commandName);
        return 1;
    }

    report = new Report(global.OutDir, commandKey);
    report.Line($"probe {VersionInfo.InformationalVersion} — {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
    report.Line($"Wywołanie: probe {ArgsUtil.MaskForReport(args)}");
    report.Line($"Folder wyjściowy: {Path.GetFullPath(global.OutDir)}");
    report.Line();

    session = new ProbeSession(global);
    int exitCode = await command.RunAsync(remaining, global, report, session, cts.Token).ConfigureAwait(false);

    report.Line();
    report.Line($"Raport: {report.Path}");
    return exitCode;
}
catch (OperationCanceledException)
{
    Console.WriteLine("Przerwano przez użytkownika.");
    report?.FileOnly("Przerwano przez użytkownika (Ctrl+C).");
    if (report is not null)
    {
        report.Line($"Raport: {report.Path}");
    }

    // Ctrl+C po udanym starcie = ten sam kod, co normalne zakończenie (0/3/4 wg tego, które z wybranych maszyn
    // zdążyły się połączyć) — przebieg przerwany ręcznie nadal mówi, co się udało. Bez żadnego wiersza (przerwanie
    // przed wczytaniem listy maszyn) zostaje 0.
    return session is { Summary.Rows.Count: > 0 } ? ProbeExitCodes.FromSummary(session.Summary) : 0;
}
catch (ProbeUsageException ex)
{
    Console.Error.WriteLine($"Błąd: {ex.Message}");
    report?.FileOnly($"BŁĄD UŻYCIA: {ex.Message}");
    return 1;
}
catch (ConfigException ex)
{
    Console.Error.WriteLine($"Błąd konfiguracji: {ex.Message}");
    report?.FileOnly($"BŁĄD KONFIGURACJI: {ex.Message}");
    return 1;
}
catch (RfbException ex)
{
    Console.Error.WriteLine($"Błąd połączenia VNC: {ex.Message}");
    report?.FileOnly($"BŁĄD RFB: {ex.Message}");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"Wystąpił nieoczekiwany błąd: {ex.Message}");
    Console.Error.WriteLine("Szczegóły techniczne zapisano do raportu (jeśli udało się go otworzyć).");
    report?.FileOnly($"BŁĄD: {ex.Message}");
    report?.FileOnly($"szczegóły techniczne: {ex}");
    return 1;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
    report?.Dispose();

    // (auto): spakuj folder wynikowy do zip, PO zamknięciu raport.txt (żeby trafił do zipa
    // domknięty). Błąd zipowania nie może zmienić kodu wyjścia programu na błąd — tylko komunikat (patrz TryZipOutDir).
    if (session is { ZipRequested: true } && globalForZip is not null)
    {
        TryZipOutDir(globalForZip.OutDir);
    }
}

static void TryZipOutDir(string outDir)
{
    try
    {
        string fullOutDir = Path.GetFullPath(outDir);
        if (!Directory.Exists(fullOutDir))
        {
            return;
        }

        string zipPath = OutputZip.BuildZipPath(fullOutDir, DateTimeOffset.Now);

        ZipFile.CreateFromDirectory(fullOutDir, zipPath);

        Console.WriteLine();
        Console.WriteLine($"Spakowano folder wynikowy do: {zipPath}");
        Console.WriteLine("Ten plik prześlij.");
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
    {
        Console.WriteLine();
        Console.WriteLine($"Nie udało się spakować folderu wynikowego do zip: {ex.Message}");
        Console.WriteLine(
            "Nic straconego — folder \"out\" zawiera te same dane, możesz go spakować ręcznie " +
            "(prawy klik na folder → Kompresuj do pliku ZIP).");
    }
}

static void TrySetUtf8Console()
{
    try
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }
    catch
    {
        // Konsola przekierowana / brak wsparcia (np. w testach) — kontynuujemy z domyślnym kodowaniem.
    }

    try
    {
        Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }
    catch
    {
        // j.w.
    }
}
