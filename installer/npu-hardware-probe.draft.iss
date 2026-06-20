; =============================================================================
; npu-hardware-probe.draft.iss   —   DRAFT / TEMPLATE, NOT COMPILED
; =============================================================================
; Phase 3 hardware-differentiated install (see docs/PHASE3_NPU_INSTALLER_PLAN.md).
;
; This is a STARTING-POINT [Code] module for the future installer build. It is
; intentionally NOT #include-d by the shipping installer/SenseEase-v0.11.iss and
; is NOT part of any build. When Phase 3 is greenlit:
;   1. Merge these [Code] routines into the shipping .iss (or `#include` this file).
;   2. Wire the Check: predicates onto the per-pack [Files] entries (see the spec §5).
;   3. SAVE THE SHIPPING .iss AS UTF-8 *WITH BOM* — the AMD page copy is bilingual
;      zh-Hant + EN and renders as mojibake without the BOM (same rule the existing
;      uninstall MsgBox relies on).
;   4. Validate the WMI enumeration ON REAL HARDWARE (AMD Ryzen-AI + Intel Core Ultra)
;      — WMI property shapes vary; the runtime launch-verify is the safety net, but
;      install-time should still attribute the common cases correctly.
;
; Design contract (keep in lock-step with the RUNTIME probe):
;   - CPU vendor      <-> SenseEase.Infrastructure.Real.HardwareProbe.DetectCpuVendor
;   - NPU name markers<-> SenseEase.Infrastructure.Real.HardwareProbe.NpuMarkers
;   - AMD -> FLM, Intel -> OpenVINO, else -> Vulkan  <-> LlmBackendSelector
;   - Install OPTIMISTICALLY; the runtime launch-verifies and falls back to Vulkan.
;
; Per-user install (PrivilegesRequired=lowest): read-only WMI works WITHOUT elevation.
; =============================================================================

[Code]

const
  AMD_CHOICE_EFFICIENT = 0;   // Q4NX-only (NPU); smallest install; lexicon fallback if NPU unavailable
  AMD_CHOICE_MAXIMUM   = 1;   // Q4NX + GGUF; full assistant keeps working on the iGPU if the NPU fails

var
  GProbed:      Boolean;   // ProbeHardware ran once
  GCpuVendor:   String;    // 'AuthenticAMD' / 'GenuineIntel' / brand string / ''
  GHasNpu:      Boolean;   // an NPU PnP device matched the marker list
  GAmdPage:     TInputOptionWizardPage;  // the AMD-only Efficient/Maximum choice page

// --- NPU name markers — MUST mirror HardwareProbe.NpuMarkers (lower-case, substring match) ---
function NpuMarkerHit(const LowerName: String): Boolean;
begin
  Result :=
    (Pos('npu compute accelerator', LowerName) > 0) or
    (Pos('npu compute',             LowerName) > 0) or
    (Pos('ipu device',              LowerName) > 0) or
    (Pos('amd ipu',                 LowerName) > 0) or
    (Pos('ai boost',                LowerName) > 0) or
    (Pos('neural processor',        LowerName) > 0);
end;

// --- CPU vendor via WMI Win32_Processor.Manufacturer (e.g. 'AuthenticAMD' / 'GenuineIntel') ---
function DetectCpuVendor(): String;
var
  Locator, Services, ObjectSet, Item: Variant;
begin
  Result := '';
  try
    Locator  := CreateOleObject('WbemScripting.SWbemLocator');
    Services := Locator.ConnectServer('localhost', 'root\CIMV2');
    ObjectSet := Services.ExecQuery('SELECT Manufacturer, Name FROM Win32_Processor');
    // Inno Setup 6 Pascal Script supports for..in over OLE collections.
    for Item in ObjectSet do
    begin
      if not VarIsNull(Item.Manufacturer) then Result := Item.Manufacturer;
      // Prefer the brand Name when present (e.g. 'AMD Ryzen AI 9 HX 370 w/ Radeon 890M')
      // — it lets the runtime/install agree on 'ryzen ai 300' generation gating later.
      if (not VarIsNull(Item.Name)) and (Trim(VarToStr(Item.Name)) <> '') then
        Result := Result + ' | ' + VarToStr(Item.Name);
      Break;  // first processor is representative
    end;
  except
    Result := '';  // WMI unavailable -> treat as 'unknown vendor' -> Vulkan baseline
  end;
end;

// --- NPU presence: any Win32_PnPEntity whose Name matches a marker ---
function HasNpuDevice(): Boolean;
var
  Locator, Services, ObjectSet, Item: Variant;
  LowerName: String;
