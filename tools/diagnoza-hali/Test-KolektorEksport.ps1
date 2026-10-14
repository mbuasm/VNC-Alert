param([string]$KatalogRoboczy = (Join-Path $PSScriptRoot ('..\..\artifacts\test-kolektor-eksport-' + [guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 5) { throw 'Uruchom przez powershell.exe (PowerShell 5.1).' }
New-Item -ItemType Directory -Path $KatalogRoboczy -Force | Out-Null
$KatalogRoboczy = (Resolve-Path -LiteralPath $KatalogRoboczy).Path
. (Join-Path $PSScriptRoot 'Kolektor-Common.ps1')
# Ladowanie funkcji bez uruchomienia wejscia exportera, zadania ani autostartu.
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Zbierz-Wyniki.ps1'), [ref]$tokens, [ref]$errors)
foreach ($definition in $ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst]}, $false)) {
    . ([scriptblock]::Create($definition.Extent.Text))
}
$script:Passed = 0
$script:Messages = New-Object 'System.Collections.Generic.List[string]'
$script:Mutations = New-Object 'System.Collections.Generic.List[string]'
$script:Processes = @()
$script:Tasks = @()
$script:FinishOnRequest = $false
$script:CurrentRoot = $null
function Powiedz([string]$Tekst) { $script:Messages.Add($Tekst) }
function Assert([bool]$Value, [string]$Message) { if (-not $Value) { throw $Message } }
function Check([string]$Name, [scriptblock]$Body) {
    $script:Messages.Clear()
    $script:Mutations.Clear()
    $script:Processes = @()
    $script:Tasks = @()
    $script:FinishOnRequest = $false
    & $Body
    $script:Passed++
    Write-Host ('PASS: ' + $Name)
}
function Expect-Failure([scriptblock]$Body, [string]$Secret = '') {
    $failed = $false
    try { & $Body | Out-Null } catch {
        $failed = $true
        if ($Secret) { Assert (-not $_.ToString().Contains($Secret)) 'Wyjatek ujawnia dane wejsciowe' }
    }
    Assert $failed 'Oczekiwano bledu'
}
function New-TestRoot([string]$Name) {
    $root = Join-Path $KatalogRoboczy ($Name + ' zażółć gęślą')
    New-Item -ItemType Directory -Path (Join-Path $root 'pakiet\probe'), (Join-Path $root 'out\dlugi') -Force | Out-Null
    return $root
}
function New-TestProcess([string]$Root, [int]$ProcessNumber, [string]$Out = '') {
    if (-not $Out) { $Out = Join-Path $Root 'out' }
    return [pscustomobject]@{
        ProcessId = $ProcessNumber
        ExecutablePath = Join-Path $Root 'pakiet\probe\probe.exe'
        CommandLine = ('"{0}" diagnoza --tryb dlugi --out "{1}" --config "{2}" --no-auth-prompt' -f
            (Join-Path $Root 'pakiet\probe\probe.exe'), $Out, (Join-Path $Root 'pakiet\config.json'))
        CreationDate = [datetime]::UtcNow.AddMinutes(-1)
    }
}
function Write-TestSummary([string]$Root, [int]$ProcessNumber, [string]$Started, [bool]$Final) {
    $object = @{pid=$ProcessNumber;startedUtc=$Started;heartbeatUtc=[DateTimeOffset]::UtcNow.ToString('O');zakonczony=$Final;maszyny=@{}}
    [IO.File]::WriteAllText((Join-Path $Root 'out\dlugi\podsumowanie.json'), ($object | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
}
function Get-CimInstance {
    [CmdletBinding()]param([string]$ClassName,[string]$Filter)
    Assert ($ClassName -eq 'Win32_Process' -and $Filter -eq "Name = 'probe.exe'") 'Nieoczekiwany odczyt procesow'
    if ($script:FinishOnRequest -and (Test-Path -LiteralPath (Join-Path $script:CurrentRoot 'out\dlugi\stop-request.json'))) {
        $request = Get-Content -LiteralPath (Join-Path $script:CurrentRoot 'out\dlugi\stop-request.json') -Encoding UTF8 -Raw | ConvertFrom-Json
        Write-TestSummary $script:CurrentRoot $request.pid $request.startedUtc $true
        return @($script:Processes | Where-Object {$_.ProcessId -ne $request.pid})
    }
    return $script:Processes
}
function Get-ScheduledTask { [CmdletBinding()]param(); return $script:Tasks }
function Stop-ScheduledTask { throw 'Test nie moze zatrzymywac zadania.' }
function Stop-Process { throw 'Test nie moze zatrzymywac procesu.' }
function Start-Process { throw 'Test nie moze uruchomic programu.' }
function Start-ScheduledTask { throw 'Test nie moze uruchomic zadania.' }
function Unregister-ScheduledTask { throw 'Test nie moze usunac zadania.' }

Check 'PS 5.1: pliki parse, UTF-8 BOM, CMD przenosi kod i nie zawiera taskkill' {
    foreach ($name in @('Kolektor-Common.ps1','Zbierz-Wyniki.ps1','Zatrzymaj-Kolektor.ps1','Test-KolektorEksport.ps1')) {
        $path = Join-Path $PSScriptRoot $name
        $t = $null; $e = $null
        [void][Management.Automation.Language.Parser]::ParseFile($path,[ref]$t,[ref]$e)
        Assert ($e.Count -eq 0) ('Blad skladni: ' + $name)
        $bytes = [IO.File]::ReadAllBytes($path)
        Assert ($bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191) ('Brak BOM: ' + $name)
    }
    foreach ($name in @('Zatrzymaj-Kolektor.cmd','Zbierz-Wyniki.cmd')) {
        $cmd = Get-Content -LiteralPath (Join-Path $PSScriptRoot $name) -Raw
        Assert ($cmd -match 'exit /b %diagnozaExit%' -and $cmd -notmatch 'taskkill') 'CMD gubi kod lub zabija procesy'
    }
}

Check 'W7: exact exe + tryb + out, ta sama binarka z innym out pozostaje' {
    $root = New-TestRoot 'identity'
    $first = New-TestProcess $root 900001 (Join-Path $root 'pakiet\..\out')
    $second = New-TestProcess $root 900002 (Join-Path $root 'out-drugi')
    $third = New-TestProcess (New-TestRoot 'obcy') 900003 (Join-Path $root 'out')
    $fourth = New-TestProcess $root 900004
    $fourth.CommandLine = $fourth.CommandLine.Replace('--tryb dlugi','--tryb szybki')
    $script:Processes = @($first,$second,$third,$fourth)
    $found = @(Get-KolektorProcess $root)
    Assert ($found.Count -eq 1 -and $found[0].ProcessId -eq 900001) 'Wybrano inny probe'
}

Check 'W7: niedostepna tozsamosc procesu i obce zadanie = odmowa bez zmian' {
    $root = New-TestRoot 'refusal'
    $script:Processes = @([pscustomobject]@{ProcessId=900005;ExecutablePath=$null;CommandLine='SECRET-DONT-PRINT'})
    Expect-Failure { Get-KolektorProcess $root } 'SECRET-DONT-PRINT'
    $script:Tasks = @([pscustomobject]@{TaskName='diagnoza-hali-kolektor';TaskPath='\';Actions=@([pscustomobject]@{
        Execute='cmd.exe';Arguments='/c foreign.cmd';WorkingDirectory=(Join-Path $root 'pakiet')
    })})
    Expect-Failure { Get-KolektorTask $root }
    $script:Tasks[0].Actions[0].Arguments = '/c ""{0}""' -f (Join-Path $root 'pakiet\Kolektor.cmd')
    Assert ($null -ne (Get-KolektorTask $root)) 'Nie rozpoznano wlasnego zadania'
}

