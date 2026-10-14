; Optional development CUDA add-on, separate from app-local CPU. Never wipe data.
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

[Files]
Source: "{#Torch}\*"; DestDir: "{localappdata}\CCP-New\libtorch-cuda"; Flags: ignoreversion recursesubdirs

[Code]
function InitializeSetup(): Boolean;
begin
  Result := not DirExists(ExpandConstant('{localappdata}\CCP-New\libtorch-cuda'));
  if not Result then
    MsgBox('The CUDA destination already exists. Keep it; use the app runtime manager or choose a new destination with the staging script.', mbInformation, MB_OK);
end;
