param(
    [Parameter(Mandatory)][string]$PackagePath,
    [ValidateRange(30, 300)][int]$StartupTimeoutSeconds = 30
)
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$exe = Join-Path $package 'MaszynyAlert.App.exe'
foreach ($relative in @('MaszynyAlert.App.exe', 'MaszynyAlert.App.dll', 'config.json', 'rules.json', 'vncalert.cmd',
    'sounds/alarm.wav', 'sounds/idle-ping.wav', 'test-ekran/CZYTAJ-TO.txt', 'build-info.json')) {
    $file = Join-Path $package $relative
    if (!(Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -eq 0) {
        throw "Brak wymaganego pliku paczki: $relative"
    }
}

# Metadane muszą opisywać dokładnie kod w EXE/DLL, a nie inną paczkę o tym samym numerze wersji.
$buildInfo = Get-Content -LiteralPath (Join-Path $package 'build-info.json') -Raw | ConvertFrom-Json
$dllVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $package 'MaszynyAlert.App.dll')).ProductVersion
$exeVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion
if ($buildInfo.commit -isnot [string] -or $buildInfo.commit -notmatch '^(?:[a-fA-F0-9]{40}|[a-fA-F0-9]{64})$') {
    throw 'build-info.json: brak pełnego, prawidłowego identyfikatora commita.'
}
if ($buildInfo.version -isnot [string] -or [string]::IsNullOrWhiteSpace($buildInfo.version) -or
    $buildInfo.version -cne $dllVersion -or $dllVersion -cne $exeVersion -or
    !$dllVersion.EndsWith('+' + $buildInfo.commit, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'build-info.json: wersja lub commit nie zgadzają się z aplikacją w EXE/DLL.'
}
Write-Output "PASS: metadane paczki zgodne z EXE/DLL ($dllVersion)."

if (![string]::IsNullOrWhiteSpace($env:GITHUB_RUN_NUMBER)) {
    $runNumber = 0
    if (![int]::TryParse($env:GITHUB_RUN_NUMBER, [ref]$runNumber)) {
        throw "GITHUB_RUN_NUMBER ma nieliczbową wartość '$($env:GITHUB_RUN_NUMBER)'."
    }
    $buildOffset = 0
    $props = Join-Path $PSScriptRoot '..\Directory.Build.props'
    if (Test-Path -LiteralPath $props) {
        $offsetMatch = [regex]::Match((Get-Content -LiteralPath $props -Raw), '<VncAlertBuildNumberOffset>(\d+)</VncAlertBuildNumberOffset>')
        if ($offsetMatch.Success) { $buildOffset = [int]$offsetMatch.Groups[1].Value }
    }
    $buildNumber = $runNumber + $buildOffset
    $exeFileInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
    if ($exeFileInfo.FileBuildPart -ne $buildNumber) {
        throw ("Numer wersji EXE ($($exeFileInfo.FileVersion)) nie zgadza się z numerem przebiegu CI ($runNumber): " +
            "trzecia część wersji musi być numerem przebiegu + $buildOffset. Sprawdź VncAlertBuildNumber w Directory.Build.props " +
            "i czy krok publikacji nie nadpisuje wersji (-p:Version).")
    }
    $expectedSuffix = if ([string]::IsNullOrWhiteSpace($env:VNCALERT_VERSION_SUFFIX)) { '' } else { '-' + $env:VNCALERT_VERSION_SUFFIX }
    $expectedVersion = '{0}.{1}.{2}{3}' -f $exeFileInfo.FileMajorPart, $exeFileInfo.FileMinorPart, $buildNumber, $expectedSuffix
    $shortVersion = $exeVersion.Split('+')[0]
    if ($shortVersion -cne $expectedVersion) {
        throw ("Wersja aplikacji '$shortVersion' różni się od oczekiwanej dla tego przebiegu CI '$expectedVersion' " +
            "(numer przebiegu $runNumber, dopisek PR '$($env:VNCALERT_VERSION_SUFFIX)'). Sprawdź Directory.Build.props i env w build.yml.")
    }
    Write-Output "PASS: wersja $shortVersion (FileVersion $($exeFileInfo.FileVersion)) odpowiada przebiegowi CI nr $runNumber."
}

# Odczyt zasobu PE bez uruchamiania EXE i bez narzędzi z Windows SDK.
if (-not ('PortableManifestReader' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class PortableManifestReader {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(IntPtr module);
    public static byte[] Read(string path) {
        var module = LoadLibraryEx(path, IntPtr.Zero, 2);
        if (module == IntPtr.Zero) throw new Win32Exception();
        try {
            var resource = FindResource(module, new IntPtr(1), new IntPtr(24));
            if (resource == IntPtr.Zero) throw new Win32Exception();
            uint size = SizeofResource(module, resource);
            if (size == 0) throw new Win32Exception();
            var loaded = LoadResource(module, resource);
            if (loaded == IntPtr.Zero) throw new Win32Exception();
            var pointer = LockResource(loaded);
            if (pointer == IntPtr.Zero) throw new Win32Exception();
            var bytes = new byte[checked((int)size)];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return bytes;
        } finally { FreeLibrary(module); }
    }
}
'@
}
$stream = [System.IO.MemoryStream]::new([PortableManifestReader]::Read($exe))
try {
    $manifest = [System.Xml.Linq.XDocument]::Load($stream)
    $ns = [System.Xml.Linq.XNamespace]::Get('urn:schemas-microsoft-com:asm.v3')
    $level = @($manifest.Descendants($ns.GetName('requestedExecutionLevel')))
    if ($level.Count -ne 1 -or $level[0].Attribute('level').Value -ne 'asInvoker') {
        throw 'Manifest EXE nie deklaruje asInvoker.'
    }
    if ($manifest.ToString() -notmatch 'PerMonitorV2') { throw 'Brak PerMonitorV2 w manifeście EXE.' }
} finally { $stream.Dispose() }
Write-Output 'PASS: komplet plików i poprawny manifest osadzony w EXE.'

function Read-DiagnosticJson($Archive, [string]$EntryName) {
    $entry = $Archive.GetEntry($EntryName)
    if ($null -eq $entry) { throw "Diagnostyka nie zawiera $EntryName." }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { return ($reader.ReadToEnd() | ConvertFrom-Json) }
    finally { $reader.Dispose() }
}

foreach ($mode in @('', '--monitoring-disabled')) {
    $proofPath = Join-Path $package 'smoke-test-output/build-identity.json'
    # Dowód musi powstać w TEJ próbie, również gdy paczka była testowana wcześniej.
    if (Test-Path -LiteralPath $proofPath) { Remove-Item -LiteralPath $proofPath -Force }
    $process = Start-Process -FilePath $exe -ArgumentList "--smoke-test $mode" -WorkingDirectory $package -WindowStyle Hidden -PassThru
    $startupWatch = [Diagnostics.Stopwatch]::StartNew()
    try {
        if (!$process.WaitForExit($StartupTimeoutSeconds * 1000)) {
            $outputPath = Join-Path $package 'smoke-test-output'
            Write-Output "Timeout smoke: PID=$($process.Id), outputExists=$(Test-Path -LiteralPath $outputPath)."
            throw "Test startu ($mode) przekroczył $StartupTimeoutSeconds sekund."
        }
        Write-Output "Proces smoke ($mode) zakończył się po $([Math]::Round($startupWatch.Elapsed.TotalSeconds, 1)) s."

        if ($process.ExitCode -ne 0) {
            $failurePath = Join-Path $package 'smoke-test-output/failure.txt'
            if (Test-Path -LiteralPath $failurePath) { Get-Content -LiteralPath $failurePath -Raw | Write-Output }
            throw "Test startu ($mode) zakończył się kodem $($process.ExitCode). Sprawdź smoke-test-output/failure.txt."
        }
        $runtimeInfo = Get-Content -LiteralPath $proofPath -Raw | ConvertFrom-Json
        if ($runtimeInfo.version -cne $buildInfo.version -or $runtimeInfo.commit -ine $buildInfo.commit) {
            throw 'Uruchomiona aplikacja raportuje inną wersję lub commit niż sprawdzona paczka.'
        }
        $archiveName = $runtimeInfo.diagnosticsArchive
        if ($archiveName -isnot [string] -or [string]::IsNullOrWhiteSpace($archiveName) -or
            $archiveName -cne [IO.Path]::GetFileName($archiveName) -or [IO.Path]::GetExtension($archiveName) -ine '.zip') {
            throw 'Dowód testu nie wskazuje lokalnego pliku ZIP diagnostyki.'
        }
        $archivePath = Join-Path (Join-Path $package 'smoke-test-output') $archiveName
        $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            $diagnosticBuild = Read-DiagnosticJson $archive 'build-info.json'
            $diagnosticStatus = Read-DiagnosticJson $archive 'status.json'
            $diagnosticManifest = Read-DiagnosticJson $archive 'manifest.json'
            if ($diagnosticBuild.version -cne $buildInfo.version -or $diagnosticBuild.commit -ine $buildInfo.commit) {
                throw 'ZIP diagnostyki zawiera inną tożsamość aplikacji.'
            }
            if ($diagnosticStatus.monitoringEnabled -isnot [bool] -or
                $diagnosticStatus.monitoringEnabled -ne ($mode -ne '--monitoring-disabled') -or
                $diagnosticStatus.effectiveClusterEnabled -ne $false) {
                throw "ZIP diagnostyki nie potwierdza stanu dla trybu '$mode'."
            }
            if ($diagnosticManifest.acceptanceStatus -cne 'NIE ODEBRANO') {
                throw 'Eksport diagnostyki nie może sam zaliczać odbioru na hali.'
            }
        } finally { $archive.Dispose() }
    } finally {
        if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() }
        $process.Dispose()
    }
}
Write-Output 'PASS: start WPF/DI/zasobnika, tożsamość wersji, eksport diagnostyki oraz globalny wyłącznik i lokalny test alarmu w izolacji od maszyn i danych użytkownika.'

