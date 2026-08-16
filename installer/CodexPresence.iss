#define MyAppName "Codex Presence"
#ifndef MyAppVersion
#define MyAppVersion "2.3.2"
#endif
#define MyAppPublisher "trifonovsdev"
#define MyAppURL "https://github.com/trifonovsdev/codex-discord-presence"
#define MyAppExeName "CodexPresence.exe"

[Setup]
AppId={{9A20AD08-D395-4E83-87DA-B9E3AD42B65A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases/latest
DefaultDirName={localappdata}\Programs\CodexPresence
DefaultGroupName=Codex Presence
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts
OutputBaseFilename=CodexPresenceSetup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\assets\codex-presence.ico
CloseApplications=yes
RestartApplications=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=Discord Rich Presence for ChatGPT Codex Desktop
VersionInfoProductName={#MyAppName}
#ifdef SIGN_BUILD
SignTool=codexsign
SignedUninstaller=yes
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "autostart"; Description: "Start Codex Presence with Windows"; GroupDescription: "Startup:"; Flags: checkedonce

[Files]
#ifdef SIGN_BUILD
Source: "..\artifacts\stage\CodexPresence.exe"; DestDir: "{app}"; Flags: ignoreversion signonce
#else
Source: "..\artifacts\stage\CodexPresence.exe"; DestDir: "{app}"; Flags: ignoreversion
#endif
Source: "..\artifacts\stage\runtime\node.exe"; DestDir: "{app}\runtime"; Flags: ignoreversion
Source: "..\artifacts\stage\runtime\NODE_LICENSE"; DestDir: "{app}\runtime"; Flags: ignoreversion
Source: "..\artifacts\stage\app\*.js"; DestDir: "{app}\app"; Flags: ignoreversion
Source: "..\artifacts\stage\app\remote-monitor.py"; DestDir: "{app}\app"; Flags: ignoreversion
Source: "..\artifacts\stage\app\config.default.json"; DestDir: "{app}\app"; Flags: ignoreversion
Source: "configure.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Codex Presence"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall Codex Presence"; Filename: "{uninstallexe}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "CodexDiscordPresence"; ValueData: """{app}\{#MyAppExeName}"" --background"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\installer\configure.ps1"" -InstallDir ""{app}"""; Flags: runhidden waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Codex Presence"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--shutdown"; Flags: runhidden waituntilterminated; RunOnceId: "CodexPresenceStopTray"
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\installer\configure.ps1"" -InstallDir ""{app}"" -Uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "CodexPresenceRemoveHooks"

[Code]
procedure StopRunningTray();
var
  ResultCode: Integer;
  Executable: String;
begin
  Executable := ExpandConstant('{app}\CodexPresence.exe');
  if FileExists(Executable) then
  begin
    Exec(Executable, '--shutdown', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(400);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningTray();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopRunningTray();
  Result := True;
end;
