<#
.DESCRIPTION
    Tryb "szybki" (domyslny): (1) srodowisko komputera, (2) siec do maszyn przypisanych do PC, (3) probe.exe diagnoza przez -Minuty,
    (4) podsumowanie na ekran + zip na Pulpit i na kazdy pendrive, (5) pytanie (60 s, domyslnie TAK) o kolektor dlugi.
    Kazdy krok w try/catch z zapisem do runner.log — blad kroku nie przerywa kolejnych.
.PARAMETER Minuty
    Czas przebiegu probe (domyslnie 35). Calosc miesci sie w Minuty + 5.
.PARAMETER Tryb
    szybki (domyslnie) albo dlugi (tylko rejestracja kolektora, bez przebiegu szybkiego).
.PARAMETER BezPytania
    Nie pytaj na koncu o kolektor (tryb szybki) — do testow.
.PARAMETER Kolektor
    Odpowiedz na pytanie o kolektor bez czekania: tak / nie (do testow i automatyzacji).
.PARAMETER BezHasla
    Nie pytaj o haslo (jak Enter przy pytaniu) — do testow bez klawiatury.
.PARAMETER VncAlertKatalog
    Katalog zainstalowanego VNC Alert (domyslnie C:\Program Files\MaszynyAlert albo ...\VNC Alert) — do testow z atrapa.
.PARAMETER VncAlertProces
    Nazwa procesu VNC Alert (domyslnie MaszynyAlert.App, VncAlert) — do testow z atrapa.
#>
param(
    [ValidateSet('1', '2')][string]$Komputer = '1',
    [int]$Minuty = 35,
    [string]$Haslo,
    [ValidateSet('szybki', 'dlugi')][string]$Tryb = 'szybki',
    [switch]$BezPytania,
    [ValidateSet('', 'tak', 'nie')][string]$Kolektor = '',
    [switch]$HasloDoKolektora,
    [switch]$BezHasla,
    [switch]$BezVncAlert,
    [ValidateSet('', 'tak', 'nie')][string]$ZatrzymajVncAlert = '',
    [string]$VncAlertKatalog = '',
    [string[]]$VncAlertProces = @('MaszynyAlert.App', 'VncAlert')
)

$ErrorActionPreference = 'Continue'
$script:Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$script:Stamp = Get-Date -Format 'yyyyMMdd-HHmm'
$script:Maszyny = if ($Komputer -eq '1') { 'amm580' } else { 'premile,cameo1' }
$script:Hosty = @(
    @{ Id = 'amm520-1'; Nazwa = '520 (1)'; Host = '192.168.1.101'; Port = 5900 },
    @{ Id = 'amm520-2'; Nazwa = '520 (2)'; Host = '192.168.1.102'; Port = 5900 },
    @{ Id = 'amm580';   Nazwa = '580';     Host = '192.168.1.110'; Port = 5900 },
    @{ Id = 'premile';  Nazwa = 'Premile'; Host = '192.168.1.114'; Port = 5900 },
    @{ Id = 'cameo1';   Nazwa = 'Cameo 1'; Host = '192.168.1.111'; Port = 5900 }
)
$script:KolektorRoot = Join-Path $env:LOCALAPPDATA 'diagnoza-hali'
$script:ZadanieNazwa = 'diagnoza-hali-kolektor'
$script:Log = $null

function Zapisz-Log([string]$Tekst) {
    $linia = '{0} {1}' -f (Get-Date -Format 'HH:mm:ss'), $Tekst
    Write-Host $linia
    if ($script:Log) { try { Add-Content -LiteralPath $script:Log -Value $linia -Encoding UTF8 } catch { } }
}

function Krok([string]$Nazwa, [scriptblock]$Blok) {
    Zapisz-Log "=== $Nazwa ==="
    try { & $Blok } catch { Zapisz-Log ("BLAD kroku '{0}': {1}" -f $Nazwa, $_.Exception.Message) }
}

function Zapisz-Plik([string]$Sciezka, $Tresc) {
    try { $Tresc | Out-File -LiteralPath $Sciezka -Encoding UTF8 -Width 4000 } catch { Zapisz-Log ("nie zapisano {0}: {1}" -f $Sciezka, $_.Exception.Message) }
}

function Ukryj-Sekrety($Obiekt) {
    if ($null -eq $Obiekt) { return }
    if ($Obiekt -is [System.Collections.IDictionary]) {
        foreach ($klucz in @($Obiekt.Keys)) {
            if ($klucz -eq 'requiresPassword' -and $Obiekt[$klucz] -is [bool]) { continue }
            if ($klucz -match '(?i)password|secret|token|hmac|api.?key|^key$') { $Obiekt[$klucz] = '***' }
            else { Ukryj-Sekrety $Obiekt[$klucz] }
        }
    } elseif ($Obiekt -is [System.Collections.IEnumerable] -and $Obiekt -isnot [string]) {
        foreach ($element in $Obiekt) { Ukryj-Sekrety $element }
    } elseif ($Obiekt -is [pscustomobject]) {
        foreach ($pole in $Obiekt.PSObject.Properties) {
            if ($pole.Name -eq 'requiresPassword' -and $pole.Value -is [bool]) { continue }
            if ($pole.Name -match '(?i)password|secret|token|hmac|api.?key|^key$') { $pole.Value = '***' }
            else { Ukryj-Sekrety $pole.Value }
        }
    }
}

