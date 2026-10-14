; CUDA add-on — REPLACES the single libtorch_root.
; Prefer the unified installer/app.iss (CPU vs CUDA page). This script is for
; upgrading an older CPU-only install without re-shipping the app.
; Do not install beside a CPU torch.dll. This script deletes then writes
; %LOCALAPPDATA%\CCP-New\libtorch from dist\libtorch-cuda (fetch_libtorch.ps1 -Kind cuda).
#define MyAppName "ColorCorrect Studio CUDA add-on"
#define MyAppVersion "0.1.0"
#define Torch "..\dist\libtorch-cuda"

[Setup]
AppId={{C0C0A11E-0CC0-4E11-9CC0-000000000002}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={localappdata}\CCP-New
DisableDirPage=yes
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=ColorCorrect-Studio-win-x64-cuda-addon
Compression=lzma2
SolidCompression=no
LZMAUseSeparateProcess=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
WizardStyle=modern
InfoBeforeFile=CUDA-ADDON.txt

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[InstallDelete]
Type: filesandordirs; Name: "{localappdata}\CCP-New\libtorch"

[Files]
Source: "{#Torch}\*"; DestDir: "{localappdata}\CCP-New\libtorch"; Flags: ignoreversion recursesubdirs

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;
