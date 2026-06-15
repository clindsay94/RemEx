; ============================================================
;  RemEx Installer
;  Build with: iscc /DAppVersion=2.0.0 installer\RemEx.iss
;  Or use:     installer\build-installer.ps1
; ============================================================

#ifndef AppVersion
  #define AppVersion "2.0.0"
#endif

#define AppName        "RemEx"
#define AppPublisher   "Connor Lindsay"
#define AppExeName     "RemEx.Host.exe"
#define AppHostExe     "RemEx.Host.exe"
#define ServiceScript  "scripts\install-service.ps1"
#ifndef SourceDir
  #define SourceDir      "..\artifacts\publish\RemEx.Host\release_win-x64"
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
SetupIconFile=..\RemEx.Host\icon.ico
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

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "RemEx"; ValueData: """{app}\{#AppExeName}"" --minimized"; Tasks: launchatlogin; Flags: uninsdeletevalue

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\scripts\install-service.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion

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
; Stop and remove the Windows Service (if it was installed) before files are deleted.
; The script handles the case where the service doesn't exist gracefully.
Filename: "powershell.exe"; \
  Parameters: "-ExecutionPolicy Bypass -NonInteractive -File ""{app}\{#ServiceScript}"" -Action Uninstall"; \
  Flags: runhidden waituntilterminated; \
  RunOnceId: "UninstallRemexService"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Remex"
Type: filesandordirs; Name: "{commonappdata}\RemEx"

; ============================================================
;  Pascal [Code] — custom wizard pages
; ============================================================
[Code]

var
  // Custom wizard pages
  InstallTypePage:  TInputOptionWizardPage;   // Client-only vs Client+Service
  ServiceModePage:  TInputOptionWizardPage;   // Auto (service) vs Manual

  // Values captured from the wizard
  InstallService:   Boolean;
  ServiceAutoStart: Boolean;


// ------------------------------------------------------------------
// Build all custom wizard pages on startup
// ------------------------------------------------------------------
procedure InitializeWizard;
begin
  // --- Page 1: Installation type ---
  InstallTypePage := CreateInputOptionPage(
    wpSelectDir,
    'Installation Type',
    'Choose how you want to install RemEx.',
    'Select the components you want to install:',
    True,   // exclusive (radio buttons)
    False   // no scrollable list
  );
  InstallTypePage.Add('Desktop Client only');
  InstallTypePage.Add('Desktop Client + Host Service');
  InstallTypePage.SelectedValueIndex := 0;

  // --- Page 2: Service startup mode ---
  ServiceModePage := CreateInputOptionPage(
    InstallTypePage.ID,
    'Host Service Startup',
    'Choose when the RemEx Host service should start.' + #13#10 +
    'The service runs as LocalSystem — no credentials required.',
    'How should the host service start?',
    True,   // exclusive (radio buttons)
    False
  );
  ServiceModePage.Add(
    'Automatically before Windows login (recommended for remote access without logging in)'
  );
  ServiceModePage.Add(
    'Manually / on-demand (you start it from services.msc or the desktop app)'
  );
  ServiceModePage.SelectedValueIndex := 0;
end;


// ------------------------------------------------------------------
// Skip pages that are not relevant to the chosen install type
// ------------------------------------------------------------------
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;

  // Hide service mode page if "Client only" was selected
  if PageID = ServiceModePage.ID then
    Result := (InstallTypePage.SelectedValueIndex = 0);
end;


// ------------------------------------------------------------------
// After files are installed, register the Windows Service if needed
// ------------------------------------------------------------------
procedure CurStepChanged(CurStep: TSetupStep);
var
  PSArgs:     String;
  ResultCode: Integer;
  ErrMsg:     String;
begin
  if CurStep <> ssPostInstall then Exit;

  // Capture wizard selections into variables
  InstallService   := (InstallTypePage.SelectedValueIndex = 1);
  ServiceAutoStart := InstallService and (ServiceModePage.SelectedValueIndex = 0);

  if not InstallService then Exit;
  if not ServiceAutoStart then Exit;

  // Install as LocalSystem — no credentials needed
  PSArgs :=
    '-ExecutionPolicy Bypass -NonInteractive' +
    ' -File "' + ExpandConstant('{app}') + '\' + '{#ServiceScript}' + '"' +
    ' -Action Install' +
    ' -HostPath "' + ExpandConstant('{app}') + '\{#AppHostExe}"';

  if not Exec('powershell.exe', PSArgs, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    ErrMsg := 'Could not launch PowerShell to install the Windows Service.' + #13#10 +
              'You can install it manually later by running:' + #13#10 +
              '  ' + ExpandConstant('{app}') + '\' + '{#ServiceScript}' + ' -Action Install';
    MsgBox(ErrMsg, mbError, MB_OK);
  end
  else if ResultCode <> 0 then
  begin
    ErrMsg := 'The Windows Service installer returned exit code ' + IntToStr(ResultCode) + '.' + #13#10 +
              'The desktop client is still installed and fully functional.' + #13#10 + #13#10 +
              'To retry service installation, run as Administrator:' + #13#10 +
              '  ' + ExpandConstant('{app}') + '\' + '{#ServiceScript}' + ' -Action Install';
    MsgBox(ErrMsg, mbInformation, MB_OK);
  end;
end;
