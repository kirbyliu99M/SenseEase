; =============================================================================
; SenseEase Beta V1.1 — AUTO-SELECT package (COMPLETE, all backends)
; Inno Setup 6.x. app + head-tracker sidecar + Intel OV (GenAI host + IR) + AMD
; (FLM + Q4NX). A runtime WMI probe attributes the machine (Intel -> OV, AMD-NPU
; -> FLM, else -> deterministic lexicon). NO Vulkan, NO GGUF. ~6.5 GB => spanning.
; Build:
;   & "...\ISCC.exe" /DFastBuild /O"...\installer\beta-v1.1\Auto" "...\SenseEase-beta-v1.1-auto.iss"
; ASCII-only (no AMD choice page), so no UTF-8-BOM requirement.
; =============================================================================

#ifndef DistRoot
  #define DistRoot SourcePath + "..\dist\beta-v1.1-auto"
#endif
#define MyAppName      "SenseEase"
#define MyAppVersion   "1.3.1"
#define MyAppVerName   "SenseEase Beta V1.3.1"
#define MyAppPublisher "ATCC A02_SenseEase"
#define MyAppExeName   "SenseEase.App.exe"
#define IconSource     SourcePath + "..\src\SenseEase.App\Resources\AppIcon.ico"

[Setup]
AppId={{B4E5F6A7-8C9D-4E2F-A031-4B5C6D7E8F90}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppVerName}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
DefaultDirName={localappdata}\Programs\SenseEase
DefaultGroupName={#MyAppName}
DisableDirPage=auto
DisableProgramGroupPage=auto
PrivilegesRequired=lowest
MinVersion=10.0.19041
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
AppMutex=Local\SenseEase.App.SingleInstance
WizardStyle=modern
SetupIconFile={#IconSource}
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppVerName}
#ifdef FastBuild
Compression=none
SolidCompression=no
#else
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
#endif
DiskSpanning=yes
DiskSliceSize=2100000000
OutputDir={#SourcePath}
OutputBaseFilename=SenseEase-Beta-V1.3.1-Auto

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
; Base: the self-contained app + sidecar; carve out \packs (re-added Check-gated below).
Source: "{#DistRoot}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.log,createdump.exe,\packs\*,\runtimes\win-x64\native\vulkan\*"
; Intel OpenVINO GenAI pack (host + INT4 IR) — Intel boxes only.
Source: "{#DistRoot}\packs\intel-ov\*"; DestDir: "{app}\packs\intel-ov"; Check: IsIntelCpu; Flags: ignoreversion recursesubdirs createallsubdirs
; AMD FastFlowLM runtime — AMD-NPU boxes only. FlmServerLauncher.FindExe probes {app}\packs\amd-flm\flm FIRST.
Source: "{#DistRoot}\packs\amd-flm\flm\*"; DestDir: "{app}\packs\amd-flm\flm"; Check: IsAmdNpu; Flags: ignoreversion recursesubdirs createallsubdirs
; AMD Q4NX -> FLM's per-user store (catalog name Qwen3-4B-Instruct-2507-NPU2) — AMD-NPU only.
Source: "{#DistRoot}\packs\amd-flm\q4nx\Qwen3-4B-Instruct-2507-NPU2\*"; DestDir: "{%USERPROFILE}\.flm\models\Qwen3-4B-Instruct-2507-NPU2"; Check: IsAmdNpu; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{autoprograms}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "SenseEase"; Flags: uninsdeletevalue

[InstallDelete]
; Prune the OTHER vendor's pack so a re-attribution (changed hardware / disabled NPU) carries no dead weight.
; [InstallDelete] runs BEFORE [Files]; Check: removes a pack only when THIS install will NOT lay it.
Type: filesandordirs; Name: "{app}\packs\intel-ov"; Check: not IsIntelCpu
Type: filesandordirs; Name: "{app}\packs\amd-flm";  Check: not IsAmdNpu

[UninstallDelete]
; INTENTIONALLY EMPTY — %LOCALAPPDATA%\SenseEase is owned by the running app and persists across reinstalls.

[Code]
// Runtime-mirroring install probe (HardwareProbe / LlmBackendSelector): AMD-NPU -> FLM, Intel -> OV GenAI host,
// else -> deterministic lexicon. Install OPTIMISTICALLY; the runtime launch-verifies. Read-only WMI works at
// PrivilegesRequired=lowest.
var
  GProbed:    Boolean;
  GCpuVendor: String;
  GHasNpu:    Boolean;

function DetectCpuVendor(): String;
var
  Vendor, BrandName: String;
begin
  Result := '';
  if RegQueryStringValue(HKLM, 'HARDWARE\DESCRIPTION\System\CentralProcessor\0', 'VendorIdentifier', Vendor) then
    Result := Vendor;
  if RegQueryStringValue(HKLM, 'HARDWARE\DESCRIPTION\System\CentralProcessor\0', 'ProcessorNameString', BrandName) then
    Result := Result + ' | ' + BrandName;
end;

function HasNpuDevice(): Boolean;
var
  Locator, Services, ObjectSet: Variant;
begin
  Result := False;
  try
    Locator  := CreateOleObject('WbemScripting.SWbemLocator');
    Services := Locator.ConnectServer('localhost', 'root\CIMV2');
    ObjectSet := Services.ExecQuery(
      'SELECT Name FROM Win32_PnPEntity WHERE ' +
      'Name LIKE "%npu compute%" OR Name LIKE "%ipu device%" OR Name LIKE "%amd ipu%" OR ' +
      'Name LIKE "%ai boost%" OR Name LIKE "%neural processor%"');
    Result := (ObjectSet.Count > 0);
  except
    Result := False;
  end;
end;

procedure ProbeHardware();
begin
  if GProbed then Exit;
  GCpuVendor := Lowercase(DetectCpuVendor());
  GHasNpu    := HasNpuDevice();
  GProbed    := True;
end;

function IsAmd(): Boolean;
begin
  ProbeHardware();
  Result := (Pos('amd', GCpuVendor) > 0) or (Pos('ryzen', GCpuVendor) > 0);
end;

function IsIntel(): Boolean;
begin
  ProbeHardware();
  Result := (Pos('intel', GCpuVendor) > 0);
end;

function IsAmdNpu(): Boolean;
begin
  Result := IsAmd() and GHasNpu;
end;

function IsIntelCpu(): Boolean;
begin
  Result := IsIntel();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
  DeleteData, Deleted: Boolean;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\SenseEase');
    if not DirExists(DataDir) then Exit;
    if UninstallSilent then
    begin
      if ExpandConstant('{param:DELETEDATA|0}') = '1' then DeleteData := True
      else Exit;
    end else
      DeleteData := MsgBox('Also delete your SenseEase personal data (settings, chat & calibration history, the user model, and logs)?' + #13#10 + 'Choose No to keep it for a reinstall.', mbConfirmation, MB_YESNO or MB_DEFBUTTON1) = IDYES;
    if DeleteData then
    begin
      Deleted := DelTree(DataDir, True, True, True);
      if (not Deleted) and (not UninstallSilent) then
        MsgBox('Some personal data files could not be removed (they may be in use by another process). You can delete them manually from:' + #13#10 + DataDir, mbInformation, MB_OK);
    end;
  end;
end;
