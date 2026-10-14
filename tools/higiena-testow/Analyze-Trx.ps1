# Użycie: pwsh -NoProfile -File tools\higiena-testow\Analyze-Trx.ps1 -Dir <katalog z .trx> [-Top 10] [-Json wynik.json]
param(
    [Parameter(Mandatory)] [string] $Dir,
    [int] $Top = 10,
    [string] $Json = ''
)
$ErrorActionPreference = 'Stop'
$ns = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'
$all = New-Object System.Collections.Generic.List[object]
$perFile = New-Object System.Collections.Generic.List[object]
foreach ($f in Get-ChildItem -LiteralPath $Dir -Filter *.trx -Recurse | Sort-Object LastWriteTime) {
    [xml]$x = Get-Content -LiteralPath $f.FullName -Raw -Encoding UTF8
    $nsm = New-Object System.Xml.XmlNamespaceManager $x.NameTable
    $nsm.AddNamespace('t', $ns)
    $defs = @{}
    foreach ($n in $x.SelectNodes('//t:TestDefinitions/t:UnitTest', $nsm)) {
        $defs[$n.id] = [pscustomobject]@{ Storage = $n.storage; Class = $n.TestMethod.className }
    }
    $asm = $null
    $rows = foreach ($n in $x.SelectNodes('//t:Results/t:UnitTestResult', $nsm)) {
        $def = $defs[$n.testId]
        if ($null -eq $asm -and $def) { $asm = [IO.Path]::GetFileNameWithoutExtension($def.Storage) }
        $em = $n.SelectSingleNode('t:Output/t:ErrorInfo/t:Message', $nsm)
        [pscustomobject]@{
            Assembly = if ($def) { [IO.Path]::GetFileNameWithoutExtension($def.Storage) } else { '?' }
            Name     = $n.testName
            Outcome  = $n.outcome
            Duration = if ($n.duration) { [TimeSpan]::Parse($n.duration).TotalSeconds } else { 0.0 }
            Start    = if ($n.startTime) { [DateTimeOffset]::Parse($n.startTime) } else { $null }
            End      = if ($n.endTime) { [DateTimeOffset]::Parse($n.endTime) } else { $null }
            Message  = if ($em) { $em.InnerText } else { $null }
        }
    }
    $rows = @($rows)
    foreach ($row in $rows) { $all.Add($row) }
    $starts = @($rows | Where-Object Start | ForEach-Object Start)
    $ends = @($rows | Where-Object End | ForEach-Object End)
    $wall = if ($starts.Count -and $ends.Count) { (($ends | Measure-Object -Maximum).Maximum - ($starts | Measure-Object -Minimum).Minimum).TotalSeconds } else { 0 }
    $perFile.Add([pscustomobject]@{
        File = $f.Name; Assembly = $asm
        Passed = @($rows | Where-Object Outcome -eq 'Passed').Count
        Failed = @($rows | Where-Object Outcome -eq 'Failed').Count
        Skipped = @($rows | Where-Object Outcome -eq 'NotExecuted').Count
        Total = $rows.Count
        SumTestSeconds = [Math]::Round((($rows | Measure-Object Duration -Sum).Sum), 1)
        WallSeconds = [Math]::Round($wall, 1)
    })
}
"== Projekty"
$perFile | Format-Table -AutoSize | Out-String -Width 220
$tot = [pscustomobject]@{
    Passed = ($perFile | Measure-Object Passed -Sum).Sum; Failed = ($perFile | Measure-Object Failed -Sum).Sum
    Skipped = ($perFile | Measure-Object Skipped -Sum).Sum; Total = ($perFile | Measure-Object Total -Sum).Sum
}
"== Razem: PASS $($tot.Passed) / FAIL $($tot.Failed) / SKIP $($tot.Skipped) / łącznie $($tot.Total)"
"== Najwolniejsze $Top"
$all | Sort-Object Duration -Descending | Select-Object -First $Top | ForEach-Object { '{0,8:N2} s  {1}  {2}' -f $_.Duration, $_.Assembly, $_.Name }
"== Niepowodzenia"
$all | Where-Object Outcome -eq 'Failed' | ForEach-Object {
    "[$($_.Assembly)] $($_.Name)"
    "    " + (($_.Message -split "`n" | Select-Object -First 6) -join "`n    ")
}
"== Pominięte"
$all | Where-Object Outcome -eq 'NotExecuted' | ForEach-Object { "[$($_.Assembly)] $($_.Name)" }
if ($Json) {
    [pscustomobject]@{ Projects = $perFile; Totals = $tot
        Slowest = ($all | Sort-Object Duration -Descending | Select-Object -First 50 Assembly, Name, Duration)
        Failed = ($all | Where-Object Outcome -eq 'Failed' | Select-Object Assembly, Name, Message)
        Skipped = ($all | Where-Object Outcome -eq 'NotExecuted' | Select-Object Assembly, Name) } |
        ConvertTo-Json -Depth 5 | Out-File -LiteralPath $Json -Encoding utf8
}
