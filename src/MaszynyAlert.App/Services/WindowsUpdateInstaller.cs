using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using MaszynyAlert.Core.Updates;
using Microsoft.Extensions.Logging;

namespace MaszynyAlert.App.Services;

public sealed class WindowsUpdateInstaller : IUpdateInstaller
{
    /// <summary>Instalacja zakończona i program uruchomiony ponownie.</summary>
    public const int ExitInstalled = 0;

    /// <summary>Okno UAC anulowane albo odrzucone.</summary>
    public const int ExitCancelledByUser = 3;

    /// <summary>Instalator zakończył się kodem różnym od 0.</summary>
    public const int ExitSetupFailed = 4;

    /// <summary>Inny błąd pomocnika (brak pliku, nieudane uruchomienie).</summary>
    public const int ExitHelperFailed = 5;

    /// <summary>Po tym czasie bez wyniku pomocnik najpewniej czeka na okno UAC — okno aktualizacji podpowiada je potwierdzić.</summary>
    public static readonly TimeSpan ConsentHintAfter = TimeSpan.FromSeconds(20);

    /// <summary>Nazwa skryptu pomocnika w katalogu pobrań.</summary>
    public const string ScriptFileName = "install-update.ps1";

    private readonly string _workDirectory;
    private readonly string _appPath;
    private readonly ILogger<WindowsUpdateInstaller> _logger;
    private readonly string? _adminDirectory;
    private readonly Func<bool> _taskRegistered = () => false;
    private readonly string _taskName = UpdateTaskContract.TaskName;

    /// <param name="workDirectory">Katalog pobrań (<c>state\updates</c>) — tu lądują skrypt, log instalatora i plik wyniku.</param>
    /// <param name="appPath">Pełna ścieżka <c>MaszynyAlert.App.exe</c> uruchamianego po instalacji.</param>
    public WindowsUpdateInstaller(string workDirectory, string appPath, ILogger<WindowsUpdateInstaller> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(appPath);
        _workDirectory = workDirectory;
        _appPath = appPath;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public WindowsUpdateInstaller(string workDirectory, string appPath, ILogger<WindowsUpdateInstaller> logger,
        string adminDirectory, Func<bool> taskRegistered, string taskName = UpdateTaskContract.TaskName)
        : this(workDirectory, appPath, logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adminDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);
        _adminDirectory = adminDirectory;
        _taskRegistered = taskRegistered ?? throw new ArgumentNullException(nameof(taskRegistered));
        _taskName = taskName;
    }

