# Podpis Authenticode plikow VNC Alert (EXE/DLL aplikacji, instalator, deinstalator).
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string[]] $Path,
    [string] $PfxBase64 = $env:CODESIGN_PFX_BASE64,
    [string] $PfxPassword = $env:CODESIGN_PFX_PASSWORD,
    [string] $TimestampUrl = $(if ($env:CODESIGN_TIMESTAMP_URL) { $env:CODESIGN_TIMESTAMP_URL } else { 'http://timestamp.digicert.com' }),
    [string] $Description = 'VNC Alert',
    [string] $SignToolPath = $env:SIGNTOOL,
    [switch] $Required
)
$ErrorActionPreference = 'Stop'

$files = @()
foreach ($item in $Path) {
    $resolved = Resolve-Path -Path $item -ErrorAction SilentlyContinue
    if (-not $resolved) { throw "Brak pliku do podpisania: $item" }
    $files += $resolved | ForEach-Object { $_.Path }
}

if ([string]::IsNullOrWhiteSpace($PfxBase64)) {
    $message = 'Podpis Authenticode pominiety: brak certyfikatu (sekret CODESIGN_PFX_BASE64). Pliki zostaja niepodpisane.'
    if ($Required) { throw $message }
    Write-Host $message
    if ($env:GITHUB_STEP_SUMMARY) { "Podpis Authenticode: pominiety (brak certyfikatu)." | Out-File $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8 }
    exit 0
}
if ([string]::IsNullOrEmpty($PfxPassword)) { throw 'Jest CODESIGN_PFX_BASE64, ale brak CODESIGN_PFX_PASSWORD.' }

function Find-SignTool([string] $Explicit) {
    $candidates = @()
    if ($Explicit) { $candidates += $Explicit }
    foreach ($root in @("${env:ProgramFiles(x86)}\Windows Kits\10\bin", "$env:ProgramFiles\Windows Kits\10\bin")) {
        if ($root -and (Test-Path -LiteralPath $root)) {
            $candidates += Get-ChildItem -LiteralPath $root -Directory |
                Where-Object { $_.Name -match '^\d+(\.\d+)+$' } |
                Sort-Object { [version] $_.Name } -Descending |
                ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' }
        }
    }
    $onPath = Get-Command 'signtool.exe' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) { $candidates += $onPath.Source }
    $found = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
    if (-not $found) { throw 'Brak signtool.exe (Windows SDK). Wskaz go zmienna SIGNTOOL albo parametrem -SignToolPath.' }
    return $found
}

$signTool = Find-SignTool $SignToolPath
$pfxPath = Join-Path ([IO.Path]::GetTempPath()) ("vncalert-codesign-" + [guid]::NewGuid().ToString('N') + '.pfx')
try {
    [IO.File]::WriteAllBytes($pfxPath, [Convert]::FromBase64String($PfxBase64.Trim()))
    Write-Host "signtool: $signTool"
    Write-Host ("Podpisuje " + $files.Count + " plik(ow), znacznik czasu: $TimestampUrl")
    & $signTool sign /fd SHA256 /td SHA256 /tr $TimestampUrl /f $pfxPath /p $PfxPassword /d $Description @files
    if ($LASTEXITCODE -ne 0) { throw "signtool sign zakonczyl sie kodem $LASTEXITCODE" }
}
finally {
    if (Test-Path -LiteralPath $pfxPath) { Remove-Item -LiteralPath $pfxPath -Force }
}

# Kontrola: podpis musi byc waznym podpisem Authenticode wg zasad Windows (lancuch do zaufanego CA).
& $signTool verify /pa /v @files
if ($LASTEXITCODE -ne 0) { throw "signtool verify zakonczyl sie kodem $LASTEXITCODE" }
foreach ($file in $files) {
    $signature = Get-AuthenticodeSignature -LiteralPath $file
    Write-Host ("PASS: podpisano " + [IO.Path]::GetFileName($file) + " (" + $signature.SignerCertificate.Subject + ")")
}
if ($env:GITHUB_STEP_SUMMARY) {
    ("Podpis Authenticode: " + $files.Count + " plik(ow), " + (Get-AuthenticodeSignature -LiteralPath $files[0]).SignerCertificate.Subject) |
        Out-File $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8
}
