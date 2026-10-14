<#
.DESCRIPTION
    Wylacznie 127.0.0.1 i katalogi testu. Bez Harmonogramu, bez ProgramData/Program Files, bez zatrzymywania VNC Alert.
    Prawdziwy probe.exe (kopia wyniku budowania) uruchamiany jest tylko z --password (magazyn DPAPI w %LOCALAPPDATA%
    nie jest otwierany), --vnc-alert-config na nieistniejacy plik w katalogu testu (ProgramData nie jest czytane) i --no-ocr.
    Dziala w Windows PowerShell 5.1 i w pwsh 7.
#>

function Get-AtrapaWolnyPort {
    # Port zamkniety na 127.0.0.1: nasluch zwolniony od razu po odczycie numeru (polaczenie = odmowa TCP).
    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    return $port
}

function Start-AtrapaVncOdmowa {
    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $state = [hashtable]::Synchronized(@{ Stop = $false; Polaczenia = 0; Odmowy = 0 })
    $ps = [PowerShell]::Create()
    [void]$ps.AddScript({
        param($Listener, $State)
        function Read-Exact($Stream, [int]$Count) {
            $buffer = New-Object byte[] $Count
            $read = 0
            while ($read -lt $Count) {
                $n = $Stream.Read($buffer, $read, $Count - $read)
                if ($n -le 0) { throw 'eof' }
                $read += $n
            }
            return $buffer
        }
        while (-not $State.Stop) {
            if (-not $Listener.Pending()) { Start-Sleep -Milliseconds 20; continue }
            $client = $Listener.AcceptTcpClient()
            $State.Polaczenia++
            try {
                $client.ReceiveTimeout = 5000; $client.SendTimeout = 5000
                $stream = $client.GetStream()
                $header = [Text.Encoding]::ASCII.GetBytes("RFB 003.008`n")
                $stream.Write($header, 0, $header.Length)
                [void](Read-Exact $stream 12)
                $stream.Write([byte[]](1, 2), 0, 2)
                [void](Read-Exact $stream 1)
                $challenge = New-Object byte[] 16
                $stream.Write($challenge, 0, 16)
                [void](Read-Exact $stream 16)
                $reason = [Text.Encoding]::ASCII.GetBytes('Authentication failed')
                $length = [BitConverter]::GetBytes([uint32]$reason.Length)
                [Array]::Reverse($length)
                $stream.Write([byte[]](0, 0, 0, 1), 0, 4)
                $stream.Write($length, 0, 4)
                $stream.Write($reason, 0, $reason.Length)
                $stream.Flush()
                $State.Odmowy++
                Start-Sleep -Milliseconds 100
            } catch { } finally { $client.Close() }
        }
    }).AddArgument($listener).AddArgument($state)
    $handle = $ps.BeginInvoke()
    return [pscustomobject]@{ Port = $listener.LocalEndpoint.Port; State = $state; PowerShell = $ps; Handle = $handle; Listener = $listener }
}

function Stop-AtrapaVnc($Atrapa) {
    if ($null -eq $Atrapa) { return }
    $Atrapa.State.Stop = $true
    try { [void]$Atrapa.Handle.AsyncWaitHandle.WaitOne(3000) } catch { }
    try { $Atrapa.Listener.Stop() } catch { }
    try { $Atrapa.PowerShell.Dispose() } catch { }
}

function New-AtrapaKolektora {
    # Uklad jak u runnera (Zarejestruj-Kolektor): <Root>\pakiet\probe\probe.exe, <Root>\pakiet\config.json,
    # <Root>\pakiet\Kolektor.cmd (start /b /wait, --out "%~dp0..\out"), <Root>\out. Jedyna maszyna wskazuje 127.0.0.1.
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$ProbeKatalog,
        [Parameter(Mandatory = $true)][int]$Port,
        [string]$Maszyna = 'amm580'
    )
    $pakiet = Join-Path $Root 'pakiet'
    New-Item -ItemType Directory -Force -Path (Join-Path $pakiet 'probe'), (Join-Path $Root 'out') | Out-Null
    Copy-Item -Path (Join-Path $ProbeKatalog '*') -Destination (Join-Path $pakiet 'probe') -Recurse -Force
    $zrodlo = Join-Path $PSScriptRoot 'config.diagnoza-hali.json'
    $tekst = (Get-Content -LiteralPath $zrodlo -Encoding UTF8 | Where-Object { $_ -notmatch '^\s*//' }) -join "`n"
    $config = $tekst | ConvertFrom-Json
    $wpis = @($config.machines | Where-Object { $_.id -eq $Maszyna })[0]
    $wpis.host = '127.0.0.1'
    $wpis.port = $Port
    $wpis.enabled = $true
    $config.machines = @($wpis)
    [IO.File]::WriteAllText((Join-Path $pakiet 'config.json'), ($config | ConvertTo-Json -Depth 100), (New-Object Text.UTF8Encoding($false)))
    $brak = Join-Path $Root 'brak-vnc-alert-config.json'
    $polecenie = '"%~dp0probe\probe.exe" diagnoza --tryb dlugi --config "%~dp0config.json" --out "%~dp0..\out" --machines {0} --no-auth-prompt --password SYNTHETIC_ONLY --vnc-alert-config "{1}" --no-ocr' -f $Maszyna, $brak
    @('@echo off', 'setlocal DisableDelayedExpansion', 'cd /d "%~dp0"', ('start "diagnoza-hali-kolektor" /b /wait {0}' -f $polecenie), 'exit /b %errorlevel%') |
        Out-File -LiteralPath (Join-Path $pakiet 'Kolektor.cmd') -Encoding ascii
    return (Join-Path $pakiet 'Kolektor.cmd')
}

