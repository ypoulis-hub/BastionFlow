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
; Allow x64 hardware OR native ARM64. On ARM64 we install native ARM64 binaries
; (no emulation overhead); on x64 we install x64. The .NET runtime, Azure CLI,
; and msrdc dependencies all have native ARM64 builds that winget resolves
; automatically.
ArchitecturesAllowed=x64compatible arm64
ArchitecturesInstallIn64BitMode=x64compatible arm64
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
; Multi-arch payload: ship both x64 and arm64 trees, install one based on the
; running CPU architecture. Inno's Check parameter is evaluated at install
; time; files whose Check returns False are skipped entirely.
Source: "..\publish-arm64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: IsArm64
Source: "..\publish-x64\*";   DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: not IsArm64

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

procedure CheckAndInstallDependencies();
var
  MissingList: String;
  MissingCount, InstalledCount, Index, Total: Integer;
  ResultCode: Integer;
  OkDotNet, OkAz, OkMsrdc: Boolean;
  Summary: String;
begin
  { Build the missing list. }
  MissingList := '';
  MissingCount := 0;
  if not IsDotNet8DesktopInstalled() then begin
    MissingList := MissingList + #13#10 + '  - .NET 8 Desktop Runtime';
    Inc(MissingCount);
  end;
  if not IsAzCliInstalled() then begin
    MissingList := MissingList + #13#10 + '  - Azure CLI';
    Inc(MissingCount);
  end;
  if not IsMsrdcInstalled() then begin
    MissingList := MissingList + #13#10 + '  - Microsoft Remote Desktop client (msrdc)';
    Inc(MissingCount);
  end;

  if MissingCount = 0 then Exit;

  if MsgBox(
       'BastionFlow needs the following components that are not installed yet:' + #13#10 +
       MissingList + #13#10 + #13#10 +
       'Install them now via winget?' + #13#10 +
       '(A console window will open for each one — please wait until it closes before continuing.)',
       mbConfirmation, MB_YESNO) <> IDYES then Exit;

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

  Total := MissingCount;
  Index := 0;
  InstalledCount := 0;
  OkDotNet := True; OkAz := True; OkMsrdc := True;

  if not IsDotNet8DesktopInstalled() then
  begin
    Inc(Index);
    WizardForm.StatusLabel.Caption :=
      Format('[%d/%d] Installing .NET 8 Desktop Runtime — see the console window...', [Index, Total]);
    WizardForm.Update();
    Exec('cmd.exe',
         '/c winget install --id "Microsoft.DotNet.DesktopRuntime.8" --accept-package-agreements --accept-source-agreements',
         '', SW_SHOW, ewWaitUntilTerminated, ResultCode);
    OkDotNet := IsDotNet8DesktopInstalled();
    if OkDotNet then Inc(InstalledCount);
  end;

  if not IsAzCliInstalled() then
  begin
    Inc(Index);
    WizardForm.StatusLabel.Caption :=
      Format('[%d/%d] Installing Azure CLI — see the console window...', [Index, Total]);
    WizardForm.Update();
    Exec('cmd.exe',
         '/c winget install --id "Microsoft.AzureCLI" --accept-package-agreements --accept-source-agreements',
         '', SW_SHOW, ewWaitUntilTerminated, ResultCode);
    OkAz := IsAzCliInstalled();
    if OkAz then Inc(InstalledCount);
  end;

  if not IsMsrdcInstalled() then
  begin
    Inc(Index);
    WizardForm.StatusLabel.Caption :=
      Format('[%d/%d] Installing Microsoft Remote Desktop client — see the console window...', [Index, Total]);
    WizardForm.Update();
    Exec('cmd.exe',
         '/c winget install --id "Microsoft.RemoteDesktopClient" --accept-package-agreements --accept-source-agreements',
         '', SW_SHOW, ewWaitUntilTerminated, ResultCode);
    OkMsrdc := IsMsrdcInstalled();
    if OkMsrdc then Inc(InstalledCount);
  end;

  WizardForm.StatusLabel.Caption :=
    Format('Dependency install complete: %d of %d succeeded.', [InstalledCount, Total]);
  WizardForm.Update();

  { Final summary. Show what succeeded and what didn't. }
  Summary := 'Dependency install finished:' + #13#10;
  if not OkDotNet then Summary := Summary + #13#10 + '  [FAILED] .NET 8 Desktop Runtime — BastionFlow will not start without this.'
  else                 Summary := Summary + #13#10 + '  [OK] .NET 8 Desktop Runtime';
  if not OkAz then     Summary := Summary + #13#10 + '  [FAILED] Azure CLI — BastionFlow needs this to drive Bastion.'
  else                 Summary := Summary + #13#10 + '  [OK] Azure CLI';
  if not OkMsrdc then  Summary := Summary + #13#10 + '  [FAILED] Microsoft Remote Desktop client — connections will fail without this.'
  else                 Summary := Summary + #13#10 + '  [OK] Microsoft Remote Desktop client';

  if OkDotNet and OkAz and OkMsrdc then
    Summary := Summary + #13#10 + #13#10 + 'BastionFlow is ready to launch.'
  else
    Summary := Summary + #13#10 + #13#10 + 'You can install the failed items manually later — see https://github.com/ypoulis-hub/BastionFlow for links.';

  MsgBox(Summary, mbInformation, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    CheckAndInstallDependencies();
end;
