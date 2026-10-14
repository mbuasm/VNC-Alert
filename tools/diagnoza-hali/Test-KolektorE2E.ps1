param(
    [string]$KatalogRoboczy = (Join-Path $PSScriptRoot ('..\..\artifacts\test-kolektor-e2e-' + [guid]::NewGuid().ToString('N'))),
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
. (Join-Path $PSScriptRoot 'Kolektor-Common.ps1')
. (Join-Path $PSScriptRoot 'Kolektor-Start.ps1')
. (Join-Path $PSScriptRoot 'Test-KolektorAtrapy.ps1')
$probeExe = (Join-Path $ProbeKatalog 'probe.exe')
$maProbe = Test-Path -LiteralPath $probeExe
$maDpapi = Test-AtrapaDpapi
$zmienna = 'VNCALERT_DIAGNOZA_PASSWORD_580'

function Invoke-ProbeHaslo([string[]]$Argumenty, [string]$Katalog, [string]$Wejscie = '') {
    # Prawdziwy probe.exe; wyjscie i bledy do plikow (sprawdzane potem pod katem hasla).
    $stdout = Join-Path $Katalog ('stdout-' + [guid]::NewGuid().ToString('N') + '.txt')
    $stderr = Join-Path $Katalog ('stderr-' + [guid]::NewGuid().ToString('N') + '.txt')
    $parametry = @{ FilePath = $probeExe; ArgumentList = $Argumenty; RedirectStandardOutput = $stdout; RedirectStandardError = $stderr; NoNewWindow = $true; Wait = $true; PassThru = $true }
    if ($Wejscie) { $parametry.RedirectStandardInput = $Wejscie }
    $p = Start-Process @parametry
    return [pscustomobject]@{ Kod = $p.ExitCode; Stdout = [IO.File]::ReadAllText($stdout); Stderr = [IO.File]::ReadAllText($stderr); Pliki = @($stdout, $stderr) }
}

Check 'E2E: odmowa hasla w fazie 0 (atrapa RFB 127.0.0.1) - kolektor konczy sie po pierwszym podsumowaniu, start NIE jest potwierdzony' {
    if (-not $maProbe) { Skip ('brak ' + $probeExe) }
    $atrapa = Start-AtrapaVncOdmowa
    $root = Join-Path $KatalogRoboczy 'odmowa-faza0'
    try {
        $cmd = New-AtrapaKolektora -Root $root -ProbeKatalog $ProbeKatalog -Port $atrapa.Port
        $t0 = [DateTimeOffset]::UtcNow
        [void](Start-AtrapaKolektora $cmd)
        $zegar = [Diagnostics.Stopwatch]::StartNew()
        $blad = $null
        try { [void](Wait-KolektorStarted -KolektorRoot $root -StartedAfterUtc $t0 -TimeoutSeconds 20) } catch { $blad = $_.Exception.Message }
        Assert ($null -ne $blad) 'Potwierdzono start kolektora, ktory odpadl w fazie 0 (odmowa hasla)'
        $raw = Read-AtrapaTekst (Join-Path $root 'out\dlugi\podsumowanie.json')
        Assert ($raw -match '"zakonczony"\s*:\s*true') 'Brak koncowego podsumowania kolektora'
        Assert ($raw.Contains('odrzucił podane hasło VNC')) 'Brak przyczyny (odmowa hasla) w podsumowaniu'
        Assert ($atrapa.State.Odmowy -ge 1) 'Atrapa nie odrzucila hasla - scenariusz nie zostal odtworzony'
        Assert (@(Get-AtrapaProcesyKolektora $root).Count -eq 0) 'Kolektor po odmowie nadal dziala'
        Write-Host ('    INFO: odmowy atrapy {0}, polaczen {1}; Wait-KolektorStarted po {2:N1} s: {3}' -f $atrapa.State.Odmowy, $atrapa.State.Polaczenia, $zegar.Elapsed.TotalSeconds, $blad)
    } finally { Stop-AtrapaKolektora $root; Stop-AtrapaVnc $atrapa }
}

Check 'E2E: kolektor pracujacy (port zamkniety 127.0.0.1) - start potwierdzony dopiero po >= 3 s zywego procesu' {
    if (-not $maProbe) { Skip ('brak ' + $probeExe) }
    $root = Join-Path $KatalogRoboczy 'praca'
    try {
        $cmd = New-AtrapaKolektora -Root $root -ProbeKatalog $ProbeKatalog -Port (Get-AtrapaWolnyPort)
        $t0 = [DateTimeOffset]::UtcNow
        [void](Start-AtrapaKolektora $cmd)
        $pid0 = Wait-AtrapaKolektora $root
        $zegar = [Diagnostics.Stopwatch]::StartNew()
        $dowod = Wait-KolektorStarted -KolektorRoot $root -StartedAfterUtc $t0 -TimeoutSeconds 45
        $sekundy = $zegar.Elapsed.TotalSeconds
        Assert ($dowod.pid -eq $pid0) 'Potwierdzono inny proces niz kolektor tego testu'
        Assert ($sekundy -ge 3) ('Potwierdzono po {0:N1} s - bez 3 s kontroli zycia procesu' -f $sekundy)
        Assert (@(Get-AtrapaProcesyKolektora $root | Where-Object { $_.ProcessId -eq $dowod.pid }).Count -eq 1) 'Potwierdzony proces juz nie dziala'
        Write-Host ('    INFO: potwierdzenie {0:N1} s po pierwszym swiezym podsumowaniu (PID {1})' -f $sekundy, $dowod.pid)
    } finally { Stop-AtrapaKolektora $root }
}

Check 'E2E: proces-atrapa kolektora - zyje 30 ms albo 1,5 s po pierwszym podsumowaniu: brak potwierdzenia; 6 s: potwierdzenie po >= 3 s' {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path -LiteralPath $csc)) { Skip 'brak csc.exe (.NET Framework 4)' }
    $zrodlo = Join-Path $KatalogRoboczy 'atrapa-kolektora.cs'
    @'
