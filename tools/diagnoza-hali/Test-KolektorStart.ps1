param([string]$KatalogRoboczy = (Join-Path $PSScriptRoot ('..\..\artifacts\test-kolektor-start-' + [guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 5) { throw 'Uruchom powershell.exe (Windows PowerShell 5.1).' }
New-Item -ItemType Directory -Path $KatalogRoboczy -Force | Out-Null
$KatalogRoboczy = (Resolve-Path -LiteralPath $KatalogRoboczy).Path
$script:passed = 0
function Assert([bool]$Warunek, [string]$Opis) { if (-not $Warunek) { throw $Opis } }
function Check([string]$Nazwa, [scriptblock]$Test) { & $Test; $script:passed++; Write-Host ('PASS: ' + $Nazwa) }
function Must-Fail([scriptblock]$Test, [string]$Opis) { $failed=$false; try { & $Test | Out-Null } catch { $failed=$true }; Assert $failed $Opis }
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Diagnoza-Hali.ps1'), [ref]$tokens, [ref]$errors)
Assert ($errors.Count -eq 0) 'Niepoprawna skladnia runnera'
$f=$ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst]},$false) | Where-Object Name -eq 'Zarejestruj-Kolektor'
. ([scriptblock]::Create($f.Extent.Text))
$script:logs=New-Object 'System.Collections.Generic.List[string]'
$script:events=New-Object 'System.Collections.Generic.List[string]'
function Zapisz-Log([string]$Tekst) { $script:logs.Add($Tekst) }
function Znajdz-Probe { return (Join-Path $script:Root 'probe\probe.exe') }
function Import-Module { }
function Get-Process { return @() }
function Stop-Process { throw 'Test zabrania zatrzymania prawdziwych procesow' }
function Stop-ScheduledTask { throw 'Test zabrania twardego zatrzymania zadania' }
function Start-Sleep { }
function Get-ScheduledTask { if ($script:registered) { return [pscustomobject]@{TaskName='diagnoza-hali-kolektor'; State=$script:taskState} } }
function New-ScheduledTaskAction { param($Execute,$Argument,$WorkingDirectory) $script:taskArgument=$Argument; return 'action' }
function New-ScheduledTaskTrigger { return 'trigger' }
function New-ScheduledTaskSettingsSet { return 'settings' }
function Register-ScheduledTask {
    if ($script:registrationFails) { throw 'Syntetyczna odmowa rejestracji' }
    $script:registered=$true; $script:events.Add('register')
    return [pscustomobject]@{TaskName='diagnoza-hali-kolektor'; State='Ready'}
}
function Start-ScheduledTask { $script:events.Add('task-start') }
function Start-Process { param($FilePath,$ArgumentList,$WorkingDirectory,$WindowStyle,[switch]$PassThru) $script:events.Add('fallback-start'); $script:fallbackWindow=$WindowStyle }
function New-TestContext([string]$Name) {
    $script:Root=Join-Path $KatalogRoboczy ($Name + '\pakiet źródłowy żółć')
    $script:KolektorRoot=Join-Path $KatalogRoboczy ($Name + '\Użytkownik żółć\diagnoza-hali')
    $script:OutSzybki=Join-Path $script:Root 'out-PC2'
    $script:ZadanieNazwa='diagnoza-hali-kolektor'; $script:Maszyny='premile,cameo1'
    $script:registered=$false; $script:taskState='Running'; $script:registrationFails=$false
    $script:proofFails=$false; $script:stopFails=$false; $script:BladKolektora=$false
    $script:logs.Clear(); $script:events.Clear()
    New-Item -ItemType Directory -Force -Path (Join-Path $script:Root 'probe'), (Join-Path $script:OutSzybki 'out') | Out-Null
    'SYNTHETIC_NON_EXECUTABLE' | Set-Content -LiteralPath (Znajdz-Probe)
    '{"machines":[]}' | Set-Content -LiteralPath (Join-Path $script:Root 'config.json') -Encoding UTF8
    '{"premile":{"tryb":"odpytywanie","powod":"żółć test"}}' | Set-Content -LiteralPath (Join-Path $script:OutSzybki 'out\tryb-maszyn.json') -Encoding UTF8
    @'
function Get-KolektorTask { return $null }
function Stop-Kolektor { if ($script:stopFails) { throw 'Syntetyczny timeout zamkniecia' }; $script:events.Add('stop-own'); return $true }
'@ | Set-Content -LiteralPath (Join-Path $script:Root 'Kolektor-Common.ps1') -Encoding UTF8
    @'
function Get-KolektorStartupShortcut { return $script:oldShortcut }
function Wait-KolektorStarted { if ($script:proofFails) { throw 'Syntetyczny brak potwierdzenia procesu' }; $script:events.Add('proof'); return [pscustomobject]@{pid=12345;zakonczony=$false} }
'@ | Set-Content -LiteralPath (Join-Path $script:Root 'Kolektor-Start.ps1') -Encoding UTF8
    $script:oldShortcut=$null
}

