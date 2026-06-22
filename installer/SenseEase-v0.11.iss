; =============================================================================
; SenseEase v0.11 Pre-Alpha Installer
; Inno Setup 6.x script
;
; Generated: 2026-05-31
; Target:    Windows 10 build 19041 (May 2020) or later, x64-compatible
; Scope:     Per-user install (no UAC elevation prompt)
; Payload:   ~3.0 GB dist tree (includes 2 GB GGUF model + Vulkan llama.cpp
;            backend + PyInstaller-bundled head-tracker sidecar with MediaPipe)
;
; -----------------------------------------------------------------------------
; MAINTAINER NOTES
; -----------------------------------------------------------------------------
; 1. AppId is LOCKED FOREVER. Never regenerate. Inno Setup uses AppId as the
;    sole upgrade-detection key; changing it produces a parallel install
;    instead of upgrading in place.
; 2. AppVersion / AppVerName / OutputBaseFilename change every release.
; 3. The user-data directory %LOCALAPPDATA%\SenseEase\ is OUT OF SCOPE for
;    this installer. It is created/managed by the running app at first launch
;    and persisted across reinstalls/uninstalls. Do NOT add it to [Dirs],
;    [Files], or [UninstallDelete].
; 4. Inno Setup uses {{ }} in source to emit a literal { } in the compiled
;    output -- that is why the AppId GUID is wrapped in {{...} below.
; 5. To produce the installer:
;      & "C:\Users\kirby\AppData\Local\Programs\Inno Setup 6\ISCC.exe" `
;          "c:\Users\kirby\Desktop\SenseEase APP\installer\SenseEase-v0.11.iss"
; =============================================================================

; 1.0.0 — Beta V1.0 (2026-06-23). First Beta milestone: supersedes all prior Alpha_Test
; artifacts (0.11.x). Bump MyAppVersion here in lockstep with Directory.Build.props
; <Version>/<FileVersion>/<InformationalVersion> every release.
#define MyAppName        "SenseEase"
#define MyAppVersion     "1.0.0"
#define MyAppVerName     "SenseEase 1.0.0"
#define MyAppPublisher   "ATCC A02_SenseEase"
#define MyAppExeName     "SenseEase.App.exe"
#define MyOutputName     "SenseEase-Setup-1.0.0"

; Repo-relative paths -- the .iss file lives in <repo>\installer\, so go up one.
#define RepoRoot         SourcePath + ".."
#define DistRoot         RepoRoot + "\dist\v0.11-pre-alpha"
#define IconSource       RepoRoot + "\src\SenseEase.App\Resources\AppIcon.ico"

[Setup]
; ----- Identity (DO NOT CHANGE AppId across releases) -----
AppId={{DB7DBFD5-66A1-40CA-A320-4FD9AF0B431C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppVerName}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}

; ----- Install layout: per-user, no elevation -----
DefaultDirName={localappdata}\Programs\SenseEase
DefaultGroupName={#MyAppName}
DisableDirPage=auto
DisableProgramGroupPage=auto
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=

; ----- OS / arch gating (matches net8.0-windows10.0.19041.0 TFM) -----
MinVersion=10.0.19041
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; ----- Single-instance guard (PKG-04) -----
; Must match App.xaml.cs SingleInstanceMutexName exactly.
; Inno Setup checks this mutex before showing the wizard so a second
; installer run while the app is open receives a clear error prompt
; rather than silently patching files that are locked mid-use.
AppMutex=Local\SenseEase.App.SingleInstance

; ----- UI -----
WizardStyle=modern
SetupIconFile={#IconSource}
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppVerName}

; ----- Compression (LZMA2 ultra64 for the 2.9 GB payload) -----
; Note: GGUF is already quantized (Q4_K_M) so compresses poorly; expect ~10-15% reduction.
; Solid compression boosts ratio for the many small framework DLLs at the cost of slower install.
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes

; ----- Output -----
OutputDir={#SourcePath}
OutputBaseFilename={#MyOutputName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Desktop shortcut is opt-in but default-checked, matching modern installer convention.
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
; Phase-3 hardware-differentiated layout. The base entry stages the app + the universal Vulkan
; runtime + the head-tracker sidecar (everything EXCEPT the per-hardware packs, which are carved
; out by Excludes here and re-added Check:-gated below). The install-time probe ([Code]) attributes
; the machine; the RUNTIME launch-verifies + always falls back to Vulkan, so install optimistically.
;   ignoreversion          -- always overwrite (we don't rely on FileVersion stamps)
;   recursesubdirs         -- include models\, runtimes\, locale subfolders
;   createallsubdirs       -- preserve directory structure
;   Excludes (H28 + Phase-3)-- never package debug symbols / doc XML / stray logs / createdump; AND
;                              carve out \models, \packs, the OpenVINO native dir (re-added below).
Source: "{#DistRoot}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.xml,*.log,createdump.exe,\models\*,\packs\*,\runtimes\win-x64\native\openvino\*"
;
; --- Universal GGUF (the Vulkan AND OpenVINO rungs both load it): everyone EXCEPT an AMD-NPU box
;     that chose "Efficient" (Q4NX-only). The AMD default is Maximum, so AMD normally gets it too. ---
Source: "{#DistRoot}\models\*"; DestDir: "{app}\models"; Check: WantsGguf; Flags: ignoreversion recursesubdirs createallsubdirs
;
; --- Intel OpenVINO llama-server build: only on Intel boxes (Check: IsIntelCpu). ---
Source: "{#DistRoot}\runtimes\win-x64\native\openvino\*"; DestDir: "{app}\runtimes\win-x64\native\openvino"; Check: IsIntelCpu; Flags: ignoreversion recursesubdirs createallsubdirs
;
; --- AMD FastFlowLM runtime: only AMD-NPU boxes. FlmServerLauncher.FindExe probes {app}\packs\amd-flm\flm FIRST. ---
Source: "{#DistRoot}\packs\amd-flm\flm\*"; DestDir: "{app}\packs\amd-flm\flm"; Check: IsAmdNpu; Flags: ignoreversion recursesubdirs createallsubdirs
;
; --- AMD Q4NX NPU model -> FLM's OWN per-user store (writable at lowest privilege) so
;     `flm serve qwen3-it:4b` resolves it. The dest dir name MUST be Qwen3-4B-Instruct-2507-NPU2
;     (FLM model_list.json's catalog name for the tag); any other name => FLM finds nothing => Vulkan. ---
Source: "{#DistRoot}\packs\amd-flm\q4nx\Qwen3-4B-Instruct-2507-NPU2\*"; DestDir: "{%USERPROFILE}\.flm\models\Qwen3-4B-Instruct-2507-NPU2"; Check: IsAmdNpu; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Start-menu shortcut (always created).
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
; Desktop shortcut (Tasks-gated).
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon
; Uninstall entry in the Start menu group (useful for testers who want to wipe the install).
Name: "{autoprograms}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[Run]
; Offer to launch immediately after install. Unchecked-by-default via "nowait postinstall skipifsilent".
; postinstall  -- shows the "Launch SenseEase" checkbox on the Finished page
; nowait       -- don't block the installer on the app's process
; skipifsilent -- don't auto-launch during /SILENT or /VERYSILENT installs (CI / scripted)
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[InstallDelete]
; PKG-05: forward hygiene — converge historical payload shapes on uninstall/upgrade.
; These entries are APPEND-ONLY across releases; never remove a row that existed in
; a prior release (we cannot know what shape an older install left on disk).
;
; All entries use Type: filesandordirs so the directive is a safe no-op when the
; path is absent (either this release never wrote it, or a prior release already
; cleaned it up). Inno Setup silently skips missing paths.
;
; --- ISA / GPU backend variant directories (shipped in older builds; Vulkan-only
;     from v0.10 onward — avx/avx2/avx512/noavx/cuda12 are pure dead weight) ---
Type: filesandordirs; Name: "{app}\runtimes\win-x64\native\cuda12"
Type: filesandordirs; Name: "{app}\runtimes\win-x64\native\avx"
Type: filesandordirs; Name: "{app}\runtimes\win-x64\native\avx2"
Type: filesandordirs; Name: "{app}\runtimes\win-x64\native\avx512"
Type: filesandordirs; Name: "{app}\runtimes\win-x64\native\noavx"
;
; --- Dropped llama-cli tool executables (never launched at runtime; stage script
;     already trims them from the dist tree, but older installs may still have them) ---
Type: filesandordirs; Name: "{app}\runtimes\win-x64\native\vulkan\llama-cli.exe"
Type: filesandordirs; Name: "{app}\runtimes\win-x64\native\vulkan\llama-bench.exe"
Type: filesandordirs; Name: "{app}\runtimes\win-x64\native\vulkan\llama-quantize.exe"
;
; --- RPC backend DLL (distributed/remote ggml only; never used in the local server) ---
Type: filesandordirs; Name: "{app}\runtimes\win-x64\native\vulkan\ggml-rpc.dll"
;
; --- Samples directory (demo-video staging removed 2026-06-08; older installs may
;     have a stale samples\ tree from pre-v0.10 builds) ---
Type: filesandordirs; Name: "{app}\samples"
;
; --- Phase-3: prune the OTHER vendor's backend pack so a machine that changed hands, or a re-run
;     that re-attributes the hardware (e.g. an NPU driver was disabled), doesn't carry dead weight.
;     [InstallDelete] runs BEFORE [Files], and Check: removes a pack only when THIS install will NOT
;     lay it. (The Q4NX copy in {%USERPROFILE}\.flm is FLM's own per-user store and is left intact.) ---
Type: filesandordirs; Name: "{app}\runtimes\win-x64\native\openvino"; Check: not IsIntelCpu
Type: filesandordirs; Name: "{app}\packs\amd-flm"; Check: not IsAmdNpu

[UninstallDelete]
; INTENTIONALLY EMPTY.
; The uninstaller removes only files/folders that Setup itself created under {app}.
; The runtime data directory %LOCALAPPDATA%\SenseEase\ (settings.json, logs,
; sidecar JSON from head-tracking, soak reports, telemetry baselines) is owned
; by the running app, not by this installer, and MUST persist across reinstalls.
; Do not list {localappdata}\SenseEase here under any circumstances.

[Registry]
; H49: clean up the autostart Run-key value on uninstall.
; The app's AutoStartRegistry helper (src\SenseEase.App\Configuration\AutoStartRegistry.cs)
; writes HKCU\Software\Microsoft\Windows\CurrentVersion\Run value name "SenseEase"
; when the user enables "start on login". Setup itself NEVER creates this value —
; autostart is user-opt-in, owned entirely by the running app.
;
; Delete-only idiom (ValueType: none + uninsdeletevalue):
;   ValueType: none      -- instructs Inno Setup to perform NO write at install
;                           time; combined with the absence of a ValueData clause
;                           this entry is a pure uninstall-cleanup registration.
;   ValueName: "SenseEase" -- the exact Run-key value name the app writes.
;   uninsdeletevalue     -- delete this value when the app is uninstalled, so
;                           no orphan Run entry remains pointing at a deleted EXE.
;
; NOTE: "ValueType: string" with "dontcreatekey" still writes SenseEase="" on
; install because HKCU\...\Run always exists. "ValueType: none" is the correct
; Inno Setup idiom for an uninstall-cleanup-only entry.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "SenseEase"; Flags: uninsdeletevalue

[Code]

// =============================================================================
// Phase-3 hardware-differentiated install probe — mirrors the RUNTIME backend
// selection (HardwareProbe / LlmBackendSelector): AMD-NPU -> FastFlowLM, Intel ->
// OpenVINO, else -> Vulkan. Install OPTIMISTICALLY; the runtime launch-verifies +
// always falls back to Vulkan, so a mis-attribution is safe. Read-only WMI works
// at PrivilegesRequired=lowest. (This .iss MUST stay UTF-8 WITH BOM — the AMD page
// copy below is bilingual zh-Hant + EN.)
// =============================================================================
const
  AMD_CHOICE_EFFICIENT = 0;   // Q4NX-only (NPU); smallest install; keyword fallback if the NPU is unavailable
  AMD_CHOICE_MAXIMUM   = 1;   // Q4NX + GGUF; full assistant keeps working on the iGPU if the NPU fails

var
  GProbed:    Boolean;
  GCpuVendor: String;
  GHasNpu:    Boolean;
  GAmdPage:   TInputOptionWizardPage;

// CPU vendor via the registry (HARDWARE\DESCRIPTION\System\CentralProcessor\0): VendorIdentifier is
// "AuthenticAMD" / "GenuineIntel"; ProcessorNameString adds the marketing name (e.g. "AMD Ryzen AI 9
// HX 370 ..."). Simpler + more robust than a Win32_Processor WMI enumeration; IsAmd/IsIntel only
// substring-match the lower-cased result. HKLM\HARDWARE is not WOW64-redirected, so this is bitness-safe.
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

// NPU presence via WMI: COUNT the Win32_PnPEntity rows whose Name matches an NPU marker. WQL LIKE is
// case-insensitive and mirrors HardwareProbe.NpuMarkers (npu compute [accelerator] / ipu device / amd ipu
// / ai boost / neural processor). Using .Count avoids the for-in loop Inno's Pascal Script lacks. Any
// failure is caught -> False -> the box is treated as non-NPU (safe: it just takes the Vulkan/GGUF path).
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
var
  NpuFlag: String;
begin
  if GProbed then Exit;
  GCpuVendor := Lowercase(DetectCpuVendor());
  GHasNpu    := HasNpuDevice();
  GProbed    := True;
  if GHasNpu then NpuFlag := '1' else NpuFlag := '0';
  Log('SenseEase NPU probe: vendor="' + GCpuVendor + '" hasNpu=' + NpuFlag);
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

// AMD Ryzen-AI NPU box -> the FastFlowLM pack (flm runtime + Q4NX).
function IsAmdNpu(): Boolean;
begin
  Result := IsAmd() and GHasNpu;
end;

// Intel box -> the OpenVINO build (OpenVINO picks NPU->CPU itself; the runtime verifies).
function IsIntelCpu(): Boolean;
begin
  Result := IsIntel();
end;

// Universal GGUF UNLESS this is an AMD-NPU box that chose "Efficient" (Q4NX-only).
function WantsGguf(): Boolean;
begin
  if IsAmdNpu() and (GAmdPage <> nil) then
    Result := (GAmdPage.SelectedValueIndex = AMD_CHOICE_MAXIMUM)
  else
    Result := True;
end;

// The AMD-only Efficient/Maximum choice page; Intel / non-NPU machines never see it.
procedure InitializeWizard();
begin
  if IsAmdNpu() then
  begin
    GAmdPage := CreateInputOptionPage(
      wpSelectDir,
      '助手模型 / Assistant model',
      '為你的 NPU 選擇安裝方式。 / Choose how the on-device assistant installs for your NPU.',
      '螢幕防護與安全機制不受影響。 / Screen protection and the safety floor are never affected.',
      True, False);
    GAmdPage.Add(
      '效率 ～2.5 GB — 助手在 NPU 上執行（最省電、最小安裝）；NPU 不可用時切換為簡易關鍵字模式。' + #13#10 +
      'Efficient ~2.5 GB — runs on the NPU (smallest install); switches to simple keyword mode if the NPU is unavailable.');
    GAmdPage.Add(
      '最高可靠性（建議）～5 GB — 另外安裝在顯示晶片上執行的通用模型，即使 NPU 失效仍維持完整助手品質。' + #13#10 +
      'Maximum reliability (recommended) ~5 GB — also installs the universal model on the graphics chip, so the full assistant keeps working if the NPU fails.');
    GAmdPage.SelectedValueIndex := AMD_CHOICE_MAXIMUM;  // Kirby 2026-06-23: default = Maximum (the GGUF safety floor for Beta)
  end;
end;

// H25 (PII): on uninstall, OFFER to delete the user's personal data directory.
// [UninstallDelete] is intentionally empty so a normal reinstall preserves data;
// this is the explicit, opt-in path for a user who wants a clean removal. Default
// is Yes (recommended) but it never deletes without consent.
//
// PKG-07 behaviour summary:
//   Silent uninstall (/SILENT or /VERYSILENT without /DELETEDATA=1):
//     → data directory is preserved unconditionally (no prompt, no deletion).
//       Scripted/enterprise uninstalls must not touch user data by default.
//   Silent uninstall + /DELETEDATA=1:
//     → data directory is deleted without a prompt (scripted wipe path).
//   Interactive uninstall:
//     → MsgBox prompt, default Yes (locked product decision).
//       DelTree return value is checked; a partial deletion (False) shows a
//       warning so the user knows some files could not be removed.
//
// %LOCALAPPDATA%\SenseEase holds settings.json, chat_history.jsonl,
// calibration_history.jsonl, habituation_profile.json, the adjustment log, the
// (dev-only) model trace, and sidecar/telemetry artifacts — i.e. all on-disk PII.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
  DeleteData: Boolean;
  Deleted: Boolean;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\SenseEase');
    if not DirExists(DataDir) then
      Exit;

    if UninstallSilent then
    begin
      // PKG-07: silent uninstall preserves data by default; /DELETEDATA=1
      // is the explicit scripted-wipe opt-in (no interactive prompt ever).
      if ExpandConstant('{param:DELETEDATA|0}') = '1' then
      begin
        DeleteData := True;
      end else
      begin
        // Silent + no switch → preserve data, no prompt, no deletion.
        Exit;
      end;
    end else
    begin
      // Interactive path: prompt with default Yes (H25 locked decision).
      // NOTE: this script must be saved as UTF-8 WITH BOM so Inno Setup
      // renders the Chinese correctly in the compiled uninstaller.
      DeleteData :=
        MsgBox('刪除您的 SenseEase 個人資料？（建議）' + #13#10 + #13#10
             + '這會移除設定、對話與校正紀錄、個人化模型與記錄檔。' + #13#10
             + 'Also delete your SenseEase personal data (settings, chat & calibration'
             + ' history, the user model, and logs)?' + #13#10
             + '選「否」可保留資料供日後重新安裝。 / Choose No to keep it for a reinstall.',
               mbConfirmation, MB_YESNO or MB_DEFBUTTON1) = IDYES;
    end;

    if DeleteData then
    begin
      Deleted := DelTree(DataDir, True, True, True);
      // PKG-07: warn on partial deletion (interactive only; silent wipe is
      // unattended so a MsgBox would hang). DelTree returns False when one or
      // more items could not be removed (e.g. a file locked by another process).
      if (not Deleted) and (not UninstallSilent) then
      begin
        MsgBox('Some personal data files could not be removed. They may be in use '
             + 'by another process. You can delete them manually from:' + #13#10
             + DataDir,
               mbInformation, MB_OK);
      end;
    end;
  end;
end;
