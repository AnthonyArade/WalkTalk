; WalkTalk installer (Inno Setup 6).
; Build it with build-installer.ps1 at the repository root, which publishes the app first and then runs:
;   ISCC.exe installer\WalkTalk.iss
; Installs for the current user (no administrator prompt) into %LOCALAPPDATA%\Programs\WalkTalk.
; Settings and downloaded models live in %LOCALAPPDATA%\WhisperStt; uninstalling asks whether to remove them.

#define AppName "WalkTalk"
#define AppExe "WalkTalk.exe"
#define PublishDir AddBackslash(SourcePath) + "..\publish"
; The published app: WalkTalk.exe and every file it needs beside it.
#define AppDir PublishDir + "\app"
#define AppVersion GetVersionNumbersString(AppDir + "\" + AppExe)

[Setup]
; Keep this AppId forever: it is how Windows recognizes upgrades of the same app.
AppId={{2B8D7785-8CB9-4648-9800-DDFE54622D9C}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#PublishDir}
OutputBaseFilename=WalkTalk-Setup-{#AppVersion}
SetupIconFile=..\src\WhisperStt.Desktop\Assets\WalkTalk.ico
WizardStyle=modern
WizardSmallImageFile=WizardSmall.bmp,WizardSmall@2x.bmp
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2
SolidCompression=yes
; Close a running WalkTalk before replacing its files during an upgrade.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; The command-line tool an early setup installed. It is no longer shipped, and an upgrade would otherwise leave the old one behind.
Type: files; Name: "{app}\WalkTalk-cli.exe"

[Files]
; The Whisper packages also bring Linux builds of their libraries, which Windows can't use.
Source: "{#AppDir}\*"; DestDir: "{app}"; Excludes: "*.so,*.dylib"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// Settings, fields and speech models (several GB) are kept unless the user asks to remove them.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
  begin
    DataDir := ExpandConstant('{localappdata}\WhisperStt');
    if DirExists(DataDir) and
       (MsgBox('Also delete your WalkTalk settings, fields, downloaded speech models, voice recordings and voice training tools?' + #13#10 + #13#10 +
               'Keep them if you plan to install WalkTalk again: the models and tools are several GB to download.',
               mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
      DelTree(DataDir, True, True, True);
  end;
end;
