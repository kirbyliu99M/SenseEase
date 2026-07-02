; =============================================================================
; SenseEase Beta V1.1 — Intel OpenVINO GenAI test installer (LEAN, single-backend)
; Inno Setup 6.x. ONE of two variants that differ ONLY in the bundled OV host
; (Python vs C++); pass /DDistRoot + /DOutputName at ISCC time.
;
; Scope: the self-contained app + the OpenVINO GenAI host (packs\intel-ov\host) +
;        the INT4 IR (packs\intel-ov\model). NO FLM, NO Vulkan, NO GGUF, NO
;        head-tracker sidecar — a clean OV-host A/B environment for the Intel box.
;        Under 4 GB => single .exe, NO disk-spanning, NO WMI hardware probe.
; Build:
;   & "...\Inno Setup 6\ISCC.exe" /DDistRoot="...\dist\beta-v1.1-py" \
;       /DOutputName="SenseEase-Beta-V1.1-OV-Python" \
;       "...\installer\SenseEase-beta-v1.1-intel-ov.iss"
;
; ASCII-only (no CJK), so no UTF-8-BOM requirement.
; =============================================================================

#ifndef DistRoot
  #define DistRoot SourcePath + "..\dist\beta-v1.1-py"
#endif
#ifndef OutputName
  #define OutputName "SenseEase-Beta-V1.1-OV"
#endif

#define MyAppName      "SenseEase"
#define MyAppVersion   "1.3.2"
#define MyAppVerName   "SenseEase Beta V1.3.2"
#define MyAppPublisher "ATCC A02_SenseEase"
#define MyAppExeName   "SenseEase.App.exe"
#define IconSource     SourcePath + "..\src\SenseEase.App\Resources\AppIcon.ico"

[Setup]
; Distinct AppId for the Beta-V1.1 Intel-OV test product (NOT the v1.0 unified AppId).
AppId={{8F2C1A40-6B3E-4D2A-9C71-2E5B7A9F4D10}
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
; Pass /DFastBuild for a quick uncompressed re-test build (~5 min vs ~30 min).
#ifdef FastBuild
Compression=none
SolidCompression=no
#else
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
#endif
OutputDir={#SourcePath}
OutputBaseFilename={#OutputName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
; The entire staged dist: the self-contained app + packs\intel-ov\host (the OV host)
; + packs\intel-ov\model (the INT4 IR). Single backend, installed unconditionally.
; NOTE: do NOT exclude *.xml — the OpenVINO IR topology files (openvino_model.xml + tokenizer/detokenizer)
; live under packs\intel-ov\model and MUST ship, else the host loads weights-only and fails (Intel field bug).
; Trim the Vulkan native pack (llama.cpp/ggml-vulkan) — it leaks into the publish via a .csproj content
; rule but Vulkan is trimmed in every Beta V1.2 SKU (OV/FLM hosts only). Saves ~100 MB.
Source: "{#DistRoot}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.log,createdump.exe,\runtimes\win-x64\native\vulkan\*"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{autoprograms}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Registry]
; Uninstall-cleanup only for the app's opt-in autostart Run-key value (the app writes it; Setup never does).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "SenseEase"; Flags: uninsdeletevalue

[UninstallDelete]
; INTENTIONALLY EMPTY — %LOCALAPPDATA%\SenseEase is owned by the running app and persists across reinstalls.

[Code]
// On uninstall, OFFER to delete the user's personal data directory (default Yes, opt-in; never without consent).
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
