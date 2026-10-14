# PS5.1. Potwierdzenie startu wspolne dla runnera i ponownego startu po eksporcie.
function Get-KolektorStartupShortcut {
    param([Parameter(Mandatory=$true)][string]$KolektorRoot)
    $shortcut = Join-Path ([Environment]::GetFolderPath('Startup')) 'diagnoza-hali-kolektor.lnk'
    if (-not (Test-Path -LiteralPath $shortcut)) { return $null }
    $ws = New-Object -ComObject WScript.Shell
    $link = $ws.CreateShortcut($shortcut)
    $package = [IO.Path]::GetFullPath((Join-Path $KolektorRoot 'pakiet'))
    $target = [IO.Path]::GetFullPath((Join-Path $package 'Kolektor.cmd'))
    if (-not [string]::IsNullOrWhiteSpace($link.Arguments) -or
        -not [string]::Equals($link.TargetPath, $target, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals($link.WorkingDirectory, $package, [StringComparison]::OrdinalIgnoreCase)) {
        throw [InvalidOperationException]::new('Pod nazwa kolektora istnieje skrot o innej akcji; nie zmieniam go.')
    }
    return $shortcut
}

function Wait-KolektorStarted {
    param(
        [Parameter(Mandatory=$true)][string]$KolektorRoot,
        [Parameter(Mandatory=$true)][DateTimeOffset]$StartedAfterUtc,
        [ValidateRange(0,300)][int]$TimeoutSeconds = 45,
        [ValidateRange(0,30)][int]$StabilnoscSekund = 3
    )
    $confirmed = $null
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        try {
            # Stan Running zadania ani pozostawiony plik nie dowodza uruchomienia tego przebiegu.
            $processes = @(Get-KolektorProcess -KolektorRoot $KolektorRoot)
            $summary = Read-KolektorSummary -KolektorRoot $KolektorRoot
            $started = [DateTimeOffset]::Parse([string]$summary.startedUtc, [Globalization.CultureInfo]::InvariantCulture)
            $heartbeat = [DateTimeOffset]::Parse([string]$summary.heartbeatUtc, [Globalization.CultureInfo]::InvariantCulture)
            $matching = @($processes | Where-Object { $_.ProcessId -eq $summary.pid })
            if ($processes.Count -eq 1 -and $matching.Count -eq 1 -and $summary.tryb -eq 'dlugi' -and
                $summary.zakonczony -is [bool] -and -not $summary.zakonczony -and
                $started -ge $StartedAfterUtc -and $heartbeat -ge $started -and
                $heartbeat -le [DateTimeOffset]::UtcNow.AddSeconds(5)) {
                $confirmed = $summary
                break
            }
        } catch {
            # Nie wypisujemy surowego JSON ani bledu parsera. Plik moze dopiero powstawac.
        }
        if ($clock.Elapsed.TotalSeconds -ge $TimeoutSeconds) { break }
        Start-Sleep -Milliseconds 250
    } while ($clock.Elapsed.TotalSeconds -lt $TimeoutSeconds)
    if ($null -eq $confirmed) {
        throw 'Nie potwierdzono uruchomienia kolektora: brak jego zywego procesu i swiezego podsumowania. Zachowaj folder wynikow oraz runner.log; nie uruchamiaj kolejnej kopii.'
    }
    for ($i = 0; $i -lt $StabilnoscSekund; $i++) {
        Start-Sleep -Milliseconds 1000
        $alive = @(Get-KolektorProcess -KolektorRoot $KolektorRoot | Where-Object { $_.ProcessId -eq $confirmed.pid })
        $finished = $false
        try { $finished = ((Read-KolektorSummary -KolektorRoot $KolektorRoot).zakonczony -eq $true) } catch { }
        if ($alive.Count -ne 1 -or $finished) {
            throw 'Kolektor zakonczyl sie zaraz po starcie (np. maszyna odrzucila haslo albo brak miejsca). Status maszyn: out\dlugi\podsumowanie.json, przyczyna: out\raport.txt. Nie uruchamiaj kolejnej kopii; zachowaj folder wynikow.'
        }
    }
    return $confirmed
}
