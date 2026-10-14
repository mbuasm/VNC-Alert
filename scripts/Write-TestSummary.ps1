# Wypisuje do Summary przebiegu GitHub Actions (i na konsolę) liczbę testów z każdego
# pliku TRX: total / passed / failed / notExecuted. Nie zmienia niczego poza tym.
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ResultsDirectory
)
$ErrorActionPreference = 'Stop'

function Write-Line([string] $Line) {
    Write-Host $Line
    if ($env:GITHUB_STEP_SUMMARY) {
        $Line | Out-File $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8
    }
}

Write-Line '### Wyniki testów'
if (!(Test-Path -LiteralPath $ResultsDirectory)) {
    Write-Line 'Brak TRX: testy nie wystartowały lub nie zapisały wyników.'
    return
}
$files = Get-ChildItem -LiteralPath $ResultsDirectory -Filter '*.trx'
if (-not $files) {
    Write-Line 'Brak TRX: testy nie wystartowały lub nie zapisały wyników.'
    return
}
$sumTotal = 0; $sumPassed = 0; $sumFailed = 0
foreach ($file in $files) {
    [xml] $testResult = Get-Content -LiteralPath $file.FullName -Raw
    $counter = $testResult.SelectSingleNode("//*[local-name()='Counters']")
    Write-Line "- $($file.Name): total=$($counter.total), passed=$($counter.passed), failed=$($counter.failed), notExecuted=$($counter.notExecuted)"
    $sumTotal += [int] $counter.total; $sumPassed += [int] $counter.passed; $sumFailed += [int] $counter.failed
}
Write-Line "**Razem: $sumPassed/$sumTotal przeszło, $sumFailed nie przeszło.**"
