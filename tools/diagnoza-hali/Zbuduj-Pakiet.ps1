param(
    [string]$Dotnet = 'D:\dotnet\dotnet.exe',
    [string]$Konfiguracja = 'Release',
    [switch]$BezZip
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Dotnet)) { $Dotnet = 'dotnet' }
$repo = (Resolve-Path (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\..')).Path
$tools = Join-Path $repo 'tools\diagnoza-hali'
$artifacts = Join-Path $repo 'artifacts'
$pakiet = Join-Path $artifacts 'diagnoza-hali'
$probeDir = Join-Path $pakiet 'probe'
$stamp = Get-Date -Format 'yyyyMMdd-HHmm'

Write-Host "Repozytorium: $repo"
if (Test-Path -LiteralPath $pakiet) { Remove-Item -LiteralPath $pakiet -Recurse -Force }
New-Item -ItemType Directory -Force -Path $probeDir | Out-Null

Write-Host 'dotnet publish probe (win-x64, self-contained)...'
& $Dotnet publish (Join-Path $repo 'src\MaszynyAlert.Probe\MaszynyAlert.Probe.csproj') -c $Konfiguracja -r win-x64 --self-contained true -p:PublishSingleFile=false -o $probeDir -m:1 -nr:false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish zakonczyl sie kodem $LASTEXITCODE" }
# config.example.json / rules.example.json z publish nie sa potrzebne w paczce (jest config.json obok runnera), ale nie szkodza.

foreach ($f in @('Diagnoza-Hali.cmd', 'Diagnoza-Hali.ps1', 'Zbierz-Wyniki.cmd', 'Zbierz-Wyniki.ps1', 'Zatrzymaj-Kolektor.cmd', 'Zatrzymaj-Kolektor.ps1', 'Kolektor-Common.ps1', 'Kolektor-Start.ps1')) {
    Copy-Item -LiteralPath (Join-Path $tools $f) -Destination (Join-Path $pakiet $f) -Force
}
Copy-Item -LiteralPath (Join-Path $tools 'config.diagnoza-hali.json') -Destination (Join-Path $pakiet 'config.json') -Force
Copy-Item -LiteralPath (Join-Path $repo 'config\rules.example.json') -Destination (Join-Path $pakiet 'rules.json') -Force

$exe = Join-Path $probeDir 'probe.exe'
Write-Host 'sprawdzam: probe.exe help'
$help = & $exe help 2>&1 | Out-String
if ($LASTEXITCODE -ne 0 -or $help -notmatch 'diagnoza') { throw "probe.exe help nie dziala (kod $LASTEXITCODE): $help" }
Write-Host ($help -split "`r?`n" | Select-Object -First 3 | Out-String)

$info = [ordered]@{
    zbudowano = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    commit = (& git -C $repo rev-parse HEAD 2>$null)
    galaz = (& git -C $repo rev-parse --abbrev-ref HEAD 2>$null)
    probeWersja = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion
}
$info | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $pakiet 'build-info.json') -Encoding UTF8

if (-not $BezZip) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = Join-Path $artifacts ("diagnoza-hali-{0}.zip" -f $stamp)
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    [IO.Compression.ZipFile]::CreateFromDirectory($pakiet, $zip)
    Write-Host ("Paczka: {0} ({1:N1} MB)" -f $zip, ((Get-Item -LiteralPath $zip).Length / 1MB))
}
Write-Host "Folder paczki: $pakiet"
