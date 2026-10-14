# Wspolna identyfikacja kolektora. Bez zatrzymywania procesu/zadania po samej nazwie.
function Resolve-KolektorPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not [IO.Path]::IsPathRooted($Path)) {
        throw 'Nie mozna potwierdzic pelnej sciezki kolektora.'
    }
    return [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
}

function Split-KolektorCommandLine([string]$CommandLine) {
    if ([string]::IsNullOrWhiteSpace($CommandLine)) { throw 'Nie mozna odczytac polecenia procesu kolektora.' }
    $tokens = New-Object 'System.Collections.Generic.List[string]'
    $end = 0
    foreach ($m in [regex]::Matches($CommandLine, '"[^"\r\n]*"|[^\s"]+')) {
        $gap = $CommandLine.Substring($end, $m.Index - $end)
        if ($gap -match '\S' -or ($tokens.Count -gt 0 -and $gap.Length -eq 0)) {
            throw 'Nie mozna jednoznacznie odczytac polecenia procesu kolektora.'
        }
        $tokens.Add($m.Value.Trim('"'))
        $end = $m.Index + $m.Length
    }
    if ($CommandLine.Substring($end) -match '\S') { throw 'Nie mozna jednoznacznie odczytac polecenia procesu kolektora.' }
    return $tokens.ToArray()
}

function Get-KolektorOption([string[]]$Tokens, [string]$Name) {
    $found = $null
    for ($i = 2; $i -lt $Tokens.Count; $i++) {
        if ($Tokens[$i] -ceq $Name) {
            if ($null -ne $found -or $i + 1 -ge $Tokens.Count) { throw 'Niejednoznaczne argumenty procesu kolektora.' }
            $found = $Tokens[$i + 1]
        }
    }
    return $found
}

function Test-KolektorTenSamProces($Process, $Wzorzec) {
    # PID moze zostac uzyty ponownie; PID razem z czasem utworzenia wskazuje jedna instancje procesu.
    return ($null -ne $Process -and $null -ne $Wzorzec -and $null -ne $Process.CreationDate -and $null -ne $Wzorzec.CreationDate -and
        $Process.ProcessId -eq $Wzorzec.ProcessId -and $Process.CreationDate -eq $Wzorzec.CreationDate)
}

function Test-KolektorTozsamoscNieczytelna($Process, [string]$ExpectedExe) {
    # Bez sciezki nie wiadomo, czy to kolektor; z nasza sciezka, ale bez polecenia nie wiadomo, czy to ten --out i tryb.
    if ([string]::IsNullOrWhiteSpace($Process.ExecutablePath)) { return $true }
    return ([string]::IsNullOrWhiteSpace($Process.CommandLine) -and (Resolve-KolektorPath $Process.ExecutablePath) -ieq $ExpectedExe)
}

