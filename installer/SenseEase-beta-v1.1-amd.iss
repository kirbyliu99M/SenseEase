; =============================================================================
; SenseEase Beta V1.1 — AMD FastFlowLM package (COMPLETE, single-backend)
; Inno Setup 6.x. app + head-tracker sidecar + FLM runtime + Q4NX. NO OV, NO
; Vulkan, NO GGUF. AMD-only: installs everything unconditionally (no WMI probe).
; ~4 GB (Q4NX 3.1 GB) => DiskSpanning (.exe + .bin slices, kept together).
; Build:
;   & "...\ISCC.exe" /DFastBuild /O"...\installer\beta-v1.1\AMD" "...\SenseEase-beta-v1.1-amd.iss"
; ASCII-only.
; =============================================================================

#ifndef DistRoot
  #define DistRoot SourcePath + "..\dist\beta-v1.1-amd"
#endif
#define MyAppName      "SenseEase"
#define MyAppVersion   "1.1.0"
#define MyAppVerName   "SenseEase Beta V1.1 (AMD)"
#define MyAppPublisher "ATCC A02_SenseEase"
#define MyAppExeName   "SenseEase.App.exe"
#define IconSource     SourcePath + "..\src\SenseEase.App\Resources\AppIcon.ico"

[Setup]
AppId={{A3D4E5F6-7B8C-4D1E-9F20-3A4B5C6D7E80}
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
OutputBaseFilename=SenseEase-Beta-V1.1-AMD

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
; Everything EXCEPT the Q4NX -> {app}  (the self-contained app + sidecar + packs\amd-flm\flm).
Source: "{#DistRoot}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.log,createdump.exe,\packs\amd-flm\q4nx\*"
; Q4NX -> FLM's OWN per-user store (writable at lowest privilege) so `flm serve qwen3-it:4b` resolves it.
; The dest dir name MUST be the FLM catalog name (Qwen3-4B-Instruct-2507-NPU2).
Source: "{#DistRoot}\packs\amd-flm\q4nx\Qwen3-4B-Instruct-2507-NPU2\*"; DestDir: "{%USERPROFILE}\.flm\models\Qwen3-4B-Instruct-2507-NPU2"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{autoprograms}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "SenseEase"; Flags: uninsdeletevalue

[UninstallDelete]
; INTENTIONALLY EMPTY — %LOCALAPPDATA%\SenseEase is owned by the running app and persists across reinstalls.

[Code]
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
