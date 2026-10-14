# Zadanie co minute wola "MaszynyAlert.App.exe --if-not-running", wiec proces zabity w Menedzerze
# zadan wraca w ciagu minuty. Aplikacja nie ma uprawnien administratora i Windows nie pozwala jej
# zablokowac "Zakoncz zadanie" - powrot po minucie jest jedyna realna ochrona.
#
# Dlaczego przez plik XML i schtasks.exe, a nie przez Register-ScheduledTask:
# instalator uruchamia Windows PowerShell 5.1, ale moze odziedziczyc PSModulePath z PowerShell 7
# (tak samo jak w Initialize-Data.ps1). Modul ScheduledTasks bywa wtedy nie do zaladowania, a
# rejestracja watchdoga cicho przepada. schtasks.exe jest czescia systemu i nie zalezy od modulow.
#
# Dlaczego taka tresc zadania:
# - MultipleInstancesPolicy = IgnoreNew: uruchomiony program JEST instancja zadania i zyje dalej,
#   wiec kolejne wyzwolenia same sie pomijaja, dopoki dziala;
# - ExecutionTimeLimit = PT0S (bez limitu): limit zakonczylby dzialajaca aplikacje po jego uplywie;
# - Repetition bez <Duration>: powtarzanie bez konca.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ApplicationDirectory,
    [string]$TaskName = 'VNC Alert watchdog'
)
$ErrorActionPreference = 'Stop'

$executable = Join-Path $ApplicationDirectory 'MaszynyAlert.App.exe'
if (!(Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Brak pliku programu: $executable"
}

function Escape-Xml([string]$value) {
    return $value.Replace('&', '&amp;').Replace('<', '&lt;').Replace('>', '&gt;').Replace('"', '&quot;')
}

# Start w przeszlosci razem ze StartWhenAvailable: pierwsze sprawdzenie wypada zaraz po instalacji.
$start = (Get-Date).AddMinutes(-1).ToString('yyyy-MM-ddTHH:mm:ss')
$xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Przywraca VNC Alert w ciagu minuty po zamknieciu procesu. Zatrzymanie na stale: komenda "vncalert killapp".</Description>
    <URI>\$(Escape-Xml $TaskName)</URI>
  </RegistrationInfo>
  <Triggers>
    <TimeTrigger>
      <StartBoundary>$start</StartBoundary>
      <Repetition>
        <Interval>PT1M</Interval>
        <StopAtDurationEnd>false</StopAtDurationEnd>
      </Repetition>
      <Enabled>true</Enabled>
    </TimeTrigger>
    <LogonTrigger>
      <Enabled>true</Enabled>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <GroupId>S-1-5-32-545</GroupId>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
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
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>$(Escape-Xml $executable)</Command>
      <Arguments>--if-not-running</Arguments>
      <WorkingDirectory>$(Escape-Xml $ApplicationDirectory)</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
"@

# schtasks.exe wymaga pliku w UTF-16 - inaczej odrzuca XML jako uszkodzony.
$file = Join-Path ([IO.Path]::GetTempPath()) ("vnc-alert-watchdog-" + [Guid]::NewGuid().ToString('N') + ".xml")
try {
    [IO.File]::WriteAllText($file, $xml, [Text.Encoding]::Unicode)
    $output = & "$env:SystemRoot\System32\schtasks.exe" /create /tn $TaskName /xml $file /f 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "schtasks /create zakonczylo sie kodem ${LASTEXITCODE}: $output" }
    Write-Output "Zarejestrowano zadanie '$TaskName'. $($output.Trim())"
}
finally { if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force } }
