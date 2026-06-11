# SenseEase Alpha — Handover & Sequential Timeline

> **For the dispatched agent:** this is your briefing. The work is large (≈111 distinct items across three source documents). Read this whole file first; do not start work until you have read sections 1-5. The implementation order in section 6 is non-trivial because items have dependencies that, if violated, will cause you to regress fixes you have just landed.

---

## 1. Project at a glance

**SenseEase** is a Windows WPF .NET 8 desktop app reducing Visually-Induced Motion Sickness (VIMS). Currently shipping as Pre-Alpha v0.11.1 to a closed tester pool. The next release is **Alpha** (formerly internally called "Beta" in earlier planning — naming was changed 2026-06-05).

- **TFM:** `net8.0-windows10.0.19041.0`, self-contained publish
- **Working directory:** `c:\Users\kirby\Desktop\SenseEase APP`
- **Installer:** Inno Setup, per-user at `%LOCALAPPDATA%\Programs\SenseEase`, AppId `DB7DBFD5-66A1-40CA-A320-4FD9AF0B431C` (**LOCKED FOREVER** — never regenerate)
- **Runtime data:** `%LOCALAPPDATA%\SenseEase\` (settings, snapshots, logs, telemetry)
- **Local LLM:** llama-server.exe + Ministral 3B Q4_K_M GGUF, Vulkan backend, HTTP loopback port 8181
- **Head-tracker sidecar:** PyInstaller `--onedir` bundle (MediaPipe FaceLandmarker), stdout JSON IPC
- **Sensing:** Windows Graphics Capture (WGC) for optical-flow analysis at 15 Hz

**Key code map (you will return to these many times):**

| Subsystem | Entry file |
|---|---|
| Inference math | [src/SenseEase.Core/Inference/HabituationEngine.cs](src/SenseEase.Core/Inference/HabituationEngine.cs) |
| Engine wrapper + persistence | [src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs](src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs) |
| Chatbot orchestration | [src/SenseEase.App/ViewModels/ChatbotViewModel.cs](src/SenseEase.App/ViewModels/ChatbotViewModel.cs) |
| Chatbot LLM service | [src/SenseEase.Infrastructure/Real/MistralChatbotService.cs](src/SenseEase.Infrastructure/Real/MistralChatbotService.cs) |
| Calibration LLM service | [src/SenseEase.Infrastructure/Real/CalibrationChatbotService.cs](src/SenseEase.Infrastructure/Real/CalibrationChatbotService.cs) |
| WGC sensing | [src/SenseEase.Infrastructure/Real/WgcSensingProvider.cs](src/SenseEase.Infrastructure/Real/WgcSensingProvider.cs) |
| Sidecar IPC | [src/SenseEase.Infrastructure/Real/SidecarHeadPoseReader.cs](src/SenseEase.Infrastructure/Real/SidecarHeadPoseReader.cs) |
| Sidecar Python | [tools/head-tracker-sidecar/head_tracker.py](tools/head-tracker-sidecar/head_tracker.py) |
| Profile snapshot store | [src/SenseEase.Infrastructure/Storage/FileHabituationProfileStore.cs](src/SenseEase.Infrastructure/Storage/FileHabituationProfileStore.cs) |
| Atomic write helper | [src/SenseEase.Infrastructure/Storage/AtomicJsonWriter.cs](src/SenseEase.Infrastructure/Storage/AtomicJsonWriter.cs) |
| Card window | [src/SenseEase.App/Views/NotificationCardWindow.xaml.cs](src/SenseEase.App/Views/NotificationCardWindow.xaml.cs) |
| Main shell | [src/SenseEase.App/MainWindow.xaml.cs](src/SenseEase.App/MainWindow.xaml.cs) |
| Installer | [installer/SenseEase-v0.11.iss](installer/SenseEase-v0.11.iss) |
| Constants | [src/SenseEase.Core/Configuration/SenseDefaults.cs](src/SenseEase.Core/Configuration/SenseDefaults.cs) |

---

## 2. The three source documents you must inherit

| Source | Items | What it contains |
|---|---|---|
| [docs/ALPHA_BACKLOG.md](docs/ALPHA_BACKLOG.md) | **A1-A8, B1-B7, C1-C7, D1-D3, E1-E4, F1-F2, G1** (~20 actionable items, the rest are CODE-ONLY whitepaper corrections already shipped) | Spec-parity, governance, test coverage, constant migration. From the 30-item whitepaper audit 2026-06-04. |
| [docs/ALPHA_SECURITY_BACKLOG.md](docs/ALPHA_SECURITY_BACKLOG.md) | **H1-H53** (29 Alpha-1 + 24 Alpha-2) | Cybersecurity findings: safety-floor bypass chain, prompt injection, loopback API auth, sidecar hardening, PII leakage, installer / supply chain. From workflow `w5ga7nd83`, 2026-06-05, 21 agents, adversarial-verified. |
| [CodeReview-Opus4.8.md](CodeReview-Opus4.8.md) | **EM1-EM7, NE1-NE31** (38 items) | Runtime defects: storms, races, data loss, silent feature breakage. Reviewer explicitly excludes items already in ALPHA_BACKLOG.md. Reviewer recommends Alpha-1 additions: all 7 EM + NE4/NE5/NE7. |

Every item in those documents includes file:line citations, threat / behaviour description, fix recipe, hour estimate. **Do not re-investigate**; the per-item detail is already there. Your job is to **implement** in the ordering this handover prescribes.

---

## 3. Hard constraints — Alpha review will reject any PR that violates these

These are **load-bearing invariants** locked across multiple sprints. Several are clinical-safety promises; others were the cost of getting prior sprints landed without breaking earlier work.

1. **Q4.5 — Chatbot reset must clear ONLY shader values, NOT habituation offset.** A user's accumulated `_offset` represents weeks of habituation work. The C5 backlog item exists because `MockInferenceEngine.ResetPersonalization` currently violates this. Any new "reset"-like API (e.g. G1's `/reset shader`) must call a narrowed method, never `_habituation.Reset()`.
2. **No PII in system logs.** `LOG-PATHS.md:156-159` enumerates the only logs allowed to contain user content by design: `chat_history.jsonl`, `calibration_history.jsonl`, `model_trace.jsonl`. Everything else (event log, adjustments log, lifecycle log, diag logs, telemetry baselines) must be PII-free. **EM7 and H19-H21 are violations to fix; do not introduce new ones.** When in doubt: log a `pattern_id` not the raw utterance, log `<LOCALAPPDATA>` not the full path, log `DateTime.UtcNow.ToString("o")` not `DateTime.Now`.
3. **HeadTrackingEnabled default MUST be false (M3 invariant).** Webcam off by default; user opt-in required. NE25 fixes a perf bug that violates the spirit of this (head-pose pump runs even when off).
4. **head_tracker.py algorithm core is untouched.** Locked decision from head-tracking sprint. You may add CLI argument handling, env-var hardening (H12-H13), `--diag` removal (H18), but do not change the EWMA smoothing, dead-zone gating, or face-presence logic.
5. **Sidecar emits ONLY luma + landmarks-derived scalars, never frame data.** OBS-3. Enforce in `SidecarFrameDto` (E3) via reflection-based schema test.
6. **Grid is world-locked.** Decision 2 from overlay sprint. Do not introduce viewport-relative grid drawing.
7. **AppId is locked forever.** Inno Setup AppId `DB7DBFD5-66A1-40CA-A320-4FD9AF0B431C`. Regenerating it produces a parallel install instead of upgrading; users who upgrade end up with two copies. **Never touch the AppId line in the .iss.**
8. **Cannot break prior works.** The user has repeatedly stated this: "All advised edits cannot break anything in the prior works. Only the logic of chatbot can be redefined." Existing test cases at HEAD must continue to pass; if a fix REQUIRES updating a test (because the test pinned the WRONG behaviour), call this out explicitly in the PR description with code evidence.
9. **Do not commit until explicitly asked.** Do not push to remote until explicitly asked. The user reviews each PR before commit.
10. **No skipping hooks (`--no-verify`) unless the user explicitly asks.** If pre-commit fails, fix the underlying issue.
11. **No PII (window titles, class names, notification content) in any new log call.** The CLAUDE.md invariant.

---

## 4. Past failures — read all of these before starting

The user has been burned by these patterns repeatedly. Most of the items below were caught by review *after* the failure caused real rework. The pattern recognition is more valuable than the specific instances.

### 4.1 Code-level failures

- **The "C5 pattern" — over-broad reset.** `MockInferenceEngine.ResetPersonalization` calls `_habituation.Reset()` which zeros `_offset`. The intent was "reset shader". The author conflated "reset state" with "reset everything." Lesson: when adding a "reset" method, **enumerate exactly which fields are cleared and document why each is in scope**. C5 is still open; the new `ResetTransientState()` method must be carefully scoped.
- **The "C3 pattern" — duplicate-source drift.** Two services had byte-identical safety-floor phrase tables. Over time they drifted at a single character (ASCII `:` vs full-width `：`). The drift was undetectable because no parity test existed. Lesson: **whenever you copy-paste a constant, write the parity test that proves they cannot drift, or extract to a single source.** This is the H1-H8 wave's primary refactor — `SafetyFloor` becomes a Core utility, both services call it.
- **The "C1 pattern" — magic literal scattered.** `triggerOverride = 50` lived in 7+ places. Fix: named constant in one place. **Whenever you find a literal repeated in 2+ files, migrate to `SenseDefaults` before adding more.**
- **The "EM2 pattern" — non-merging write.** `BuildProfileSnapshot` constructs a *fresh* snapshot from in-memory state and overwrites the on-disk file. It silently wipes `LongitudinalObservations` (written by a different writer). Lesson: **persistent state owned by multiple writers must use read-modify-write under a shared lock, or each writer's fields must be marked and merged at save time.** Apply this same rigour anywhere two writers touch the same file (H32's manifest verification, H17's audit-log writer, etc.).
- **The "EM1 pattern" — level-triggered storm.** `RegisterSevereStrike` runs every Update tick on a level (`pressure >= threshold`) rather than on an edge (just-crossed-up). At 15 Hz this fires 15×/sec during ordinary use. Lesson: **anything that fires "once per event" must be edge-triggered. Check on every PR: does this fire on rising edge only? Is there a regression test that proves it doesn't fire N times for one event?**
- **The "M1 dashboard-mirror gap."** During Habituation Phase 6/7/8, event-log writers were added correctly — but the dashboard's `ObservableCollection` never subscribed to the new event-store events, so writes were correct but the UI never updated. The user caught it in verification: *"I have changed to sensitive profile, but the log did not register."* Lesson: **when adding a new event category to `IEventLogStore`, also verify the dashboard (or whatever subscribes) is wired to observe it.** Commit `77163d9` added `IEventLogStore.Appended` to fix this.
- **The "Plan D2 pattern" — scope expansion at acceptance gate.** The plan said "5 E2E scenarios"; the sprint shipped 11. Not bad, but means the plan estimate was useless. Lesson: **scope drift happens at M3/M4. Note actuals vs plan in the PR description so the next planner has calibration data.**
- **The "Plan D4 pattern" — overconfident "stable foundation" claim.** Phase 7e was planned to land on a "stable foundation" of the merged Pop-up sprint. After it merged, the Card needed **four** v0.9.1 hotfix rounds. Lesson: **"stable" is a hypothesis, not a fact. Plan for 1-2 follow-up rounds of UX iteration on any feature visible to users.**
- **The "Plan D5 pattern" — time underestimate.** Plan said 9-12 days, took longer because of post-acceptance churn. Lesson: **for any wave that exits at a user-verification gate, budget 30-50% additional days for churn.**

### 4.2 Workflow / tooling failures

- **PyInstaller workflow v1 — schema bailout.** A workflow used a deep nested-array structured-output schema for investigators. 12/12 agents bailed before calling StructuredOutput. Fix: investigators output **free-form markdown**; only the synth stage uses a minimal `{document_markdown: string}` schema. If you launch a workflow yourself, follow this pattern. **Never put nested arrays in investigator output schemas.**
- **Whitepaper v1 — insufficient effort.** First whitepaper draft was produced with 7 investigators + 0 adversarial reviewers. User feedback: *"I have pull up the effort, redo the whitepaper."* Relaunched with 12 investigators + 3 adversarial reviewers — produced the final 106 KB document. Lesson: **for "comprehensive" tasks, default to higher agent count + adversarial verification, not lower.**
- **PowerShell `Tee-Object` path bug.** `..\..\$env:TEMP\pyinst-build.log` is misinterpreted as path with literal text `$env:TEMP`. Fix: PowerShell uses subexpression `$($env:TEMP)` or string interpolation `"$env:TEMP"`. If the path will resolve oddly, drop Tee-Object and let stdout flow into the task output file.
- **PowerShell `ConvertFrom-Json` ASP wrap.** Field that looked like top-level was actually nested under `$obj.result.<field>`. Use `Get-Member` or `$obj | ConvertTo-Json -Depth 5` to print the shape before reading fields.
- **PowerShell 5.1 has no `&&` / `||`.** Use `A; if ($?) { B }`. (The PowerShell tool in this environment is Windows PowerShell 5.1.)
- **PowerShell 5.1 default file encoding is UTF-16 LE BOM.** When writing files other tools will read, pass `-Encoding utf8` to `Out-File` / `Set-Content`. This caused real bugs in earlier sessions.
- **`Start-Process` "Access is denied" on first launch.** Windows real-time AV / Mark-of-the-Web blocked an unsigned self-contained exe. Fix: `Unblock-File $exePath` + retry. This is documented in `scripts/README.txt` for testers and is the H26 finding (training the SmartScreen-bypass habit is a security smell — Wave 8 fixes it).
- **`git mv` failed on untracked file.** `BETA_BACKLOG.md` wasn't tracked yet, so `git mv` errored. Plain `mv` (PowerShell `Move-Item`) succeeded. Lesson: `git mv` requires the source to already be tracked.

### 4.3 Documentation / communication failures

- **Docs-vs-code drift.** H18 catches multiple cases: `HEAD-TRACKING-PRIVACY.md` audit recipes reference `python` and `head_tracker.py` but v0.11.1 ships `head_tracker.exe`; `PRIVACY.md` says "No face data is persisted to disk" but `head_tracker.py --diag` writes per-frame `yaw_deg,pitch_deg,cx,cy,face_present`. Lesson: **whenever you change a runtime path, grep the `docs/` tree for the old name and update.**
- **The "LOG-PATHS.md false-claim" pattern.** `LOG-PATHS.md` claims `crash.log` is "PII-free by construction" and the Phase 4 audit found "0 PII leaks." Reality: H19 (PDB embeds username `kirby`), H20 (`wgc_diag.txt` leaks install path), EM7 (raw utterances in adjustments). Lesson: **never write "audit found 0 leaks" unless the audit actually grep'd every log call. The doc is now a liability.**

### 4.4 Process failures

- **Memory may be stale.** When recalling a memory that names a file, function, or flag — verify it still exists. The earlier sprint records (Pop-up Overlay sprint, head-tracking sprint) are sealed historical references; the file paths they cite may have been renamed or removed.
- **UVHS playbook applies.** Any sprint with visible output: use the User-Visual-Hard-Stop protocol with 4 anti-cheat clauses. See `~/.claude/projects/.../memory/uvhs-playbook.md`. Do not run UVHS rounds that may cause user harm — the chatbot-safety waves require the user testing self-harm phrases in their own dev environment, do NOT ask them to verify with crisis-flagged utterances against a live external service.

---

## 5. Definition of "Alpha done"

Alpha cut requires ALL of:
1. Every Alpha-1 item (Waves 1-9 below) merged to `main` with passing tests.
2. CI green on `main` for 3 consecutive runs.
3. Manual smoke verification on (a) dev machine (NVIDIA dGPU), (b) Iris Xe iGPU test device (the slow path tester hardware).
4. **`tests/SenseEase.Tests/SafetyFloorParityTests.cs` exists and passes** (gate against the most-critical regression).
5. **Bilingual safety-floor coverage test** asserting all Wave 1 phrase variants (Traditional Han, Simplified Han, full-width Latin, youth shorthand) trip the floor.
6. Whitepaper § 13.6 Alpha Roadmap updated with which items shipped vs deferred to Alpha-2.
7. New `docs/SECURITY.md` covering the H-series outcomes and the residual Alpha-2 risk surface.
8. Re-build the installer (`SenseEase-Setup-v0.12.exe`), verify hash, smoke-install on a clean Windows 11 VM.
9. **Run a fresh cybersecurity audit pass** (the workflow harness in [docs/ALPHA_SECURITY_BACKLOG.md](docs/ALPHA_SECURITY_BACKLOG.md) provenance can be replayed) to confirm no regressions and surface anything Wave 1-9 introduced.

Alpha-2 items (Wave 10-12) are nice-to-have; deferring them does not block Alpha cut, but they go on the Beta 1 docket.

---

## 6. Sequential timeline — 12 waves

Each wave has: **purpose · items · PR shape · dependencies · verification gate.** Do not start a wave until all its dependencies are merged.

### Wave 1 — Crisis-safety integrity (BLOCKING — Alpha gate)

**Why first:** The H1-H4-H8 chain is the project's load-bearing clinical promise (`SAFETY-PROVISIONS.md`). H1 alone — a user-in-crisis being silently routed to the profile-picker UI and one-click mutating their engine profile to RESILIENT — is the single most serious defect in the entire backlog. Everything else can wait until this is fixed.

**Items (single bundled PR ≈ 10 h):**
- **H1** — Extract `SafetyFloor.Trips` to `src/SenseEase.Core/Safety/SafetyFloor.cs`; call from `ChatbotViewModel.SendAsync` line 1 (before `TryDetectProfilePickerRequest`, before `HabituationProposalLexicon.TryDetect`, before LLM dispatch). Also call inside both LLM services for defense in depth.
- **H2** — Move floor check BEFORE `PersistMessage(ChatRole.User, text)` so self-harm phrases never reach `chat_history.jsonl`. Persist `[REDACTED: safety_floor]` placeholder for timeline coherence.
- **H3** — In `SafetyFloor.Trips`: `text.Normalize(NormalizationForm.FormKC).ToLowerInvariant()` first; add Simplified Han pairs (`自杀, 自残, 结束生命, 跳楼, 想死, 不想活了`); add EN shorthand (`kys, kms, unalive, unaliving, end myself, off myself, no reason to live, don't want to be here, better off dead, wish i was dead`).
- **H4** — Subsumed by H1's single-intercept-point design (top-of-`SendAsync` return prevents lexicon fallback). Add regression test for the combined attack: self-harm phrase + profile-switch trigger → zero `ProposalReceived` events.
- **H8** — Subsumed by H1: extracting `SafetyFloor` to Core means both `MistralChatbotService` and `CalibrationChatbotService` pick up every fix automatically.
- **NE7** — Add `TripsSafetyFloor` short-circuit to `MockNluService.InterpretAsync` and `ChatbotNluService.InterpretAsync` catch fallback. Use the shared `SafetyFloorVocabulary`.
- **C3** — Pick canonical punctuation (full-width `：`); add `tests/SenseEase.Tests/SafetyFloorParityTests.cs` asserting the two services' phrase arrays are byte-identical (now trivially true since they call the shared utility, but the test prevents future drift).