function Kopiuj-JsonBezSekretow([string]$Zrodlo, [string]$Cel) {
    # Brak zapisu surowej tresci po bledzie parsera. Config pakietu ma komentarze w osobnych wierszach.
    $tresc = (Get-Content -LiteralPath $Zrodlo -Encoding UTF8 -ErrorAction Stop | Where-Object { $_ -notmatch '^\s*//' }) -join "`n"
    $obiekt = $tresc | ConvertFrom-Json -ErrorAction Stop
    Ukryj-Sekrety $obiekt
    $obiekt | ConvertTo-Json -Depth 100 | Out-File -LiteralPath $Cel -Encoding UTF8 -ErrorAction Stop
}

function Uruchom-Polecenie([string]$Nazwa, [string]$Plik, [string]$Polecenie, [string[]]$Argumenty) {
    try {
        $wynik = & $Polecenie @Argumenty 2>&1 | Out-String
        Zapisz-Plik $Plik $wynik
        Zapisz-Log ("{0}: OK ({1} znakow) -> {2}" -f $Nazwa, $wynik.Length, (Split-Path -Leaf $Plik))
    } catch { Zapisz-Log ("{0}: blad {1}" -f $Nazwa, $_.Exception.Message) }
}

function Test-Port([string]$Adres, [int]$Port, [int]$LimitMs) {
    $klient = New-Object System.Net.Sockets.TcpClient
    try {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $async = $klient.BeginConnect($Adres, $Port, $null, $null)
        if (-not $async.AsyncWaitHandle.WaitOne($LimitMs, $false)) { return ("zamkniety/brak odpowiedzi (limit {0} ms)" -f $LimitMs) }
        $klient.EndConnect($async)
        return ("otwarty ({0} ms)" -f $sw.ElapsedMilliseconds)
    } catch { return ("zamkniety ({0})" -f $_.Exception.InnerException.Message) } finally { $klient.Close() }
}

function Pytanie-TakNie([string]$Tekst, [int]$Sekund) {
    Write-Host ''
    Write-Host $Tekst
    Write-Host ('Enter albo brak odpowiedzi w {0} s = TAK. Wpisz "n" i Enter = NIE.' -f $Sekund)
    $limit = (Get-Date).AddSeconds($Sekund)
    $wpis = ''
    while ((Get-Date) -lt $limit) {
        if ([Console]::KeyAvailable) {
            $k = [Console]::ReadKey($true)
            if ($k.Key -eq 'Enter') { break }
            $wpis += $k.KeyChar
        } else { Start-Sleep -Milliseconds 200 }
    }
    if ($wpis.Trim().ToLower() -like 'n*') { return 'nie' }
    return 'tak'
}

function Znajdz-VncAlert {
    $katalogi = @()
    if ($VncAlertKatalog) { $katalogi += $VncAlertKatalog }
    $katalogi += @((Join-Path $env:ProgramFiles 'MaszynyAlert'), (Join-Path $env:ProgramFiles 'VNC Alert'))
    foreach ($k in $katalogi) {
        if ($k -and (Test-Path -LiteralPath (Join-Path $k 'MaszynyAlert.App.exe'))) { return $k }
    }
    return $null
}

function Procesy-VncAlert {
    return @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $VncAlertProces -contains $_.ProcessName })
}

function Znajdz-Probe {
    foreach ($kandydat in @((Join-Path $script:Root 'probe\probe.exe'), (Join-Path $script:Root 'probe.exe'))) {
        if (Test-Path -LiteralPath $kandydat) { return $kandydat }
    }
    return $null
}

function Pobierz-Haslo {
    if ($Komputer -eq '2') { return $null }
    if ($Haslo) { return $Haslo }
    if ($BezHasla) { return $null }
    Write-Host ''
    Write-Host 'Haslo VNC do maszyny 580 (RealVNC, samo haslo bez nazwy uzytkownika).'
    Write-Host 'Wpisz je i nacisnij Enter. Gdy nie znasz — sam Enter (580 zostanie oznaczona "wymaga hasla", reszta zbierze sie normalnie).'
    $bezpieczne = Read-Host -AsSecureString 'Haslo VNC 580'
    $wskaznik = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($bezpieczne)
    try { $jawne = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($wskaznik) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($wskaznik) }
    if ([string]::IsNullOrEmpty($jawne)) { return $null }
    return $jawne
}

