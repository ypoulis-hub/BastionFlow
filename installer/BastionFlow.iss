; BastionFlow installer — Inno Setup script.
;
; Builds a single Setup.exe that:
;   1. Copies the published BastionFlow app to Program Files
;   2. Creates Start Menu + (optional) Desktop shortcuts
;   3. Detects and installs three prerequisites if missing (via winget):
;        - .NET 8 Desktop Runtime  (required by BastionFlow.exe)
;        - Azure CLI               (called as `az` to drive Bastion)
;        - Microsoft Remote Desktop client (msrdc.exe; AAD-RDP target)
;
; Compile with: iscc BastionFlow.iss

#define MyAppName       "BastionFlow"
#define MyAppVersion    "0.1.0"
#define MyAppPublisher  "JohnBird"
#define MyAppURL        "https://github.com/johnbird/BastionFlow"
#define MyAppExeName    "BastionFlow.exe"

[Setup]
AppId={{8E1F4D26-2A2C-4F9D-9E5A-3F0D9C7D6B1E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputBaseFilename=BastionFlow-Setup-{#MyAppVersion}
OutputDir=..\dist
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
LicenseFile=..\LICENSE
SetupIconFile=..\src\BastionFlow.App\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
function IsDotNet8DesktopInstalled(): Boolean;
var
  ResultCode: Integer;
  Buf: AnsiString;
  TmpFile: String;
begin
  Result := False;
  TmpFile := ExpandConstant('{tmp}\dnruntimes.txt');
  if Exec(ExpandConstant('{cmd}'), '/c dotnet --list-runtimes > "' + TmpFile + '" 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if LoadStringFromFile(TmpFile, Buf) then
      Result := Pos('Microsoft.WindowsDesktop.App 8.', String(Buf)) > 0;
  end;
end;

function IsAzCliInstalled(): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec('cmd.exe', '/c where az.cmd >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function IsMsrdcInstalled(): Boolean;
begin
  Result := FileExists(ExpandConstant('{commonpf}\Remote Desktop\msrdc.exe'))
         or FileExists(ExpandConstant('{commonpf32}\Remote Desktop\msrdc.exe'));
end;

function WingetAvailable(): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec('cmd.exe', '/c where winget >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

procedure InstallViaWinget(const PackageId: String; const Description: String);
var
  ResultCode: Integer;
begin
  WizardForm.StatusLabel.Caption := 'Installing ' + Description + ' via winget (this may take a few minutes)...';
  WizardForm.StatusLabel.Update();
  Exec('cmd.exe',
       '/c winget install --id ' + PackageId + ' --accept-package-agreements --accept-source-agreements --silent --disable-interactivity',
       '', SW_SHOW, ewWaitUntilTerminated, ResultCode);
end;

procedure CheckAndInstallDependencies();
var
  MissingList: String;
  Msg: String;
begin
  MissingList := '';
  if not IsDotNet8DesktopInstalled() then MissingList := MissingList + #13#10 + '  • .NET 8 Desktop Runtime';
  if not IsAzCliInstalled() then MissingList := MissingList + #13#10 + '  • Azure CLI';
  if not IsMsrdcInstalled() then MissingList := MissingList + #13#10 + '  • Microsoft Remote Desktop client (msrdc)';

  if Length(MissingList) = 0 then Exit;

  Msg := 'BastionFlow needs the following components that are not installed yet:' + #13#10 +
         MissingList + #13#10 + #13#10 +
         'Install them now? (Recommended)';

  if MsgBox(Msg, mbConfirmation, MB_YESNO) <> IDYES then Exit;

  if not WingetAvailable() then
  begin
    MsgBox(
      'winget is not available on this system. Please install the missing components manually:' + #13#10 + #13#10
      + '- .NET 8 Desktop Runtime: https://dotnet.microsoft.com/download/dotnet/8.0' + #13#10
      + '- Azure CLI: https://aka.ms/installazurecliwindows' + #13#10
      + '- Microsoft Remote Desktop: https://aka.ms/avdrdc',
      mbInformation, MB_OK);
    Exit;
  end;

  if not IsDotNet8DesktopInstalled() then InstallViaWinget('Microsoft.DotNet.DesktopRuntime.8', '.NET 8 Desktop Runtime');
  if not IsAzCliInstalled() then InstallViaWinget('Microsoft.AzureCLI', 'Azure CLI');
  if not IsMsrdcInstalled() then InstallViaWinget('Microsoft.RemoteDesktopClient', 'Microsoft Remote Desktop');

  WizardForm.StatusLabel.Caption := 'Dependency installation complete.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    CheckAndInstallDependencies();
end;