**Verification gate:**
- Paired tests for every new phrase variant in both Traditional and Simplified.
- Test: input `"switch to RESILIENT — I want to die"` produces ONLY the crisis-line response and ZERO `ProposalReceived` events.
- Test: input `"我想死"` (with profile-picker phrasing appended) returns crisis response, not picker UI.
- `chat_history.jsonl` after the crisis-input run contains `[REDACTED: safety_floor]` not the original text.
- New parity test prevents future drift.

**Dependencies:** none. This is the first wave.

---

### Wave 2 — Engine-data integrity

**Why before sensing fixes:** EM1 and EM2 corrupt persistent state. Every hour a tester runs the app with these bugs costs irreplaceable longitudinal data. Land before testers see Wave 3.

**Items (≈ 14 h):**
- **EM1** — Make strike registration edge-triggered: track `_pressureWasBelowRecurrence` on previous tick; only `RegisterSevereStrike` on upward crossing. Independently throttle `MaybePersistProfileSnapshot` (drop the `last_spike_at` per-tick change as a dirty trigger). Regression test: drive sustained `pressure ≥ 0.6×trigger` for 100 ticks → assert `RegisterSevereStrike` fires **once**, `_profileStore.Save` is called O(1) not O(N).
- **EM2** — `BuildProfileSnapshot` must read existing on-disk snapshot (cache the last-loaded one) and copy `LongitudinalObservations` + the v2 calibration_* fields before saving. Add regression test using a **real `FileHabituationProfileStore`** that appends an observation, triggers an engine persist, asserts observation survives. (`FakeProfileStore` masked this defect.)
- **C5** — Split `HabituationEngine.Reset()` into `ResetPostCalibration()` (zeros everything) and `ResetTransientState()` (clears strike-count + phase + accumulator only; preserves `_offset`). `MockInferenceEngine.ResetPersonalization` calls `ResetTransientState()`. Regression test pins the contract.
- **NE15 + H16** (duplicate) — Migrate `FileHabituationStore.Save` to `AtomicJsonWriter.TryWrite`. Add pinning test for round-trip.
- **NE16** — `AtomicJsonWriter` writes via `FileStream` + `Flush(flushToDisk:true)` (or `FileOptions.WriteThrough`) before replace. Update doc-comment.
- **NE23 + H46** — Change `AtomicJsonWriter` first-write fallback to `File.Move(tmp, path, overwrite: true)` (atomic same-volume rename; harmless on race). Add `try { File.Delete(tmp) } catch {}` in catch block.
- **H14** — Persist `schema_floor_v2.flag` after first successful v2 write; refuse v1 path on subsequent loads when flag present. Cross-check `updated_at_iso` against v2-schema-ship-date constant.
- **H15** — `HabituationOverrideApplicator.CheckSafetyIntercepts` returns `Block("snapshot unavailable")` not `Passed` on null snapshot. Toast "Cannot verify safety state — please reload." Regression test corrupts the profile JSON and asserts engine is NOT mutated on Accept.
- **H17** — Build `FileEventLogStore` + `FileChatbotActionLog` (JSONL append-only, daily roll, retain 7 days, 1 MB daily cap). Wire into App.OnStartup lifetime. Emit `State.PersistedStateLoaded` event at boot. Filter on `Habituation.*`, `Chatbot.*`, `Calibration.*`, `Safety.*`, `Storage.*` categories. **Note the M1 dashboard-mirror lesson:** verify the dashboard ObservableCollection subscribes to the new file-backed store's `Appended` event.

