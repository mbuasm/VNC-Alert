# Okno startuje zminimalizowane i zamyka się po końcu. Wynik: TRX w -ResultsDir (+ blame-hang przy zawieszeniu).
#
# Przykłady:
#   # zawieszenie testów hasła przy interaktywnej konsoli (Probe, DPAPI dostępne):
#   pwsh -NoProfile -File tools\higiena-testow\Invoke-TestsOnFewCpus.ps1 -Project tests\MaszynyAlert.Probe.Tests\MaszynyAlert.Probe.Tests.csproj `
#        -Filter 'FullyQualifiedName~KolektorPasswordStoreTests.HasloZapisz' -Console -HangTimeout 1m -ResultsDir D:\tmp\konsola
#   # 1 procesor dzielony z 3 procesami obciążającymi, 32 wątki xUnit:
#   pwsh -NoProfile -File tools\higiena-testow\Invoke-TestsOnFewCpus.ps1 -Project tests\MaszynyAlert.Windows.Tests\MaszynyAlert.Windows.Tests.csproj `
#        -Affinity 1 -Burners 3 -RunSettings 'xUnit.MaxParallelThreads=32' -ResultsDir D:\tmp\obciazenie
param(
    [Parameter(Mandatory)] [string] $Project,
    [Parameter(Mandatory)] [string] $ResultsDir,
    [string] $Affinity = 'FFFFFFFF',
    [int] $Burners = 0,
    [switch] $Console,
    [string] $Filter = '',
    [string] $RunSettings = '',
    [string] $HangTimeout = '5m',
    [switch] $NoBuild,
    [string] $Dotnet = 'dotnet',
    [string] $DotnetSlot = ''
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $ResultsDir | Out-Null
$ResultsDir = (Resolve-Path $ResultsDir).Path
$Project = (Resolve-Path $Project).Path
$marker = 'WER-HIGIENA-BURNER'

$testArgs = @('test', $Project, '-c', 'Release', '-m:1', '-nr:false', '-p:UseSharedCompilation=false',
    '--blame-hang', '--blame-hang-timeout', $HangTimeout, '--logger', 'trx', '--results-directory', $ResultsDir)
if ($NoBuild) { $testArgs += '--no-build' }
if ($Filter) { $testArgs += @('--filter', $Filter) }
if ($RunSettings) { $testArgs += '--'; $testArgs += @($RunSettings -split ';' | Where-Object { $_ }) }

# Każdy argument w apostrofach: PowerShell nie rozbija wtedy `-p:x` na `-p:` i `x` (problem wywołań skryptów z argumentami z dwukropkiem).
$quoted = ($testArgs | ForEach-Object { "'" + ($_ -replace "'", "''") + "'" }) -join ' '
$inner = if ($DotnetSlot) { "& '$DotnetSlot' $quoted; exit `$LASTEXITCODE" } else { "& '$Dotnet' $quoted; exit `$LASTEXITCODE" }
$innerFile = Join-Path $ResultsDir 'uruchom.ps1'
Set-Content -LiteralPath $innerFile -Value $inner -Encoding utf8
$log = Join-Path $ResultsDir 'dotnet-test.log'
$redirect = if ($Console) { '' } else { ' < NUL' }
$launcher = Join-Path $ResultsDir 'uruchom.cmd'
Set-Content -LiteralPath $launcher -Encoding oem -Value @('@echo off',
    ('pwsh -NoProfile -File "{0}" > "{1}" 2>&1{2}' -f $innerFile, $log, $redirect))

try {
    # Start-Process (bez dziedziczenia uchwytów wywołującego), potem maska — `cmd /c start` dziedziczyłby potok wyjścia
    # i blokował wywołanie typu `... | Out-Null` do końca pracy obciążaczy.
    $mask = [IntPtr][Convert]::ToInt64($Affinity, 16)
    for ($i = 0; $i -lt $Burners; $i++) {
        $b = Start-Process pwsh -ArgumentList @('-NoProfile', '-Command', "`$m='$marker'; while(`$true){}") -WindowStyle Minimized -PassThru
        $b.ProcessorAffinity = $mask
    }
    Start-Sleep -Milliseconds 1000
    $sw = [Diagnostics.Stopwatch]::StartNew()
    cmd /c ('start "" /min /wait /affinity {0} cmd /c "{1}"' -f $Affinity, $launcher)
    "dotnet test: {0:N0} s (affinity 0x{1}, obciążacze {2}, konsola {3})" -f $sw.Elapsed.TotalSeconds, $Affinity, $Burners, [bool]$Console
} finally {
    Get-CimInstance Win32_Process -Filter "Name = 'pwsh.exe'" | Where-Object { $_.CommandLine -like "*$marker*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}
Get-Content -LiteralPath $log | Select-String -Pattern 'niepowodzenie:|failed:|przerwany|aborted|Test uruchomiony|was running' | ForEach-Object { $_.Line }
Get-ChildItem -LiteralPath $ResultsDir -Recurse -Filter 'Sequence_*.xml' | ForEach-Object {
    ([xml](Get-Content -LiteralPath $_.FullName -Raw)).TestSequence.Test | Where-Object { $_.Completed -eq 'False' } |
        ForEach-Object { "ZAWIESZONY/NIEDOKOŃCZONY: $($_.Name)" }
}
& (Join-Path $PSScriptRoot 'Analyze-Trx.ps1') -Dir $ResultsDir -Top 5
