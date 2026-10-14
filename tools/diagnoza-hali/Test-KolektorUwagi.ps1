<#
.DESCRIPTION
    Kazdy Check konczy sie PASS, FAIL albo SKIP i nie przerywa nastepnych; kod wyjscia = liczba FAIL.
    Bez sieci poza 127.0.0.1, bez Harmonogramu, bez ProgramData/Program Files, bez zatrzymywania VNC Alert.
    Checki "[prawdziwy kolektor]" uruchamiaja kopie probe.exe (-ProbeKatalog = wynik budowania src\MaszynyAlert.Probe,
    Release) w katalogu testu, z --password (bez magazynu DPAPI w %LOCALAPPDATA%), --vnc-alert-config na nieistniejacy plik
    w katalogu testu i --no-ocr; jedyna maszyna wskazuje zamkniety port 127.0.0.1. Konczone sa tylko te procesy.
    Uruchomienie: powershell.exe -NoProfile -ExecutionPolicy Bypass -File Test-KolektorUwagi.ps1 -KatalogRoboczy <tmp>
#>
param(
    [string]$KatalogRoboczy = (Join-Path $PSScriptRoot ('..\..\artifacts\test-kolektor-uwagi-' + [guid]::NewGuid().ToString('N'))),
    [string]$ProbeKatalog = (Join-Path $PSScriptRoot '..\..\src\MaszynyAlert.Probe\bin\Release\net10.0-windows10.0.19041.0')
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $KatalogRoboczy -Force | Out-Null
$KatalogRoboczy = (Resolve-Path -LiteralPath $KatalogRoboczy).Path
$script:passed = 0; $script:failed = 0; $script:skipped = 0
function Assert([bool]$Warunek, [string]$Opis) { if (-not $Warunek) { throw $Opis } }
function Skip([string]$Powod) { throw ('SKIP: ' + $Powod) }
function Check([string]$Nazwa, [scriptblock]$Test) {
    try { & $Test; $script:passed++; Write-Host ('PASS: ' + $Nazwa) }
    catch {
        if ($_.Exception.Message.StartsWith('SKIP: ')) { $script:skipped++; Write-Host ('SKIP: ' + $Nazwa + ' -> ' + $_.Exception.Message.Substring(6)) }
        else { $script:failed++; Write-Host ('FAIL: ' + $Nazwa + ' -> ' + $_.Exception.Message) }
    }
}
$utf8 = New-Object Text.UTF8Encoding($false)
. (Join-Path $PSScriptRoot 'Kolektor-Common.ps1')
. (Join-Path $PSScriptRoot 'Kolektor-Start.ps1')
. (Join-Path $PSScriptRoot 'Test-KolektorAtrapy.ps1')
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Diagnoza-Hali.ps1'), [ref]$tokens, [ref]$errors)
Assert ($errors.Count -eq 0) 'Niepoprawna skladnia runnera'
$probeExe = Join-Path $ProbeKatalog 'probe.exe'
$maProbe = Test-Path -LiteralPath $probeExe
$maDpapi = Test-AtrapaDpapi

function Write-PodsumowanieSurowe([string]$Root, [int]$ProcessId, [string]$StartedUtc, [bool]$Zakonczony) {
    # Ten sam ksztalt i format czasu co DlugiKolektor.WriteSummary (UtcDateTime.ToString("O")); zapis bez ConvertTo-Json.
    $dir = Join-Path $Root 'out\dlugi'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $json = '{{"tryb":"dlugi","pid":{0},"startedUtc":"{1}","heartbeatUtc":"{2}","zakonczony":{3},"maszyny":{{}}}}' -f $ProcessId, $StartedUtc, [DateTime]::UtcNow.ToString('O'), $(if ($Zakonczony) { 'true' } else { 'false' })
    [IO.File]::WriteAllText((Join-Path $dir 'podsumowanie.json'), $json, $utf8)
}

Check 'kolektor-probe-01 [atrapa procesu]: Stop-Kolektor zapisuje prosbe, gdy stop-request.json zostal po poprzednim zatrzymaniu' {
    $root = Join-Path $KatalogRoboczy 'u01-atrapa'
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'pakiet\probe'), (Join-Path $root 'out\dlugi') | Out-Null
    $requestPath = Join-Path $root 'out\dlugi\stop-request.json'
    [IO.File]::WriteAllText($requestPath, '{"pid":2012,"startedUtc":"2026-10-11T17:47:40.3243409Z"}', $utf8)
    $script:u01 = @{ Root = $root; Started = [DateTime]::UtcNow.AddSeconds(-30).ToString('O'); Created = [DateTime]::Now.AddMinutes(-1) }
    Write-PodsumowanieSurowe $root 4242 $script:u01.Started $false
    function Get-CimInstance {
        [CmdletBinding()]param([string]$ClassName, [string]$Filter)
        $request = [IO.File]::ReadAllText((Join-Path $script:u01.Root 'out\dlugi\stop-request.json'))
        if ($request -match '"pid"\s*:\s*4242') { Write-PodsumowanieSurowe $script:u01.Root 4242 $script:u01.Started $true; return @() }
        $exe = Join-Path $script:u01.Root 'pakiet\probe\probe.exe'
        return @([pscustomobject]@{ ProcessId = 4242; ExecutablePath = $exe; CreationDate = $script:u01.Created
            CommandLine = ('"{0}" diagnoza --tryb dlugi --out "{1}" --no-auth-prompt' -f $exe, (Join-Path $script:u01.Root 'out')) })
    }
    $ok = $false; $blad = ''
    try { $ok = Stop-Kolektor -KolektorRoot $root -TimeoutSeconds 2 } catch { $blad = $_.Exception.Message }
    Assert ($ok -eq $true) ("Stop-Kolektor odmowil przy istniejacym stop-request.json: '" + $blad + "'")
    Assert ([IO.File]::ReadAllText($requestPath) -match '"pid"\s*:\s*4242') 'Nie zapisano prosby dla biezacej instancji'
}