function Kopiuj-DoCelow([string]$Zip) {
    $cele = @()
    try {
        $pulpit = [Environment]::GetFolderPath('Desktop')
        $cel = Join-Path $pulpit (Split-Path -Leaf $Zip)
        if ($Zip -ne $cel) { Copy-Item -LiteralPath $Zip -Destination $cel -Force -ErrorAction Stop }
        $cele += $cel
    } catch { Zapisz-Log ("Pulpit: {0}" -f $_.Exception.Message) }
    try {
        $wymienne = Get-Volume -ErrorAction Stop | Where-Object { $_.DriveType -eq 'Removable' -and $_.DriveLetter }
        foreach ($w in $wymienne) {
            try {
                $cel = Join-Path ("{0}:\" -f $w.DriveLetter) (Split-Path -Leaf $Zip)
                Copy-Item -LiteralPath $Zip -Destination $cel -Force -ErrorAction Stop
                $cele += $cel
            } catch { Zapisz-Log ("pendrive {0}: {1}" -f $w.DriveLetter, $_.Exception.Message) }
        }
    } catch { Zapisz-Log ("Get-Volume: {0}" -f $_.Exception.Message) }
    return $cele
}

function Zarejestruj-Kolektor([string]$HasloVnc) {
    $script:BladKolektora = $true
    . (Join-Path $script:Root 'Kolektor-Common.ps1')
    . (Join-Path $script:Root 'Kolektor-Start.ps1')
    $pakiet = Join-Path $script:KolektorRoot 'pakiet'
    $outDlugi = Join-Path $script:KolektorRoot 'out'
    $probeSource = Znajdz-Probe
    if (-not $probeSource -or -not (Test-Path -LiteralPath (Join-Path $script:Root 'config.json'))) { throw 'Niekompletny pakiet kolektora: brak probe.exe lub config.json.' }
    $probeSrc = Split-Path -Parent $probeSource
    $poprzedniSkrot = Get-KolektorStartupShortcut -KolektorRoot $script:KolektorRoot
    try { Import-Module ScheduledTasks -ErrorAction Stop }
    catch { throw 'Harmonogram niedostepny: nie mozna wykluczyc starego zadania. Nie dodaje drugiego Autostartu.' }
    $poprzednieZadanie = Get-KolektorTask -KolektorRoot $script:KolektorRoot
    # Poprzedni kolektor musi sam domknac epizody. Nie ma Force ani zatrzymywania aplikacji VNC Alert.
    [void](Stop-Kolektor -KolektorRoot $script:KolektorRoot)
    # cmd /wait moze zakonczyc zadanie chwile po probe. IgnoreNew nie moze pominac nowego startu.
    if ($null -ne $poprzednieZadanie) {
        for ($proba = 0; $poprzednieZadanie.State -eq 'Running' -and $proba -lt 15; $proba++) {
            Start-Sleep -Milliseconds 200
            $poprzednieZadanie = Get-KolektorTask -KolektorRoot $script:KolektorRoot
        }
        if ($null -eq $poprzednieZadanie -or $poprzednieZadanie.State -eq 'Running') { throw 'Poprzednie zadanie jeszcze sie konczy lub zmienilo tozsamosc. Pakiet nie zostal podmieniony.' }
    }
    New-Item -ItemType Directory -Force -Path $pakiet, $outDlugi -ErrorAction Stop | Out-Null
    Zapisz-Log "kopiuje pakiet do $pakiet (zadanie Harmonogramu nie moze zalezec od pendrive'a)..."
    $probeDst = Join-Path $pakiet 'probe'
    if (Test-Path -LiteralPath $probeDst) {
        $resolvedProbe = [IO.Path]::GetFullPath($probeDst)
        $expectedProbe = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetFullPath($pakiet)) 'probe'))
        if (-not [string]::Equals($resolvedProbe, $expectedProbe, [StringComparison]::OrdinalIgnoreCase)) { throw 'Niepoprawny katalog docelowy probe.' }
        Remove-Item -LiteralPath $resolvedProbe -Recurse -Force -ErrorAction Stop
    }
    Copy-Item -LiteralPath $probeSrc -Destination $probeDst -Recurse -Force -ErrorAction Stop
    foreach ($f in @('config.json', 'build-info.json', 'Zbierz-Wyniki.cmd', 'Zbierz-Wyniki.ps1', 'Zatrzymaj-Kolektor.cmd', 'Zatrzymaj-Kolektor.ps1', 'Kolektor-Common.ps1', 'Kolektor-Start.ps1')) {
        $src = Join-Path $script:Root $f
        if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src -Destination (Join-Path $pakiet $f) -Force -ErrorAction Stop }
    }
    if ($script:OutSzybki) {
        $tryb = Join-Path $script:OutSzybki 'out\tryb-maszyn.json'
        if (Test-Path -LiteralPath $tryb) { Copy-Item -LiteralPath $tryb -Destination (Join-Path $outDlugi 'tryb-maszyn.json') -Force -ErrorAction Stop; Zapisz-Log 'tryb-maszyn.json z przebiegu szybkiego skopiowany do kolektora.' }
    }
    # ASCII zawiera tylko stale znaki. %~dp0 rozwija Unicode/spacje dopiero w cmd.exe na stanowisku.
    $cmd = '"%~dp0probe\probe.exe" diagnoza --tryb dlugi --config "%~dp0config.json" --out "%~dp0..\out" --machines {0} --no-auth-prompt' -f $script:Maszyny
    if ($HasloDoKolektora) { Zapisz-Log 'HasloDoKolektora: zapis jawny jest zablokowany; kolektor korzysta tylko z DPAPI.' }
    $kolektorCmd = Join-Path $pakiet 'Kolektor.cmd'
    @('@echo off', 'setlocal DisableDelayedExpansion', 'rem /wait utrzymuje zadanie do zakonczenia probe; /b nie otwiera drugiego okna.', 'cd /d "%~dp0"', ('start "diagnoza-hali-kolektor" /b /wait {0}' -f $cmd), 'exit /b %errorlevel%') | Out-File -LiteralPath $kolektorCmd -Encoding ascii -ErrorAction Stop
    # Register-ScheduledTask moze byc zablokowane polityka zwyklego konta. Stan Ready nie oznacza pracy probe.
    $zarejestrowano = $false
    if ($poprzedniSkrot) { Remove-Item -LiteralPath $poprzedniSkrot -Force -ErrorAction Stop }
    try {
        $akcja = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument ('/c ""{0}""' -f $kolektorCmd) -WorkingDirectory $pakiet
        $wyzwalacz = New-ScheduledTaskTrigger -AtLogOn -User ("{0}\{1}" -f $env:USERDOMAIN, $env:USERNAME)
        $ustawienia = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -StartWhenAvailable
        $zad = Register-ScheduledTask -TaskName $script:ZadanieNazwa -Action $akcja -Trigger $wyzwalacz -Settings $ustawienia -Force -ErrorAction Stop
        $zarejestrowano = $true
        Zapisz-Log ("zadanie {0}: zarejestrowane; teraz sprawdze proces i podsumowanie." -f $zad.TaskName)
    } catch {
        if ($poprzednieZadanie -or (Get-KolektorTask -KolektorRoot $script:KolektorRoot)) { throw 'Nie udalo sie zaktualizowac istniejacego zadania. Nie dodaje drugiego Autostartu.' }
        Zapisz-Log 'Rejestracja zadania nie powiodla sie — sprobuje Autostartu tego uzytkownika.'
    }
    $startedAfter = [DateTimeOffset]::UtcNow
    if ($zarejestrowano) {
        # Nie ma zapasowego drugiego startu, jezeli zadanie przyjeto, ale nie potwierdzono uruchomienia.
        Start-ScheduledTask -TaskName $script:ZadanieNazwa -ErrorAction Stop
        $potwierdzenie = Wait-KolektorStarted -KolektorRoot $script:KolektorRoot -StartedAfterUtc $startedAfter
    } else {
        $autostart = [Environment]::GetFolderPath('Startup')
        $skrot = Join-Path $autostart ($script:ZadanieNazwa + '.lnk')
        $ws = New-Object -ComObject WScript.Shell
        $lnk = $ws.CreateShortcut($skrot)
        [void](Get-KolektorStartupShortcut -KolektorRoot $script:KolektorRoot)
        $lnk.TargetPath = $kolektorCmd
        $lnk.Arguments = ''
        $lnk.WorkingDirectory = $pakiet
        $lnk.WindowStyle = 7
        $lnk.Save()
        Zapisz-Log 'Zapisano Autostart. Teraz sprawdze rzeczywiste uruchomienie kolektora.'
        Start-Process -FilePath 'cmd.exe' -ArgumentList ('/c ""{0}""' -f $kolektorCmd) -WorkingDirectory $pakiet -WindowStyle Hidden -ErrorAction Stop
        $potwierdzenie = Wait-KolektorStarted -KolektorRoot $script:KolektorRoot -StartedAfterUtc $startedAfter
    }
    $script:BladKolektora = $false
    Zapisz-Log ("Kolektor: potwierdzono uruchomienie (PID {0}). Stan maszyn i liczbe klatek sprawdz w {1}\dlugi\podsumowanie.json. Wyniki zbiera Zbierz-Wyniki.cmd; zatrzymanie: Zatrzymaj-Kolektor.cmd." -f $potwierdzenie.pid, $outDlugi)
    if ($HasloVnc -and -not $HasloDoKolektora) { Zapisz-Log 'haslo 580 nie jest w poleceniu zadania — kolektor bierze je z hasla.dpapi (probe.exe haslo lista).' }
}