$process = Start-Process -FilePath $exe -ArgumentList '--settings-smoke-test' -WorkingDirectory $package -WindowStyle Hidden -PassThru
try {
    if (!$process.WaitForExit(30000)) { throw 'Test osobnego edytora ustawień przekroczył 30 sekund.' }
    if ($process.ExitCode -ne 0) { throw 'Błąd startu/zamknięcia osobnego edytora ustawień. Sprawdź smoke-test-output.' }
} finally {
    if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $process.Dispose()
}
Write-Output 'PASS: osobny edytor ustawień otwiera się i zamyka bez hosta monitorowania.'

# Komenda zatrzymania jest w paczce i daje się uruchomić. Sprawdzamy WYŁĄCZNIE użycie bez argumentu:
# "vncalert killapp" działa na mutex Global\MaszynyAlert.App, więc zatrzymałoby program uruchomiony
# na komputerze, na którym akurat idzie ten test. Ścieżkę zatrzymania pokrywają testy jednostkowe
# StopRequestTests i scenariusz „aktualizacja przy działającej aplikacji” w Test-Installer.ps1.
$usage = & "$env:ComSpec" /c "`"$(Join-Path $package 'vncalert.cmd')`"" 2>&1 | Out-String
$usageCode = $LASTEXITCODE
if ($usageCode -ne 2 -or $usage -notmatch 'killapp') {
    throw "Komenda vncalert nie opisuje użycia (kod ${usageCode}): $usage"
}
Write-Output 'PASS: komenda vncalert jest w paczce i opisuje "vncalert killapp".'

$stateDir = Join-Path $package 'state'
New-Item -ItemType Directory -Path $stateDir -Force | Out-Null
Set-Content -LiteralPath (Join-Path $stateDir 'stop.requested') -Value ([DateTimeOffset]::Now.ToString('o')) -NoNewline
$process = Start-Process -FilePath $exe -ArgumentList '--if-not-running' -WorkingDirectory $package -WindowStyle Hidden -PassThru
try {
    if (!$process.WaitForExit(30000)) { throw 'Tryb --if-not-running nie zakończył się w 30 sekund.' }
    if ($process.ExitCode -ne 0) { throw "Tryb --if-not-running zakończył się kodem $($process.ExitCode)." }
}
finally {
    if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $process.Dispose()
}
if (Get-Process -Name 'MaszynyAlert.App' -ErrorAction SilentlyContinue) {
    throw 'Watchdog uruchomił program mimo świeżej komendy zatrzymania.'
}
Remove-Item -LiteralPath $stateDir -Recurse -Force
Write-Output 'PASS: watchdog milczy przez 10 minut po komendzie zatrzymania (tryb --if-not-running).'

# Kod wyjścia skryptu ma pochodzić z jego własnych sprawdzeń, a nie z ostatniego uruchomionego programu.
exit 0