Check 'kolektor-probe-01 [prawdziwy kolektor 127.0.0.1]: drugie lagodne zatrzymanie po ponownym starcie (cykl Zbierz-Wyniki)' {
    if (-not $maProbe) { Skip ('brak ' + $probeExe + ' (zbuduj src\MaszynyAlert.Probe -c Release)') }
    $root = Join-Path $KatalogRoboczy 'u01-e2e'
    $cmd = New-AtrapaKolektora -Root $root -ProbeKatalog $ProbeKatalog -Port (Get-AtrapaWolnyPort)
    try {
        $t0 = [DateTimeOffset]::UtcNow
        [void](Start-AtrapaKolektora $cmd)
        $first = Wait-KolektorStarted -KolektorRoot $root -StartedAfterUtc $t0 -TimeoutSeconds 45
        try { [void](Stop-Kolektor -KolektorRoot $root) }
        catch {
            Start-Sleep -Seconds 2
            $koniec = (Read-AtrapaTekst (Join-Path $root 'out\dlugi\podsumowanie.json')) -match '"zakonczony"\s*:\s*true'
            Assert ((@(Get-AtrapaProcesyKolektora $root).Count -eq 0) -and $koniec) ('Pierwsze lagodne zatrzymanie nie powiodlo sie: ' + $_.Exception.Message)
        }
        Assert (Test-Path -LiteralPath (Join-Path $root 'out\dlugi\stop-request.json')) 'Po pierwszym zatrzymaniu brak stop-request.json'
        # Zbierz-Wyniki bez -Usun wznawia kolektor (Restart-Kolektor); stop-request.json zostaje w out\dlugi.
        $t1 = [DateTimeOffset]::UtcNow
        [void](Start-AtrapaKolektora $cmd)
        $second = Wait-KolektorStarted -KolektorRoot $root -StartedAfterUtc $t1 -TimeoutSeconds 45
        Assert ($second.pid -ne $first.pid) 'Nie uruchomiono drugiej instancji'
        $ok = $false; $blad = ''
        try { $ok = Stop-Kolektor -KolektorRoot $root } catch { $blad = $_.Exception.Message }
        $dziala = @(Get-AtrapaProcesyKolektora $root).Count
        Assert ($ok -eq $true) ("Drugie lagodne zatrzymanie odrzucone: '" + $blad + "'; procesow kolektora nadal: " + $dziala + ' (Zbierz-Wyniki: brak ZIP, -Usun nie usuwa autostartu)')
    } finally { Stop-AtrapaKolektora $root }
}

Check 'kolektor-probe-01b: prosba zapisana, gdy kolektor C# akurat czyta stary stop-request.json; dluzszy odczyt = odmowa bez zmian' {
    $root = Join-Path $KatalogRoboczy 'u01b'
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'pakiet\probe'), (Join-Path $root 'out\dlugi') | Out-Null
    $requestPath = Join-Path $root 'out\dlugi\stop-request.json'
    $stara = '{"pid":2012,"startedUtc":"2026-10-11T17:47:40.3243409Z"}'
    $script:u01b = @{ Root = $root; Started = [DateTime]::UtcNow.AddSeconds(-30).ToString('O'); Created = [DateTime]::Now.AddMinutes(-1) }
    function Get-CimInstance {
        [CmdletBinding()]param([string]$ClassName, [string]$Filter)
        $request = [IO.File]::ReadAllText((Join-Path $script:u01b.Root 'out\dlugi\stop-request.json'))
        if ($request -match '"pid"\s*:\s*4646') { Write-PodsumowanieSurowe $script:u01b.Root 4646 $script:u01b.Started $true; return @() }
        $exe = Join-Path $script:u01b.Root 'pakiet\probe\probe.exe'
        return @([pscustomobject]@{ ProcessId = 4646; ExecutablePath = $exe; CreationDate = $script:u01b.Created
            CommandLine = ('"{0}" diagnoza --tryb dlugi --out "{1}" --no-auth-prompt' -f $exe, (Join-Path $script:u01b.Root 'out')) })
    }
    foreach ($przypadek in @(@{ Ms = 1000; Sukces = $true }, @{ Ms = 5000; Sukces = $false })) {
        [IO.File]::WriteAllText($requestPath, $stara, $utf8)
        Write-PodsumowanieSurowe $root 4646 $script:u01b.Started $false
        $stan = [hashtable]::Synchronized(@{ Otwarty = $false })
        $ps = [PowerShell]::Create()
        [void]$ps.AddScript({ param($Path, $Ms, $Stan)
            $s = New-Object IO.FileStream($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            try { $Stan.Otwarty = $true; Start-Sleep -Milliseconds $Ms } finally { $s.Dispose() } }).AddArgument($requestPath).AddArgument($przypadek.Ms).AddArgument($stan)
        $h = $ps.BeginInvoke()
        $ok = $false; $blad = ''
        try {
            $zegar = [Diagnostics.Stopwatch]::StartNew()
            while (-not $stan.Otwarty -and $zegar.Elapsed.TotalSeconds -lt 10) { Start-Sleep -Milliseconds 10 }
            Assert $stan.Otwarty 'Czytelnik testu nie otworzyl pliku'
            try { $ok = Stop-Kolektor -KolektorRoot $root -TimeoutSeconds 2 } catch { $blad = $_.Exception.Message }
        } finally { [void]$h.AsyncWaitHandle.WaitOne(15000); $ps.Dispose() }
        $tresc = [IO.File]::ReadAllText($requestPath)
        $tmp = @(Get-ChildItem -LiteralPath (Join-Path $root 'out\dlugi') -Filter 'stop-request.json.*.tmp')
        if ($przypadek.Sukces) {
            Assert ($ok -eq $true -and $tresc -match '"pid"\s*:\s*4646') ("Czytelnik {0} ms: prosba niezapisana: '{1}'" -f $przypadek.Ms, $blad)
        } else {
            Assert ($ok -ne $true -and $blad.StartsWith('Nie zapisano prosby') -and $tresc -ceq $stara) (
                "Czytelnik {0} ms (dluzej niz ponowienia): oczekiwano odmowy i nietknietej starej prosby; ok={1}, blad='{2}', tresc={3}" -f $przypadek.Ms, $ok, $blad, $tresc)
        }
        Assert ($tmp.Count -eq 0) 'Zostal plik tymczasowy prosby'
    }
}

