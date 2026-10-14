# PowerShell 5.1, plik w ASCII (Windows po polsku czyta pliki bez BOM jako cp1250).

$ErrorActionPreference = 'Continue'

function Sprawdz([string]$Opis) {
    if ($LASTEXITCODE -ne 0) { Write-Output ('blad: ' + $Opis + ' zwrocilo ' + $LASTEXITCODE); exit 1 }
}

$numer = $env:GITHUB_RUN_NUMBER
if ($numer -notmatch '^[0-9]{1,9}$') { Write-Output 'blad: brak GITHUB_RUN_NUMBER - skrypt jest dla przebiegu CI'; exit 2 }
$numer = [int]$numer + 270
$sha = $env:GITHUB_SHA
if ($sha -notmatch '^[0-9a-f]{40}$') { Write-Output 'blad: brak GITHUB_SHA'; exit 2 }

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
if (-not $env:RUNNER_TOOL_CACHE) { Write-Output 'blad: brak RUNNER_TOOL_CACHE'; exit 2 }
$dotnetDir = Join-Path $env:RUNNER_TOOL_CACHE 'dotnet'
$maSdk = (Test-Path -LiteralPath (Join-Path $dotnetDir 'dotnet.exe')) -and
    [bool](Get-ChildItem -LiteralPath (Join-Path $dotnetDir 'sdk') -Directory -Filter '10.0.*' -ErrorAction SilentlyContinue)
if (-not $maSdk) {
    Write-Output ('info: instalacja .NET SDK z global.json do ' + $dotnetDir)
    $instalator = Join-Path $env:RUNNER_TEMP 'dotnet-install.ps1'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -UseBasicParsing -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $instalator
    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $instalator -JsonFile global.json -InstallDir $dotnetDir -NoPath
    Sprawdz 'dotnet-install.ps1'
}
$env:DOTNET_ROOT = $dotnetDir
$env:PATH = $dotnetDir + ';' + $env:PATH
& dotnet --version
Sprawdz 'dotnet --version'

$app = 'publish/app'
if (Test-Path -LiteralPath $app) { Remove-Item -LiteralPath $app -Recurse -Force }

Write-Output ('info: dotnet publish, wersja 0.4.' + $numer)
& dotnet publish src/MaszynyAlert.App/MaszynyAlert.App.csproj -c Release -r win-x64 --self-contained true `
    ('-p:SourceRevisionId=' + $sha) -p:ContinuousIntegrationBuild=true -p:PublishSingleFile=false `
    -p:VncAlertMajorMinor=0.4 ('-p:VncAlertBuildNumber=' + $numer) -o $app
Sprawdz 'dotnet publish'

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path (Join-Path $app 'sounds') | Out-Null
if (-not (Test-Path -LiteralPath (Join-Path $app 'config.json'))) {
    Copy-Item -LiteralPath 'config/config.example.json' -Destination (Join-Path $app 'config.json')
}
Copy-Item -LiteralPath 'config/rules.example.json' -Destination (Join-Path $app 'rules.json') -Force
Copy-Item -LiteralPath 'sounds/alarm.wav' -Destination (Join-Path $app 'sounds/alarm.wav') -Force
Copy-Item -LiteralPath 'config/test-ekran' -Destination (Join-Path $app 'test-ekran') -Recurse -Force

foreach ($plik in @('MaszynyAlert.App.exe', 'MaszynyAlert.App.dll', 'coreclr.dll', 'config.example.json', 'rules.example.json', 'sounds/alarm.wav', 'sounds/idle-ping.wav', 'vncalert.cmd')) {
    if (-not (Test-Path -LiteralPath (Join-Path $app $plik))) { Write-Output ('blad: niepelna paczka, brak ' + $plik); exit 1 }
}

# build-info.json jak scripts/Write-BuildInfo.ps1 (ten ma polskie znaki, a PowerShell 5.1 czyta go
# jako cp1250) - zapis UTF-8 bez BOM.
$exe = (Resolve-Path -LiteralPath (Join-Path $app 'MaszynyAlert.App.exe')).Path
$wersja = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion
if (-not $wersja.EndsWith('+' + $sha, [StringComparison]::OrdinalIgnoreCase)) {
    Write-Output ('blad: wersja EXE (' + $wersja + ') nie zawiera commita ' + $sha); exit 1
}
$przebieg = $env:GITHUB_SERVER_URL + '/' + $env:GITHUB_REPOSITORY + '/actions/runs/' + $env:GITHUB_RUN_ID
$json = '{' + "`n" + '  "version": "' + $wersja + '",' + "`n" + '  "commit": "' + $sha + '",' + "`n" + '  "run": "' + $przebieg + '"' + "`n" + '}' + "`n"
[IO.File]::WriteAllText((Join-Path (Resolve-Path -LiteralPath $app).Path 'build-info.json'), $json, (New-Object Text.UTF8Encoding($false)))

Write-Output ('info: paczka gotowa: ' + $wersja + ' (FileVersion ' + [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion + ')')
exit 0