function Get-KolektorProcess([string]$KolektorRoot, $Konczacy = $null) {
    $root = Resolve-KolektorPath $KolektorRoot
    $expectedExe = Join-Path $root 'pakiet\probe\probe.exe'
    $expectedOut = Join-Path $root 'out'
    for ($attempt = 1; ; $attempt++) {
        try { $processes = @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'probe.exe'" -ErrorAction Stop) }
        catch { throw 'Nie mozna sprawdzic tozsamosci procesow probe. Niczego nie zatrzymano.' }
        $unknown = @($processes | Where-Object { (Test-KolektorTozsamoscNieczytelna $_ $expectedExe) -and -not (Test-KolektorTenSamProces $_ $Konczacy) })
        if ($unknown.Count -eq 0) { break }
        if ($attempt -ge 10) { throw 'Brak dostepu do sciezki lub polecenia procesu probe. Niczego nie zatrzymano.' }
        Start-Sleep -Milliseconds 200
    }
    foreach ($process in $processes) {
        if (Test-KolektorTozsamoscNieczytelna $process $expectedExe) { $process; continue } # tylko $Konczacy (petla wyzej)
        if ((Resolve-KolektorPath $process.ExecutablePath) -ine $expectedExe) { continue }
        $tokens = @(Split-KolektorCommandLine $process.CommandLine)
        if ($tokens.Count -lt 2 -or $tokens[1] -cne 'diagnoza') { continue }
        if ((Get-KolektorOption $tokens '--tryb') -cne 'dlugi') { continue }
        $outArgument = Get-KolektorOption $tokens '--out'
        if ([string]::IsNullOrEmpty($outArgument)) { continue }
        if ((Resolve-KolektorPath $outArgument) -ine $expectedOut) { continue }
        $process
    }
}

function Get-KolektorTask([string]$KolektorRoot) {
    $root = Resolve-KolektorPath $KolektorRoot
    try { $tasks = @(Get-ScheduledTask -ErrorAction Stop | Where-Object { $_.TaskName -eq 'diagnoza-hali-kolektor' }) }
    catch { throw (New-Object NotSupportedException('Nie mozna sprawdzic zadania kolektora. Nie zmieniono Harmonogramu.')) }
    if ($tasks.Count -eq 0) { return $null }
    if ($tasks.Count -ne 1) { throw (New-Object InvalidOperationException('Niejednoznaczne zadanie kolektora. Nie zmieniono Harmonogramu.')) }
    $task = $tasks[0]
    $actions = @($task.Actions)
    $expectedArguments = '/c ""{0}""' -f (Join-Path $root 'pakiet\Kolektor.cmd')
    $validExecute = $false
    if ($actions.Count -eq 1) {
        $execute = [string]$actions[0].Execute
        $validExecute = $execute -ieq 'cmd.exe' -or $execute -ieq (Join-Path $env:SystemRoot 'System32\cmd.exe')
    }
    if ($task.TaskPath -ne '\' -or $actions.Count -ne 1 -or -not $validExecute -or
        $actions[0].Arguments -ine $expectedArguments -or
        [string]::IsNullOrWhiteSpace($actions[0].WorkingDirectory) -or
        (Resolve-KolektorPath $actions[0].WorkingDirectory) -ine (Join-Path $root 'pakiet')) {
        throw (New-Object InvalidOperationException('Zadanie o nazwie kolektora wskazuje inny program lub katalog. Nie zostalo zmienione.'))
    }
    return $task
}

function ConvertFrom-KolektorJson([string]$Json) {
    $object = $Json | ConvertFrom-Json -ErrorAction Stop
    if ($PSVersionTable.PSVersion.Major -ge 6 -and $object -is [Management.Automation.PSCustomObject]) {
        # Pola najwyzszego poziomu (startedUtc, heartbeatUtc) - dokladny tekst z parsera JSON platformy, jak w PS 5.1.
        $document = [Text.Json.JsonDocument]::Parse($Json)
        try {
            foreach ($property in $document.RootElement.EnumerateObject()) {
                if ($property.Value.ValueKind -eq [Text.Json.JsonValueKind]::String) { $object.($property.Name) = $property.Value.GetString() }
            }
        } finally { $document.Dispose() }
    }
    return $object
}

function Read-KolektorSummary([string]$KolektorRoot) {
    $path = Join-Path (Resolve-KolektorPath $KolektorRoot) 'out\dlugi\podsumowanie.json'
    $stream = $null
    $reader = $null
    try {
        # C# podmienia podsumowanie atomowo; czytelnik nie moze blokowac rename/replace.
        $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        $reader = New-Object IO.StreamReader($stream, (New-Object Text.UTF8Encoding($false, $true)), $true)
        return (ConvertFrom-KolektorJson $reader.ReadToEnd())
    }
    catch { throw 'Nie mozna odczytac podsumowania kolektora. Szczegoly pominieto, aby nie ujawnic danych.' }
    finally {
        if ($null -ne $reader) { $reader.Dispose() }
        if ($null -ne $stream) { $stream.Dispose() }
    }
}

function Stop-Kolektor([string]$KolektorRoot, [int]$TimeoutSeconds = 30) {
    $root = Resolve-KolektorPath $KolektorRoot
    $processes = @(Get-KolektorProcess -KolektorRoot $root)
    if ($processes.Count -eq 0) { return $true }
    if ($processes.Count -ne 1) { throw 'Kilka kolektorow zapisuje ten sam katalog. Niczego nie zatrzymano i nie spakowano.' }
    $process = $processes[0]
    $summary = Read-KolektorSummary $root
    $started = [DateTimeOffset]::MinValue
    if ($summary.pid -ne $process.ProcessId -or $summary.zakonczony -ne $false -or
        -not [DateTimeOffset]::TryParse([string]$summary.startedUtc, [ref]$started) -or
        $null -eq $process.CreationDate -or $started.UtcDateTime -lt $process.CreationDate.ToUniversalTime().AddSeconds(-2)) {
        throw 'Podsumowanie nie potwierdza tozsamosci dzialajacego kolektora. Niczego nie zatrzymano.'
    }
    $requestPath = Join-Path $root 'out\dlugi\stop-request.json'
    $temporary = $requestPath + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $request = [ordered]@{pid = [int]$process.ProcessId; startedUtc = [string]$summary.startedUtc} | ConvertTo-Json -Compress
        [IO.File]::WriteAllText($temporary, $request, (New-Object Text.UTF8Encoding($false)))
        for ($attempt = 1; ; $attempt++) {
            try {
                if ([IO.File]::Exists($requestPath)) { [IO.File]::Replace($temporary, $requestPath, [NullString]::Value) }
                else { [IO.File]::Move($temporary, $requestPath) }
                break
            } catch {
                if ($attempt -ge 20) { throw }
                Start-Sleep -Milliseconds 100
            }
        }
    } catch { throw 'Nie zapisano prosby o zakonczenie kolektora. Nie wymuszono zatrzymania.' }
    finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }

    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        $remaining = @(Get-KolektorProcess -KolektorRoot $root -Konczacy $process)
        if ($remaining.Count -eq 0) {
            $final = Read-KolektorSummary $root
            if ($final.pid -eq $process.ProcessId -and $final.startedUtc -ceq $summary.startedUtc -and $final.zakonczony -eq $true) {
                return $true
            }
            throw 'Proces zakonczony, ale brak jego koncowego podsumowania. Nie spakowano niepotwierdzonych danych.'
        }
        if ($remaining.Count -ne 1 -or $remaining[0].ProcessId -ne $process.ProcessId -or $remaining[0].CreationDate -ne $process.CreationDate) {
            throw 'Zmienia sie instancja kolektora. Nie wymuszono zatrzymania i nie spakowano danych.'
        }
        if ($watch.Elapsed.TotalSeconds -ge $TimeoutSeconds) { break }
        Start-Sleep -Milliseconds 200
    } while ($true)
    throw 'Kolektor nie potwierdzil zakonczenia w czasie oczekiwania. Nadal moze dzialac; brak ZIP. Nie zamykaj go sila.'
}