Check 'kolektor-probe-05 [atrapa procesu]: Stop-Kolektor uznaje zakonczenie, gdy wlasny kolektor znika (chwilowo bez sciezki w Win32_Process)' {
    $root = Join-Path $KatalogRoboczy 'u05'
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'pakiet\probe') | Out-Null
    $script:u05 = @{ Root = $root; Started = [DateTime]::UtcNow.AddSeconds(-30).ToString('O'); Created = [DateTime]::Now.AddMinutes(-1); Po = 0 }
    Write-PodsumowanieSurowe $root 4545 $script:u05.Started $false
    function Get-CimInstance {
        [CmdletBinding()]param([string]$ClassName, [string]$Filter)
        $exe = Join-Path $script:u05.Root 'pakiet\probe\probe.exe'
        if (Test-Path -LiteralPath (Join-Path $script:u05.Root 'out\dlugi\stop-request.json')) {
            $script:u05.Po++
            if ($script:u05.Po -eq 1) {
                # Kolektor przyjal prosbe i konczy sie: podsumowanie koncowe zapisane, proces jeszcze na liscie, bez sciezki.
                Write-PodsumowanieSurowe $script:u05.Root 4545 $script:u05.Started $true
                return @([pscustomobject]@{ ProcessId = 4545; ExecutablePath = $null; CommandLine = $null; CreationDate = $script:u05.Created })
            }
            return @()
        }
        return @([pscustomobject]@{ ProcessId = 4545; ExecutablePath = $exe; CreationDate = $script:u05.Created
            CommandLine = ('"{0}" diagnoza --tryb dlugi --out "{1}" --no-auth-prompt' -f $exe, (Join-Path $script:u05.Root 'out')) })
    }
    $ok = $false; $blad = ''
    try { $ok = Stop-Kolektor -KolektorRoot $root -TimeoutSeconds 2 } catch { $blad = $_.Exception.Message }
    Assert ($ok -eq $true) ("Stop-Kolektor zglosil blad, choc wlasny kolektor zakonczyl sie poprawnie (zakonczony=true): '" + $blad + "'")
}

# Atrapa Get-CimInstance jest definiowana w zakresie kazdego checku (pozniejsze checki uzywaja prawdziwego cmdletu).
function New-AtrapaWpisu([string]$Root, [int]$ProcessId, $Created, [switch]$BezSciezki, [switch]$BezPolecenia) {
    $exe = Join-Path $Root 'pakiet\probe\probe.exe'
    $cmd = '"{0}" diagnoza --tryb dlugi --out "{1}" --no-auth-prompt' -f $exe, (Join-Path $Root 'out')
    return [pscustomobject]@{ ProcessId = $ProcessId; CreationDate = $Created
        ExecutablePath = $(if ($BezSciezki) { $null } else { $exe }); CommandLine = $(if ($BezSciezki -or $BezPolecenia) { $null } else { $cmd }) }
}
function New-AtrapaOdczyty {
    # Kazdy argument = jeden odczyt Win32_Process (pojedynczy wpis, tablica wpisow albo @() = brak procesow).
    $lista = New-Object 'System.Collections.Generic.List[object]'
    foreach ($odczyt in $args) { $lista.Add([object[]]@($odczyt)) }
    return , $lista
}
function New-AtrapaWin32([string]$Nazwa, [int]$ProcessId) {
    $root = Join-Path $KatalogRoboczy $Nazwa
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'pakiet\probe') | Out-Null
    $script:u05x = @{ Root = $root; Pid = $ProcessId; Started = [DateTime]::UtcNow.AddSeconds(-30).ToString('O'); Created = [DateTime]::Now.AddMinutes(-1)
        Odczyty = (New-AtrapaOdczyty @()); PoProsbie = (New-AtrapaOdczyty @()); iOdczyt = 0; iPoProsbie = 0; Wywolan = 0; KoniecPoProsbie = $true }
    Write-PodsumowanieSurowe $root $ProcessId $script:u05x.Started $false
    return $root
}
function Get-AtrapaWin32Odczyt {
    $script:u05x.Wywolan++
    $requestPath = Join-Path $script:u05x.Root 'out\dlugi\stop-request.json'
    if ((Test-Path -LiteralPath $requestPath) -and [IO.File]::ReadAllText($requestPath).Contains(('"pid":{0}' -f $script:u05x.Pid))) {
        if ($script:u05x.KoniecPoProsbie) { Write-PodsumowanieSurowe $script:u05x.Root $script:u05x.Pid $script:u05x.Started $true }
        $lista = $script:u05x.PoProsbie; $i = [Math]::Min($script:u05x.iPoProsbie, $lista.Count - 1); $script:u05x.iPoProsbie++
    } else {
        $lista = $script:u05x.Odczyty; $i = [Math]::Min($script:u05x.iOdczyt, $lista.Count - 1); $script:u05x.iOdczyt++
    }
    return $lista[$i]
}

