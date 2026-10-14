# Uruchamiany przez instalator z uprawnieniami administratora, przed startem aplikacji.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DataDirectory,
    [Parameter(Mandatory)][string]$ApplicationDirectory,
    # Tylko definicje funkcji (dot-source w Test-Installer.ps1) - bez zmian w katalogu danych.
    [switch]$DefinitionsOnly
)
$ErrorActionPreference = 'Stop'

# Instalator uruchamia Windows PowerShell 5.1, ale może odziedziczyć PSModulePath
# z PowerShell 7 (np. przez Inno Setup w CI). Moduł Security z PS7 nie działa w 5.1.
# Ładujemy moduł z bieżącego silnika przed pierwszym Set-Acl, bez zmiany ACL.
Import-Module "$PSHOME\Modules\Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1" -ErrorAction Stop

function Assert-NoReparsePoint([string]$Path) {
    $candidate = [IO.Path]::GetFullPath($Path)
    while ($candidate) {
        if (Test-Path -LiteralPath $candidate) {
            if ((Get-Item -LiteralPath $candidate -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Niedozwolone przekierowanie katalogu/pliku: $candidate"
            }
        }
        $candidate = [IO.Path]::GetDirectoryName($candidate)
    }
}

function Get-NativeFileApi {
    # CreateFileW i GetFileInformationByHandle przez P/Invoke zdefiniowany w pamieci (Reflection.Emit) - bez Add-Type, ktory
    # kompiluje C# w katalogu TEMP (przy SYSTEM: C:\Windows\Temp, zapisywalny dla Users).
    if ($script:NativeFileApi) { return $script:NativeFileApi }
    $assembly = [Reflection.Emit.AssemblyBuilder]::DefineDynamicAssembly(
        (New-Object Reflection.AssemblyName('VncAlertNativeFileApi')), [Reflection.Emit.AssemblyBuilderAccess]::Run)
    $type = $assembly.DefineDynamicModule('VncAlertNativeFileApi').DefineType('VncAlertNativeFileApi', 'Public, Class, Sealed, Abstract')
    $create = $type.DefinePInvokeMethod('CreateFileW', 'kernel32.dll', 'Public, Static, PinvokeImpl',
        [Reflection.CallingConventions]::Standard, [Microsoft.Win32.SafeHandles.SafeFileHandle],
        [Type[]]@([string], [int], [int], [IntPtr], [int], [int], [IntPtr]),
        [Runtime.InteropServices.CallingConvention]::Winapi, [Runtime.InteropServices.CharSet]::Unicode)
    $create.SetImplementationFlags('PreserveSig')
    $information = $type.DefinePInvokeMethod('GetFileInformationByHandle', 'kernel32.dll', 'Public, Static, PinvokeImpl',
        [Reflection.CallingConventions]::Standard, [bool], [Type[]]@([Microsoft.Win32.SafeHandles.SafeFileHandle], [byte[]]),
        [Runtime.InteropServices.CallingConvention]::Winapi, [Runtime.InteropServices.CharSet]::Unicode)
    $information.SetImplementationFlags('PreserveSig')
    $script:NativeFileApi = $type.CreateType()
    return $script:NativeFileApi
}

function Set-AclByHandle([string]$Path, $Acl, [bool]$Directory) {
    $api = Get-NativeFileApi
    # READ_CONTROL | WRITE_DAC | WRITE_OWNER | FILE_READ_ATTRIBUTES; wspoldzielenie odczyt/zapis/usuwanie; OPEN_EXISTING;
    # FILE_FLAG_BACKUP_SEMANTICS (katalogi) | FILE_FLAG_OPEN_REPARSE_POINT.
    $handle = $api::CreateFileW($Path, 0xE0080, 7, [IntPtr]::Zero, 3, 0x02200000, [IntPtr]::Zero)
    if ($handle.IsInvalid) { throw "Nie otwarto $Path do zmiany uprawnien." }
    try {
        $buffer = New-Object byte[] 52
        if (-not $api::GetFileInformationByHandle($handle, $buffer)) { throw "Nie odczytano atrybutow $Path." }
        $attributes = [BitConverter]::ToUInt32($buffer, 0)
        if ($attributes -band 0x400) { throw "Przekierowanie wykryte na uchwycie (zmiana uprawnien odrzucona): $Path" }
        if ([bool]($attributes -band 0x10) -ne $Directory) { throw "Nieoczekiwany rodzaj obiektu (plik/katalog): $Path" }
        $persist = [Security.AccessControl.NativeObjectSecurity].GetMethod('Persist', [Reflection.BindingFlags]'Instance, NonPublic',
            $null, [Type[]]@([Runtime.InteropServices.SafeHandle], [Security.AccessControl.AccessControlSections]), $null)
        $null = $persist.Invoke($Acl, @($handle, [Security.AccessControl.AccessControlSections]'Access, Owner'))
    }
    finally { $handle.Dispose() }
}

function Set-DataAcl([string]$Path, [bool]$Directory, [bool]$Writable) {
    Assert-NoReparsePoint $Path
    if ($Directory) { $acl = [Security.AccessControl.DirectorySecurity]::new() }
    else { $acl = [Security.AccessControl.FileSecurity]::new() }
    $acl.SetAccessRuleProtection($true, $false)
    $admin = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $acl.SetOwner($admin)
    $inheritance = [Security.AccessControl.InheritanceFlags]::None
    if ($Directory) { $inheritance = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit' }
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-32-545')) {
        $rights = [Security.AccessControl.FileSystemRights]::FullControl
        if ($sid -eq 'S-1-5-32-545') {
            $rights = [Security.AccessControl.FileSystemRights]::ReadAndExecute
            if ($Writable -and $Directory) { $rights = [Security.AccessControl.FileSystemRights]::Modify }
            elseif ($Writable) { $rights = [Security.AccessControl.FileSystemRights]'Read, Write' }
        }
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            [Security.Principal.SecurityIdentifier]::new($sid), $rights, $inheritance,
            [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow))
    }
    Set-AclByHandle $Path $acl $Directory
}

