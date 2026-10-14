[CmdletBinding()]
param(
    [string]$PackagePath = 'publish/app',
    [string]$OutputPath = 'artifacts/installer',
    [string]$CompilerPath,
    [string]$AppDirName
)
$ErrorActionPreference = 'Stop'

# Gdzie szukać kompilatora Inno Setup 6, po kolei: jawny -CompilerPath, zmienna
# INNO_SETUP (dla instalacji poza domyślnym katalogiem — tak jest na self-hosted
# runnerze), domyślne katalogi instalacji i wreszcie PATH. Runner windows-latest
# ma ISCC.exe w Program Files (x86), więc tam kolejność kończy się na drugim kroku.
# INNO_SETUP może wskazywać sam plik ISCC.exe albo katalog, w którym leży.
if (!$CompilerPath) {
    $candidates = @()
    if ($env:INNO_SETUP) {
        $candidates += $env:INNO_SETUP
        $candidates += (Join-Path $env:INNO_SETUP 'ISCC.exe')
    }
    $candidates += "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    $candidates += "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    $onPath = Get-Command 'ISCC.exe' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) { $candidates += $onPath.Source }
    $CompilerPath = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
    if (!$CompilerPath) {
        throw ("Brak kompilatora Inno Setup 6. Sprawdzono: " + (($candidates | Where-Object { $_ }) -join '; ') +
            ". Wskaż go zmienną środowiskową INNO_SETUP albo parametrem -CompilerPath.")
    }
}
& "$PSScriptRoot/Check-PrivateMaterials.ps1"
$package = (Resolve-Path -LiteralPath $PackagePath).Path
foreach ($file in @('MaszynyAlert.App.exe', 'MaszynyAlert.App.dll', 'coreclr.dll', 'config.example.json', 'rules.example.json', 'sounds/alarm.wav', 'sounds/idle-ping.wav', 'vncalert.cmd')) {
    if (!(Test-Path -LiteralPath (Join-Path $package $file))) { throw "Niepełna paczka: $file" }
}
if (!(Test-Path -LiteralPath $CompilerPath)) { throw "Brak kompilatora Inno Setup 6: $CompilerPath" }
$output = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$signArguments = @()
$signing = -not [string]::IsNullOrWhiteSpace($env:CODESIGN_PFX_BASE64)
if ($signing) {
    $signScript = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'Sign-Files.ps1')).Path
    $pwsh = (Get-Command 'pwsh' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
    if (!$pwsh) { $pwsh = 'powershell.exe' }
    # $q = cudzysłów, $f = podpisywany plik (Inno Setup cytuje go sam) — bez literalnych cudzysłowów
    # w argumencie, żeby PowerShell nie musiał ich uciekać przy przekazaniu do ISCC.
    $signArguments = @('/DSignTool', "/Svncalert=`$q$pwsh`$q -NoProfile -ExecutionPolicy Bypass -File `$q$signScript`$q -Path `$f")
    Write-Host 'Podpis Authenticode: włączony (sekret CODESIGN_PFX_BASE64).'
} else {
    Write-Host 'Podpis Authenticode: pominięty (brak sekretu CODESIGN_PFX_BASE64) — instalator niepodpisany.'
}
$defineArguments = @()
if ($AppDirName) { $defineArguments += "/DAppDirName=$AppDirName" }
& $CompilerPath "/DPublishDir=$package" "/DOutputPath=$output" @defineArguments @signArguments (Join-Path $PSScriptRoot '../installer/MaszynyAlert.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup zakończył się kodem $LASTEXITCODE" }
$setup = Join-Path $output 'VncAlert-Setup.exe'
if (!(Test-Path -LiteralPath $setup)) { throw 'Kompilator nie utworzył instalatora.' }
if ($signing) {
    $signature = Get-AuthenticodeSignature -LiteralPath $setup
    if ($signature.Status -ne 'Valid') { throw "Instalator nie ma ważnego podpisu Authenticode: $($signature.Status) $($signature.StatusMessage)" }
    Write-Host "PASS: instalator podpisany ($($signature.SignerCertificate.Subject))."
}
$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), "$hash  VncAlert-Setup.exe`n")