function Start-AtrapaKolektora([string]$KolektorCmd) {
    # Jak sciezka zapasowa runnera i Restart-Kolektor bez zadania: cmd /c ""Kolektor.cmd"", okno ukryte.
    return (Start-Process -FilePath 'cmd.exe' -ArgumentList ('/c ""{0}""' -f $KolektorCmd) -WorkingDirectory (Split-Path -Parent $KolektorCmd) -WindowStyle Hidden -PassThru)
}

function Read-AtrapaTekst([string]$Path) {
    # Odczyt jak Read-KolektorSummary (FileShare.ReadWrite|Delete) - nie blokuje File.Replace kolektora.
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try { $reader = New-Object IO.StreamReader($stream, (New-Object Text.UTF8Encoding($false)), $true); return $reader.ReadToEnd() }
    finally { $stream.Dispose() }
}

function Wait-AtrapaKolektora([string]$Root, [int]$TimeoutSeconds = 45) {
    # Niezalezne od Wait-KolektorStarted: zywy probe.exe z katalogu testu i jego niekoncowe podsumowanie (tekst, bez ConvertFrom-Json).
    $summary = Join-Path $Root 'out\dlugi\podsumowanie.json'
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $processes = @(Get-AtrapaProcesyKolektora $Root)
        if ($processes.Count -eq 1 -and (Test-Path -LiteralPath $summary)) {
            $raw = ''
            try { $raw = Read-AtrapaTekst $summary } catch { }
            if ($raw -match ('"pid"\s*:\s*{0}\b' -f $processes[0].ProcessId) -and $raw -match '"zakonczony"\s*:\s*false') { return $processes[0].ProcessId }
        }
        Start-Sleep -Milliseconds 250
    }
    throw 'Atrapa kolektora nie wystartowala w czasie testu.'
}

function Get-AtrapaProcesyKolektora([string]$Root) {
    # Tylko probe.exe z katalogu tego testu (nigdy inne procesy).
    $exe = [IO.Path]::GetFullPath((Join-Path $Root 'pakiet\probe\probe.exe'))
    return @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'probe.exe'" | Where-Object { $_.ExecutablePath -and ([IO.Path]::GetFullPath($_.ExecutablePath) -ieq $exe) })
}

function Stop-AtrapaKolektora([string]$Root) {
    # Sprzatanie po tescie: lagodna prosba z dokladnym startedUtc (tekst z pliku, bez ConvertFrom-Json), potem - tylko
    # gdy nadal dziala - zakonczenie WYLACZNIE procesu probe.exe z katalogu testu.
    $summaryPath = Join-Path $Root 'out\dlugi\podsumowanie.json'
    $requestPath = Join-Path $Root 'out\dlugi\stop-request.json'
    for ($i = 0; $i -lt 3; $i++) {
        $processes = @(Get-AtrapaProcesyKolektora $Root)
        if ($processes.Count -eq 0) { return }
        try {
            $raw = Read-AtrapaTekst $summaryPath
            $match = [regex]::Match($raw, '"startedUtc"\s*:\s*"([^"]+)"')
            $request = '{{"pid":{0},"startedUtc":"{1}"}}' -f $processes[0].ProcessId, $match.Groups[1].Value
            $temporary = $requestPath + '.sprzatanie'
            [IO.File]::WriteAllText($temporary, $request, (New-Object Text.UTF8Encoding($false)))
            if ([IO.File]::Exists($requestPath)) { [IO.File]::Delete($requestPath) }
            [IO.File]::Move($temporary, $requestPath)
        } catch { }
        $deadline = [DateTime]::UtcNow.AddSeconds(8)
        while ([DateTime]::UtcNow -lt $deadline -and @(Get-AtrapaProcesyKolektora $Root).Count -gt 0) { Start-Sleep -Milliseconds 200 }
    }
    foreach ($process in @(Get-AtrapaProcesyKolektora $Root)) { Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue }
}

function Test-AtrapaDpapi {
    try {
        Add-Type -AssemblyName System.Security -ErrorAction SilentlyContinue
        $p = [Security.Cryptography.ProtectedData]::Protect([byte[]](1, 2, 3), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        $b = [Security.Cryptography.ProtectedData]::Unprotect($p, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        return ($b.Length -eq 3)
    } catch { return $false }
}

function Read-AtrapaHasloDpapi([string]$Plik, [string]$Id) {
    Add-Type -AssemblyName System.Security -ErrorAction SilentlyContinue
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $entropy = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes('diagnoza-hali.hasla.v1')) } finally { $sha.Dispose() }
    # JsonSerializer escapuje "+" w base64 jako + - wartosc tylko przez parser JSON, nie wyrazeniem regularnym.
    $store = [IO.File]::ReadAllText($Plik) | ConvertFrom-Json
    $wartosc = $store.$Id
    if ([string]::IsNullOrEmpty($wartosc)) { return $null }
    $plain = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($wartosc), $entropy, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    return [Text.Encoding]::UTF8.GetString($plain)
}
