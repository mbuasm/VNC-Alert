# Uruchamiane jako SYSTEM przez zadanie, ktore rejestruje Register-UpdateTask.ps1; grupa Users moze je tylko uruchomic.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ApplicationDirectory,
    [Parameter(Mandatory)][string]$DataDirectory,
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [switch]$DefinitionsOnly
)
$ErrorActionPreference = 'Stop'

$work = Join-Path $DataDirectory 'updates-admin'
$taskLog = Join-Path $work 'install-task.log'
$resultPath = Join-Path $work 'install-task.result.json'
$verifiedPath = Join-Path $work 'install-task.verified.json'
$pendingPath = Join-Path $work 'pending-install.json'
# Znacznik ciszy watchdoga zapisany przez "--stop" uruchomione jako SYSTEM (WatchdogTask.SystemMarkerPath w programie).
$systemMarker = Join-Path $work 'stop.requested'
$account = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$trusted = @('S-1-5-18', 'S-1-5-32-544')

function Test-AdminOnlyDirectory([string]$Path) {
    # Zwraca opis problemu albo pusty tekst: katalog istnieje, nie jest przekierowaniem, nalezy do Administratorow/SYSTEM
    # i nikt poza nimi nie ma w nim zapisu, usuwania ani zmiany uprawnien.
    if (-not [System.IO.Directory]::Exists($Path)) { return "brak katalogu $Path" }
    $info = New-Object System.IO.DirectoryInfo($Path)
    if ($info.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { return "katalog $Path jest przekierowaniem" }
    $acl = $info.GetAccessControl()
    $owner = $acl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value
    if ($trusted -notcontains $owner) { return "wlascicielem $Path jest $owner" }
    $unsafe = [System.Security.AccessControl.FileSystemRights]'Write, Delete, ChangePermissions, TakeOwnership, DeleteSubdirectoriesAndFiles'
    foreach ($rule in $acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow) { continue }
        if ($rule.PropagationFlags -band [System.Security.AccessControl.PropagationFlags]::InheritOnly) { continue }
        if ($trusted -contains $rule.IdentityReference.Value) { continue }
        if ($rule.FileSystemRights -band $unsafe) { return ($rule.IdentityReference.Value + ' moze zmieniac ' + $Path) }
    }
    return ''
}

function Write-TaskLog([string]$Text) {
    try {
        if ([System.IO.File]::Exists($taskLog) -and (New-Object System.IO.FileInfo($taskLog)).Length -gt 1MB) {
            [System.IO.File]::Delete($taskLog)
        }
        [System.IO.File]::AppendAllText($taskLog, ((Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + ' ' + $Text + "`r`n"))
    } catch { }
}

function Write-TaskResult($RequestId, $Version, [int]$ExitCode, $SetupExitCode, [string]$Message) {
    $payload = [ordered]@{
        requestId = $RequestId
        version = $Version
        exitCode = $ExitCode
        setupExitCode = $SetupExitCode
        finishedAt = (Get-Date).ToUniversalTime().ToString('o')
        account = $account
        message = $Message
    }
    $json = $payload | ConvertTo-Json -Compress
    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        try { [System.IO.File]::WriteAllText($resultPath, $json); break }
        catch { Start-Sleep -Milliseconds 500 }
    }
    Write-TaskLog ('wynik ' + $ExitCode + ': ' + $Message)
}

function Get-Sha256([System.IO.Stream]$Stream) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return ([System.BitConverter]::ToString($sha.ComputeHash($Stream))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Write-TaskEvent([string]$Text) {
    try {
        $eventcreate = Join-Path $env:SystemRoot 'System32\eventcreate.exe'
        $null = & $eventcreate /T ERROR /ID 7 /L APPLICATION /SO 'VNC Alert update' /D $Text 2>&1
    } catch { }
}

function Remove-SystemMarker {
    $lastError = ''
    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        if (-not [System.IO.File]::Exists($systemMarker)) { return $true }
        try { [System.IO.File]::Delete($systemMarker); Write-TaskLog 'zdjeto znacznik zatrzymania instalatora po instalacji'; return $true }
        catch { $lastError = $_.Exception.Message; Start-Sleep -Milliseconds 500 }
    }
    if (-not [System.IO.File]::Exists($systemMarker)) { return $true }
    Write-TaskLog ('nie zdjeto znacznika zatrzymania instalatora ' + $systemMarker + ' po ' + $attempt + ' probach (' + $lastError +
        ') - watchdog nie wznowi programu do 10 min od zatrzymania; operator moze uruchomic program recznie.')
    return $false
}

if ($DefinitionsOnly) { return }

