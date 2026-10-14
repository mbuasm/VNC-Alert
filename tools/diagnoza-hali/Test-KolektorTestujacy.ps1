<#
.DESCRIPTION
    Kazdy Check konczy sie PASS, FAIL albo SKIP i nie przerywa nastepnych; kod wyjscia = liczba FAIL. PS 5.1 i pwsh 7.
    Bez sieci poza 127.0.0.1, bez Harmonogramu (Get/Start/Unregister-ScheduledTask - atrapy), bez Pulpitu i pendrive
    (Get-KolektorCopyTargets - atrapa), bez ProgramData/Program Files, bez zatrzymywania VNC Alert.
    Prawdziwe procesy: kopia probe.exe (-ProbeKatalog) i atrapy EXE kompilowane csc.exe - wylacznie w -KatalogRoboczy;
    konczone sa tylko procesy uruchomione przez ten test (po PID). Zbierz-Wyniki.ps1 - tylko funkcje (AST), bez wejscia.
    Uruchomienie: powershell.exe -NoProfile -ExecutionPolicy Bypass -File Test-KolektorTestujacy.ps1 -KatalogRoboczy <tmp>
#>
param(
    [string]$KatalogRoboczy = (Join-Path $PSScriptRoot ('..\..\artifacts\test-kolektor-testujacy-' + [guid]::NewGuid().ToString('N'))),
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
$t = $null; $e = $null
$astZbierz = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Zbierz-Wyniki.ps1'), [ref]$t, [ref]$e)
Assert ($e.Count -eq 0) 'Niepoprawna skladnia Zbierz-Wyniki.ps1'
foreach ($definicja in $astZbierz.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    . ([scriptblock]::Create($definicja.Extent.Text))
}
$script:komunikaty = New-Object 'System.Collections.Generic.List[string]'
function Powiedz([string]$Tekst) { $script:komunikaty.Add($Tekst) }
$t = $null; $e = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Diagnoza-Hali.ps1'), [ref]$t, [ref]$e)
Assert ($e.Count -eq 0) 'Niepoprawna skladnia runnera'
$probeExe = Join-Path $ProbeKatalog 'probe.exe'
$maProbe = Test-Path -LiteralPath $probeExe
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$maCsc = Test-Path -LiteralPath $csc
$hostExe = (Get-Process -Id $PID).Path
$zmienna = 'VNCALERT_DIAGNOZA_PASSWORD_580'

function New-TkExe([string]$Zrodlo, [string]$Exe) {
    # Atrapa EXE z kodu C# (csc.exe .NET Framework 4) - tylko w katalogu testu.
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Exe) | Out-Null
    $cs = $Exe + '.cs'
    [IO.File]::WriteAllText($cs, $Zrodlo, $utf8)
    $wynik = & $csc /nologo ('/out:' + $Exe) $cs 2>&1 | Out-String
    Assert (Test-Path -LiteralPath $Exe) ('Nie skompilowano atrapy: ' + $wynik)
    return $Exe
}
$zrodloObcego = 'using System; using System.Threading; public static class AtrapaObca { public static int Main(string[] a) { int ms = 120000; for (int i = 0; i + 1 < a.Length; i++) if (a[i] == "--zyj-ms") ms = int.Parse(a[i + 1]); Thread.Sleep(ms); return 0; } }'
$zrodloRaportu = @'
using System; using System.Collections; using System.IO;
public static class RaportZmiennej {
    public static int Main(string[] args) {
        // Tylko stan zmiennej (brak / pusta / dlugosc) - nigdy jej tresc.
        string stan = "ABSENT";
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables()) {
            if (string.Equals((string)e.Key, "VNCALERT_DIAGNOZA_PASSWORD_580", StringComparison.OrdinalIgnoreCase)) {
                string v = e.Value as string; stan = string.IsNullOrEmpty(v) ? "EMPTY" : ("SET:" + v.Length);
            }
        }
        File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dziecko.txt"), stan + Environment.NewLine);
        return 0;
    }
}
'@

function Write-TkPodsumowanie([string]$Root, [int]$ProcessId, [string]$StartedUtc, [bool]$Zakonczony) {
    # Ksztalt i format czasu jak DlugiKolektor.WriteSummary (UtcDateTime.ToString("O")); zapis bez ConvertTo-Json.
    $dir = Join-Path $Root 'out\dlugi'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $json = '{{"tryb":"dlugi","pid":{0},"startedUtc":"{1}","heartbeatUtc":"{2}","zakonczony":{3},"maszyny":{{}}}}' -f $ProcessId, $StartedUtc, [DateTime]::UtcNow.ToString('O'), $(if ($Zakonczony) { 'true' } else { 'false' })
    [IO.File]::WriteAllText((Join-Path $dir 'podsumowanie.json'), $json, $utf8)
}

