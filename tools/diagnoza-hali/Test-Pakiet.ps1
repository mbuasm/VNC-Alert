<#
.SYNOPSIS
    Regresje pakietu w Windows PowerShell 5.1. Bez Pester, sieci, prawdziwych procesow i Harmonogramu.
.DESCRIPTION
    Wykonuje funkcje i kroki z AST produkcyjnego runnera, z atrapami efektow zewnetrznych.
    Pliki testowe powstaja tylko w katalogu roboczym testu. Nie uruchamia calego runnera.
#>
param([string]$KatalogRoboczy = (Join-Path $PSScriptRoot ('..\..\artifacts\test-pakiet-' + [guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 5) { throw 'Uruchom powershell.exe (Windows PowerShell 5.1), nie pwsh.' }
New-Item -ItemType Directory -Path $KatalogRoboczy -Force | Out-Null
$KatalogRoboczy = (Resolve-Path -LiteralPath $KatalogRoboczy).Path
$script:passed = 0
function Assert([bool]$Warunek, [string]$Opis) { if (-not $Warunek) { throw $Opis } }
function Check([string]$Nazwa, [scriptblock]$Test) {
    & $Test
    $script:passed++
    Write-Host ('PASS: ' + $Nazwa)
}
$runner = Join-Path $PSScriptRoot 'Diagnoza-Hali.ps1'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($runner, [ref]$tokens, [ref]$errors)
foreach ($name in @('Krok', 'Pobierz-Haslo', 'Kopiuj-DoCelow', 'Ukryj-Sekrety', 'Kopiuj-JsonBezSekretow', 'Zarejestruj-Kolektor')) {
    $f = $ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] }, $false) | Where-Object Name -eq $name
    Assert ($null -ne $f) ('Brak funkcji ' + $name)
    . ([scriptblock]::Create($f.Extent.Text))
}
function Block([string]$Prefix) {
    $node = $ast.FindAll({ param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Krok' }, $true) |
        Where-Object { $_.CommandElements[1].Value -like ($Prefix + '*') } | Select-Object -First 1
    Assert ($null -ne $node) ('Brak kroku ' + $Prefix)
    return $node.CommandElements[2].ScriptBlock.GetScriptBlock()
}
$script:logs = New-Object 'System.Collections.Generic.List[string]'
$script:tcp = New-Object 'System.Collections.Generic.List[string]'
$script:mutations = New-Object 'System.Collections.Generic.List[string]'
function Zapisz-Log([string]$Tekst) { $script:logs.Add($Tekst) }
function Zapisz-Plik { }
function Uruchom-Polecenie { }
function Znajdz-VncAlert { return (Join-Path $KatalogRoboczy 'atrapa VNC Alert') }
function Procesy-VncAlert { return @([pscustomobject]@{Id=987654321;ProcessName='SyntheticOnly'}) }
function Stop-Process { $script:mutations.Add('Stop-Process'); throw 'Niedozwolone zatrzymanie procesu' }
function Start-Process { $script:mutations.Add('Start-Process'); throw 'Niedozwolony start procesu' }
function Test-Port([string]$Adres,[int]$Port,[int]$LimitMs) { $script:tcp.Add($Adres + ':' + $Port); return 'atrapa' }
function ping.exe { return 'atrapa ping' }
function tracert.exe { return 'atrapa tracert' }
function arp.exe { return '' }
function Get-Volume { return @() }
$script:OutSzybki = Join-Path $KatalogRoboczy 'zażółć gęślą test\out-PC1'
New-Item -ItemType Directory -Force -Path $script:OutSzybki | Out-Null
$script:Hosty = @(
    @{Id='amm520-1';Nazwa='520';Host='127.0.0.1';Port=59201},
    @{Id='amm520-2';Nazwa='520';Host='127.0.0.1';Port=59202},
    @{Id='amm580';Nazwa='580';Host='127.0.0.1';Port=59203},
    @{Id='premile';Nazwa='Premile';Host='127.0.0.1';Port=59204},
    @{Id='cameo1';Nazwa='Cameo';Host='127.0.0.1';Port=59205})

Check 'PS 5.1: wszystkie skrypty parsowalne, UTF-8 BOM' {
    foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1') {
        $t=$null; $e=$null
        [void][Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$t,[ref]$e)
        Assert ($e.Count -eq 0) ($file.Name + ': ' + ($e -join ', '))
        $b=[IO.File]::ReadAllBytes($file.FullName)
        Assert ($b[0]-eq239 -and $b[1]-eq187 -and $b[2]-eq191) ($file.Name + ': brak BOM')
    }
}
Check 'PC1/PC2: rozlaczne maszyny, zero TCP do 520, krok 0 nie steruje aplikacja' {
    $selection = $ast.FindAll({ param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -eq '$script:Maszyny' }, $true) | Select-Object -First 1
    foreach ($Komputer in @('1','2')) {
        . ([scriptblock]::Create($selection.Extent.Text))
        Assert ($script:Maszyny -eq $(if($Komputer -eq '1'){'amm580'}else{'premile,cameo1'})) 'Zly przydzial PC'
        $script:MaszynyPrzebiegu=$script:Maszyny
        foreach ($ZatrzymajVncAlert in @('nie','tak')) {
            $script:tcp.Clear()
            & (Block '0.')
            & (Block '2.')
            Assert (-not ($script:tcp -contains '127.0.0.1:59201')) 'TCP do 520-1'
            Assert (-not ($script:tcp -contains '127.0.0.1:59202')) 'TCP do 520-2'
            Assert ($script:tcp.Count -eq $(if($Komputer -eq '1'){2}else{4})) 'Zly zakres sieci'
        }
    }
    Assert ($script:mutations.Count -eq 0) 'Krok 0 zmienil aplikacje'
}
Check 'Haslo: PC2 nie pyta, pusty Enter i polskie znaki zachowane' {
    $Komputer='2'; $Haslo='IGNORED'
    function Read-Host { throw 'PC2 nie moze pytac o 580' }
    Assert ($null -eq (Pobierz-Haslo)) 'PC2 przyjal haslo 580'
    $Komputer='1'; $Haslo=$null; $BezHasla=$false
    function Read-Host { return (New-Object Security.SecureString) }
    Assert ($null -eq (Pobierz-Haslo)) 'Puste haslo nie jest null'
    function Read-Host { return (ConvertTo-SecureString 'SYNTHETIC_zażółć["' -AsPlainText -Force) }
    Assert ((Pobierz-Haslo) -ceq 'SYNTHETIC_zażółć["') 'Zmienione haslo Unicode'
}
Check 'Probe: haslo tylko w srodowisku dziecka, przywrocone po sukcesie i wyjatku' {
    $probe='Fake-Probe'; $haslo='SYNTHETIC_zażółć["'; $Minuty=2; $config=Join-Path $KatalogRoboczy 'config ze spacjami.json'
    $srod=$KatalogRoboczy; $script:MaszynyPrzebiegu='amm580'
    $envName='VNCALERT_DIAGNOZA_PASSWORD_580'; $saved=[Environment]::GetEnvironmentVariable($envName,'Process')
    try {
        [Environment]::SetEnvironmentVariable($envName,'PREVIOUS','Process')
        foreach ($script:failProbe in @($false,$true)) {
            function Fake-Probe {
                Assert (-not ($args -contains $haslo)) 'Haslo w argumentach'
                Assert (-not ($args -contains '--password')) 'Argument password'
                Assert ([Environment]::GetEnvironmentVariable($envName,'Process') -ceq $haslo) 'Brak hasla w pamieci'
                $global:LASTEXITCODE=3
                if ($script:failProbe) { throw ('syntetyczny blad ' + $haslo) }
            }
            $script:logs.Clear()
            & (Block '3.')
            Assert ([Environment]::GetEnvironmentVariable($envName,'Process') -eq 'PREVIOUS') 'Nie przywrocono env'
            Assert (-not (($script:logs -join "`n").Contains($haslo))) 'Haslo w logu'
            Assert ($script:KodProbe -eq $(if($script:failProbe){-1}else{3})) 'Zly kod probe'
        }
    } finally { [Environment]::SetEnvironmentVariable($envName,$(if ($null -eq $saved) { [NullString]::Value } else { $saved }),'Process') }
}
Check 'DPAPI: polskie haslo dociera do probe przez srodowisko procesu (--z-env), bez potoku; blad nie ujawnia hasla' {
    $reader=Join-Path $KatalogRoboczy 'stdin.ps1'
    '$s=[Environment]::GetEnvironmentVariable("VNCALERT_DIAGNOZA_PASSWORD_580"); if (-not ($args -contains "--z-env")) { $s="BRAK_Z_ENV" }; [IO.File]::WriteAllText((Join-Path $PSScriptRoot "received.txt"), [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($s))); if (Test-Path (Join-Path $PSScriptRoot "fail-password")) { Write-Error ("syntetyczny blad " + $s); exit 1 }; exit 0' | Set-Content -LiteralPath $reader -Encoding UTF8
    $probe=Join-Path $KatalogRoboczy 'stdin.cmd'
    @('@echo off', ('powershell.exe -NoProfile -File "{0}" %*' -f $reader)) | Set-Content -LiteralPath $probe -Encoding ascii
    $haslo='SYNTHETIC_zażółć["'; $Kolektor='tak'; $before=$OutputEncoding.WebName
    function Zarejestruj-Kolektor { }
    foreach ($script:failPassword in @($false,$true)) {
        if ($script:failPassword) { 'synthetic' | Set-Content -LiteralPath (Join-Path $KatalogRoboczy 'fail-password') }
        $script:logs.Clear()
        & (Block '5.')
        $received=Get-Content -LiteralPath (Join-Path $KatalogRoboczy 'received.txt') -Raw
        Assert ($received -eq [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($haslo))) 'Haslo zmienione w drodze do probe'
        Assert ($OutputEncoding.WebName -eq $before) 'Kodowanie nie przywrocone'
        Assert ($null -eq [Environment]::GetEnvironmentVariable('VNCALERT_DIAGNOZA_PASSWORD_580','Process')) 'Haslo zostalo w srodowisku runnera'
        Assert (-not (($script:logs -join "`n").Contains($haslo))) 'Haslo w bledzie DPAPI'
    }
}
Check 'DPAPI: polskie haslo dociera takze przy starcie przez -Command (PPM "Uruchom w programie PowerShell")' {
    $dir=Join-Path $KatalogRoboczy 'command-scope'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $reader=Join-Path $dir 'stdin.ps1'
    '$s=[Environment]::GetEnvironmentVariable("VNCALERT_DIAGNOZA_PASSWORD_580"); if (-not ($args -contains "--z-env")) { $s="BRAK_Z_ENV" }; [IO.File]::WriteAllText((Join-Path $PSScriptRoot "received.txt"), [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($s))); exit 0' | Set-Content -LiteralPath $reader -Encoding UTF8
    $fakeProbe=Join-Path $dir 'stdin.cmd'
    @('@echo off', ('powershell.exe -NoProfile -File "{0}" %*' -f $reader)) | Set-Content -LiteralPath $fakeProbe -Encoding ascii
    $wrapper=Join-Path $dir 'wrapper.ps1'
    @'
param([string]$Runner, [string]$FakeProbe, [string]$Out)
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($Runner, [ref]$tokens, [ref]$errors)
$node=$ast.FindAll({ param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Krok' }, $true) |
    Where-Object { $_.CommandElements[1].Value -like '5.*' } | Select-Object -First 1
function Zapisz-Log([string]$Tekst) { }
function Zarejestruj-Kolektor { }
$probe=$FakeProbe; $Kolektor='tak'; $script:OutSzybki=$Out
$haslo='SYNTHETIC_' + [char]0x017C + [char]0x00F3 + [char]0x0142 + [char]0x0107
& ($node.CommandElements[2].ScriptBlock.GetScriptBlock())
'@ | Set-Content -LiteralPath $wrapper -Encoding UTF8
    $expected='SYNTHETIC_' + [char]0x017C + [char]0x00F3 + [char]0x0142 + [char]0x0107
    $command="& '{0}' -Runner '{1}' -FakeProbe '{2}' -Out '{3}'" -f $wrapper, $runner, $fakeProbe, $dir
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -Command $command | Out-Null
    $received=Get-Content -LiteralPath (Join-Path $dir 'received.txt') -Raw
    Assert ($received -eq [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($expected))) 'Potok przy -Command zamienil polskie znaki hasla'
}
Check 'Kolektor: dawne HasloDoKolektora nie zapisuje hasla w CMD ani zadaniu' {
    $script:Root=Join-Path $KatalogRoboczy 'source'; $script:KolektorRoot=Join-Path $KatalogRoboczy 'collector'; $script:ZadanieNazwa='SYNTHETIC_ONLY'
    $script:Maszyny='amm580'; $HasloDoKolektora=$true; $script:registered=$false
    New-Item -ItemType Directory -Force -Path (Join-Path $script:Root 'probe') | Out-Null
    'placeholder' | Set-Content -LiteralPath (Join-Path $script:Root 'probe\probe.exe')
    '{"machines":[]}' | Set-Content -LiteralPath (Join-Path $script:Root 'config.json') -Encoding UTF8
    @'
function Get-KolektorTask { return $null }
function Stop-Kolektor { return $true }
'@ | Set-Content -LiteralPath (Join-Path $script:Root 'Kolektor-Common.ps1') -Encoding UTF8
    @'
function Get-KolektorStartupShortcut { return $null }
function Wait-KolektorStarted { return [pscustomobject]@{pid=12345;zakonczony=$false} }
'@ | Set-Content -LiteralPath (Join-Path $script:Root 'Kolektor-Start.ps1') -Encoding UTF8
    function Znajdz-Probe { return (Join-Path $script:Root 'probe\probe.exe') }
    function Import-Module { }
    function Get-Process { return @() }
    function Start-Sleep { }
    function Get-ScheduledTask { if ($script:registered) { return [pscustomobject]@{State='Running'} } }
    function Stop-ScheduledTask { throw 'Nie ma starego zadania w tescie' }
    function New-ScheduledTaskAction { param($Execute,$Argument,$WorkingDirectory) $script:taskArgument=$Argument; return 'action' }
    function New-ScheduledTaskTrigger { return 'trigger' }
    function New-ScheduledTaskSettingsSet { return 'settings' }
    function Register-ScheduledTask { $script:registered=$true; return [pscustomobject]@{TaskName='SYNTHETIC_ONLY';State='Ready'} }
    function Start-ScheduledTask { }
    $haslo='SYNTHETIC_SECRET_123'
    Zarejestruj-Kolektor $haslo
    $generated=Get-Content -LiteralPath (Join-Path $script:KolektorRoot 'pakiet\Kolektor.cmd') -Raw
    Assert (-not $generated.Contains($haslo)) 'Jawne haslo w Kolektor.cmd'
    Assert (-not $script:taskArgument.Contains($haslo)) 'Jawne haslo w zadaniu'
    Assert ($script:mutations.Count -eq 0) 'Uruchomiono proces z testu'
}
Check 'JSON: zagniezdzone sekrety i cudzyslowy usuniete, Unicode i dane profilu zachowane' {
    $src=Join-Path $KatalogRoboczy 'config.json'; $dst=Join-Path $KatalogRoboczy 'clean.json'
    $secret='SYNTHETIC_"escape\\secret'
    @{theme='jasny-żółty';machines=@(@{id='test';profile='amm580';requiresPassword=$false;password=$secret;vncPassword=$secret});nested=@{token=$secret;secretValue=$secret;hmac=$secret;apiKey=$secret};roi=@{x=10;y=20}} |
        ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $src -Encoding UTF8
    Kopiuj-JsonBezSekretow $src $dst
    $clean=Get-Content -LiteralPath $dst -Encoding UTF8 -Raw
    Assert (-not $clean.Contains('SYNTHETIC')) 'Sekret w JSON'
    $data=$clean | ConvertFrom-Json
    Assert ($data.theme -ceq 'jasny-żółty' -and $data.roi.x -eq 10 -and $data.machines[0].profile -eq 'amm580') 'Zniszczono pomiar'
    Assert ($data.machines[0].requiresPassword -is [bool] -and $data.machines[0].requiresPassword -eq $false) 'Zniszczono flage requiresPassword'
    Kopiuj-JsonBezSekretow (Join-Path $PSScriptRoot 'config.diagnoza-hali.json') (Join-Path $KatalogRoboczy 'actual-config-clean.json')
}
Check 'Niepoprawny JSON: bez surowej kopii i bez sekretu w komunikacie parsera' {
    $src=Join-Path $KatalogRoboczy 'broken.json'; $kopia=$KatalogRoboczy; $f='must-not-exist.json'
    '{"password":"SYNTHETIC_PARSER_SECRET",broken' | Set-Content -LiteralPath $src -Encoding UTF8
    $try=$ast.FindAll({ param($n) $n -is [Management.Automation.Language.TryStatementAst] -and $n.Body.Extent.Text.Contains('Kopiuj-JsonBezSekretow $src') },$true) | Select-Object -First 1
    $script:logs.Clear()
    & ([scriptblock]::Create($try.Extent.Text))
    Assert (-not (Test-Path -LiteralPath (Join-Path $kopia $f))) 'Surowy config zapisany'
    Assert (-not (($script:logs -join "`n").Contains('SYNTHETIC_PARSER_SECRET'))) 'Sekret w bledzie parsera'
}
Check 'Blad kopiowania i brak pendrive: zadnych fikcyjnych sciezek sukcesu' {
    function Copy-Item { [CmdletBinding()]param($LiteralPath,$Destination,[switch]$Force) Write-Error 'Syntetyczny brak miejsca' }
    function Get-Volume { return @() }
    $destinations=@(Kopiuj-DoCelow (Join-Path $KatalogRoboczy 'test.zip'))
    Assert ($destinations.Count -eq 0) 'Zgloszono nieistniejaca kopie'
}
Check 'ZIP: Unicode, manifest i config zachowane; blad ZIP wykrywany' {
    $script:Root=Join-Path $KatalogRoboczy 'zip'; $script:Stamp='synthetic'; $Komputer='1'; $script:Zip=$null
    New-Item -ItemType Directory -Force -Path $script:Root | Out-Null
    $srod=Join-Path $script:OutSzybki 'srodowisko'
    New-Item -ItemType Directory -Force -Path $srod | Out-Null
    '{"commit":"synthetic"}' | Set-Content -LiteralPath (Join-Path $srod 'diagnoza-build-info.json') -Encoding UTF8
    Kopiuj-JsonBezSekretow (Join-Path $PSScriptRoot 'config.diagnoza-hali.json') (Join-Path $srod 'diagnoza-config.json')
    function Kopiuj-DoCelow { return @() }
    Krok 'test ZIP' (Block '4.')
    Assert (Test-Path -LiteralPath $script:Zip) 'ZIP nie powstal'
    $archive=[IO.Compression.ZipFile]::OpenRead($script:Zip)
    try {
        $names=@($archive.Entries | ForEach-Object { $_.FullName.Replace('\','/') })
        Assert ($names -contains 'srodowisko/diagnoza-build-info.json') 'Brak manifestu'
        Assert ($names -contains 'srodowisko/diagnoza-config.json') 'Brak configu'
        Assert (-not ($names -match 'secrets\.dat|hasla\.dpapi')) 'Sekrety w ZIP'
    } finally { $archive.Dispose() }
    $script:Zip=$null; $script:OutSzybki=Join-Path $KatalogRoboczy 'missing'; $script:BladZip=$false
    Krok 'test ZIP failure' (Block '4.')
    $guard=$ast.FindAll({param($n) $n -is [Management.Automation.Language.IfStatementAst] -and $n.Extent.Text.StartsWith('if (-not $script:Zip -or')},$true) | Select-Object -First 1
    & ([scriptblock]::Create($guard.Extent.Text))
    Assert $script:BladZip 'Blad ZIP zostal potraktowany jako sukces'
    $exitNode=$ast.FindAll({param($n) $n -is [Management.Automation.Language.ExitStatementAst]},$true) | Select-Object -Last 1
    $script:KodProbe=0
    $exitCode=& ([scriptblock]::Create($exitNode.Pipeline.Extent.Text))
    Assert ($exitCode -eq 1) 'Blad ZIP nie daje kodu 1'
}
Check 'Krok 1: manifest i rzeczywista konfiguracja diagnozy trafiaja do wynikow' {
    $script:Root=Join-Path $KatalogRoboczy 'metadata-source'; $srod=Join-Path $KatalogRoboczy 'metadata-out'
    New-Item -ItemType Directory -Force -Path $script:Root,$srod | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'config.diagnoza-hali.json') -Destination (Join-Path $script:Root 'config.json')
    '{"commit":"TEST_COMMIT","galaz":"test/diagnoza"}' | Set-Content -LiteralPath (Join-Path $script:Root 'build-info.json') -Encoding UTF8
    $copyNode=$ast.FindAll({param($n) $n -is [Management.Automation.Language.ForEachStatementAst] -and $n.Variable.Extent.Text -eq '$wejscie'},$true) | Select-Object -First 1
    & ([scriptblock]::Create($copyNode.Extent.Text))
    $manifest=Get-Content -LiteralPath (Join-Path $srod 'diagnoza-build-info.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert ($manifest.commit -eq 'TEST_COMMIT') 'Utracony commit diagnozy'
    $config=Get-Content -LiteralPath (Join-Path $srod 'diagnoza-config.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert (($config.machines | Where-Object id -eq 'amm520-1').requiresPassword -eq $false) 'Utracona konfiguracja 520'
}
Write-Host ('WYNIK: {0}/{0} PASS; PowerShell {1}; bez sieci i zmian programow/zadan. Pliki: {2}' -f $script:passed,$PSVersionTable.PSVersion,$KatalogRoboczy)
