#define AppVersion "0.1.0"

[Setup]
AppId={{A0C4D2B1-5F54-4FA7-9D49-7A9E9D2D6D01}
AppName=Aevocis
AppVersion={#AppVersion}
AppPublisher=Aevocis
DefaultDirName={localappdata}\Programs\Aevocis
DefaultGroupName=Aevocis
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=Aevocis-{#AppVersion}-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\aevocis.exe

[Files]
Source: "..\build\x64-release\aevocis.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\build\x64-release\aevocis_cli.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\build\x64-release\Models\*"; DestDir: "{app}\Models"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Aevocis"; Filename: "{app}\aevocis.exe"
Name: "{userstartup}\Aevocis"; Filename: "{app}\aevocis.exe"

[Run]
Filename: "{app}\aevocis.exe"; Description: "启动 Aevocis"; Flags: nowait postinstall skipifsilent
