# Stage a SenseEase demo box so it boots CLEAN: no fresh-install tier-5 seed, no durable
# protection-level floor, no welcome banner, no weekly recap mid-demo.
#
# WHY THIS EXISTS (defect being worked around, not fixed):
#   On a genuine fresh install, ApplyAutoProfilingStartDefaults seeds VimsTier = 5
#   (SenseDefaults.AutoProfilingStartLevel) and holds it for 60 s. During that hold the
#   displayed tier is Math.Max(naturalTier, PersistentOverlayBaselineTier) with naturalTier
#   pinned at 5, so the strength slider CANNOT go below 5. If the user nudges the level at
#   all during that window (off 5 and back, or +/- pills), SetIntensityLevel persists
#   persistent_overlay_baseline_tier = 5 + persistent_overlay_enabled = true, which is a
#   HARD FLOOR on every subsequent boot. Nothing automatic ever lowers it. That is the
#   "stuck at level 5, had to reset the app" field report.
#
#   ApplyAutoProfilingStartDefaults early-returns when a habituation profile already exists,
#   so staging one post-onboarding removes the seed, the hold, and therefore the capture
#   window entirely. With no hold, the slider is an ordinary slider again and is safe to
#   drive on stage.
#
# This script only writes %LOCALAPPDATA%\SenseEase\. It does not touch the app install.
# Run it with SenseEase CLOSED — a running app re-saves its in-memory prefs over yours.
#
#   powershell -ExecutionPolicy Bypass -File scripts\stage-demo-box.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\stage-demo-box.ps1 -DryRun
#   powershell -ExecutionPolicy Bypass -File scripts\stage-demo-box.ps1 -Force   # overwrite existing profile
[CmdletBinding()]
param(
  [switch]$DryRun,
  [switch]$Force
)
$ErrorActionPreference = 'Stop'

$root       = Join-Path $env:LOCALAPPDATA 'SenseEase'
$profilePath= Join-Path $root 'habituation_profile.json'
$prefsPath  = Join-Path $root 'user-preferences.json'
$flagPath   = Join-Path $root 'first-run-complete.flag'
$utf8NoBom  = New-Object System.Text.UTF8Encoding($false)
$stamp      = (Get-Date).ToUniversalTime().ToString('o')

function Say($m) { Write-Host $m }
function WriteText($path, $text) {
  if ($DryRun) { Say "      [dry-run] would write $path"; return }
  [System.IO.File]::WriteAllText($path, $text, $utf8NoBom)
}
function Backup($path) {
  if (-not (Test-Path $path)) { return }
  $bak = "$path.pre-demo-bak"
  if (Test-Path $bak) { return }   # keep the FIRST backup; never clobber it
  if ($DryRun) { Say "      [dry-run] would back up -> $bak"; return }
  Copy-Item $path $bak -Force
  Say "      backed up -> $(Split-Path $bak -Leaf)"
}

Say "=== SenseEase demo-box staging ==="
Say "    data root: $root"
if ($DryRun) { Say "    MODE: DRY RUN (nothing will be written)" }

# --- 0. Refuse to run while the app is up (it would re-save over us) ---------------
$running = @(Get-Process -Name 'SenseEase.App' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
  Write-Error "SenseEase.App is running (pid $($running[0].Id)). Close it fully (tray -> Exit) and re-run."
  exit 1
}
Say "[0] app not running - ok"

if (-not (Test-Path $root)) {
  if ($DryRun) { Say "    [dry-run] would create $root" }
  else { New-Item -ItemType Directory -Path $root -Force | Out-Null }
}