using System; using System.Diagnostics; using System.IO; using System.Text; using System.Threading;
public static class AtrapaKolektora {
    public static int Main(string[] args) {
        string outDir = null; int zyjMs = 0;
        for (int i = 0; i + 1 < args.Length; i++) { if (args[i] == "--out") outDir = args[i + 1]; if (args[i] == "--zyj-ms") zyjMs = int.Parse(args[i + 1]); }
        string dir = Path.Combine(outDir, "dlugi"); Directory.CreateDirectory(dir);
        string started = DateTime.UtcNow.ToString("O"); int pid = Process.GetCurrentProcess().Id;
        Zapisz(dir, pid, started, false); Thread.Sleep(zyjMs); Zapisz(dir, pid, started, true);
        return 4;
    }
    static void Zapisz(string dir, int pid, string started, bool koniec) {
        string json = "{\"tryb\":\"dlugi\",\"pid\":" + pid + ",\"startedUtc\":\"" + started + "\",\"heartbeatUtc\":\"" + DateTime.UtcNow.ToString("O") + "\",\"zakonczony\":" + (koniec ? "true" : "false") + "}";
        string tmp = Path.Combine(dir, "podsumowanie.json.tmp"); string path = Path.Combine(dir, "podsumowanie.json");
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(tmp, path, null, true); else File.Move(tmp, path);
    }
}
'@ | Set-Content -LiteralPath $zrodlo -Encoding ascii
    foreach ($przypadek in @(@{ Ms = 30; Potwierdzic = $false }, @{ Ms = 1500; Potwierdzic = $false }, @{ Ms = 6000; Potwierdzic = $true })) {
        $root = Join-Path $KatalogRoboczy ('atrapa-procesu-' + $przypadek.Ms + 'ms')
        New-Item -ItemType Directory -Force -Path (Join-Path $root 'pakiet\probe'), (Join-Path $root 'out') | Out-Null
        $exe = Join-Path $root 'pakiet\probe\probe.exe'
        $kompilacja = & $csc /nologo ('/out:' + $exe) $zrodlo 2>&1 | Out-String
        Assert (Test-Path -LiteralPath $exe) ('Nie skompilowano atrapy: ' + $kompilacja)
        $t0 = [DateTimeOffset]::UtcNow
        $proces = Start-Process -FilePath $exe -ArgumentList ('diagnoza --tryb dlugi --out "{0}" --zyj-ms {1}' -f (Join-Path $root 'out'), $przypadek.Ms) -WindowStyle Hidden -PassThru
        $zegar = [Diagnostics.Stopwatch]::StartNew()
        $dowod = $null; $blad = $null
        try { $dowod = Wait-KolektorStarted -KolektorRoot $root -StartedAfterUtc $t0 -TimeoutSeconds 10 } catch { $blad = $_.Exception.Message }
        $sekundy = $zegar.Elapsed.TotalSeconds
        [void]$proces.WaitForExit(15000)
        if ($przypadek.Potwierdzic) {
            Assert ($null -ne $dowod -and $sekundy -ge 3) ('Proces zyjacy {0} ms: brak potwierdzenia albo potwierdzenie po {1:N1} s ({2})' -f $przypadek.Ms, $sekundy, $blad)
        } else {
            Assert ($null -eq $dowod) ('Potwierdzono start procesu, ktory zyl tylko {0} ms po pierwszym podsumowaniu' -f $przypadek.Ms)
        }
        Write-Host ('    INFO: zycie {0} ms -> {1} po {2:N1} s' -f $przypadek.Ms, $(if ($dowod) { 'potwierdzono PID ' + $dowod.pid } else { $blad }), $sekundy)
    }
}

