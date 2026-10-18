; Dream Player installer (Inno Setup 6). Built by scripts\build-installer.ps1,
; which publishes the app self-contained and passes:
;   /DAppVersion=0.2  /DPublishDir=<dotnet publish output>  /O<output dir>
; Installs per user by default (no admin); the setup offers "for all users".
; The library and settings (%LOCALAPPDATA%\DreamPlayer) are left on uninstall.

#ifndef AppVersion
  #define AppVersion "0.2"
#endif
#ifndef PublishDir
  #error PublishDir is not defined: build with scripts\build-installer.ps1
#endif

#define AppName "Dream Player"
#define AppExe "DreamPlayer.exe"

[Setup]
; AppId identifies the app for upgrades and uninstall: never change it.
AppId={{B6E3A1D4-7C2F-4E8B-9A51-3F0D6C2E8A47}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=ksimilikon
AppPublisherURL=https://github.com/ksimilikon/rs_dream_player
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 1809, as TargetPlatformMinVersion
MinVersion=10.0.17763
OutputBaseFilename=DreamPlayer-{#AppVersion}-setup
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
LicenseFile=..\LICENSE

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; an upgrade replaces the whole app folder: no leftovers of removed files.
Type: filesandordirs; Name: "{app}\*"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
