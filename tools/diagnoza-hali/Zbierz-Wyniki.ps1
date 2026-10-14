<#
.SYNOPSIS
    Bezpiecznie zamyka wlasny kolektor, pakuje wyniki i wznawia zbieranie (lub usuwa autostart z -Usun).
.DESCRIPTION
    Nie zatrzymuje innych probe.exe. Brak potwierdzonego zamkniecia blokuje ZIP.
    Kod 1 oznacza blad zatrzymania, ZIP, kopii lub ponownego uruchomienia; oryginalny out zawsze zostaje.
#>
param(
    [switch]$Usun,
    [string]$Katalog = (Join-Path $env:LOCALAPPDATA 'diagnoza-hali')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Kolektor-Common.ps1')
. (Join-Path $PSScriptRoot 'Kolektor-Start.ps1')

function Powiedz([string]$Tekst) { Write-Host ('{0} {1}' -f (Get-Date -Format 'HH:mm:ss'), $Tekst) }

function Ukryj-SekretyEksportu($Obiekt) {
    if ($null -eq $Obiekt -or $Obiekt -is [string] -or $Obiekt -is [ValueType]) { return }
    if ($Obiekt -is [Collections.IDictionary]) {
        foreach ($key in @($Obiekt.Keys)) {
            if ($key -eq 'requiresPassword' -and $Obiekt[$key] -is [bool]) { continue }
            if ($key -match '(?i)password|secret|token|hmac|api.?key|^key$') { $Obiekt[$key] = '***' }
            else { Ukryj-SekretyEksportu $Obiekt[$key] }
        }
    } elseif ($Obiekt -is [Collections.IEnumerable]) {
        foreach ($item in $Obiekt) { Ukryj-SekretyEksportu $item }
    } else {
        foreach ($property in @($Obiekt.PSObject.Properties)) {
            if ($property.Name -eq 'requiresPassword' -and $property.Value -is [bool]) { continue }
            if ($property.Name -match '(?i)password|secret|token|hmac|api.?key|^key$') { $property.Value = '***' }
            else { Ukryj-SekretyEksportu $property.Value }
        }
    }
}

function ConvertTo-KolektorSafeJson([string]$Json) {
    try {
        # Komentarze konfiguracji dopuszczone tylko w osobnych wierszach, jak w runnerze.
        $clean = [regex]::Replace($Json, '(?m)^\s*//[^\r\n]*', '')
        if ([string]::IsNullOrWhiteSpace($clean)) { throw 'Brak JSON.' }
        if ($clean.TrimStart().StartsWith('[')) { $object = @(ConvertFrom-Json -InputObject $clean -ErrorAction Stop) }
        else { $object = ConvertFrom-Json -InputObject $clean -ErrorAction Stop }
        Ukryj-SekretyEksportu $object
        return (ConvertTo-Json -InputObject $object -Depth 100 -Compress -ErrorAction Stop)
    } catch { throw 'Niepoprawny JSON w wynikach. Nie wyeksportowano surowej tresci ani bledu parsera.' }
}

function Get-KolektorExportFiles([string]$Directory) {
    foreach ($item in Get-ChildItem -LiteralPath $Directory -Force -ErrorAction Stop) {
        # Nie podazamy za dowiazaniem poza katalog wyniku.
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Wyniki zawieraja dowiazanie. ZIP nie powstal; dane zrodlowe zostaly.' }
        if ($item.Name -imatch '^(secrets\.dat|secrets\.lock|hasla\.dpapi)$') { continue }
        if ($item.PSIsContainer) { Get-KolektorExportFiles $item.FullName }
        else { $item }
    }
}

function New-KolektorZip([string]$Out, [string]$Zip, [switch]$Incomplete, [switch]$SkipSummary) {
    $root = (Resolve-KolektorPath $Out) + '\'
    $target = [IO.Path]::GetFullPath($Zip)
    if ($target.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw 'ZIP musi znajdowac sie poza katalogiem out.' }
    $temporary = $target + '.' + [guid]::NewGuid().ToString('N') + '.partial'
    $archive = $null
    $fileStream = $null
    try {
        Add-Type -AssemblyName System.IO.Compression -ErrorAction Stop
        Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop
        $fileStream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew)
        $archive = New-Object IO.Compression.ZipArchive($fileStream, [IO.Compression.ZipArchiveMode]::Create, $true)
        foreach ($file in Get-KolektorExportFiles $Out) {
            $name = $file.FullName.Substring($root.Length).Replace('\', '/')
            if ($SkipSummary -and $name -ieq 'dlugi/podsumowanie.json') { continue }
            if ($Incomplete -and $name -ieq 'eksport-kolektora-status.txt') { continue }
            $entry = $archive.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
            $destination = $entry.Open()
            try {
                if ($file.Extension -ieq '.json' -or $file.Extension -ieq '.jsonl') {
                    $reader = New-Object IO.StreamReader($file.FullName, (New-Object Text.UTF8Encoding($false, $true)), $true)
                    $writer = New-Object IO.StreamWriter($destination, (New-Object Text.UTF8Encoding($false)))
                    try {
                        if ($file.Extension -ieq '.jsonl') {
                            while (($line = $reader.ReadLine()) -ne $null) {
                                if (-not [string]::IsNullOrWhiteSpace($line)) { $writer.WriteLine((ConvertTo-KolektorSafeJson $line)) }
                            }
                        } else { $writer.Write((ConvertTo-KolektorSafeJson ($reader.ReadToEnd()))) }
                    } finally { $reader.Dispose(); $writer.Dispose() }
                } else {
                    $source = [IO.File]::OpenRead($file.FullName)
                    try { $source.CopyTo($destination) } finally { $source.Dispose() }
                }
            } finally { $destination.Dispose() }
        }
        if ($Incomplete) {
            $entry = $archive.CreateEntry('eksport-kolektora-status.txt')
            $writer = New-Object IO.StreamWriter($entry.Open(), (New-Object Text.UTF8Encoding($true)))
            try { $writer.WriteLine('WYNIK NIEPELNY: brak potwierdzenia zakonczenia ostatniego kolektora. Zebrano istniejace dane; ostatni epizod moze byc niedomkniety. Brakujace lub uszkodzone podsumowanie nie jest dowodem konca. Nie zatwierdzaj czasu epizodow bez dalszej analizy.') }
            finally { $writer.Dispose() }
        }
        $archive.Dispose(); $archive = $null
        $fileStream.Dispose(); $fileStream = $null
        # Nowa nazwa zawiera sekundy i losowy sufiks: nie kasujemy poprzedniego archiwum.
        [IO.File]::Move($temporary, $target)
        return $target
    } catch { throw 'Nie utworzono kompletnego ZIP-a (odczyt, JSON, dostep lub miejsce na dysku). Oryginalny katalog out zostal bez zmian.' }
    finally {
        if ($null -ne $archive) { $archive.Dispose() }
        if ($null -ne $fileStream) { $fileStream.Dispose() }
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}

function Show-KolektorEpisodes([string]$Out) {
    $directory = Join-Path $Out 'dlugi'
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) { Powiedz 'Brak danych trybu dlugiego.'; return }
    Powiedz '=== Epizody (czerwien/zolc) zebrane przez kolektor ==='
    $total = 0
    foreach ($file in Get-ChildItem -LiteralPath $directory -Filter 'epizody.jsonl' -Recurse -ErrorAction Stop) {
        $count = 0
        $reader = New-Object IO.StreamReader($file.FullName, (New-Object Text.UTF8Encoding($false, $true)), $true)
        try {
            while (($line = $reader.ReadLine()) -ne $null) {
                if ([string]::IsNullOrWhiteSpace($line)) { continue }
                $count++
                if ($count -le 5) {
                    try {
                        $item = (ConvertTo-KolektorSafeJson $line) | ConvertFrom-Json -ErrorAction Stop
                        $ocr = '-'
                        if ($item.tekstOcr -is [string]) { $ocr = $item.tekstOcr.Substring(0, [Math]::Min(80, $item.tekstOcr.Length)) }
                        $bbox = '-'
                        if ($item.bbox) { $bbox = '{0},{1},{2},{3}' -f $item.bbox.x, $item.bbox.y, $item.bbox.width, $item.bbox.height }
                        Powiedz ('    {0} {1} {2}s bbox={3} OCR: {4}' -f $item.poczatek, $item.kolor, $item.trwanieSek, $bbox, $ocr)
                    } catch { Powiedz '    Nie odczytano rekordu epizodu. Surowa tresc zostala pominieta.' }
                }
            }
        } finally { $reader.Dispose() }
        $total += $count
        Powiedz ('{0}: {1} epizodow' -f (Split-Path -Leaf $file.DirectoryName), $count)
    }
    Powiedz ('Razem epizodow: {0}' -f $total)
    $summaryPath = Join-Path $directory 'podsumowanie.json'
    if (Test-Path -LiteralPath $summaryPath -PathType Leaf) {
        try {
            $summary = (ConvertTo-KolektorSafeJson (Get-Content -LiteralPath $summaryPath -Encoding UTF8 -Raw -ErrorAction Stop)) | ConvertFrom-Json -ErrorAction Stop
            foreach ($machine in $summary.maszyny.PSObject.Properties) {
                Powiedz ('{0}: status {1}, tryb {2}, odczytow {3}, zmian {4}, epizodow {5}, dysk {6} MB' -f
                    $machine.Value.nazwa, $machine.Value.status, $machine.Value.tryb, $machine.Value.odczytow,
                    $machine.Value.zmian, $machine.Value.epizodow.razem, $machine.Value.dyskMb)
            }
        } catch { Powiedz 'Nie odczytano podsumowania. Surowa tresc i blad parsera zostaly pominiete.' }
    }
}

function Copy-KolektorZip([string]$Zip, [string[]]$Targets) {
    $failed = 0
    foreach ($targetDirectory in $Targets) {
        try {
            $target = Join-Path $targetDirectory (Split-Path -Leaf $Zip)
            if ([IO.Path]::GetFullPath($target) -ine [IO.Path]::GetFullPath($Zip)) {
                Copy-Item -LiteralPath $Zip -Destination $target -Force -ErrorAction Stop
            }
            if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or (Get-Item -LiteralPath $target -ErrorAction Stop).Length -ne (Get-Item -LiteralPath $Zip -ErrorAction Stop).Length) {
                throw 'Nie potwierdzono kopii.'
            }
            Powiedz ('kopia: {0}' -f $target)
        } catch { $failed++; Powiedz ('Nie udalo sie skopiowac ZIP-a do: {0}. Lokalny ZIP zostal zachowany.' -f $targetDirectory) }
    }
    return $failed
}