Check 'E2E: probe haslo zapisz amm580 --z-env - haslo bez zmian, poza konsola, raportem i plikiem' {
    if (-not $maProbe) { Skip ('brak ' + $probeExe) }
    if (-not $maDpapi) { Skip 'DPAPI niedostepne w tej sesji' }
    $dir = Join-Path $KatalogRoboczy 'haslo-z-env'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $plik = Join-Path $dir 'hasla.dpapi'
    $haslo = 'SYNTHETIC_Zażółć gęślą "$`%PATH%^&!1 ł'
    $poprzednie = [Environment]::GetEnvironmentVariable($zmienna, 'Process')
    try {
        [Environment]::SetEnvironmentVariable($zmienna, $haslo, 'Process')
        $w = Invoke-ProbeHaslo @('haslo', 'zapisz', 'amm580', '--z-env', '--plik', $plik, '--out', (Join-Path $dir 'out')) $dir
    } finally { [Environment]::SetEnvironmentVariable($zmienna, $(if ($null -eq $poprzednie) { [NullString]::Value } else { $poprzednie }), 'Process') }
    Assert ($w.Kod -eq 0) ('Kod wyjscia ' + $w.Kod)
    $odczyt = Read-AtrapaHasloDpapi $plik 'amm580'
    Assert ($odczyt -ceq $haslo) ('Haslo zmienione w drodze do DPAPI (dlugosc {0} zamiast {1})' -f ([string]$odczyt).Length, $haslo.Length)
    foreach ($f in @($w.Pliki + (Join-Path $dir 'out\raport.txt') + $plik)) {
        $tekst = [IO.File]::ReadAllText($f)
        Assert (-not $tekst.Contains($haslo) -and -not $tekst.Contains('SYNTHETIC_Za') -and -not $tekst.Contains('gęślą')) ('Haslo (lub jego czesc) w ' + $f)
    }
}