Check 'kolektor-probe-05b: wlasny kolektor konczacy sie ze sciezka, ale bez polecenia w Win32_Process - zakonczenie potwierdzone' {
    function Get-CimInstance { [CmdletBinding()]param([string]$ClassName, [string]$Filter) Get-AtrapaWin32Odczyt }
    $root = New-AtrapaWin32 'u05b' 4747
    $c = $script:u05x.Created
    $script:u05x.Odczyty = New-AtrapaOdczyty (New-AtrapaWpisu $root 4747 $c)
    $script:u05x.PoProsbie = New-AtrapaOdczyty (New-AtrapaWpisu $root 4747 $c -BezPolecenia) @()
    $ok = $false; $blad = ''
    try { $ok = Stop-Kolektor -KolektorRoot $root -TimeoutSeconds 2 } catch { $blad = $_.Exception.Message }
    Assert ($ok -eq $true) ("Wlasny konczacy sie kolektor bez polecenia uznany za blad: '" + $blad + "'")
}

Check 'kolektor-probe-05c: obcy probe.exe bez sciezki, ktory znika w trakcie sprawdzania, nie blokuje zatrzymania wlasnego kolektora' {
    function Get-CimInstance { [CmdletBinding()]param([string]$ClassName, [string]$Filter) Get-AtrapaWin32Odczyt }
    $root = New-AtrapaWin32 'u05c' 4848
    $c = $script:u05x.Created
    $obcy = [pscustomobject]@{ ProcessId = 9191; CreationDate = [DateTime]::Now.AddSeconds(-5); ExecutablePath = $null; CommandLine = $null }
    $script:u05x.Odczyty = New-AtrapaOdczyty @((New-AtrapaWpisu $root 4848 $c), $obcy) (New-AtrapaWpisu $root 4848 $c)
    $script:u05x.PoProsbie = New-AtrapaOdczyty @()
    $ok = $false; $blad = ''
    try { $ok = Stop-Kolektor -KolektorRoot $root -TimeoutSeconds 2 } catch { $blad = $_.Exception.Message }
    $prosbaPlik = Join-Path $root 'out\dlugi\stop-request.json'
    $prosba = if (Test-Path -LiteralPath $prosbaPlik) { [IO.File]::ReadAllText($prosbaPlik) } else { '' }
    Assert ($ok -eq $true -and $prosba.Contains('"pid":4848')) ("Znikajacy obcy proces zablokowal zatrzymanie: '" + $blad + "'")
}

Check 'kolektor-probe-05d: obcy probe.exe stale bez sciezki = odmowa jak przed poprawka (bez prosby, bez zatrzymania)' {
    function Get-CimInstance { [CmdletBinding()]param([string]$ClassName, [string]$Filter) Get-AtrapaWin32Odczyt }
    $root = New-AtrapaWin32 'u05d' 4949
    $c = $script:u05x.Created
    $obcy = [pscustomobject]@{ ProcessId = 9292; CreationDate = [DateTime]::Now.AddSeconds(-5); ExecutablePath = $null; CommandLine = $null }
    $script:u05x.Odczyty = New-AtrapaOdczyty @((New-AtrapaWpisu $root 4949 $c), $obcy)
    $ok = $false; $blad = ''
    try { $ok = Stop-Kolektor -KolektorRoot $root -TimeoutSeconds 2 } catch { $blad = $_.Exception.Message }
    Assert ($ok -ne $true -and $blad.StartsWith('Brak dostepu')) ("Przyjeto obcy nieczytelny proces: ok={0}, blad='{1}'" -f $ok, $blad)
    Assert (-not (Test-Path -LiteralPath (Join-Path $root 'out\dlugi\stop-request.json'))) 'Wyslano prosbe mimo nieczytelnego obcego procesu'
}

