# Dwie rzeczywiste wersje EXE pozwalają sprawdzić aktualizację i odmowę downgrade.
[CmdletBinding()]
param([string]$BaselineVersion = '0.1.3')
$ErrorActionPreference = 'Stop'
$output = 'artifacts/installer-baseline'
$commit = (git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Nie można ustalić commita.' }
& dotnet publish src/MaszynyAlert.App/MaszynyAlert.App.csproj -c Release -r win-x64 --self-contained true "-p:Version=$BaselineVersion" "-p:SourceRevisionId=$commit" -p:PublishSingleFile=false -o "$output/app"
if ($LASTEXITCODE -ne 0) { throw 'Nie udało się zbudować starszej wersji testowej.' }
& "$PSScriptRoot/Write-BuildInfo.ps1" -PackagePath "$output/app" -Commit $commit
& "$PSScriptRoot/Build-Installer.ps1" -PackagePath "$output/app" -OutputPath "$output/setup" -AppDirName 'MaszynyAlert'
& dotnet publish tests/InstallerProbe/InstallerProbe.csproj -c Release -r win-x64 --self-contained true -o artifacts/installer-probe
if ($LASTEXITCODE -ne 0) { throw 'Nie udało się zbudować próbnika DPAPI/ACL.' }
