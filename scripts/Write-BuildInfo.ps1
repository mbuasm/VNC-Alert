param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][ValidatePattern('^(?:[a-fA-F0-9]{40}|[a-fA-F0-9]{64})$')][string]$Commit,
    [string]$RunUrl
)
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$dll = Join-Path $package 'MaszynyAlert.App.dll'
$exe = Join-Path $package 'MaszynyAlert.App.exe'
foreach ($file in @($dll, $exe)) {
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Brak pliku aplikacji: $file" }
}
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($dll).ProductVersion
$exeVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion
if ([string]::IsNullOrWhiteSpace($version) -or $version -cne $exeVersion) {
    throw 'Wersje DLL i EXE są puste albo różne. Zbuduj całą paczkę ponownie.'
}
if (!$version.EndsWith('+' + $Commit, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Wersja aplikacji nie zawiera wskazanego commita. Publikuj z SourceRevisionId ustawionym na ten commit.'
}
$info = [ordered]@{ version = $version; commit = $Commit.ToLowerInvariant() }
if (![string]::IsNullOrWhiteSpace($RunUrl)) { $info.run = $RunUrl }
$info | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $package 'build-info.json') -Encoding utf8
Write-Output "Zapisano metadane paczki: $version"