Check 'W7: lagodne zakonczenie zapisuje pid/start, czeka na final i nie dotyka drugiego procesu' {
    $root = New-TestRoot 'graceful'
    $script:CurrentRoot = $root
    $script:Processes = @((New-TestProcess $root 900006),(New-TestProcess $root 900007 (Join-Path $root 'out-drugi')))
    $started = [DateTimeOffset]::UtcNow.AddSeconds(-10).ToString('O')
    Write-TestSummary $root 900006 $started $false
    $script:FinishOnRequest = $true
    Assert (Stop-Kolektor $root -TimeoutSeconds 0) 'Nie potwierdzono zamkniecia'
    $request = Get-Content -LiteralPath (Join-Path $root 'out\dlugi\stop-request.json') -Encoding UTF8 -Raw | ConvertFrom-Json
    Assert ($request.pid -eq 900006 -and $request.startedUtc -ceq $started) 'Zla tozsamosc prosby'
    Assert ((Read-KolektorSummary $root).zakonczony -eq $true) 'Brak finalnego zapisu'
}

Check 'W7: timeout bez force; stare podsumowanie nie wysyla prosby do nowego PID' {
    $root = New-TestRoot 'timeout'
    $script:Processes = @((New-TestProcess $root 900008))
    $started = [DateTimeOffset]::UtcNow.AddSeconds(-10).ToString('O')
    Write-TestSummary $root 900008 $started $false
    Expect-Failure { Stop-Kolektor $root -TimeoutSeconds 0 }
    Assert (Test-Path -LiteralPath (Join-Path $root 'out\dlugi\stop-request.json')) 'Brak lagodnej prosby'
    $other = New-TestRoot 'stale'
    $script:Processes = @((New-TestProcess $other 900009))
    Write-TestSummary $other 900008 $started $false
    Expect-Failure { Stop-Kolektor $other -TimeoutSeconds 0 }
    Assert (-not (Test-Path -LiteralPath (Join-Path $other 'out\dlugi\stop-request.json'))) 'Zatrzymano proces na podstawie starego PID'
}

