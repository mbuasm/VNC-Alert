#ifndef PublishDir
  #define PublishDir "..\publish\app"
#endif
#ifndef OutputPath
  #define OutputPath "..\artifacts\installer"
#endif
#ifndef OutputBaseName
  #define OutputBaseName "VncAlert-Setup"
#endif
#ifndef AppDirName
  #define AppDirName "VNC Alert"
#endif
#define AppVersion GetFileVersion(PublishDir + "\MaszynyAlert.App.exe")

[Setup]
AppId={{DBFA5243-64F6-49C5-9FDC-89B197AD78F9}
AppName=VNC Alert
AppVersion={#AppVersion}
; Zasób wersji samego VncAlert-Setup.exe (Właściwości pliku → Szczegóły): wersja, opis, produkt i firma.
VersionInfoVersion={#AppVersion}
VersionInfoDescription=VNC Alert - instalator
VersionInfoProductName=VNC Alert
AppPublisher=VNC Alert
; Nazwa w Dodaj/usuń programy (AppId bez zmian — to ta sama aplikacja, aktualizacja istniejącej instalacji).
UninstallDisplayName=VNC Alert
DefaultDirName={autopf}\{#AppDirName}
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir={#OutputPath}
OutputBaseFilename={#OutputBaseName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\MaszynyAlert.App\Assets\VncAlert.ico
UninstallDisplayIcon={app}\MaszynyAlert.App.exe
AppMutex=Global\MaszynyAlert.App,Global\MaszynyAlert.Setup
CloseApplications=no
RestartApplications=no
SetupLogging=yes
#ifdef SignTool
; Podpis Authenticode instalatora i deinstalatora: komende "vncalert" definiuje
; Build-Installer.ps1 przez /S (scripts/Sign-Files.ps1, sekrety CODESIGN_*).
SignTool=vncalert $f
SignedUninstaller=yes
SignToolRetryCount=3
#endif
; Instalator dopisuje katalog programu do PATH (komenda "vncalert killapp").
ChangesEnvironment=yes

[Languages]
Name: "polish"; MessagesFile: "compiler:Languages\Polish.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "config.json,rules.json,secrets.dat,history.csv,downtime.jsonl,alarm-instructions.json*,inspiracje\*,out z produkcji\*,logs\*,captures\*,test-ekran\*,state\*,smoke-test-output\*,sounds\*"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\sounds\*"; DestDir: "{app}\sounds"; Flags: onlyifdoesntexist
Source: "Update-Task.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
Source: "Register-UpdateTask.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
Source: "Initialize-Data.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
Source: "Register-Watchdog.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
; skipifsourcedoesntexist: instalator bazowy w teście CI buduje się z paczki starszej wersji, która tego pliku nie ma.
Source: "{#PublishDir}\test-ekran\CZYTAJ-TO.txt"; DestDir: "{commonappdata}\MaszynyAlert\test-ekran"; Flags: ignoreversion uninsneveruninstall skipifsourcedoesntexist; Check: CanWriteTestScreenReadme

[Registry]
; Pierwsza instalacja włącza autostart. Aktualizacja zachowuje także jego wyłączenie.
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MaszynyAlert"; ValueData: """{app}\MaszynyAlert.App.exe"""; Flags: uninsdeletevalue; Check: IsFirstInstall

[Icons]
Name: "{commonprograms}\VNC Alert"; Filename: "{app}\MaszynyAlert.App.exe"; Parameters: "--preview"; WorkingDir: "{app}"; IconFilename: "{app}\MaszynyAlert.App.exe"
Name: "{commondesktop}\VNC Alert"; Filename: "{app}\MaszynyAlert.App.exe"; Parameters: "--preview"; WorkingDir: "{app}"; IconFilename: "{app}\MaszynyAlert.App.exe"

[InstallDelete]
; Usuń skróty poprzednich nazw podczas aktualizacji tej samej aplikacji.
Type: files; Name: "{commonprograms}\FrezGuard.lnk"
Type: files; Name: "{commonprograms}\Maszyny-alert.lnk"

[Run]
Filename: "{app}\MaszynyAlert.App.exe"; Description: "Uruchom VNC Alert"; Flags: postinstall nowait skipifsilent runasoriginaluser; Check: DataReady

[Code]
const
  AppMutexName = 'Global\MaszynyAlert.App';
  EnvironmentKey = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';
  AppFolderCurrent = 'VNC Alert';
  AppFolderPrevious = 'MaszynyAlert';
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{DBFA5243-64F6-49C5-9FDC-89B197AD78F9}_is1';
  WatchdogTaskName = 'VNC Alert watchdog';

var
  FirstInstall: Boolean;
  DataInitialized: Boolean;
  StopRecorded: Boolean;
  AppWasRunning: Boolean;
  MarkerExistedBefore: Boolean;
  InstallSucceeded: Boolean;
  UninstallDone: Boolean;
  SystemChecked: Boolean;
  SystemSetup: Boolean;

function DataReady: Boolean;
begin
  Result := DataInitialized;
end;

procedure AddToPath(Directory: String);
var
  Path: String;
begin
  if not RegQueryStringValue(HKLM, EnvironmentKey, 'Path', Path) then Path := '';
  if Pos(';' + Uppercase(Directory) + ';', ';' + Uppercase(Path) + ';') > 0 then Exit;
  while (Length(Path) > 0) and (Copy(Path, Length(Path), 1) = ';') do
    Delete(Path, Length(Path), 1);
  if Path = '' then Path := Directory else Path := Path + ';' + Directory;
  if RegWriteExpandStringValue(HKLM, EnvironmentKey, 'Path', Path) then
    Log('VNC Alert: dopisano katalog programu do PATH.')
  else
    Log('VNC Alert: NIE udalo sie dopisac katalogu programu do PATH.');
end;

procedure RemoveFromPath(Directory: String);
var
  Path: String;
  Position, Size: Integer;
begin
  if not RegQueryStringValue(HKLM, EnvironmentKey, 'Path', Path) then Exit;
  { Pozycja w dopełnionym łańcuchu ';Path;' wskazuje od razu początek katalogu w samym Path. }
  Position := Pos(';' + Uppercase(Directory) + ';', ';' + Uppercase(Path) + ';');
  if Position = 0 then Exit;
  Size := Length(Directory);
  if Copy(Path, Position + Size, 1) = ';' then
    Delete(Path, Position, Size + 1)
  else if (Position > 1) and (Copy(Path, Position - 1, 1) = ';') then
    Delete(Path, Position - 1, Size + 1)
  else
    Delete(Path, Position, Size);
  if RegWriteExpandStringValue(HKLM, EnvironmentKey, 'Path', Path) then
    Log('VNC Alert: usunieto katalog programu z PATH.')
  else
    Log('VNC Alert: NIE udalo sie zapisac PATH bez katalogu programu.');
end;

function WaitForApplicationExit(Seconds: Integer): Boolean;
var
  Waited: Integer;
begin
  Waited := 0;
  while CheckForMutexes(AppMutexName) do
  begin
    if Waited >= Seconds * 1000 then
    begin
      Result := False;
      Exit;
    end;
    Sleep(200);
    Waited := Waited + 200;
  end;
  Result := True;
end;

function RunningAsSystem: Boolean;
var
  Output: TExecOutput;
  ResultCode: Integer;
begin
  if not SystemChecked then
  begin
    SystemChecked := True;
    SystemSetup := True;
    try
      if ExecAndCaptureOutput(ExpandConstant('{sys}\whoami.exe'), '/user /fo csv /nh', '', SW_HIDE,
        ewWaitUntilTerminated, ResultCode, Output) and (ResultCode = 0) and (GetArrayLength(Output.StdOut) > 0) and
        (Pos('"S-1-5-', Output.StdOut[0]) > 0) then
        SystemSetup := Pos('"S-1-5-18"', Output.StdOut[0]) > 0
      else
        Log('VNC Alert: nie ustalono konta instalatora (whoami.exe) - przyjeto SYSTEM: bez zapisu i kasowania w katalogach operatora.');
    except
      Log('VNC Alert: blad odczytu konta instalatora (whoami.exe) - przyjeto SYSTEM: ' + GetExceptionMessage + '.');
    end;
    Log('VNC Alert: instalator dziala jako SYSTEM: ' + IntToStr(Ord(SystemSetup)) + '.');
  end;
  Result := SystemSetup;
end;

function StopMarkerPath: String;
begin
  if RunningAsSystem then
    Result := ExpandConstant('{commonappdata}\MaszynyAlert\updates-admin\stop.requested')
  else
    Result := ExpandConstant('{commonappdata}\MaszynyAlert\state\stop.requested');
end;

{ FILE_ATTRIBUTE_REPARSE_POINT samego wpisu (FindFirst bez maski nie wchodzi w przekierowanie). }
function IsReparsePoint(const Path: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(Path, FindRec) then
  try
    Result := (FindRec.Attributes and $400) <> 0;
  finally
    FindClose(FindRec);
  end;
end;

function CanWriteTestScreenReadme: Boolean;
begin
  Result := False;
  if RunningAsSystem then
    Log('VNC Alert: CZYTAJ-TO.txt pominiety - instalator jako SYSTEM nie pisze w katalogach operatora.')
  else if IsReparsePoint(ExpandConstant('{commonappdata}\MaszynyAlert')) or
    IsReparsePoint(ExpandConstant('{commonappdata}\MaszynyAlert\test-ekran')) then
    Log('VNC Alert: CZYTAJ-TO.txt pominiety - katalog danych albo test-ekran jest przekierowaniem.')
  else
    Result := True;
end;

function InstalledApplicationDirectory: String;
var
  Previous: String;
begin
  if RegQueryStringValue(HKLM, UninstallKey, 'Inno Setup: App Path', Previous) and (Previous <> '') then
  begin
    Result := RemoveBackslash(Previous);
    Exit;
  end;
  Result := ExpandConstant('{autopf}\' + AppFolderPrevious);
  if FileExists(Result + '\MaszynyAlert.App.exe') then Exit;
  Result := ExpandConstant('{autopf}\' + AppFolderCurrent);
end;

function ApplicationExecutable: String;
begin
  Result := InstalledApplicationDirectory + '\MaszynyAlert.App.exe';
end;

function StopRunningApplication: Boolean;
var
  Executable: String;
  ResultCode: Integer;
begin
  Result := True;
  Executable := ApplicationExecutable;
  if not StopRecorded then
  begin
    { Pierwsze wywołanie zapamiętuje stan sprzed instalatora: czy program działał i czy znacznik ciszy
      watchdoga już był (wtedy założył go operator komendą killapp i zostaje). }
    StopRecorded := True;
    AppWasRunning := CheckForMutexes(AppMutexName);
    MarkerExistedBefore := FileExists(StopMarkerPath);
    Log('VNC Alert: przed instalacja program dzialal: ' + IntToStr(Ord(AppWasRunning)) +
      ', znacznik zatrzymania byl: ' + IntToStr(Ord(MarkerExistedBefore)) + '.');
  end;
  if FileExists(Executable) then
  begin
    { Ta sama droga, co "vncalert killapp": program zamyka się sam i zapisuje swoje dane.
      Wołamy JUŻ BEZ działającego programu, bo "--stop" zostawia też znacznik ciszy dla watchdoga
      z Harmonogramu zadań — bez niego zadanie wznowiłoby aplikację w trakcie podmiany plików. }
    Exec(Executable, '--stop', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if WaitForApplicationExit(2) then Exit;
  end;
  if not CheckForMutexes(AppMutexName) then Exit;
  { Awaryjnie: instalator ma uprawnienia administratora i kończy proces, który nie odpowiada
    albo został uruchomiony z innego katalogu (paczka portable). }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM MaszynyAlert.App.exe /T', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode);
  Result := WaitForApplicationExit(10);
end;

procedure RestoreMonitoring(StartApplication: Boolean);
var
  ResultCode: Integer;
begin
  if not StopRecorded then Exit;
  if (not MarkerExistedBefore) or AppWasRunning then
  begin
    if FileExists(StopMarkerPath) and DeleteFile(StopMarkerPath) then
      Log('VNC Alert: usunieto znacznik zatrzymania zalozony przez instalator: ' + StopMarkerPath + '.');
  end;
  if StartApplication and AppWasRunning and RunningAsSystem then
  begin
    Log('VNC Alert: instalacja nie doszla do skutku; instalator dziala jako SYSTEM - programu nie uruchamia, ' +
      'wraca przez pomocnika operatora albo watchdog (do minuty).');
    Exit;
  end;
  if StartApplication and AppWasRunning and FileExists(ApplicationExecutable) then
  begin
    if ExecAsOriginalUser(ApplicationExecutable, '--if-not-running', ExtractFileDir(ApplicationExecutable),
      SW_SHOWNORMAL, ewNoWait, ResultCode) then
      Log('VNC Alert: instalacja nie doszla do skutku - program uruchomiony ponownie.')
    else
      Log('VNC Alert: NIE udalo sie uruchomic programu po nieudanej instalacji (kod ' + IntToStr(ResultCode) + ').');
  end;
end;

function FindCrashedProcess(const Executable: String): Integer;
var
  Locator, Service, Items, Item: Variant;
  I: Integer;
  Path: String;
begin
  Result := 0;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Items := Service.ExecQuery('SELECT ProcessId, ExecutablePath, ThreadCount FROM Win32_Process WHERE Name = ''MaszynyAlert.App.exe''');
    for I := 0 to Items.Count - 1 do
    begin
      Item := Items.ItemIndex(I);
      if not VarIsNull(Item.ExecutablePath) then
      begin
        Path := Item.ExecutablePath;
        if (CompareText(Path, Executable) = 0) and (Item.ThreadCount = 0) then
        begin
          Result := Item.ProcessId;
          Exit;
        end;
      end;
    end;
  except
    Log('VNC Alert: nie udalo sie sprawdzic procesow (WMI): ' + GetExceptionMessage);
  end;
end;

function InitializeSetup: Boolean;
begin
  Result := StopRunningApplication;
  if not Result then
    SuppressibleMsgBox('Nie udało się zatrzymać działającego VNC Alert. Zatrzymaj go komendą ' +
      '"vncalert killapp" i uruchom instalację ponownie.', mbCriticalError, MB_OK, IDOK);
end;

function InitializeUninstall: Boolean;
begin
  Result := StopRunningApplication;
  if not Result then
    SuppressibleMsgBox('Nie udało się zatrzymać działającego VNC Alert. Zatrzymaj go komendą ' +
      '"vncalert killapp" i uruchom odinstalowanie ponownie.', mbCriticalError, MB_OK, IDOK);
end;

procedure RemoveWatchdogTask;
var
  ResultCode: Integer;
begin
  { Zadanie nalezy do Harmonogramu, a nie do plikow programu, wiec odinstalowanie musi je usunac samo.
    Brak zadania to nie blad: schtasks konczy sie wtedy kodem innym niz 0 i tylko o tym zapisujemy. }
  if Exec(ExpandConstant('{sys}\schtasks.exe'),
    '/delete /tn "' + WatchdogTaskName + '" /f', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
    and (ResultCode = 0) then
    Log('VNC Alert: usunieto zadanie watchdoga z Harmonogramu zadan.')
  else
    Log('VNC Alert: zadanie watchdoga nie zostalo usuniete (moglo nie istniec).');
end;

const
  UpdateTaskName = 'VNC Alert update';

procedure RemoveUpdateTask;
var
  ResultCode: Integer;
begin
  if Exec(ExpandConstant('{sys}\schtasks.exe'),
    '/delete /tn "' + UpdateTaskName + '" /f', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
    and (ResultCode = 0) then
    Log('VNC Alert: usunieto zadanie aktualizacji z Harmonogramu zadan.')
  else
    Log('VNC Alert: zadanie aktualizacji nie zostalo usuniete (moglo nie istniec).');
end;

procedure RegisterUpdateTask;
var
  Arguments: String;
  ResultCode: Integer;
begin
  Arguments := ExpandConstant('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' +
    '"{app}\installer\Register-UpdateTask.ps1" -ApplicationDirectory "{app}" -DataDirectory "{commonappdata}\MaszynyAlert" ' +
    '-AppVersion {#AppVersion}');
  if ExecAndLogOutput(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Arguments,
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode, nil) and (ResultCode = 0) then
  begin
    Log('VNC Alert: zarejestrowano zadanie aktualizacji w Harmonogramie zadan.');
    Exit;
  end;
  Log('VNC Alert: NIE udalo sie zarejestrowac zadania aktualizacji - aktualizacje beda wymagaly UAC.');
  RemoveUpdateTask;
  SuppressibleMsgBox('Nie udało się zarejestrować zadania „VNC Alert update” w Harmonogramie zadań. ' +
    'Program działa normalnie, a aktualizacje będą wymagały potwierdzenia w oknie Windows (UAC). ' +
    'Szczegóły są w dzienniku instalatora.', mbInformation, MB_OK, IDOK);
end;

<event('CurStepChanged')>
procedure UpdateTaskCurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssDone then
  begin
    if not DataReady then
      Log('VNC Alert: dane nie zostaly przygotowane - zadanie aktualizacji nie zostanie zarejestrowane.');
    if DataReady then
      RegisterUpdateTask
    else
      RemoveUpdateTask;
  end;
end;

<event('CurUninstallStepChanged')>
procedure UpdateTaskCurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RemoveUpdateTask;
end;

procedure RegisterWatchdogTask;
var
  Arguments: String;
  ResultCode: Integer;
begin
  Arguments := ExpandConstant('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' +
    '"{app}\installer\Register-Watchdog.ps1" -ApplicationDirectory "{app}"');
  if ExecAndLogOutput(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Arguments,
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode, nil) and (ResultCode = 0) then
  begin
    Log('VNC Alert: zarejestrowano zadanie watchdoga w Harmonogramie zadan.');
    Exit;
  end;
  { Brak watchdoga nie psuje monitoringu — program dziala dalej, tylko nie wraca sam po zabiciu
    procesu. Instalacja konczy sie powodzeniem, a administrator dostaje jasna informacje. }
  Log('VNC Alert: NIE udalo sie zarejestrowac zadania watchdoga.');
  SuppressibleMsgBox('Nie udało się zarejestrować zadania „VNC Alert watchdog” w Harmonogramie zadań. ' +
    'Program będzie działał normalnie, ale po zamknięciu procesu nie wróci sam. ' +
    'Szczegóły są w dzienniku instalatora.', mbInformation, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    UninstallDone := True;
    RemoveWatchdogTask;
  end;
  if CurUninstallStep = usPostUninstall then
  begin
    RemoveFromPath(ExpandConstant('{app}'));
    if FileExists(StopMarkerPath) and DeleteFile(StopMarkerPath) then
      Log('VNC Alert: usunieto znacznik zatrzymania po odinstalowaniu: ' + StopMarkerPath + '.');
  end;
end;

procedure DeinitializeSetup;
begin
  RestoreMonitoring(not InstallSucceeded);
end;

procedure DeinitializeUninstall;
begin
  { Odinstalowanie anulowane po zatrzymaniu programu: znacznik znika, więc watchdog wznowi program w ciągu minuty
    (ExecAsOriginalUser nie działa w deinstalatorze, a zwykłe Exec uruchomiłoby program z uprawnieniami administratora). }
  if not UninstallDone then RestoreMonitoring(False);
end;

function GetCustomSetupExitCode: Integer;
begin
  if DataInitialized then Result := 0 else Result := 1;
end;

function IsFirstInstall: Boolean;
begin
  Result := FirstInstall;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExistingVersion, NewVersion: Int64;
  CrashedProcess, ResultCode: Integer;
  AppDir: String;
begin
  Result := '';
  { Program mógł zostać uruchomiony już po starcie instalatora; AppMutex jest sprawdzany także tutaj. }
  if not StopRunningApplication then
  begin
    Result := 'Nie udało się zatrzymać działającego VNC Alert. Zatrzymaj go komendą "vncalert killapp".';
    Exit;
  end;
  CrashedProcess := FindCrashedProcess(ApplicationExecutable);
  Log('VNC Alert: kontrola procesu po awarii: ' + IntToStr(CrashedProcess) + ' (0 = brak).');
  if CrashedProcess <> 0 then
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /PID ' + IntToStr(CrashedProcess), '', SW_HIDE,
      ewWaitUntilTerminated, ResultCode);
    Sleep(1000);
    if FindCrashedProcess(ApplicationExecutable) <> 0 then
    begin
      { Bez okna Ponów/Pomiń/Przerwij: jasny komunikat i kod wyjścia; poprzednia wersja wraca (DeinitializeSetup). }
      Result := 'Proces VNC Alert pozostały po awarii (PID ' + IntToStr(CrashedProcess) + ') blokuje pliki programu ' +
        'i nie da się go zakończyć. Uruchom ponownie Windows i powtórz instalację — do tego czasu działa poprzednia wersja.';
      Exit;
    end;
    Log('VNC Alert: proces po awarii zakonczony.');
  end;
  AppDir := RemoveBackslash(ExpandConstant('{app}'));
  Log('VNC Alert: katalog instalacji: ' + AppDir + '.');
  if (CompareText(AppDir, RemoveBackslash(ExpandConstant('{autopf}\' + AppFolderCurrent))) <> 0) and
    (CompareText(AppDir, RemoveBackslash(ExpandConstant('{autopf}\' + AppFolderPrevious))) <> 0) then
  begin
    Result := 'Ta wersja wymaga instalacji w Program Files\VNC Alert (albo w dotychczasowym Program Files\MaszynyAlert).';
    Exit;
  end;
  FirstInstall := not FileExists(ExpandConstant('{app}\MaszynyAlert.App.exe'));
  if GetPackedVersion(ExpandConstant('{app}\MaszynyAlert.App.exe'), ExistingVersion) then
  begin
    if not StrToVersion('{#AppVersion}', NewVersion) then
      RaiseException('Nieprawidłowy numer wersji instalatora.');
    if ComparePackedVersion(ExistingVersion, NewVersion) > 0 then
      Result := 'Na komputerze jest nowsza wersja. Instalacja starszej wersji została zablokowana.';
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Arguments: String;
begin
  if CurStep = ssPostInstall then
  begin
    Arguments := ExpandConstant('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{app}\installer\Initialize-Data.ps1" -DataDirectory "{commonappdata}\MaszynyAlert" -ApplicationDirectory "{app}"');
    DataInitialized := False;
    if not ExecAndLogOutput(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Arguments,
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode, nil) or (ResultCode <> 0) then
      RaiseException('Nie udało się przygotować danych i uprawnień. Nie uruchamiaj aplikacji; zachowaj dziennik instalatora i skontaktuj się z administratorem.');
    DataInitialized := True;
    AddToPath(ExpandConstant('{app}'));
    RegisterWatchdogTask;
  end;
  if CurStep = ssDone then
    InstallSucceeded := True;
end;
