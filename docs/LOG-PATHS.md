# All log paths and locations

> **Flag:** This document is the canonical enumeration of every log file, user-profile file, persisted-state file, and diagnostic sink the SenseEase desktop app reads or writes on a user's machine. Generated 2026-05-31 at v0.11-Unreleased (HEAD = `c47bb21` on `main`); last updated 2026-06-11, branch `alpha1-w2-engine-integrity` (B1b: H51 diag-path fix, FEAT-13/DEAD-07 migration, collect-logs.ps1, model_trace SenseEaseDataRoot note). Update on every commit that adds, removes, or relocates a path.

Two roots cover all persistent user-machine state:

- **`%LOCALAPPDATA%\SenseEase\`** = `C:\Users\<user>\AppData\Local\SenseEase\` — durable user data + crash logs + chatbot transcripts. Survives reboot. NOT cleared by Windows automatic cleanup.
- **`%TEMP%\`** = `C:\Users\<user>\AppData\Local\Temp\` — diagnostic logs + caches. Subject to Windows automatic cleanup (Storage Sense, manual Disk Cleanup).

A third class is **in-memory only** — looks like a log from the API surface but never touches disk. Easy to miss; explicitly enumerated below.

A fourth class is **repo-tracked baseline artifacts** under `baseline/` and `publish-test/` — captured during development for perf/UVHS reference; NOT written by the running app on user machines.

---

## 1. Quick-reference table (most useful entry point)

| File | Full path | Writer | Purpose | Format | Retention |
|---|---|---|---|---|---|
| `user-preferences.json` | `%LOCALAPPDATA%\SenseEase\user-preferences.json` | [UserPreferences.Save](../src/SenseEase.App/Configuration/UserPreferences.cs#L190) | Single canonical preferences home (AutoExpand, BarTrayColor, Vestibular, HeadTracking opt-in, etc.) | JSON schema v2, atomic tmp+File.Replace | Permanent until user deletes |
| `habituation_profile.json` | `%LOCALAPPDATA%\SenseEase\habituation_profile.json` | [AppConfig.HabituationProfilePath](../src/SenseEase.App/Configuration/AppConfig.cs#L156) → [FileHabituationProfileStore](../src/SenseEase.Infrastructure/Storage/FileHabituationProfileStore.cs) | Phase 4 habituation snapshot — profile, strikes, state, longitudinal_observations, calibration_count | JSON schema v2, atomic tmp+File.Replace | Permanent until user deletes |
| `habituation.json` | `%LOCALAPPDATA%\SenseEase\habituation.json` | [AppConfig.HabituationStatePath](../src/SenseEase.App/Configuration/AppConfig.cs#L182) → [FileHabituationStore](../src/SenseEase.Infrastructure/Storage/FileHabituationStore.cs) | Legacy offset-only file (Phase 3). Coexists with `habituation_profile.json`; presence drives synthesize-defaults edge case in store loader. | JSON (single offset) | Permanent until user deletes |
| `adjustments.jsonl` | `%LOCALAPPDATA%\SenseEase\adjustments.jsonl` | [AppConfig.AdjustmentLogPath](../src/SenseEase.App/Configuration/AppConfig.cs#L148) → [FileAdjustmentLog](../src/SenseEase.Infrastructure/Storage/FileAdjustmentLog.cs) | Per-user personalization adjustment log (Phase 2.4) — survives restarts | JSONL (append) | Permanent until user deletes |
| `chat_history.jsonl` | `%LOCALAPPDATA%\SenseEase\chat_history.jsonl` | [AppConfig.ChatHistoryPath](../src/SenseEase.App/Configuration/AppConfig.cs#L188) → [FileChatHistoryStore](../src/SenseEase.Infrastructure/Storage/FileChatHistoryStore.cs) | Phase 4 main chatbot transcript — survives restarts so returning user sees prior context | JSONL (append, best-effort) | Permanent until user deletes |
| `calibration_history.jsonl` | `%LOCALAPPDATA%\SenseEase\calibration_history.jsonl` | **REMOVED** — writer deleted with calibration subsystem (D-13); migration delete at [App.OnStartup (FEAT-13/DEAD-07)](../src/SenseEase.App/App.xaml.cs) | Phase 5b calibration-mode LLM transcript (content-bearing PII). Writer removed at D-13; file is deleted on first startup post-upgrade. T4 Settings clear-all also deletes it (both are correct). | JSONL (append) — no writer | Deleted by migration; absence is steady state |
| `model_trace.jsonl` | `%LOCALAPPDATA%\SenseEase\model_trace.jsonl` | [LocalLlmChatbotService.LogModelTrace](../src/SenseEase.Infrastructure/Real/LocalLlmChatbotService.cs) | Raw model input + raw output (every chatbot turn) — used to distinguish "model produced empty reply, lexicon backfilled" from "model produced the right reply". Path resolves through `SenseEaseDataRoot.Provider` (set by App.xaml.cs at startup), so test suites redirect via `DataRootOverride` and the trace never lands in the developer's data folder. | JSONL (append) | Permanent; tail to verify model behavior |
| `llama-server.log` | `%LOCALAPPDATA%\SenseEase\llama-server.log` | [LocalLlmChatbotService](../src/SenseEase.Infrastructure/Real/LocalLlmChatbotService.cs) | llama-server.exe stdout+stderr tee via cmd.exe wrapper | Plain text (append) | Truncated on each app start (File.Delete then append) |
| `behavior_log.jsonl` | `%LOCALAPPDATA%\SenseEase\behavior_log.jsonl` | [AppConfig.BehaviorLogPath](../src/SenseEase.App/Configuration/AppConfig.cs) → [DailyBehaviorAggregateWriter](../src/SenseEase.Infrastructure/Storage/DailyBehaviorAggregateWriter.cs) | **UM1** daily passive behaviour aggregate — one **numeric-only** row per day (session minutes, ofi p50/p90, tier modal, spike/stable-window/mask counts, offset delta). No raw text → no PII. | JSONL (append, idempotent by `date`) | Permanent until user deletes / H24 clear / H25 uninstall. No rotation (Pre-Alpha follow-up) |
| `user_model_index.json` | `%LOCALAPPDATA%\SenseEase\user_model_index.json` | [AppConfig.UserModelIndexPath](../src/SenseEase.App/Configuration/AppConfig.cs) → [UserModelIndexStore](../src/SenseEase.Infrastructure/Storage/UserModelIndexStore.cs) | **UM3** incremental-analysis index — `last_analyzed_chat_turn_index` + per-month `bucket_manifest`. Carries `exportable:false` / `deletable_on_clear:true`. | JSON schema v1, atomic tmp+File.Replace | Permanent until user deletes / H24 clear / H25 uninstall |
| `symptom_log.jsonl` | `%LOCALAPPDATA%\SenseEase\symptom_log.jsonl` | [AppConfig.SymptomLogPath](../src/SenseEase.App/Configuration/AppConfig.cs) → [SymptomEventRecorder](../src/SenseEase.Infrastructure/Storage/SymptomEventRecorder.cs) → [FileSymptomEventStore](../src/SenseEase.Infrastructure/Storage/FileSymptomEventStore.cs) | **Beta-1 Phase-1** structured symptom/comfort events — the explicit **FMS 0–20 self-reports** (D-18/19) + daily NL→FMS estimates (D-21). One `SymptomEvent`/line (kind, measured, scale, value, context_tier, intervention_state, trigger, foreground_key). **Patient-reported health data**; feeds the B1 selector + Eye-Care chart. `exportable:false`. H24 clear deletes it. | JSONL (append, best-effort) | Permanent until user deletes / H24 clear / H25 uninstall |
| `fms_log.jsonl` | `%LOCALAPPDATA%\SenseEase\fms_log.jsonl` | [AppConfig.FmsLogPath](../src/SenseEase.App/Configuration/AppConfig.cs) → [FmsCheckInService.Record](../src/SenseEase.App/Services/FmsCheckInService.cs) | **Chatbot-tuning sprint (2026-06-19)** dedicated **dev-monitoring** log of the in-chat FMS quick-rate — one clean row per response (ts, fms 0–20, scaleMax, trigger, app_context, tier, protection_state, **mode + ofi** = the efference-copy context [Driver/Passenger weighting + raw on-screen motion], so a rating is readable against whether the provoking motion was self-generated, lang, source). Parallel to the NPS `survey_log.jsonl`; ALSO drives the adaptive 7-point window (re-centres on the last pick). H24 clear deletes it. | JSONL (append, best-effort) | Permanent until user deletes / H24 clear |
| `survey_log.jsonl` | `%LOCALAPPDATA%\SenseEase\survey_log.jsonl` | [NpsService.RecordResponse](../src/SenseEase.App/Services/NpsService.cs) | **NPS KPI** — one row per satisfaction response (ts, score 1–5, scaleMax, comment, appVersion, lang). Comment field may carry free text. | JSONL (append, best-effort) | Permanent until user deletes |
| `nps_state.json` | `%LOCALAPPDATA%\SenseEase\nps_state.json` | [NpsService.Save](../src/SenseEase.App/Services/NpsService.cs) | NPS scheduler state (LastAskedUtc, LastScore, AskCount, DismissedUntilUtc, SessionsSeen) — the cooldown/cadence gate. | JSON, atomic tmp+Move | Permanent until user deletes |
| `crash.log` | `%LOCALAPPDATA%\SenseEase\crash.log` | [App.xaml.cs:18-86](../src/SenseEase.App/App.xaml.cs#L18-L86) | Crash logger — Dispatcher / AppDomain.UnhandledException / TaskScheduler.UnobservedTaskException. Best-effort, never throws inside itself. | Plain text (append) | Permanent. **First file to check on "cannot start" reports.** |
| `first-run-complete.flag` | `%LOCALAPPDATA%\SenseEase\first-run-complete.flag` | [AppConfig.MarkFirstRunComplete](../src/SenseEase.App/Configuration/AppConfig.cs#L171) | Empty marker file — presence is the entire signal. Drives dashboard welcome banner suppression. | Empty (mtime = first-completion timestamp) | Permanent until user deletes |
| `wgc_diag.txt` | `%LOCALAPPDATA%\SenseEase\diag\wgc_diag.txt` | [WgcDiagLog.Append](../src/SenseEase.Infrastructure/Real/WgcDiagLog.cs#L43) | **Primary diagnostic sink.** WGC Sensing + Sidecar lifecycle + Tray ctor + MainWindow load + 9 ex-SafeLog sites all route here. Clause-5 reference: append-only, retry-on-IOException, `[N=xxxxxxx]` monotonic sequence prefix. H51: moved from `%TEMP%` to `%LOCALAPPDATA%\SenseEase\diag\` so the usage-time fingerprint is cleaned by H25 uninstall instead of leaking across runs until 30-day %TEMP% auto-cleanup. Rotates to `wgc_diag.prev.txt` at 5 MB. | Plain text (append, rotated at 5 MB) | Durable; cleaned by H25 uninstall |
| `wgc_diag.lost.log` | `%LOCALAPPDATA%\SenseEase\diag\wgc_diag.lost.log` | [WgcDiagLog](../src/SenseEase.Infrastructure/Real/WgcDiagLog.cs#L46) | **Orthogonal loss sink** (UVHS Playbook clause 5). When primary sink IOException retry exhausts, message goes here. SilentLossCount counter exposed for invariant tests. H51: moved from `%TEMP%` alongside `wgc_diag.txt`. | Plain text (append) | Durable; cleaned by H25 uninstall |
| `wgc_ofi_trace.txt` | `%TEMP%\wgc_ofi_trace.txt` | [WgcSensingProvider.OnTick](../src/SenseEase.Infrastructure/Real/WgcSensingProvider.cs#L313) | Per-tick OFI trace (env-var gated; zero overhead in production). Header line written once on Start; per-tick rows appended. | CSV-like text (append) | Subject to %TEMP% auto-cleanup |
| `display-baseline.json` | `%LOCALAPPDATA%\SenseEase\display-baseline.json` | **REMOVED at v0.11** ([App.OnStartup migration](../src/SenseEase.App/App.xaml.cs#L190-L218)) | Display Comfort decommissioned. Idempotent one-time delete on startup; never re-created. | — | Deleted; absence is the steady state |

---

## 2. By location

### 2.1 `%LOCALAPPDATA%\SenseEase\` — durable user data

Resolved via `Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)` + `"SenseEase"` subdirectory. Subdirectory is created on demand by each writer (no centralized init).

#### Profiles & state (atomic JSON)

- **[user-preferences.json](../src/SenseEase.App/Configuration/UserPreferences.cs#L190)** — Pop-up sprint Task 5 + M8 head-tracking fields. Schema v2 (v1 also loads via roll-forward). Atomic write via [AtomicJsonWriter](../src/SenseEase.Infrastructure/Storage/AtomicJsonWriter.cs) (tmp + File.Replace) so Process.Kill mid-write leaves file at either old or new value, never half-written. Fields: `auto_expand_notifications`, `bar_tray_color_enabled`, `use_new_bar_overlay`, `minimize_to_tray_on_close`, `run_at_windows_startup`, `head_tracking_enabled`, `head_tracking_follow_mode`, `head_tracking_escape_hatch_enabled`. ResetToDefaults is public+static for chatbot intent.
- **[habituation_profile.json](../src/SenseEase.App/Configuration/AppConfig.cs#L156)** — JSON schema v2 (Phase 4). Fields: profile (sensitive/balanced/firm), strikes, state (Running/Cooldown/PausedAwaitingUserConfirm), longitudinal_observations[], calibration_count, medical_advisory_shown, personal_pressure_scale, last_calibration_at_iso. Atomic via [FileHabituationProfileStore](../src/SenseEase.Infrastructure/Storage/FileHabituationProfileStore.cs). **Schema (still v3) now also carries `vestibular_protection_enabled` (bool)** — the persistent auto-profiling vestibular signal (see `docs/HABITUATION-PROFILE.md`); additive, default false.
- **[habituation.json](../src/SenseEase.App/Configuration/AppConfig.cs#L182)** — Phase 3 offset-only (legacy). Untouched semantics; coexists with the snapshot. Its presence is the signal for the Stage 6c app-data-wipe edge case in FileHabituationProfileStore (synthesize defaults-only snapshot when `_profile.json` is missing but `habituation.json` survives → user had prior usage).

#### Transcripts (append-only JSONL)

- **[chat_history.jsonl](../src/SenseEase.App/Configuration/AppConfig.cs#L188)** — main chatbot turns. Best-effort; if append fails, the turn is dropped from history but rendered to UI.
- ~~**calibration_history.jsonl**~~ — **REMOVED (D-13 / FEAT-13/DEAD-07)**. Writer (CalibrationChatbotService) deleted with the calibration subsystem. File is content-bearing PII (calibration Q&A, may include health details). [App.OnStartup](../src/SenseEase.App/App.xaml.cs) performs an idempotent `File.Delete` on every launch (FEAT-13/DEAD-07 migration) so upgraded installs are cleaned on first run. T4 (Settings clear-all) also deletes it. Path constant `AppConfig.CalibrationHistoryPath` is retained as the migration's path source only. Absence is the steady state.
- **[model_trace.jsonl](../src/SenseEase.Infrastructure/Real/LocalLlmChatbotService.cs)** — every (user_text, raw_model_output) pair across both chatbots. Per-line JSON object with `t`, `user`, raw model fields. Critical for distinguishing model-produced replies from lexicon-backfilled empties. Used as a verbatim trace source by [HabituationOverrideParserRealLlmTraceTests](../tests/SenseEase.Tests/HabituationOverrideParserRealLlmTraceTests.cs).
- **[adjustments.jsonl](../src/SenseEase.App/Configuration/AppConfig.cs#L148)** — per-user personalization adjustments. Survives restarts. [FileAdjustmentLog](../src/SenseEase.Infrastructure/Storage/FileAdjustmentLog.cs) is the writer.

#### Self-report & survey data (append-only) — the "profile the user" data sinks

- **[symptom_log.jsonl](../src/SenseEase.App/Configuration/AppConfig.cs)** — Beta-1 Phase-1 structured symptom/comfort events; the **FMS 0–20 self-reports** (the in-chat quick-rate + daily NL→FMS estimates), one `SymptomEvent`/line. Patient-reported health data — feeds the adaptive selector (B1 outcome term) and the Eye-Care chart. Writer: [SymptomEventRecorder](../src/SenseEase.Infrastructure/Storage/SymptomEventRecorder.cs) → [FileSymptomEventStore](../src/SenseEase.Infrastructure/Storage/FileSymptomEventStore.cs). H24 clear deletes it.
- **[fms_log.jsonl](../src/SenseEase.App/Services/FmsCheckInService.cs)** — chatbot-tuning sprint (2026-06-19): a dedicated, clean **dev-monitoring** log of the in-chat FMS quick-rate (one row per response), co-located with the NPS data. Also drives the adaptive 7-point window (re-centres on the last pick). Additive to `symptom_log.jsonl` (the clinical store), not a replacement. H24 clear deletes it.
- **[survey_log.jsonl](../src/SenseEase.App/Services/NpsService.cs)** + **[nps_state.json](../src/SenseEase.App/Services/NpsService.cs)** — NPS KPI responses (score 1–5 + optional comment) + the scheduler cadence state.

> Note: the chatbot's *narrative* user-profile (the longitudinal observations the LLM writes) lives inside `habituation_profile.json` (`longitudinal_observations[]`), with the analysis watermark in `user_model_index.json` and the passive numeric feed in `behavior_log.jsonl` — all three above. The files in this subsection are the **self-reported** side of the same picture.

#### llama-server transcript (append text)

- **[llama-server.log](../src/SenseEase.Infrastructure/Real/LocalLlmChatbotService.cs)** — tee of llama-server.exe stdout+stderr via cmd.exe wrapper (direct redirect from a no-console WPF parent caused server stub exit; cmd wrapper gives proper console + file-backed stdio). **Truncated** on each app start (`File.Delete(logPath)` at startup). Captures model load progress and any server-side errors.

#### Crash log (append text, NEVER throws inside itself)

- **[crash.log](../src/SenseEase.App/App.xaml.cs#L62)** — three subscribers: `DispatcherUnhandledException`, `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`. Format: `[ISO-8601 UTC] [scope] message + stack`. Append-only via `File.AppendAllText`. Best-effort: any IO failure is swallowed (cannot afford to throw inside a crash handler).

#### First-run flag (empty file)

- **[first-run-complete.flag](../src/SenseEase.App/Configuration/AppConfig.cs#L165)** — file presence is the entire signal (banner shown iff file does not exist). Content = UTC ISO-8601 timestamp written via `File.WriteAllText` but never read. Lives alongside other LocalAppData state so a manual wipe of `%LOCALAPPDATA%\SenseEase\` also resets first-run.

#### Removed (one-time migration deletes on startup)

- **`display-baseline.json`** — Display Comfort feature decommissioned (UI Consolidation Sprint Phase 4). [App.OnStartup](../src/SenseEase.App/App.xaml.cs#L190-L218) performs an idempotent `File.Delete` on every launch; absence is the steady state. Migration is in [CHANGELOG.md v0.11 Removed](../CHANGELOG.md). Outcome of delete is logged through [WgcDiagLog](../src/SenseEase.Infrastructure/Real/WgcDiagLog.cs).
- **`calibration_history.jsonl`** — Calibration subsystem removed (D-13 / FEAT-13/DEAD-07). Content-bearing PII; writer (CalibrationChatbotService) deleted. [App.OnStartup (FEAT-13/DEAD-07)](../src/SenseEase.App/App.xaml.cs) performs an idempotent `File.Delete` via `AppConfig.CalibrationHistoryPath` on every launch so upgraded installs are cleaned even if the user never visits Settings. T4 (Settings clear-all) also deletes it. Outcome logged through [WgcDiagLog](../src/SenseEase.Infrastructure/Real/WgcDiagLog.cs).

### 2.2 `%LOCALAPPDATA%\SenseEase\diag\` — diagnostic sinks (H51: moved from `%TEMP%`)

**H51 (B1a):** `wgc_diag.txt` and `wgc_diag.lost.log` were moved from `%TEMP%` to `%LOCALAPPDATA%\SenseEase\diag\` so the usage-time fingerprint is cleaned by the H25 uninstall flow instead of leaking across runs until 30-day `%TEMP%` auto-cleanup. The fallback (if `LocalApplicationData` is unavailable) is still `%TEMP%`.

Resolved via `Environment.GetFolderPath(SpecialFolder.LocalApplicationData)` + `"SenseEase\diag"`. Directory created on first `WgcDiagLog.Append` call.

- **[wgc_diag.txt](../src/SenseEase.Infrastructure/Real/WgcDiagLog.cs#L43)** — **single most important diagnostic file.** UVHS Playbook clause 5 reference implementation:
  - Append-only (no `WriteAllText` ever — historical bug that silently truncated)
  - Retry on `IOException` (concurrent-writer contention)
  - `[N=xxxxxxx]` monotonic sequence prefix on every line (orthogonal probe — gaps detect silent loss)
  - Rotates to `wgc_diag.prev.txt` at 5 MB (H51/H36)
  - 9 ex-`SafeLog` call sites route through this single sink: `[App.xaml.cs]`, `[MainWindow.xaml.cs]`, `[MainViewModel.cs]`, `[TrayIconService.cs]`, `[ValidationModeBanner.cs]`, plus `WgcSensingProvider` lifecycle, sidecar lifecycle, mutex catch, hydrate fail. See [WgcDiagLog.cs](../src/SenseEase.Infrastructure/Real/WgcDiagLog.cs) header XML doc for full call-site enumeration.
- **[wgc_diag.lost.log](../src/SenseEase.Infrastructure/Real/WgcDiagLog.cs#L46)** — independent loss sink (clause 5). When primary append's retry budget exhausts, the message is written here instead. `WgcDiagLog.SilentLossCount` is incremented for invariant testing. **This file existing with content = silent-loss occurred on primary; investigate the parent run.**

### 2.2b `%TEMP%\` — remaining items in temp (subject to OS cleanup)

Resolved via `Path.GetTempPath()` which returns `C:\Users\<user>\AppData\Local\Temp\` on Windows 11.

- **[wgc_ofi_trace.txt](../src/SenseEase.Infrastructure/Real/WgcSensingProvider.cs#L313)** — env-var gated (`SENSEEASE_WGC_OFI_TRACE=1`). Zero overhead when off (env var read once at Start). Per-tick OFI trace for dev diagnostics. Header line written once; one row per tick. Trace path also referenced from [MainWindow.xaml.cs:1057](../src/SenseEase.App/MainWindow.xaml.cs#L1057) for the user-facing "Trace path" hint.

#### Caches (NOT logs — for reference)

- `%TEMP%\llama-b9294-cuda` — llama.cpp CUDA runtime cache. See `.gitignore:41` for restore source.
- `%TEMP%\cudart-b9294` — CUDA runtime cache. Same.

### 2.3 Sidecar process I/O — `head_tracker.py`

Sidecar communicates with the C# parent via **pipes, not files**, on user machines.

- **stdout** → [SidecarHeadPoseReader](../src/SenseEase.Infrastructure/Real/SidecarHeadPoseReader.cs) consumes one JSON pose object per line. Never written to disk on user machines.
- **stderr** → SidecarHeadPoseReader maintains an in-process ring buffer ([_stderrTail](../src/SenseEase.Infrastructure/Real/SidecarHeadPoseReader.cs#L96-L98), default 8 KiB) and dumps the tail to `wgc_diag.txt` via `_log?.Invoke` when the sidecar process exits. Never persisted directly.
- **`head_tracker.py --diag <path>`** — dev-only ad-hoc CSV (columns: `t_ms, yaw_deg, pitch_deg, cx_raw, cy_raw, cx_smoothed, cy_smoothed, face_detected, face_present, mirror, velocity_deg_per_frame, adaptive_alpha`). Path is caller-supplied; not used in production. Reference: [head_tracker.py:545](../tools/head-tracker-sidecar/head_tracker.py#L545).
- **`head_tracker.py --soak`** — emits one JSON report object on stdout (used to produce `soak_report.json` at repo root during dev runs; not user-machine output).

See [docs/HEAD-TRACKING-PRIVACY.md](HEAD-TRACKING-PRIVACY.md) for the privacy posture (sidecar emits only `yaw_deg`, `pitch_deg`, `confidence`, `face_present` — never frames, never landmarks, never identifiers).

### 2.4 Support bundle — `scripts/collect-logs.ps1`

**Canonical support-bundle flow.** Run this script to package diagnostic files into a zip for the test team.

```
powershell -ExecutionPolicy Bypass -File scripts\collect-logs.ps1
```

Output: `SenseEase-diag-<yyyyMMdd-HHmm>.zip` on the Desktop.

**Included** (technical diagnostics only):
- `%LOCALAPPDATA%\SenseEase\diag\*` — `wgc_diag.txt`, `wgc_diag.lost.log`, `wgc_diag.prev.txt` (if rotated)
- `%LOCALAPPDATA%\SenseEase\logs\*` — daily-rolled event / chatbot-action audit logs (numeric state + action lifecycle only — no chat text)
- `%LOCALAPPDATA%\SenseEase\crash.log`
- `%LOCALAPPDATA%\SenseEase\llama-server.log`

**Excluded** (PII / health data — NOT included by default):
- `chat_history.jsonl`, `model_trace.jsonl`, `calibration_history.jsonl` (legacy; deleted by migration), `habituation_profile.json`, `behavior_log.jsonl`, `symptom_log.jsonl` (FMS health self-reports), `fms_log.jsonl` (FMS quick-rate dev log), `survey_log.jsonl` (NPS feedback, may carry a free-text comment)

An opt-in full-folder zip command is documented (but commented out) at the bottom of the script for cases where the test team explicitly requests full data.

**`model_trace.jsonl` path note:** resolves through `SenseEaseDataRoot.Provider` (set in `App.xaml.cs` to `AppConfig.DataRoot`). In production this is `%LOCALAPPDATA%\SenseEase\model_trace.jsonl`. In tests the `DataRootOverride` seam redirects it to an isolated temp directory, so the trace never lands in the developer's data folder during test runs.

---

## 3. In-memory only (NOT persisted) — easy to miss

These look like log/store APIs but never touch disk. State is lost on every app restart.

- **`IEventLogStore`** = [InMemoryEventLogStore](../src/SenseEase.Infrastructure/Storage/InMemoryEventLogStore.cs) — bounded ring (`SenseDefaults.EventLogMaxCount`). Raises `Appended` event for live dashboard mirror. **No File* variant exists.** Categories: Setup, System, Habituation, Demo, Calibration, Inference, Feedback, Lux (legacy), Error.
- **`IChatbotActionLog`** = [InMemoryChatbotActionLog](../src/SenseEase.Infrastructure/Storage/InMemoryChatbotActionLog.cs) — tracks `Proposed/Accepted/Declined/Expired/Intercepted` lifecycle states for chatbot-driven habituation overrides (Q4.7). **In-memory only**; survives until app exit, not across restarts.

**Implication for advisor:** if a session boundary matters for evidence-gathering, this state is gone. The persistent counterparts are `model_trace.jsonl` (every chatbot turn raw) + `habituation_profile.json` longitudinal_observations (post-acceptance summaries) + `adjustments.jsonl` (personalization deltas) — none of which capture the in-memory action-log's proposal-state granularity.

---

## 4. Repository-tracked baseline / reference artifacts

NOT user-machine output. Captured during development; committed for advisor + UVHS reference.

| Path | Source | Purpose |
|---|---|---|
| `baseline/m5.1-a-diag.csv` | head_tracker.py `--diag` run | M5.1-A perf reference |
| `baseline/m5.1-c-perf-isolated.json` | C# perf harness | M5.1-C isolated CompositionTarget.Rendering perf reference |
| `baseline/m7-perf-report.md` | manual | M7 real-machine perf UVHS report |
| `baseline/m8-pr-description.md` | manual | M8 head-tracking PR description archive |
| `baseline/overlay-idle-screenshot-instructions.md` + `overlay-idle-observation.md` | manual | UVHS reference for `overlay-idle.png` (human-visual-reference, NOT auto-compared baseline — UVHS Playbook §"Reference auto-artifacts") |
| `baseline/build-warnings.txt`, `tests.txt`, `coldstart-seconds.txt`, `files-snapshot.txt`, `git-state.txt`, `coldstart-instructions.md` | repo-state captures | misc reference |
| `face_landmarker.task` | MediaPipe distribution | head_tracker.py model — bundled, not generated |
| `publish-test/` | `dotnet publish` self-contained output (currently untracked) | smoke-test built binaries pre-tag |
| `soak_report.json` (repo root, currently untracked) | head_tracker.py `--soak` stdout | sidecar soak-run output |

---

## 5. External services touched (not files, but log-adjacent)

- **llama-server.exe** at `127.0.0.1:<port>` (default `--port 8080` per [LocalLlmChatbotService](../src/SenseEase.Infrastructure/Real/LocalLlmChatbotService.cs)). Subprocess started by LocalLlmChatbotService; stdout+stderr tee'd to `llama-server.log` (above). HTTP traffic NOT logged separately — request/response bodies appear in `model_trace.jsonl` (parent-side capture).
- **Windows Event Viewer → Application log** — `.NET Runtime` / `Application Error` entries on hard crash before crash.log handler fires. Referenced by HANDOVER.md:153 (retired, see [PROJECT_HISTORY.md](PROJECT_HISTORY.md)) (archived; see ALPHA_HANDOVER.md (retired, see [PROJECT_HISTORY.md](PROJECT_HISTORY.md)) for current guidance) as the second place to check after `crash.log`.

---

## 6. Retention / rotation / atomicity notes

| Property | Files that have it |
|---|---|
| **Atomic write (tmp + File.Replace)** | `user-preferences.json`, `habituation_profile.json`, `user_model_index.json` (UM3) |
| **Append-only with retry + sequence-prefix (clause 5)** | `wgc_diag.txt` |
| **Append-only best-effort (no clause 5)** | `chat_history.jsonl`, `model_trace.jsonl`, `adjustments.jsonl`, `behavior_log.jsonl` (UM1, idempotent by date), `crash.log`, `wgc_diag.lost.log`, `wgc_ofi_trace.txt` (`calibration_history.jsonl` removed — no writer; migration delete on startup) |
| **Truncated on app start** | `llama-server.log` (deletes then re-creates) |
| **Subject to %TEMP% auto-cleanup** | `wgc_ofi_trace.txt` only (`wgc_diag.txt` and `wgc_diag.lost.log` moved to `%LOCALAPPDATA%\SenseEase\diag\` at H51) |
| **Rotates at 5 MB (single generation)** | `wgc_diag.txt` → `wgc_diag.prev.txt` (H51/H36) |
| **No size cap / no rotation** | All JSONL transcripts + `crash.log`. Diagnostic Storage Sense or manual user delete is the only cleanup. |
| **No max-age** | All persistent files. None expire automatically. |

**JSONL transcripts do not rotate by size or age.** If a chatbot-heavy user runs the app for months, `chat_history.jsonl` and `model_trace.jsonl` grow unbounded. Identified as a Pre-Alpha follow-up. `wgc_diag.txt` is the exception — it rotates at 5 MB since H51/H36.

---

## 7. PII posture (cross-reference)

Per standing instruction "no PII in any logs (no window titles, class names, notification content)":

- `wgc_diag.txt` ✓ — clause-5 enforced: numbers + identifiers only (PIDs, sequence numbers, event-type names, exception types). Recent Phase 4 audit found 0 PII leaks.
- `chat_history.jsonl`, `model_trace.jsonl` — contain user-typed text and model responses. **By design — these ARE the user's conversation.** Not subject to the no-PII rule (the rule targets *system* logs, not user content). (`calibration_history.jsonl` was also in this category; writer removed at D-13, file deleted by migration.)
- `crash.log` — stack traces only; PII-free by construction.
- `head_tracker.py` sidecar — privacy-by-design ([docs/HEAD-TRACKING-PRIVACY.md](HEAD-TRACKING-PRIVACY.md)): emits only numeric angles + face_present boolean. No frames, no landmarks, no identifiers.
- WindowsActivityFilter event sink — explicitly does NOT log window titles or class names per the standing rule.

---

## 8. Maintenance rule

When a commit adds, removes, or relocates a path, update **this document in the same commit**. Treat `LOG-PATHS.md` like CHANGELOG.md — drift between code and doc is a maintainability bug.

Audit checklist for each commit:
1. Did I add a new `Environment.GetFolderPath(...)` or `Path.GetTempPath()` call? → add to §1 + §2.
2. Did I delete a file path? → mark as Removed with the migration commit hash.
3. Did I change an atomic strategy or retention property? → update §6.
4. Did I add a new event-log category or in-memory store? → update §3.

Last verified: 2026-06-19, branch = `alpha1-w2-engine-integrity` (chatbot-tuning sprint). Changes: documented the previously-missing self-report/survey data sinks — `symptom_log.jsonl` (FMS health self-reports, pre-existing gap), `fms_log.jsonl` (NEW dedicated in-chat FMS quick-rate dev log), `survey_log.jsonl` + `nps_state.json` (NPS); added them to the support-bundle exclusion (health/feedback PII) and a §2.1 "profile the user" subsection. Known-stale (not yet fixed): the chatbot service is now `LocalLlmChatbotService.cs` (was `LocalLlmChatbotService.cs`) and the llama-server port is `8181` (doc still says 8080) — track for a follow-up sweep of the §1/§5 references.

Prior: 2026-06-11 — H51 diag-path fix (wgc_diag → %LOCALAPPDATA%\SenseEase\diag\); FEAT-13/DEAD-07 calibration_history.jsonl migration-delete; collect-logs.ps1 support-bundle section; model_trace.jsonl SenseEaseDataRoot note.
