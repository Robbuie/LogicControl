; Inno Setup script for LogicControl.
;
; The same shape as NetControl's: a per-user install (%LOCALAPPDATA%\Programs\LogicControl), no
; administrator rights, no runtime to install first. The portable single exe is published beside
; this installer on every release; the installer is for the machine somebody uses every week and
; wants a Start menu entry and an entry in Add/Remove Programs.
;
; Build:  ISCC.exe /DAppVersion=0.2.0 /DSourceDir=..\artifacts\LogicControl-0.2.0 installer\LogicControl.iss
;
; tools/publish.ps1 -Installer does this for you and passes both values.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef SourceDir
  #define SourceDir "..\artifacts\LogicControl-" + AppVersion
#endif

#define AppName    "LogicControl"
#define AppExeName "LogicControl.exe"
#define AppPublisher "Robbuie"

[Setup]
; Keep this GUID forever - it is how Windows and the in-app updater recognise an existing install
; and upgrade it in place (Diagnostics/InstallLocation.cs reads the same key).
AppId={{E998DA6F-0F1B-47D1-A470-8A7AA5FB4A2B}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppSupportURL=https://github.com/Robbuie/LogicControl
AppUpdatesURL=https://github.com/Robbuie/LogicControl/releases/latest
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto

; Lowest, and the default: {autopf} resolves to %LOCALAPPDATA%\Programs - no UAC prompt.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

OutputDir=..\dist_installer
OutputBaseFilename=LogicControl-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\LogicControl.App\Assets\LogicControl.ico
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible

; Reinstalling over a running copy closes it cleanly rather than failing on a locked file.
CloseApplications=yes
RestartApplications=yes
SetupLogging=yes

; The .lcdev association is written under HKCU; tell Explorer so the icon appears straight away.
ChangesAssociations=yes

UninstallDisplayName={#AppName} {#AppVersion}
UninstallDisplayIcon={app}\{#AppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"
Name: "lcdev"; Description: "Open LogicControl development sets (.lcdev) with LogicControl"; GroupDescription: "File types:"

[Files]
; One file: LogicControl publishes self-contained and single-file.
Source: "{#SourceDir}\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}";           Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";     Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; .lcdev is LogicControl's own format, so associating it takes nothing from anybody. .L5X is left
; alone on purpose: it belongs to Studio 5000, and an installer that quietly took it over would be
; the first thing an engineer uninstalled. "Open with" still works for it.
Root: HKCU; Subkey: "Software\Classes\.lcdev"; ValueType: string; ValueName: ""; ValueData: "LogicControl.DevelopmentSet"; Flags: uninsdeletevalue; Tasks: lcdev
Root: HKCU; Subkey: "Software\Classes\LogicControl.DevelopmentSet"; ValueType: string; ValueName: ""; ValueData: "LogicControl development set"; Flags: uninsdeletekey; Tasks: lcdev
Root: HKCU; Subkey: "Software\Classes\LogicControl.DevelopmentSet\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"; Tasks: lcdev
Root: HKCU; Subkey: "Software\Classes\LogicControl.DevelopmentSet\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""; Tasks: lcdev
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".l5x"; ValueData: ""; Flags: uninsdeletekey

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; The rolling diagnostic log and any downloaded updates - regenerable. settings.json is kept: it is
; site configuration (the switch that stops the update check, a mirror URL), and an uninstall that
; discarded it would turn a reinstall into a machine phoning out again without anybody choosing it.
; Development sets and L5X files are wherever the user saved them and are never touched.
Type: filesandordirs; Name: "{localappdata}\LogicControl\logs"
Type: filesandordirs; Name: "{localappdata}\LogicControl\updates"