Check 'W5: CMD nie zapisuje polskiej sciezki w ASCII; decyzje z out-PC/out sa zachowane' {
    New-TestContext 'unicode'
    $HasloDoKolektora=$true
    Zarejestruj-Kolektor 'SYNTHETIC_SECRET_żółć['
    $cmdPath=Join-Path $script:KolektorRoot 'pakiet\Kolektor.cmd'
    $cmd=Get-Content -LiteralPath $cmdPath -Encoding ascii -Raw
    Assert ($cmd.Contains('%~dp0probe\probe.exe')) 'CMD utrwala bezwzgledna sciezke zamiast rozwijac ja podczas startu'
    Assert ($cmd.Contains('--out "%~dp0..\out"')) 'Out nie jest rozwijane jako pelna sciezka Unicode'
    Assert (-not $cmd.Contains('SYNTHETIC_SECRET')) 'Haslo w CMD'
    Assert (-not $script:taskArgument.Contains('SYNTHETIC_SECRET')) 'Haslo w poleceniu zadania'
    $mode=Get-Content -LiteralPath (Join-Path $script:KolektorRoot 'out\tryb-maszyn.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert ($mode.premile.powod -ceq 'żółć test') 'Nie skopiowano decyzji trybu z wlasciwego out'
    Assert (-not $script:BladKolektora) 'Potwierdzony start nie zostal uznany'
}
Check 'W5: prawdziwy CMD w sciezce Unicode przekazuje argumenty do atrapy EXE bez strat' {
    New-TestContext 'cmd-real'
    Zarejestruj-Kolektor $null
    $package=Join-Path $script:KolektorRoot 'pakiet'
    $stubExe=Join-Path $package 'probe\probe.exe'
    Remove-Item -LiteralPath $stubExe -Force
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;
public static class SyntheticCollectorArguments {
    public static int Main(string[] args) {
        File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "received-args.txt"), args, new UTF8Encoding(false));
        return 17;
    }
}
'@ -Language CSharp -OutputAssembly $stubExe -OutputType ConsoleApplication
    $psi=New-Object Diagnostics.ProcessStartInfo
    $psi.FileName=$env:ComSpec
    $psi.Arguments='/d /c ""' + (Join-Path $package 'Kolektor.cmd') + '""'
    $psi.UseShellExecute=$false; $psi.CreateNoWindow=$true
    $child=[Diagnostics.Process]::Start($psi)
    try {
        Assert ($child.WaitForExit(15000)) 'Atrapa CMD nie zakonczyla sie w 15s'
        Assert ($child.ExitCode -eq 17) 'CMD zgubil kod wyjscia EXE'
    } finally { $child.Dispose() }
    $received=@(Get-Content -LiteralPath (Join-Path $package 'probe\received-args.txt') -Encoding UTF8)
    $configIndex=[Array]::IndexOf($received,'--config')
    $outIndex=[Array]::IndexOf($received,'--out')
    Assert ($configIndex -gt 0 -and $outIndex -gt 0) 'Utracone opcje'
    Assert ($received[$configIndex+1] -ceq (Join-Path $package 'config.json')) 'CMD uszkodzil Unicode w --config'
    Assert ([IO.Path]::GetFullPath($received[$outIndex+1]) -ceq (Join-Path $script:KolektorRoot 'out')) 'CMD uszkodzil Unicode w --out'
    Assert ($received -contains 'premile,cameo1') 'CMD zgubil wybor PC2'
}
Check 'W8: Ready i brak procesu nie daja sukcesu ani drugiego startu przez Autostart' {
    New-TestContext 'ready'; $script:taskState='Ready'; $script:proofFails=$true
    Must-Fail { Zarejestruj-Kolektor $null } 'Brak procesu nie byl bledem'
    Assert $script:BladKolektora 'Brak kodu niepowodzenia kolektora'
    Assert (-not ($script:events -contains 'fallback-start')) 'Uruchomiono drugi kolektor po niepotwierdzonym starcie zadania'
    Assert (-not (($script:logs -join "`n") -match 'Kolektor dziala|potwierdzono uruchomienie')) 'Falszywy sukces'
}
Check 'W7/W8: odmowa lagodnego zatrzymania poprzednika blokuje podmiane plikow' {
    New-TestContext 'stop'; $script:stopFails=$true
    Must-Fail { Zarejestruj-Kolektor $null } 'Podmieniono pliki bez zakonczonego poprzednika'
    Assert (-not ($script:events -contains 'register')) 'Zarejestrowano mimo niezakonczonego poprzednika'
    Assert (-not (Test-Path -LiteralPath (Join-Path $script:KolektorRoot 'pakiet\probe\probe.exe'))) 'Skopiowano EXE mimo odmowy stop'
}
Check 'W8: blad kopiowania jest koncem rejestracji, bez startu niekompletnego pakietu' {
    New-TestContext 'copy-failure'
    function Copy-Item { [CmdletBinding()]param($LiteralPath,$Destination,[switch]$Recurse,[switch]$Force) Write-Error 'Syntetyczny brak miejsca' }
    Must-Fail { Zarejestruj-Kolektor $null } 'Blad kopiowania nie przerwal rejestracji'
    Assert (-not ($script:events -contains 'register')) 'Zarejestrowano niepelny pakiet'
}
Check 'W8: konczacy sie cmd zadania nie pozwala pominac nowego startu przez IgnoreNew' {
    New-TestContext 'old-task-running'
    @'
function Get-KolektorTask { return [pscustomobject]@{State='Running'} }
function Stop-Kolektor { $script:events.Add('stop-own'); return $true }
'@ | Set-Content -LiteralPath (Join-Path $script:Root 'Kolektor-Common.ps1') -Encoding UTF8
    Must-Fail { Zarejestruj-Kolektor $null } 'Podmieniono nadal dzialajace zadanie'
    Assert ($script:events -contains 'stop-own') 'Nie poproszono o domkniecie probe'
    Assert (-not ($script:events -contains 'register')) 'Zarejestrowano zanim zakonczyl sie stary cmd'
    Assert (-not (Test-Path -LiteralPath (Join-Path $script:KolektorRoot 'pakiet\probe\probe.exe'))) 'Podmieniono pliki zanim zakonczyl sie stary cmd'
}
Check 'W8: po migracji Autostart do zadania nie zostaje drugi wyzwalacz' {
    New-TestContext 'migration'
    $script:oldShortcut=Join-Path $KatalogRoboczy 'synthetic-shortcut.lnk'
    'atrapa' | Set-Content -LiteralPath $script:oldShortcut
    Zarejestruj-Kolektor $null
    Assert (-not (Test-Path -LiteralPath $script:oldShortcut)) 'Pozostal poprzedni Autostart obok zadania'
    Assert ($script:events -contains 'register') 'Brak nowego zadania'
}
Check 'W8: fallback wymaga potwierdzenia procesu, a blokada EXE jest bledem' {
    foreach ($fail in @($false,$true)) {
        New-TestContext ('fallback-' + $fail); $script:registrationFails=$true; $script:proofFails=$fail
        $script:shortcut=[pscustomobject]@{TargetPath='';Arguments='';WorkingDirectory='';WindowStyle=0}
        $script:shortcut | Add-Member -MemberType ScriptMethod -Name Save -Value { $script:events.Add('shortcut-save') }
        $script:shell=[pscustomobject]@{}
        $script:shell | Add-Member -MemberType ScriptMethod -Name CreateShortcut -Value { param($path) return $script:shortcut }
        function New-Object { param($ComObject) Assert ($ComObject -eq 'WScript.Shell') 'Nieoczekiwany COM'; return $script:shell }
        if ($fail) { Must-Fail { Zarejestruj-Kolektor $null } 'Brak procesu fallback nie byl bledem' }
        else { Zarejestruj-Kolektor $null }
        Assert ($script:events -contains 'fallback-start') 'Nie sprobowano startu przez fallback'
        Assert ($script:fallbackWindow -eq 'Hidden') 'Start fallback nie byl ukryty'
        Assert ($script:BladKolektora -eq $fail) 'Niepoprawny wynik potwierdzenia fallback'
    }
}
Check 'W8: nieczytelny Harmonogram i obca tozsamosc nie uruchamiaja fallback' {
    New-TestContext 'task-access'
    @'
function Get-KolektorTask { throw [NotSupportedException]::new('Syntetyczna odmowa odczytu') }
function Stop-Kolektor { throw 'Nie powinno dojsc do stop' }
'@ | Set-Content -LiteralPath (Join-Path $script:Root 'Kolektor-Common.ps1') -Encoding UTF8
    Must-Fail { Zarejestruj-Kolektor $null } 'Brak odczytu Harmonogramu nie byl bledem'
    Assert ($script:events.Count -eq 0) 'Zmieniono zadanie mimo braku dostepu'
    New-TestContext 'task-foreign'
    @'
function Get-KolektorTask { throw [InvalidOperationException]::new('Syntetyczna obca akcja') }
function Stop-Kolektor { throw 'Nie powinno dojsc do stop' }
'@ | Set-Content -LiteralPath (Join-Path $script:Root 'Kolektor-Common.ps1') -Encoding UTF8
    Must-Fail { Zarejestruj-Kolektor $null } 'Obce zadanie nie zatrzymalo rejestracji'
    Assert ($script:events.Count -eq 0) 'Zmieniono obce zadanie'
}
Check 'W8: nieudany start daje kod1 w szybkim i bezposrednim trybie dlugim' {
    $script:BladKolektora=$true; $script:BladZip=$false; $script:KodProbe=0
    $exits=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.ExitStatementAst]},$true) |
        Where-Object { $_.Extent.Text.Contains('$script:BladKolektora') })
    Assert ($exits.Count -eq 2) 'Brak propagacji bledu kolektora w obu wyjsciach'
    foreach ($node in $exits) {
        $code=& ([scriptblock]::Create($node.Pipeline.Extent.Text))
        Assert ($code -eq 1) 'Brak kodu1 po bledzie kolektora'
    }
}

