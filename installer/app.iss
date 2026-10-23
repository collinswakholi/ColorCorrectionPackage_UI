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
Name: "standard"; Description: "Desktop application"
Name: "custom"; Description: "Custom installation"; Flags: iscustom

[Components]
Name: "app"; Description: "ColorCorrect Studio and required application libraries"; Types: standard custom; Flags: fixed
Name: "python"; Description: "Optional Python API wheel and examples (Python installed separately)"; Types: custom

[Files]
Source: "{#Dist}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "python\*,*.pdb"; Components: app
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
var Code: Integer; Redist: String;
begin
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
end;

function NeedRestart: Boolean;
begin
  Result := RuntimeRestart;
end;
