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
#define AppExeName     "Remex.Client.Desktop.exe"
#define AppHostExe     "Remex.Host.exe"
#define ServiceScript  "scripts\install-service.ps1"
#define SourceDir      "..\Remex.Client.Desktop\bin\Release\net10.0\win-x64\publish"

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
SetupIconFile=..\Remex.Client.Desktop\icon.ico
WizardImageFile=..\Remex.Client\Assets\New-REMEX.png
WizardSmallImageFile=..\Remex.Client\Assets\New-REMEX.png
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

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

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

; ============================================================
;  Pascal [Code] — custom wizard pages
; ============================================================
[Code]

var
  // Custom wizard pages
  InstallTypePage:  TInputOptionWizardPage;   // Client-only vs Client+Service
  ServiceModePage:  TInputOptionWizardPage;   // Auto (service) vs Manual
  CredentialsPage:  TInputQueryWizardPage;    // Username / Password

  // Values captured from the wizard
  InstallService:   Boolean;
  ServiceAutoStart: Boolean;
  ServiceUsername:  String;
  ServicePassword:  String;


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
    'Choose when the RemEx Host service should start.',
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

  // --- Page 3: Service account credentials ---
  CredentialsPage := CreateInputQueryPage(
    ServiceModePage.ID,
    'Service Account Credentials',
    'The Windows Service must run as a real user account to access the desktop session.',
    'Enter the account that the RemEx Host service should run as. ' +
    'Use the format .\Username for a local account (e.g. .\Connor).'
  );
  CredentialsPage.Add('Username (e.g. .\Connor):', False);
  CredentialsPage.Add('Password:', True);  // True = masked password field

  // Pre-populate username with current user
  CredentialsPage.Values[0] := '.\' + GetUserNameString;
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

  // Hide credentials page if "Client only" OR "Manual" was selected
  if PageID = CredentialsPage.ID then
    Result := (InstallTypePage.SelectedValueIndex = 0) or
              (ServiceModePage.SelectedValueIndex = 1);
end;


// ------------------------------------------------------------------
// Validate inputs before advancing
// ------------------------------------------------------------------
function NextButtonClick(CurPageID: Integer): Boolean;
var
  User, Pass: String;
begin
  Result := True;

  if CurPageID = CredentialsPage.ID then
  begin
    User := Trim(CredentialsPage.Values[0]);
    Pass := CredentialsPage.Values[1];

    if User = '' then
    begin
      MsgBox('Please enter a username for the service account.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if Pass = '' then
    begin
      MsgBox('Please enter the password for the service account.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;
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

  // Collect credentials
  ServiceUsername := Trim(CredentialsPage.Values[0]);
  ServicePassword := CredentialsPage.Values[1];

  // Escape any single quotes for PowerShell by doubling them
  StringChange(ServiceUsername, '''', '''''');
  StringChange(ServicePassword, '''', '''''');

  // Build PowerShell arguments — wrap strings in single quotes
  PSArgs :=
    '-ExecutionPolicy Bypass -NonInteractive' +
    ' -File "' + ExpandConstant('{app}') + '\' + '{#ServiceScript}' + '"' +
    ' -Action Install' +
    ' -HostPath "' + ExpandConstant('{app}') + '\{#AppHostExe}"' +
    ' -Username ''' + ServiceUsername + '''' +
    ' -Password ''' + ServicePassword + '''';

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