Check 'W9: ZIP maskuje zagniezdzone JSON/JSONL i wyklucza pliki hasel w kazdym podkatalogu' {
    $root = New-TestRoot 'zip-unicode'
    $out = Join-Path $root 'out'
    $secret = 'żółć "tajna" ['
    $doc = [ordered]@{nazwa='Żółta maszyna';requiresPassword=$true;password=$secret;nested=@(@{accessClientSecret=$secret;apiKey=$secret});empty=@()}
    [IO.File]::WriteAllText((Join-Path $out 'config.json'), ($doc | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText((Join-Path $out 'dlugi\epizody.jsonl'), ('{"tekstOcr":"Żółć","token":"not-for-zip"}' + [Environment]::NewLine), (New-Object Text.UTF8Encoding($false)))
    foreach ($name in @('secrets.dat','secrets.lock','hasla.dpapi')) { [IO.File]::WriteAllText((Join-Path $out ('dlugi\' + $name)), 'NEVER-EXPORT') }
    $zip = New-KolektorZip $out (Join-Path $root 'wynik.zip')
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        Assert ($archive.Entries.Count -eq 2) 'Eksport objal pliki sekretow'
        $reader = New-Object IO.StreamReader($archive.GetEntry('config.json').Open(), [Text.Encoding]::UTF8)
        try { $result = $reader.ReadToEnd() } finally { $reader.Dispose() }
        Assert (-not $result.Contains($secret)) 'Haslo w ZIP'
        $parsed = $result | ConvertFrom-Json
        Assert ($parsed.nazwa -eq 'Żółta maszyna' -and $parsed.requiresPassword -eq $true -and $parsed.password -eq '***') 'UTF-8 lub redakcja'
        Assert ($parsed.nested[0].accessClientSecret -eq '***' -and $parsed.nested[0].apiKey -eq '***' -and $parsed.empty.Count -eq 0) 'Zagniezdzone pola/array'
        $reader = New-Object IO.StreamReader($archive.GetEntry('dlugi/epizody.jsonl').Open(), [Text.Encoding]::UTF8)
        try { $jsonl = $reader.ReadToEnd() } finally { $reader.Dispose() }
        Assert ($jsonl.Contains('Żółć') -and -not $jsonl.Contains('not-for-zip')) 'JSONL bez BOM lub sekret'
    } finally { $archive.Dispose() }
    Show-KolektorEpisodes $out
    Assert (($script:Messages -join '|').Contains('Żółć')) 'Podglad OCR bez jawnego UTF-8'
}

Check 'W9/W6: niepoprawny JSON nie wycieka w bledzie ani czesciowym ZIP; out zostaje' {
    $root = New-TestRoot 'bad-json'
    $source = Join-Path $root 'out\broken.json'
    $raw = '{"password":"SYNTHETIC-PASSWORD-NOT-PRINTED", broken'
    [IO.File]::WriteAllText($source, $raw, (New-Object Text.UTF8Encoding($false)))
    Expect-Failure { New-KolektorZip (Join-Path $root 'out') (Join-Path $root 'failure.zip') } 'SYNTHETIC-PASSWORD-NOT-PRINTED'
    Assert ([IO.File]::ReadAllText($source) -ceq $raw) 'Zmieniono zrodlowe dane'
    Assert (-not (Test-Path -LiteralPath (Join-Path $root 'failure.zip'))) 'Niekompletny ZIP udaje gotowy'
    Assert (@(Get-ChildItem -LiteralPath $root -Filter '*.partial').Count -eq 0) 'Zostal czesciowy ZIP'
    Expect-Failure { ConvertTo-KolektorSafeJson $raw } 'SYNTHETIC-PASSWORD-NOT-PRINTED'
}

Check 'W6: kopiowanie potwierdza istnienie; nieprzerywajacy blad jest porazka, bez komunikatu kopia' {
    $root = New-TestRoot 'copy'
    $zip = Join-Path $root 'original.zip'
    [IO.File]::WriteAllText($zip, 'archive-fixture')
    $destination = Join-Path $root 'copy-ok'
    New-Item -ItemType Directory -Path $destination | Out-Null
    Assert ((Copy-KolektorZip $zip @($destination)) -eq 0) 'Poprawna kopia'
    $script:Messages.Clear()
    function Copy-Item { [CmdletBinding()]param([string]$LiteralPath,[string]$Destination,[switch]$Force); Write-Error 'Synthetic full disk' }
    Assert ((Copy-KolektorZip $zip @((Join-Path $root 'copy-fail'))) -eq 1) 'Blad kopii udaje sukces'
    Assert (-not ($script:Messages -match '^kopia:')) 'Nieistniejaca kopia ogloszona jako sukces'
    Assert (Test-Path -LiteralPath $zip) 'Usunieto oryginalny ZIP'
}

Check 'W6/W7: odmowa stop blokuje ZIP i usuwanie; awaria kopii daje kod1 i zachowuje dane/autostart' {
    $root = New-TestRoot 'orchestration'
    [IO.File]::WriteAllText((Join-Path $root 'out\wynik.txt'), 'fixture')
    function Stop-Kolektor { throw 'Synthetic timeout' }
    function New-KolektorZip { throw 'NIE POWINNO PAKOWAC' }
    function Remove-KolektorAutostart { $script:Mutations.Add('remove') }
    function Restart-Kolektor { $script:Mutations.Add('restart') }
    Assert ((Invoke-KolektorExport $root -Remove) -eq 1) 'Brak exit1'
    Assert ($script:Mutations.Count -eq 0) 'Zmiany po odmowie stop'
}
Check 'W6: poprawny ZIP i blad kopii zwraca1, nie usuwa -Usun, nie kasuje out' {
    $root = New-TestRoot 'copy-exit'
    [IO.File]::WriteAllText((Join-Path $root 'out\wynik.txt'), 'fixture')
    Write-TestSummary $root 900010 ([DateTimeOffset]::UtcNow.ToString('O')) $true
    function Stop-Kolektor { return $true }
    function Get-KolektorCopyTargets { return @((Join-Path $root 'nieistniejacy-pulpit')) }
    function Copy-Item { [CmdletBinding()]param([string]$LiteralPath,[string]$Destination,[switch]$Force); Write-Error 'Synthetic disk full' }
    function Remove-KolektorAutostart { $script:Mutations.Add('remove') }
    Assert ((Invoke-KolektorExport $root -Remove) -eq 1) 'Brak exit1 po bledzie kopii'
    Assert ($script:Mutations.Count -eq 0) 'Usunieto autostart po bledzie kopii'
    Assert (@(Get-ChildItem -LiteralPath $root -Filter '*.zip').Count -eq 1) 'Nie zachowano lokalnego ZIP'
    Assert (Test-Path -LiteralPath (Join-Path $root 'out\wynik.txt')) 'Nie zachowano out'
}
Check 'W6/W8: brak potwierdzenia restartu daje kod1 mimo poprawnego ZIP' {
    $root = New-TestRoot 'restart'
    [IO.File]::WriteAllText((Join-Path $root 'out\wynik.txt'), 'fixture')
    Write-TestSummary $root 900011 ([DateTimeOffset]::UtcNow.ToString('O')) $true
    function Stop-Kolektor { return $true }
    function Get-KolektorCopyTargets { return @() }
    function Restart-Kolektor { throw 'Synthetic restart failed' }
    Assert ((Invoke-KolektorExport $root) -eq 1) 'Brak exit1 po bledzie restartu'
    Assert (@(Get-ChildItem -LiteralPath $root -Filter '*.zip').Count -eq 1) 'Blad restartu usuwa ZIP'
}
Check 'W6/W7: po crashu ZIP istnieje, ale wynik niepelny daje1 i blokuje -Usun' {
    foreach ($state in @('niezakonczony','uszkodzony','brak','zakonczony')) {
        $script:Mutations.Clear()
        $root = New-TestRoot ('crash-' + $state)
        [IO.File]::WriteAllText((Join-Path $root 'out\wynik.txt'), 'fixture')
        if ($state -eq 'niezakonczony' -or $state -eq 'zakonczony') {
            Write-TestSummary $root 900012 ([DateTimeOffset]::UtcNow.ToString('O')) ($state -eq 'zakonczony')
        }
        if ($state -eq 'uszkodzony') {
            [IO.File]::WriteAllText((Join-Path $root 'out\dlugi\podsumowanie.json'), '{"password":"CRASH-SECRET", broken')
        }
        function Stop-Kolektor { return $true }
        function Get-KolektorCopyTargets { return @() }
        function Remove-KolektorAutostart { $script:Mutations.Add('remove') }
        $result = Invoke-KolektorExport $root -Remove
        $complete = $state -eq 'zakonczony'
        Assert ($result -eq $(if ($complete) {0} else {1})) ('Zly kod po crashu: ' + $state)
        Assert ($script:Mutations.Count -eq $(if ($complete) {1} else {0})) ('Usunieto autostart po crashu: ' + $state)
        $zips = @(Get-ChildItem -LiteralPath $root -Filter '*.zip')
        Assert ($zips.Count -eq 1) ('Nie zebrano istniejacych danych: ' + $state)
        $archive = [IO.Compression.ZipFile]::OpenRead($zips[0].FullName)
        try {
            Assert ($null -ne $archive.GetEntry('wynik.txt')) 'Brak zebranych danych'
            Assert (($null -ne $archive.GetEntry('eksport-kolektora-status.txt')) -eq (-not $complete)) 'Brak trwałego oznaczenia niepelnosci'
            if ($state -eq 'uszkodzony') { Assert ($null -eq $archive.GetEntry('dlugi/podsumowanie.json')) 'Dolaczono surowy uszkodzony JSON' }
        } finally { $archive.Dispose() }
        Assert (Test-Path -LiteralPath (Join-Path $root 'out\wynik.txt')) 'Usunieto dane zrodlowe'
        Assert (-not (($script:Messages -join '|').Contains('CRASH-SECRET'))) 'Komunikat ujawnia tresc uszkodzonego JSON'
    }
}
Check 'W7: -Usun usuwa tylko potwierdzony task/skrot/DPAPI, zostawia out' {
    $root = New-TestRoot 'scoped-remove'
    $shortcut = Join-Path $root 'atrapa.lnk'
    $script:ShortcutFixture = $shortcut
    [IO.File]::WriteAllText($shortcut, 'shortcut-fixture')
    [IO.File]::WriteAllText((Join-Path $root 'hasla.dpapi'), 'encrypted-fixture')
    [IO.File]::WriteAllText((Join-Path $root 'out\wynik.txt'), 'fixture')
    function Get-KolektorTask { param([string]$KolektorRoot); return [pscustomobject]@{TaskName='diagnoza-hali-kolektor';TaskPath='\'} }
    function Get-KolektorStartupShortcut { param([string]$KolektorRoot); return $script:ShortcutFixture }
    function Unregister-ScheduledTask { [CmdletBinding(SupportsShouldProcess=$true)]param([string]$TaskName,[string]$TaskPath); $script:Mutations.Add($TaskPath + $TaskName) }
    Remove-KolektorAutostart $root
    Assert ($script:Mutations.Count -eq 1 -and $script:Mutations[0] -eq '\diagnoza-hali-kolektor') 'Usunieto inne zadanie'
    Assert (-not (Test-Path -LiteralPath $shortcut) -and -not (Test-Path -LiteralPath (Join-Path $root 'hasla.dpapi'))) 'Nie usunieto wlasnego autostartu/DPAPI'
    Assert (Test-Path -LiteralPath (Join-Path $root 'out\wynik.txt')) 'Usunieto wyniki'
}
Write-Host ('PASS: {0}/{0} testow eksportera kolektora, PowerShell {1}' -f $script:Passed, $PSVersionTable.PSVersion)