# ---------------------------------------------------------------------------------------------------------------
if ($Tryb -eq 'dlugi') {
    New-Item -ItemType Directory -Force -Path $script:KolektorRoot | Out-Null
    $script:Log = Join-Path $script:KolektorRoot ("runner-dlugi-{0}.log" -f $script:Stamp)
    $script:OutSzybki = $null
    if (-not (Znajdz-Probe)) { Zapisz-Log 'BLAD: brak probe\probe.exe obok skryptu.'; exit 1 }
    $h = Pobierz-Haslo
    if ($h) {
        $probeExe = Znajdz-Probe
        $poprzednieHaslo = [Environment]::GetEnvironmentVariable('VNCALERT_DIAGNOZA_PASSWORD_580', 'Process')
        try {
            # Bez potoku: PS 5.1 koduje potok wg $OutputEncoding z zakresu zaleznego od sposobu startu (-File/-Command).
            [Environment]::SetEnvironmentVariable('VNCALERT_DIAGNOZA_PASSWORD_580', $h, 'Process')
            $wynik = (& $probeExe haslo zapisz amm580 --z-env --out (Join-Path $script:KolektorRoot 'haslo') 2>&1 | Out-String)
            if ($LASTEXITCODE -eq 0) { Zapisz-Log ("haslo 580 zapisane dla kolektora, zaszyfrowane dla konta {0} na tym PC." -f $env:USERNAME) } else { Zapisz-Log ("zapis hasla nie powiodl sie (kod {0})." -f $LASTEXITCODE) }
        } catch { Zapisz-Log 'Nie zapisano hasla w DPAPI; szczegoly bledu pominieto, aby nie ujawnic hasla.' }
        finally { [Environment]::SetEnvironmentVariable('VNCALERT_DIAGNOZA_PASSWORD_580', $(if ($null -eq $poprzednieHaslo) { [NullString]::Value } else { $poprzednieHaslo }), 'Process') }
    }
    Krok 'Rejestracja kolektora dlugiego' { Zarejestruj-Kolektor $h }
    exit $(if ($script:BladKolektora) { 1 } else { 0 })
}