# --- 1. Post-onboarding habituation profile (kills the tier-5 seed) ---------------
# Field values mirror what the runtime itself writes (MockInferenceEngine.cs:767 ->
# HabituationProfile.Adapting / "auto_profiling_default"). schema_version 3 is the
# current supported version (FileHabituationProfileStore.SupportedSchemaVersion).
# externally_paused MUST be false: a paused engine would make the demo look calm for
# the wrong reason (that is what invalidated an earlier field test).
if ((Test-Path $profilePath) -and -not $Force) {
  Say "[1] habituation_profile.json already present - leaving it (use -Force to replace)"
  # A pre-existing profile still suppresses the tier-5 seed, so it is fine to keep.
  # But externally_paused=true would mean the ENGINE IS PAUSED - the demo would look
  # calm for the wrong reason (this is what invalidated an earlier field test).
  try {
    $existing = [System.IO.File]::ReadAllText($profilePath) | ConvertFrom-Json
    Say ("      existing: profile={0} tier={1} paused={2}" -f $existing.profile, $existing.current_vims_tier, $existing.externally_paused)
    if ($existing.externally_paused -eq $true) {
      Say "      *** WARNING: externally_paused = TRUE - the engine is PAUSED."
      Say "      *** Protection will not engage. Re-run with -Force to replace this profile."
    }
  } catch {
    Say "      *** WARNING: could not parse the existing profile: $($_.Exception.Message)"
    Say "      *** An unparseable profile loads as null -> the tier-5 seed WILL fire. Use -Force."
  }
} else {
  Backup $profilePath
  $profileJson = @"
{
  "schema_version": 3,
  "updated_at_iso": "$stamp",
  "profile": "ADAPTING",
  "profile_source": "auto_profiling_default",
  "habituation_level": 0,
  "habituation_offset_pp": 0,
  "habituation_offset_cap_pp": 25,
  "current_vims_tier": 1,
  "severe_strike_count": 0,
  "severe_strike_limit": 2,
  "severe_strike_limit_override": null,
  "engine_state": "RUNNING",
  "externally_paused": false,
  "awaiting_user_confirm": false,
  "last_spike_at_iso": null,
  "next_step_eligible_at_iso": null,
  "params_snapshot": {
    "stable_window_ms": 180000,
    "step_interval_ms": 45000,
    "step_magnitude_pp": 1.5
  },
  "calibration_count": 0,
  "last_calibration_at_iso": null,
  "calibration_completeness": null,
  "calibration_path": null,
  "calibration_responses": null,
  "longitudinal_observations": [],
  "longitudinal_observations_v2": {},
  "personal_pressure_scale": 1,
  "auto_adapt_trigger_delta_pp": 0,
  "symptom_dominance_primary": null,
  "medical_advisory_shown": null,
  "calibration_confidence": null,
  "vestibular_protection_enabled": false,
  "was_recovered_from_wipe": false
}
"@
  WriteText $profilePath $profileJson
  Say "[1] wrote habituation_profile.json (ADAPTING, tier 1, engine RUNNING, not paused)"
}

# --- 2. first-run flag: suppress the welcome banner AND the weekly recap ----------
# Content is the first-run UTC timestamp. The recap debuts in the SECOND week of use,
# so stamping NOW guarantees it cannot fire during the demo.
if ((Test-Path $flagPath) -and -not $Force) {
  Say "[2] first-run-complete.flag already present - leaving it"
} else {
  WriteText $flagPath $stamp
  Say "[2] wrote first-run-complete.flag (stamped now -> recap cannot fire this week)"
}

# --- 3. Clear the durable protection-level floor ----------------------------------
# Surgical: preserve language/theme/head-tracking etc. Only the two overlay keys move.
if (Test-Path $prefsPath) {
  Backup $prefsPath
  $prefs = [System.IO.File]::ReadAllText($prefsPath) | ConvertFrom-Json
  $before = "enabled=$($prefs.persistent_overlay_enabled) tier=$($prefs.persistent_overlay_baseline_tier)"
  # -Force on Add-Member updates the property when it already exists.
  $prefs | Add-Member -NotePropertyName 'persistent_overlay_enabled'      -NotePropertyValue $false -Force
  $prefs | Add-Member -NotePropertyName 'persistent_overlay_baseline_tier'-NotePropertyValue 1      -Force
  WriteText $prefsPath ($prefs | ConvertTo-Json -Depth 20)
  if ($DryRun) { Say "[3] would clear overlay floor (currently: $before)" }
  else         { Say "[3] cleared overlay floor  (was: $before  ->  now: enabled=False tier=1)" }
} else {
  Say "[3] no user-preferences.json - app will boot on defaults (enabled=false, tier=1). Nothing to clear."
}

# --- 4. Verify ---------------------------------------------------------------------
Say ""
Say "=== verification ==="
if ($DryRun) {
  Say "    (dry run - nothing written, so nothing to verify)"
} else {
  $p = [System.IO.File]::ReadAllText($profilePath) | ConvertFrom-Json
  Say ("    profile        : {0} / tier {1} / paused={2}" -f $p.profile, $p.current_vims_tier, $p.externally_paused)
  if (Test-Path $prefsPath) {
    $q = [System.IO.File]::ReadAllText($prefsPath) | ConvertFrom-Json
    Say ("    overlay floor  : enabled={0} baseline_tier={1}" -f $q.persistent_overlay_enabled, $q.persistent_overlay_baseline_tier)
  }
  Say "    first-run flag : $(Test-Path $flagPath)"
}
Say ""
Say "NEXT: launch SenseEase and confirm it reads Lv.01 AT REST (not Lv.05)."
Say "      If it reads Lv.05, staging did not take - do not proceed to the demo."
Say ""
Say "ON STAGE - use '/feather 95' then '/feather 10' in the chat box to show the"
Say "      aperture RESIZE (~3x clear-hole radius). It is session-only, persists"
Say "      nothing, and the ASCII command works in the Chinese UI too."
Say ""
Say "      Do NOT drive the popup-bar intensity slider / +- pills on stage: they"
Say "      change darkness only (never geometry), and they re-persist"
Say "      persistent_overlay_enabled=true + a baseline tier, which is the known"
Say "      consent-bypass floor this script just cleared. Recoverable (drag to 1),"
Say "      but there is no reason to touch it - /feather is the lever."
exit 0