Check 'kolektor-probe-05e: po prosbie ten sam PID z innym czasem utworzenia (PID uzyty ponownie) i bez sciezki nie jest uznany za wlasny kolektor' {
    function Get-CimInstance { [CmdletBinding()]param([string]$ClassName, [string]$Filter) Get-AtrapaWin32Odczyt }
    $root = New-AtrapaWin32 'u05e' 5050
    $c = $script:u05x.Created
    $script:u05x.Odczyty = New-AtrapaOdczyty (New-AtrapaWpisu $root 5050 $c)
    $script:u05x.PoProsbie = New-AtrapaOdczyty (New-AtrapaWpisu $root 5050 ($c.AddSeconds(30)) -BezSciezki)
    $script:u05x.KoniecPoProsbie = $false
    $ok = $false; $blad = ''
    try { $ok = Stop-Kolektor -KolektorRoot $root -TimeoutSeconds 5 } catch { $blad = $_.Exception.Message }
    Assert ($ok -ne $true -and $blad.StartsWith('Brak dostepu')) ("Obcy proces o tym samym PID uznany za wlasny: ok={0}, blad='{1}'" -f $ok, $blad)
}

Check 'kolektor-probe-05f: Wait-KolektorStarted - kolektor znikajacy w kontroli 3 s (chwilowo bez sciezki) to "zakonczyl sie zaraz po starcie", nie "brak dostepu"' {
    function Get-CimInstance { [CmdletBinding()]param([string]$ClassName, [string]$Filter) Get-AtrapaWin32Odczyt }
    function Start-Sleep { }
    $root = New-AtrapaWin32 'u05f' 5151
    $c = $script:u05x.Created
    $script:u05x.Started = [DateTime]::UtcNow.ToString('O')
    Write-PodsumowanieSurowe $root 5151 $script:u05x.Started $false
    $script:u05x.Odczyty = New-AtrapaOdczyty (New-AtrapaWpisu $root 5151 $c) (New-AtrapaWpisu $root 5151 $c -BezSciezki) @()
    $blad = ''
    try { [void](Wait-KolektorStarted -KolektorRoot $root -StartedAfterUtc ([DateTimeOffset]::UtcNow.AddSeconds(-10)) -TimeoutSeconds 0) } catch { $blad = $_.Exception.Message }
    Assert ($blad.StartsWith('Kolektor zakonczyl sie zaraz po starcie')) ("Oczekiwano 'zakonczyl sie zaraz po starcie', jest: '" + $blad + "'")
}