$script:OutSzybki = Join-Path $script:Root ("out-PC{0}-{1}" -f $Komputer, $script:Stamp)
New-Item -ItemType Directory -Force -Path $script:OutSzybki | Out-Null
$script:Log = Join-Path $script:OutSzybki 'runner.log'
$srod = Join-Path $script:OutSzybki 'srodowisko'
New-Item -ItemType Directory -Force -Path $srod | Out-Null
$start = Get-Date
Zapisz-Log ("Diagnoza hali, PC #{0}, {1} min, start {2}, folder {3}" -f $Komputer, $Minuty, $start.ToString('yyyy-MM-dd HH:mm:ss'), $script:OutSzybki)

$probe = Znajdz-Probe
if (-not $probe) { Zapisz-Log 'BLAD: brak probe\probe.exe obok skryptu — paczka niekompletna. Konce.'; exit 1 }
$config = Join-Path $script:Root 'config.json'
if (-not (Test-Path -LiteralPath $config)) { Zapisz-Log 'BLAD: brak config.json obok skryptu.'; exit 1 }

$haslo = Pobierz-Haslo
Zapisz-Log ("haslo VNC 580: {0}" -f $(if ($haslo) { 'podano (nie zapisuje)' } else { 'brak' }))

$script:VncAlertKat = $null
$script:VncAlertZatrzymany = $false
$script:MaszynyPrzebiegu = $script:Maszyny
Krok '0. Zainstalowany VNC Alert' {
    $script:VncAlertKat = Znajdz-VncAlert
    Zapisz-Log '520 (1)/(2): POMINIETE na obu PC, bez TCP, skanowania i VNC. Zabierz eksport diagnostyki VNC Alert.'
    Zapisz-Log 'VNC Alert pozostaje bez zmian — pakiet nie zatrzymuje ani nie uruchamia programu.'
}

