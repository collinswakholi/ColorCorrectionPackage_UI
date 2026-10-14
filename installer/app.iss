; ColorCorrect Studio — Windows installer. Ships CPU LibTorch only.
; CUDA is a separate optional per-user runtime. Each worker loads only one tree.
; Intel/AMD iGPU uses DirectML at runtime. No Vulkan LibTorch.
#define MyAppName "ColorCorrect Studio"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "Collins Wakholi"
#define MyAppURL "https://github.com/collinswakholi/ColorCorrectionPackage_UI"
#define Dist "..\dist\win-cpu"
#define TorchCpu "..\dist\libtorch-cpu"

[Setup]
AppId={{C0C0A11E-0CC0-4E11-9CC0-000000000001}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases/tag/v0.1.0
DefaultDirName={autopf}\ColorCorrect Studio
DefaultGroupName=ColorCorrect Studio
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=ColorCorrect-Studio-win-x64
Compression=lzma2
SolidCompression=no
LZMAUseSeparateProcess=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
WizardStyle=modern
UninstallDisplayIcon={app}\app\ColorCorrect.App.exe
LicenseFile=..\LICENSE
InfoBeforeFile=SMARTSCREEN.txt

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[InstallDelete]
; Exact formerly managed video files only. Do not remove user data or runtimes.
Type: files; Name: "{app}\ffmpeg.exe"
Type: files; Name: "{app}\ffprobe.exe"
Type: files; Name: "{app}\ffmpeg-sha256.txt"
Type: files; Name: "{app}\licenses\ffmpeg\LICENSE"
Type: files; Name: "{app}\licenses\ffmpeg\README.txt"
Type: files; Name: "{app}\api\tests\integration_video.py"

[Files]
Source: "{#Dist}\app\*"; DestDir: "{app}\app"; Flags: ignoreversion recursesubdirs; Excludes: "*.pdb"
Source: "{#Dist}\ccp-engine.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Dist}\models\*"; DestDir: "{app}\models"; Flags: ignoreversion recursesubdirs
Source: "{#Dist}\refs\*"; DestDir: "{app}\refs"; Flags: ignoreversion recursesubdirs
Source: "{#Dist}\api\*"; DestDir: "{app}\api"; Flags: ignoreversion recursesubdirs
Source: "{#Dist}\docs\*"; DestDir: "{app}\docs"; Flags: ignoreversion recursesubdirs
Source: "{#Dist}\notebooks\*"; DestDir: "{app}\notebooks"; Flags: ignoreversion recursesubdirs
Source: "{#Dist}\test_images\*"; DestDir: "{app}\test_images"; Flags: ignoreversion recursesubdirs
Source: "{#Dist}\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion recursesubdirs
Source: "{#Dist}\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Dist}\SHA256SUMS.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Dist}\build-info.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Dist}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Dist}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Dist}\run.cmd"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
; Exactly the freshly staged, checksum-covered runtime DLLs, including vccorlib.
Source: "{#Dist}\*.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Dist}\libtorch\*"; DestDir: "{app}\libtorch"; Flags: ignoreversion recursesubdirs
Source: "..\scripts\install_libtorch_cuda.ps1"; DestDir: "{tmp}"; Flags: deleteafterinstall
Source: "..\scripts\runtime_hashes.psd1"; DestDir: "{tmp}"; Flags: deleteafterinstall
Source: "..\scripts\install_libtorch_cuda.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "..\scripts\runtime_hashes.psd1"; DestDir: "{app}\scripts"; Flags: ignoreversion

[Icons]
Name: "{group}\ColorCorrect Studio"; Filename: "{app}\app\ColorCorrect.App.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\ColorCorrect Studio"; Filename: "{app}\app\ColorCorrect.App.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop icon"; GroupDescription: "Additional icons:"; Flags: unchecked

[Run]
Filename: "{app}\app\ColorCorrect.App.exe"; Description: "Launch ColorCorrect Studio"; Flags: nowait postinstall skipifsilent

[Code]
var
  AccelPage: TInputOptionWizardPage;
  HwLabel: TNewStaticText;
  ReuseCudaPath: String;
  DownloadCuda: Boolean;

function CollectGpuNames(): String;
var
  Locator, Service, Items, Item: Variant;
  I, N: Integer;
  Name: String;
begin
  Result := '';
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\cimv2');
    Items := Service.ExecQuery('SELECT Name FROM Win32_VideoController');
    N := Integer(Items.Count);
    for I := 0 to N - 1 do
    begin
      Item := Items.ItemIndex(I);
      Name := Item.Name;
      if Name <> '' then
      begin
        if Result <> '' then
          Result := Result + ', ';
        Result := Result + Name;
      end;
    end;
  except
    Result := '';
  end;
end;

function HasNvidiaGpu(): Boolean;
var
  Names: String;
begin
  Result := FileExists(ExpandConstant('{sys}\nvapi64.dll')) or
    FileExists(ExpandConstant('{sys}\nvcuda.dll'));
  if Result then
    Exit;
  Names := UpperCase(CollectGpuNames());
  Result := Pos('NVIDIA', Names) > 0;
end;

function HasIntegratedGpu(): Boolean;
var
  Names: String;
begin
  Names := UpperCase(CollectGpuNames());
  Result := (Pos('INTEL', Names) > 0) or (Pos('AMD', Names) > 0) or
    (Pos('RADEON', Names) > 0) or (Pos('UHD', Names) > 0) or
    (Pos('IRIS', Names) > 0);
end;

function DetectCudaToolkit(): String;
begin
  Result := GetEnv('CUDA_PATH');
  if (Result = '') or (not DirExists(Result)) then
    Result := GetEnv('CUDA_PATH_V12_6');
  if (Result <> '') and (not DirExists(Result)) then
    Result := '';
end;

function IsCudaLibTorchDir(const Dir: String): Boolean;
var
  Json, Kind: String;
  KindA: AnsiString;
begin
  Result := False;
  if (Dir = '') or (not FileExists(Dir + '\lib\torch_cuda.dll')) then
    Exit;
  Json := Dir + '\libtorch.json';
  if not FileExists(Json) then
  begin
    Result := True;
    Exit;
  end;
  if not LoadStringFromFile(Json, KindA) then
    Exit;
  Kind := LowerCase(KindA);
  if Pos('"kind":"cuda"', Kind) > 0 then
    Result := True
  else if Pos('"kind": "cuda"', Kind) > 0 then
    Result := True;
  if Result and (Pos('"version":"2.9', Kind) = 0) and (Pos('"version": "2.9', Kind) = 0) then
    Result := False;
end;

function FindExistingCudaLibTorch(): String;
var
  Local, EnvRoot, Cache: String;
begin
  Result := '';
  Local := ExpandConstant('{localappdata}\CCP-New\libtorch-cuda');
  EnvRoot := GetEnv('CCP_LIBTORCH_ROOT');
  if EnvRoot = '' then
    EnvRoot := GetEnv('LIBTORCH_PATH');
  Cache := ExpandConstant('{localappdata}\CCP-New\cache\extract-cuda\libtorch');
  if IsCudaLibTorchDir(Local) then
    Result := Local
  else if IsCudaLibTorchDir(EnvRoot) then
    Result := EnvRoot
  else if IsCudaLibTorchDir(Cache) then
    Result := Cache;
end;

function HardwareCaption(): String;
var
  Names, Toolkit, Existing: String;
begin
  Names := CollectGpuNames();
  if Names = '' then
    Names := 'none reported';
  Result := 'Detected GPUs: ' + Names + #13#10;
  Toolkit := DetectCudaToolkit();
  if Toolkit <> '' then
    Result := Result + 'CUDA toolkit: ' + Toolkit + #13#10
  else if HasNvidiaGpu() then
    Result := Result + 'NVIDIA GPU present. System CUDA toolkit not required; LibTorch bundles its runtime.' + #13#10
  else
    Result := Result + 'No NVIDIA GPU found.' + #13#10;
  Existing := FindExistingCudaLibTorch();
  if Existing <> '' then
    Result := Result + 'Existing LibTorch CUDA 2.9: ' + Existing + #13#10
  else
    Result := Result + 'No local LibTorch CUDA 2.9 tree. Choosing CUDA will download it (~3.8 GB).' + #13#10;
  if HasIntegratedGpu() then
    Result := Result + 'Intel/AMD graphics: plane detect can use DirectML. No extra library. No Vulkan LibTorch.';
end;

function CliWantsCuda(): Boolean;
begin
  Result := CompareText(ExpandConstant('{param:LIBTORCH|cpu}'), 'cuda') = 0;
end;

function InstallCudaTorch(): Boolean;
begin
  if AccelPage = nil then
    Result := CliWantsCuda()
  else
    Result := AccelPage.Values[1];
end;

procedure InitializeWizard();
begin
  ReuseCudaPath := FindExistingCudaLibTorch();
  DownloadCuda := False;
  AccelPage := CreateInputOptionPage(wpSelectDir,
    'Acceleration',
    'CPU LibTorch is in this Setup. CUDA is reused from this PC or downloaded.',
    'CPU is included. CUDA is a separate add-on; each worker loads one runtime. Intel/AMD graphics use DirectML.',
    True, False);
  AccelPage.Add('CPU (default) — included in Setup');
  AccelPage.Add('NVIDIA CUDA — reuse a local 2.9 tree, or download (~3.8 GB)');
  if CliWantsCuda() then
  begin
    AccelPage.Values[1] := True;
    if ReuseCudaPath = '' then
      DownloadCuda := True;
  end
  else
    AccelPage.Values[0] := True;

  AccelPage.CheckListBox.Height := ScaleY(56);
  HwLabel := TNewStaticText.Create(AccelPage);
  HwLabel.Parent := AccelPage.Surface;
  HwLabel.AutoSize := False;
  HwLabel.WordWrap := True;
  HwLabel.Width := AccelPage.SurfaceWidth;
  HwLabel.Top := AccelPage.CheckListBox.Top + AccelPage.CheckListBox.Height + ScaleY(12);
  HwLabel.Height := ScaleY(96);
  HwLabel.Caption := HardwareCaption();
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Msg: String;
begin
  Result := True;
  if (AccelPage = nil) or (CurPageID <> AccelPage.ID) then
    Exit;
  ReuseCudaPath := FindExistingCudaLibTorch();
  DownloadCuda := False;
  if not InstallCudaTorch() then
    Exit;
  if WizardSilent then
  begin
    if ReuseCudaPath = '' then
      DownloadCuda := True;
    Exit;
  end;
  if not HasNvidiaGpu() then
  begin
    Result := SuppressibleMsgBox(
      'No NVIDIA GPU was detected. Continue with CUDA anyway? The app will stay on CPU until a driver is present.',
      mbConfirmation, MB_YESNO, IDNO) = IDYES;
    if not Result then
      Exit;
  end;
  if ReuseCudaPath <> '' then
  begin
    Msg := 'Use existing LibTorch CUDA at:' + #13#10 + ReuseCudaPath + #13#10 + #13#10 +
      'This is not packed in Setup.';
    if SuppressibleMsgBox(Msg, mbConfirmation, MB_YESNO, IDYES) = IDYES then
    begin
      Result := True;
      Exit;
    end;
    ReuseCudaPath := '';
  end;
  Result := SuppressibleMsgBox(
    'No local LibTorch CUDA 2.9 was found. Download official LibTorch 2.9.1 + CUDA 12.6 from download.pytorch.org now (~3.8 GB)? You can keep CPU if you choose No.',
    mbConfirmation, MB_YESNO, IDYES) = IDYES;
  if Result then
    DownloadCuda := True
  else
    AccelPage.Values[0] := True;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
var
  Accel: String;
begin
  if InstallCudaTorch() then
  begin
    if ReuseCudaPath <> '' then
      Accel := 'LibTorch: reuse CUDA at ' + ReuseCudaPath
    else if DownloadCuda then
      Accel := 'LibTorch: download CUDA 2.9.1 after files copy (not in Setup)'
    else
      Accel := 'LibTorch: CPU fallback';
  end
  else
    Accel := 'LibTorch: CPU (default, included)';
  Result := MemoDirInfo + NewLine + NewLine + Accel + NewLine + NewLine + MemoTasksInfo;
end;

function RunCudaStager(const ExtraArgs: String): Boolean;
var
  Cmd, Params, Status: String;
  ResultCode: Integer;
begin
  Cmd := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  Params := '-NoProfile -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{tmp}\install_libtorch_cuda.ps1') +
    '" -Dest "' + ExpandConstant('{localappdata}\CCP-New\libtorch-cuda') + '" ' + ExtraArgs;
  Status := 'Preparing CUDA LibTorch';
  WizardForm.StatusLabel.Caption := Status;
  Result := Exec(Cmd, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Dest, Args: String;
begin
  if CurStep <> ssPostInstall then
    Exit;
  if not InstallCudaTorch() then
    Exit;
  Dest := ExpandConstant('{localappdata}\CCP-New\libtorch-cuda');
  if (ReuseCudaPath <> '') and (CompareText(ReuseCudaPath, Dest) = 0) then
    Exit;
  Args := '';
  if (ReuseCudaPath <> '') and (CompareText(ReuseCudaPath, Dest) <> 0) then
    Args := '-ReusePath "' + ReuseCudaPath + '"'
  else if DownloadCuda then
    Args := ''
  else
    Exit;
  if not RunCudaStager(Args) then
    SuppressibleMsgBox(
      'CUDA LibTorch could not be installed. ColorCorrect Studio will use the CPU library that Setup already wrote.',
      mbInformation, MB_OK, IDOK);
end;