$startHelper=Join-Path $PSScriptRoot 'Kolektor-Start.ps1'
Assert (Test-Path -LiteralPath $startHelper) 'Brak wspolnego potwierdzenia startu'
. (Join-Path $PSScriptRoot 'Kolektor-Common.ps1')
. $startHelper
Check 'W8: skrot Autostartu musi wskazywac dokladnie wlasny pakiet bez dodatkowych argumentow' {
    $root=Join-Path $KatalogRoboczy 'shortcut-proof'; $package=Join-Path $root 'pakiet'
    $script:shortcut=[pscustomobject]@{TargetPath=(Join-Path $package 'Kolektor.cmd');Arguments='';WorkingDirectory=$package}
    $script:shell=[pscustomobject]@{}
    $script:shell | Add-Member -MemberType ScriptMethod -Name CreateShortcut -Value { param($path) return $script:shortcut }
    function New-Object { param($ComObject) return $script:shell }
    function Test-Path { return $true }
    Assert ($null -ne (Get-KolektorStartupShortcut -KolektorRoot $root)) 'Nie przyjeto wlasnego skrotu'
    $script:shortcut.Arguments='--password SYNTHETIC_SECRET'
    Must-Fail { Get-KolektorStartupShortcut -KolektorRoot $root } 'Przyjeto skrot z innymi argumentami'
    $script:shortcut.Arguments=''; $script:shortcut.WorkingDirectory=$KatalogRoboczy
    Must-Fail { Get-KolektorStartupShortcut -KolektorRoot $root } 'Przyjeto skrot z obcym katalogiem'
    $script:shortcut.WorkingDirectory=$package; $script:shortcut.TargetPath=Join-Path $KatalogRoboczy 'foreign.cmd'
    Must-Fail { Get-KolektorStartupShortcut -KolektorRoot $root } 'Przyjeto skrot innego programu'
}
function Set-Summary([string]$Root, [int]$ProcessNumber, [DateTimeOffset]$Started, [bool]$Finished=$false) {
    $dir=Join-Path $Root 'out\dlugi'; New-Item -ItemType Directory -Force -Path $dir | Out-Null
    @{tryb='dlugi';pid=$ProcessNumber;startedUtc=$Started.ToUniversalTime().ToString('O');heartbeatUtc=[DateTimeOffset]::UtcNow.ToString('O');zakonczony=$Finished} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dir 'podsumowanie.json') -Encoding UTF8
}
Check 'W8: dowod startu wymaga zywego PID i swiezego niekoncowego podsumowania' {
    $root=Join-Path $KatalogRoboczy 'dowod'; $since=[DateTimeOffset]::UtcNow.AddSeconds(-5)
    function Get-KolektorProcess { return @([pscustomobject]@{ProcessId=1001;CreationDate=[DateTime]::UtcNow}) }
    Set-Summary $root 1001 ([DateTimeOffset]::UtcNow)
    $proof=Wait-KolektorStarted -KolektorRoot $root -StartedAfterUtc $since -TimeoutSeconds 0
    Assert ($proof.pid -eq 1001) 'Nie przyjeto wlasnego procesu i swiezego pliku'
    Set-Summary $root 1001 ([DateTimeOffset]::UtcNow.AddDays(-1))
    Must-Fail { Wait-KolektorStarted -KolektorRoot $root -StartedAfterUtc $since -TimeoutSeconds 0 } 'Przyjeto stare podsumowanie'
    Set-Summary $root 1002 ([DateTimeOffset]::UtcNow)
    Must-Fail { Wait-KolektorStarted -KolektorRoot $root -StartedAfterUtc $since -TimeoutSeconds 0 } 'Przyjeto podsumowanie obcego PID'
    Set-Summary $root 1001 ([DateTimeOffset]::UtcNow) $true
    Must-Fail { Wait-KolektorStarted -KolektorRoot $root -StartedAfterUtc $since -TimeoutSeconds 0 } 'Przyjeto juz zakonczony przebieg'
    Set-Summary $root 1001 ([DateTimeOffset]::UtcNow)
    function Get-KolektorProcess { return @([pscustomobject]@{ProcessId=1001}, [pscustomobject]@{ProcessId=1002}) }
    Must-Fail { Wait-KolektorStarted -KolektorRoot $root -StartedAfterUtc $since -TimeoutSeconds 0 } 'Przyjeto dwa procesy zapisujace do tego samego katalogu'
    function Get-KolektorProcess { return @() }
    Must-Fail { Wait-KolektorStarted -KolektorRoot $root -StartedAfterUtc $since -TimeoutSeconds 0 } 'Przyjeto plik bez procesu'
}
Check 'W8: niepelny zapis JSON nie jest sukcesem; pozniejszy swiezy zapis potwierdza start' {
    $root=Join-Path $KatalogRoboczy 'opozniony'; $since=[DateTimeOffset]::UtcNow.AddSeconds(-5)
    Set-Summary $root 1001 ([DateTimeOffset]::UtcNow)
    '{niepelny' | Set-Content -LiteralPath (Join-Path $root 'out\dlugi\podsumowanie.json') -Encoding UTF8
    function Get-KolektorProcess { return @([pscustomobject]@{ProcessId=1001}) }
    function Start-Sleep { Set-Summary $root 1001 ([DateTimeOffset]::UtcNow) }
    $proof=Wait-KolektorStarted -KolektorRoot $root -StartedAfterUtc $since -TimeoutSeconds 1
    Assert ($proof.pid -eq 1001) 'Nie doczekano poprawnego zapisu'
}
Check 'Proces konczacy sie zaraz po pierwszym podsumowaniu nie jest potwierdzeniem startu' {
    $root=Join-Path $KatalogRoboczy 'krotki'; $since=[DateTimeOffset]::UtcNow.AddSeconds(-5)
    Set-Summary $root 1001 ([DateTimeOffset]::UtcNow)
    $script:checks=0
    function Get-KolektorProcess { $script:checks++; if ($script:checks -le 1) { return @([pscustomobject]@{ProcessId=1001}) } else { return @() } }
    function Start-Sleep { }
    Must-Fail { Wait-KolektorStarted -KolektorRoot $root -StartedAfterUtc $since -TimeoutSeconds 0 } 'Przyjeto proces, ktory zakonczyl sie zaraz po starcie'
    # Proces jeszcze jest, ale koncowe podsumowanie juz mowi zakonczony.
    $script:checks=0
    function Get-KolektorProcess { $script:checks++; return @([pscustomobject]@{ProcessId=1001}) }
    function Start-Sleep { if ($script:checks -ge 1) { Set-Summary $root 1001 ([DateTimeOffset]::UtcNow) $true } }
    Set-Summary $root 1001 ([DateTimeOffset]::UtcNow)
    Must-Fail { Wait-KolektorStarted -KolektorRoot $root -StartedAfterUtc $since -TimeoutSeconds 0 } 'Przyjeto przebieg zakonczony w czasie sprawdzania'
    # Proces trwa przez caly czas sprawdzania: start potwierdzony.
    $script:checks=0
    function Start-Sleep { }
    Set-Summary $root 1001 ([DateTimeOffset]::UtcNow)
    $proof=Wait-KolektorStarted -KolektorRoot $root -StartedAfterUtc $since -TimeoutSeconds 0
    Assert ($proof.pid -eq 1001) 'Nie potwierdzono trwajacego kolektora'
    Assert ($script:checks -ge 4) 'Nie sprawdzono procesu po pierwszym odczycie'
}
Write-Host ('WYNIK: {0}/{0} PASS; PowerShell {1}; zadania/procesy mockowane, 1 rzeczywisty CMD uruchamia wylacznie atrape EXE bez sieci. Pliki: {2}' -f $script:passed,$PSVersionTable.PSVersion,$KatalogRoboczy)