Krok '1. Srodowisko komputera' {
    foreach ($wejscie in @('config.json', 'build-info.json')) {
        $zrodlo = Join-Path $script:Root $wejscie
        if (Test-Path -LiteralPath $zrodlo) {
            try { Kopiuj-JsonBezSekretow $zrodlo (Join-Path $srod ('diagnoza-' + $wejscie)) }
            catch { Zapisz-Log ("nie zapisano oczyszczonego {0}; bez surowej kopii." -f $wejscie) }
        }
    }
    $info = [ordered]@{
        komputer = $env:COMPUTERNAME; uzytkownik = $env:USERNAME; data = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz')
        strefa = (Get-TimeZone).Id; windows = (Get-CimInstance Win32_OperatingSystem).Caption; build = [Environment]::OSVersion.Version.ToString()
        pc = $Komputer; minuty = $Minuty; powershell = $PSVersionTable.PSVersion.ToString()
    }
    try { $info.jezykiUzytkownika = (Get-WinUserLanguageList | ForEach-Object { $_.LanguageTag }) -join ',' } catch { $info.jezykiUzytkownika = "blad: $($_.Exception.Message)" }
    try {
        # Pakiety OCR Windows leza w C:\Windows\OCR\<jezyk> (np. en-US, pl-PL) — bez admina; rejestr OCR nie jest pewny.
        $ocr = Get-ChildItem (Join-Path $env:WINDIR 'OCR') -Directory -ErrorAction Stop | ForEach-Object { $_.Name }
        $info.ocrFoldery = ($ocr -join ',')
    } catch { $info.ocrFoldery = "brak/blad: $($_.Exception.Message)" }
    try {
        $pakiety = Get-WindowsCapability -Online -ErrorAction Stop | Where-Object { $_.Name -like 'Language.OCR*' -and $_.State -eq 'Installed' } | ForEach-Object { $_.Name }
        $info.ocrPakiety = ($pakiety -join ',')
    } catch { $info.ocrPakiety = "niedostepne bez admina: $($_.Exception.Message)" }
    $pf = 'C:\Program Files\MaszynyAlert'
    $info.vncAlertZainstalowany = Test-Path -LiteralPath $pf
    if ($info.vncAlertZainstalowany) {
        try { $info.vncAlertWersja = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $pf 'MaszynyAlert.App.exe')).ProductVersion } catch { $info.vncAlertWersja = 'brak exe' }
        $bi = Join-Path $pf 'build-info.json'
        if (Test-Path -LiteralPath $bi) { Copy-Item -LiteralPath $bi -Destination (Join-Path $srod 'build-info.json') -Force }
    }
    try { $info.realVncViewer = @(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like '*VNC*' } | ForEach-Object { "$($_.DisplayName) $($_.DisplayVersion)" }) -join '; ' } catch { }
    try { $info.procesyVnc = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match 'vnc|MaszynyAlert' } | ForEach-Object { $_.ProcessName }) -join ',' } catch { }
    Zapisz-Plik (Join-Path $srod 'srodowisko.json') ($info | ConvertTo-Json -Depth 3)
    Zapisz-Log ("komputer {0}, Windows {1}, OCR: {2}, VNC Alert: {3} {4}" -f $info.komputer, $info.windows, $info.ocrFoldery, $info.vncAlertZainstalowany, $info.vncAlertWersja)
    Uruchom-Polecenie 'ipconfig /all' (Join-Path $srod 'ipconfig.txt') 'ipconfig.exe' @('/all')
    Uruchom-Polecenie 'route print' (Join-Path $srod 'route.txt') 'route.exe' @('print')
    Uruchom-Polecenie 'arp -a (przed)' (Join-Path $srod 'arp-przed.txt') 'arp.exe' @('-a')
    try { Get-NetAdapter -ErrorAction Stop | Format-List -Property Name, InterfaceDescription, Status, LinkSpeed, MacAddress | Out-String | Out-File -LiteralPath (Join-Path $srod 'adaptery.txt') -Encoding UTF8 } catch { Zapisz-Log "Get-NetAdapter: $($_.Exception.Message)" }
    try { Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop | Format-Table -AutoSize InterfaceAlias, IPAddress, PrefixLength | Out-String | Out-File -LiteralPath (Join-Path $srod 'adresy-ipv4.txt') -Encoding UTF8 } catch { }
    # Nazwa wartosci zostaje (diagnostyka), tresc pod nazwa z Key/Password/Passwd/Secret/Token/Credential -> <pominięto>;
    # dane binarne tylko jako rozmiar, wiec wynik nie zalezy od $FormatEnumerationLimit.
    try {
        $rv = 'HKCU:\SOFTWARE\RealVNC\vncviewer'
        $sekretRealVnc = '(?i)key|password|passwd|secret|token|credential'
        if (Test-Path $rv) {
            $ustawienia = [ordered]@{}
            foreach ($pole in (Get-ItemProperty $rv).PSObject.Properties) {
                if ($pole.Name -like 'PS*') { continue }
                $ustawienia[$pole.Name] = if ($pole.Name -match $sekretRealVnc) { '<pominięto>' }
                    elseif ($pole.Value -is [byte[]]) { '<dane binarne: {0} B>' -f $pole.Value.Length }
                    elseif ($pole.Value -is [array]) { $pole.Value -join '; ' }
                    else { $pole.Value }
            }
            [pscustomobject]$ustawienia | Format-List | Out-String | Out-File -LiteralPath (Join-Path $srod 'realvnc-viewer-ustawienia.txt') -Encoding UTF8
        }
        $ab = Join-Path $env:APPDATA 'RealVNC\vncviewer\addressbook.vnc'
        if (Test-Path $ab) { Get-Content $ab | ForEach-Object { if ($_ -match $sekretRealVnc) { '<pominięto>' } else { $_ } } | Out-File -LiteralPath (Join-Path $srod 'realvnc-addressbook-bez-hasel.txt') -Encoding UTF8 }
    } catch { Zapisz-Log "RealVNC: $($_.Exception.Message)" }
    # VNC Alert: config/rules/logi/state z ProgramData — NIGDY secrets.dat / secrets.lock.
    $pd = 'C:\ProgramData\MaszynyAlert'
    if (Test-Path -LiteralPath $pd) {
        $kopia = Join-Path $srod 'MaszynyAlert-ProgramData'
        New-Item -ItemType Directory -Force -Path $kopia | Out-Null
        foreach ($f in @('config.json', 'rules.json', 'build-info.json', 'theme.json')) {
            $src = Join-Path $pd $f
            if (Test-Path -LiteralPath $src) {
                try {
                    Kopiuj-JsonBezSekretow $src (Join-Path $kopia $f)
                } catch { Zapisz-Log ("nie zapisano oczyszczonego {0}; bez tresci bledu parsera i bez surowej kopii." -f $f) }
            }
        }
        try { Get-ChildItem (Join-Path $pd 'logs') -Filter 'app-*.log' -ErrorAction Stop | Sort-Object LastWriteTime -Descending | Select-Object -First 3 | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $kopia $_.Name) -Force } } catch { }
        try { Get-ChildItem (Join-Path $pd 'state') -Filter '*.json' -ErrorAction Stop | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $kopia ('state-' + $_.Name)) -Force } } catch { }
        Zapisz-Log 'skopiowano config/rules/logi/state VNC Alert (bez secrets.dat).'
    } else { Zapisz-Log 'C:\ProgramData\MaszynyAlert nie istnieje — VNC Alert nie jest tu zainstalowany.' }
}