Check 'kolektor-probe-02: zrzut RealVNC Viewer w kroku 1 runnera bez tresci sekretow (RSA_Private_Key i inne -> <pominięto>)' {
    $node = $ast.FindAll({ param($n) $n -is [Management.Automation.Language.TryStatementAst] -and $n.Body.Extent.Text.Contains('RealVNC\vncviewer') }, $true) | Select-Object -First 1
    Assert ($null -ne $node) 'Brak bloku zrzutu RealVNC w runnerze'
    $srod = Join-Path $KatalogRoboczy 'u02-srodowisko'
    New-Item -ItemType Directory -Force -Path $srod | Out-Null
    $klucz = New-Object byte[] 1191
    for ($i = 0; $i -lt $klucz.Length; $i++) { $klucz[$i] = [byte](($i * 37 + 11) % 256) }
    $klucz[0] = 0; $klucz[1] = 0; $klucz[2] = 8; $klucz[3] = 0
    $pominieto = '<pomini' + [char]0x0119 + 'to>'
    # Kazdy wzorzec nazwy, rozne wielkosci liter, binarne i tekstowe.
    $sekrety = [ordered]@{ 'RSA_Private_Key' = $klucz; 'Password' = 'SYNTHETIC_PASSWORD_NOT_IN_DUMP'; 'ProxyPasswd' = 'SYNTHETIC_PASSWD_NOT_IN_DUMP'
        'cloudAuthTOKEN' = 'SYNTHETIC_TOKEN_NOT_IN_DUMP'; 'clientsecret' = 'SYNTHETIC_SECRET_NOT_IN_DUMP'; 'SavedCredential' = 'SYNTHETIC_CREDENTIAL_NOT_IN_DUMP'
        'ServerKeyHash' = [byte[]](201..230) }
    $script:u02 = New-Object psobject
    foreach ($wpis in @(
            @('_AnlClientId', '00000000-0000-4000-8000-000000000000'), @('EnableAnalytics', 0), @('_SplashVer', '1'),
            @('_AnlSelected', '0'), @('_AnlInclRate', '0.0000'), @('ShowHelpConnecting', '0'), @('_SidebarWidth', 200),
            @('HistoryList', [string[]]('127.0.0.1::5900', 'atrapa-testowa::5901')), @('_WindowState', [byte[]](1..10)),
            @('PSPath', 'Microsoft.PowerShell.Core\Registry::HKEY_CURRENT_USER\SOFTWARE\RealVNC\vncviewer'), @('PSChildName', 'vncviewer'))) {
        $script:u02 | Add-Member -MemberType NoteProperty -Name $wpis[0] -Value $wpis[1]
    }
    foreach ($nazwa in $sekrety.Keys) { $script:u02 | Add-Member -MemberType NoteProperty -Name $nazwa -Value $sekrety[$nazwa] }
    # Ksiazka adresowa: atrapa pliku w APPDATA testu (prawdziwy profil nie jest czytany).
    $appdata = Join-Path $KatalogRoboczy 'u02-appdata'
    New-Item -ItemType Directory -Force -Path (Join-Path $appdata 'RealVNC\vncviewer') | Out-Null
    [IO.File]::WriteAllLines((Join-Path $appdata 'RealVNC\vncviewer\addressbook.vnc'), [string[]]@('[Polaczenie 1]', 'Host=127.0.0.1::5900',
        'Password=SYNTHETIC_AB_PASSWORD', 'RsaPrivateKey=SYNTHETIC_AB_KEY', 'AuthToken=SYNTHETIC_AB_TOKEN', 'Label=Atrapa testowa'), $utf8)
    function Test-Path { param([Parameter(Position = 0)]$Path)
        if ([string]$Path -eq 'HKCU:\SOFTWARE\RealVNC\vncviewer') { return $true }
        return (Microsoft.PowerShell.Management\Test-Path -LiteralPath $Path) }
    function Get-ItemProperty { param([Parameter(Position = 0)]$Path) return $script:u02 }
    $script:u02log = New-Object 'System.Collections.Generic.List[string]'
    function Zapisz-Log([string]$Tekst) { $script:u02log.Add($Tekst) }
    $plik = Join-Path $srod 'realvnc-viewer-ustawienia.txt'
    $plikAb = Join-Path $srod 'realvnc-addressbook-bez-hasel.txt'
    $limit = $global:FormatEnumerationLimit; $poprzednieAppData = $env:APPDATA
    try {
        $env:APPDATA = $appdata
        & ([scriptblock]::Create($node.Extent.Text))
        $domyslnie = [IO.File]::ReadAllText($plik); $ksiazka = [IO.File]::ReadAllText($plikAb)
        $global:FormatEnumerationLimit = -1
        & ([scriptblock]::Create($node.Extent.Text))
        $bezLimitu = [IO.File]::ReadAllText($plik)
    } finally { $global:FormatEnumerationLimit = $limit; $env:APPDATA = $poprzednieAppData }
    Assert ($script:u02log.Count -eq 0) ('Blok RealVNC zglosil blad: ' + ($script:u02log -join ' | '))
    Assert ($domyslnie -ceq $bezLimitu) 'Zrzut zalezy od $FormatEnumerationLimit'
    foreach ($tekst in @($domyslnie, $ksiazka)) {
        Assert (-not $tekst.Contains('SYNTHETIC_')) ('Tresc sekretu w zrzucie: ' + (($tekst -split "`r?`n") | Where-Object { $_.Contains('SYNTHETIC_') } | Select-Object -First 1))
    }
    $rsa = (($domyslnie -split "`r?`n") | Where-Object { $_ -match '^RSA_Private_Key\b' } | Select-Object -First 1)
    foreach ($nazwa in $sekrety.Keys) {
        Assert ($domyslnie -match ('(?m)^{0}\s*:\s*{1}\s*$' -f [regex]::Escape($nazwa), [regex]::Escape($pominieto))) ("Wartosc {0} nie jest zastapiona znacznikiem (wiersz RSA: '{1}')" -f $nazwa, $rsa)
    }
    Assert (-not $domyslnie.Contains('{')) 'Kolekcja sformatowana przez Format-List (wynik zalezny od $FormatEnumerationLimit)'
    Assert ($domyslnie -match '(?m)^_SidebarWidth\s*:\s*200\s*$' -and $domyslnie -match '(?m)^EnableAnalytics\s*:\s*0\s*$' -and
        $domyslnie -match '(?m)^HistoryList\s*:\s*127\.0\.0\.1::5900; atrapa-testowa::5901\s*$' -and $domyslnie -match '(?m)^_WindowState\s*:\s*<dane binarne: 10 B>\s*$') 'Zrzut zgubil ustawienia bez sekretow'
    Assert (-not $domyslnie.Contains('PSPath') -and -not $domyslnie.Contains('PSChildName')) 'Pola PS* w zrzucie'
    $linie = @($ksiazka -split "`r?`n" | Where-Object { $_ -ne '' })
    Assert ($linie.Count -eq 6 -and @($linie | Where-Object { $_ -ceq $pominieto }).Count -eq 3 -and $linie -contains 'Host=127.0.0.1::5900' -and $linie -contains 'Label=Atrapa testowa') ('Ksiazka adresowa: ' + ($linie -join ' | '))
}

# ConvertFrom-Json w pwsh 7 zamienia ciagi ISO na DateTime; [string] daje wtedy "MM/dd/yyyy HH:mm:ss" bez strefy i ulamkow.
Check 'kolektor-probe-03a: Wait-KolektorStarted potwierdza swieze podsumowanie zapisane przez kolektor (startedUtc ISO 8601 UTC)' {
    $root = Join-Path $KatalogRoboczy 'u03a'
    $teraz = [DateTimeOffset]::UtcNow
    Write-PodsumowanieSurowe $root 1001 $teraz.UtcDateTime.ToString('O') $false
    function Get-KolektorProcess { return @([pscustomobject]@{ ProcessId = 1001 }) }
    function Start-Sleep { }
    $wynik = $null; $blad = ''
    try { $wynik = Wait-KolektorStarted -KolektorRoot $root -StartedAfterUtc $teraz.AddSeconds(-5) -TimeoutSeconds 0 } catch { $blad = $_.Exception.Message }
    $odczyt = (Read-KolektorSummary $root).startedUtc
    Assert ($null -ne $wynik -and $wynik.pid -eq 1001) ("Nie potwierdzono swiezego startu (PowerShell {0}): startedUtc po ConvertFrom-Json = {1} '{2}'; {3}" -f $PSVersionTable.PSVersion, $odczyt.GetType().Name, [string]$odczyt, $blad)
}

