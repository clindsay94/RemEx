; ============================================================
;  RemEx Installer
;  Build with: iscc /DAppVersion=2.2.0 installer\RemEx.iss
;  Or use:     installer\build-installer.ps1
; ============================================================

#ifndef AppVersion
  #define AppVersion "2.2.0"
#endif

#define AppName        "RemEx"
#define AppPublisher   "Connor Lindsay"
#define AppExeName     "Remex.Agent.exe"
#define AppHostExe     "Remex.Agent.exe"
#define AutostartScript  "scripts\autostart-remex.ps1"
#ifndef SourceDir
  #define SourceDir      "..\artifacts\publish\remex.agent\release_win-x64"
#endif

[Setup]
AppId={{A3F7C2B1-84E5-4D9A-B6F0-1C2D3E4F5A6B}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/clindsay94/remex
AppSupportURL=https://github.com/clindsay94/remex/issues
AppUpdatesURL=https://github.com/clindsay94/remex/releases

; Upgrade: uninstall previous version first if major version changes,
; otherwise do an in-place upgrade (files overwritten, shortcuts kept).
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes

LicenseFile=terms.rtf
SetupIconFile=..\remex.agent\icon.ico
WizardResizable=yes
WizardStyle=modern

OutputDir=Output
OutputBaseFilename=RemEx-v{#AppVersion}-Setup
Compression=lzma2/max
SolidCompression=yes

PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

MinVersion=10.0

; Uninstaller
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName} {#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked
Name: "launchatlogin"; Description: "Launch {#AppName} when you sign in"; GroupDescription: "Startup options:"

; NOTE: No HKCU Run-key entry. RemEx auto-start is an elevated Task Scheduler logon
; task (registered below via autostart-remex.ps1). A Run-key would start a competing
; MEDIUM-integrity instance that wins the single-instance mutex and reintroduces the
; UIPI input block, so it must not exist.

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\scripts\autostart-remex.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion

[Icons]
; Start Menu
Name: "{group}\{#AppName}";         Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
; Desktop (optional task)
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; Offer to launch the app after install
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Remove the elevated logon task, firewall rules, event-log source, and any legacy
; RemexHost service before files are deleted. The script handles "not installed" gracefully.
Filename: "powershell.exe"; \
  Parameters: "-ExecutionPolicy Bypass -NonInteractive -File ""{app}\{#AutostartScript}"" -Action Uninstall"; \
  Flags: runhidden waituntilterminated; \
  RunOnceId: "UninstallRemexLogonTask"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Remex"
Type: filesandordirs; Name: "{commonappdata}\RemEx"

; ============================================================
;  Pascal [Code] — custom wizard pages
; ============================================================
[Code]

// ------------------------------------------------------------------
// After files are installed, register the elevated logon auto-start
// task when the user opted into "Launch RemEx when you sign in".
//
// RemEx 2.0 is a single interactive user-session app (no Windows Service).
// autostart-remex.ps1 -Action Install registers a Task Scheduler logon task
// that starts RemEx elevated at sign-in with no UAC prompt, and configures
// the firewall so your phone can connect.
// ------------------------------------------------------------------
procedure CurStepChanged(CurStep: TSetupStep);
var
  PSArgs:     String;
  ResultCode: Integer;
  ErrMsg:     String;
begin
  if CurStep <> ssPostInstall then Exit;

  // Only set up auto-start if the user kept the "Launch at login" task checked.
  if not WizardIsTaskSelected('launchatlogin') then Exit;

  PSArgs :=
    '-ExecutionPolicy Bypass -NonInteractive' +
    ' -File "' + ExpandConstant('{app}') + '\' + '{#AutostartScript}' + '"' +
    ' -Action Install' +
    ' -HostPath "' + ExpandConstant('{app}') + '\{#AppHostExe}"';

  if not Exec('powershell.exe', PSArgs, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    ErrMsg := 'Could not launch PowerShell to set up automatic start-up.' + #13#10 +
              'You can set it up later from the RemEx Settings screen, or by running:' + #13#10 +
              '  ' + ExpandConstant('{app}') + '\' + '{#AutostartScript}' + ' -Action Install';
    MsgBox(ErrMsg, mbError, MB_OK);
  end
  else if ResultCode <> 0 then
  begin
    ErrMsg := 'Setting up automatic start-up returned exit code ' + IntToStr(ResultCode) + '.' + #13#10 +
              'RemEx is still installed and you can start it from the Start Menu.' + #13#10 + #13#10 +
              'To retry, run as Administrator:' + #13#10 +
              '  ' + ExpandConstant('{app}') + '\' + '{#AutostartScript}' + ' -Action Install';
    MsgBox(ErrMsg, mbInformation, MB_OK);
  end;
end;