Krok '2. Siec do maszyn' {
    $siec = Join-Path $script:OutSzybki 'siec'
    New-Item -ItemType Directory -Force -Path $siec | Out-Null
    foreach ($m in ($script:Hosty | Where-Object { ($script:MaszynyPrzebiegu -split ',') -contains $_.Id -and $_.Id -notlike 'amm520-*' })) {
        $plik = Join-Path $siec ("{0}.txt" -f $m.Id)
        $linie = @("=== {0} ({1}) {2}:{3} ===" -f $m.Nazwa, $m.Id, $m.Host, $m.Port)
        try { $linie += (& ping.exe -n 4 -w 1000 $m.Host 2>&1 | Out-String) } catch { $linie += "ping: $($_.Exception.Message)" }
        foreach ($port in @($m.Port, 9100)) {
            # Wlasny test TCP z limitem 3 s zamiast Test-NetConnection (ten na martwym adresie potrafi czekac ponad 20 s na port).
            $linie += ("TCP port {0}: {1}" -f $port, (Test-Port $m.Host $port 3000))
        }
        try { $linie += (& tracert.exe -h 3 -w 500 $m.Host 2>&1 | Out-String) } catch { $linie += "tracert: $($_.Exception.Message)" }
        try {
            $arp = (& arp.exe -a 2>&1 | Out-String) -split "`r?`n" | Where-Object { $_ -match [regex]::Escape($m.Host) }
            $linie += "arp: " + ($(if ($arp) { $arp -join ' | ' } else { 'brak wpisu' }))
            if ($arp -and $arp[0] -match '([0-9a-f]{2}-){5}[0-9a-f]{2}') {
                $mac = $Matches[0].ToUpper()
                $oui = $mac.Substring(0, 8)
                $producenci = @{ '00-1E-C0' = 'Microchip/SMSC'; '00-0C-29' = 'VMware'; '00-50-56' = 'VMware'; 'B8-27-EB' = 'Raspberry Pi'; 'DC-A6-32' = 'Raspberry Pi'; 'E4-5F-01' = 'Raspberry Pi'; '00-80-E1' = 'STMicroelectronics'; '00-04-A3' = 'Microchip'; '00-1B-21' = 'Intel'; '00-E0-4C' = 'Realtek'; '00-0E-C6' = 'ASIX (USB-LAN)'; '00-50-C2' = 'IEEE (OEM)'; '70-B3-D5' = 'IEEE (OEM)'; '00-30-11' = 'HMS Industrial'; '00-30-DE' = 'Wago'; '00-0E-8C' = 'Siemens'; '00-60-35' = 'Dallmeier'; 'AC-DE-48' = 'prywatny'; '00-19-99' = 'Fujitsu'; '00-11-11' = 'Intel'; 'B4-2E-99' = 'Giga-Byte'; '00-01-29' = 'DFI'; '00-30-18' = 'Jetway'; '00-E0-C5' = 'BCOM'; '80-EE-73' = 'Shuttle'; '00-30-64' = 'ADLINK'; '00-D0-C9' = 'Advantech'; '00-0B-AB' = 'Advantech'; '74-FE-48' = 'Advantech'; '00-90-E8' = 'Moxa'; '00-0D-4B' = 'Roku'; '00-1C-C0' = 'Intel'; '00-14-2A' = 'Elitegroup'; '00-E0-4B' = 'Jump Industrielle'; '68-ED-A4' = 'Shenzhen Shiningworth' }
                $prod = if ($producenci.ContainsKey($oui)) { $producenci[$oui] } else { 'nieznany OUI (sprawdz w domu)' }
                $linie += ("MAC {0} -> producent (OUI {1}): {2}" -f $mac, $oui, $prod)
            }
        } catch { $linie += "arp: $($_.Exception.Message)" }
        Zapisz-Plik $plik ($linie -join "`r`n")
        $tcpOpis = ($linie | Where-Object { $_ -like 'TCP port*' }) -join '; '
        Zapisz-Log ("{0}: {1}" -f $m.Nazwa, $tcpOpis)
    }
}

Krok '3. probe.exe diagnoza' {
    $outProbe = Join-Path $script:OutSzybki 'out'
    $argumenty = @('diagnoza', '--tryb', 'szybki', '--machines', $script:MaszynyPrzebiegu, '--config', $config, '--out', $outProbe, '--minuty', "$Minuty", '--no-zip', '--no-auth-prompt')
    Zapisz-Log ("uruchamiam: probe.exe {0}{1}" -f ($argumenty -join ' '), $(if ($haslo) { ' (haslo 580 w pamieci procesu)' } else { '' }))
    $poprzednieHaslo = [Environment]::GetEnvironmentVariable('VNCALERT_DIAGNOZA_PASSWORD_580', 'Process')
    $kod = -1
    try {
        # $null w parametrze string = "": PS 5.1 (.NET Framework) usuwa wtedy zmienna, pwsh 7.6 (.NET 10) zostawia pusta.
        # [NullString]::Value usuwa ja w obu (bez hasla dziecko nie dziedziczy wartosci po rodzicu).
        [Environment]::SetEnvironmentVariable('VNCALERT_DIAGNOZA_PASSWORD_580', $(if ($null -eq $haslo) { [NullString]::Value } else { $haslo }), 'Process')
        & $probe @argumenty
        $kod = $LASTEXITCODE
    } catch { Zapisz-Log 'probe.exe nie uruchomil sie. Przy blokadzie Smart App Control przerwij probe; nie zmieniaj zabezpieczen Windows.' }
    finally { [Environment]::SetEnvironmentVariable('VNCALERT_DIAGNOZA_PASSWORD_580', $(if ($null -eq $poprzednieHaslo) { [NullString]::Value } else { $poprzednieHaslo }), 'Process') }
    Zapisz-Log ("probe.exe zakonczyl sie kodem {0} (0 = wszystkie maszyny polaczone, 3 = czesc, 4 = zadna, 1 = blad)" -f $kod)
    $script:KodProbe = $kod
    $pods = Join-Path $outProbe 'diagnoza\podsumowanie.txt'
    if (Test-Path -LiteralPath $pods) {
        Write-Host ''
        Write-Host '================= PODSUMOWANIE DIAGNOZY ================='
        Get-Content -LiteralPath $pods | ForEach-Object { Write-Host $_ }
        Write-Host '========================================================='
    } else { Zapisz-Log 'brak podsumowanie.txt — sprawdz out\raport.txt.' }
    Uruchom-Polecenie 'arp -a (po)' (Join-Path $srod 'arp-po.txt') 'arp.exe' @('-a')
    Zapisz-Log '520: bez aktywnej diagnozy; VNC Alert nie byl zatrzymywany.'
}

