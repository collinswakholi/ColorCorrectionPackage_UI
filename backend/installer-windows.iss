; Inno Setup Script for ColorCorrector
; Build with: iscc installer-windows.iss
; Requires: Inno Setup 6.x (https://jrsoftware.org/isinfo.php)

#define MyAppName "ColorCorrector"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Collins Wakholi"
#define MyAppURL "https://github.com/collinswakholi/ColorCorrectionPackage_UI"
#define MyAppExeName "ColorCorrector.exe"
#define MyAppDescription "Color Correction Package UI - Image Color Correction Tool"

[Setup]
; Basic app information
AppId={{8E9A2B3C-4D5E-6F7A-8B9C-0D1E2F3A4B5C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE.txt
; Output configuration
OutputDir=installer_output
OutputBaseFilename=ColorCorrector-Setup-Windows
; SetupIconFile=..\frontend\public\icon.ico  ; Uncomment if you have an icon file
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; Privileges - standard user (no admin required)
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; Visual appearance
; WizardImageFile=compiler:WizModernImage-IS.bmp
; WizardSmallImageFile=compiler:WizModernSmallImage-IS.bmp

; Architecture
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "quicklaunchicon"; Description: "{cm:CreateQuickLaunchIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked; OnlyBelowVersion: 6.1; Check: not IsAdminInstallMode

[Files]
; Include all files from the PyInstaller dist folder
Source: "dist\ColorCorrector\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; NOTE: Don't use "Flags: ignoreversion" on any shared system files

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "{#MyAppDescription}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon; Comment: "{#MyAppDescription}"
Name: "{userappdata}\Microsoft\Internet Explorer\Quick Launch\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: quicklaunchicon

[Run]
; Option to launch the application after installation
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent shellexec

[Code]
// Custom code to show message after installation
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    // You can add custom post-install actions here
  end;
end;

// Show instructions on the finish page
function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
var
  S: String;
begin
  S := '';
  S := S + 'Installation Summary:' + NewLine;
  S := S + NewLine;
  S := S + MemoDirInfo + NewLine;
  S := S + NewLine;
  S := S + 'After installation:' + NewLine;
  S := S + '  1. Launch ColorCorrector from the Start Menu or Desktop' + NewLine;
  S := S + '  2. The server will start automatically' + NewLine;
  S := S + '  3. Open your browser to http://localhost:5000' + NewLine;
  S := S + '  4. Upload images and perform color correction' + NewLine;
  
  Result := S;
end;

[UninstallDelete]
; Clean up any runtime-generated files
Type: filesandordirs; Name: "{app}\__pycache__"
Type: filesandordirs; Name: "{app}\*.log"
Type: filesandordirs; Name: "{app}\temp"
