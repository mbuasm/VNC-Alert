# Test zmienia Program Files/HKLM; wyłącznie na jednorazowym runnerze GitHub Actions.
[CmdletBinding()]
param(
    [string]$SetupPath = 'artifacts/installer/VncAlert-Setup.exe',
    [string]$BaselineSetupPath = 'artifacts/installer-baseline/setup/VncAlert-Setup.exe',
    [string]$ProbePath = 'artifacts/installer-probe/InstallerProbe.exe'
)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows') {
    throw 'Ten test instalacji jest przeznaczony wyłącznie dla jednorazowego runnera Windows CI.'
}
$setup = (Resolve-Path -LiteralPath $SetupPath).Path
$baseline = (Resolve-Path -LiteralPath $BaselineSetupPath).Path
$probe = (Resolve-Path -LiteralPath $ProbePath).Path
$previousApp = Join-Path $env:ProgramFiles 'MaszynyAlert'
$currentApp = Join-Path $env:ProgramFiles 'VNC Alert'
$app = $previousApp
$data = Join-Path $env:ProgramData 'MaszynyAlert'
$logs = [IO.Path]::GetFullPath('artifacts/installer-test')
$runKey = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'VNC Alert.lnk'
$startShortcut = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'VNC Alert.lnk'
New-Item -ItemType Directory -Path $logs -Force | Out-Null
if ((Test-Path -LiteralPath $previousApp) -or (Test-Path -LiteralPath $currentApp) -or (Test-Path -LiteralPath $data)) {
    throw 'Runner zawiera już instalację lub dane VNC Alert (Program Files\MaszynyAlert, Program Files\VNC Alert albo ProgramData\MaszynyAlert).'
}
function Run-Setup([string]$Executable, [string]$LogName, [bool]$ExpectFailure = $false) {
    $logPath = Join-Path $logs $LogName
    $arguments = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="' + $logPath + '"'
    $process = Start-Process -FilePath $Executable -ArgumentList $arguments -PassThru -WindowStyle Hidden
    if (!$process.WaitForExit(180000)) { throw 'Przekroczono czas instalacji.' }
    if (($process.ExitCode -ne 0) -ne $ExpectFailure) {
        if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath -Tail 60 | Write-Output }
        throw "Nieoczekiwany kod $($process.ExitCode); oczekiwana awaria: $ExpectFailure; log: $LogName"
    }
    "PASS: $LogName, exit=$($process.ExitCode)" | Add-Content (Join-Path $logs 'checks.txt')
}
function Run-Probe([string]$Operation) {
    & $probe $Operation $data
    if ($LASTEXITCODE -ne 0) { throw "Próbnik DPAPI/ACL nie przeszedł: $Operation" }
    "PASS: DPAPI/ACL $Operation" | Add-Content (Join-Path $logs 'checks.txt')
}
function Get-DataHashes {
    $result = @{}
    foreach ($name in @('config.json', 'rules.json', 'secrets.dat', 'history.csv', 'downtime.jsonl', 'logs/retained.log', 'captures/retained.txt')) {
        $result[$name] = (Get-FileHash -LiteralPath (Join-Path $data $name)).Hash
    }
    return $result
}
function Assert-DataUnchanged($Hashes) {
    foreach ($name in $Hashes.Keys) {
        if ((Get-FileHash -LiteralPath (Join-Path $data $name)).Hash -ne $Hashes[$name]) {
            throw "Instalator zmienił dane: $name"
        }
    }
}
function Assert-ReadOnlyAcl {
    foreach ($name in @('config.json', 'rules.json', 'secrets.dat')) {
        $acl = Get-Acl -LiteralPath (Join-Path $data $name)
        $rules = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
        $users = @($rules | Where-Object { $_.IdentityReference.Value -eq 'S-1-5-32-545' })
        if (!$acl.AreAccessRulesProtected -or !$users) { throw "Brak ochrony ACL: $name" }
        foreach ($rule in $rules) {
            if ($rule.AccessControlType -eq 'Allow' -and
                $rule.IdentityReference.Value -notin @('S-1-5-18', 'S-1-5-32-544') -and
                ($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]'Write, Delete, ChangePermissions, TakeOwnership')) {
                throw "Nieadministrator może zmieniać chroniony plik: $name"
            }
        }
    }
}
function Assert-WritableDataDirectories {
    # Katalogi, do ktorych operator MUSI moc pisac bez UAC: logi, zrzuty, obrazy testowe, znacznik
    # komendy zatrzymania (watchdog), wspolne instrukcje naprawy grupy i wspolna konfiguracja z Panelu
    # administracyjnego. Sam zapis probnikiem sprawdza 'Run-Probe operator'; tutaj pilnujemy, ze katalogi
    # w ogole powstaly przy instalacji.
    foreach ($name in @('logs', 'captures', 'test-ekran', 'state', 'group-instructions', 'shared-config')) {
        $target = Join-Path $data $name
        if (!(Test-Path -LiteralPath $target -PathType Container)) { throw "Brak katalogu danych: $name" }
        $acl = Get-Acl -LiteralPath $target
        $users = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) |
            Where-Object { $_.IdentityReference.Value -eq 'S-1-5-32-545' -and $_.AccessControlType -eq 'Allow' -and
                ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]'Write') })
        if (!$users) { throw "Operator nie moze pisac do katalogu: $name" }
    }
    'PASS: katalogi danych zapisywalne przez operatora (logs, captures, test-ekran, state, group-instructions, shared-config).' |
        Add-Content (Join-Path $logs 'checks.txt')
}
function Assert-PathEntry([bool]$Expected) {
    $path = (Get-ItemPropertyValue 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment' 'Path')
    $matching = @(($path -split ';') | Where-Object { $_ -ne '' -and $_.TrimEnd('\') -ieq $app.TrimEnd('\') })
    if ($Expected -and $matching.Count -ne 1) {
        throw "Katalog programu ma być dokładnie raz w PATH, a jest $($matching.Count) razy."
    }
    if (!$Expected -and $matching.Count -ne 0) { throw 'Katalog programu pozostał w PATH.' }
    if ($Expected -and ($path -match (';;' + [Regex]::Escape($app)) -or $path -match ('^;' + [Regex]::Escape($app)))) { throw "PATH ma pusty wpis przed katalogiem programu: $path" }
    "PASS: PATH z komendą vncalert, wpis obecny: $Expected." | Add-Content (Join-Path $logs 'checks.txt')
}
$watchdogTask = 'VNC Alert watchdog'
function Get-WatchdogTask {
    # Przez COM Schedule.Service, nie przez modul ScheduledTasks: test ma sprawdzac to samo, co widzi
    # system, i nie zalezec od tego, czy modul da sie zaladowac w biezacym silniku PowerShell.
    $service = New-Object -ComObject Schedule.Service
    $service.Connect()
    try { return $service.GetFolder('\').GetTask($watchdogTask) }
    catch { return $null }
}
function Assert-WatchdogTask([bool]$Expected) {
    $task = Get-WatchdogTask
    if (!$Expected) {
        if ($task) { throw 'Zadanie watchdoga pozostalo w Harmonogramie zadan.' }
        'PASS: watchdog usuniety z Harmonogramu zadan.' | Add-Content (Join-Path $logs 'checks.txt')
        return
    }
    if (!$task) { throw 'Brak zadania watchdoga w Harmonogramie zadan po instalacji.' }
    $definition = $task.Definition
    $groupId = [string]$definition.Principal.GroupId
    $groupSid = if ($groupId -match '^S-1-') { $groupId } else {
        (New-Object Security.Principal.NTAccount($groupId)).Translate([Security.Principal.SecurityIdentifier]).Value }
    if ($groupSid -ne 'S-1-5-32-545') { throw "Watchdog nie dziala dla grupy Users: $groupId ($groupSid)." }
    if ($definition.Principal.RunLevel -ne 0) { throw 'Watchdog nie moze dzialac z podwyzszonymi uprawnieniami (RunLevel).' }
    if ($definition.Settings.MultipleInstances -ne 2) { throw "Watchdog: MultipleInstances = $($definition.Settings.MultipleInstances), oczekiwano IgnoreNew (2)." }
    if ([string]$definition.Settings.ExecutionTimeLimit -ne 'PT0S') { throw "Watchdog: ExecutionTimeLimit = $($definition.Settings.ExecutionTimeLimit), oczekiwano PT0S." }
    $intervals = @(foreach ($trigger in $definition.Triggers) { [string]$trigger.Repetition.Interval })
    if ($intervals -notcontains 'PT1M') { throw "Watchdog bez powtarzania co minute: $($intervals -join ', ')" }
    if ($definition.Actions.Count -ne 1) { throw 'Zadanie watchdoga ma wiecej niz jedna akcje.' }
    $action = $definition.Actions.Item(1)
    if ($action.Path -ine $exe -or $action.Arguments -cne '--if-not-running') {
        throw "Zadanie watchdoga uruchamia cos innego: $($action.Path) $($action.Arguments)"
    }
    # Zadanie co minute uruchamialoby aplikacje w srodku dalszych sprawdzen (a przy ich koncu nie ma
    # juz plikow programu), wiec zaraz po potwierdzeniu wylaczamy je na czas testu. Prawdziwy powrot
    # zabitego procesu sprawdza sie na stanowisku - CI nie ma sesji interaktywnej, w ktorej zadanie
    # z zasada InteractiveToken w ogole by wystartowalo.
    $task.Enabled = $false
    'PASS: watchdog zarejestrowany dla zalogowanego uzytkownika; wylaczony na czas testu.' |
        Add-Content (Join-Path $logs 'checks.txt')
}

$updateTask = 'VNC Alert update'
$updatesAdmin = Join-Path $data 'updates-admin'
$downloadsDir = Join-Path $data 'state\updates'
$taskResultPath = Join-Path $updatesAdmin 'install-task.result.json'
$stopMarkerPath = Join-Path $data 'state\stop.requested'
$systemMarkerPath = Join-Path $updatesAdmin 'stop.requested'
# Prawa do zadania zastrzezone dla SYSTEM i Administratorow: GENERIC_ALL/WRITE, DELETE, WRITE_DAC, WRITE_OWNER oraz
# zapis danych, rozszerzonych atrybutow i atrybutow pliku zadania.
$taskWriteMask = 0x10000000 -bor 0x40000000 -bor 0x00010000 -bor 0x00040000 -bor 0x00080000 -bor 0x2 -bor 0x4 -bor 0x10 -bor 0x100
$writeRights = [Security.AccessControl.FileSystemRights]'Write, Delete, ChangePermissions, TakeOwnership, DeleteSubdirectoriesAndFiles'
$script:standardCredential = $null
$script:standardUser = $null
function Get-UpdateTask {
    $service = New-Object -ComObject Schedule.Service
    $service.Connect()
    try { return $service.GetFolder('\').GetTask($updateTask) }
    catch { return $null }
}
function Get-InstalledFileVersion { return [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion }
function Assert-NotWritableByUsers([string]$Path) {
    $acl = Get-Acl -LiteralPath $Path
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne 'Allow') { continue }
        if ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) { continue }
        if ($rule.IdentityReference.Value -notin @('S-1-5-32-545', 'S-1-5-11', 'S-1-1-0', 'S-1-5-4')) { continue }
        if ($rule.FileSystemRights -band $writeRights) { throw "Zwykly uzytkownik ($($rule.IdentityReference.Value)) moze zmienic: $Path" }
    }
}
function Assert-UpdateTask([bool]$Expected) {
    $task = Get-UpdateTask
    if (!$Expected) {
        if ($task) { throw 'Zadanie aktualizacji istnieje, a nie powinno (odinstalowanie albo dane nieprzygotowane).' }
        'PASS: brak zadania aktualizacji w Harmonogramie zadan.' | Add-Content (Join-Path $logs 'checks.txt')
        return
    }
    if (!$task) { throw "Brak zadania '$updateTask' po instalacji." }
    # Definicja z obiektu COM, nie z wzorcow XML: Windows pomija w XML zadania ustawienia domyslne (np. IgnoreNew), a zmienna
    # petli $expected bylaby tym samym co parametr [bool]$Expected (PowerShell nie rozroznia wielkosci liter).
    $definition = $task.Definition
    $userId = [string]$definition.Principal.UserId
    $userSid = if ($userId -match '^S-1-') { $userId } else {
        (New-Object Security.Principal.NTAccount($userId)).Translate([Security.Principal.SecurityIdentifier]).Value }
    if ($userSid -ne 'S-1-5-18') { throw "Zadanie aktualizacji dziala jako $userId ($userSid), nie SYSTEM." }
    if ($definition.Principal.RunLevel -ne 1) { throw 'Zadanie aktualizacji bez najwyzszych uprawnien (RunLevel).' }
    if ($definition.Settings.MultipleInstances -ne 2) { throw "Zadanie aktualizacji: MultipleInstances = $($definition.Settings.MultipleInstances), oczekiwano IgnoreNew (2)." }
    if ($definition.Triggers.Count -ne 0) { throw 'Zadanie aktualizacji ma wyzwalacz - ma ruszac wylacznie na zadanie programu.' }
    if ($definition.Actions.Count -ne 1) { throw 'Zadanie aktualizacji ma wiecej niz jedna akcje.' }
    $action = $definition.Actions.Item(1)
    if ($action.Path -ine (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -or
        $action.Arguments -notmatch [Regex]::Escape((Join-Path $app 'installer\Update-Task.ps1'))) {
        throw "Zadanie aktualizacji uruchamia cos innego: $($action.Path) $($action.Arguments)"
    }
    $version = Get-InstalledFileVersion
    if ($action.Arguments -notmatch ('-ExpectedVersion "?' + [Regex]::Escape($version) + '"?(\s|$)')) {
        throw "Zadanie aktualizacji nie jest zwiazane z zainstalowana wersja $version : $($action.Arguments)"
    }
    # Deskryptor zadania (DACL): nikt poza SYSTEM i Administratorami nie zmienia ani nie usuwa zadania; Users uruchamiaja.
    $sddl = $task.GetSecurityDescriptor(4)
    $descriptor = New-Object Security.AccessControl.RawSecurityDescriptor($sddl)
    $usersCanRun = $false
    foreach ($ace in $descriptor.DiscretionaryAcl) {
        if ($ace.AceQualifier -ne [Security.AccessControl.AceQualifier]::AccessAllowed) { continue }
        $sid = $ace.SecurityIdentifier.Value
        if ($sid -in @('S-1-5-18', 'S-1-5-32-544')) { continue }
        if ($ace.AccessMask -band $taskWriteMask) {
            throw ('Zadanie aktualizacji: {0} moze je zmienic albo usunac (maska 0x{1:X8}; {2}).' -f $sid, $ace.AccessMask, $sddl)
        }
        if ($sid -eq 'S-1-5-32-545' -and ($ace.AccessMask -band (0x20000000 -bor 0x20))) { $usersCanRun = $true }
    }
    if (!$usersCanRun) { throw "Grupa Users nie moze uruchomic zadania aktualizacji: $sddl" }
    foreach ($script in @('Update-Task.ps1', 'Register-UpdateTask.ps1')) {
        Assert-NotWritableByUsers (Join-Path $app "installer\$script")
    }
    "PASS: zadanie aktualizacji jako SYSTEM, bez wyzwalaczy, wersja $version, Users tylko odczyt i uruchomienie ($sddl)." |
        Add-Content (Join-Path $logs 'checks.txt')
}
function Assert-UpdatesAdminDirectory {
    if (!(Test-Path -LiteralPath $updatesAdmin -PathType Container)) { throw 'Brak katalogu updates-admin.' }
    if ((Get-Item -LiteralPath $updatesAdmin -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'updates-admin jest przekierowany (junction/symlink).'
    }
    if (!(Get-Acl -LiteralPath $updatesAdmin).AreAccessRulesProtected) { throw 'updates-admin dziedziczy uprawnienia.' }
    Assert-NotWritableByUsers $data
    $items = @(Get-Item -LiteralPath $updatesAdmin -Force) + @(Get-ChildItem -LiteralPath $updatesAdmin -Force -Recurse)
    foreach ($item in $items) {
        $acl = Get-Acl -LiteralPath $item.FullName
        $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
        if ($owner -notin @('S-1-5-32-544', 'S-1-5-18')) {
            throw "Wlascicielem $($item.FullName) jest $owner - wlasciciel moze sam zmienic uprawnienia."
        }
        foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
            if ($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -notin @('S-1-5-18', 'S-1-5-32-544') -and
                ($rule.FileSystemRights -band $writeRights)) {
                throw "Zwykly uzytkownik ($($rule.IdentityReference.Value)) moze zmieniac $($item.FullName)."
            }
        }
    }
    $downloadsAcl = if (Test-Path -LiteralPath $downloadsDir) { $downloadsDir } else { Join-Path $data 'state' }
    $users = @((Get-Acl -LiteralPath $downloadsAcl).GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) |
        Where-Object { $_.IdentityReference.Value -eq 'S-1-5-32-545' -and $_.AccessControlType -eq 'Allow' -and
            ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]'Write') })
    if (!$users) { throw 'Operator nie moze pisac do state\updates (pobrania aktualizacji).' }
    'PASS: updates-admin w katalogu danych tylko dla administratorow i SYSTEM; state\updates (pobrania) zapisywalny dla operatora.' |
        Add-Content (Join-Path $logs 'checks.txt')
}
function New-StandardUser {
    # Konto bez grupy Administratorzy: z niego program wyzwala zadanie na stanowisku "obcego klienta".
    if ($script:standardCredential) { return }
    $name = 'vncstd' + (Get-Random -Minimum 1000 -Maximum 9999)
    $password = 'Vn!' + [Guid]::NewGuid().ToString('N').Substring(0, 8) + 'aZ7'
    & net.exe user $name $password /add | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Nie utworzono konta testowego $name." }
    $script:standardUser = $name
    $script:standardCredential = [pscredential]::new(".\$name", (ConvertTo-SecureString $password -AsPlainText -Force))
}
function Remove-StandardUser {
    if ($script:standardUser) { & net.exe user $script:standardUser /delete | Out-Null }
    $script:standardUser = $null
    $script:standardCredential = $null
}
function Invoke-SchtasksAs([pscredential]$Credential, [string]$Arguments) {
    $out = Join-Path $logs ('schtasks-' + [Guid]::NewGuid().ToString('N') + '.txt')
    $process = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\schtasks.exe') -ArgumentList $Arguments `
        -Credential $Credential -WorkingDirectory $env:SystemRoot -RedirectStandardOutput $out -RedirectStandardError "$out.err" -PassThru -Wait
    return $process.ExitCode
}
function Wait-TaskResult([string]$RequestId, [datetime]$After, [int]$Seconds = 180) {
    $waited = [Diagnostics.Stopwatch]::StartNew()
    while ($waited.Elapsed.TotalSeconds -lt $Seconds) {
        if ((Test-Path -LiteralPath $taskResultPath) -and (Get-Item -LiteralPath $taskResultPath).LastWriteTimeUtc -gt $After) {
            $result = $null
            try { $result = Get-Content -LiteralPath $taskResultPath -Raw | ConvertFrom-Json } catch { }
            # Jak pomocnik operatora: wynik z naszym identyfikatorem albo bez identyfikatora, zapisany po wyzwoleniu.
            if ($result -and (!$RequestId -or $result.requestId -eq $RequestId -or [string]::IsNullOrEmpty([string]$result.requestId))) { return $result }
        }
        Start-Sleep -Milliseconds 500
    }
    $taskLog = Join-Path $updatesAdmin 'install-task.log'
    if (Test-Path -LiteralPath $taskLog) { Get-Content -LiteralPath $taskLog -Tail 40 | Out-Host }
    throw "Zadanie aktualizacji nie zwrocilo wyniku w $Seconds s (zadanie: $RequestId)."
}
function Wait-TaskFinished([datetime]$After, [int]$Seconds = 120) {
    # Gdy zadanie celowo nie pisze wyniku (katalog niechroniony), czekamy na koniec przebiegu w Harmonogramie.
    $waited = [Diagnostics.Stopwatch]::StartNew()
    while ($waited.Elapsed.TotalSeconds -lt $Seconds) {
        $task = Get-UpdateTask
        if ($task -and $task.State -ne 4 -and $task.LastRunTime -gt $After) { return $task.LastTaskResult }
        Start-Sleep -Milliseconds 500
    }
    throw "Zadanie aktualizacji nie skonczylo przebiegu w $Seconds s."
}
function Test-UpdateTaskAsStandardUser {
    New-StandardUser
    Remove-Item -LiteralPath (Join-Path $downloadsDir 'update-request.json') -Force -ErrorAction SilentlyContinue
    $before = (Get-Date).ToUniversalTime().AddSeconds(-1)
    $run = Invoke-SchtasksAs $script:standardCredential "/run /tn `"$updateTask`""
    if ($run -ne 0) { throw "Konto standardowe nie moze uruchomic zadania aktualizacji (schtasks /run, kod $run)." }
    $result = Wait-TaskResult $null $before 120
    if ($result.account -ne 'S-1-5-18') { throw "Zadanie uruchomione z konta standardowego dzialalo jako $($result.account), nie SYSTEM." }
    if ([int]$result.exitCode -ne 6 -or $result.message -notmatch 'Brak') {
        throw "Zadanie bez zadania programu mialo odmowic z powodu braku zadania (6), a zwrocilo $($result.exitCode): $($result.message)"
    }
    foreach ($forbidden in @("/change /tn `"$updateTask`" /disable", "/delete /tn `"$updateTask`" /f")) {
        if ((Invoke-SchtasksAs $script:standardCredential $forbidden) -eq 0) { throw "Konto standardowe wykonalo: schtasks $forbidden" }
    }
    $task = Get-UpdateTask
    if (!$task -or !$task.Enabled) { throw 'Konto standardowe usunelo albo wylaczylo zadanie aktualizacji.' }
    'PASS: konto standardowe uruchamia zadanie aktualizacji (dziala jako SYSTEM, bez zadania odmawia), nie wylacza i nie usuwa go.' |
        Add-Content (Join-Path $logs 'checks.txt')
}
function New-CatalogCertificate {
    $rsa = [Security.Cryptography.RSA]::Create(2048)
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=VNC Alert test catalog', $rsa,
        [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $san = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
    $san.AddIpAddress([Net.IPAddress]::Loopback)
    $request.CertificateExtensions.Add($san.Build())
    $usage = [Security.Cryptography.OidCollection]::new()
    $null = $usage.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'))
    $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($usage, $false))
    $created = $request.CreateSelfSigned([DateTimeOffset]::Now.AddDays(-1), [DateTimeOffset]::Now.AddDays(2))
    $flags = [Security.Cryptography.X509Certificates.X509KeyStorageFlags]'MachineKeySet, PersistKeySet, Exportable'
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($created.Export('Pfx', 'vnc-test'), 'vnc-test', $flags)
    foreach ($storeName in @('My', 'Root')) {
        $store = [Security.Cryptography.X509Certificates.X509Store]::new($storeName, 'LocalMachine')
        $store.Open('ReadWrite')
        if ($storeName -eq 'My') { $store.Add($certificate) }
        else { $store.Add([Security.Cryptography.X509Certificates.X509Certificate2]::new($certificate.RawData)) }
        $store.Close()
    }
    return $certificate
}
function Remove-CatalogCertificate([string]$Thumbprint) {
    foreach ($storeName in @('My', 'Root')) {
        $store = [Security.Cryptography.X509Certificates.X509Store]::new($storeName, 'LocalMachine')
        $store.Open('ReadWrite')
        foreach ($item in @($store.Certificates.Find('FindByThumbprint', $Thumbprint, $false))) { $store.Remove($item) }
        $store.Close()
    }
}
function Start-CatalogServer([string]$IndexText) {
    $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $probe.Start(); $port = $probe.LocalEndpoint.Port; $probe.Stop()
    $certificate = New-CatalogCertificate
    & netsh.exe http add sslcert "ipport=127.0.0.1:$port" "certhash=$($certificate.Thumbprint)" 'appid={6a0bd0c8-3f1e-4b77-9a2f-b1a5e3f0c001}' | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "netsh http add sslcert zakonczylo sie kodem $LASTEXITCODE." }
    $prefix = "https://127.0.0.1:$port/"
    $job = Start-Job -ScriptBlock {
        param($Prefix, $Text)
        $listener = [Net.HttpListener]::new()
        $listener.Prefixes.Add($Prefix)
        $listener.Start()
        try {
            while ($true) {
                $context = $listener.GetContext()
                if ($context.Request.Url.AbsolutePath -eq '/INDEX.md') {
                    $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
                    $context.Response.ContentType = 'text/markdown; charset=utf-8'
                    $context.Response.ContentLength64 = $bytes.Length
                    $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
                }
                else { $context.Response.StatusCode = 404 }
                $context.Response.Close()
            }
        }
        finally { $listener.Stop() }
    } -ArgumentList $prefix, $IndexText
    $deadline = (Get-Date).AddSeconds(30)
    while ($true) {
        try { if ((Invoke-WebRequest -Uri "${prefix}INDEX.md" -SkipCertificateCheck -TimeoutSec 5).StatusCode -eq 200) { break } } catch { }
        if ((Get-Date) -gt $deadline) { Receive-Job $job | Out-Host; throw 'Lokalny katalog wydan (https) nie wystartowal.' }
        Start-Sleep -Milliseconds 300
    }
    return [pscustomobject]@{ Url = $prefix; Job = $job; Port = $port; Thumbprint = $certificate.Thumbprint }
}
function Stop-CatalogServer($Server) {
    if (!$Server) { return }
    Stop-Job -Job $Server.Job -ErrorAction SilentlyContinue
    Remove-Job -Job $Server.Job -Force -ErrorAction SilentlyContinue
    & netsh.exe http delete sslcert "ipport=127.0.0.1:$($Server.Port)" | Out-Null
    Remove-CatalogCertificate $Server.Thumbprint
}
function Invoke-UpdateTask([string]$FileName) {
    $requestId = [Guid]::NewGuid().ToString('N')
    $request = [ordered]@{ requestId = $requestId; fileName = $FileName } | ConvertTo-Json -Compress
    [IO.File]::WriteAllText((Join-Path $downloadsDir 'update-request.json'), $request)
    $before = (Get-Date).ToUniversalTime().AddSeconds(-1)
    $run = Invoke-SchtasksAs $script:standardCredential "/run /tn `"$updateTask`""
    if ($run -ne 0) { throw "schtasks /run z konta standardowego zakonczylo sie kodem $run." }
    $result = Wait-TaskResult $requestId $before 300
    if ($result.requestId -ne $requestId) { throw "Wynik zadania nie niesie identyfikatora zadania: $($result | ConvertTo-Json -Compress)" }
    return $result
}
function Assert-Refused($Result, [string]$Reason, [string]$Case) {
    if ([int]$Result.exitCode -ne 6 -or $Result.message -notmatch $Reason) {
        throw "Zadanie ($Case) mialo odmowic z powodem '$Reason', a zwrocilo $($Result.exitCode): $($Result.message)"
    }
    if ($null -ne $Result.setupExitCode) { throw "Instalator wystartowal mimo odmowy ($Case)." }
}
function New-NewerInstaller {
    $current = Get-InstalledFileVersion
    $parsed = [Version]$current
    $newer = [Version]::new($parsed.Major, $parsed.Minor, $parsed.Build, $parsed.Revision + 1).ToString()
    if ($newer.Length -ne $current.Length) { throw "Wersja $newer ma inna dlugosc niz $current - test zaklada te sama." }
    $package = [IO.Path]::GetFullPath('publish/app')
    if ((Get-InstalledFileVersion) -ne [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $package 'MaszynyAlert.App.exe')).FileVersion) {
        throw 'Paczka publish/app ma inna wersje niz zainstalowany program.'
    }
    $copy = Join-Path $logs 'newer-package'
    if (Test-Path -LiteralPath $copy) { Remove-Item -LiteralPath $copy -Recurse -Force }
    Copy-Item -LiteralPath $package -Destination $copy -Recurse
    $target = Join-Path $copy 'MaszynyAlert.App.exe'
    $bytes = [IO.File]::ReadAllBytes($target)
    $latin = [Text.Encoding]::GetEncoding(28591)
    $text = $latin.GetString($bytes)
    $signature = $text.IndexOf($latin.GetString([byte[]](0xBD, 0x04, 0xEF, 0xFE)), [StringComparison]::Ordinal)
    if ($signature -lt 0) { throw 'Brak VS_FIXEDFILEINFO w MaszynyAlert.App.exe.' }
    $least = [BitConverter]::GetBytes([uint32](([uint32]$parsed.Build -shl 16) -bor [uint32]($parsed.Revision + 1)))
    [Array]::Copy($least, 0, $bytes, $signature + 12, 4)
    $key = $text.IndexOf($latin.GetString([Text.Encoding]::Unicode.GetBytes('FileVersion')), [StringComparison]::Ordinal)
    if ($key -lt 0) { throw 'Brak klucza FileVersion w zasobie MaszynyAlert.App.exe.' }
    $value = $text.IndexOf($latin.GetString([Text.Encoding]::Unicode.GetBytes($current)), $key, [StringComparison]::Ordinal)
    if ($value -lt 0 -or $value - $key -gt 64) { throw 'Brak tekstu FileVersion w zasobie MaszynyAlert.App.exe.' }
    $replacement = [Text.Encoding]::Unicode.GetBytes($newer)
    [Array]::Copy($replacement, 0, $bytes, $value, $replacement.Length)
    [IO.File]::WriteAllBytes($target, $bytes)
    $patched = [Diagnostics.FileVersionInfo]::GetVersionInfo($target)
    if ($patched.FileVersion -ne $newer -or $patched.FilePrivatePart -ne ($parsed.Revision + 1)) {
        throw "Kopia programu ma wersje $($patched.FileVersion) / $($patched.FilePrivatePart), oczekiwano $newer."
    }
    $iscc = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
    $out = Join-Path $logs 'newer-installer'
    New-Item -ItemType Directory -Path $out -Force | Out-Null
    & $iscc "/DPublishDir=$copy" "/O$out" ([IO.Path]::GetFullPath('installer/MaszynyAlert.iss')) | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "ISCC (instalator nowszej wersji) zakonczyl sie kodem $LASTEXITCODE." }
    Remove-Item -LiteralPath $copy -Recurse -Force
    return [pscustomobject]@{ Path = (Join-Path $out 'VncAlert-Setup.exe'); Version = $newer }
}
function Test-UpdateTaskInstall {
    New-StandardUser
    $configPath = Join-Path $data 'config.json'
    $config = [IO.File]::ReadAllText($configPath)
    $installedBefore = Get-InstalledFileVersion
    $newer = New-NewerInstaller
    $fileName = 'VncAlert-Setup-' + $newer.Version + '.exe'
    $installedFileName = 'MaszynyAlert-Setup-' + $installedBefore + '.exe'
    $download = Join-Path $downloadsDir $fileName
    # Jak program przy pierwszym pobraniu: katalog pobran dziedziczy uprawnienia operatora z 'state'.
    New-Item -ItemType Directory -Path $downloadsDir -Force | Out-Null
    Copy-Item -LiteralPath $newer.Path -Destination $download -Force
    $size = (Get-Item -LiteralPath $download).Length
    $sha = (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant()
    $index = "# Wydania`n`n| Plik | Rozmiar | SHA-256 |`n|---|---|---|`n| [$installedFileName]($installedFileName) | ($size B) | ``$sha`` |`n| [$fileName]($fileName) | ($size B) | ``$sha`` |`n"
    $server = $null
    try {
        $server = Start-CatalogServer $index
        $patched = $config -replace 'https://wydania\.mbuas\.pl/maszyny-alert/', $server.Url
        if ($patched -eq $config) { throw 'Nie znaleziono adresu katalogu wydan w config.json testu.' }
        [IO.File]::WriteAllText($configPath, $patched)

        Assert-Refused (Invoke-UpdateTask '..\..\Windows\System32\cmd.exe') 'konwencji' 'nazwa ze sciezka'
        Assert-Refused (Invoke-UpdateTask $installedFileName) 'nowsza' 'wersja nie nowsza (stara nazwa pliku)'
        $original = [IO.File]::ReadAllBytes($download)
        $forged = [byte[]]$original.Clone()
        $forged[$forged.Length - 1] = $forged[$forged.Length - 1] -bxor 0xFF
        [IO.File]::WriteAllBytes($download, $forged)
        $result = Invoke-UpdateTask $fileName
        Assert-Refused $result 'SHA-256' 'plik podmieniony'
        $forgedSha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($forged))
        if ($result.message -match $forgedSha) { throw 'Odmowa zdradza sume pliku odczytanego przez SYSTEM.' }
        if (@(Get-ChildItem -LiteralPath $updatesAdmin -Recurse -Filter '*.exe').Count -ne 0) { throw 'Kopia podmienionego pliku zostala w updates-admin.' }
        'PASS: zadanie odrzuca nazwe ze sciezka, wersje nie nowsza i plik inny niz w spisie - z powodem, bez sumy pliku, instalator nie startuje.' |
            Add-Content (Join-Path $logs 'checks.txt')

        $operatorStamp = (Get-Date).ToUniversalTime().AddMinutes(-1).ToString('o')
        [IO.File]::WriteAllText($stopMarkerPath, $operatorStamp)
        $operatorWritten = (Get-Item -LiteralPath $stopMarkerPath).LastWriteTimeUtc
        [IO.File]::WriteAllBytes($download, $original)
        $result = Invoke-UpdateTask $fileName
        if ([int]$result.exitCode -ne 0 -or [int]$result.setupExitCode -ne 0) {
            throw "Instalacja przez zadanie nie powiodla sie: $($result.exitCode)/$($result.setupExitCode) $($result.message)"
        }
        if ($result.account -ne 'S-1-5-18') { throw "Zadanie dzialalo jako $($result.account), nie SYSTEM." }
        $installedAfter = Get-InstalledFileVersion
        if ($installedAfter -ne $newer.Version) { throw "Po instalacji przez zadanie zainstalowana jest $installedAfter, oczekiwano $($newer.Version)." }
        $setupLog = Join-Path $updatesAdmin ('install-' + $newer.Version + '.log')
        if (!(Test-Path -LiteralPath $setupLog) -or (Get-Content -LiteralPath $setupLog -Raw) -notmatch 'Installation process succeeded') {
            throw 'Brak logu udanej instalacji w updates-admin.'
        }
        if (Test-Path -LiteralPath (Join-Path $updatesAdmin 'pending-install.json')) { throw 'Po udanej instalacji zostal znacznik proby.' }
        if (@(Get-ChildItem -LiteralPath $updatesAdmin -Directory -Filter 'run-*').Count -ne 0) { throw 'Po instalacji zostal katalog przebiegu z kopia.' }
        if (!(Test-Path -LiteralPath $stopMarkerPath) -or [IO.File]::ReadAllText($stopMarkerPath) -ne $operatorStamp -or
            (Get-Item -LiteralPath $stopMarkerPath).LastWriteTimeUtc -ne $operatorWritten) {
            throw 'Instalator albo zadanie jako SYSTEM zmienily znacznik zatrzymania operatora w state.'
        }
        if (Test-Path -LiteralPath $systemMarkerPath) { throw "Po instalacji przez zadanie zostal znacznik zatrzymania SYSTEM: $systemMarkerPath" }
        Remove-Item -LiteralPath $stopMarkerPath -Force
        Assert-UpdateTask $true
        Remove-Item -LiteralPath (Join-Path $downloadsDir 'update-request.json') -Force -ErrorAction SilentlyContinue
        $before = (Get-Date).ToUniversalTime().AddSeconds(-1)
        $previousResult = (Get-Item -LiteralPath $taskResultPath).LastWriteTimeUtc
        if ($previousResult -ge $before) { $before = $previousResult }
        if ((Invoke-SchtasksAs $script:standardCredential "/run /tn `"$updateTask`"") -ne 0) { throw 'schtasks /run po instalacji przez zadanie nie powiodlo sie.' }
        $next = Wait-TaskResult $null $before 120
        if ([int]$next.exitCode -ne 6 -or $next.message -notmatch 'Brak') {
            throw "Zadanie po instalacji nowej wersji mialo czekac na nastepne zadanie (6, Brak), a zwrocilo $($next.exitCode): $($next.message)"
        }
        if (!(Test-Path -LiteralPath $exe -PathType Leaf) -or (Test-Path -LiteralPath $currentApp)) {
            throw "Aktualizacja przez zadanie opuscila katalog instalacji $app (UsePreviousAppDir) albo zalozyla $currentApp."
        }
        "PASS: instalacja $installedBefore -> $($newer.Version) ($fileName ze spisu mieszanego, w katalogu $app) przez zadanie jako SYSTEM bez UAC (wyzwolona z konta standardowego); zadanie przerejestrowane na $($newer.Version) i przyjmuje nastepny przebieg; znacznik operatora w state nietkniety, znacznik SYSTEM w updates-admin zdjety; log w updates-admin, znacznik proby i kopia usuniete." |
            Add-Content (Join-Path $logs 'checks.txt')
    }
    finally {
        Stop-CatalogServer $server
        [IO.File]::WriteAllText($configPath, $config)
        Remove-Item -LiteralPath $download, (Join-Path $downloadsDir 'update-request.json') -Force -ErrorAction SilentlyContinue
    }
}
function Invoke-UnprotectedCase([string]$Case, [scriptblock]$Break, [scriptblock]$Restore) {
    $taskLog = Join-Path $updatesAdmin 'install-task.log'
    $resultBefore = if (Test-Path -LiteralPath $taskResultPath) { (Get-Item -LiteralPath $taskResultPath).LastWriteTimeUtc } else { $null }
    $logBefore = if (Test-Path -LiteralPath $taskLog) { (Get-Item -LiteralPath $taskLog).LastWriteTimeUtc } else { $null }
    & $Break
    try {
        $before = (Get-Date).AddSeconds(-1)
        & (Join-Path $env:SystemRoot 'System32\schtasks.exe') /run /tn $updateTask | Out-Null
        $code = Wait-TaskFinished $before 120
        if ($code -ne 7) { throw "Zadanie ($Case) zakonczylo sie kodem $code zamiast 7 (odmowa)." }
        $resultAfter = if (Test-Path -LiteralPath $taskResultPath) { (Get-Item -LiteralPath $taskResultPath).LastWriteTimeUtc } else { $null }
        $logAfter = if (Test-Path -LiteralPath $taskLog) { (Get-Item -LiteralPath $taskLog).LastWriteTimeUtc } else { $null }
        if ($resultAfter -ne $resultBefore) { throw "Zadanie ($Case) pisalo wynik do niechronionego katalogu." }
        if ($logAfter -ne $logBefore) { throw "Zadanie ($Case) pisalo log do niechronionego katalogu." }
        $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'VNC Alert update'; Id = 7; StartTime = $before } -ErrorAction SilentlyContinue)
        if ($events.Count -eq 0) { throw "Brak wpisu 'VNC Alert update' (7) w Dzienniku zdarzen po odmowie ($Case)." }
    }
    finally { & $Restore }
    "PASS: zadanie odmawia pracy (kod 7, bez wyniku i logu, powod w Dzienniku zdarzen): $Case." | Add-Content (Join-Path $logs 'checks.txt')
}
function Test-UpdateTaskRefusesUnprotectedDirectory {
    $users = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')
    $rule = [Security.AccessControl.FileSystemAccessRule]::new($users, 'Modify', 'ContainerInherit, ObjectInherit', 'None', 'Allow')
    Invoke-UnprotectedCase 'Users z zapisem w updates-admin' {
        $acl = Get-Acl -LiteralPath $updatesAdmin; $acl.AddAccessRule($rule); Set-Acl -LiteralPath $updatesAdmin -AclObject $acl
    } {
        $acl = Get-Acl -LiteralPath $updatesAdmin; $null = $acl.RemoveAccessRuleSpecific($rule); Set-Acl -LiteralPath $updatesAdmin -AclObject $acl
    }
    Invoke-UnprotectedCase 'wlascicielem updates-admin jest konto standardowe' {
        & icacls.exe $updatesAdmin /setowner $script:standardUser | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'icacls /setowner nie powiodlo sie.' }
    } {
        & icacls.exe $updatesAdmin /setowner '*S-1-5-32-544' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'icacls /setowner (przywrocenie Administratorow) nie powiodlo sie.' }
    }
    $dataRule = [Security.AccessControl.FileSystemAccessRule]::new($users, 'Modify', 'None', 'None', 'Allow')
    Invoke-UnprotectedCase 'Users z zapisem w katalogu danych' {
        $acl = Get-Acl -LiteralPath $data; $acl.AddAccessRule($dataRule); Set-Acl -LiteralPath $data -AclObject $acl
    } {
        $acl = Get-Acl -LiteralPath $data; $null = $acl.RemoveAccessRuleSpecific($dataRule); Set-Acl -LiteralPath $data -AclObject $acl
    }
    $moved = Join-Path $data 'updates-admin.test-junction'
    Invoke-UnprotectedCase 'updates-admin jest przekierowaniem' {
        Move-Item -LiteralPath $updatesAdmin -Destination $moved
        New-Item -ItemType Junction -Path $updatesAdmin -Target $moved | Out-Null
    } {
        [IO.Directory]::Delete($updatesAdmin)
        Move-Item -LiteralPath $moved -Destination $updatesAdmin
    }
    Assert-UpdatesAdminDirectory
}
function Invoke-RegisterUpdateTask([string]$Version) {
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $app 'installer\Register-UpdateTask.ps1') `
        -ApplicationDirectory $app -DataDirectory $data -AppVersion $Version | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Register-UpdateTask.ps1 zakonczyl sie kodem $LASTEXITCODE." }
}
function Test-UpdateTaskVersionBinding {
    Invoke-RegisterUpdateTask '1.2.3.4'
    try {
        $before = (Get-Date).ToUniversalTime().AddSeconds(-1)
        $run = Invoke-SchtasksAs $script:standardCredential "/run /tn `"$updateTask`""
        if ($run -ne 0) { throw "schtasks /run zakonczylo sie kodem $run." }
        $result = Wait-TaskResult $null $before 120
        if ([int]$result.exitCode -ne 8 -or $result.message -notmatch 'wersj') {
            throw "Zadanie zwiazane z inna wersja mialo odmowic (8, wersja), a zwrocilo $($result.exitCode): $($result.message)"
        }
    }
    finally { Invoke-RegisterUpdateTask (Get-InstalledFileVersion) }
    'PASS: zadanie zwiazane z inna wersja programu odmawia kodem 8 bez uruchamiania programu (wycofanie bez recznego sprzatania).' |
        Add-Content (Join-Path $logs 'checks.txt')
}
function Test-ProgramRefusesSystem {
    $name = 'VNC Alert test SYSTEM'
    $xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <Principals><Principal id="Author"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings><ExecutionTimeLimit>PT5M</ExecutionTimeLimit><AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled></Settings>
  <Actions Context="Author"><Exec><Command>$exe</Command><WorkingDirectory>$app</WorkingDirectory></Exec></Actions>
</Task>
"@
    $file = Join-Path $logs 'system-guard-task.xml'
    [IO.File]::WriteAllText($file, $xml, [Text.Encoding]::Unicode)
    & (Join-Path $env:SystemRoot 'System32\schtasks.exe') /create /tn $name /xml $file /f | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Nie utworzono zadania testowego SYSTEM.' }
    try {
        $service = New-Object -ComObject Schedule.Service
        $service.Connect()
        $before = (Get-Date).AddSeconds(-1)
        & (Join-Path $env:SystemRoot 'System32\schtasks.exe') /run /tn $name | Out-Null
        $waited = [Diagnostics.Stopwatch]::StartNew()
        do {
            Start-Sleep -Milliseconds 500
            $task = $service.GetFolder('\').GetTask($name)
        } while (($task.State -eq 4 -or $task.LastRunTime -le $before) -and $waited.Elapsed.TotalSeconds -lt 60)
        if ($task.LastTaskResult -ne 3) { throw "Program uruchomiony jako SYSTEM zakonczyl sie kodem $($task.LastTaskResult) zamiast 3." }
    }
    finally { & (Join-Path $env:SystemRoot 'System32\schtasks.exe') /delete /tn $name /f | Out-Null }
    'PASS: program uruchomiony jako SYSTEM (zadanie testowe) konczy sie od razu kodem 3.' | Add-Content (Join-Path $logs 'checks.txt')
}
function Test-DataAclByHandle {
    $work = Join-Path $logs 'acl-by-handle'
    $target = Join-Path $work 'cel'
    $link = Join-Path $work 'przekierowanie'
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    New-Item -ItemType Junction -Path $link -Target $target | Out-Null
    $before = (Get-Acl -LiteralPath $target).Sddl
    $probeScript = Join-Path $work 'probe.ps1'
    @'
param([string]$Script, [string]$Link)
. $Script -DataDirectory 'x' -ApplicationDirectory 'y' -DefinitionsOnly
function Assert-NoReparsePoint([string]$Path) { }
try { Set-DataAcl $Link $true $true; 'BRAK ODMOWY' } catch { 'ODMOWA: ' + $_.Exception.Message }
'@ | Set-Content -LiteralPath $probeScript -Encoding ascii
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $output = & $powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $probeScript `
        -Script (Join-Path $app 'installer\Initialize-Data.ps1') -Link $link 2>&1 | Out-String
    try {
        if ($output -notmatch 'ODMOWA: Przekierowanie wykryte na uchwycie') { throw "Set-DataAcl nie odmowil na przekierowaniu: $output" }
        if ((Get-Acl -LiteralPath $target).Sddl -ne $before) { throw 'Set-DataAcl zmienil uprawnienia celu przekierowania.' }
    }
    finally { [IO.Directory]::Delete($link); Remove-Item -LiteralPath $work -Recurse -Force }
    'PASS: Initialize-Data nadaje uprawnienia przez uchwyt - na przekierowaniu (po wygranym wyscigu) odmawia, cel bez zmian.' |
        Add-Content (Join-Path $logs 'checks.txt')
}
function Test-SystemMarkerRemovalRetry {
    $work = Join-Path $logs 'system-marker'
    $admin = Join-Path $work 'updates-admin'
    New-Item -ItemType Directory -Path $admin -Force | Out-Null
    $marker = Join-Path $admin 'stop.requested'
    $taskLog = Join-Path $admin 'install-task.log'
    $go = Join-Path $work 'start'
    $probeScript = Join-Path $work 'probe.ps1'
    @'
param([string]$Script, [string]$Data, [string]$Go)
. $Script -ApplicationDirectory 'x' -DataDirectory $Data -ExpectedVersion '0' -DefinitionsOnly
while (-not [IO.File]::Exists($Go)) { Start-Sleep -Milliseconds 50 }
'WYNIK: ' + (Remove-SystemMarker)
'@ | Set-Content -LiteralPath $probeScript -Encoding ascii
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $updateTask = Join-Path $app 'installer\Update-Task.ps1'
    function Invoke-MarkerProbe([int]$ReleaseAfterMs) {
        # $ReleaseAfterMs < 0: plik otwarty do konca proby. Proba czeka na plik 'start', wiec pierwsze usuniecie trafia
        # na otwarty znacznik niezaleznie od czasu startu powershell.exe.
        Remove-Item -LiteralPath $go, $taskLog -Force -ErrorAction SilentlyContinue
        [IO.File]::WriteAllText($marker, [DateTimeOffset]::Now.ToString('o'))
        $holder = [IO.File]::Open($marker, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            $start = New-Object Diagnostics.ProcessStartInfo $powershell
            $start.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $probeScript + '" -Script "' + $updateTask +
                '" -Data "' + $work + '" -Go "' + $go + '"'
            $start.UseShellExecute = $false
            $start.CreateNoWindow = $true
            $start.RedirectStandardOutput = $true
            $start.RedirectStandardError = $true
            $probe = [Diagnostics.Process]::Start($start)
            $stdout = $probe.StandardOutput.ReadToEndAsync()
            $stderr = $probe.StandardError.ReadToEndAsync()
            $clock = [Diagnostics.Stopwatch]::StartNew()
            [IO.File]::WriteAllText($go, 'start')
            if ($ReleaseAfterMs -ge 0) { Start-Sleep -Milliseconds $ReleaseAfterMs; $holder.Dispose() }
            if (!$probe.WaitForExit(60000)) { $probe.Kill(); throw 'Proba zdjecia znacznika nie skonczyla sie w 60 s.' }
            $clock.Stop()
        }
        finally { $holder.Dispose() }
        $log = if (Test-Path -LiteralPath $taskLog) { Get-Content -LiteralPath $taskLog -Raw } else { '' }
        return [pscustomobject]@{ Output = ($stdout.Result + $stderr.Result); Log = $log; Seconds = $clock.Elapsed.TotalSeconds }
    }
    try {
        $released = Invoke-MarkerProbe 1500
        if ($released.Output -notmatch 'WYNIK: True' -or (Test-Path -LiteralPath $marker)) {
            throw "Remove-SystemMarker nie zdjal znacznika zwolnionego po 1,5 s: $($released.Output) $($released.Log)"
        }
        $held = Invoke-MarkerProbe -1
        if ($held.Output -notmatch 'WYNIK: False' -or -not (Test-Path -LiteralPath $marker)) {
            throw "Remove-SystemMarker na stale otwartym znaczniku: oczekiwano False: $($held.Output)"
        }
        if ($held.Log -notmatch 'nie zdjeto znacznika') { throw "Brak wpisu o niezdjetym znaczniku w install-task.log: $($held.Log)" }
        if ($held.Seconds -lt 3) { throw "Remove-SystemMarker poddal sie po $($held.Seconds) s - bez ponowienia." }
    }
    finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
    'PASS: zadanie zdejmuje znacznik ciszy instalatora z ponowieniem; przy trwalej blokadzie wpis w install-task.log.' |
        Add-Content (Join-Path $logs 'checks.txt')
}
function Test-StopSignalFromOtherOwner {
    $session = [Diagnostics.Process]::GetCurrentProcess().SessionId
    if ($session -eq 0) {
        'SKIP: obcy sygnal Global\ nie zatrzymuje programu - runner w sesji 0, gdzie Local\ = Global\; do sprawdzenia na stanowisku.' |
            Add-Content (Join-Path $logs 'checks.txt')
        return
    }
    $squatter = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::AutoReset, 'Global\MaszynyAlert.StopRequest')
    $running = Start-Process -FilePath $exe -WorkingDirectory $app -PassThru
    try {
        if (!(Wait-AppRunning $true 60)) { throw 'Aplikacja nie wystartowala (test obcego sygnalu).' }
        Start-Sleep -Seconds 3
        $null = $squatter.Set()
        Start-Sleep -Seconds 3
        if (!(Test-AppRunning)) { throw 'Obcy obiekt Global\MaszynyAlert.StopRequest zatrzymal program.' }
        $stopClock = [Diagnostics.Stopwatch]::StartNew()
        $stopCommand = Start-Process -FilePath $exe -ArgumentList '--stop' -WorkingDirectory $app -Wait -PassThru
        $commandSeconds = $stopClock.Elapsed.TotalSeconds
        $stopped = Wait-AppRunning $false 15
        "Obcy sygnal: --stop exit=$($stopCommand.ExitCode) po $([Math]::Round($commandSeconds, 1)) s; program zakonczony: $stopped po $([Math]::Round($stopClock.Elapsed.TotalSeconds, 1)) s." |
            Add-Content (Join-Path $logs 'checks.txt')
        if (!$stopped) {
            Get-ChildItem -LiteralPath (Join-Path $data 'logs') -Filter 'app-*.log' -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime | Select-Object -Last 1 | ForEach-Object {
                    Write-Output "===== log programu: $($_.FullName)"
                    Get-Content -LiteralPath $_.FullName -Tail 150 | Write-Output
                }
            throw 'Komenda --stop nie zatrzymala programu po tescie obcego sygnalu.'
        }
        "PASS: obcy obiekt Global\MaszynyAlert.StopRequest nie zatrzymuje programu, --stop dziala (sesja $session)." |
            Add-Content (Join-Path $logs 'checks.txt')
    }
    finally {
        $running.Refresh()
        if (!$running.HasExited) { $running.Kill(); $running.WaitForExit() }
        $running.Dispose()
        $squatter.Dispose()
        Remove-Item -LiteralPath $stopMarkerPath -Force -ErrorAction SilentlyContinue
    }
}
function Wait-SetupFinished([string]$LogName, [int]$Seconds = 60) {
    # Uninstaller kopiuje się do TEMP i uruchamia kopię, więc PIERWSZY proces kończy się od razu,
    # a reszta pracy (w tym usPostUninstall, gdzie sprzątamy wpis w PATH) trwa dalej. Samo
    # WaitForExit nie wystarcza — czekamy, aż Inno zamknie swój dziennik.
    $path = Join-Path $logs $LogName
    $waited = [Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        if ((Test-Path -LiteralPath $path) -and
            @(Get-Content -LiteralPath $path -Tail 3 -ErrorAction SilentlyContinue) -match 'Log closed') { return }
        if ($waited.Elapsed.TotalSeconds -ge $Seconds) { throw "Nie zakończono w $Seconds s: $LogName" }
        Start-Sleep -Milliseconds 200
    }
}
function Test-AppRunning {
    $mutex = $null
    try {
        if (![Threading.Mutex]::TryOpenExisting('Global\MaszynyAlert.App', [ref]$mutex)) { return $false }
        $mutex.Dispose()
        return $true
    }
    catch [UnauthorizedAccessException] { return $true }
}
function Wait-AppRunning([bool]$Expected, [int]$Seconds) {
    $waited = [Diagnostics.Stopwatch]::StartNew()
    while ((Test-AppRunning) -ne $Expected) {
        if ($waited.Elapsed.TotalSeconds -ge $Seconds) { return $false }
        Start-Sleep -Milliseconds 200
    }
    return $true
}
$stopMarker = Join-Path $data 'state\stop.requested'
function Assert-NoStopMarker([string]$When) {
    if (Test-Path -LiteralPath $stopMarker) { throw "Po $When został znacznik zatrzymania (watchdog milczy 10 min): $stopMarker" }
    "PASS: brak znacznika zatrzymania po $When." | Add-Content (Join-Path $logs 'checks.txt')
}
function Assert-SystemSetupLeftOperatorData([string]$LogText, [string]$When) {
    if ($LogText -notmatch 'instalator dziala jako SYSTEM: 1') { throw "Instalator nie rozpoznał konta SYSTEM ($When)." }
    if ($LogText -match [Regex]::Escape('state\stop.requested')) { throw "Instalator jako SYSTEM usunął znacznik w state ($When)." }
    $systemMarker = Join-Path $data 'updates-admin\stop.requested'
    if (Test-Path -LiteralPath $systemMarker) { throw "Po $When został znacznik SYSTEM: $systemMarker" }
    if (Test-Path -LiteralPath $stopMarker) {
        throw "Po $When jest znacznik w state - '--stop' jako SYSTEM zapisał go w katalogu operatora zamiast w updates-admin: $stopMarker"
    }
    "PASS: instalator jako SYSTEM nie kasuje w state, znacznik SYSTEM zdjęty ($When)." | Add-Content (Join-Path $logs 'checks.txt')
}
function Assert-CrashCheckLogged([string]$LogName) {
    if ((Get-Content -LiteralPath (Join-Path $logs $LogName) -Raw) -notmatch 'VNC Alert: kontrola procesu po awarii') {
        throw "Instalator nie sprawdził procesu pozostałego po awarii: $LogName"
    }
    "PASS: kontrola procesu po awarii w $LogName." | Add-Content (Join-Path $logs 'checks.txt')
}
function Assert-PreviewShortcuts {
    $shell = New-Object -ComObject WScript.Shell
    foreach ($path in @($desktopShortcut, $startShortcut)) {
        if (!(Test-Path -LiteralPath $path)) { throw "Brak skrótu: $path" }
        $shortcut = $shell.CreateShortcut($path)
        if ($shortcut.TargetPath -ine $exe -or $shortcut.Arguments -cne '--preview' -or
            $shortcut.WorkingDirectory -ine $app -or $shortcut.IconLocation -ine "$exe,0") {
            throw "Skrót nie otwiera Podglądu lub ma inną ikonę: $path"
        }
    }
    'PASS: skróty pulpitu i menu Start otwierają Podgląd z ikoną aplikacji.' |
        Add-Content (Join-Path $logs 'checks.txt')
}
Run-Setup $baseline 'install.log'
$exe = Join-Path $app 'MaszynyAlert.App.exe'
if (!(Test-Path -LiteralPath $exe)) { throw 'Brak aplikacji po instalacji.' }
Assert-PreviewShortcuts
Assert-PathEntry $true
Assert-WatchdogTask $true
Assert-WritableDataDirectories
Assert-UpdateTask $true
Assert-UpdatesAdminDirectory
$oldVersion =[Version][Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion
$expectedRun = '"' + $exe + '"'
if ((Get-ItemPropertyValue $runKey 'MaszynyAlert') -ne $expectedRun) { throw 'Nieprawidłowy autostart.' }
Add-Content -LiteralPath (Join-Path $data 'config.json') -Value ' '
Add-Content -LiteralPath (Join-Path $data 'rules.json') -Value ' '
Run-Probe 'seed'
[IO.File]::WriteAllText((Join-Path $data 'history.csv'), 'test historii')
[IO.File]::WriteAllText((Join-Path $data 'downtime.jsonl'), '{"test":"retained downtime"}')
[IO.File]::WriteAllText((Join-Path $data 'logs/retained.log'), 'test logu')
[IO.File]::WriteAllText((Join-Path $data 'captures/retained.txt'), 'test zrzutu')
$hashes = Get-DataHashes
Remove-ItemProperty -LiteralPath $runKey -Name 'MaszynyAlert'
# Własny dźwięk alarmu podmieniony na stanowisku nie może zostać nadpisany przez aktualizację.
$customSound = Join-Path $app 'sounds/alarm.wav'
if (!(Test-Path -LiteralPath $customSound -PathType Leaf)) { throw 'Brak sounds/alarm.wav po instalacji.' }
[IO.File]::WriteAllBytes($customSound, [Text.Encoding]::ASCII.GetBytes('wlasny dzwiek alarmu'))
$customSoundHash = (Get-FileHash -LiteralPath $customSound).Hash

# Prawdziwa awaria inicjalizacji: niedozwolone przekierowanie konfiguracji.
# Operacje dotyczą wyłącznie dwóch nazw w katalogu danych testowej instalacji.
$configPath = [IO.Path]::GetFullPath((Join-Path $data 'config.json'))
$backupPath = [IO.Path]::GetFullPath((Join-Path $data 'config.failure-test.json'))
foreach ($path in @($configPath, $backupPath)) {
    if ([IO.Path]::GetDirectoryName($path) -ne [IO.Path]::GetFullPath($data)) { throw 'Ścieżka poza danymi testu.' }
}
Move-Item -LiteralPath $configPath -Destination $backupPath
try {
    New-Item -ItemType SymbolicLink -Path $configPath -Target $backupPath | Out-Null
    Run-Setup $setup 'initialization-failure.log' $true
}
finally {
    if (Test-Path -LiteralPath $configPath) { Remove-Item -LiteralPath $configPath -Force }
    Move-Item -LiteralPath $backupPath -Destination $configPath
}
$failureLog = Get-Content -LiteralPath (Join-Path $logs 'initialization-failure.log') -Raw
if ($failureLog -notmatch 'Initialize-Data.ps1' -or $failureLog -notmatch 'Niedozwolone przekierowanie') {
    throw 'Oczekiwana awaria nie pochodzi z kontroli przekierowania danych.'
}
Assert-UpdateTask $false

if (Get-Process -Name MaszynyAlert.App -ErrorAction SilentlyContinue) { throw 'Instalator uruchomił monitoring w teście.' }
Assert-NoStopMarker 'nieudanej instalacji'
Assert-DataUnchanged $hashes
Run-Setup $setup 'upgrade.log'
Assert-NoStopMarker 'aktualizacji'
Assert-CrashCheckLogged 'upgrade.log'
if ((Get-Content -LiteralPath (Join-Path $logs 'upgrade.log') -Raw) -notmatch [Regex]::Escape("VNC Alert: katalog instalacji: $app")) {
    throw "Instalator nie zapisal w dzienniku katalogu instalacji $app (UsePreviousAppDir)."
}
if (Test-Path -LiteralPath $currentApp) { throw "Aktualizacja instalacji w $app zalozyla drugi katalog $currentApp." }
"PASS: aktualizacja instalacji sprzed zmiany nazwy zostaje w $app (UsePreviousAppDir), bez katalogu $currentApp." |
    Add-Content (Join-Path $logs 'checks.txt')
if (!(Test-Path -LiteralPath (Join-Path $data 'test-ekran\CZYTAJ-TO.txt') -PathType Leaf)) {
    throw 'Brak test-ekran\CZYTAJ-TO.txt w katalogu danych po instalacji.'
}
'PASS: test-ekran\CZYTAJ-TO.txt w katalogu danych.' | Add-Content (Join-Path $logs 'checks.txt')
Assert-PreviewShortcuts
# Aktualizacja nie może dopisać katalogu do PATH po raz drugi (Assert-PathEntry liczy wystąpienia).
Assert-PathEntry $true
Assert-WatchdogTask $true
Assert-WritableDataDirectories
Assert-UpdateTask $true
Assert-UpdatesAdminDirectory
if (!(Test-Path -LiteralPath (Join-Path $app 'vncalert.cmd') -PathType Leaf)) {
    throw 'Brak komendy vncalert.cmd w katalogu instalacji.'
}
$newVersion = [Version][Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion
if ($newVersion -le $oldVersion) { throw "Nie wykonano aktualizacji: $oldVersion -> $newVersion" }
if (Get-ItemProperty -LiteralPath $runKey -Name 'MaszynyAlert' -ErrorAction SilentlyContinue) {
    throw 'Aktualizacja ponownie włączyła wyłączony autostart.'
}
Assert-ReadOnlyAcl
Assert-DataUnchanged $hashes
if ((Get-FileHash -LiteralPath $customSound).Hash -ne $customSoundHash) { throw 'Aktualizacja nadpisała własny dźwięk alarmu.' }
if (!(Test-Path -LiteralPath (Join-Path $app 'sounds/idle-ping.wav') -PathType Leaf)) { throw 'Brak sounds/idle-ping.wav po aktualizacji.' }
'PASS: aktualizacja zachowała własny sounds/alarm.wav.' | Add-Content (Join-Path $logs 'checks.txt')
Run-Probe 'verify'

# Odmowa cofnięcia wersji nie może podmienić plików ani danych.
$exeHash = (Get-FileHash -LiteralPath $exe).Hash
Run-Setup $baseline 'downgrade-blocked.log' $true
if ((Get-FileHash -LiteralPath $exe).Hash -ne $exeHash) { throw 'Downgrade podmienił aplikację.' }
Assert-DataUnchanged $hashes
Run-Probe 'update'
Assert-ReadOnlyAcl
Run-Probe 'operator'
$hashes = Get-DataHashes

Run-Probe 'vnc-password'
$running = Start-Process -FilePath $exe -WorkingDirectory $app -PassThru
try {
    if (!(Wait-AppRunning $true 60)) { throw 'Aplikacja nie wystartowała w teście instalatora.' }
    Run-Setup $setup 'upgrade-running.log'
    if (!(Wait-AppRunning $false 15)) { throw 'Instalator nie zatrzymał działającej aplikacji.' }
    $running.Refresh()
    if (!$running.HasExited) { throw 'Proces aplikacji przetrwał instalację.' }
    "PASS: instalacja zatrzymała działającą aplikację (exit=$($running.ExitCode))." |
        Add-Content (Join-Path $logs 'checks.txt')
}
finally {
    $running.Refresh()
    if (!$running.HasExited) { $running.Kill(); $running.WaitForExit() }
    $running.Dispose()
}
Assert-PreviewShortcuts
Assert-PathEntry $true
Assert-WatchdogTask $true
Assert-ReadOnlyAcl
Run-Probe 'operator'

$running = Start-Process -FilePath $exe -WorkingDirectory $app -PassThru
try {
    if (!(Wait-AppRunning $true 60)) { throw 'Aplikacja nie wystartowała przed testem odrzuconej instalacji.' }
    Run-Setup $baseline 'downgrade-running.log' $true
    if (!(Wait-AppRunning $true 30)) { throw 'Po odrzuconej instalacji program nie wrócił (10 min bez nadzoru).' }
    'PASS: po odrzuconej instalacji program wrócił od razu.' | Add-Content (Join-Path $logs 'checks.txt')
}
finally {
    $running.Dispose()
    Get-Process -Name MaszynyAlert.App -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    $null = Wait-AppRunning $false 15
}
Assert-NoStopMarker 'odrzuconej instalacji przy działającym programie'

$systemTask = 'VNC Alert test instalatora jako SYSTEM'
$systemLog = Join-Path $logs 'downgrade-system.log'
$running = Start-Process -FilePath $exe -WorkingDirectory $app -PassThru
try {
    if (!(Wait-AppRunning $true 60)) { throw 'Aplikacja nie wystartowała przed testem instalacji jako SYSTEM.' }
    $command = '"' + $baseline + '" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="' + $systemLog + '"'
    & "$env:SystemRoot\System32\schtasks.exe" /create /tn $systemTask /tr $command /sc once /st 23:59 /ru SYSTEM /rl HIGHEST /f | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Nie utworzono zadania testowego SYSTEM.' }
    & "$env:SystemRoot\System32\schtasks.exe" /run /tn $systemTask | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Nie uruchomiono zadania testowego SYSTEM.' }
    Wait-SetupFinished 'downgrade-system.log' 180
    Start-Sleep -Seconds 5
    $systemLogText = Get-Content -LiteralPath $systemLog -Raw
    if ($systemLogText -notmatch 'Instalacja starszej wersji|nowsza wersja') { throw 'Instalacja jako SYSTEM nie trafiła na odmowę starszej wersji.' }
    if ($systemLogText -match 'program uruchomiony ponownie') { throw 'Instalator jako SYSTEM zapisał w dzienniku, że uruchomił program.' }
    if ($systemLogText -notmatch 'dziala jako SYSTEM - programu nie uruchamia') { throw 'Dziennik instalatora jako SYSTEM nie mówi, kto przywróci program.' }
    Assert-SystemSetupLeftOperatorData $systemLogText 'odrzuconej instalacji jako SYSTEM'
    $systemOwned = @(Get-CimInstance Win32_Process -Filter "Name = 'MaszynyAlert.App.exe'" | Where-Object {
        (Invoke-CimMethod -InputObject $_ -MethodName GetOwner).User -eq 'SYSTEM' })
    if ($systemOwned) { throw "Instalator jako SYSTEM uruchomił program na koncie SYSTEM (PID $($systemOwned.ProcessId -join ', '))." }
    'PASS: instalator jako SYSTEM nie uruchamia programu i mówi prawdę w dzienniku.' | Add-Content (Join-Path $logs 'checks.txt')
}
finally {
    & "$env:SystemRoot\System32\schtasks.exe" /delete /tn $systemTask /f 2>&1 | Out-Null
    $running.Dispose()
    Get-Process -Name MaszynyAlert.App -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    $null = Wait-AppRunning $false 15
}
Assert-NoStopMarker 'odrzuconej instalacji jako SYSTEM'

$readme = Join-Path $data 'test-ekran\CZYTAJ-TO.txt'
$readmeWritten = (Get-Item -LiteralPath $readme).LastWriteTimeUtc
$systemLog = Join-Path $logs 'reinstall-system.log'
try {
    $command = '"' + $setup + '" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="' + $systemLog + '"'
    & "$env:SystemRoot\System32\schtasks.exe" /create /tn $systemTask /tr $command /sc once /st 23:59 /ru SYSTEM /rl HIGHEST /f | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Nie utworzono zadania testowego SYSTEM (ponowna instalacja).' }
    & "$env:SystemRoot\System32\schtasks.exe" /run /tn $systemTask | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Nie uruchomiono zadania testowego SYSTEM (ponowna instalacja).' }
    Wait-SetupFinished 'reinstall-system.log' 180
    $systemLogText = Get-Content -LiteralPath $systemLog -Raw
    if ($systemLogText -notmatch 'Installation process succeeded') { throw 'Ponowna instalacja jako SYSTEM nie powiodła się.' }
    if ($systemLogText -notmatch 'CZYTAJ-TO\.txt pominiety - instalator jako SYSTEM') { throw 'Instalator jako SYSTEM nie pominął CZYTAJ-TO.txt.' }
    if ((Get-Item -LiteralPath $readme).LastWriteTimeUtc -ne $readmeWritten) { throw 'Instalator jako SYSTEM zapisał test-ekran\CZYTAJ-TO.txt.' }
    Assert-SystemSetupLeftOperatorData $systemLogText 'ponownej instalacji jako SYSTEM'
    'PASS: instalator jako SYSTEM nie pisze CZYTAJ-TO.txt w test-ekran.' | Add-Content (Join-Path $logs 'checks.txt')
}
finally { & "$env:SystemRoot\System32\schtasks.exe" /delete /tn $systemTask /f 2>&1 | Out-Null }
# Instalacja rejestruje watchdog na nowo (włączony) — wyłączamy go jak po każdej instalacji w tym teście.
Assert-WatchdogTask $true
Assert-NoStopMarker 'ponownej instalacji jako SYSTEM'
$hashes = Get-DataHashes

Set-ItemProperty -LiteralPath $runKey -Name 'MaszynyAlert' -Value $expectedRun
Run-Setup $setup 'reinstall.log'
Assert-PreviewShortcuts
Assert-PathEntry $true
Assert-WatchdogTask $true
if ((Get-ItemPropertyValue $runKey 'MaszynyAlert') -ne $expectedRun) { throw 'Zmieniono włączony autostart.' }
Assert-DataUnchanged $hashes
Assert-ReadOnlyAcl
Run-Probe 'operator'
$hashes = Get-DataHashes
Test-DataAclByHandle
Test-SystemMarkerRemovalRetry
Test-StopSignalFromOtherOwner
try {
Test-UpdateTaskAsStandardUser
Test-UpdateTaskInstall
# Setup uruchomiony przez zadanie rejestruje watchdog na nowo (wlaczony) - wylaczamy go jak po kazdej instalacji w tym tescie.
Assert-WatchdogTask $true
Test-UpdateTaskRefusesUnprotectedDirectory
Test-UpdateTaskVersionBinding
Test-ProgramRefusesSystem
}
finally { Remove-StandardUser }
Assert-UpdateTask $true
Assert-WatchdogTask $true
Assert-UpdatesAdminDirectory
Assert-ReadOnlyAcl
Run-Probe 'operator'
$hashes = Get-DataHashes
Run-Setup (Join-Path $app 'unins000.exe') 'uninstall.log'
Wait-SetupFinished 'uninstall.log'
if (Test-Path -LiteralPath $exe) { throw 'Aplikacja pozostała po odinstalowaniu.' }
if ((Test-Path -LiteralPath $desktopShortcut) -or (Test-Path -LiteralPath $startShortcut)) {
    throw 'Skrót pozostał po odinstalowaniu.'
}
if (Get-ItemProperty -LiteralPath $runKey -Name 'MaszynyAlert' -ErrorAction SilentlyContinue) { throw 'Pozostał autostart.' }
Assert-PathEntry $false
Assert-WatchdogTask $false
Assert-UpdateTask $false
Assert-DataUnchanged $hashes
Run-Probe 'operator'
$hashes = Get-DataHashes

$operatorMarkerStamp = (Get-Date).ToUniversalTime().ToString('o')
[IO.File]::WriteAllText($stopMarkerPath, $operatorMarkerStamp)
Run-Setup $setup 'fresh-install.log'
$app = $currentApp
$exe = Join-Path $app 'MaszynyAlert.App.exe'
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Nowa instalacja nie trafila do $app." }
if (Test-Path -LiteralPath (Join-Path $previousApp 'MaszynyAlert.App.exe')) { throw "Nowa instalacja zalozyla tez program w starym katalogu $previousApp." }
if ((Get-ItemPropertyValue $runKey 'MaszynyAlert') -ne ('"' + $exe + '"')) { throw "Autostart nowej instalacji nie wskazuje $exe." }
Assert-PreviewShortcuts
Assert-PathEntry $true
Assert-WatchdogTask $true
Assert-UpdateTask $true
Assert-UpdatesAdminDirectory
if (!(Test-Path -LiteralPath $stopMarkerPath) -or [IO.File]::ReadAllText($stopMarkerPath) -ne $operatorMarkerStamp) {
    throw 'Nowa instalacja usunela albo zmienila znacznik zatrzymania operatora (znacznik sprzed instalacji zostaje, gdy program nie dzialal).'
}
if (Get-Process -Name MaszynyAlert.App -ErrorAction SilentlyContinue) { throw 'Watchdog uruchomil program po nowej instalacji mimo znacznika operatora.' }
Assert-DataUnchanged $hashes
Assert-ReadOnlyAcl
Run-Probe 'operator'
$hashes = Get-DataHashes
Remove-Item -LiteralPath $stopMarkerPath -Force
"PASS: nowa instalacja w $app (dane zachowane w $data, znacznik operatora nietkniety, program nie wystartowal); watchdog, zadanie aktualizacji, PATH, skroty i autostart wskazuja nowy katalog." |
    Add-Content (Join-Path $logs 'checks.txt')
Run-Setup (Join-Path $app 'unins000.exe') 'uninstall-fresh.log'
Wait-SetupFinished 'uninstall-fresh.log'
if (Test-Path -LiteralPath $exe) { throw 'Aplikacja pozostała po odinstalowaniu nowej instalacji.' }
Assert-PathEntry $false
Assert-WatchdogTask $false
Assert-UpdateTask $false
Assert-DataUnchanged $hashes
"PASS: $oldVersion -> $newVersion; instalacja, rzeczywisty DPAPI, ACL operatora, awaria i ponowienie, downgrade, autostart, PATH z komendą vncalert, watchdog w Harmonogramie zadań, katalogi zapisywalne dla operatora, aktualizacja przy działającej aplikacji, odinstalowanie; aktualizacja w miejscu w $previousApp i nowa instalacja w $currentApp." |
    Set-Content -LiteralPath (Join-Path $logs 'result.txt')