if ($DefinitionsOnly) { return }

Assert-NoReparsePoint $DataDirectory
New-Item -ItemType Directory -Path $DataDirectory -Force | Out-Null
Set-DataAcl $DataDirectory $true $false
foreach ($pair in @(@('config.example.json', 'config.json'), @('rules.example.json', 'rules.json'))) {
    $target = Join-Path $DataDirectory $pair[1]
    Assert-NoReparsePoint $target
    if (!(Test-Path -LiteralPath $target)) {
        Copy-Item -LiteralPath (Join-Path $ApplicationDirectory $pair[0]) -Destination $target
    }
    Set-DataAcl $target $false $false
}
foreach ($name in @('secrets.dat', 'secrets.lock', 'config.json.bak', 'alarm-instructions.json', 'alarm-instructions.json.bak', 'alarm-instructions.json.lock')) {
    $target = Join-Path $DataDirectory $name
    Assert-NoReparsePoint $target
    if (Test-Path -LiteralPath $target) { Set-DataAcl $target $false $false }
}
# 'state' trzyma znacznik stop.requested komendy "vncalert killapp" (watchdog Harmonogramu zadan);
# pisze go raz operator bez uprawnien administratora, raz instalator - stad prawo zapisu dla Users.
foreach ($name in @('logs', 'captures', 'test-ekran', 'state', 'group-instructions', 'shared-config')) {
    $target = Join-Path $DataDirectory $name
    Assert-NoReparsePoint $target
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Set-DataAcl $target $true $true
}
$updatesAdmin = Join-Path $DataDirectory 'updates-admin'
Assert-NoReparsePoint $updatesAdmin
New-Item -ItemType Directory -Path $updatesAdmin -Force | Out-Null
Set-DataAcl $updatesAdmin $true $false
$history = Join-Path $DataDirectory 'history.csv'
Assert-NoReparsePoint $history
if (!(Test-Path -LiteralPath $history)) { [IO.File]::WriteAllBytes($history, [byte[]]@()) }
Set-DataAcl $history $false $true
$downtime = Join-Path $DataDirectory 'downtime.jsonl'
Assert-NoReparsePoint $downtime
if (!(Test-Path -LiteralPath $downtime)) { [IO.File]::WriteAllBytes($downtime, [byte[]]@()) }
Set-DataAcl $downtime $false $true