# --- Atrapa Win32_Process: kolejne odczyty przed prosba (Przed) i po niej (Po); ostatni odczyt sie powtarza -------------
function New-TkWpis([string]$Root, [int]$ProcessId, $Created, [string]$Out = '', [string]$Exe = '', [switch]$BezSciezki, [switch]$BezPolecenia) {
    if (-not $Exe) { $Exe = Join-Path $Root 'pakiet\probe\probe.exe' }
    if (-not $Out) { $Out = Join-Path $Root 'out' }
    $cmd = '"{0}" diagnoza --tryb dlugi --out "{1}" --no-auth-prompt' -f $Exe, $Out
    return [pscustomobject]@{ ProcessId = $ProcessId; CreationDate = $Created
        ExecutablePath = $(if ($BezSciezki) { $null } else { $Exe }); CommandLine = $(if ($BezSciezki -or $BezPolecenia) { $null } else { $cmd }) }
}
function New-TkOdczyty {
    $lista = New-Object 'System.Collections.Generic.List[object]'
    foreach ($odczyt in $args) { $lista.Add([object[]]@($odczyt)) }
    return , $lista
}
function New-TkAtrapa([string]$Nazwa, [int]$ProcessId) {
    $root = Join-Path $KatalogRoboczy $Nazwa
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'pakiet\probe') | Out-Null
    $script:tk = @{ Root = $root; Pid = $ProcessId; Started = [DateTime]::UtcNow.AddSeconds(-30).ToString('O'); Created = [DateTime]::Now.AddMinutes(-1)
        Przed = (New-TkOdczyty @()); Po = (New-TkOdczyty @()); iPrzed = 0; iPo = 0; Koniec = $true; Odczytow = 0 }
    Write-TkPodsumowanie $root $ProcessId $script:tk.Started $false
    return $root
}
function Get-TkOdczyt {
    $script:tk.Odczytow++
    $requestPath = Join-Path $script:tk.Root 'out\dlugi\stop-request.json'
    if ((Test-Path -LiteralPath $requestPath) -and [IO.File]::ReadAllText($requestPath).Contains(('"pid":{0}' -f $script:tk.Pid))) {
        if ($script:tk.Koniec) { Write-TkPodsumowanie $script:tk.Root $script:tk.Pid $script:tk.Started $true }
        $lista = $script:tk.Po; $i = [Math]::Min($script:tk.iPo, $lista.Count - 1); $script:tk.iPo++
    } else {
        $lista = $script:tk.Przed; $i = [Math]::Min($script:tk.iPrzed, $lista.Count - 1); $script:tk.iPrzed++
    }
    return $lista[$i]
}

