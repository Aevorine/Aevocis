#define AppVersion "0.2.0"

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
Source: "..\build\x64-release\*.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\build\x64-release\Aevocis.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\build\x64-release\Aevocis.png"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\Aevocis"; Filename: "{app}\aevocis.exe"; IconFilename: "{app}\Aevocis.ico"
Name: "{autodesktop}\Aevocis"; Filename: "{app}\aevocis.exe"; IconFilename: "{app}\Aevocis.ico"
Name: "{userstartup}\Aevocis"; Filename: "{app}\aevocis.exe"; IconFilename: "{app}\Aevocis.ico"

[Run]
Filename: "{app}\aevocis.exe"; Description: "启动 Aevocis"; Flags: nowait postinstall skipifsilent
