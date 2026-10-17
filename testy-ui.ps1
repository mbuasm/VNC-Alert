$ErrorActionPreference = 'Continue'

function Zakoncz {
    param([string]$Opis, [int]$Kod)
    Write-Output ('blad: ' + $Opis + ' zwrocilo ' + $Kod)
    Diagnostyka
    exit $Kod
}

function Diagnostyka {
    foreach ($dir in @('publish/app/smoke-test-output', 'artifacts/installer-test')) {
        if (!(Test-Path -LiteralPath $dir)) { continue }
        Get-ChildItem -LiteralPath $dir -Recurse -File -Include *.txt, *.log, *.json | ForEach-Object {
            Write-Output "===== $($_.FullName)"
            Get-Content -LiteralPath $_.FullName -Tail 80
        }
    }
}

function Pwsh {
    param([string]$Opis, [string]$Polecenie)
    $plik = Join-Path $temp ('krok-' + [guid]::NewGuid().ToString('N') + '.ps1')
    [IO.File]::WriteAllText($plik, "`$ErrorActionPreference = 'Stop'`n$Polecenie`nif (Test-Path -LiteralPath variable:\LASTEXITCODE) { exit `$LASTEXITCODE }`n", (New-Object Text.UTF8Encoding($true)))
    Write-Output ('info: ' + $Opis)
    $global:LASTEXITCODE = 0
    & pwsh -NoProfile -NonInteractive -File $plik
    $kod = $LASTEXITCODE
    Remove-Item -LiteralPath $plik -Force
    if ($kod -ne 0) { Zakoncz $Opis $kod }
}

$temp = $env:RUNNER_TEMP
if (-not $temp) { $temp = [IO.Path]::GetTempPath() }
$wyniki = $env:UI_TESTY_WYNIKI
if (-not $wyniki) { $wyniki = 'TestResults/ui' }
if (-not $env:RUNNER_TOOL_CACHE) { $env:RUNNER_TOOL_CACHE = Join-Path $temp 'narzedzia' }

Write-Output 'info: paczka wydania'
$global:LASTEXITCODE = 0
& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\scripts\Przygotuj-Wydanie.ps1
if ($LASTEXITCODE -ne 0) { Zakoncz 'Przygotuj-Wydanie.ps1' $LASTEXITCODE }

Pwsh 'smoke startu paczki' './scripts/Test-PortablePackage.ps1 -PackagePath publish/app -StartupTimeoutSeconds 120'

$packageRoot = (Resolve-Path 'publish/app').Path
$smokeOutput = [IO.Path]::GetFullPath((Join-Path $packageRoot 'smoke-test-output'))
if (!$smokeOutput.StartsWith($packageRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    Zakoncz 'kontrola katalogu testu startu' 1
}
if (Test-Path -LiteralPath $smokeOutput) { Remove-Item -LiteralPath $smokeOutput -Recurse -Force }

$iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
if (!(Test-Path -LiteralPath $iscc -PathType Leaf)) { Zakoncz "brak $iscc" 1 }
$out = Join-Path $temp 'wydanie-sprawdz'
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $out | Out-Null
Write-Output 'info: instalator wydania'
$global:LASTEXITCODE = 0
& $iscc "/O$out" installer/MaszynyAlert-wydanie.iss
if ($LASTEXITCODE -ne 0) { Zakoncz 'ISCC' $LASTEXITCODE }
$pliki = @(Get-ChildItem -LiteralPath $out -File -Filter '*.exe')
if ($pliki.Count -ne 1) { Zakoncz "liczba instalatorow $($pliki.Count)" 1 }
$exe = [Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path 'publish/app/MaszynyAlert.App.exe').Path)
$oczekiwana = "VncAlert-Setup-$($exe.FileVersion).exe"
if ($pliki[0].Name -cne $oczekiwana) { Zakoncz "nazwa instalatora $($pliki[0].Name) zamiast $oczekiwana" 1 }
Write-Output "info: $($pliki[0].Name), wersja $($exe.ProductVersion)"
if ($env:GITHUB_STEP_SUMMARY) {
    [IO.File]::AppendAllText($env:GITHUB_STEP_SUMMARY, "### Sprawdzone wydanie: ``$($pliki[0].Name)`` (wersja $($exe.ProductVersion))`n", (New-Object Text.UTF8Encoding($false)))
}

$setup = $pliki[0].FullName.Replace("'", "''")
Pwsh 'test instalacji, aktualizacji i odinstalowania' "./scripts/Prepare-InstallerTests.ps1`n./scripts/Test-Installer.ps1 -SetupPath '$setup'`nGet-Content -LiteralPath artifacts/installer-test/checks.txt"

if ($env:CI_UI_VNC_ADRES -and $env:CI_UI_VNC_HASLO) {
    $env:VNCALERT_REAL_VNC = $env:CI_UI_VNC_ADRES
    $env:VNCALERT_REAL_VNC_PASSWORD = $env:CI_UI_VNC_HASLO
    Write-Output "info: VNCALERT_REAL_VNC=$env:CI_UI_VNC_ADRES"
}

$sdk = (& dotnet --version | Out-String).Trim()
Write-Output "info: .NET SDK $sdk, wyniki trx w $wyniki"
$global:LASTEXITCODE = 0
& dotnet test tests/MaszynyAlert.Windows.Tests/MaszynyAlert.Windows.Tests.csproj -c Release -m:1 -nr:false -p:UseSharedCompilation=false --logger 'trx;LogFileName=windows-tests.trx' --logger 'console;verbosity=normal' --results-directory $wyniki
if ($LASTEXITCODE -ne 0) { Zakoncz 'dotnet test' $LASTEXITCODE }
exit 0