Check 'E2E: probe haslo zapisz --z-env bez zmiennej i dla innej maszyny - kod 1, nic nie zapisano' {
    if (-not $maProbe) { Skip ('brak ' + $probeExe) }
    if (-not $maDpapi) { Skip 'DPAPI niedostepne w tej sesji' }
    $dir = Join-Path $KatalogRoboczy 'haslo-z-env-bledy'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $plik = Join-Path $dir 'hasla.dpapi'
    $poprzednie = [Environment]::GetEnvironmentVariable($zmienna, 'Process')
    try {
        [Environment]::SetEnvironmentVariable($zmienna, [NullString]::Value, 'Process')
        $bez = Invoke-ProbeHaslo @('haslo', 'zapisz', 'amm580', '--z-env', '--plik', $plik, '--out', (Join-Path $dir 'out')) $dir
        [Environment]::SetEnvironmentVariable($zmienna, 'SYNTHETIC_INNA', 'Process')
        $inna = Invoke-ProbeHaslo @('haslo', 'zapisz', 'premile', '--z-env', '--plik', $plik, '--out', (Join-Path $dir 'out')) $dir
    } finally { [Environment]::SetEnvironmentVariable($zmienna, $(if ($null -eq $poprzednie) { [NullString]::Value } else { $poprzednie }), 'Process') }
    Assert ($bez.Kod -eq 1 -and $bez.Stdout.Contains('Puste hasło')) ('Bez zmiennej: kod {0}' -f $bez.Kod)
    Assert ($inna.Kod -eq 1 -and $inna.Stderr.Contains('--z-env dotyczy wyłącznie hasła 580')) ('Inna maszyna: kod {0}' -f $inna.Kod)
    Assert (-not (Test-Path -LiteralPath $plik)) 'Zapisano plik hasel mimo bledu'
    Assert (-not $inna.Stdout.Contains('SYNTHETIC_INNA') -and -not $inna.Stderr.Contains('SYNTHETIC_INNA')) 'Wartosc zmiennej na konsoli'
}

Check 'E2E: haslo z przekierowanego wejscia (UTF-8 z BOM i bez BOM, potok PowerShell z BOM) - zapis bez znacznika BOM' {
    if (-not $maProbe) { Skip ('brak ' + $probeExe) }
    if (-not $maDpapi) { Skip 'DPAPI niedostepne w tej sesji' }
    $dir = Join-Path $KatalogRoboczy 'haslo-wejscie'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $haslo = 'SYNTHETIC_Wejście-ł'
    foreach ($bom in @($true, $false)) {
        $plik = Join-Path $dir ('hasla-bom-' + $bom + '.dpapi')
        $wejscie = Join-Path $dir ('wejscie-bom-' + $bom + '.txt')
        $bajty = [Text.Encoding]::UTF8.GetBytes($haslo + "`r`n")
        if ($bom) { $bajty = [byte[]](239, 187, 191) + $bajty }
        [IO.File]::WriteAllBytes($wejscie, $bajty)
        $w = Invoke-ProbeHaslo @('haslo', 'zapisz', 'amm580', '--plik', $plik, '--out', (Join-Path $dir 'out')) $dir $wejscie
        Assert ($w.Kod -eq 0) ('Kod wyjscia {0} (BOM={1})' -f $w.Kod, $bom)
        $odczyt = Read-AtrapaHasloDpapi $plik 'amm580'
        Assert ($odczyt -ceq $haslo) ('Haslo z wejscia zmienione (BOM={0}): dlugosc {1} zamiast {2}' -f $bom, ([string]$odczyt).Length, $haslo.Length)
    }
    # Potok PowerShell z $OutputEncoding UTF-8 z BOM (PS 5.1 dokleja wtedy BOM) - BOM nie moze trafic do hasla.
    $plik = Join-Path $dir 'hasla-potok.dpapi'
    $kodowanie = $global:OutputEncoding
    try {
        $global:OutputEncoding = New-Object Text.UTF8Encoding($true)
        $null = ($haslo | & $probeExe haslo zapisz amm580 --plik $plik --out (Join-Path $dir 'out') 2>&1 | Out-String)
    } finally { $global:OutputEncoding = $kodowanie }
    $odczyt = Read-AtrapaHasloDpapi $plik 'amm580'
    Assert ($odczyt -ceq $haslo) ('Potok PowerShell UTF-8 z BOM zmienil haslo: dlugosc {0} zamiast {1}' -f ([string]$odczyt).Length, $haslo.Length)
}

Write-Host ('WYNIK: {0} PASS, {1} FAIL, {2} SKIP; PowerShell {3}; prawdziwy probe.exe, tylko 127.0.0.1 i katalog testu. Pliki: {4}' -f $script:passed, $script:failed, $script:skipped, $PSVersionTable.PSVersion, $KatalogRoboczy)
exit $script:failed