Check 'kolektor-probe-03b: Stop-Kolektor przekazuje startedUtc do stop-request.json bez zmiany (C# porownuje ciag "O")' {
    $root = Join-Path $KatalogRoboczy 'u03b'
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'pakiet\probe') | Out-Null
    $script:u03 = @{ Root = $root; Started = [DateTime]::UtcNow.AddSeconds(-30).ToString('O'); Created = [DateTime]::Now.AddMinutes(-1) }
    Write-PodsumowanieSurowe $root 4343 $script:u03.Started $false
    function Get-CimInstance {
        [CmdletBinding()]param([string]$ClassName, [string]$Filter)
        $requestPath = Join-Path $script:u03.Root 'out\dlugi\stop-request.json'
        # Jak StopRequestLoopAsync: zatrzymuje sie tylko przy pid i startedUtc rownym ciagowi z podsumowania.
        if ((Test-Path -LiteralPath $requestPath) -and [IO.File]::ReadAllText($requestPath).Contains('"' + $script:u03.Started + '"')) {
            Write-PodsumowanieSurowe $script:u03.Root 4343 $script:u03.Started $true; return @()
        }
        $exe = Join-Path $script:u03.Root 'pakiet\probe\probe.exe'
        return @([pscustomobject]@{ ProcessId = 4343; ExecutablePath = $exe; CreationDate = $script:u03.Created
            CommandLine = ('"{0}" diagnoza --tryb dlugi --out "{1}" --no-auth-prompt' -f $exe, (Join-Path $script:u03.Root 'out')) })
    }
    $ok = $false; $blad = ''
    try { $ok = Stop-Kolektor -KolektorRoot $root -TimeoutSeconds 1 } catch { $blad = $_.Exception.Message }
    $prosba = ''
    if (Test-Path -LiteralPath (Join-Path $root 'out\dlugi\stop-request.json')) { $prosba = [IO.File]::ReadAllText((Join-Path $root 'out\dlugi\stop-request.json')) }
    Assert ($ok -eq $true) ("Kolektor nie rozpoznal prosby (PowerShell {0}): w podsumowaniu '{1}', w prosbie {2}; {3}" -f $PSVersionTable.PSVersion, $script:u03.Started, $prosba, $blad)
}

Check 'kolektor-probe-03c: Read-KolektorSummary oddaje startedUtc i heartbeatUtc dokladnie jak w pliku (tekst), reszta pol bez zmian' {
    $root = Join-Path $KatalogRoboczy 'u03c'
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'out\dlugi') | Out-Null
    $started = '2026-10-12T01:13:45.4303688Z'; $heartbeat = '2026-10-12T01:23:45.0000000Z'
    $json = @('{', '  "probe": "0.0.0-test",', '  "tryb": "dlugi",', '  "pid": 4242,', ('  "startedUtc": "{0}",' -f $started), ('  "heartbeatUtc": "{0}",' -f $heartbeat),
        '  "start": "2026-10-12 03:13:45",', '  "dzialaSek": 600,', '  "zakonczony": false,', '  "pominiete": {},',
        '  "maszyny": {', '    "amm580": { "nazwa": "580", "polaczonoO": "2026-10-12 03:13:50", "epizodow": { "razem": 1, "ostatni": null } }', '  }', '}') -join "`n"
    [IO.File]::WriteAllText((Join-Path $root 'out\dlugi\podsumowanie.json'), $json, $utf8)
    $s = Read-KolektorSummary $root
    Assert ($s.startedUtc -is [string] -and $s.startedUtc -ceq $started) ("startedUtc po odczycie (PowerShell {0}): {1} '{2}'" -f $PSVersionTable.PSVersion, $s.startedUtc.GetType().Name, [string]$s.startedUtc)
    Assert ($s.heartbeatUtc -is [string] -and $s.heartbeatUtc -ceq $heartbeat) ("heartbeatUtc po odczycie: {0} '{1}'" -f $s.heartbeatUtc.GetType().Name, [string]$s.heartbeatUtc)
    Assert ($s.pid -eq 4242 -and $s.zakonczony -is [bool] -and -not $s.zakonczony -and $s.tryb -ceq 'dlugi' -and $s.dzialaSek -eq 600) 'Zmienione pola liczbowe/logiczne'
    Assert ($s.maszyny.amm580.nazwa -ceq '580' -and $s.maszyny.amm580.epizodow.razem -eq 1 -and $s.start -ceq '2026-10-12 03:13:45') 'Zmienione pola zagniezdzone'
    $inv = [Globalization.CultureInfo]::InvariantCulture
    Assert ([DateTimeOffset]::Parse($s.startedUtc, $inv).UtcTicks -eq [DateTimeOffset]::Parse($started, $inv).UtcTicks) 'Parse przesuwa chwile startu'
}