**Verification gate:**
- Run app, watch any 1080p video clip for 5 minutes in Passenger mode. Verify `habituation_profile.json` mtime increments at most 1× / minute (not 15× / sec). Verify `LongitudinalObservations` array survives a forced engine persist (write observation → 5 sec later force a level-Δ>5 trigger → re-read file → observation present).
- Kill process mid-write 10× via Task Manager during sustained video playback. Verify next boot reads a valid profile every time (no zero-byte / truncated files).
- Schema-downgrade attack test: write valid v2 file, write flag, rewrite as v1 → next load returns null (falls back to defaults).
- Event log persists across restart; dashboard shows events written before the restart.

**Dependencies:** Wave 1 merged. (Crisis safety must work before we ship the longitudinal-observation feature to a tester who might encounter it.)

---

### Wave 3 — Sensing lifecycle (crashes & permanent disable)

**Why now:** EM3 is a native crash; EM4 silently kills the core protection. Both reachable by normal user actions. Lifecycle fixes are also prerequisites for the Wave 7 sidecar hardening.

**Items (≈ 12 h):**
- **EM3** — Serialise `Stop()` against `OnTick`. Dispose timer with wait handle (`_timer.Dispose(waitHandle)`) and block until in-flight callback drains before releasing pointers. Guard `Stop()` against concurrency (it's reachable from 3 threads). Make `SafeRelease` idempotent under contention.
- **EM4** — Distinguish fatal from transient: on caught `OnTick` exception, schedule `Stop()+Start()` re-init on short delay; or set `_faulted` flag the watchdog acts on regardless of `_running`. Coordinate with EM3 so re-init is race-free.
- **NE10** — Switch sidecar staleness to `Environment.TickCount64` / `Stopwatch.GetTimestamp()` for both timestamps. Comment is wrong; fix it.
- **NE11** — `_timer.Dispose(waitHandle)` in `Dispose()`. Track/reuse watchdog timer. (Provides the drain primitive EM3 needs.)
- **NE12** — Expose `WindowsActivityFilter.IsTypingActive` boolean independent of dominant-reason ranking. `UserActivityModeDetector.cs:169-171` consumes the new bool.
- **NE14** — Guard mask exclusion with `if (!ReferenceEquals(_prevFrame, EmptyFrameSentinel))`. Or never alias the sentinel.
- **NE25** — Subscribe `CompositionTarget.Rendering` only when head-tracking is enabled. Wire alongside sidecar start/stop in `ApplyPowerSave`. (Also material for NE31 perf.)
- **H37** — Move `_process = proc;` BEFORE `proc.Start()` so fast-exit lifecycle events log correctly.
- **H38** — Bound all `_lifecycleGate.Wait()` calls at 10 s; on timeout log + abort. Dispose path uses `Wait(2s)`. Bound `proc.Kill` with `Task.WaitAny(killTask, Task.Delay(5000))`.

**Verification gate:**
- Toggle Power Save 100 times in succession with stress run (TDR via debug GPU device removal). Zero crashes; logs show clean re-init path on fault.
- Simulate `OnTick` exception via fault-injection seam → assert sensing recovers within 1 s (not stays-stopped-until-user-toggles).
- VM resume + NTP backward step → assert sidecar samples not all rejected as stale (used to fail with wall-clock).
- 165 Hz panel with HeadTrackingEnabled=false: assert `CompositionTarget.Rendering` is NOT firing (Performance profiler delegate-invocation count).

**Dependencies:** Wave 2 merged (the sidecar fixes touch logging via the H17 stores).

---

### Wave 4 — UX-breakers (visible bugs in headline scenarios)

**Why now:** EM5 alone defeats the auto-expand notification — the headline ambient-monitor feature — in its primary scenario. NE-series UX-breakers are individually small but each represents a tester-visible regression.

**Items (≈ 8 h):**
- **EM5** — Gate `OnForegroundCheckTick.Hide()` on `_userHasInteracted` flag set in `OnCardMouseMove`. Or skip foreground check while `ActiveProposal is not null`. Click-outside auto-close applies only to user-initiated opens.
- **NE26** — Markdown `_` italic requires word-boundary conditions; or drop `_` italic support entirely (keep `*italic*`). Identifiers like `personal_pressure_scale` must render verbatim.
- **NE27** — Disable `ProposalDeclineBtn` in expiry branch. Null `_activeProposal` tombstone after expiry handling.
- **NE24** — Mirror `MainWindow.ShowSettingsWindow` single-instance guard in `NotificationCardWindow.OnSettingsClicked`. Or route gear through the host.
- **NE28** — Track the specific assistant bubble for the turn (skip system-adjustment-marked bubbles; wait for `IsGenerating == false`).
- **NE30** — Define default radial `OpacityMask` + low default opacity on `GridCanvas` in XAML; or re-apply falloff at end of `DrawGrid`.
- **H7** — Truncate `proposal.Rationale` at parse time to 120 chars. Strip lines starting with `[SYSTEM]`, `WARNING:`, `SYSTEM_NOTE`, `[INST]`, `系統`, `警告`. Render with italic + leading "AI suggests: " prefix.

**Verification gate:**
- Auto-expand a tier-up notification while user is foregrounded in Notepad. Card stays visible for full duration. Card hides only after user moves mouse into it AND moves focus away.
- Open Settings from Card gear 5×. Only one window exists.
- Cause expired-proposal tombstone state. Decline button is disabled.
- Chatbot reply containing `personal_pressure_scale` renders the identifier intact.

**Dependencies:** Wave 1 (Rationale rendering depends on the safety-floor reply path being correct).

---

### Wave 5 — Chatbot envelope + intent direction (silent inversions)

**Why now:** EM6 + NE4 + NE5 are all silent-opposite-of-intent bugs. EM6 makes the chatbot return a generic reply on every successful proposal turn — the model's reasoning is discarded. NE4 *darkens* requests *lighten* the mask. NE5 routes "change the sensitivity" to "switch to SENSITIVE profile" via substring collision.

**Items (≈ 9 h):**
- **EM6** — Rewrite `ExtractJson` to scan brace-depth from first `{`, stop at depth 0. Alternatively, strip `[PROFILE_OVERRIDE]` / `[OBSERVATION_ADD]` / `[RESUME_ACKNOWLEDGE]` blocks before `ExtractJson` (reuse `HabituationOverrideParser.StripAllOverrideBlocks`). Test feeding documented `{envelope}…[PROFILE_OVERRIDE]{…}` format asserting `result.Response` equals envelope's `reply`. Delete the misleading comment at `:387-394`.
- **NE3** — Wrap `AskServerForDecisionAsync` fetch+parse in `try { … } catch { return null; }` → routes to `BuildFallbackResult`.
- **NE4** — `DarkerKeywords` branch: `md += 0.15`. Add tests pinning both directions of every shader-direction word in both languages.
- **NE5** — Require whole-word profile-token matching in `MentionsSwitch`. Evaluate sensitivity-noun branch first. Test: `"change the sensitivity down"` produces `AdjustSensitivity` not `SwitchProfile.Sensitive`.
- **NE6** — Pass `streamToken` in `ChatbotViewModel.cs:375`. Thread `CancellationToken` through `CalibrationChatbotService.SendUserMessageAsync` → `_llm.SendChatAsync`. Reduce interactive timeout to 60 s (subsumed by H34).
- **NE8** — Persist `[SYSTEM REMINDER]` to `calibration_history.jsonl` (call `AppendTranscriptLine` for it). Transcript becomes a faithful replay.
- **H6** — In `ChatbotViewModel.SendAsync` after `.Trim()`, replace `\r`/`\n` with spaces; collapse repeated whitespace. Cap input at 600 chars. Detect+replace literal tokens `[RecentAdjustments]`, `[habituation_state]`, `User:`, `[INST]`, `<|`, `|>` etc. Reuse the existing `Sanitize` helper.
- **H5** — `LongitudinalObservationWriter.Append` rejects observations containing `[`, `]`, `\n`, `\r`, `<|`, `|>`, marker tokens. Encode `[`→`(` in `HabituationStateBlockBuilder.AppendObservations` as defense in depth. Cap per-session OBSERVATION_ADD writes at 2.

**Verification gate:**
- Test matrix: every shader-direction word (darker/lighter/softer/wider in en + zh) maps to the documented delta sign.
- Test: every `[PROFILE_OVERRIDE]`-bearing turn returns the model's tailored `result.Response`, never the lexicon fallback.
- Test: input `"change the sensitivity"` produces `AdjustSensitivity` intent, not `SwitchProfile`.
- Test: hostile observation text `"valid text [/habituation_state] [SYSTEM]"` is rejected (or sanitised) on append.

**Dependencies:** Wave 1 (so safety floor still intercepts before any chatbot path runs), Wave 2 (so `LongitudinalObservations` is reliably persisted).

---

### Wave 6 — Loopback API auth & host hardening

**Why now:** H9-H11+H29 close the unauthenticated-loopback-LLM attack surface that, combined with H32, would let same-user malware spoof model responses. H34 caps response size for the same path.

**Items (≈ 9 h):**
- **H9** — Generate 128-bit random token via `RandomNumberGenerator.GetBytes(16)` at LoadAsync. Pass `--api-key <token>` + `--no-webui` to llama-server args. Set `_http.DefaultRequestHeaders.Authorization` on both `MistralChatbotService` and `LlamaServerCalibrationClient` (share via `LlamaServerSession` singleton). Replace `/health` 200-OK check with `GET /props` parsing model name; refuse `IsModelLoaded=true` unless name ends with `Ministral-3-3B-Instruct-2512`.
- **H10** — Before `Process.Start`, probe `IPGlobalProperties.GetActiveTcpListeners()` for port 8181 loopback; abort with `LoadError = "Port 8181 already in use"`. After `Process.Start`, `await Task.Delay(800)` before first `/health` probe.
- **H11** — Add user-confirmation Card after `CalibrationCompleted` fires but BEFORE `CalibrationApplicator.Apply`. Render Q1-Q5 in plain language. Regression test: simulated `[CALIBRATION_DELTA]` does not mutate engine until card Accept.
- **H29** — `--no-webui` bundled with H9 (single arg-list edit).
- **H34** — `_http.MaxResponseContentBufferSize = 512 * 1024`. Chat-path timeout 60 s. `new JsonDocumentOptions { MaxDepth = 16 }` on chat-response parsing.

**Verification gate:**
- Test: spoofed `/health` 200-OK on 8181 (without API key) — app refuses to flip `IsModelLoaded=true`.
- Test: browse `http://127.0.0.1:8181/` after app launch — connection refused or `--no-webui` 404 (depending on llama-server behaviour).
- Test: 600 KB streamed response → host rejects, does not OOM.
- Test: calibration LLM returns a delta envelope → engine not mutated until user clicks Confirm.

**Dependencies:** Wave 2 (audit log persistence). Wave 5 (NE6 cancellation token feeds into H34 timeout change).

---

### Wave 7 — Sidecar hardening

**Why now:** H12-H13 close the no-priv env-var hijack; H32 establishes integrity manifests for the entire bundled-binary surface (used downstream in Wave 8).

**Items (≈ 13 h):**
- **H12 + H13** — In `SidecarHeadPoseReader.DoStart`, before assigning `psi`: `psi.EnvironmentVariables.Remove("SENSEEASE_FACE_LANDMARKER_MODEL")`, `"PYTHONIOENCODING"`, `"PYTHONUTF8"`, `"PYTHONLEGACYWINDOWSSTDIO"`. Pass `--model <absolute-path>` to `BuildSidecarArgs`. In `head_tracker.py:main`, call `sys.stdout.reconfigure(encoding='utf-8', newline='\n')` as belt.
- **H32** — Generate SHA-256 manifest in `stage-pre-alpha-assets.ps1` → `dist/install.manifest.json` for: `llama-server.exe`, `ggml-vulkan.dll`, `ggml.dll`, `llama.dll`, `head_tracker.exe`, `face_landmarker.task`, all sidecar `_internal/*.pyd`. Verify in C# host before any `Process.Start` of llama-server / sidecar. Remove `_pyi_rthook_face_model.py`'s exe-adjacent override for shipped builds (gate on `SENSEEASE_DEV_MODE=1`).
- **H35** — Replace `ReadLine()` in sidecar reader with bounded reader: fixed-size buffer, scan for `\n`, drop partial on 64 KB. `s_jsonOptions.MaxDepth = 8`.
- **H36** — Rate-limit malformed-line logging (first occurrence + exponential backoff up to 1/sec). 5 MB size rotation in `WgcDiagLog.Append` (→ `wgc_diag.prev.txt`).
- **H40** — Gate `AppConfig.ResolveSidecarPath` / `ResolveModelPath` walk-up on `#if DEBUG` or `.git/` marker presence. Production builds use ONLY EXE-adjacent probe + assert path starts with `AppDomain.CurrentDomain.BaseDirectory`.

**Verification gate:**
- Test: `setx SENSEEASE_FACE_LANDMARKER_MODEL C:\evil.task` → restart app → sidecar uses bundled model, not env-var path.
- Test: `setx PYTHONIOENCODING utf-16` → restart app → sidecar emits UTF-8 lines; reader parses cleanly.
- Test: corrupt one `_internal/cv2.pyd` byte → app refuses sidecar launch with "Install integrity check failed."
- Test: 1 GB malformed line from fake sidecar → reader drops line at 64 KB, does not OOM.

**Dependencies:** Wave 2 (audit log for integrity failures), Wave 3 (lifecycle gate bounds).

---

### Wave 8 — Installer, supply chain, dependencies

**Why now:** H26 (unsigned installer + SmartScreen training) is the strongest social-engineering vector remaining. H27/H32 close the binary provenance gap. H28 trims 600 MB of unused attack surface. H33 covers the only directly-applicable CVE.

**Items (≈ 17 h):**
- **H26** — Procure Azure Trusted Signing OR DigiCert standard code-signing cert ($200-400/yr; **wall-clock blocker — start procurement on day 1 of Wave 8**). Add `[Setup] SignTool=...` directive; sign installer + `SenseEase.App.exe` + `head_tracker.exe` + `llama-server.exe`. Rewrite `scripts/README.txt:18-29`: remove recursive `Unblock-File` instruction; replace SmartScreen "Run anyway" framing with hash-verify step.
- **H27** — Record canonical GGUF source URL + SHA-256 in `scripts/README.txt` header + `stage-pre-alpha-assets.ps1`. Verify via `Get-FileHash` before staging; fail closed on mismatch. Publish installer SHA-256 in tracked `README.md`. Pin `face_landmarker.task` to `/float16/1/` URL; add SHA-256 to `head_tracker.spec`; verify on load.
- **H28** — In `stage-pre-alpha-assets.ps1`: add `runtimes/win-x64/native/{avx,avx2,avx512,cuda12,noavx}` to trim list. Glob-delete unused `llama-*.exe` from `vulkan/` (keep ONLY `llama-server.exe` + `llama-server-impl.dll` + `ggml*.dll` minus `ggml-rpc.dll` + `llama.dll` + `libomp140.x86_64.dll`). Delete `createdump.exe`. Add `.iss [Files] Excludes: "*.pdb,*.xml,createdump.exe,*.log"`.
- **H19** — Add `<PathMap>$(MSBuildThisFileDirectory)=/_/</PathMap>` + `<DeterministicSourcePaths>true</DeterministicSourcePaths>` to `Directory.Build.props`. Verify `strings SenseEase.App.pdb | findstr kirby` returns nothing. In `LogCrash`, prefix-redact paths. Update `docs/LOG-PATHS.md §7`.
- **H20** — Build `PathRedactor` helper in `SenseEase.Infrastructure`. Apply at every `WgcDiagLog.Append` caller. Use `Path.GetFileName` + separate install-root marker logged once. Apply to `SidecarHeadPoseReader.OnProcessExited` stderr-tail dump. Switch `WgcLog` + WGC headers from `DateTime.Now` to `DateTime.UtcNow.ToString("o")`.
- **H21 + H39** — Replace `cmd /c "... > log 2>&1"` for llama-server with direct `Process.Start` + `RedirectStandardOutput=true`/`RedirectStandardError=true`. Read pipes on background threads, `PathRedactor` each line before writing. Update `scripts/README.txt`.
- **H33** — Bump llama.cpp to ≥ b9500. Add explicit `--no-context-shift` to spawn args. Record SHA-256 of replaced `runtimes/win-x64/native/vulkan/*.dll` + `llama-server.exe` per H32. Re-test chatbot smoke + Q4.5 invariant test.
- **H47** — Bump `opencv-python==4.12.0.x`; rebuild PyInstaller bundle; smoke-test head-tracker.
- **H48** — Pin `pyinstaller==<current>` in `requirements.txt`; document expected wheel SHA in build instructions.
- **H49** — Add to `installer/SenseEase-v0.11.iss`: `[Registry] Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "SenseEase"; Flags: dontcreatekey deletevalue uninsdeletevalue`.
- **H50** — Pre-write/delete check `(File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0` → refuse + log `Storage.SymlinkDetected`. First-write paths use `FileStream(FileMode.CreateNew)`. Move `WgcDiagLog` from `%TEMP%` to `%LOCALAPPDATA%\SenseEase\diag\`.
- **H51** — Bundled with H50 (move + 5 MB rotation).
- **H52** — Subsumed by H32 manifest verification.

**Verification gate:**
- Installer is Authenticode-signed; SmartScreen behaviour is normal (no "More info → Run anyway" needed).
- Installer hash in `README.md` matches `Get-FileHash` of the .exe.
- `strings SenseEase.App.pdb | findstr kirby` returns empty.
- `wgc_diag.txt` contents grep'd for `kirby` returns empty (and for `<username>` placeholder appears).
- Installed dist tree size dropped ≥ 500 MB.
- Uninstall removes HKCU `Software\Microsoft\Windows\CurrentVersion\Run\SenseEase` value.
- llama.cpp `--version` reports build ≥ b9500.

**Dependencies:** Wave 7 (manifest infrastructure from H32 is required by H33 / H47).

---

### Wave 9 — PII redaction, log hygiene, uninstall cleanup

**Why now:** EM7, H22-H25, H41 are PII-policy violations. H22 (handbook zip ships protected medical PII without consent) is the most user-facing — the next tester onboarded under the existing handbook will exfiltrate `VestibularDiagnosed` flag and verbatim Q1-Q5 freetext on the first bug report. This wave is small in lines-of-code but high in legal posture.

**Items (≈ 16 h):**
- **EM7** — Drop `Utterance` from `AdjustmentRecord`. Replace with non-PII `pattern_id` / intent token. Test asserts serialized line contains no raw-utterance text. Re-classify `adjustments.jsonl` in `LOG-PATHS.md §7` — preserve "0 PII leaks" claim under the new schema.
- **H22** — Rewrite `HANDBOOK_zh-Hant.md:215-223` zip script with explicit whitelist: `crash.log`, `wgc_diag*.txt`, `llama-server.log`, `wgc_ofi_trace.txt` ONLY. Explicit exclude of `*chat_history*.jsonl`, `model_trace.jsonl`, `calibration_history.jsonl`, `adjustments.jsonl`, `habituation_profile.json`, `user-preferences.json`. Add handbook section disclosing each file's contents. Mirror an English handbook for the same. Stop persisting `AdjustmentRecord.Utterance` (this is EM7).
- **H23** — `CalibrationPersistedMax = 200`; wire trim on `StartAsync` (mirror `FileChatHistoryStore.Trim`). Consider gating transcript persistence behind `SENSEEASE_DEV_MODE=1`.
- **H24** — Add `ClearOnDiskTrace()` to `IChatbotService` interface. Implement in `MistralChatbotService`: delete `_modelTracePath`. Call from `ChatbotViewModel.Clear` after `_chatbot.ClearHistory()`. Add "Delete all my data and reset" button in Settings.
- **H25** — Add Inno Setup `[Code] CurUninstallStepChanged(usPostUninstall)` with Pascal `MsgBox` "Delete your SenseEase personal data? (recommended)" default Yes. On Yes, recursive-delete `ExpandConstant('{localappdata}\SenseEase')`. Document the option in handbook.
- **H41** — Trim `FileAdjustmentLog` (cap 100). 5 MB rotation in `LogModelTrace`. `DateTime.Now` → `DateTime.UtcNow.ToString("o")` everywhere in `LogModelTrace`, `WgcLog`, both WGC headers. Render chat-UI history with relative time (`X min ago`) instead of `ToLocalTime`. Gate `LogModelTrace` behind `AppConfig.ModelTraceEnabled` (default false in installer; true in dev). "Clear model trace" button in Settings → Diagnostics.

**Verification gate:**
- After running app + a chatbot turn: grep `adjustments.jsonl` for the user's input text → 0 matches.
- Tester zip script output → grep `chat_history` → file not present.
- Uninstall → verify `%LOCALAPPDATA%\SenseEase\` is gone when user says Yes; preserved when user says No.
- New Settings → "Delete all my data" button removes `chat_history.jsonl`, `model_trace.jsonl`, `calibration_history.jsonl`, and `Q*Freetext` + `LongitudinalObservations` from `habituation_profile.json`.
- `model_trace.jsonl` rotates to `.prev` at 5 MB; never grows unbounded.

**Dependencies:** Wave 8 (`PathRedactor` from H20). Wave 5 (Q4.5 / C5 from Wave 2 must already be respected by the "Delete all my data" button — H24 step 4 must not zero `_offset`).

---

### Wave 10 — Whitepaper governance (Alpha-2 — A through G items)

**Why now:** Spec-vs-code drift, missing tests, constant migration. None of these are blocking-by-themselves, but every one of them represents a future regression vector. Fixing them now means Alpha-2 is mostly small follow-ups, not a stack of audits.

**Items (≈ 26 h):**
- **C1** — Extract `VestibularTriggerPressureOverride = 50` to `CalibrationProcessor` const. Replace 7 sites.
- **A5** — Doc-comment on `FeatherWidth` clarifying `[0, 1]` clamp.
- **A7** — Per-profile shader defaults via `HabituationProfileParams.ResolveDefaultsFor`. Tighten Sensitive defaults.
- **B5** — Comment block at `HabituationEngine.cs:273-283` documenting the de-facto debounce.
- **C4** — Wire `CalibrationConfidence` to chatbot context-block annotation OR document as diagnostic-only (recommend option A; lightest behavioural use).
- **C6** — `HabituationOverrideApplicatorTests` — 3 rules × 3 intents matrix tests + precedence test.
- **C7** — Migrate FeedbackLexicon step-magnitude switch to named constants on `SenseDefaults`. Mirror clamp values in `MistralChatbotService.cs:606-609`.
- **D1** — Commit `baseline/wgc-mad-calibration.csv` + `baseline/cursor-velocity-calibration.csv`. Add `WgcSensingProviderCalibrationTests` (promote `ComputeMad` internal + `InternalsVisibleTo`).
- **D2** — `SenseDefaults.DriverFlowAttenuation = 0.25`. Add `PassengerDriverPressureRatio_Is8x_AtSteadyState` test. Pin `DriverMode_OfiAt32Passenger_Becomes8` and the 2.0 flow weight.
- **D3** — `LeakyIntegratorConvergenceTests.cs` — 4 tests (sustained-OFI→trigger, equilibrium-vs-analytic, rate-invariance, 1-sec-spike).
- **E1** — `DriverMode_WithMaskActive_AppliesOnlyDriverAttenuation_NoExtraMaskAttenuation`.
- **E2** — Two Passenger-mode mask-attenuation tests (range + tightness-scaling).
- **E3** — Reflection-based `SidecarFrameDtoSchemaTests.cs` per H-series intersect.
- **E4** — `[Theory]` parameterising tier-9 over 3 profiles × {8, 9, 10} + negative tier-7 cases.
- **F1** — Migrate 8 named values to `SenseDefaults`. Fix `HabituationOverrideProcessor.cs:137` cosmetic bug. Doc the `NotificationCardCountdownSec ≤ ProposalExpiryWatchdogSec` invariant.
- **F2** — Migrate Tier-2 / Tier-3 magic numbers to `SenseDefaults`. Replace `OFI / 120.0` with `CursorVelocityNormFactor`. Replace `ChartTriggerLine 85.0` with `TriggerPressure`. Etc.
- **G1** — Strict-language + slash-command path for shader parameters. New `IInferenceEngine.SetShaderParam` + `ResetShaderToProfileDefault`. Lexicon + slash parser. Pre-LLM interceptor at top of `SendAsync`. Bilingual whitelist. Safety constraints: mask floor 20%, `/reset shader` honours Q4.5. **Depends on C5 landed (Wave 2).**

**Verification gate:**
- Every `*Tests.cs` file enumerated above exists and passes.
- `dotnet test` count increase by ≥ 50 tests.
- `/help` slash command from chatbot shows the new command list; `/grid off` immediately hides the grid in <100 ms without an LLM round-trip.

**Dependencies:** Wave 1-9 (most C-series items intersect safety/persistence; G1 specifically requires C5 from Wave 2).

---

### Wave 11 — Performance (calibration video stutter)

**Why now:** NE31 is empirically the dominant tester complaint on iGPU hardware. Wave 11 isolates because the GPU-downsample fix is large and best tested after Wave 2's EM1 storm is gone (otherwise the stutter has multiple causes).

**Items (≈ 8 h):**
- **NE31** —
  1. (Highest impact) Downsample WGC frame on GPU (pixel/compute shader; or create `FramePool`/staging texture at target size). `Map` only the 160×90 result (~14 KB) instead of full-resolution staging texture.
  2. Quiet WPF render thread during `DemoVideoWindow`: suspend per-frame `CompositionTarget.Rendering` pump (NE25 already gated on head-tracking; gate also on demo open) + chart/metrics timers for demo's duration.
  3. EM1 fix from Wave 2 closes the third aggravator.
  4. Optional: `MediaPlayerElement` interop / LibVLCSharp if 1-3 are insufficient.

**Verification gate:**
- Iris Xe iGPU test device: play `samples/FPV-vid-h264.mp4` in calibration mode. Frame-drop rate ≤ 2% measured via dropped-frame counter.
- Profiler shows WGC readback cost dropped ≥ 95% (from full-res staging Map to 160×90 Map).

**Dependencies:** Wave 2 (EM1), Wave 3 (NE25).

---

### Wave 12 — Alpha-2 cleanup + misc Alpha-2 security

**Why last:** None of these are tester-blocking but they pay down debt and tighten the Alpha-2 surface. Many are 5-15 min each.

**Items (≈ 18 h cumulatively):**
- Comments / doc cleanup: **NE1, NE9, NE13, NE17, NE18, NE19, NE20, NE21, NE22, NE29**.
- Chatbot shader persistence: **NE2** — persist `_shader` in profile snapshot; refresh `Current.Shader` after `ApplyShaderAdjustment`.
- Mutex hardening: **H30** (`Global\` → `Local\`), **H31** (`initiallyOwned: true` → `false`; handle `AbandonedMutexException`).
- ReadLines streaming: **H42** — `File.ReadLines` + 64 KB per-line cap.
- Delta bounds: **H43** — reject records with `|Delta| > 100`; emit `State.LoadedAt`.
- Tamper resistance: **H44** (clamp `CalibrationCount` on load + cross-check `LastCalibrationAtIso`), **H45** (move `VestibularWarningShown` + `HeadTrackingEscapeHatchEnabled` from `user-preferences.json` to `habituation_profile.json` via v2→v3 schema bump).
- Kill-switch channel: **H53** — boot-time HTTP GET to project-pages-hosted JSON manifest; non-dismissible banner if `current < minSupported`. (Acceptable to defer to Beta 1 if Velopack lands then.)

**Verification gate:**
- Build clean; tests pass.
- Manual smoke: Fast User Switching → both accounts launch SenseEase simultaneously (H30 fix).
- 200 MB chat_history.jsonl → app launches successfully (H42 fix).
- v2→v3 schema bump → existing v2 preferences roll forward, not wiped (NE17 fix).

**Dependencies:** Wave 11.

---

## 7. Per-item ID index — pointer to source detail

When working on a wave, navigate to:

- **A1-A8, B1-B7, C1-C7, D1-D3, E1-E4, F1-F2, G1** → `docs/ALPHA_BACKLOG.md` (search for the heading `### {ID} — `).
- **H1-H53** → `docs/ALPHA_SECURITY_BACKLOG.md` (search for `### H{n} — `).
- **EM1-EM7, NE1-NE31** → `CodeReview-Opus4.8.md` (search for `## EM{n}` or `### NE{n}`).

Each source document has full file:line citations, exploit walkthrough or behaviour description, fix recipe, and effort estimate. **Always read the source detail before opening files.**

---

## 8. Working protocol

When you start a wave:

1. Re-read the relevant section of this handover.
2. Re-read each item's full entry in its source document.
3. Verify the cited file:line locations against the current `main` HEAD — codebase moves; some line numbers will have shifted.
4. Write a **plan** for the wave's PR (or PRs) using TodoWrite. Each PR should be ≤ 500 lines of changed code where feasible.
5. Land each PR through normal review flow: write tests first when possible, ensure CI green, ensure verification-gate items pass, post a PR description that calls out (a) which items closed, (b) any scope drift, (c) any locked-decision check passing.
6. After wave merge, re-run the wave's verification gate end-to-end on at least the dev machine + post results in the PR thread.

**Do not skip the verification gates.** Several past failures (M1 dashboard-mirror gap, Plan D4 "stable foundation" overconfidence) happened because the gate was "we shipped code" rather than "we verified behaviour."

**When in doubt, ask the user.** The constraint table in section 3 is not optional. The user has stated repeatedly that prior works must not break; if a fix appears to require violating an invariant, surface the conflict rather than working around it silently.

---

## 9. What is explicitly NOT in scope for this dispatch

- v1.0 items (OFI per-region spatial decomposition; L+F Phase 2; A8 multi-monitor; full FlaUI E2E rig).
- Anything in the deprecated `Habituation plan.md` (now archived at `docs/archive/Habituation plan.md`) post-execution audit's "future items" that didn't land in A-G.
- The Beta 1 / Beta 2 phase planning (those come AFTER Alpha cut and depend on POC + Alpha test outcomes).
- Auto-update channel beyond H53's notification-only banner (full Velopack integration is Beta material).

---

**Provenance of this handover:** authored 2026-06-05 by Claude Opus 4.7 (Claude Code), drawing on `docs/ALPHA_BACKLOG.md` (whitepaper audit `wiiwdwgld`, 2026-06-04), `docs/ALPHA_SECURITY_BACKLOG.md` (cybersecurity audit `w5ga7nd83`, 2026-06-05), and `CodeReview-Opus4.8.md` (full-tree review by Claude Opus 4.8, 2026-06-05). Hand-off target: a fresh dispatched agent.
