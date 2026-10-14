# Tresc zadania:
# - UserId S-1-5-18 (SYSTEM) + HighestAvailable: zadanie uruchamia instalator bez UAC;
# - bez wyzwalaczy: rusza tylko na zadanie programu (schtasks /run), nigdy samo;
# - akcja: Windows PowerShell 5.1 z katalogu systemowego i skrypt Update-Task.ps1 z Program Files (tylko administratorzy
#   go zmieniaja); argumenty stale, wpisane tutaj - uzytkownik nie przekaze zadaniu niczego poza plikiem zadania, ktory
#   skrypt i program traktuja jako niezaufany;
# - MultipleInstancesPolicy IgnoreNew: najwyzej jedna instancja; ExecutionTimeLimit PT1H (instalator trwa ~10-60 s).
#
# Deskryptor zadania (DACL), ustawiany po rejestracji przez COM Schedule.Service:
#   D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;BU)
# SYSTEM i Administratorzy - pelne prawa; grupa Users (BU) - odczyt i uruchomienie, bez zmiany, wylaczenia i usuniecia.
#
# Dlaczego schtasks.exe /xml + COM, a nie Register-ScheduledTask: ten sam powod co w Register-Watchdog.ps1 (modul
# ScheduledTasks bywa nie do zaladowania przy PSModulePath z PowerShell 7); schtasks.exe nie ustawia deskryptora, wiec
# robi to COM (IRegisteredTask.SetSecurityDescriptor).
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ApplicationDirectory,
    [Parameter(Mandatory)][string]$DataDirectory,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$AppVersion,
    [string]$TaskName = 'VNC Alert update'
)
$ErrorActionPreference = 'Stop'

$ApplicationDirectory = $ApplicationDirectory.TrimEnd('\')
$DataDirectory = $DataDirectory.TrimEnd('\')
$script = Join-Path $ApplicationDirectory 'installer\Update-Task.ps1'
if (!(Test-Path -LiteralPath $script -PathType Leaf)) { throw "Brak skryptu zadania: $script" }
$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$sddl = 'D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;BU)'

function Escape-Xml([string]$value) {
    return $value.Replace('&', '&amp;').Replace('<', '&lt;').Replace('>', '&gt;').Replace('"', '&quot;')
}

# Sciezki bez koncowego ukosnika: '\"' na koncu argumentu zmienia znaczenie cudzyslowu w wierszu polecen.
$arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $script + '" -ApplicationDirectory "' +
    $ApplicationDirectory + '" -DataDirectory "' + $DataDirectory + '" -ExpectedVersion ' + $AppVersion
$xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Instaluje aktualizacje VNC Alert pobrana przez program, po sprawdzeniu pliku ze spisem wydan (bez okna UAC). Uruchamia je program; samo nie rusza.</Description>
    <URI>\$(Escape-Xml $TaskName)</URI>
  </RegistrationInfo>
  <Principals>
    <Principal id="Author">
      <UserId>S-1-5-18</UserId>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT1H</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>$(Escape-Xml $powershell)</Command>
      <Arguments>$(Escape-Xml $arguments)</Arguments>
      <WorkingDirectory>$(Escape-Xml $ApplicationDirectory)</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
"@

# schtasks.exe wymaga pliku w UTF-16 - inaczej odrzuca XML jako uszkodzony.
$file = Join-Path ([IO.Path]::GetTempPath()) ("vnc-alert-update-task-" + [Guid]::NewGuid().ToString('N') + ".xml")
try {
    [IO.File]::WriteAllText($file, $xml, [Text.Encoding]::Unicode)
    $output = & "$env:SystemRoot\System32\schtasks.exe" /create /tn $TaskName /xml $file /f 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "schtasks /create zakonczylo sie kodem ${LASTEXITCODE}: $output" }
}
finally { if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force } }

$service = New-Object -ComObject Schedule.Service
$service.Connect()
$task = $service.GetFolder('\').GetTask($TaskName)
$task.SetSecurityDescriptor($sddl, 0)
$applied = $task.GetSecurityDescriptor(4)
Write-Output "Zarejestrowano zadanie '$TaskName' (SYSTEM, bez wyzwalaczy); deskryptor: $applied"