Check 'kolektor-probe-04 [prawdziwy kolektor 127.0.0.1]: runner -Tryb dlugi zapisuje haslo 580, gdy poprzedni kolektor jeszcze dziala' {
    if (-not $maProbe) { Skip ('brak ' + $probeExe) }
    if (-not $maDpapi) { Skip 'DPAPI niedostepne w tej sesji' }
    $root = Join-Path $KatalogRoboczy 'u04'
    $cmd = New-AtrapaKolektora -Root $root -ProbeKatalog $ProbeKatalog -Port (Get-AtrapaWolnyPort)
    $plik = Join-Path $root 'hasla-test.dpapi'
    $wrapper = Join-Path $root 'probe-z-plikiem-testu.cmd'
    @('@echo off', ('"{0}" %* --plik "{1}"' -f $probeExe, $plik), 'exit /b %errorlevel%') | Out-File -LiteralPath $wrapper -Encoding ascii
    $blok = $ast.FindAll({ param($n) $n -is [Management.Automation.Language.IfStatementAst] -and $n.Clauses[0].Item1.Extent.Text -eq '$h' }, $true) | Select-Object -First 1
    Assert ($null -ne $blok) 'Brak bloku zapisu hasla w trybie dlugim'
    try {
        [void](Start-AtrapaKolektora $cmd)
        [void](Wait-AtrapaKolektora $root)
        $script:KolektorRoot = $root
        $script:u04logs = New-Object 'System.Collections.Generic.List[string]'
        function Zapisz-Log([string]$Tekst) { $script:u04logs.Add($Tekst) }
        function Znajdz-Probe { return $wrapper }
        $h = 'SYNTHETIC_Zażółć-580'
        $ErrorActionPreference = 'Continue'
        $wynik = ''
        . ([scriptblock]::Create($blok.Extent.Text)) # kropka: $wynik (wyjscie probe, runner go nie loguje) zostaje do diagnozy
        $ErrorActionPreference = 'Stop'
        $log = $script:u04logs -join ' | '
        $odczyt = $null
        if (Test-Path -LiteralPath $plik) { $odczyt = Read-AtrapaHasloDpapi $plik 'amm580' }
        Assert (-not $log.Contains($h)) 'Haslo w logu runnera'
        Assert (-not ([string]$wynik).Contains($h)) 'Haslo w wyjsciu probe'
        $przyczyna = (([string]$wynik -split "`r?`n") | Where-Object { $_ -match 'raport|Błąd|Blad' } | Select-Object -First 1)
        Assert (($log -match 'haslo 580 zapisane') -and ($odczyt -ceq $h)) ("Haslo nie zostalo zapisane: log runnera '" + $log + "'; probe: '" + $przyczyna + "'; plik hasel istnieje: " + (Test-Path -LiteralPath $plik))
    } finally { Stop-AtrapaKolektora $root }
}

Check 'napr-kolektor: krok 3 runnera bez hasla 580 - zmienna usunieta dla probe (nie pusta) i przywrocona po kroku (PS 5.1 i pwsh 7)' {
    $node = $ast.FindAll({ param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Krok' }, $true) |
        Where-Object { $_.CommandElements[1].Value -like '3.*' } | Select-Object -First 1
    Assert ($null -ne $node) 'Brak kroku 3 w runnerze'
    $blok = $node.CommandElements[2].ScriptBlock.GetScriptBlock()
    $zmienna = 'VNCALERT_DIAGNOZA_PASSWORD_580'
    $zapisane = [Environment]::GetEnvironmentVariable($zmienna, 'Process')
    function Zapisz-Log([string]$Tekst) { }
    function Uruchom-Polecenie { }
    function Opis-Zmiennej { $v = [Environment]::GetEnvironmentVariable('VNCALERT_DIAGNOZA_PASSWORD_580', 'Process'); if ($null -eq $v) { '<brak>' } else { "'" + $v + "'" } }
    function Atrapa-Probe { $script:u3dziecko = Opis-Zmiennej; $global:LASTEXITCODE = 4 }
    try {
        foreach ($przed in @($null, 'SYNTHETIC_STARE_HASLO')) {
            [Environment]::SetEnvironmentVariable($zmienna, $(if ($null -eq $przed) { [NullString]::Value } else { $przed }), 'Process')
            $probe = 'Atrapa-Probe'; $haslo = $null; $Minuty = 1; $config = Join-Path $KatalogRoboczy 'brak-config.json'
            $script:OutSzybki = Join-Path $KatalogRoboczy 'u3-out'; $script:MaszynyPrzebiegu = 'amm580'; $srod = $script:OutSzybki
            $script:u3dziecko = 'nie wywolano'
            & $blok
            Assert ($script:u3dziecko -ceq '<brak>') ("Probe bez hasla dostal zmienna {0} (przed krokiem: {1}; PowerShell {2})" -f $script:u3dziecko, $(if ($null -eq $przed) { '<brak>' } else { $przed }), $PSVersionTable.PSVersion)
            $po = Opis-Zmiennej
            Assert ($po -ceq $(if ($null -eq $przed) { '<brak>' } else { "'" + $przed + "'" })) ("Po kroku 3 zmienna {0} zamiast stanu sprzed kroku (PowerShell {1})" -f $po, $PSVersionTable.PSVersion)
        }
    } finally { [Environment]::SetEnvironmentVariable($zmienna, $(if ($null -eq $zapisane) { [NullString]::Value } else { $zapisane }), 'Process') }
}

Write-Host ('WYNIK: {0} PASS, {1} FAIL, {2} SKIP; PowerShell {3}; tylko 127.0.0.1 i katalog testu. Pliki: {4}' -f $script:passed, $script:failed, $script:skipped, $PSVersionTable.PSVersion, $KatalogRoboczy)
exit $script:failed
