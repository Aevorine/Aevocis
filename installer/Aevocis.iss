; Aevocis (native Rust+Slint build) installer.
;
; Requirement this script exists to satisfy: the user picks *where* to
; install, but the folder Setup actually creates there is always literally
; named "Aevocis" -- never a bare drive root, never whatever name the user
; happened to browse to. See the [Code] section below for how that's
; enforced; the built-in Inno Setup directory page has no such option on its
; own, so this is done by hand against the officially documented
; NextButtonClick/DirEdit API (jrsoftware.org/ishelp).
;
; Compile with: iscc installer\Aevocis.iss   (run from native-rust\)

#define MyAppName "Aevocis"
#define MyAppVersion "0.2.0"
#define MyAppPublisher "Aevorine"
#define MyAppExeName "Aevocis.exe"
#define MyAppURL "https://github.com/Aevorine/Aevocis"

[Setup]
AppId={{836392FA-1C17-470E-91DC-765F615233E3}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
; {autopf} under PrivilegesRequired=lowest resolves to a real per-user
; Program-Files-equivalent (no UAC prompt); the dialog override below still
; lets the user opt into a real machine-wide admin install if they want one.
DefaultDirName={autopf}\Aevocis
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=Aevocis-Setup-{#MyAppVersion}
SetupIconFile=..\assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ChangesEnvironment=no

[Languages]
; Simplified Chinese isn't bundled with the base Inno Setup compiler (it's an
; unofficial translation distributed separately) -- rather than depend on a
; file that may or may not exist on whatever machine compiles this script,
; the wizard chrome (Next/Back/Cancel etc.) ships in English; every string
; this project actually authored below (task descriptions, run prompt) is
; Chinese regardless of this setting.
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "开机自动启动 Aevocis"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\target\release\osw_native.exe"; DestDir: "{app}"; DestName: "{#MyAppExeName}"; Flags: ignoreversion
; SenseVoice weights: the same model.int8.onnx + tokens.txt already shipped
; with the C# app, plus its FunASR model-license text (redistribution
; requires keeping this file alongside the weights -- see
; src-reference/TECH_ROADMAP.md #1.2). Not committed to native-rust's own
; git history (matches the C# app's existing policy of not vendoring the
; ~237MB weights into version control); this installer packages them
; straight from the reference checkout at compile time instead.
Source: "..\..\src-reference\OpenSuperWhisper.App\Models\sensevoice\model.int8.onnx"; DestDir: "{app}\models\sensevoice"; Flags: ignoreversion
Source: "..\..\src-reference\OpenSuperWhisper.App\Models\sensevoice\tokens.txt"; DestDir: "{app}\models\sensevoice"; Flags: ignoreversion
Source: "..\..\src-reference\OpenSuperWhisper.App\Models\sensevoice\LICENSE-MODEL.txt"; DestDir: "{app}\models\sensevoice"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Aevocis"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\Aevocis"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 Aevocis"; Flags: nowait postinstall skipifsilent

[Code]
// Registers/unregisters the HKCU Run-key entry the "开机自动启动" task asks
// for. Deliberately mirrors osw_native::autostart (src/autostart.rs) rather
// than relying on Inno's own [Tasks]-to-registry sugar, because the running
// app's tray-menu checkbox (the *other* place this same setting can be
// flipped, post-install) needs to agree with whatever the installer wrote --
// both read/write the identical "Aevocis" value under the identical key.
procedure SetAutostart(Enable: Boolean);
var
  RunKey: String;
begin
  RunKey := 'Software\Microsoft\Windows\CurrentVersion\Run';
  if Enable then
    RegWriteStringValue(HKCU, RunKey, 'Aevocis', '"' + ExpandConstant('{app}\{#MyAppExeName}') + '"')
  else
    RegDeleteValue(HKCU, RunKey, 'Aevocis');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    SetAutostart(WizardIsTaskSelected('autostart'));
end;

// Enforces the one real requirement this script exists for: whatever
// location the user picks on the standard "Select Destination Location"
// page (wpSelectDir) -- typed by hand or chosen via Browse -- the directory
// Setup actually installs into always ends in "\Aevocis". A path that
// already ends that way (case-insensitively) is left untouched, so
// repeatedly visiting this page (e.g. via Back) never doubles up into
// "...\Aevocis\Aevocis". This silently corrects rather than rejecting the
// input, since the goal is "always named Aevocis", not "make the user type
// it themselves".
function NextButtonClick(CurPageID: Integer): Boolean;
var
  Dir, Leaf: String;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    Dir := WizardForm.DirEdit.Text;
    while (Length(Dir) > 0) and (Dir[Length(Dir)] = '\') do
      Dir := Copy(Dir, 1, Length(Dir) - 1);
    if Length(Dir) = 0 then
      Exit;
    Leaf := ExtractFileName(Dir);
    if CompareText(Leaf, 'Aevocis') <> 0 then
      WizardForm.DirEdit.Text := Dir + '\Aevocis';
  end;
end;