    /// <summary>Czy instalować przez zadanie „VNC Alert update” (bez UAC). Błąd sprawdzenia = ścieżka z UAC.</summary>
    private bool UseTask()
    {
        if (_adminDirectory is null) return false;
        try { return _taskRegistered(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nie sprawdzono zadania „{Task}” — instalacja przez okno UAC.", _taskName);
            return false;
        }
    }

    /// <summary>
    /// Pomocnik działa dłużej niż <see cref="ConsentHintAfter"/> — najpewniej czeka na potwierdzenie okna UAC.
    /// Argument to podpowiedź po polsku dla okna aktualizacji. Zgłaszane z wątku tła.
    /// </summary>
    public event Action<string>? ConsentPending;

    /// <summary>Treść podpowiedzi zgłaszanej przez <see cref="ConsentPending"/>.</summary>
    public static readonly string ConsentHint =
        "Potwierdź okno Windows (UAC), żeby zainstalować nową wersję. Jeśli okna nie widać, kliknij migający przycisk "
        + "„Kontrola konta użytkownika” na pasku zadań. Do tego czasu program działa dalej na obecnej wersji.";

    /// <inheritdoc />
    public async Task<(InstallLaunchResult Result, string? Error)> LaunchAsync(string setupPath, Version version, string expectedSha256, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setupPath);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        if (!File.Exists(setupPath))
            return (InstallLaunchResult.Failed, $"Brak pobranego instalatora: {setupPath}");

        try
        {
            string actual;
            using (FileStream stream = File.OpenRead(setupPath))
                actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return (InstallLaunchResult.Failed,
                    $"Pobrany instalator ma inną sumę SHA-256 niż podana w spisie wydań ({actual} zamiast {expectedSha256}) — pliku nie uruchomiono.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (InstallLaunchResult.Failed, "Nie odczytano pobranego instalatora: " + ex.Message);
        }

        bool useTask = UseTask();
        string script, log, result;
        string? requestId = null, taskResult = null;
        try
        {
            Directory.CreateDirectory(_workDirectory);
            script = Path.Combine(_workDirectory, ScriptFileName);
            log = useTask
                ? Path.Combine(_adminDirectory!, UpdateTaskContract.SetupLogFileName(version))
                : Path.Combine(_workDirectory, $"install-{version.ToString(4)}.log");
            result = Path.Combine(_workDirectory, $"install-{version.ToString(4)}.result.json");
            // Skrypt jest zapisywany przy każdej instalacji: treść zawsze pochodzi z wersji, która instaluje. Leży w katalogu
            // operatora — w trybie zadania nie podnosi uprawnień, więc jego podmiana nie daje nic ponad konto operatora.
            File.WriteAllText(script, Script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            if (File.Exists(result)) File.Delete(result);
            if (useTask)
            {
                requestId = Guid.NewGuid().ToString("N");
                taskResult = Path.Combine(_adminDirectory!, UpdateTaskContract.TaskResultFileName);
                File.WriteAllText(Path.Combine(_workDirectory, UpdateTaskContract.RequestFileName),
                    new UpdateTaskRequest(requestId, Path.GetFileName(setupPath)).ToJson());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (InstallLaunchResult.Failed, "Nie przygotowano pomocnika instalacji: " + ex.Message);
        }

        // Okno UAC dostaje pierwszy plan tylko wtedy, gdy proces, który o nie prosi, może przejąć pierwszy plan.
        // Po kliknięciu w oknie aktualizacji to nasz program ma pierwszy plan — oddajemy prawo dalej, żeby
        // monit nie pojawił się jedynie jako migający przycisk na pasku zadań. Bez kliknięcia (instalacja
        // automatyczna) wywołanie nic nie zmienia i niczemu nie szkodzi.
        if (!useTask) AllowSetForegroundWindow(AsfwAny);

        var start = new ProcessStartInfo(PowerShellPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Katalog roboczy poza Program Files: otwarty katalog bieżący pomocnika nie może blokować podmiany plików.
            WorkingDirectory = _workDirectory,
        };
        foreach (string argument in new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
            "-Setup", setupPath, "-App", _appPath, "-Log", log, "-Result", result, "-Sha256", expectedSha256.ToUpperInvariant(),
        })
        {
            start.ArgumentList.Add(argument);
        }
        if (useTask)
        {
            foreach (string argument in new[] { "-TaskName", _taskName, "-TaskResult", taskResult!, "-RequestId", requestId! })
                start.ArgumentList.Add(argument);
        }

        int helperPid;
        try
        {
            using Process launcher = Process.Start(start) ?? throw new InvalidOperationException("Nie uruchomiono powershell.exe.");
            Task<string> stdout = launcher.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderr = launcher.StandardError.ReadToEndAsync(ct);
            await launcher.WaitForExitAsync(ct).ConfigureAwait(false);
            string output = await stdout.ConfigureAwait(false);
            string errors = await stderr.ConfigureAwait(false);
            string? pidLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            if (launcher.ExitCode != 0 || !int.TryParse(pidLine, out helperPid))
            {
                return (InstallLaunchResult.Failed,
                    $"Pomocnik instalacji nie wystartował (kod {launcher.ExitCode}). {Shorten(errors)}".Trim());
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (InstallLaunchResult.Failed, "Nie uruchomiono pomocnika instalacji: " + ex.Message);
        }

        _logger.LogInformation(useTask
                ? "Uruchomiono pomocnika instalacji wersji {Version} (proces {Pid}) — instalacja przez zadanie „VNC Alert update”, bez UAC; log instalatora: {Log}."
                : "Uruchomiono pomocnika instalacji wersji {Version} (proces {Pid}); log instalatora: {Log}.",
            version.ToString(4), helperPid, log);

        await WaitForHelperAsync(helperPid, consentHint: !useTask, ct).ConfigureAwait(false);
        return ReadResult(result, log);
    }

    /// <summary>
    /// Czeka na koniec drugiego etapu. Anulowanie (zamykanie programu — zwykle właśnie przez instalator)
    /// przerywa tylko czekanie: pomocnika NIE zabijamy, bo to on dokończy instalację i uruchomi program.
    /// </summary>
    private async Task WaitForHelperAsync(int pid, bool consentHint, CancellationToken ct)
    {
        Process helper;
        try { helper = Process.GetProcessById(pid); }
        catch (ArgumentException) { return; } // już się zakończył — wynik jest w pliku
        using (helper)
        {
            Task exited = helper.WaitForExitAsync(ct);
            Task first = await Task.WhenAny(exited, Task.Delay(ConsentHintAfter, ct)).ConfigureAwait(false);
            // W trybie zadania nie ma okna UAC — podpowiedź „potwierdź okno Windows” byłaby fałszywa.
            if (consentHint && first != exited && !helper.HasExited)
            {
                _logger.LogInformation("Pomocnik instalacji czeka dłużej niż {Seconds} s — najpewniej na potwierdzenie okna UAC.",
                    (int)ConsentHintAfter.TotalSeconds);
                try { ConsentPending?.Invoke(ConsentHint); }
                catch (Exception ex) { _logger.LogWarning(ex, "Błąd przy pokazywaniu podpowiedzi UAC."); }
            }
            await exited.ConfigureAwait(false);
        }
    }

    private (InstallLaunchResult Result, string? Error) ReadResult(string resultPath, string log)
    {
        HelperResult? result = null;
        try
        {
            if (File.Exists(resultPath))
                result = JsonSerializer.Deserialize<HelperResult>(File.ReadAllText(resultPath), ResultJson);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Nie odczytano wyniku pomocnika instalacji {Path}.", resultPath);
        }

        if (result is null)
            return (InstallLaunchResult.Failed, $"Pomocnik instalacji zakończył się bez wyniku — log instalatora: {log}");

        _logger.LogInformation("Pomocnik instalacji zakończył się kodem {Code} (instalator: {SetupCode}).",
            result.ExitCode, result.SetupExitCode);
        return result.ExitCode switch
        {
            ExitInstalled => (InstallLaunchResult.Started, null),
            ExitCancelledByUser => (InstallLaunchResult.CancelledByUser, "Okno Windows (UAC) zostało anulowane."),
            ExitSetupFailed => (InstallLaunchResult.Failed,
                $"Instalator zakończył się kodem {result.SetupExitCode?.ToString() ?? "?"} — log instalatora: {log}"),
            UpdateTaskContract.ExitRejected => (InstallLaunchResult.Failed,
                $"Zadanie aktualizacji odrzuciło instalację (nic nie uruchomiono). {result.Message}".Trim()),
            _ => (InstallLaunchResult.Failed,
                $"Pomocnik instalacji zakończył się kodem {result.ExitCode}. {result.Message} — log instalatora: {log}".Trim()),
        };
    }

    private static string Shorten(string text)
    {
        text = text.Trim();
        return text.Length <= 300 ? text : text[..300] + "…";
    }

    private const int AsfwAny = -1;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    private static readonly JsonSerializerOptions ResultJson = new() { PropertyNameCaseInsensitive = true };

    private sealed record HelperResult(int ExitCode, int? SetupExitCode, string? FinishedAt, string? Message);

    /// <summary>Windows PowerShell 5.1 z katalogu systemowego — nie z PATH, żeby nikt nie podstawił własnego.</summary>
    private static string PowerShellPath =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>
    /// Treść <c>install-update.ps1</c>. Zgodna z Windows PowerShell 5.1, bez modułów zewnętrznych. Bez
    /// <c>-Detached</c> to etap pierwszy (uruchamia drugi i wypisuje jego PID), z <c>-Detached</c> — drugi.
    /// </summary>
    internal const string Script = """
        # VNC Alert - pomocnik instalacji aktualizacji. Plik generuje program przy kazdej instalacji - nie edytuj.
        # Etap 1 (bez -Detached): uruchamia etap 2 poza drzewem procesow programu i wypisuje jego PID.
        # Etap 2 (-Detached): z -TaskName wyzwala zadanie "VNC Alert update" (SYSTEM, bez UAC) i czeka na jego wynik;
        # bez -TaskName - instalator z UAC (/VERYSILENT). Potem ponowne uruchomienie programu na koncie operatora.
        # Kody: 0 zainstalowano, 3 UAC anulowane, 4 instalator zwrocil blad, 5 inny blad, 6 zadanie odrzucilo plik.
        # Kod 8 zadania (inna wersja programu) nie wychodzi z pomocnika: pomocnik instaluje wtedy ten sam plik przez UAC.
        param(
            [Parameter(Mandatory = $true)][string]$Setup,
            [Parameter(Mandatory = $true)][string]$App,
            [Parameter(Mandatory = $true)][string]$Log,
            [Parameter(Mandatory = $true)][string]$Result,
            [Parameter(Mandatory = $true)][string]$Sha256,
            [string]$TaskName,
            [string]$TaskResult,
            [string]$RequestId,
            [switch]$Detached
        )
        $ErrorActionPreference = 'Stop'

        function Write-Result([int]$ExitCode, $SetupExitCode, [string]$Message) {
            $payload = [ordered]@{
                exitCode = $ExitCode
                setupExitCode = $SetupExitCode
                finishedAt = (Get-Date).ToUniversalTime().ToString('o')
                message = $Message
            }
            [System.IO.File]::WriteAllText($Result, ($payload | ConvertTo-Json -Compress))
        }

        function Test-Cancelled($ErrorRecord) {
            $exception = $ErrorRecord.Exception
            while ($null -ne $exception) {
                if ($exception -is [System.ComponentModel.Win32Exception] -and $exception.NativeErrorCode -eq 1223) { return $true }
                $exception = $exception.InnerException
            }
            return $false
        }

        function Test-AppRunning {
            # Zywa instancja = muteks pojedynczej instancji programu. Martwy klon WER (0 watkow) o tej samej nazwie
            # i sciezce nie trzyma juz zadnych uchwytow, wiec go nie liczymy.
            $mutex = $null
            try {
                if ([System.Threading.Mutex]::TryOpenExisting('Global\MaszynyAlert.App', [ref]$mutex)) { $mutex.Dispose(); return $true }
                return $false
            } catch [System.UnauthorizedAccessException] {
                return $true
            }
        }

        function Start-AppIfStopped([string]$AppArgument) {
            # Zwraca $true, gdy po wywolaniu dziala zywa instancja programu (potwierdzone muteksem, do 20 s).
            # Z '--if-not-running' (tryb zadania: znacznik ciszy zdejmuje zadanie) wyscig z watchdogiem nie pokazuje okna
            # "VNC Alert juz dziala"; sciezka z UAC startuje bez argumentu, bo zwykly start kasuje znacznik po --stop.
            if (Test-AppRunning) { return $true }
            if ($AppArgument) { Start-Process -FilePath $App -ArgumentList $AppArgument -WorkingDirectory (Split-Path -Parent $App) }
            else { Start-Process -FilePath $App -WorkingDirectory (Split-Path -Parent $App) }
            for ($i = 0; $i -lt 40; $i++) {
                Start-Sleep -Milliseconds 500
                if (Test-AppRunning) { return $true }
            }
            return $false
        }

        function Read-TaskResult([DateTime]$Since) {
            # Wynik zadania z katalogu tylko dla administratorow: nasz po identyfikatorze zadania; wynik bez identyfikatora
            # (zadanie nie odczytalo zadania) liczy sie tylko, gdy powstal po wyzwoleniu.
            try {
                if (-not (Test-Path -LiteralPath $TaskResult -PathType Leaf)) { return $null }
                $written = (Get-Item -LiteralPath $TaskResult).LastWriteTimeUtc
                $outcome = [System.IO.File]::ReadAllText($TaskResult) | ConvertFrom-Json
                if ($outcome.requestId -eq $RequestId) { return $outcome }
                if ([string]::IsNullOrEmpty([string]$outcome.requestId) -and $written -ge $Since) { return $outcome }
            } catch { }
            return $null
        }

        function Get-TaskRun {
            # Stan zadania w Harmonogramie (grupa Users ma prawo odczytu): czy biegnie albo czeka w kolejce (4, 2), kiedy ruszylo,
            # jakim kodem skonczylo.
            # $null = zadania nie ma albo nie da sie go odczytac.
            try {
                $service = New-Object -ComObject Schedule.Service
                $service.Connect()
                $task = $service.GetFolder('\').GetTask($TaskName)
                return [pscustomobject]@{ Running = ($task.State -eq 4 -or $task.State -eq 2); LastRun = [DateTime]$task.LastRunTime; LastResult = [int64]$task.LastTaskResult }
            } catch { return $null }
        }

        if (-not $Detached) {
            $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
            $arguments = @(
                '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
                '-File', ('"' + $PSCommandPath + '"'),
                '-Setup', ('"' + $Setup + '"'),
                '-App', ('"' + $App + '"'),
                '-Log', ('"' + $Log + '"'),
                '-Result', ('"' + $Result + '"'),
                '-Sha256', $Sha256,
                '-Detached')
            if ($TaskName) {
                $arguments += @('-TaskName', ('"' + $TaskName + '"'), '-TaskResult', ('"' + $TaskResult + '"'), '-RequestId', $RequestId)
            }
            $child = Start-Process -FilePath $powershell -ArgumentList $arguments -WindowStyle Hidden `
                -WorkingDirectory (Split-Path -Parent $Result) -PassThru
            [Console]::Out.WriteLine($child.Id)
            exit 0
        }

        if ($TaskName) {
            # Zadanie "VNC Alert update" (P7b): ten etap NIE podnosi uprawnien. Zadanie (SYSTEM, skrypt w Program Files)
            # samo sprawdza plik ze spisem wydan, uruchamia instalator i zapisuje wynik w katalogu tylko dla administratorow.
            # Program wraca stad - na koncie i w sesji operatora, nie w sesji 0 jako SYSTEM.
            try {
                $started = [DateTime]::UtcNow
                $startedLocal = [DateTime]::Now.AddSeconds(-2)
                $schtasks = Join-Path $env:SystemRoot 'System32\schtasks.exe'
                $ErrorActionPreference = 'Continue'
                $null = & $schtasks '/run' '/tn' $TaskName 2>$null
                $runCode = $LASTEXITCODE
                $ErrorActionPreference = 'Stop'
                if ($runCode -ne 0) {
                    Write-Result 5 $null ('Nie uruchomiono zadania aktualizacji "' + $TaskName + '" (schtasks, kod ' + $runCode + ').')
                    exit 5
                }
                $outcome = $null
                $seenRun = $false
                $idleSince = $null
                for ($i = 0; $i -lt 1800 -and $null -eq $outcome; $i++) {
                    Start-Sleep -Seconds 1
                    $outcome = Read-TaskResult $started
                    if ($null -ne $outcome) { break }
                    # Kod 7 (katalog niechroniony), przebieg pominiety przez IgnoreNew albo zadanie usuniete
                    # w trakcie nie zostawiaja wyniku - zamiast czekac 30 min, po kilku sekundach bezczynnosci zadania zglaszamy blad.
                    $run = Get-TaskRun
                    if ($null -ne $run -and ($run.Running -or $run.LastRun -ge $startedLocal)) { $seenRun = $true }
                    if ($null -ne $run -and $run.Running) { $idleSince = $null; continue }
                    if (-not $seenRun -and $i -lt 15) { continue }
                    if ($null -eq $idleSince) { $idleSince = [DateTime]::UtcNow; continue }
                    if (([DateTime]::UtcNow - $idleSince).TotalSeconds -lt 5) { continue }
                    $outcome = Read-TaskResult $started
                    if ($null -ne $outcome) { break }
                    if ($null -eq $run) { $reason = 'zadania nie ma w Harmonogramie zadan albo nie da sie go odczytac' }
                    elseif ($run.LastRun -lt $startedLocal) { $reason = 'Harmonogram nie uruchomil nowego przebiegu - poprzedni jeszcze trwal; sprobuj ponownie za kilka minut' }
                    elseif ($run.LastResult -eq 7) { $reason = 'kod 7: katalog danych albo updates-admin pozwala pisac innym kontom niz Administratorzy/SYSTEM, wiec zadanie niczego nie uruchomilo; szczegoly w Dzienniku zdarzen Windows (Aplikacja, zrodlo "VNC Alert update"); naprawa - ponowna instalacja z konta administratora' }
                    else { $reason = 'kod ' + $run.LastResult }
                    $null = Start-AppIfStopped '--if-not-running'
                    Write-Result 5 $null ('Zadanie aktualizacji zakonczylo sie bez wyniku (' + $reason + ').')
                    exit 5
                }
                if ($null -eq $outcome) {
                    $null = Start-AppIfStopped '--if-not-running'
                    Write-Result 5 $null 'Zadanie aktualizacji nie zwrocilo wyniku w 30 min.'
                    exit 5
                }
                $code = [int]$outcome.exitCode
                if ($code -eq 8) {
                    # Zadanie zarejestrowano dla innej wersji programu (re-rejestracja przy poprzedniej instalacji
                    # sie nie udala) - ten sam plik instalujemy przez okno UAC ponizej; instalator z UAC zarejestruje zadanie od nowa.
                    $Log = Join-Path (Split-Path -Parent $Result) (Split-Path -Leaf $Log)
                    $TaskName = $null
                }
                elseif ($code -ne 0) {
                    # Odmowa (6) albo blad instalatora (4): program dziala dalej albo wraca stara wersja.
                    $null = Start-AppIfStopped '--if-not-running'
                    Write-Result $code $outcome.setupExitCode ([string]$outcome.message)
                    exit $code
                }
                else {
                    if (Start-AppIfStopped '--if-not-running') {
                        Write-Result 0 0 'Zainstalowano (zadanie aktualizacji, bez UAC); program uruchomiony ponownie.'
                    } else {
                        # Gdy zadanie nie zdjelo znacznika ciszy instalatora (lezy obok wyniku zadania,
                        # Users go czytaja), start z --if-not-running konczy sie cicho, a watchdog milczy do 10 min.
                        $systemMarker = Join-Path (Split-Path -Parent $TaskResult) 'stop.requested'
                        if (Test-Path -LiteralPath $systemMarker -PathType Leaf) {
                            Write-Result 0 0 'Zainstalowano (zadanie aktualizacji), ale nie potwierdzono uruchomienia programu w ciagu 20 s, a zadanie nie zdjelo znacznika zatrzymania instalatora (updates-admin\stop.requested, szczegoly w install-task.log) - watchdog wznowi program dopiero po 10 min od zatrzymania; mozna uruchomic go recznie.'
                        } else {
                            Write-Result 0 0 'Zainstalowano (zadanie aktualizacji), ale nie potwierdzono uruchomienia programu w ciagu 20 s - watchdog wznowi go w ciagu minuty.'
                        }
                    }
                    exit 0
                }
            } catch {
                try { Write-Result 5 $null $_.Exception.Message } catch { }
                exit 5
            }
        }

        try {
            if (-not (Test-Path -LiteralPath $Setup -PathType Leaf)) {
                Write-Result 5 $null ('Brak pliku instalatora: ' + $Setup)
                exit 5
            }
            # Suma ze spisu wydan; sprawdzana tu, tuz przed UAC, bo katalog jest zapisywalny dla operatora.
            # Skrot z .NET, nie z polecenia modulu Utility: przy PSModulePath z PowerShell 7 modul 5.1 sie nie laduje.
            # Plik zostaje otwarty bez wspoldzielenia zapisu i usuwania do konca
            # instalatora, wiec nie da sie go podmienic miedzy sprawdzeniem a uruchomieniem.
            $guard = [System.IO.File]::Open($Setup, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
            try {
                $sha = [System.Security.Cryptography.SHA256]::Create()
                try { $actual = ([System.BitConverter]::ToString($sha.ComputeHash($guard))).Replace('-', '') } finally { $sha.Dispose() }
                if ($actual -ne $Sha256) {
                    Write-Result 5 $null ('Plik instalatora zmienil sie po sprawdzeniu (SHA-256 ' + $actual + ' zamiast ' + $Sha256 + ').')
                    exit 5
                }
                $setupArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG="' + $Log + '"'))
                try {
                    $process = Start-Process -FilePath $Setup -ArgumentList $setupArguments -Verb RunAs -PassThru `
                        -WorkingDirectory (Split-Path -Parent $Setup)
                } catch {
                    if (Test-Cancelled $_) {
                        Write-Result 3 $null 'Okno UAC anulowane.'
                        exit 3
                    }
                    Write-Result 5 $null ('Nie uruchomiono instalatora: ' + $_.Exception.Message)
                    exit 5
                }
                # Uchwyt pobrany przed czekaniem - bez tego Windows PowerShell 5.1 potrafi zwrocic pusty ExitCode.
                $null = $process.Handle
                $process.WaitForExit()
                $setupExitCode = $process.ExitCode
            } finally {
                $guard.Dispose()
            }
            if ($setupExitCode -ne 0) {
                # Instalator mogl juz zatrzymac program - wraca stara wersja, a ona sama pokaze, ze instalacja nie doszla do skutku.
                $null = Start-AppIfStopped
                Write-Result 4 $setupExitCode ('Instalator zakonczyl sie kodem ' + $setupExitCode)
                exit 4
            }
            if (Start-AppIfStopped) {
                Write-Result 0 0 'Zainstalowano; program uruchomiony ponownie.'
            } else {
                Write-Result 0 0 'Zainstalowano, ale nie potwierdzono uruchomienia programu w ciagu 20 s - watchdog wznowi go w ciagu 10 min.'
            }
            exit 0
        } catch {
            try { Write-Result 5 $null $_.Exception.Message } catch { }
            exit 5
        }
        """;
}
