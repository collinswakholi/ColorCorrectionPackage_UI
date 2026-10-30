#define MyAppName "ColorCorrect Studio"
#ifndef Dist
  #error Pass /DDist=<validated native Windows payload directory>
#endif
#include Dist + "\..\installer-metadata.iss"

[Setup]
AppId={{C0C0A11E-0CC0-4E11-9CC0-000000000001}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersion}
VersionInfoProductVersion={#MyAppVersion}
AppPublisher=Collins Wakholi
DefaultDirName={localappdata}\Programs\ColorCorrect Studio
DefaultGroupName=ColorCorrect Studio
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=ColorCorrect-Studio-{#MyAppVersion}-{#PackageRid}-Setup
Compression=lzma2
SolidCompression=no
LZMAUseSeparateProcess=yes
#if PackageRid == "win-arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
#endif
MinVersion=10.0.22000
PrivilegesRequired=lowest
WizardStyle=modern
UninstallDisplayIcon={app}\app\ColorCorrect.App.exe
LicenseFile=..\LICENSE
InfoBeforeFile=SMARTSCREEN.txt
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "recommended"; Description: "Recommended - CPU and detected GPU support"
Name: "cpu"; Description: "CPU only"
Name: "custom"; Description: "Custom installation"; Flags: iscustom

[Components]
Name: "app"; Description: "ColorCorrect Studio and required application libraries"; Types: recommended cpu custom; Flags: fixed
Name: "directml"; Description: "DirectML acceleration (shared libraries are always retained)"; Types: recommended custom
Name: "webgpu"; Description: "Optional WebGPU acceleration"; Types: recommended custom; Check: WantsWebGpu
Name: "python"; Description: "Optional Python API wheel and examples (Python installed separately)"; Types: recommended cpu custom

[Files]
Source: "{#Dist}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "python\*,optional-payloads\*,*.pdb,onnxruntime_providers_webgpu.dll"; Components: app
Source: "{#Dist}\optional-payloads\webgpu\*"; DestDir: "{app}\optional-payloads\webgpu"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: app
Source: "{#Dist}\onnxruntime_providers_webgpu.dll"; DestDir: "{app}"; Flags: ignoreversion; Components: webgpu
Source: "{#Dist}\python\*"; DestDir: "{app}\python"; Flags: ignoreversion recursesubdirs; Components: python

[Icons]
Name: "{group}\ColorCorrect Studio"; Filename: "{app}\ColorCorrect-Studio.cmd"; IconFilename: "{app}\app\ColorCorrect.App.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\ColorCorrect Studio"; Filename: "{app}\ColorCorrect-Studio.cmd"; IconFilename: "{app}\app\ColorCorrect.App.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop icon"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "installvcruntime"; Description: "Install the included Microsoft Visual C++ runtime (administrator permission required)"; GroupDescription: "Missing prerequisite:"; Check: NeedsVCRuntime

[Run]
Filename: "{app}\ColorCorrect-Studio.cmd"; Description: "Launch ColorCorrect Studio"; Flags: shellexec nowait postinstall skipifsilent; Check: HasVCRuntime

[Code]
var RuntimeRestart: Boolean;

function WantsWebGpu: Boolean;
var Locator, Service, Adapters: Variant;
begin
  Result := True; { Unknown inventory keeps the optional route available. }
  if WizardSetupType(False) = 'custom' then exit;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('', 'root\CIMV2');
    Adapters := Service.ExecQuery('SELECT Name FROM Win32_VideoController WHERE ConfigManagerErrorCode = 0 AND PNPDeviceID LIKE ''PCI%''');
    Result := Adapters.Count > 0;
  except
    Log('Graphics inventory unavailable; install support and verify afterward.');
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + NewLine + NewLine + MemoTypeInfo + NewLine + MemoComponentsInfo + NewLine + NewLine + MemoTasksInfo + NewLine + NewLine +
    'CPU inference and correction are always available. Selected GPU support uses bundled libraries. Missing Microsoft runtime installation requests administrator access. Graphics drivers are not changed. Setup verifies selected engines after copying files.';
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpSelectComponents then begin
    if WantsWebGpu then
      WizardForm.ComponentsDiskSpaceLabel.Caption := 'A graphics device is present. GPU execution is checked after installation; CPU support is always installed.'
    else
      WizardForm.ComponentsDiskSpaceLabel.Caption := 'No physical PCI graphics device was observed. CPU operation is available; virtual devices require a later execution check.';
  end;
end;


function VersionAtLeast(Installed, Required: String): Boolean;
var I, A, B, P: Integer; Part: String;
begin
  if (Length(Installed) > 0) and (Installed[1] = 'v') then Delete(Installed, 1, 1);
  for I := 1 to 4 do begin
    P := Pos('.', Installed); if P = 0 then P := Length(Installed) + 1;
    Part := Copy(Installed, 1, P - 1); Delete(Installed, 1, P); A := StrToIntDef(Part, 0);
    P := Pos('.', Required); if P = 0 then P := Length(Required) + 1;
    Part := Copy(Required, 1, P - 1); Delete(Required, 1, P); B := StrToIntDef(Part, 0);
    if A > B then begin Result := True; exit; end;
    if A < B then begin Result := False; exit; end;
  end;
  Result := True;
end;

function HasVCRuntime: Boolean;
var Key, Version: String; Installed: Cardinal;
begin
  Installed := 0; Version := '';
#if PackageRid == "win-arm64"
  Key := 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\arm64';
#else
  Key := 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64';
#endif
  Result := RegQueryDWordValue(HKLM64, Key, 'Installed', Installed) and (Installed = 1)
    and RegQueryStringValue(HKLM64, Key, 'Version', Version) and VersionAtLeast(Version, '{#VcVersion}');
end;

function NeedsVCRuntime: Boolean;
begin
  Result := not HasVCRuntime;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var Code: Integer; Redist, Mode, Components, Args: String;
begin
  if CurStep = ssPostInstall then Log('Selected components: ' + WizardSelectedComponents(False));
  if (CurStep = ssPostInstall) and NeedsVCRuntime then begin
    if WizardIsTaskSelected('installvcruntime') and not WizardSilent then begin
      Redist := ExpandConstant('{app}\prerequisites\vc_redist.exe');
      if CompareText(GetSHA256OfFile(Redist), '{#VcSha256}') <> 0 then begin
        MsgBox('The Microsoft prerequisite failed its integrity check. It was not run.', mbError, MB_OK);
        exit;
      end;
      if not ShellExec('runas', Redist, '/install /passive /norestart', '', SW_SHOWNORMAL, ewWaitUntilTerminated, Code) then
        MsgBox('The runtime installation was declined or could not start. Run check-system.ps1 -InstallPrerequisites before starting the app.', mbInformation, MB_OK)
      else if Code = 3010 then RuntimeRestart := True
      else if (Code <> 0) and (Code <> 1638) then
        MsgBox('Microsoft runtime installation failed (code ' + IntToStr(Code) + '). The application files are installed; run check-system.ps1 -InstallPrerequisites to retry.', mbError, MB_OK);
    end;
    if NeedsVCRuntime and not WizardSilent then
      MsgBox('The application files are installed, but the Microsoft runtime prerequisite is incomplete. Run check-system.ps1 -InstallPrerequisites before starting the app. No GPU drivers were changed.', mbInformation, MB_OK);
  end;
  if (CurStep = ssPostInstall) and HasVCRuntime then begin
    Mode := 'recommended'; Components := '';
    if WizardSetupType(False) = 'cpu' then Mode := 'cpu'
    else if WizardSetupType(False) = 'custom' then begin
      Mode := 'custom';
      if WizardIsComponentSelected('webgpu') then Components := 'webgpu';
      if WizardIsComponentSelected('directml') then begin
        if Components <> '' then Components := Components + ',';
        Components := Components + 'directml';
      end;
      if Components <> '' then Components := ' -Components ' + Components;
    end;
    Args := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\setup.ps1') + '" -Mode ' + Mode + Components + ' -Install -Verify -NonInteractive';
    if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Args,
      ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
      Log('Setup preferences could not be saved. Use Check setup in the application to repair.');
  end;
end;

function NeedRestart: Boolean;
begin
  Result := RuntimeRestart;
end;