Krok '4. Zip na Pulpit i pendrive' {
    $zip = Join-Path $script:Root ("diagnoza-hali-PC{0}-{1}.zip" -f $Komputer, $script:Stamp)
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($script:OutSzybki, $zip)
    Zapisz-Log ("zip: {0} ({1:N1} MB)" -f $zip, ((Get-Item -LiteralPath $zip).Length / 1MB))
    $cele = Kopiuj-DoCelow $zip
    foreach ($c in $cele) { Zapisz-Log "kopia: $c" }
    $script:Zip = $zip
}
if (-not $script:Zip -or -not (Test-Path -LiteralPath $script:Zip)) {
    Zapisz-Log 'BLAD: ZIP nie powstal. Zachowaj folder out; nie ma gotowego archiwum do zabrania.'
    $script:BladZip = $true
}

$czas = (Get-Date) - $start
Zapisz-Log ("Koniec trybu szybkiego po {0:N1} min (limit {1} min). Kod probe: {2}." -f $czas.TotalMinutes, ($Minuty + 5), $script:KodProbe)

if (-not $BezPytania) {
    Krok '5. Kolektor dlugi' {
        $odp = $Kolektor
        if (-not $odp) {
            Write-Host ''
            Write-Host 'Zostawic na tym komputerze KOLEKTOR DLUGI (zbiera prawdziwe alarmy przez dni/tygodnie, bez obslugi)?'
            Write-Host 'Enter albo brak odpowiedzi w 60 s = TAK. Wpisz "n" i Enter, jesli nie chcesz zostawiac kolektora.'
            $limit = (Get-Date).AddSeconds(60)
            $wpis = ''
            while ((Get-Date) -lt $limit) {
                if ([Console]::KeyAvailable) {
                    $k = [Console]::ReadKey($true)
                    if ($k.Key -eq 'Enter') { break }
                    $wpis += $k.KeyChar
                } else { Start-Sleep -Milliseconds 200 }
            }
            $odp = if ($wpis.Trim().ToLower() -like 'n*') { 'nie' } else { 'tak' }
        }
        Zapisz-Log "kolektor dlugi: $odp"
        if ($odp -eq 'tak') {
            if ($haslo) {
                $poprzednieHaslo = [Environment]::GetEnvironmentVariable('VNCALERT_DIAGNOZA_PASSWORD_580', 'Process')
                try {
                    [Environment]::SetEnvironmentVariable('VNCALERT_DIAGNOZA_PASSWORD_580', $haslo, 'Process')
                    $wynik = (& $probe haslo zapisz amm580 --z-env --out (Join-Path $script:OutSzybki 'out') 2>&1 | Out-String)
                    if ($LASTEXITCODE -eq 0) { Zapisz-Log ("haslo 580 zapisane dla kolektora, zaszyfrowane dla konta {0} na tym PC (%LOCALAPPDATA%\diagnoza-hali\hasla.dpapi)." -f $env:USERNAME) }
                    else { Zapisz-Log ("zapis hasla dla kolektora nie powiodl sie (kod {0}); bez tresci bledu." -f $LASTEXITCODE) }
                } catch { Zapisz-Log 'Nie zapisano hasla w DPAPI; szczegoly bledu pominieto, aby nie ujawnic hasla.' }
                finally { [Environment]::SetEnvironmentVariable('VNCALERT_DIAGNOZA_PASSWORD_580', $(if ($null -eq $poprzednieHaslo) { [NullString]::Value } else { $poprzednieHaslo }), 'Process') }
            } else { Zapisz-Log 'bez hasla 580 — kolektor oznaczy 580 "wymaga hasla"; zapisz je pozniej: probe\probe.exe haslo zapisz amm580' }
            Zarejestruj-Kolektor $haslo
        }
    }
}

if ($script:Zip) { Zapisz-Log ('Zabierz istniejacy ZIP: ' + $script:Zip + ' (i zdjecia tabliczek). Sprawdz kopie na pendrive.') }
exit $(if ($script:BladZip -or $script:BladKolektora) { 1 } elseif ($script:KodProbe -ge 0) { $script:KodProbe } else { 1 })