foreach ($problem in @((Test-AdminOnlyDirectory $work), (Test-AdminOnlyDirectory $DataDirectory))) {
    if ($problem) {
        Write-TaskEvent ('Zadanie aktualizacji VNC Alert odmowilo pracy (kod 7): ' + $problem + '. Katalog danych i updates-admin ' +
            'musza nalezec do Administratorow/SYSTEM i nie moga dawac zapisu innym kontom - napraw uprawnienia ponowna instalacja ' +
            'z konta administratora.')
        exit 7
    }
}

try {
    Write-TaskLog ('start zadania (konto ' + $account + ', wersja zadania ' + $ExpectedVersion + ')')
    if ([System.IO.File]::Exists($verifiedPath)) { [System.IO.File]::Delete($verifiedPath) }

    $exe = Join-Path $ApplicationDirectory 'MaszynyAlert.App.exe'
    $installed = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion
    if ($installed -ne $ExpectedVersion) {
        Write-TaskResult $null $null 8 $null ('Zadanie zarejestrowano dla wersji programu ' + $ExpectedVersion + ', a zainstalowana jest ' + $installed + ' - zadanie nic nie uruchamia; instalacja przez okno UAC.')
        exit 8
    }

    $verify = Start-Process -FilePath $exe -ArgumentList '--update-task-verify' -WorkingDirectory $ApplicationDirectory `
        -WindowStyle Hidden -PassThru
    # Uchwyt pobrany przed czekaniem - bez tego Windows PowerShell 5.1 potrafi zwrocic pusty ExitCode.
    $null = $verify.Handle
    if (-not $verify.WaitForExit(900000)) {
        try { $verify.Kill() } catch { }
        Write-TaskResult $null $null 5 $null 'Sprawdzenie pliku przekroczylo 15 min.'
        exit 5
    }
    $verified = $null
    if ([System.IO.File]::Exists($verifiedPath)) {
        $verified = [System.IO.File]::ReadAllText($verifiedPath) | ConvertFrom-Json
    }
    if ($null -eq $verified) {
        Write-TaskResult $null $null 5 $null ('Sprawdzenie pliku zakonczylo sie bez wyniku (kod ' + $verify.ExitCode + ').')
        exit 5
    }
    if ($verify.ExitCode -ne 0 -or -not $verified.accepted) {
        Write-TaskResult $verified.requestId $verified.version 6 $null ([string]$verified.message)
        exit 6
    }

    $setup = [System.IO.Path]::GetFullPath([string]$verified.setupPath)
    $run = [System.IO.Path]::GetDirectoryName($setup)
    if ([System.IO.Path]::GetDirectoryName($run) -ne [System.IO.Path]::GetFullPath($work) -or
        -not ([System.IO.Path]::GetFileName($run)).StartsWith('run-')) {
        Write-TaskResult $verified.requestId $verified.version 5 $null ('Kopia instalatora poza katalogiem przebiegu: ' + $setup)
        exit 5
    }
    $setupLog = Join-Path $work ('install-' + $verified.version + '.log')
    # Kopia jest otwarta bez wspoldzielenia zapisu i usuwania od sumy do konca instalatora.
    $guard = [System.IO.File]::Open($setup, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
        $actual = Get-Sha256 $guard
        if ($actual -ne ([string]$verified.sha256).ToLowerInvariant()) {
            Write-TaskResult $verified.requestId $verified.version 6 $null 'Kopia instalatora zmienila sie po sprawdzeniu (SHA-256) - odrzucona.'
            exit 6
        }
        Write-TaskLog ('instalacja ' + $verified.fileName + ' (SHA-256 ' + $actual + ')')
        $setupArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG="' + $setupLog + '"'))
        $process = Start-Process -FilePath $setup -ArgumentList $setupArguments -WorkingDirectory $run -PassThru
        $null = $process.Handle
        $process.WaitForExit()
        $setupExitCode = $process.ExitCode
    }
    finally { $guard.Dispose() }

    $null = Remove-SystemMarker
    try { [System.IO.Directory]::Delete($run, $true) } catch { Write-TaskLog ('nie usunieto katalogu przebiegu: ' + $_.Exception.Message) }
    if ($setupExitCode -ne 0) {
        # Znacznik proby zostaje: po restarcie w starej wersji program nie ponawia sam, czeka na czlowieka.
        Write-TaskResult $verified.requestId $verified.version 4 $setupExitCode ('Instalator zakonczyl sie kodem ' + $setupExitCode + ' - log: ' + $setupLog)
        exit 4
    }
    if ([System.IO.File]::Exists($pendingPath)) { [System.IO.File]::Delete($pendingPath) }
    Write-TaskResult $verified.requestId $verified.version 0 0 ('Zainstalowano ' + $verified.fileName + '.')
    exit 0
}
catch {
    try { Write-TaskResult $null $null 5 $null $_.Exception.Message } catch { }
    exit 5
}
