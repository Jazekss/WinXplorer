; WinXplorer installer (Inno Setup 6). Built by build-installer.ps1, which publishes the app first.
; Installs to Program Files\JKapps\WinXplorer (settings: %LOCALAPPDATA%\JKapps\WinXplorer); .NET is included.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

[Setup]
AppId={{2D784AD2-0C79-4041-96B3-A4656BD75681}
AppName=WinXplorer
AppVersion={#AppVersion}
AppVerName=WinXplorer {#AppVersion}
AppPublisher=Jazekss
AppPublisherURL=https://buymeacoffee.com/jazeknom
DefaultDirName={autopf}\JKapps\WinXplorer
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
UsePreviousAppDir=no
OutputDir=..\artifacts
OutputBaseFilename=WinXplorer-Setup-{#AppVersion}
SetupIconFile=..\src\WinXplorer\Assets\WinXplorer.ico
UninstallDisplayIcon={app}\WinXplorer.exe
UninstallDisplayName=WinXplorer
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
CloseApplications=yes
MinVersion=10.0.17763

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "latvian"; MessagesFile: "Languages\Latvian.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "norwegian"; MessagesFile: "compiler:Languages\Norwegian.isl"
Name: "polish"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "swedish"; MessagesFile: "compiler:Languages\Swedish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\artifacts\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Registry]
; JKapps convention: Program Files\JKapps\<App>, %LOCALAPPDATA%\JKapps\<App>, registry Software\JKapps\<App>
Root: HKA; Subkey: "Software\JKapps"; Flags: uninsdeletekeyifempty
Root: HKA; Subkey: "Software\JKapps\WinXplorer"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\JKapps\WinXplorer"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"
Root: HKA; Subkey: "Software\JKapps\WinXplorer"; ValueType: string; ValueName: "Version"; ValueData: "{#AppVersion}"

[Icons]
Name: "{autoprograms}\WinXplorer"; Filename: "{app}\WinXplorer.exe"
Name: "{autodesktop}\WinXplorer"; Filename: "{app}\WinXplorer.exe"; Tasks: desktopicon

[Run]
; first start after installing is slow because the antivirus scans every new DLL as it is loaded:
; load them all once here (hidden), so the user's first start is a normal one
Filename: "{app}\WinXplorer.exe"; Parameters: "--warmup"; StatusMsg: "Preparing WinXplorer..."; Flags: runhidden waituntilterminated
Filename: "{app}\WinXplorer.exe"; Description: "{cm:LaunchProgram,WinXplorer}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; folders go back to Windows Explorer, archive menu entries are removed (settings in %LOCALAPPDATA%\JKapps\WinXplorer stay)
Filename: "{app}\WinXplorer.exe"; Parameters: "--unregister"; RunOnceId: "WinXplorerUnregister"; Flags: waituntilterminated
