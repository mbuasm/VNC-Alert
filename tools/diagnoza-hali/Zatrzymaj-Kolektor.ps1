param([string]$Katalog = (Join-Path $env:LOCALAPPDATA 'diagnoza-hali'))
$ErrorActionPreference = 'Stop'
try {
    . (Join-Path $PSScriptRoot 'Kolektor-Common.ps1')
    $null = Stop-Kolektor -KolektorRoot $Katalog
    Write-Host 'Kolektor zakonczyl prace albo nie dzialal. Inne procesy pozostaly bez zmian.'
    Write-Host 'Autostart pozostaje: przy nastepnym logowaniu kolektor wystartuje. Usuniecie: Zbierz-Wyniki.cmd -Usun.'
    exit 0
} catch {
    Write-Host 'Nie potwierdzono lagodnego zakonczenia kolektora. Niczego nie zabito sila; nie zamykaj okna probe.'
    Write-Host 'Nie pakuj katalogu out, dopoki kolektor nie zakonczy zapisu.'
    exit 1
}