begin
  Result := False;
  try
    Locator  := CreateOleObject('WbemScripting.SWbemLocator');
    Services := Locator.ConnectServer('localhost', 'root\CIMV2');
    // PNPClass='ComputeAccelerator' would be tighter but isn't populated on all builds;
    // scan names broadly and rely on the marker list (cheap; runs once).
    ObjectSet := Services.ExecQuery('SELECT Name FROM Win32_PnPEntity');
    for Item in ObjectSet do
    begin
      if not VarIsNull(Item.Name) then
      begin
        LowerName := Lowercase(VarToStr(Item.Name));
        if NpuMarkerHit(LowerName) then
        begin
          Result := True;
          Break;
        end;
      end;
    end;
  except
    Result := False;  // can't enumerate -> assume no NPU -> Vulkan baseline
  end;
end;

procedure ProbeHardware();
begin
  if GProbed then Exit;
  GCpuVendor := Lowercase(DetectCpuVendor());
  GHasNpu    := HasNpuDevice();
  GProbed    := True;
  Log('SenseEase NPU probe: vendor="' + GCpuVendor + '" hasNpu=' + IntToStr(Integer(GHasNpu)));
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

// === Check: predicates for [Files] entries ===================================

// AMD Ryzen-AI NPU box -> install the FastFlowLM pack (flm runtime + Q4NX).
function IsAmdNpu(): Boolean;
begin
  Result := IsAmd() and GHasNpu;
end;

// Intel box -> install the OpenVINO llama-server build (OpenVINO picks NPU->CPU itself,
// so we don't hard-gate on NPU presence here; the runtime verifies).
function IsIntelCpu(): Boolean;
begin
  Result := IsIntel();
end;

// Install the universal GGUF UNLESS this is an AMD-NPU box that chose "Efficient"
// (Q4NX-only). Intel + non-NPU machines always get the GGUF (it serves both the
// OpenVINO and Vulkan rungs — one model, no duplicate).
function WantsGguf(): Boolean;
begin
  if IsAmdNpu() and (GAmdPage <> nil) then
    Result := (GAmdPage.SelectedValueIndex = AMD_CHOICE_MAXIMUM)
  else
    Result := True;
end;

// === The AMD-only Efficient/Maximum choice page =============================
// Shown ONLY on an AMD-NPU box. Intel / non-NPU machines never see it (the matched
// pack installs silently — one model, no trade-off). Default = Efficient.
procedure InitializeWizard();
begin
  if IsAmdNpu() then
  begin
    GAmdPage := CreateInputOptionPage(
      wpSelectDir,
      '助手模型 / Assistant model',
      '為你的 NPU 選擇安裝方式。 / Choose how the on-device assistant installs for your NPU.',
      '螢幕防護與安全機制不受影響。 / Screen protection and the safety floor are never affected.',
      True {Exclusive radio}, False {not a ListBox});
    GAmdPage.Add(
      '效率（建議）～2.5 GB — 助手在 NPU 上執行（最省電、最小安裝）。' + #13#10 +
      'Efficient (recommended) ~2.5 GB — runs on the NPU; smallest install. ' +
      'If the NPU is ever unavailable it switches to simple keyword mode.');
    GAmdPage.Add(
      '最高可靠性 ～5 GB — 另外安裝在顯示晶片上執行的通用模型，' +
      '即使 NPU 失效仍維持完整助手品質。' + #13#10 +
      'Maximum reliability ~5 GB — also installs the universal model (runs on the ' +
      'graphics chip), so the full assistant keeps working if the NPU fails.');
    GAmdPage.SelectedValueIndex := AMD_CHOICE_EFFICIENT;  // default = Efficient (FLM validated stable, 2026-06-21)
  end;
end;

// =============================================================================
; Wiring sketch for the [Files] section (for reference — lives in the .iss, not here):
;
;   ; Vulkan baseline (universal fallback) — ALWAYS
;   Source: "{#DistRoot}\runtimes\win-x64\native\vulkan\*"; DestDir: "{app}\runtimes\win-x64\native\vulkan"; Flags: ...
;   ; AMD NPU pack (flm runtime + Q4NX) — only AMD-NPU
;   Source: "{#DistRoot}\packs\amd-flm\*"; DestDir: "{app}"; Check: IsAmdNpu; Flags: ...
;   ; Intel OpenVINO build — only Intel
;   Source: "{#DistRoot}\packs\intel-ov\*"; DestDir: "{app}\runtimes\win-x64\native\openvino"; Check: IsIntelCpu; Flags: ...
;   ; Universal GGUF — everyone EXCEPT AMD-Efficient
;   Source: "{#DistRoot}\models\*"; DestDir: "{app}\models"; Check: WantsGguf; Flags: ...
; =============================================================================