Check 'TK-01 [prawdziwy kolektor]: 4 lagodne zatrzymania z rzedu - eksport, eksport, Zatrzymaj-Kolektor.ps1 (osobny proces), eksport -Usun' {
    if (-not $maProbe) { Skip ('brak ' + $probeExe) }
    $root = Join-Path $KatalogRoboczy 'tk01-cykl'
    $cmd = New-AtrapaKolektora -Root $root -ProbeKatalog $ProbeKatalog -Port (Get-AtrapaWolnyPort)
    $kopie = Join-Path $KatalogRoboczy 'tk01-kopie'
    New-Item -ItemType Directory -Force -Path $kopie | Out-Null
    $script:tk01 = @{ Usun = $false; Wyrejestrowano = 0; Skrot = (Join-Path $KatalogRoboczy 'tk01-atrapa-skrotu.lnk'); Kopie = $kopie }
    [IO.File]::WriteAllText($script:tk01.Skrot, 'atrapa skrotu')
    [IO.File]::WriteAllText((Join-Path $root 'hasla.dpapi'), 'atrapa DPAPI')
    function Get-KolektorCopyTargets { return @($script:tk01.Kopie) }
    function Get-ScheduledTask { throw 'Test nie czyta Harmonogramu.' }
    function Start-ScheduledTask { throw 'Test nie uruchamia zadania.' }
    function Stop-Process { throw 'Test nie zatrzymuje procesow sila w tym checku.' }
    function Get-KolektorTask { param([string]$KolektorRoot)
        if ($script:tk01.Usun) { return [pscustomobject]@{ TaskName = 'diagnoza-hali-kolektor'; TaskPath = '\'; State = 'Ready' } }
        return $null }
    function Unregister-ScheduledTask { [CmdletBinding(SupportsShouldProcess = $true)]param([string]$TaskName, [string]$TaskPath) $script:tk01.Wyrejestrowano++ }
    function Get-KolektorStartupShortcut { param([string]$KolektorRoot) if ($script:tk01.Usun) { return $script:tk01.Skrot } return $null }
    $prosba = Join-Path $root 'out\dlugi\stop-request.json'
    try {
        $t0 = [DateTimeOffset]::UtcNow
        [void](Start-AtrapaKolektora $cmd)
        $pid1 = (Wait-KolektorStarted -KolektorRoot $root -StartedAfterUtc $t0 -TimeoutSeconds 45).pid
        $pidy = @($pid1)
        foreach ($n in 1, 2) {
            $script:komunikaty.Clear()
            $kod = Invoke-KolektorExport -KolektorRoot $root
            $zywe = @(Get-AtrapaProcesyKolektora $root)
            Assert ($kod -eq 0) ("Eksport {0}: kod {1}; {2}" -f $n, $kod, ($script:komunikaty -join ' | '))
            Assert ([IO.File]::ReadAllText($prosba).Contains(('"pid":{0}' -f $pidy[-1]))) ("Eksport {0}: prosba nie dla zatrzymanej instancji {1}" -f $n, $pidy[-1])
            Assert ($zywe.Count -eq 1 -and $zywe[0].ProcessId -ne $pidy[-1]) ("Eksport {0}: brak nowej instancji po wznowieniu (procesow {1})" -f $n, $zywe.Count)
            $pidy += $zywe[0].ProcessId
        }
        Assert (@(Get-ChildItem -LiteralPath $root -Filter 'diagnoza-hali-kolektor-*.zip').Count -eq 2) 'Nie powstaly dwa ZIP-y'
        Assert (@(Get-ChildItem -LiteralPath $kopie -Filter '*.zip').Count -eq 2) 'Brak kopii ZIP-ow w atrapie Pulpitu'
        # Trzecie zatrzymanie z rzedu: wejscie Zatrzymaj-Kolektor.ps1 w osobnym procesie tego samego PowerShell (-Katalog = katalog testu).
        $wyjscie = & $hostExe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Zatrzymaj-Kolektor.ps1') -Katalog $root 2>&1 | Out-String
        $kodZ = $LASTEXITCODE
        Assert ($kodZ -eq 0) ('Zatrzymaj-Kolektor.ps1: kod {0}: {1}' -f $kodZ, $wyjscie.Trim())
        Assert (@(Get-AtrapaProcesyKolektora $root).Count -eq 0) 'Zatrzymaj-Kolektor.ps1: kolektor nadal dziala'
        Assert ([IO.File]::ReadAllText($prosba).Contains(('"pid":{0}' -f $pidy[-1]))) 'Zatrzymaj-Kolektor.ps1: prosba nie dla biezacej instancji'
        Assert ((Read-AtrapaTekst (Join-Path $root 'out\dlugi\podsumowanie.json')) -match '"zakonczony"\s*:\s*true') 'Zatrzymaj-Kolektor.ps1: brak koncowego podsumowania'
        # Czwarte: ponowny start i Zbierz-Wyniki -Usun (koniec zbierania).
        $t1 = [DateTimeOffset]::UtcNow
        [void](Start-AtrapaKolektora $cmd)
        $pidy += (Wait-KolektorStarted -KolektorRoot $root -StartedAfterUtc $t1 -TimeoutSeconds 45).pid
        $script:tk01.Usun = $true
        $script:komunikaty.Clear()
        $kod = Invoke-KolektorExport -KolektorRoot $root -Remove
        Assert ($kod -eq 0) ('Eksport -Usun: kod {0}; {1}' -f $kod, ($script:komunikaty -join ' | '))
        Assert (@(Get-AtrapaProcesyKolektora $root).Count -eq 0) 'Eksport -Usun: kolektor dziala (wznowiono albo nie zatrzymano)'
        Assert ([IO.File]::ReadAllText($prosba).Contains(('"pid":{0}' -f $pidy[-1]))) 'Eksport -Usun: prosba nie dla biezacej instancji'
        Assert ($script:tk01.Wyrejestrowano -eq 1) ('Eksport -Usun: wyrejestrowan zadania {0}' -f $script:tk01.Wyrejestrowano)
        Assert (-not (Test-Path -LiteralPath $script:tk01.Skrot) -and -not (Test-Path -LiteralPath (Join-Path $root 'hasla.dpapi'))) 'Eksport -Usun: zostal skrot albo hasla.dpapi'
        Assert (@(Get-ChildItem -LiteralPath $root -Filter 'diagnoza-hali-kolektor-*.zip').Count -eq 3) 'Eksport -Usun: brak trzeciego ZIP-a'
        Assert (@($pidy | Select-Object -Unique).Count -eq 4) ('Instancje nie byly rozne: ' + ($pidy -join ','))
        Write-Host ('    INFO: zatrzymane instancje {0}' -f ($pidy -join ' -> '))
    } finally { Stop-AtrapaKolektora $root }
}

Check 'TK-02 [prawdziwe procesy]: obce probe.exe (inna sciezka z naszym --out, nasz EXE z innym --out, bez argumentow) nie sa zatrzymywane ani liczone' {
    if (-not $maProbe) { Skip ('brak ' + $probeExe) }
    if (-not $maCsc) { Skip 'brak csc.exe (.NET Framework 4)' }
    $root = Join-Path $KatalogRoboczy 'tk02'
    $cmd = New-AtrapaKolektora -Root $root -ProbeKatalog $ProbeKatalog -Port (Get-AtrapaWolnyPort)
    $obcyExe = New-TkExe $zrodloObcego (Join-Path $KatalogRoboczy 'tk02-obcy\probe.exe')
    $naszExe = Join-Path $root 'pakiet\probe\probe.exe'
    $outDrugi = Join-Path $root 'out-drugi'
    $obce = @()
    try {
        # F1: obcy EXE o tej samej nazwie z dokladnie naszym --out; F3: obcy bez argumentow.
        $obce += Start-Process -FilePath $obcyExe -ArgumentList ('diagnoza --tryb dlugi --out "{0}" --no-auth-prompt --zyj-ms 120000' -f (Join-Path $root 'out')) -WindowStyle Hidden -PassThru
        $obce += Start-Process -FilePath $obcyExe -ArgumentList '--zyj-ms 120000' -WindowStyle Hidden -PassThru
        # F2: nasz EXE (prawdziwy probe), drugi kolektor z innym --out.
        $argF2 = 'diagnoza --tryb dlugi --config "{0}" --out "{1}" --machines amm580 --no-auth-prompt --password SYNTHETIC_ONLY --vnc-alert-config "{2}" --no-ocr' -f (Join-Path $root 'pakiet\config.json'), $outDrugi, (Join-Path $root 'brak-vnc-alert-config.json')
        $f2 = Start-Process -FilePath $naszExe -ArgumentList $argF2 -WorkingDirectory (Join-Path $root 'pakiet') -WindowStyle Hidden -PassThru
        $obce += $f2
        $zegar = [Diagnostics.Stopwatch]::StartNew()
        while (-not (Test-Path -LiteralPath (Join-Path $outDrugi 'dlugi\podsumowanie.json')) -and $zegar.Elapsed.TotalSeconds -lt 45) { Start-Sleep -Milliseconds 250 }
        Assert (Test-Path -LiteralPath (Join-Path $outDrugi 'dlugi\podsumowanie.json')) 'Drugi kolektor (F2) nie wystartowal'
        $t0 = [DateTimeOffset]::UtcNow
        [void](Start-AtrapaKolektora $cmd)
        $start = Wait-KolektorStarted -KolektorRoot $root -StartedAfterUtc $t0 -TimeoutSeconds 45
        $nasze = @(Get-KolektorProcess -KolektorRoot $root)
        Assert ($nasze.Count -eq 1 -and $nasze[0].ProcessId -eq $start.pid) ('Get-KolektorProcess: {0} procesow ({1}); oczekiwano tylko kolektora {2}' -f $nasze.Count, (($nasze | ForEach-Object { $_.ProcessId }) -join ','), $start.pid)
        Assert (@($obce | Where-Object { $_.ProcessId -eq $start.pid }).Count -eq 0) 'Potwierdzono start obcego procesu'
        $ok = $false; $blad = ''
        try { $ok = Stop-Kolektor -KolektorRoot $root } catch { $blad = $_.Exception.Message }
        Assert ($ok -eq $true) ("Stop-Kolektor przy obcych probe.exe: '" + $blad + "'")
        Assert (@(Get-AtrapaProcesyKolektora $root | Where-Object { $_.ProcessId -eq $start.pid }).Count -eq 0) 'Nasz kolektor nadal dziala'
        foreach ($o in $obce) { Assert ($null -ne (Get-Process -Id $o.Id -ErrorAction SilentlyContinue)) ('Obcy proces {0} zostal zakonczony' -f $o.Id) }
        Assert (-not (Test-Path -LiteralPath (Join-Path $outDrugi 'dlugi\stop-request.json'))) 'Prosba o zakonczenie trafila do obcego --out'
        Assert ([IO.File]::ReadAllText((Join-Path $root 'out\dlugi\stop-request.json')).Contains(('"pid":{0}' -f $start.pid))) 'Prosba nie dla naszego kolektora'
        Assert ((Read-AtrapaTekst (Join-Path $outDrugi 'dlugi\podsumowanie.json')) -match '"zakonczony"\s*:\s*false') 'Drugi kolektor (F2) zakonczyl prace'
    } finally {
        Stop-AtrapaKolektora $root
        # F2 lagodnie (prosba z jego wlasnego podsumowania), potem wylacznie procesy uruchomione przez ten test.
        try {
            $raw = Read-AtrapaTekst (Join-Path $outDrugi 'dlugi\podsumowanie.json')
            $s = [regex]::Match($raw, '"startedUtc"\s*:\s*"([^"]+)"').Groups[1].Value
            [IO.File]::WriteAllText((Join-Path $outDrugi 'dlugi\stop-request.json'), ('{{"pid":{0},"startedUtc":"{1}"}}' -f $f2.Id, $s), $utf8)
            [void]$f2.WaitForExit(10000)
        } catch { }
        foreach ($o in $obce) { if ($null -ne $o -and -not $o.HasExited) { Microsoft.PowerShell.Management\Stop-Process -Id $o.Id -Force -ErrorAction SilentlyContinue } }
    }
}

Check 'TK-03a: po prosbie obcy nieczytelny wpis z INNYM PID, ale tym samym czasem utworzenia - nie jest nasz (odmowa, nie sukces)' {
    function Get-CimInstance { [CmdletBinding()]param([string]$ClassName, [string]$Filter) Get-TkOdczyt }
    $root = New-TkAtrapa 'tk03a' 6161
    $c = $script:tk.Created
    $script:tk.Przed = New-TkOdczyty (New-TkWpis $root 6161 $c)
    $script:tk.Po = New-TkOdczyty (New-TkWpis $root 9393 $c -BezSciezki)
    $ok = $false; $blad = ''
    try { $ok = Stop-Kolektor -KolektorRoot $root -TimeoutSeconds 5 } catch { $blad = $_.Exception.Message }
    Assert ($ok -ne $true -and $blad.StartsWith('Brak dostepu')) ("Obcy wpis z tym samym czasem utworzenia uznany za nasz: ok={0}, blad='{1}'" -f $ok, $blad)
}

Check 'TK-03b: po prosbie nasz wpis bez sciezki + obcy czytelny (nasz EXE, inny --out) - czekamy tylko na nasz, obcy bez prosby' {
    function Get-CimInstance { [CmdletBinding()]param([string]$ClassName, [string]$Filter) Get-TkOdczyt }
    $root = New-TkAtrapa 'tk03b' 6262
    $c = $script:tk.Created
    $obcy = New-TkWpis $root 9494 ([DateTime]::Now.AddMinutes(-2)) -Out (Join-Path $root 'out-drugi')
    $script:tk.Przed = New-TkOdczyty @((New-TkWpis $root 6262 $c), $obcy)
    $script:tk.Po = New-TkOdczyty @((New-TkWpis $root 6262 $c -BezSciezki), $obcy) @((New-TkWpis $root 6262 $c -BezSciezki), $obcy) $obcy
    $ok = $false; $blad = ''
    try { $ok = Stop-Kolektor -KolektorRoot $root -TimeoutSeconds 5 } catch { $blad = $_.Exception.Message }
    Assert ($ok -eq $true) ("Nie potwierdzono zakonczenia naszego kolektora przy obcym procesie: '" + $blad + "'")
    Assert ($script:tk.iPo -ge 3) ('Nie czekano na znikniecie naszego wpisu (odczytow po prosbie: {0})' -f $script:tk.iPo)
    $prosba = [IO.File]::ReadAllText((Join-Path $root 'out\dlugi\stop-request.json'))
    Assert ($prosba.Contains('"pid":6262') -and -not $prosba.Contains('9494')) ('Prosba: ' + $prosba)
}

Check 'TK-03c: nasz wpis bez sciezki nie znika - limit czasu, bez sukcesu i bez zabijania' {
    function Get-CimInstance { [CmdletBinding()]param([string]$ClassName, [string]$Filter) Get-TkOdczyt }
    function Stop-Process { throw 'Stop-Kolektor nie moze zabijac procesu.' }
    $root = New-TkAtrapa 'tk03c' 6363
    $c = $script:tk.Created
    $script:tk.Przed = New-TkOdczyty (New-TkWpis $root 6363 $c)
    $script:tk.Po = New-TkOdczyty (New-TkWpis $root 6363 $c -BezSciezki)
    $script:tk.Koniec = $false
    $zegar = [Diagnostics.Stopwatch]::StartNew()
    $ok = $false; $blad = ''
    try { $ok = Stop-Kolektor -KolektorRoot $root -TimeoutSeconds 2 } catch { $blad = $_.Exception.Message }
    Assert ($ok -ne $true -and $blad.StartsWith('Kolektor nie potwierdzil zakonczenia')) ("Oczekiwano limitu czasu: ok={0}, blad='{1}'" -f $ok, $blad)
    Assert ($zegar.Elapsed.TotalSeconds -lt 10) ('Limit 2 s trwal {0:N1} s' -f $zegar.Elapsed.TotalSeconds)
}

Check 'TK-03d: przed prosba nasz wpis chwilowo bez sciezki (2 odczyty), potem czytelny - prosba wyslana, zakonczenie potwierdzone' {
    function Get-CimInstance { [CmdletBinding()]param([string]$ClassName, [string]$Filter) Get-TkOdczyt }
    $root = New-TkAtrapa 'tk03d' 6464
    $c = $script:tk.Created
    $script:tk.Przed = New-TkOdczyty (New-TkWpis $root 6464 $c -BezSciezki) (New-TkWpis $root 6464 $c -BezSciezki) (New-TkWpis $root 6464 $c)
    $script:tk.Po = New-TkOdczyty @()
    $ok = $false; $blad = ''
    try { $ok = Stop-Kolektor -KolektorRoot $root -TimeoutSeconds 5 } catch { $blad = $_.Exception.Message }
    Assert ($ok -eq $true) ("Chwilowo nieczytelny wlasny proces przed prosba zablokowal zatrzymanie: '" + $blad + "'")
}

Check 'TK-03e: Get-KolektorProcess -Konczacy bez czasu utworzenia we wzorcu nie adoptuje nieczytelnego wpisu o tym samym PID' {
    function Get-CimInstance { [CmdletBinding()]param([string]$ClassName, [string]$Filter) Get-TkOdczyt }
    $root = New-TkAtrapa 'tk03e' 6565
    $script:tk.Przed = New-TkOdczyty (New-TkWpis $root 6565 $null -BezSciezki)
    $wzorzec = [pscustomobject]@{ ProcessId = 6565; CreationDate = $null }
    $wynik = $null; $blad = ''
    try { $wynik = @(Get-KolektorProcess -KolektorRoot $root -Konczacy $wzorzec) } catch { $blad = $_.Exception.Message }
    Assert ($null -eq $wynik -and $blad.StartsWith('Brak dostepu')) ("Wpis bez czasu utworzenia uznany za ta sama instancje: wynik={0}, blad='{1}'" -f @($wynik).Count, $blad)
}

Check 'TK-03f: po prosbie nasz konczacy sie wpis i NOWA instancja naszego kolektora - "zmienia sie instancja", bez sukcesu' {
    function Get-CimInstance { [CmdletBinding()]param([string]$ClassName, [string]$Filter) Get-TkOdczyt }
    $root = New-TkAtrapa 'tk03f' 6666
    $c = $script:tk.Created
    $script:tk.Przed = New-TkOdczyty (New-TkWpis $root 6666 $c)
    $script:tk.Po = New-TkOdczyty @((New-TkWpis $root 6666 $c -BezSciezki), (New-TkWpis $root 6767 ([DateTime]::Now)))
    $ok = $false; $blad = ''
    try { $ok = Stop-Kolektor -KolektorRoot $root -TimeoutSeconds 5 } catch { $blad = $_.Exception.Message }
    Assert ($ok -ne $true -and $blad.StartsWith('Zmienia sie instancja kolektora')) ("Oczekiwano odmowy przy nowej instancji: ok={0}, blad='{1}'" -f $ok, $blad)
}

Check 'TK-03g: Wait-KolektorStarted - obcy probe.exe chwilowo bez sciezki w kontroli 3 s nie psuje potwierdzenia; stale nieczytelny = brak potwierdzenia' {
    function Get-CimInstance { [CmdletBinding()]param([string]$ClassName, [string]$Filter) Get-TkOdczyt }
    function Start-Sleep { }
    foreach ($przypadek in @(@{ Nazwa = 'tk03g-chwilowy'; Stale = $false }, @{ Nazwa = 'tk03g-staly'; Stale = $true })) {
        $root = New-TkAtrapa $przypadek.Nazwa 6868
        $c = $script:tk.Created
        $script:tk.Started = [DateTime]::UtcNow.ToString('O')
        Write-TkPodsumowanie $root 6868 $script:tk.Started $false
        $nasz = New-TkWpis $root 6868 $c
        $obcy = [pscustomobject]@{ ProcessId = 9595; CreationDate = [DateTime]::Now; ExecutablePath = $null; CommandLine = $null }
        if ($przypadek.Stale) { $script:tk.Przed = New-TkOdczyty @($nasz, $obcy) }
        else { $script:tk.Przed = New-TkOdczyty $nasz @($nasz, $obcy) $nasz }
        $wynik = $null; $blad = ''
        try { $wynik = Wait-KolektorStarted -KolektorRoot $root -StartedAfterUtc ([DateTimeOffset]::UtcNow.AddSeconds(-10)) -TimeoutSeconds 0 } catch { $blad = $_.Exception.Message }
        if ($przypadek.Stale) { Assert ($null -eq $wynik -and $blad -ne '') 'Potwierdzono start mimo stale nieczytelnego obcego probe.exe' }
        else { Assert ($null -ne $wynik -and $wynik.pid -eq 6868) ("Chwilowo nieczytelny obcy proces zablokowal potwierdzenie: '" + $blad + "'") }
    }
}

Check 'TK-04: sekrety w wartosciach (tekst, REG_MULTI_SZ, binarne, (default), wielkosc liter) i w ksiazce adresowej - brak tresci przy kazdym $FormatEnumerationLimit' {
    $node = $ast.FindAll({ param($n) $n -is [Management.Automation.Language.TryStatementAst] -and $n.Body.Extent.Text.Contains('RealVNC\vncviewer') }, $true) | Select-Object -First 1
    Assert ($null -ne $node) 'Brak bloku zrzutu RealVNC w runnerze'
    $srod = Join-Path $KatalogRoboczy 'tk04-srodowisko'
    New-Item -ItemType Directory -Force -Path $srod | Out-Null
    $pominieto = '<pomini' + [char]0x0119 + 'to>'
    # Przecinek: bez niego funkcja rozwija byte[] w Object[], a Get-ItemProperty oddaje REG_BINARY jako System.Byte[].
    function Bajty([string]$Tekst) { return , ([Text.Encoding]::ASCII.GetBytes($Tekst)) }
    $script:tk04 = New-Object psobject
    $wpisy = [ordered]@{
        '(default)' = (Bajty 'SYNTHETIC_TK_DEFAULT_BIN'); 'AuthTokens' = [string[]]@('SYNTHETIC_TK_TOKEN_A', 'SYNTHETIC_TK_TOKEN_B')
        'PASSWORD' = 'SYNTHETIC_TK_UPPER'; 'Passwd' = 'SYNTHETIC_TK_PASSWD'; 'UserCredentials' = 'SYNTHETIC_TK_CRED'
        'X509_PRIVATE_KEY_BLOB' = (Bajty 'SYNTHETIC_TK_KEY_BIN'); 'secret_answer' = 'SYNTHETIC_TK_SECRET_LC'
        'Identity' = (Bajty 'SYNTHETIC_TK_IDENTITY_BIN'); 'PSUserKey' = 'SYNTHETIC_TK_PS_PREFIX'
        'HistoryList' = [string[]]@('127.0.0.1::5900', 'atrapa::5901'); 'ProxyServer' = 'proxy.atrapa:8080'; 'EnableAnalytics' = 0; 'Pusta' = $null
        'PSPath' = 'Microsoft.PowerShell.Core\Registry::HKEY_CURRENT_USER\SOFTWARE\RealVNC\vncviewer' }
    foreach ($nazwa in $wpisy.Keys) { $script:tk04 | Add-Member -MemberType NoteProperty -Name $nazwa -Value $wpisy[$nazwa] }
    foreach ($nazwa in '(default)', 'X509_PRIVATE_KEY_BLOB', 'Identity') { Assert ($script:tk04.$nazwa -is [byte[]]) ('Atrapa {0} nie jest byte[] jak REG_BINARY' -f $nazwa) }
    Assert ($script:tk04.AuthTokens -is [string[]] -and $script:tk04.HistoryList -is [string[]]) 'Atrapy REG_MULTI_SZ nie sa string[]'
    $appdata = Join-Path $KatalogRoboczy 'tk04-appdata'
    New-Item -ItemType Directory -Force -Path (Join-Path $appdata 'RealVNC\vncviewer') | Out-Null
    $ksiazkaWe = [string[]]@('[Connection]', 'Host=127.0.0.1::5900', 'Password=SYNTHETIC_TK_AB_PASSWORD', 'PASSWORD=SYNTHETIC_TK_AB_UPPER',
        'ProxyPasswd=SYNTHETIC_TK_AB_PASSWD', '<entry name="x" token="SYNTHETIC_TK_AB_XML"/>', 'ApiKey = SYNTHETIC_TK_AB_KEY',
        'Credential: SYNTHETIC_TK_AB_CRED', 'ClientSecret=SYNTHETIC_TK_AB_SECRET', 'Label=Atrapa')
    [IO.File]::WriteAllLines((Join-Path $appdata 'RealVNC\vncviewer\addressbook.vnc'), $ksiazkaWe, $utf8)
    function Test-Path { param([Parameter(Position = 0)]$Path)
        if ([string]$Path -eq 'HKCU:\SOFTWARE\RealVNC\vncviewer') { return $true }
        return (Microsoft.PowerShell.Management\Test-Path -LiteralPath $Path) }
    function Get-ItemProperty { param([Parameter(Position = 0)]$Path) return $script:tk04 }
    $script:tk04log = New-Object 'System.Collections.Generic.List[string]'
    function Zapisz-Log([string]$Tekst) { $script:tk04log.Add($Tekst) }
    $plik = Join-Path $srod 'realvnc-viewer-ustawienia.txt'
    $plikAb = Join-Path $srod 'realvnc-addressbook-bez-hasel.txt'
    $limit = $global:FormatEnumerationLimit; $poprzednieAppData = $env:APPDATA
    $zrzuty = @{}
    try {
        $env:APPDATA = $appdata
        foreach ($l in 0, 1, 4, 16, -1) {
            $global:FormatEnumerationLimit = $l
            & ([scriptblock]::Create($node.Extent.Text))
            $zrzuty[$l] = [IO.File]::ReadAllText($plik)
        }
        $ksiazka = [IO.File]::ReadAllText($plikAb)
    } finally { $global:FormatEnumerationLimit = $limit; $env:APPDATA = $poprzednieAppData }
    Assert ($script:tk04log.Count -eq 0) ('Blok RealVNC zglosil blad: ' + ($script:tk04log -join ' | '))
    foreach ($l in 1, 4, 16, -1) { Assert ($zrzuty[$l] -ceq $zrzuty[0]) ('Zrzut zalezy od $FormatEnumerationLimit (0 vs {0})' -f $l) }
    $zrzut = $zrzuty[0]
    foreach ($tekst in @($zrzut, $ksiazka)) {
        $linia = ($tekst -split "`r?`n") | Where-Object { $_.Contains('SYNTHETIC') } | Select-Object -First 1
        Assert ($null -eq $linia) ('Tresc sekretu w wyniku: ' + $linia)
    }
    # Bajty znacznikow (np. "83, 89, 78" = "SYN") nie moga trafic do wyniku w zadnej postaci.
    Assert (-not ($zrzut -match '\b83,\s*89,\s*78\b') -and -not $zrzut.Contains('{')) 'Bajty wartosci binarnej w zrzucie'
    foreach ($nazwa in 'AuthTokens', 'PASSWORD', 'Passwd', 'UserCredentials', 'X509_PRIVATE_KEY_BLOB', 'secret_answer') {
        Assert ($zrzut -match ('(?m)^{0}\s*:\s*{1}\s*$' -f [regex]::Escape($nazwa), [regex]::Escape($pominieto))) ('Brak znacznika dla ' + $nazwa)
    }
    Assert ($zrzut -match '(?m)^\(default\)\s*:\s*<dane binarne: 24 B>\s*$' -and $zrzut -match '(?m)^Identity\s*:\s*<dane binarne: 25 B>\s*$') 'Binarne wartosci bez sekretnej nazwy nie sa sprowadzone do rozmiaru'
    Assert ($zrzut -match '(?m)^HistoryList\s*:\s*127\.0\.0\.1::5900; atrapa::5901\s*$' -and $zrzut -match '(?m)^ProxyServer\s*:\s*proxy\.atrapa:8080\s*$' -and
        $zrzut -match '(?m)^EnableAnalytics\s*:\s*0\s*$' -and $zrzut -match '(?m)^Pusta\s*:') 'Zgubiono jawne ustawienia'
    Assert (-not $zrzut.Contains('PSPath') -and -not $zrzut.Contains('PSUserKey')) 'Pola PS* w zrzucie'
    $linie = @($ksiazka -split "`r?`n" | Where-Object { $_ -ne '' })
    Assert ($linie.Count -eq $ksiazkaWe.Count) ('Ksiazka: {0} wierszy zamiast {1}' -f $linie.Count, $ksiazkaWe.Count)
    Assert (@($linie | Where-Object { $_ -ceq $pominieto }).Count -eq 7 -and $linie -contains 'Host=127.0.0.1::5900' -and $linie -contains 'Label=Atrapa' -and $linie -contains '[Connection]') ('Ksiazka adresowa: ' + ($linie -join ' | '))
}

# === 5. Zmienna hasla: usuwana (nie pusta) w PS 5.1 i pwsh 7; dziecko po kroku nie dziedziczy hasla ===================
Check 'TK-05a: [NullString]::Value (wprost i w $(if ...)) usuwa zmienna procesu - dziecko widzi brak, nie pusta' {
    if (-not $maCsc) { Skip 'brak csc.exe (.NET Framework 4)' }
    $raport = New-TkExe $zrodloRaportu (Join-Path $KatalogRoboczy 'tk05a\raport-zmiennej.exe')
    $plik = Join-Path (Split-Path -Parent $raport) 'dziecko.txt'
    $zapisane = [Environment]::GetEnvironmentVariable($zmienna, 'Process')
    $stany = New-Object 'System.Collections.Generic.List[string]'
    try {
        foreach ($sposob in 'wprost', 'podwyrazenie', 'zmienna-null') {
            [Environment]::SetEnvironmentVariable($zmienna, 'SYNTHETIC_TK_ENV', 'Process')
            $x = $null
            switch ($sposob) {
                'wprost' { [Environment]::SetEnvironmentVariable($zmienna, [NullString]::Value, 'Process') }
                'podwyrazenie' { [Environment]::SetEnvironmentVariable($zmienna, $(if ($null -eq $x) { [NullString]::Value } else { $x }), 'Process') }
                'zmienna-null' { [Environment]::SetEnvironmentVariable($zmienna, $x, 'Process') }
            }
            if (Test-Path -LiteralPath $plik) { Remove-Item -LiteralPath $plik -Force }
            & $raport
            $stan = ([IO.File]::ReadAllText($plik)).Trim()
            $stany.Add(('{0}={1}/{2}' -f $sposob, $stan, $(if ($null -eq [Environment]::GetEnvironmentVariable($zmienna, 'Process')) { 'null' } else { "''" })))
        }
    } finally { [Environment]::SetEnvironmentVariable($zmienna, $(if ($null -eq $zapisane) { [NullString]::Value } else { $zapisane }), 'Process') }
    Write-Host ('    INFO: PowerShell {0} (.NET {1}): {2}' -f $PSVersionTable.PSVersion, [Environment]::Version, ($stany -join ', '))
    Assert ($stany[0] -ceq 'wprost=ABSENT/null' -and $stany[1] -ceq 'podwyrazenie=ABSENT/null') ('[NullString]::Value nie usunal zmiennej: ' + ($stany -join ', '))
}

Check 'TK-05b: bloki hasla runnera (tryb dlugi, krok 3, krok 5) - dziecko w trakcie dostaje haslo albo brak; po bloku stan sprzed (brak = brak), takze dla Start-Process' {
    if (-not $maCsc) { Skip 'brak csc.exe (.NET Framework 4)' }
    $raport = New-TkExe $zrodloRaportu (Join-Path $KatalogRoboczy 'tk05b\raport-zmiennej.exe')
    $plik = Join-Path (Split-Path -Parent $raport) 'dziecko.txt'
    $blokDlugi = $ast.FindAll({ param($n) $n -is [Management.Automation.Language.IfStatementAst] -and $n.Clauses[0].Item1.Extent.Text -eq '$h' }, $true) | Select-Object -First 1
    $krok3 = ($ast.FindAll({ param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Krok' }, $true) |
        Where-Object { $_.CommandElements[1].Value -like '3.*' } | Select-Object -First 1).CommandElements[2].ScriptBlock
    $blok5 = $ast.FindAll({ param($n) $n -is [Management.Automation.Language.IfStatementAst] -and $n.Clauses[0].Item1.Extent.Text -eq '$haslo' -and
        $n.Clauses[0].Item2.Extent.Text.Contains('haslo zapisz amm580 --z-env') }, $true) | Select-Object -First 1
    Assert ($null -ne $blokDlugi -and $null -ne $krok3 -and $null -ne $blok5) 'Brak blokow hasla w runnerze'
    function Zapisz-Log([string]$Tekst) { }
    function Uruchom-Polecenie { }
    function Znajdz-Probe { return $raport }
    $zapisane = [Environment]::GetEnvironmentVariable($zmienna, 'Process')
    $haslo = 'SYNTHETIC_TK_Haslo-ż'
    $bledy = New-Object 'System.Collections.Generic.List[string]'
    try {
        foreach ($przed in @($null, 'SYNTHETIC_TK_STARE')) {
            $oczekPo = $(if ($null -eq $przed) { 'ABSENT' } else { 'SET:' + $przed.Length })
            foreach ($wariant in 'dlugi', 'krok3-z-haslem', 'krok3-bez-hasla', 'krok5') {
                [Environment]::SetEnvironmentVariable($zmienna, $(if ($null -eq $przed) { [NullString]::Value } else { $przed }), 'Process')
                if (Test-Path -LiteralPath $plik) { Remove-Item -LiteralPath $plik -Force }
                $script:KolektorRoot = Join-Path $KatalogRoboczy 'tk05b-kolektor'; $script:OutSzybki = Join-Path $KatalogRoboczy 'tk05b-szybki'
                $probe = $raport; $Minuty = 1; $config = Join-Path $KatalogRoboczy 'brak-config.json'; $script:MaszynyPrzebiegu = 'amm580'; $srod = $script:OutSzybki
                switch ($wariant) {
                    'dlugi' { $h = $haslo; . ([scriptblock]::Create($blokDlugi.Extent.Text)); $oczekW = 'SET:' + $haslo.Length }
                    'krok3-z-haslem' { & $krok3.GetScriptBlock(); $oczekW = 'SET:' + $haslo.Length }
                    'krok3-bez-hasla' { $hasloZapas = $haslo; $haslo = $null; & $krok3.GetScriptBlock(); $haslo = $hasloZapas; $oczekW = 'ABSENT' }
                    'krok5' { . ([scriptblock]::Create($blok5.Extent.Text)); $oczekW = 'SET:' + $haslo.Length }
                }
                $w = ([IO.File]::ReadAllText($plik)).Trim()
                # Dziecko po bloku - jak kolektor uruchamiany przez Start-Process (sciezka zapasowa) i przez &.
                Remove-Item -LiteralPath $plik -Force
                Start-Process -FilePath $raport -WindowStyle Hidden -Wait
                & $raport
                $po = @(([IO.File]::ReadAllLines($plik)) | Where-Object { $_ })
                $rodzic = [Environment]::GetEnvironmentVariable($zmienna, 'Process')
                $opis = '{0}/przed={1}: w trakcie {2} (oczek. {3}), po: {4} (oczek. {5}), rodzic {6}' -f $wariant, $(if ($null -eq $przed) { 'brak' } else { 'stare' }), $w, $oczekW, ($po -join ','), $oczekPo, $(if ($null -eq $rodzic) { 'null' } elseif ($rodzic -ceq $przed) { 'stare' } else { "'" + $rodzic.Length + "'" })
                if ($w -cne $oczekW -or $po.Count -ne 2 -or $po[0] -cne $oczekPo -or $po[1] -cne $oczekPo -or $rodzic -cne $przed) { $bledy.Add($opis) }
            }
        }
    } finally { [Environment]::SetEnvironmentVariable($zmienna, $(if ($null -eq $zapisane) { [NullString]::Value } else { $zapisane }), 'Process') }
    Assert ($bledy.Count -eq 0) ('PowerShell {0}: ' -f $PSVersionTable.PSVersion + ($bledy -join ' | '))
}

# === 6. pwsh 7: odczyt podsumowania w swiezym procesie (bez wczesniej zaladowanych bibliotek) ==========================
Check 'TK-06: Read-KolektorSummary w swiezym procesie tego samego PowerShell oddaje startedUtc jako dokladny tekst' {
    $root = Join-Path $KatalogRoboczy 'tk06'
    $started = '2026-10-12T01:13:45.4303688Z'
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'out\dlugi') | Out-Null
    [IO.File]::WriteAllText((Join-Path $root 'out\dlugi\podsumowanie.json'), ('{{"tryb":"dlugi","pid":1,"startedUtc":"{0}","heartbeatUtc":"{0}","zakonczony":false}}' -f $started), $utf8)
    $skrypt = Join-Path $root 'odczyt.ps1'
    [IO.File]::WriteAllText($skrypt, ('. ''{0}''; $s = Read-KolektorSummary ''{1}''; [Console]::Out.Write($s.startedUtc.GetType().Name + ''|'' + $s.startedUtc)' -f (Join-Path $PSScriptRoot 'Kolektor-Common.ps1'), $root), (New-Object Text.UTF8Encoding($true)))
    $wynik = (& $hostExe -NoProfile -ExecutionPolicy Bypass -File $skrypt 2>&1 | Out-String).Trim()
    Assert ($wynik -ceq ('String|' + $started)) ("Swiezy proces ({0}): '{1}'" -f $hostExe, $wynik)
}

Write-Host ('WYNIK: {0} PASS, {1} FAIL, {2} SKIP; PowerShell {3}; tylko 127.0.0.1 i katalog testu. Pliki: {4}' -f $script:passed, $script:failed, $script:skipped, $PSVersionTable.PSVersion, $KatalogRoboczy)
exit $script:failed