function Get-KolektorCopyTargets {
    $targets = New-Object 'System.Collections.Generic.List[string]'
    $desktop = [Environment]::GetFolderPath('Desktop')
    if (-not [string]::IsNullOrWhiteSpace($desktop)) { $targets.Add($desktop) }
    foreach ($volume in Get-Volume -ErrorAction Stop | Where-Object { $_.DriveType -eq 'Removable' -and $_.DriveLetter }) {
        $targets.Add(('{0}:\' -f $volume.DriveLetter))
    }
    return $targets.ToArray()
}

function Remove-KolektorAutostart([string]$KolektorRoot) {
    $task = Get-KolektorTask -KolektorRoot $KolektorRoot
    $shortcut = Get-KolektorStartupShortcut $KolektorRoot
    if ($null -ne $task) { Unregister-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath -Confirm:$false -ErrorAction Stop }
    if ($null -ne $shortcut) { Remove-Item -LiteralPath $shortcut -Force -ErrorAction Stop }
    $passwords = Join-Path (Resolve-KolektorPath $KolektorRoot) 'hasla.dpapi'
    if (Test-Path -LiteralPath $passwords) { Remove-Item -LiteralPath $passwords -Force -ErrorAction Stop }
    Powiedz 'Autostart kolektora usuniety. Dane w out i ZIP zostaja.'
}

function Restart-Kolektor([string]$KolektorRoot) {
    $root = Resolve-KolektorPath $KolektorRoot
    # Nieczytelny Harmonogram nie dowodzi, ze nie ma juz drugiego sposobu startu.
    $task = Get-KolektorTask -KolektorRoot $root
    if ($null -ne $task) {
        # cmd /wait potrzebuje chwili po lagodnym koncu probe. Nie konczymy zadania sila.
        for ($attempt = 0; $task.State -eq 'Running' -and $attempt -lt 15; $attempt++) {
            Start-Sleep -Milliseconds 200
            $task = Get-KolektorTask -KolektorRoot $root
        }
        if ($null -eq $task -or $task.State -eq 'Running') { throw 'Poprzednie zadanie jeszcze sie konczy. Kolektor nie zostal wznowiony.' }
    }
    $startedAfter = [DateTimeOffset]::UtcNow
    if ($null -ne $task) { Start-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath -ErrorAction Stop }
    else {
        $command = Join-Path $root 'pakiet\Kolektor.cmd'
        if (-not (Test-Path -LiteralPath $command -PathType Leaf)) { throw 'Brak pliku startowego kolektora.' }
        Start-Process -FilePath 'cmd.exe' -ArgumentList ('/c ""{0}""' -f $command) -WorkingDirectory (Split-Path -Parent $command) -WindowStyle Hidden -ErrorAction Stop
    }
    $null = Wait-KolektorStarted -KolektorRoot $root -StartedAfterUtc $startedAfter
    Powiedz 'Potwierdzono ponowna prace kolektora. Koniec zbierania: Zbierz-Wyniki.cmd -Usun.'
}

function Invoke-KolektorExport([string]$KolektorRoot, [switch]$Remove) {
    $ErrorActionPreference = 'Stop'
    $root = Resolve-KolektorPath $KolektorRoot
    $out = Join-Path $root 'out'
    try {
        Powiedz 'Prosze wlasny kolektor o zapis i zakonczenie...'
        $null = Stop-Kolektor -KolektorRoot $root
    } catch { Powiedz 'Nie potwierdzono bezpiecznego zakonczenia kolektora. Brak ZIP; niczego nie zabito ani nie usunieto.'; return 1 }
    if (-not (Test-Path -LiteralPath $out -PathType Container)) { Powiedz ('Brak danych: {0}. ZIP nie powstal.' -f $out); return 1 }
    $exitCode = 0
    $incomplete = $false
    $skipSummary = $false
    try {
        $lastSummary = Read-KolektorSummary $root
        $incomplete = -not ($lastSummary.zakonczony -is [bool] -and $lastSummary.zakonczony)
    } catch {
        $incomplete = $true
        # Uszkodzonego JSON nie dolaczamy w surowej postaci; reszta zebranych danych nadal trafia do ZIP.
        $skipSummary = $true
    }
    if ($incomplete) {
        $exitCode = 1
        Powiedz 'WYNIK NIEPELNY: brak potwierdzenia zakonczenia ostatniego kolektora. ZIP obejmie istniejace dane; ostatni epizod moze byc niedomkniety.'
    }
    $zip = $null
    try {
        $fileName = 'diagnoza-hali-kolektor-{0}-{1}-{2}.zip' -f $env:COMPUTERNAME, (Get-Date -Format 'yyyyMMdd-HHmmss'), ([guid]::NewGuid().ToString('N').Substring(0, 6))
        $zip = New-KolektorZip -Out $out -Zip (Join-Path $root $fileName) -Incomplete:$incomplete -SkipSummary:$skipSummary
        Powiedz ('ZIP: {0} ({1:N1} MB)' -f $zip, ((Get-Item -LiteralPath $zip).Length / 1MB))
        Show-KolektorEpisodes $out
        try { $targets = @(Get-KolektorCopyTargets) }
        catch { $targets = @([Environment]::GetFolderPath('Desktop')); $exitCode = 1; Powiedz 'Nie odczytano listy pendrive. Skopiuj lokalny ZIP recznie.' }
        if ((Copy-KolektorZip -Zip $zip -Targets $targets) -gt 0) { $exitCode = 1 }
    } catch { $exitCode = 1; Powiedz ('Eksport nie jest kompletny. Zachowaj katalog: {0}. Szczegoly bledu pominieto, aby nie ujawnic danych.' -f $out) }
    if ($Remove) {
        # Usuwanie autostartu/hasel dopiero po zapisaniu ZIP-a i wszystkich wykrytych kopii.
        if ($exitCode -eq 0 -and $null -ne $zip) {
            try { Remove-KolektorAutostart $root }
            catch { $exitCode = 1; Powiedz 'Nie usunieto wszystkich elementow autostartu. ZIP i dane zostaly; sprawdz wynik przed wylogowaniem.' }
        } else { Powiedz 'Nie usunieto autostartu ani hasel po bledzie eksportu. Dane pozostaly na komputerze.' }
    } else {
        try { Restart-Kolektor $root }
        catch { $exitCode = 1; Powiedz 'Nie potwierdzono ponownej pracy kolektora. Wyniki zostaly; zbieranie moze byc zatrzymane.' }
    }
    return $exitCode
}

if ($MyInvocation.InvocationName -ne '.') {
    $ErrorActionPreference = 'Stop'
    try { exit (Invoke-KolektorExport -KolektorRoot $Katalog -Remove:$Usun) }
    catch { Powiedz 'Eksport przerwany. Zachowaj katalog wynikow; nie usuwaj danych.'; exit 1 }
}
