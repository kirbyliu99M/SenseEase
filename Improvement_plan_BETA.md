> **[LEGACY - immediate predecessor of Beta-1]** Superseded by [Beta-1.md](Beta-1.md) (2026-06-14). Retained for historical reference / back-trace; not the current plan.

# SenseEase — Improvement Plan (Beta)

**Date:** 2026-06-10
**Baseline:** branch `alpha1-w2-engine-integrity` @ `f819617` (installer v0.11.3, "Pre-Alpha" per the project's release-phase naming)
**Method:** read-only full sweep. 44 agents: 4 doc-readers (intent distillation) → 6 dimension sweepers (UI/UX, Feature, Logic, Security, Dev-intent effectiveness, Dead code) → adversarial verification of **every** finding against the working tree + git history → completeness critic → 4 gap-fill sweeps (test-suite quality, installer/lifecycle, sidecar internals, shutdown/exception paths), also verified.
**Trust model:** every entry below was independently re-confirmed by a verifier agent that re-opened the cited file at the cited line on this branch; verifier corrections are folded in. Exactly 1 of 128 raw findings was rejected (it contradicted a deliberate 2026-06-09 UI removal). Findings already fixed since the 2026-06-05 audits were checked and **excluded** — this document contains only confirmed-open items.
**No code was changed by this sweep.**
**Revision 2026-06-11:** all 15 product-owner decision points ratified by Kirby — 12 in the first round, plus the three remaining micro-decisions (D-13…D-15) in a second round the same day. Rulings are recorded in §1.5 and folded into every affected entry (CW-1/2/3/10, FEAT-06/07/11/14, LOGIC-05/13/14, INTENT-06/08/09, UX-20, DEAD-02/04, TEST-09/15, CW-12), the §11 roadmap, and §12. A post-decision coherence check was added as §14. **Zero open decision points remain.**

---

## 0. How to read this document

- **IDs** are stable and traceable to the sweep evidence: `UX-` UI/UX · `FEAT-` features · `LOGIC-` logic · `SEC-` security/privacy · `INTENT-` dev-intent effectiveness · `DEAD-` dead code/hygiene · `TEST-` test-suite integrity · `PKG-` installer/packaging lifecycle · `SIDE-` head-tracking sidecar · `EXIT-` shutdown/exception lifecycle.
- **Severity:** `P1` = fix before (or at the very start of) shipping to Alpha testers — a safety contract, headline feature, or user-trust property is broken. `P2` = Beta-1 work — real correctness/honesty/quality defects. `P3` = Beta-2 / opportunistic — polish, debt, hygiene.
- **Effort:** `S` ≤ ~half a day · `M` ~1–3 days · `L` multi-day / external-gated.
- **carried-over** marks items already known from `CodeReview-Opus4.8.md`, `docs/ALPHA_BACKLOG.md`, `docs/ALPHA_SECURITY_BACKLOG.md`, or `docs/CodeReview-W1W2.md` that remain unfixed at HEAD. Everything else is **new** to this sweep.
- **Phase naming reminder** (from `docs/ALPHA_BACKLOG.md`): current v0.11.x = *Pre-Alpha*; the first tester release = *Alpha*; *Beta 1/2* follow. This plan is the requested Beta improvement plan, but Section 2 flags the subset that should not wait past the Alpha cut.

**Totals: 127 confirmed-open findings — 18 P1, 52 P2, 57 P3** (14 unique P1 problems after de-duplication; see §10 duplicate map).

---

## 1. Executive summary

The codebase is in substantially better shape than the 2026-06-05 audits left it: the crisis safety-floor chain (H1–H4/H8), EM1–EM7 emergencies, the W1–W9 hardening waves, PII gates, and the UM storage layer are verifiably fixed at HEAD. The sweep confirms the Chang-Hain leaky-TDP implementation matches the whitepaper, the "never initiate" contract holds, OBS-3 scalar-only IPC holds, and there is no network egress beyond `127.0.0.1:8181`.

What remains clusters into seven themes:

1. **Two features are silently inert in production.** The UM4 retrospective user-model analyzer literally never runs (`MainWindow` is null when it is scheduled — FEAT-01), and the chatbot/slash mask-depth & feather actuators change nothing on screen because no renderer consumes them (FEAT-03) — yet the assistant confirms the changes as applied.
2. **A clinical-contract leak.** The live Adaptive-Fit selector's set moves silently clear the habituation safety pause, including the top-band `PausedAwaitingUserConfirm` that is locked to exit *only* via user confirmation (LOGIC-01) — and the habituation opt-in control itself no longer exists in the UI (FEAT-02).
3. **Severed or dishonest affordances after the 2026-06-08/09 UI removals.** The chatbot directs users to a profile switcher that no longer exists (FEAT-04/UX-01); the popup card clips the crisis hotline text to ~2 lines of 10 px (UX-02); Paused vs Faulted are confusable with a fake "tap to retry" (UX-03); a permanent "Restart needed" chip lies (UX-08).
4. **"My settings reset themselves" has real code causes** beyond the known Trend-Micro file-lock environment: the overlay baseline level, `/grid off`, and all chatbot shader personalization are session-only (UX-12, FEAT-05/LOGIC-10, FEAT-15), and `behavior_log.jsonl` is rewritten non-atomically every 60 s (LOGIC-06).
5. **The installer quietly violates its own opt-in rule** — every 0608/0609 install enables autostart for every user (PKG-01) — and the tester-facing README/hash-verification chain is stale enough to actively mislead (PKG-02/03).
6. **The test suite has integrity gaps where it matters most:** chatbot tests append crisis/injection payloads to the developer's real `chat_history.jsonl` (TEST-01), the H2 redaction "boundary" test is a tautology (TEST-02), prefs tests delete the dev's real preferences file and are the root of the known flake (TEST-03), and the five headline locked constants are not literal-pinned (TEST-10).
7. **Drift between docs/comments and shipped behavior** keeps misleading audits and agents: stale flag comments claim default-OFF for live features (DEAD-10), `AutoProfilingEnabled=false` promises a revert path that was deleted (FEAT-07/DEAD-02), and Phase-1 `ARCHITECTURE.md` is actively wrong (DEAD-13).

The highest-leverage structural investments for Beta: one **storage-path seam** for tests (fixes TEST-01/03/14 together), one **clock-unification commit** (fixes UX-15, FEAT-10, LOGIC-04, SEC-05), one **narrow habituation reset** (fixes LOGIC-01/INTENT-01 and protects Q4.5), and the already-planned **overlay motion-coherence work** (`docs/BETA_FEATHERING_COHERENCE_PLAN.md` A1/A2), which is also the natural moment to make MaskDepth/FeatherWidth real (CW-2).

---

## 1.5 Product-owner decision record (ratified 2026-06-11, Kirby)

Every decision point the 2026-06-10 sweep left open is now ruled. Rulings are authoritative; affected entries below carry them inline as "decided 2026-06-11 (D-n)".

| # | Decision point | Ruling |
|---|---|---|
| D-1 | CW-1 — habituation opt-in home | **Option A:** reinstate a pause/resume toggle in **Settings**; update `SAFETY-PROVISIONS.md` |
| D-2 | CW-2 — MaskDepth/FeatherWidth | **Option A — wire them**, landing inside the A2 single-pass field rework (`BETA_FEATHERING_COHERENCE_PLAN.md`); B0 interim: the chatbot must stop claiming the no-op changes applied |
| D-3 | CW-3 — profile management | **Option B — auto-managed.** One commit removes: canned reply, system-prompt EXAMPLE 2, `HabituationProposalLexicon` profile-switch detection, dead `ProfileOptions` template, `SelectProfileCommand`, orphaned `ProfilePill` style |
| D-4 | LOGIC-05 (NE9) — cursor-move OFI | **Not by design.** Replace the hard zero with a **0.25 weight multiplier** on raw OFI during the cursor-move window; withdraw the Wave-12 "doc-cleanup only" triage |
| D-5 | LOGIC-13 — fresh-user L5 seed | **Option A:** exempt the seed from fast-release during an initial stable window; record as deliberate onboarding protection |
| D-6 | INTENT-06 — `DisplayStressPercent` | **Option A:** surface in the Advanced/telemetry expander with the original caption "Relative to your personal threshold"; keep the computation; whitepaper §6.5 unchanged |
| D-7 | INTENT-08 — vestibular regret-off | **Option A:** clear the advisory on regret-off so both safety channels agree; document in `SAFETY-PROVISIONS §3.1`; add a parity test |
| D-8 | FEAT-07 — `AutoProfilingEnabled=false` | **Option A:** keep the flag for Alpha; flag-off semantics = **retain the TYPICAL/L5 seed**; pin with a test; update `ALPHA2_CORE_ARCHITECTURE.md` |
| D-9 | FEAT-11 — Cursor/Demo modes | **Option B — retire.** Update the whitepaper; delete the full DEAD-03 debris list (`UseMockSensing` branches, `RejectCursorDragOfi`, DEMO/CURSOR labels, both providers). `WindowsActivityFilter` stays (live for WGC) |
| D-10 | UX-20 — orphaned flags | **Formally retire + delete paths** for both `BarTrayColorEnabled` and `HeadTrackingEscapeHatchEnabled` — the escape-hatch feature is removed entirely (supersedes FEAT-06) |
| D-11 | CW-10 / SIDE-01…03 — C4 unlock | **Authorized.** Jump filter (SIDE-01), capture loop (SIDE-02), scheduler resync (SIDE-03) may be edited per the CW-10 fix text; the locked EWMA/dead-zone/face-presence trio stays untouched |
| D-12 | INTENT-09 — D6 override record | **Confirmed.** Append to `LOCKED_DIRECTION_ALPHA1.md`: "2026-06-06 Kirby override: `_persistentOverlayEnabled=false`; rationale: default-OFF for fresh installs; sign-off: Kirby"; fix the `D6DayOneProtectionTests` header |
| D-13 | DEAD-02 — calibration plumbing | **Option B — delete.** Remove `ApplyCalibrationResult` / `CalibrationProcessor.Process` and their test suites, executed together with the FEAT-07/D-8 cleanup commit; doc-comment states rollback = `git revert 3131387`. Engine-consumed constants stay |
| D-14 | DEAD-04 — `VestibularWarningShown` | **Option A — delete** the field and every doc reference, including the Toggle-B dialog text (`POPUP-OVERLAY.md §4`); backlog H45 closes as moot for this field |
| D-15 | FEAT-14 — `ResetToDefaults` | **Option A — wire** to a Settings action with a confirm dialog; per the original contract comment the reset also clears `VestibularWarningShown` (transitional — vacuous once D-14's deletion lands) |

**Decision cascades** (verified in §14): D-9 also closes LOGIC-11 and the cursor half of LOGIC-14 · D-10 supersedes FEAT-06 (the global-hotkey fix is **not** built) and obligates handbook + `HARD_CONSTRAINTS.md` A3 updates · D-2 moves TEST-04 into the A2 wiring commit · D-3 closes UX-01 via the honest-copy rewrite · D-8 redefines DEAD-02's doc-rewrite target · D-13 supersedes TEST-15's dead-surface labeling (the suites are deleted, not labeled) and makes TEST-09's live-path port **mandatory in the same commit** · D-14 makes D-15's clear-`VestibularWarningShown` clause vacuous once the deletion lands.

---

## 2. Critical workstreams (de-duplicated P1 clusters)

These twelve workstreams absorb all 18 P1 findings plus their directly-coupled P2s. Recommended order within the Alpha-cut window: CW-1 → CW-6 → CW-5 → CW-4 → CW-3 → CW-2 (interim honesty fix) → CW-7…CW-12 early in Beta-1. All decision points were ratified 2026-06-11 — see §1.5.

### CW-1 · Habituation pause integrity (clinical) — `P1 / S–M`
**Findings:** LOGIC-01 ≡ INTENT-01, plus FEAT-02.
**Problem:** `MockInferenceEngine.ApplySetMove` (`:941`) and `SetLevel` snap paths (`:792`) call `HabituationEngine.ResetTransientState()`, which clears `_externallyPaused`, `_pauseCooldown` **and** `_awaitingUserConfirm` (`HabituationEngine.cs:463-477`). With `AdaptiveFitSelectorEnabled=true` (production default since `c1461a9`), a **silent background set move** un-pauses opted-out habituation and exits the tier≥8 `PausedAwaitingUserConfirm` without `AcknowledgeAndResume` — both locked contracts (whitepaper §7.4, SAFETY-PROVISIONS §8). Compounding it, the habituation pause/resume toggle was removed from the dashboard on 2026-06-09 (`MainWindow.xaml:541-544`), so the engine starts paused with **no UI path to opt in**, and the pause then flips via these side effects — the graded-exposure feature is in a user-uncontrollable state.
**Fix:**
1. Add a narrower `ResetChatbotDelta()` on `HabituationEngine` that zeroes the FOV offset/shader delta only, preserving `_externallyPaused`, `_awaitingUserConfirm`, `_pauseCooldown`, `_severeStrikeCount`. Use it in `ApplySetMove` and the `SetChangeSnap`/`FeltIntensitySnap` branches of `SetLevel`. Keep full `ResetTransientState` for the explicit user `ResetPersonalization` path only.
2. Pin with tests: a silent selector move while externally paused / awaiting confirm leaves both flags intact.
3. **Decided 2026-06-11 (D-1):** reinstate the pause/resume control in **Settings** (a 漸進適應/graded-exposure group), bound to the existing `SetHabituationPaused` path, with a one-line plain-language caption of what resuming means. Update `SAFETY-PROVISIONS.md` to name the Settings toggle as the **sole** opt-in/opt-out path. Step 1's narrow reset guarantees selector moves can never flip this user choice.
**Constraint check:** preserves Q4.5 (`_offset` untouched); engine-touching, so build+test gate + advisor pass per house rules.

### CW-2 · Honest actuators: MaskDepth / FeatherWidth are render-inert — `P1 / M (decided: wire at A2; interim honesty fix in B0)`
**Findings:** FEAT-03 ≡ INTENT-02, plus TEST-04.
**Problem:** `OverlayWindow` consumes only `Shader.GridVisibility` (`OverlayWindow.xaml.cs:765`); mask alpha comes solely from `VimsLevels[tier]` / `MaskPeakAlphaOverride`, feather ramp is fixed (0.35→0.95). No renderer reads `Shader.MaskDepth` or `FeatherWidth` — yet the LLM prompt (`MistralChatbotService.cs:106-110`), `/mask`, `/feather`, and SENSITIVE profile defaults all drive these knobs, the chatbot **confirms** the changes, adjustment rows are logged, and the 0.20 `MaskDepthSafetyFloor` guards a dead parameter. The `(120+135*md)` formula survives only as a stale comment (`ShaderParams.cs:11`). No test binds `ShaderParams` to the renderer, so this can stay invisible forever (TEST-04).
**Fix (decided 2026-06-11 — D-2: wire them, inside the A2 single-pass rework):**
1. **B0 interim (S):** stop the chatbot/slash replies claiming the change applied — reword confirmations to honest copy ("已記錄，將於 Beta 渲染器生效 / recorded; takes visual effect in the Beta renderer"), or temporarily reject with a calm message. Honesty first, wiring later. Fix the stale `ShaderParams.cs:11` formula comment in the same commit (free).
2. **B2 — wire both knobs inside the A2 single-pass field rework** (`BETA_FEATHERING_COHERENCE_PLAN.md`): `MaskDepth` becomes a bounded multiplier on the tier-resolved peak alpha (locked `VimsLevels` table, 0.20 floor, and `MaskPeakAlphaOverride` precedence all respected); `FeatherWidth` becomes the field's feather-band-width input. Both are natural per-frame field-math uniforms — A2 needs exactly these inputs anyway. UVHS visual review gates the merge.
3. Land TEST-04's OverlayWindow round-trip STA test (pattern exists in `OverlayHeadPoseTests`) in the same commit as the wiring: apply two MaskDepth/FeatherWidth values, assert the rendered field differs.

### CW-3 · Profile-switch dead end + stale chatbot promises — `P1 / S–M`
**Findings:** FEAT-04 ⊃ UX-01.
**Problem:** Dashboard profile pills were removed 2026-06-09; `SettingsContent.xaml` has no profile control; the Processor early-rejects `SwitchProfile` (`HabituationOverrideProcessor.cs:101-105`); the in-chat `ProfileOptions` picker is unreachable dead XAML (`MainWindow.xaml:936-970`). Yet the chatbot's canned reply still sends users to "the main window's Settings" (`ChatbotViewModel.cs:373-375`) and system-prompt EXAMPLE 2 (`MistralChatbotService.cs:224-229`) still teaches the model to promise profile-switch confirmation cards that get silently rejected.
**Fix (decided 2026-06-11 — D-3: profiles are auto-managed; no user-facing switcher returns):** one cleanup commit: (1) rewrite the canned reply to honestly describe auto-management（e.g. 「SenseEase 會依您的使用情況自動調整防護設定；敏感度（前庭）保護開關在 設定」 / "SenseEase manages your comfort profile automatically; the sensitivity-protection switch lives in Settings"）; (2) remove system-prompt EXAMPLE 2 and the `HabituationProposalLexicon` profile-switch detection so the model stops promising cards the Processor rejects; (3) delete the dead `ProfileOptions` ItemsControl template, `SelectProfileCommand`, and the orphaned `ProfilePill` style (closes the UX-18/DEAD-08 slice too). Keep the Processor's `SwitchProfile` early-reject as defense-in-depth.

### CW-4 · UM3/UM4 background user-model analysis never runs — `P1 / S`
**Findings:** FEAT-01 ≡ EXIT-01, plus TEST-07.
**Problem:** `App.xaml` uses `StartupUri`, so `Application.MainWindow` is **null** throughout `OnStartup`; `(MainWindow?.DataContext as MainViewModel)?.StartBackgroundUserModelAnalysis()` (`App.xaml.cs:424-425`) is a permanent no-op. Even if started, the one-shot T+2 min `RunAsync` would usually find llama-server not yet spawned (it lazy-loads on first chat). UM4 retrospective analysis and the UM3 index advance therefore never execute in production — the User-Model sprint's "complete" status is wrong at the scheduling seam. All five analyzer tests bypass this seam (TEST-07).
**Fix:** move the kickoff to `MainWindow.Loaded` / `OnSourceInitialized` (or a one-shot `Activated` handler); add a retry after the first successful chat turn (server actually up). Factor the trigger into a testable `MaybeRunRetrospectiveAnalysis()` and pin the regression with a test.

### CW-5 · Crisis-response legibility on the popup card — `P1 / M`
**Findings:** UX-02.
**Problem:** the card's chat face renders replies in `ChatStatusText` — `FontSize=10`, `MaxHeight=32`, no scroll (`NotificationCardWindow.xaml:685-692`). The full assistant reply, **including the fixed crisis safety-floor response (~230 chars, hotlines at the tail)**, is assigned there (`NotificationCardWindow.xaml.cs:1366`) — so "988 / findahelpline.com" can be clipped invisibly at the exact moment it matters. Violates locked invariant A1 (crisis text fully presented, ≥12 px floor).
**Fix:** remove `MaxHeight`, raise to ≥12 px, let the card grow (it already supports `SizeToContent`). For the crisis path specifically: detect the SafetyFloor response and render it in a dedicated non-truncating panel, or route it to the main chat tab. UVHS the result.

### CW-6 · Packaging trust bundle — `P1 / S each`
**Findings:** PKG-01, PKG-02, PKG-03.
- **PKG-01 (P1):** the `[Registry]` Run-key entry (`installer/SenseEase-v0.11.iss:133`) uses `ValueType: string` + `dontcreatekey`, which **still writes** `SenseEase=""` because HKCU\…\Run always exists. `AutoStartRegistry.IsEnabled()` treats any string as enabled and "heals" it into a real autostart command — every 0608/0609 install silently enables autostart, against the script's own opt-in comment and H49. **Fix:** `ValueType: none; ValueName: "SenseEase"; Flags: uninsdeletevalue` (delete-only idiom); plus defense-in-depth: `IsEnabled()` treats empty/whitespace value as disabled and deletes it (heals machines polluted by 0608/0609 builds). Rebuild.
- **PKG-02 (P1):** shipped `scripts/README.txt` (restaged into dist root every build) tells testers to hash-verify a **nonexistent filename**, claims CUDA-12 (build is Vulkan-only), recommends zip-replace updates that now **trip the live H32 integrity gate** ("please reinstall"), and points at the retired `%TEMP%` diag path. **Fix:** rewrite for v0.11.3 (correct artifact name, Vulkan, "run the new installer" with the manual-replace warning, `diag\` paths, H25 uninstall prompt).
- **PKG-03 (P2, do with the bundle):** only the superseded 0608 build has a published `.sha256`; four ~2.3 GB exes accumulate undistinguished. **Fix:** emit a `.sha256` per build (stage-script step), archive/delete superseded exes, record canonical filename+hash in CHANGELOG/handbook.

### CW-7 · Settings that silently reset (real code causes) — `P1-adjacent / S–M`
**Findings:** UX-12 ≡ FEAT-15, FEAT-05 ≡ LOGIC-10 (carried NE2), LOGIC-06.
**Problem:** three distinct persistence gaps look identical to the known AV file-lock symptom but are real code: (1) the card's +/- baseline (`PersistentOverlayEnabled`/`BaselineTier`) are plain VM fields — the one effortless comfort action resets to off/L1 every restart (`MainViewModel.cs:1162-1184`); (2) chatbot/G1 shader adjustments are appended to `adjustments.jsonl` but only `FovRadius` is ever replayed at boot — `/grid off` reverts to 0.75 each launch (`MockInferenceEngine.cs:311-322`), contradicting the system prompt's "Adjustments PERSIST across sessions"; (3) `behavior_log.jsonl` is fully rewritten via `File.WriteAllText` every 60 s (`DailyBehaviorAggregateWriter.cs:103-109`) — a crash mid-write destroys all longitudinal UM1/UM5 history (H16 violation), and the reader uses a different lock instance.
**Fix:** persist the two overlay-baseline fields in `UserPreferences` (additive schema, default-OFF for fresh installs per the Kirby 2026-06-06 override); replay shader knobs from the adjustment log (or a small atomic JSON) after `SeedShaderFromProfile`, honoring `_shaderUserModified`; switch the behavior writer to tmp+`File.Replace` (reuse `AtomicJsonWriter`) and share one writer instance with `ChatbotViewModel`.

### CW-8 · Liveness honesty completion — `P1+P2 / S–M each`
**Findings:** UX-03, INTENT-03, INTENT-04, INTENT-05, LOGIC-08.
**Problem set:** Paused vs Faulted are confusable (fault copy says "paused", offers a "tap to retry" that retries nothing, tray icon never changes — `TrayIconService.cs:496-512`); the tray fault verdict is clobbered back to a healthy band label within ~0.6 s by per-tick `RefreshLabels` during self-heal warmup (INTENT-05); the contract's headline scenario — silent frame starvation — is undetectable because samples are emitted regardless of `realFrame` and `GraphicsCaptureItem.Closed` is never subscribed (INTENT-03); the Faulted-surface worst case drifted to ~6–7 s vs the locked ~5 s (INTENT-04); and the resume watchdog keys off frame arrival, so every Power-Save resume on a static screen triggers a spurious Stop/Start (LOGIC-08).
**Fix bundle:** distinct fault tray treatment + honest copy ("Sensing interrupted — reconnecting automatically"), keep "paused" wording exclusive to user pause; guard `RefreshLabels` with the `_preFaultStatusText` stash pattern the language path already uses; subscribe `GraphicsCaptureItem.Closed` → `MarkUnhealthy`+recovery, and document the accepted residual starvation gap in `liveness-contract.md`; either lower `SensingFaultedThresholdMs` to 5000 or amend the locked doc, and pin the constants in `LivenessContractTests`; key the resume watchdog off `_lastPollAt` (or delete it — the D3 watchdog covers the wedge case).

### CW-9 · Test-suite isolation & truthfulness — `P1 / S–M`
**Findings:** TEST-01, TEST-02, TEST-03, TEST-14 (plus TEST-04 in CW-2, TEST-07 in CW-4).
**Problem:** the `LOCALAPPDATA` env-var "sandbox" in test static ctors is a verified no-op (`Environment.GetFolderPath` ignores it) — chatbot VM tests **append crisis/injection payloads to the developer's real `chat_history.jsonl`** (which live UM4 would later ingest); `UserPreferencesTests` deletes/rewrites the real prefs file per test while other classes run in parallel — the documented isolation flake, and an AV-killed test host leaves real prefs deleted (mimicking the "settings reset" symptom); VM/window tests construct production-wired stores at real paths; the H2 redaction test asserts on a stub the test itself populated — the clinical "crisis phrases never persist verbatim" guarantee is unpinned.
**Fix:** one seam — `AppConfig.DataRootOverride` (test-only static, set in a collection fixture to a temp dir) used by all path properties — fixes TEST-01/03/14 together. Then drive `ChatbotViewModel.SendAsync` with a crisis input against a recording store and assert the persisted turn equals `SafetyFloor.RedactedPlaceholder` (TEST-02). Delete the ineffective env-var ctors.

### CW-10 · Head-tracking resilience — `P1+P2 / S–M`
**Findings:** SIDE-01, SIDE-02, SIDE-03, SIDE-04, FEAT-08.
**Problem set:** the Layer-1 jump filter latches when a face is re-acquired >25° from the last valid pose (detection routinely drops at extreme yaw) — the mask can freeze at a screen edge until restart (SIDE-01); one failed `cap.read()` exits the sidecar cleanly (code 0) with no reconnect and no C# restart — head-tracking dead for the session (SIDE-02); the 15 Hz scheduler never resyncs `next_tick`, so after sleep/AV-stall the loop runs unthrottled at camera rate for as long as the stall lasted (SIDE-03); on sustained face loss the sidecar repeats last-valid cx/cy at 15 Hz forever and **no C# consumer reads `FaceDetected`** — the mask parks at the last gaze position, defeating the 2 s staleness fallback (SIDE-04); and every failure mode is invisible to the user who opted in — diag-file-only (FEAT-08).
**Constraint note:** `head_tracker.py`'s algorithm core (EWMA, dead-zone, face-presence) is **locked** (HARD_CONSTRAINTS C4). **Unlock authorized 2026-06-11 (D-11)** for exactly SIDE-01 (jump filter), SIDE-02 (capture-loop re-open), SIDE-03 (scheduler resync) — the locked trio itself stays byte-untouched; record the authorization in the commit message when editing. SIDE-04 stays host-side (treat `FaceDetected==false` older than a grace window as inactive → existing center-lerp), which C4 explicitly prefers.
**Fix:** unlatch on N-consecutive-self-consistent frames or reset `last_valid` after >1 s of no face (SIDE-01); Python-side bounded capture re-open with backoff, exit non-zero only on sustained failure (SIDE-02); one-line `next_tick = now` resync when behind >2 ticks (SIDE-03); host-side FaceDetected grace (SIDE-04); surface a head-tracking health scalar in Settings + tray tooltip mirroring the D3 Faulted pattern (FEAT-08). Add the dropout-at-extreme → recenter regression test (SIDE-09's harness).

### CW-11 · Shutdown & crash-path determinism — `P2 / S–M`
**Findings:** EXIT-02, EXIT-03, EXIT-04, EXIT-05.
**Problem set:** crash-log hooks are registered ~300 lines into `OnStartup` — any throw in mutex/integrity/localization/prefs hydration (exactly where Trend-Micro transient locks bite at login-autostart) dies with zero evidence (EXIT-02); the `stack_dump.txt` thread-pileup amplifier is still live — sync `Dispatcher.Invoke` from the unguarded 1 Hz detector Poll (EXIT-03); there is no central dispose registry — `_sensing.Stop()` is never called on exit (widening the EM3 race into every quit) and `MistralChatbotService.Dispose()` has effectively zero callers, leaving the job object as the only thing reaping a 2 GB llama-server (EXIT-04); crash exits lose the ≤60 s behavior tail and `LogCrash` has no AV-lock retry (EXIT-05).
**Fix:** register all exception hooks as the first statements of `OnStartup`; change `MainViewModel.cs:915` to `InvokeAsync` + add an `Interlocked` re-entrancy gate in `Poll`; add `_sensing.Stop()` + `_mistral.Dispose()` to `DisposeServices` and adopt a CompositeDisposable pattern; best-effort behavior flush in terminating handlers + one short retry in `LogCrash`.

### CW-12 · Clock unification — `P2 / S`
**Findings:** UX-15, FEAT-10, LOGIC-04, SEC-05 (one root cause), plus LOGIC-11.
**Problem:** `EventLogEntry.Timestamp` defaults to `DateTime.Now`; engine writers set `UtcNow` explicitly; 9 App-layer durable appenders (5 in `HabituationOverrideApplicator`, 4 in `MainViewModel`) rely on the default — so durable JSONL files named by UTC day mix clocks 8 h apart (Taiwan), `ReadDurable(since)` and `IntensityStatsBuilder` windows skew, and the visible Event Log shows slash-command rows 8 h off adjacent rows. `MockSensingProvider` also stamps local time (latent hours-long cooldown freeze if Demo mode ever returns — LOGIC-11).
**Fix (one commit):** default `EventLogEntry.Timestamp = DateTime.UtcNow`; convert in the single display helper (`EventLogEntry.Time` → `ToLocalTime`); audit the ~10 appenders; pin the default Kind with a one-line test. (`MockSensingProvider.SampledAt → UtcNow` applies only if the D-9 provider deletion has not landed yet — once DEAD-03 executes, LOGIC-11 dies with the provider.)

---

## 3. UI/UX findings (21)

*P1 items UX-01/02/03 are covered in CW-3/5/8 above; listed here for completeness of the dimension.*

| ID | Sev | Effort | Title |
|----|-----|--------|-------|
| UX-01 | P1 | S | Chatbot directs to a profile switcher that no longer exists (→ CW-3) |
| UX-02 | P1 | M | Card chat replies incl. crisis response clipped to ~2 lines of 10 px (→ CW-5) |
| UX-03 | P1 | M | Paused vs Faulted confusable; fake "tap to retry" (→ CW-8) |
| UX-04 | P2 | S | Tray menu mixed-language |
| UX-05 | P2 | S | Card proposal summary + expiry countdown zh-only with engine jargon |
| UX-06 | P2 | S | Hero chip shows raw `Driver`/`Passenger` tokens; Advanced strings EN-only |
| UX-07 | P2 | S | Chat failure/loading states EN-only jargon; leak full file paths; raw exceptions as bubbles |
| UX-08 | P2 | S | Permanent "Restart needed" chip; live-looking radios; no webcam/privacy copy |
| UX-09 | P2 | M | "Clear all my data" overstates scope (chat + calibration history survive) |
| UX-10 | P2 | M | Typography floor violated in 25 places (10/11 px on live surfaces) |
| UX-11 | P2 | S | WCAG AA contrast failures masked by a false in-file claim |
| UX-12 | P2 | S | Overlay baseline session-only — silently resets (→ CW-7) |
| UX-13 | P2 | M | First-run experience is a void; `IsFirstRun` dead |
| UX-14 | P2 | M | Notification card unreachable by keyboard; sub-44 px targets |
| UX-15 | P2 | S | Event Log mixes UTC and local rows (→ CW-12) |
| UX-16 | P3 | S | Window titles don't follow the language toggle |
| UX-17 | P3 | S | Typing-dots + scrollbar fades ignore Reduce-Motion |
| UX-18 | P3 | S | Inert UI debris: invisible suppression pill, orphaned styles, collapsed cards |
| UX-19 | P3 | S | Welcome bubble stacked-bilingual; full-width parens in EN strings |
| UX-20 | P3 | M | Orphaned flags — **decided: retire both + delete paths** (D-10; escape-hatch feature removed) |
| UX-21 | P3 | S | Blocked-proposal reason half-translated at 11 px |

### Detail (P2/P3)

- **UX-04 (P2, S, localization).** `設定` (`TrayIconService.cs:147`), `退出 SenseEase` (`:150`), and the power-save status `已暫停 · 點此恢復` (`:474`) are hardcoded zh; the menu's other items localize. **Fix:** wrap in `LocalizationManager.Pick(...)` and re-pick in `RefreshLanguageDependentLabels` (plumbing already exists).
- **UX-05 (P2, S, localization+voice).** `FormatProposalSummary` emits `把 sensitivity 調整為 {v}` / `把 severe-strike-limit 調整為 {v}` — zh-only **and** internal jargon on a consumer surface; `UpdateExpiryText` countdown zh-only (`NotificationCardWindow.xaml.cs:323-336, 388-390`). The 2026-06-09 `Loc()` pass missed these. **Fix:** route through `Loc(zh,en)`; use the established plain vocabulary ("調整防護敏感度為… / Adjust protection sensitivity to …").
- **UX-06 (P2, S, voice).** Hero pill binds raw `{Binding Mode}` → literal `Driver`/`Passenger` in both languages (`MainWindow.xaml:316`); "Mask ACTIVE", "Hidden/Visible", "OFF/ACTIVE", `Suppressed: …`, `Auto · {Mode} · idle Ns` are hardcoded EN. **Fix:** a converter mapping Mode → localized plain labels (e.g. 操作中(輕度防護) / Active use · 觀看中(完整防護) / Watching — semantics may be reworded, never inverted per D1); localize or explicitly dev-label the Advanced strings.
- **UX-07 (P2, S, carried).** Chat status leaks absolute model paths (username) and tech jargon; the generic catch shows `[Error] {ex.Message}` as an assistant bubble (`ChatbotViewModel.cs:213-215, 282, 297-307, 628`). **Fix:** localized calm copy ("助理正在準備，第一次約需 30 秒…"; "目前無法使用助理，仍可使用快速指令"); probe-path detail to the Event Log; never render raw exceptions. Pairs with DEAD-06 (wire `ModelPathProbes` into the Event-Log detail).
- **UX-08 (P2, S, honesty).** The Restart-needed chip has no pending-change logic — it shows on every Settings open (`SettingsContent.xaml:343-396`); follow-mode radios stay active while tracking is off; the 頭部追蹤與隱私 section has zero webcam/local-only copy at the point of consent (B1). **Fix:** show the chip only when `HeadTrackingEnabled` differs from the app-start value; `IsEnabled`-bind the radios; add one caption: "開啟後會使用視訊鏡頭，影像僅在本機處理，不會儲存或上傳 / Uses your webcam; processed locally, never stored or uploaded."
- **UX-09 (P2, M, honesty/H24).** `OnClearAllDataClicked` deletes behavior log/index/observations/model_trace only — `chat_history.jsonl` and `calibration_history.jsonl` (the designated content-bearing PII files) survive a button labelled "清除所有資料並重設 / Clear all my data and reset" (`SettingsContent.xaml.cs:400-444`). **Fix:** either include both files (confirm dialog enumerates them) or rename to "Clear behaviour model" and point at the chat Clear for transcripts. Coordinate with FEAT-13/DEAD-07 (calibration-history orphan).
- **UX-10 (P2, M, carried).** 25 occurrences of `FontSize="10|11"` across 5 XAML files — Settings captions, card rationale/safety-note, expiry, chat status, SpikePopup hint, ValidationModeBanner — against the theme's own locked floor "no 10/11px text anywhere" (`SenseEaseTheme.xaml:74-75`), for an audience that may be nauseated. **Fix:** sweep to 12 px `Type.Caption` styles; add the orphan-size lint to CI grep.
- **UX-11 (P2, S, carried).** Verified math: `TextTertiary #8A93A3` on white = **3.10:1** at caption sizes (needs 4.5); GhostBtn accent-on-muted = **3.76:1** (`MainWindow.xaml:87-90`); the theme header falsely claims "All text pairs clear WCAG AA 4.5:1" (`SenseEaseTheme.xaml:25`). **Fix:** darken `TextTertiary` ≈ `#6B7585`, ghost-button text to `AccentHover #3552D9`; re-verify dark palette; replace the in-file claim with a token contrast test.
- **UX-13 (P2, M).** First-run is a void: the welcome banner was removed with the calibration demo (comment `MainWindow.xaml:284-285`), `AppConfig.IsFirstRun`/`MarkFirstRunComplete` have zero consumers, and the first thing a fresh user sees is a medical-adjacent vestibular question with no framing; close-to-tray is unexplained until the handbook. **Fix:** one dismissible first-run hero strip keyed off the existing flag (what the overlay does · close ≠ quit · where Settings live), sequenced **before** the vestibular prompt. Delete or use the dead flag either way (FEAT-12).
- **UX-14 (P2, M, carried, accessibility).** Card HWND is permanently `WS_EX_NOACTIVATE` (`Interop/ClickThrough.cs:76-96`, applied at `NotificationCardWindow.xaml.cs:506`) — keyboard/switch users cannot dismiss, adjust, or answer proposals at all; Close/Flip are 22×22, +/- pills 30×30 (target 44×44). **Fix:** global hotkey or tray-menu item that activates the card when visible (`SetForegroundWindow` works despite NOACTIVATE); enlarge hit areas via transparent padding, keep 22 px glyphs. NOACTIVATE itself is intentional quiet-guardian design — keep it.
- **UX-16 (P3, S).** `SettingsWindow` title zh-only, `EventLogWindow` zh-only, `MainWindow` EN-only. `{loc:T}` returns a Binding and works on `Title`. **Fix:** bind all three.
- **UX-17 (P3, S, carried).** Typing-dots run `RepeatBehavior="Forever"` with no `AccessibilityHelper.ReducedMotion()` gate (`MainWindow.xaml:869-888`) — decorative pulsing for the entire ~30 s first LLM load on a motion-sensitivity product; `AutoHideScroll` fades likewise. **Fix:** gate; swap to static "…" when reduced; snap scrollbar opacity.
- **UX-18 (P3, S).** The only consumer of `ActivityStatus`/`AutoModeText` (the "why isn't sensing reacting?" explanation) is a hardcoded-Collapsed expander; `ProfilePill` style orphaned; Session-Summary card Collapsed; legacy Engage/Mute buttons kept-Collapsed for event wiring. **Fix:** move the ActivityStatus row into the Dashboard Advanced expander (it answers a real user question), then delete the rest (keep VM properties used by tests).
- **UX-19 (P3, S).** `WelcomeMessage` const stacks zh+en regardless of toggle (`ChatbotViewModel.cs:80-82`); EN branch uses full-width parens (`:872-873`). **Fix:** split into zh/en consts picked at display time (never persisted — safe); fix parens.
- **UX-20 (P3, M, carried — decided 2026-06-11, D-10: formally retire both).** `BarTrayColorEnabled` (default false) still gates the tray accent dot, and `HeadTrackingEscapeHatchEnabled=true` still arms double-tap-Ctrl, but both toggles were removed from Settings 2026-06-09 — JSON-edit is the only access, and the chord doesn't fire end-to-end anyway (FEAT-06). **Fix (one retirement commit):** delete `BarTrayColorEnabled` plus the tray accent-dot / `PersistentBarWindow` colour path; delete the **entire escape-hatch feature** — `HeadTrackingEscapeHatchEnabled`, the `PreviewKeyDown` chord detection (`MainWindow.xaml.cs:437-480`), `OverlayWindow.TriggerEscapeHatch`, and the hidden hotkey doc panel (`SettingsContent.xaml:515`). Keep reading both JSON fields tolerantly so old prefs files load. **Doc cascade:** remove the M5.1-B double-tap-Ctrl section from both handbooks; update `HARD_CONSTRAINTS.md` A3 (the invariant constrained the hatch *if present* — full removal is the conservative direction: the mask is never weakened, and the vestibular force-disable becomes vacuous). Supersedes FEAT-06 — no global hotkey is built.
- **UX-21 (P3, S).** Applicator `Reason` strings are hardcoded EN (`HabituationOverrideApplicator.cs:97-182`); the card prepends a localized prefix → half-translated 11 px safety note. **Fix:** code→`Pick(zh,en)` map at the card layer; 12 px. (Chip colour/italics are by-design — verified.)

---

## 4. Feature findings (16)

*FEAT-01/02/03/04 are CW-4/1/2/3. FEAT-05/15 are CW-7. FEAT-08 is CW-10.*

| ID | Sev | Effort | Title |
|----|-----|--------|-------|
| FEAT-01 | P1 | S | UM3/UM4 analysis never runs (→ CW-4) |
| FEAT-02 | P1 | M | Habituation opt-in unreachable; pause flips via side effects (→ CW-1) |
| FEAT-03 | P1 | M | MaskDepth/FeatherWidth render-inert (→ CW-2) |
| FEAT-04 | P1 | M | Profile switching has no working entry point (→ CW-3) |
| FEAT-05 | P2 | M | Shader adjustments not persisted across restarts (NE2; → CW-7) |
| FEAT-06 | P2 | — | Escape hatch non-functional — **superseded by D-10: feature retired** (see UX-20) |
| FEAT-07 | P2 | S | `AutoProfilingEnabled=false` revert contract broken |
| FEAT-08 | P2 | M | Head-tracking failures invisible to the opted-in user (→ CW-10) |
| FEAT-09 | P2 | S | 1 MB/day durable-log cap silently drops the rest of the day |
| FEAT-10 | P3 | S | Durable safety-intercept entries persist local time (→ CW-12) |
| FEAT-11 | P3 | S | Cursor "fallback" documented but unreachable; Demo/Cursor dead |
| FEAT-12 | P3 | S | First-run flag machinery dead |
| FEAT-13 | P3 | S | Orphaned `calibration_history.jsonl` survives every clear path |
| FEAT-14 | P3 | S | `ResetToDefaults` wired to nothing — **decided: wire to Settings + confirm** (D-15) |
| FEAT-15 | P3 | S | Card +/- level session-only (→ CW-7) |
| FEAT-16 | P3 | M | AdaptiveFit selector data starvation: calm users may never leave Settling |

### Detail

- **FEAT-06 (P2, carried — punchlist P0; superseded 2026-06-11 by D-10).** Escape-hatch detection lives in `MainWindow.PreviewKeyDown` (`MainWindow.xaml.cs:437-480`); no `RegisterHotKey`/hook exists repo-wide, so the documented "double-tap Ctrl dims the mask to 30% for 3 s" silently does nothing in normal (dashboard-hidden) use. **Resolution: do not build the global hotkey — the escape-hatch feature is formally retired with its flag and all code paths (see UX-20 / D-10).** The broken-affordance honesty problem disappears with the feature; remember the handbook M5.1-B removal in the same change.
- **FEAT-07 (P2, S) ≡ DEAD-02.** `AppConfig.cs:276-294` still promises "set to false to restore the explicit-calibration flow (suppressed-not-removed)" — but commit `3131387` **deleted** the calibration subsystem. Flag-off now strands fresh users at RESILIENT/L1 with no onboarding (the Settings vestibular group also hides under flag-off). The documented byte-identical revert is impossible. **Fix (decided 2026-06-11 — D-8: keep the flag through Alpha; flag-off = retain the TYPICAL/L5 seed):** (1) rewrite the `AppConfig` doc-comment — flag-off no longer restores explicit calibration (that requires `git revert 3131387`); its defined semantics are now "fresh users still seed at TYPICAL/L5; only the vestibular prompt, console visibility, and selector-adjacent UI react to the flag". (2) Make the code match the new semantics: ensure the TYPICAL/L5 seeding in `ApplyAutoProfilingStartDefaults` runs (or is preserved) under flag-off, so flag-off can never strand a fresh user at RESILIENT/L1. (3) Pin with a test: flag-off fresh install ⇒ profile TYPICAL + seed L5. (4) Update `ALPHA2_CORE_ARCHITECTURE.md`'s "suppressed-not-removed / byte-identical revert" claim. (5) **Per D-13, the same cleanup commit deletes the dead plumbing** — `ApplyCalibrationResult` (`MockInferenceEngine.cs:1293`), `CalibrationProcessor.Process`, and their test suites — keeping the engine-consumed constants; port the vestibular-guard tests to the live `SetVestibularProtection` path first (see DEAD-02 / TEST-09).
- **FEAT-09 (P2, S, carried) ≡ LOGIC-07 ≡ SEC-04.** `AuditLogFiles.Append` silently returns once the day file hits 1 MB (`FileEventLogStore.cs:129-140`) — no marker, no rotation. Downstream: the selector's 7-day stats window truncates unseen; `Chatbot.Declined` entries lose restart durability (rule-5 cooldown); `Safety.*` forensic entries vanish. **Fix:** on first cap hit write one `[truncated: daily cap]` sentinel; roll to a bounded `.overflow1.jsonl`; at minimum always append `Safety.*` and `Chatbot.Declined` regardless of cap.
- **FEAT-11 (P3, S) ≡ DEAD-03.** Whitepaper calls the cursor provider the sensing "fallback", but no code path ever assigns `SensingMode.Cursor` (or `Demo`); a permanent WGC failure just shows Faulted. `MockSensingProvider`/`CursorVelocityProvider` are constructed, never started. **Fix (decided 2026-06-11 — D-9: retire):** update the whitepaper (cursor sensing is no longer a fallback tier; permanent WGC failure surfaces Faulted per the liveness contract) and delete the dead modes in one commit per the full DEAD-03 debris list: `SensingMode.Demo`/`Cursor` branches, `MockSensingProvider`, `CursorVelocityProvider`, the `UseMockSensing` branches (`MainViewModel.cs:819-824, 992-996, 1011-1012, 1369`), `RejectCursorDragOfi`, and the "DEMO MODE"/"CURSOR VELOCITY" labels. **Keep `WindowsActivityFilter`** — it is live for WGC (`WgcSensingProvider.cs:748`). **Cascade:** this deletion also closes LOGIC-11 (the local-time stamp dies with the provider) and the cursor half of LOGIC-14.
- **FEAT-12 (P3, S).** `IsFirstRun`/`MarkFirstRunComplete`/`FirstRunFlagPath` have zero consumers since the calibration removal. **Fix:** delete, or repurpose as UX-13's first-run signal.
- **FEAT-13 (P3, S) ≡ DEAD-07.** `calibration_history.jsonl` — a whitelisted content-bearing PII transcript from pre-0.11.3 builds — has no writer, no reader, and is skipped by both the chat Clear and the Settings clear-all; only the opt-in uninstall wipe removes it. Contradicts H24 for upgraded installs. **Fix:** add it to the Settings clear-all list **and** a one-time `App.OnStartup` migration delete (mirroring `display-baseline.json`); update `LOG-PATHS.md` in the same commit per its locked rule.
- **FEAT-14 (P3, S — decided 2026-06-11, D-15: wire it).** `UserPreferences.ResetToDefaults` (documented 2026-05-26 as the chatbot-offerable "reset my preferences") has zero callers. **Fix:** add a "重設偏好設定 / Reset preferences" action in Settings wired to the existing method, behind a `ThemedDialog` confirm that plainly enumerates what resets (theme, language, toggles, HUD position). Per the original contract comment, the confirm-and-reset also clears `VestibularWarningShown` **by design** — a transitional clause: once D-14's field deletion lands there is nothing left to clear and the confirm copy must not mention it. After the D-10 retirements the method also stops touching the deleted flags.
- **FEAT-16 (P3, M).** Selector stats count **lifecycle event rows**, not time samples — a calm user generates ~1 durable row per launch, below the 12-sample floor, so the live selector can sit in Settling indefinitely; eventful users' P50/P90 are spike-biased (`IntensityStatsBuilder.cs:46-108`). **Fix options:** a capped low-rate durable heartbeat row with current pressure, or source stats from `behavior_log` daily aggregates. Validate against the M1 capture follow-up; **no SenseDefaults value changes.**

---

## 5. Logic findings (14)

*LOGIC-01 → CW-1 · LOGIC-04 → CW-12 · LOGIC-06/10 → CW-7 · LOGIC-07 → FEAT-09 · LOGIC-08 → CW-8.*

| ID | Sev | Effort | Title |
|----|-----|--------|-------|
| LOGIC-01 | P1 | S | Set move/level snap clears the safety pause (→ CW-1) |
| LOGIC-02 | P2 | S | `WgcSensingProvider.Start()` not serialized — double-init / COM leak race |
| LOGIC-03 | P2 | M | Sync file IO (with AV-retry sleeps) inside the engine lock on the UI thread |
| LOGIC-04 | P2 | S | App-layer durable entries persist local time (→ CW-12) |
| LOGIC-05 | P2 | M | Any cursor movement zeroes WGC OFI for 300 ms (NE9) |
| LOGIC-06 | P2 | S | `behavior_log.jsonl` rewritten non-atomically every 60 s (→ CW-7) |
| LOGIC-07 | P3 | S | 1 MB/day cap silent drop (≡ FEAT-09) |
| LOGIC-08 | P3 | S | Spurious WGC re-init 2 s after every Resume (→ CW-8) |
| LOGIC-09 | P3 | S | Stale hysteresis comment 100/85 vs actual 85/72.25 (NE1) |
| LOGIC-10 | P2 | M | Shader adjustments never restored at boot (NE2; → CW-7) |
| LOGIC-11 | P3 | S | `MockSensingProvider` stamps local time (→ CW-12) |
| LOGIC-12 | P3 | M | H14 residual: deletable flag file re-enables v1 downgrade null-fill |
| LOGIC-13 | P3 | S | Auto-profiling L5 seed erased by fast-release on first calm tick |
| LOGIC-14 | P3 | S | `CursorVelocityProvider` timer never disposed (NE11 residual) |

### Detail

- **LOGIC-02 (P2, S).** `Stop()` and `OnTick` take `_lifecycleLock`; `Start()` does not (`WgcSensingProvider.cs:435-610`). Fault recovery (`Task.Run`), the resume watchdog (timer thread), and UI `Resume()` can race `Stop();Start()` — two threads pass `if (_running) return` before either sets it, double-running init and leaking the first session's AddRef'd COM pointers (stuck yellow border, native memory). **Fix:** wrap `Start()`'s body in `lock(_lifecycleLock)`, re-check `_running` inside; optionally coalesce all re-init paths through one method.
- **LOGIC-03 (P2, M, carried W1W2 #15).** Each dirty tick runs `_profileStore.Update` → `AtomicJsonWriter.TryRead` (Thread.Sleep 40/80/120/160 ms on transient locks ≈ 400 ms worst) + fsync write **under `_gate` on the UI dispatcher**; the selector additionally parses the full 7-day durable JSONL every ~60 s under the same lock. On this machine's AV that's a real ~0.5 s UI freeze. **Fix:** snapshot under `_gate`, then persist/read on a single-writer background queue; feed selector stats via a cached field.
- **LOGIC-05 (P2, M, carried NE9 — decided 2026-06-11, D-4: not by design; attenuate, don't zero).** `ShouldSuppressOfi` holds 300 ms after **any** pointer delta and `OnTick` hard-zeroes raw OFI while suppressed — continuous mouse motion while video/scroll plays means zero pressure accumulation exactly during real vection (`IsCursorCaptureEnabled=false` voids the sprite-noise rationale). The Wave-12 "NE9 doc-cleanup only" triage is **withdrawn**. **Fix:** during the cursor-move window in WGC mode, apply a **0.25 weight multiplier** to raw OFI instead of the hard zero — replace the suppression branch's `rawOfi = 0` with `rawOfi *= CursorMoveOfiAttenuation` (new **additive** named constant `= 0.25`; intentionally mirrors the Driver-attenuation magnitude but stays a separate constant — no locked values change). Keep the 300 ms window logic as-is. Add a test: sustained synthetic OFI with continuous cursor motion accumulates ~25% of unsuppressed pressure, not zero. (Cursor-provider-only suppression is moot — the provider retires under D-9.)
- **LOGIC-09 (P3, S, carried NE1) ≡ INTENT-07.** One-line comment fix at `MockInferenceEngine.cs:397` ("trigger at TriggerPressure (85), release below 85% of it (72.25)"). Bundle into DEAD-10's comment commit.
- **LOGIC-12 (P3, M, carried H14 residual) ≡ SEC-09.** Downgrade refusal = `File.Exists(schema_floor_v2.flag)` — delete the flag + plant a v1 file and v2 safety fields null-fill (mitigated: the engine re-derives `MedicalAdvisoryShown` from the in-memory vestibular flag). **Fix:** carry the schema floor in a second location (user-preferences) or HMAC it; fold into the H44/H45 tamper wave. Documented-loose in `HABITUATION_ADJUSTMENT_PLAN §4.D`.
- **LOGIC-13 (P3, S — decided 2026-06-11, D-5: protect the seed).** `ApplyAutoProfilingStartDefaults` seeds `_vimsTier=5` for fresh users, but the first calm Update tick snaps tier→1 via fast-release (gap 4 ≥ 1.5) within ~66 ms — the documented "fresh users start at L5「中」" is never user-visible (and the flag defaults ON, so this affects every fresh install). **Fix:** exempt the seeded tier from fast-release during an initial stable window (e.g. until the first genuine pressure-driven tier change, or a fixed settling period) so a fresh user actually sees and feels the L5 median before the engine takes over. Record in the Alpha-2 addendum docs that this is **deliberate onboarding protection**, not a change to tier projection; pin with a test (fresh seed + calm ticks within the window ⇒ tier stays 5; window elapsed ⇒ normal projection resumes).
- **LOGIC-14 (P3, S, carried NE11 residual).** `CursorVelocityProvider`'s timer is never disposed; **and the verifier found `WgcSensingProvider.Dispose()` itself is never called from `DisposeServices`**. **Fix (post-D-9):** the cursor-provider half is closed by the D-9 deletion (FEAT-11/DEAD-03); the remaining task is wiring `WgcSensingProvider.Dispose()` into teardown — folds into CW-11/EXIT-04.

---

## 6. Security & privacy findings (12)

*SEC-04 ≡ FEAT-09 · SEC-05 → CW-12 · SEC-09 ≡ LOGIC-12.*

| ID | Sev | Effort | Title |
|----|-----|--------|-------|
| SEC-01 | P2 | S | Username/install paths leak into `wgc_diag.txt` + `llama-server.log` (H20/H21 residual) |
| SEC-02 | P2 | S | Tester log-zip bundles the full PII corpus (H22) |
| SEC-03 | P2 | M | H32 integrity gate is delete-to-bypass; manifest unsigned in user-writable dir |
| SEC-04 | P2 | S | 1 MB/day cap drops Safety/Declined entries (≡ FEAT-09) |
| SEC-05 | P3 | S | Local-time persisted timestamps (→ CW-12) |
| SEC-06 | P3 | S | llama-server Bearer key on the child command line |
| SEC-07 | P3 | S | `LlamaServerCalibrationClient` missing H34 hardening |
| SEC-08 | P3 | S | Unbounded `ReadAllLines` + uncapped growth on remaining JSONL stores (H41/H42 residual) |
| SEC-09 | P3 | M | Schema-downgrade flag-file bypass (≡ LOGIC-12) |
| SEC-10 | P2 | L | External-gated supply chain: H26 cert, H33 llama.cpp, H47 opencv CVE, H48 PyInstaller pin |
| SEC-11 | P3 | M | Engine persist can null carried-forward fields when an existing profile reads null (narrow W2.0-C residual) |
| SEC-12 | P3 | S | Sidecar `PRIVACY.md` omits the `--diag` CSV escape valve (doc-only) |

### Detail

- **SEC-01 (P2, S, carried).** `PathRedactor` (H20) was never built — comments say so verbatim (`MistralChatbotService.cs:418-419, 493-495`); `App.xaml.cs:275-278` writes `Assembly.Location` (full `C:\Users\<name>\…`) into `wgc_diag.txt`; `SidecarHeadPoseReader.cs:319` logs the full sidecar path. The handbook log-zip then ships these. **Fix:** lift `App.xaml.cs`'s redaction regex into a small Infrastructure `PathRedactor`; apply in `WgcDiagLog.Append/WriteHeader` and the stderr pump.
- **SEC-02 (P2, S, carried H22).** `HANDBOOK_en.md:232-246` (and zh) zips `$env:LOCALAPPDATA\SenseEase\*` — chat transcripts, `habituation_profile.json` (medical-advisory/vestibular flags), behavior log — to the test team by default. **Fix:** ship `scripts/collect-logs.ps1` including only `logs\`, `diag\`, `crash.log`, `llama-server.log` and explicitly excluding the five content-bearing files; full zip becomes an explicit opt-in second command. Fix both handbooks' stale `%TEMP%` path while there (PKG-09).
- **SEC-03 (P2, M, carried H32-residual).** `IntegrityVerifier.Verify()` returns true when `install.manifest.json` is absent, and the manifest ships unsigned into the same user-writable dir as the binaries it guards — delete-to-bypass. Partially by-design for Alpha-1 (signing blocked on H26), but the scope is undocumented. **Fix now:** document the gate's limited scope in `docs/SECURITY.md`. **Fix at H26:** sign the app and either compile expected child hashes into the signed assembly or Authenticode-verify children instead of the sidecar manifest. See also PKG-06 (the manifest hashes the wrong file set).
- **SEC-06 (P3, S).** `--api-key {_apiKey}` on the child command line is readable by any same-user process (WMI `Win32_Process`) — the exact attacker class H9's loopback auth excludes. **Fix:** pass via child environment (`LLAMA_ARG_API_KEY`), drop the CLI arg.
- **SEC-07 (P3, S, carried) ⊂ EXIT-06.** `LlamaServerCalibrationClient.cs:52` keeps a 5-minute timeout, no `MaxResponseContentBufferSize`, no `MaxDepth` — H34 landed only in `MistralChatbotService`. Live once CW-4 fixes UM4. **Fix:** mirror 60 s / 512 KiB / MaxDepth 16.
- **SEC-08 (P3, S, carried).** `FileAdjustmentLog` (`:35, :71`), `FileChatbotActionLog.LoadRecent:113`, `DailyBehaviorAggregateWriter:83` still use unbounded `ReadAllLines`; `adjustments.jsonl` grows without bound across months. The 64 KB huge-line guard exists only in `FileChatHistoryStore`. **Fix:** reuse that pattern (`File.ReadLines` + skip >64 KB lines); startup trim for `adjustments.jsonl` (keep last ~2000 — `WeightFor` tares at the last reset anyway).
- **SEC-10 (P2, L, external-gated — keep on the Beta-entry checklist).** `opencv-python==4.10.0.84` (H47 CVE), PyInstaller unpinned (H48), llama.cpp `b9294` (H33 — note the cited CVE needs `--context-shift`, which the app disables, so live exploitability is gated), unsigned installer (H26 — **start cert procurement now**; 2–4 wk lead). After bumps: regenerate the H32 manifest + `.sha256` sidecars.
- **SEC-11 (P3, M).** W2.0-C guards the boot-time case (`_profileExistedAtLoad`), but a profile **created mid-session** can still be clobbered if a transient AV lock makes `Load()` return null during a dirty tick (`BuildProfileSnapshot`, guard at `MockInferenceEngine.cs:1653`). **Fix:** make `TryRead` distinguish file-missing from file-unreadable; skip the persist (retry next tick) when unreadable.
- **SEC-12 (P3, S, doc-only).** Add one `PRIVACY.md` paragraph: `--diag` exists, writes derived pose scalars only, dev-only, never passed by the host (`BuildSidecarArgs` is the enforcement point).

---

## 7. Effectiveness of dev intention (9)

**What HOLDS (verified end-to-end, do not re-audit):** the Chang-Hain leaky-TDP implementation (time-corrected decay `0.965^(dt/0.1)`, MAD×4.0 → suppress → clamp → median → EMA pipeline, raw-trigger vs EffectiveTrigger asymmetry); the "never initiate" contract (LLM sends only from user turns; proposals surface only behind user-opens); the feathering P1 radius envelope (attack 25 ms / release 600 ms; P2/P3 confirmed absent **as planned**); the safety-floor chain incl. W1W2 #1/#6/#7/#9/#10; the QA gates exist (lint ratchet, parity, D6, schema tests — with the integrity caveats in §8).

| ID | Sev | Effort | Title |
|----|-----|--------|-------|
| INTENT-01 | P1 | S | Silent set move exits the locked safety pause (≡ LOGIC-01 → CW-1) |
| INTENT-02 | P1 | M | Mask-depth/feather controls inert (≡ FEAT-03 → CW-2) |
| INTENT-03 | P2 | M | Liveness contract can't detect silent frame starvation (→ CW-8) |
| INTENT-04 | P3 | S | Faulted-surface latency drifted ~5 s → ~7 s vs locked number (→ CW-8) |
| INTENT-05 | P2 | S | Tray fault verdict clobbered during self-heal (→ CW-8) |
| INTENT-06 | P2 | S | `DisplayStressPercent` regressed to dead code — personalization invisible again (R1) |
| INTENT-07 | P3 | S | Stale hysteresis comment (≡ LOGIC-09) |
| INTENT-08 | P3 | S | Vestibular "regret" toggle never unblocks the chatbot relaxation channel |
| INTENT-09 | P3 | S | D6 default-ON decision reversed in code; locked doc + test comment stale |

### Detail

- **INTENT-06 (P2, S — decided 2026-06-11, D-6: surface it).** The 2026-06-09 hero-meter coherence fix rebound the meter to `VimsTier`; `DisplayStressPercent` — shipped by the Cross-audit precisely to fix root cause R1 ("personalization not user-visible") — is computed every tick and rendered nowhere; the "Relative to your personal threshold" caption is gone. **Fix:** add a `DisplayStressPercent` row to the Dashboard **Advanced/telemetry expander** with the original literal caption "Relative to your personal threshold"; keep the engine computation untouched; whitepaper §6.5 stays as-is (it becomes true again once the row ships). Synergy: UX-18 moves the ActivityStatus row into the same expander — do both in one pass. The hero meter keeps `VimsTier`; the coherence fix stands.
- **INTENT-08 (P3, S — align two safety channels).** After the Settings regret-toggle turns vestibular protection OFF, the engine reverts (trigger 85, strike override null) and the **selector** may propose weaker bands, but `MedicalAdvisoryShown` stays `Vestibular` forever, so the **chatbot** relaxation channel stays blocked with a vestibular-advisory message — two channels disagree. Fails safe (over-blocks), hence P3. **Fix (decided 2026-06-11 — D-7: clear on regret-off):** when the Settings regret toggle deliberately disables vestibular protection, clear `MedicalAdvisoryShown` (or equivalently gate the Applicator Rule-2 / Processor checks on `VestibularProtectionEnabled`) so the chatbot and selector channels agree. Document the rule in `SAFETY-PROVISIONS §3.1` — "the advisory is active exactly while vestibular protection is enabled; the Settings off-switch clears both channels" — and add a parity test beside `VestibularProtectionParityTests`: regret-off ⇒ both channels unblock; re-enable ⇒ both re-block.
- **INTENT-09 (P3, S, doc-only).** The 2026-06-06 Kirby override (`_persistentOverlayEnabled=false`) is deliberate, but `docs/design/LOCKED_DIRECTION_ALPHA1.md` (§1 item 2 / D6 row) and the `D6DayOneProtectionTests` header still document default-ON. Reactive protection IS on day-1 (overlay force-shown on Loaded). **Fix (confirmed 2026-06-11 — D-12):** append to `LOCKED_DIRECTION_ALPHA1.md` (§1 item 2 / D6 row): "2026-06-06 Kirby override: `_persistentOverlayEnabled = false`; rationale: default-OFF for fresh installs; sign-off: Kirby". Fix the `D6DayOneProtectionTests` header in the same change (same edit as TEST-05). No code change — the override is deliberate.

---

## 8. Test-suite integrity (14) — *gap-fill dimension*

*TEST-01/02/03/14 → CW-9 · TEST-04 → CW-2 · TEST-07 → CW-4.*

| ID | Sev | Effort | Title |
|----|-----|--------|-------|
| TEST-01 | P1 | M | Chatbot VM tests write crisis/injection payloads to the dev's real chat history (→ CW-9) |
| TEST-02 | P1 | S | H2 redaction "boundary" test is a tautology (→ CW-9) |
| TEST-03 | P1 | S | Prefs tests delete the real prefs file in parallel — root of the known flake (→ CW-9) |
| TEST-04 | P1 | M | No test binds ShaderParams to the renderer (→ CW-2) |
| TEST-05 | P2 | S | D6 test exercises a private mirror; never asserts the (reversed) production default |
| TEST-06 | P2 | S | MainWindow smoke test overclaims binding-error coverage |
| TEST-07 | P2 | M | UM4 tests bypass the scheduling seam where the shipped bug lives (→ CW-4) |
| TEST-09 | P2 | S | Vestibular L9 floor test self-admittedly vacuous; guard tested via dead calibration path |
| TEST-10 | P2 | S | Locked clinical constants not literal-pinned — the lock has no automated enforcement |
| TEST-11 | P2 | S | BannedPhrase lint scans only `src/SenseEase.App`; misses Core/Infrastructure copy + XAML element content |
| TEST-12 | P3 | S | DesignLint is count-only — a banned hex can re-enter under the baseline |
| TEST-13 | P3 | S | Symlink-guard test self-passes invisibly on unprivileged machines |
| TEST-14 | P2 | M | VM/window tests run against real %LOCALAPPDATA% (→ CW-9) |
| TEST-15 | P3 | S | Stale gate-test headers; ~48 calibration tests pin production-dead code unlabelled |

### Detail

- **TEST-05 (P2, S).** `D6DayOneProtectionTests` defines a private copy of the floor math and never calls production `ResolveDisplayedTier`; its header claims defaults the code reversed. **Fix:** call the real method; `Assert.False(vm.PersistentOverlayEnabled)` with a dated comment pinning the Kirby override; rewrite the header.
- **TEST-06 (P2, S).** The smoke test's doc claims it fails on "any XAML binding error" — binding failures are silent traces, not exceptions (the cited VimsTier bug only failed because it threw). **Fix:** attach a `PresentationTraceSources.DataBindingSource` listener at Error level and fail on captures; add 2–3 cheap post-layout state assertions.
- **TEST-09 (P2, S).** The L9 clamp on an actually-applied/confirmed move is never asserted (the test's own comment says "maintained vacuously"). Note: the live `SetVestibularProtection` guard path IS covered by `AdaptiveFitProposeConfirmTests` — the remaining gap is the clamp. **Fix:** a propose-confirm test asserting `confirmLevel >= 9` for a vestibular user through `ApplyProposedSetMove`. **D-13 cascade — the live-path port is now mandatory:** the `ApplyCalibrationResult`-based guard tests are deleted with the plumbing, so their `SetVestibularProtection` duplicates must land in the same commit (see DEAD-02 sequencing guard).
- **TEST-10 (P2, S — cheap, high lock-value).** Tests use `SenseDefaults.X` symbolically as the expected value, so changing `TriggerPressure=85`, `NoiseGateThreshold=8`, `PassiveFlowWeight=2.0`, `WgcMadNormFactor=4.0`, or `CursorEmaAlpha=0.55` fails **nothing**. **Fix:** literal pins in `SenseDefaultsConstantsTests`, each commented with the lock source. (Structural guard; no value changes.)
- **TEST-11 (P2, S).** The §6 banned-phrase gate misses Core/Infrastructure user-facing strings (11 Core files carry Han copy; e.g. `耐受` in `HabituationProposalLexicon.cs:111` escapes today), XAML element-content text, EN "symptom" and `{N} minutes` patterns from the locked spec. **Fix:** extend scan scope + element-content regex; beware input-matcher lexicons (legit zh matches) — scope the zh check to output-copy files.
- **TEST-13 (P3, S).** The H50 symlink-rejection test does `Assert.True(true); return;` when symlink creation is unprivileged — reports PASSED forever on non-Developer-Mode machines. **Fix:** `SkippableFact` so CI shows SKIPPED.
- **TEST-15 (P3, S).** `AdaptiveFitWiringTests` header still says the flag is default-OFF (it asserts ON at line 172); ~48 calibration tests pin a deliberately-kept-but-dead surface with no label, inflating the green-count's meaning. **Fix (updated by D-13):** update the stale `AdaptiveFitWiringTests` header; the calibration-test labeling half is **superseded** — D-13 deletes those suites outright with the plumbing (see DEAD-02), so no banner/trait is needed.

---

## 9. Dead code & repo hygiene (15) + packaging/lifecycle remainder

### Dead code (DEAD-)

| ID | Sev | Effort | Title |
|----|-----|--------|-------|
| DEAD-01 | P2 | S | LLamaSharp + Cuda12 NuGet packages completely unused (ship as dead weight; AV re-scans them) |
| DEAD-02 | P2 | M | Auto-profiling revert path dead (≡ FEAT-07) — **decided: delete the test-only plumbing** (D-13) |
| DEAD-03 | P3 | M | Demo/Cursor sensing modes unreachable (≡ FEAT-11) |
| DEAD-04 | P3 | S | `VestibularWarningShown` dead field — **decided: delete field + all doc refs** (D-14) |
| DEAD-05 | P3 | S | `MockNluService` zero instantiations — dead class receiving safety-floor maintenance |
| DEAD-06 | P3 | S | Write-only members: `AppConfig.ModelPathProbes`, `IsMockHeadPoseSweepActive` |
| DEAD-07 | P2 | S | `CalibrationHistoryPath` dead config + orphaned PII file (≡ FEAT-13) |
| DEAD-08 | P3 | S | Unused XAML: 3 styles, 2 never-bound converters, ~9 orphan theme tokens |
| DEAD-09 | P3 | S | 6 zero-ref "DEPRECATED (Phase 2)" constants inside the locked SenseDefaults file |
| DEAD-10 | P3 | S | 8 load-bearing stale comments (incl. false "default OFF" claims on live flags) |
| DEAD-11 | P3 | M | `use_new_bar_overlay` legacy SpikePopup path past its planned retirement (v0.8 → v0.11.3) |
| DEAD-12 | P3 | S | `samples/` dead fixtures tracked |
| DEAD-13 | P3 | M | Doc archive sweep: Phase-1 `ARCHITECTURE.md` actively wrong; stale `HANDOVER.md`; CHANGELOG stops pre-v0.11 |
| DEAD-14 | P3 | S | Dev hotkeys live in production: Ctrl+Shift+H mock sweep, Ctrl+Shift+F forced-fault |
| DEAD-15 | P3 | S | `.claude/settings.local.json` tracked (machine paths); sprint scratch in `baseline/` |

Key notes:
- **DEAD-01 (do with PKG-08):** zero `using LLama` anywhere; the side-loaded llama-server made both `PackageReference`s dead. Delete both lines; keep the b9294 pin as a comment if it documents the side-loaded binaries.
- **DEAD-02 (decided 2026-06-11 — D-13: delete, executed with the FEAT-07/D-8 commit):** remove `ApplyCalibrationResult` (`MockInferenceEngine.cs:1293`), `CalibrationProcessor.Process`, and their test suites (`CalibrationProcessorTests`, `CalibrationApplicationTests`, calibration E2E/integration suites — ~48 tests; the headline test count drops accordingly, expected). The `AppConfig` doc-comment states rollback = `git revert 3131387`. **Keep** the engine-consumed constants (`VestibularTriggerPressureOverride = 50`, `VestibularPersonalPressureScaleCap`) — backlog C1 (extract/pin the literal) still applies to them. **Sequencing guard:** the vestibular-guard tests that ride `ApplyCalibrationResult` (TEST-09 / `AdaptiveFitWiringTests:148-160`) must be ported onto the live `SetVestibularProtection` path **in the same commit**, so vestibular coverage never drops, even transiently.
- **DEAD-04 (decided 2026-06-11 — D-14: delete):** remove the `VestibularWarningShown` field (JSON reads stay roll-forward tolerant so old prefs files load), its `UserPreferencesTests` rows, and **every doc reference including the Toggle-B dialog text** (`POPUP-OVERLAY.md §4` / line 145). Close backlog H45 as moot for this field. The live vestibular flow (`VestibularPromptAnswered` + profile-store `VestibularProtectionEnabled`/`MedicalAdvisoryShown`) is unaffected.
- **DEAD-09:** structural removal of **unread declarations** is permitted under the SenseDefaults lock (no value changes). Migrate the lone `HabituationMaxOffset` test assertion to `HabituationProfileParams.Resilient`; leave a tombstone comment.
- **DEAD-10 (one mechanical commit, do early):** the false "default OFF" comments on `MainViewModel.cs:693-703`, `SenseDefaults.cs:178`, `SelectorWeights.cs` are exactly the misdirection that caused the earlier flag-ON-but-inert audit miss. Includes NE1 (LOGIC-09/INTENT-07), `AppConfig.cs:38`, `UserPreferences.cs:57`, and three comments naming deleted classes.
- **DEAD-11:** delete `SpikePopupWindow` + the legacy branch (`MainWindow.xaml.cs:146-186`) + `PopupSurfaceGate` legacy rows; keep reading the JSON field tolerantly so old prefs load.
- **DEAD-13:** create `docs/archive/`; move `ARCHITECTURE.md`, root `HANDOVER.md`, `Habituation plan.md` (128 KB at root), `M5_IMPLEMENTATION_PLAN.md` with "superseded by X on DATE" banners; backfill CHANGELOG v0.11→v0.11.3; fix `scripts/README.txt` CUDA line (also PKG-02).
- **DEAD-14:** wrap the Ctrl+Shift+H/F chords in a diagnostics gate (`#if DEBUG` or env var like the bar-validation stub); at minimum mark the forced-fault visibly "simulated" — it currently undermines the liveness honesty rule. Document all three in `DEBUGGING.md`.
- **DEAD-15:** `git rm --cached .claude/settings.local.json` + gitignore; move head-tracking sprint scratch out of `baseline/` so the M1 calibration CSVs stand alone; delete the gitignored root scratch (screenshots, stack dumps, 0-byte `soak_report.json` — see EXIT-06/SIDE-08).

### Packaging & lifecycle remainder (PKG-)

*PKG-01/02/03 → CW-6.*

| ID | Sev | Effort | Title |
|----|-----|--------|-------|
| PKG-04 | P2 | M | Upgrade/uninstall over the running tray app unguarded (no `AppMutex`; close-to-tray swallows close; windowless children hold files) |
| PKG-05 | P3* | S | No `[InstallDelete]` — forward hygiene for payload-shape convergence (*verifier downgraded: no pre-trim installer ever shipped) |
| PKG-06 | P2 | M | Integrity manifest covers the wrong files: loaded DLLs/`_internal` unhashed; hashed GGUF/ggml entries never verified |
| PKG-07 | P2 | S | H25 data-delete prompt defaults Yes — `/VERYSILENT /SUPPRESSMSGBOXES` auto-wipes all user data incl. the Q4.5-sacred offset |
| PKG-08 | P3 | S | Payload hygiene: empty `samples/` ships; dead LLamaSharp.dll; assemblies stamped 1.0.0.0 vs installer 0.11.3 |
| PKG-09 | P3 | S | `%TEMP%` dev-trace residue not covered by uninstall; handbooks zip a retired `%TEMP%` path |
| PKG-10 | P3 | M | WiX plan: managed-BA .NET prerequisite must be a Phase-0 locked decision; installer zh-Hant parity |

Key notes:
- **PKG-04:** add `AppMutex=Local\SenseEase.App.SingleInstance` to `[Setup]` (Inno checks it in Setup **and** uninstaller); ensure tray-quit terminates llama-server/head_tracker; check `DelTree`'s return and warn on partial wipe.
- **PKG-06:** extend manifest patterns to `llama*.dll`, `mtmd.dll`, `libomp*.dll`, the PyInstaller `_internal/` tree, and `face_landmarker.task` (see SIDE-10); add a startup sweep verifying manifest entries (hash `_internal` once at startup, not per-spawn). Until then the H32 "tampered binary never reaches Process.Start" promise holds only for the two exe stubs.
- **PKG-07:** in `CurUninstallStepChanged`, skip the prompt+DelTree entirely when `UninstallSilent` (data preserved by default in silent mode; optional explicit `/DELETEDATA=1` switch). Update the WiX plan §7 to rely on that guarantee. Interactive default-Yes is a locked product decision — keep it for interactive flows.
- **PKG-08:** add `Version`/`FileVersion` from the git tag to `Directory.Build.props` so crash reports correlate to releases; drop the empty `samples/` dir; DEAD-01 removes LLamaSharp.dll.

### Sidecar remainder (SIDE-)

*SIDE-01/02/03/04 → CW-10.*

| ID | Sev | Effort | Title |
|----|-----|--------|-------|
| SIDE-05 | P3 | S | `ChildProcessReaper` failure paths fully silent — webcam-light-stays-on would be undiagnosable |
| SIDE-06 | P3 | S | `TryParseSampleLine` doc claims validation that doesn't exist; `{}` parses to corner-pinned (0,0) |
| SIDE-07 | P3 | S | Webcam-open failure falls through to file-path interpretation — misleading error; no index probe |
| SIDE-08 | P3 | S | Soak evidence hygiene: 0-byte archival reports; UTF-16 live report; 14.988 Hz passed a ≥15 Hz rule as "≈15" |
| SIDE-09 | P3 | M | Python tests cover only the EMA — twice-flip-flopped pose-sign math and jump filter untested, no CI hook |
| SIDE-10 | P3 | S | PyInstaller rthook re-creates the H12 model-hijack channel for an exe-adjacent `face_landmarker.task` |

Key notes: SIDE-05 — check both Win32 returns, log once via `WgcDiagLog`. SIDE-06 — nullable `t_ms/cx/cy` + reject-on-null + clamp to [0,1]; today's contradiction between doc and code must be resolved one way. SIDE-09 — synthetic rotation-matrix tests pinning yaw/pitch signs end-to-end (the sign conventions shipped wrong **twice**), plus the jump-filter sequence test; wire into the release checklist. SIDE-10 — hash the `.task` into the H32 manifest or drop the rthook from release builds; fix the `head_tracker.spec` header comment (PyInstaller 6 puts data in `_internal/`).

### Shutdown remainder (EXIT-)

*EXIT-01 → CW-4 · EXIT-02/03/04/05 → CW-11.*

- **EXIT-06 (P3, S, carried).** Root crash artifacts' recorded root causes are both fixed at HEAD (LLamaSharp AV-exception path deleted; genstuck lived inside llama-server, now bounded 60 s/512 KB). Remaining: copy the H34 pattern to `LlamaServerCalibrationClient` (≡ SEC-07); delete/archive the 8 gitignored root artifacts (`stack_dump*.txt`, `app.std*.log`, `fresh_std*.log`, `verify.log`) — they invite mis-triage and accidental log-bundle inclusion; NE6 is mostly done (cancellation + 60 s ceiling exist) — only a visible cancel button for in-flight turns remains.

---

## 10. Duplicate / cross-reference map

One fix closes every ID in the row.

| Canonical | Duplicates / direct dependents |
|---|---|
| LOGIC-01 | INTENT-01 (FEAT-02 coupled — resolved by D-1) |
| FEAT-03 | INTENT-02, TEST-04 (test), CW-2 |
| FEAT-04 | UX-01 |
| FEAT-01 | EXIT-01, TEST-07 (test) |
| UX-12 | FEAT-15 |
| FEAT-05 | LOGIC-10 (carried NE2) |
| FEAT-09 | LOGIC-07, SEC-04 |
| EventLogEntry clock (CW-12) | UX-15, FEAT-10, LOGIC-04, SEC-05 (+ LOGIC-11) |
| FEAT-07 | DEAD-02 |
| FEAT-13 | DEAD-07 (UX-09 coupled) |
| FEAT-11 | DEAD-03 |
| LOGIC-09 | INTENT-07, ⊂ DEAD-10 |
| LOGIC-12 | SEC-09 |
| FEAT-06 | ⊂ UX-20 |
| DEAD-01 | ⊂ PKG-08 |
| SEC-07 | ⊂ EXIT-06 |

---

## 11. Prioritized roadmap

### Wave B0 — before the Alpha tester cut (all S/M; ~3–5 working days)
1. **CW-1** narrow habituation reset + pause-preservation tests (clinical lock).
2. **CW-6** PKG-01 Run-key fix + `IsEnabled` empty-value heal; PKG-02 README rewrite; PKG-03 `.sha256` per build.
3. **CW-5** crisis-text legibility on the card (+ UVHS).
4. **CW-4** UM4 kickoff move + retry + seam test.
5. **CW-3** honest profile copy: canned reply, system-prompt EXAMPLE 2, lexicon; delete dead picker.
6. **CW-2 interim:** stop confirming inert mask/feather changes + fix the `ShaderParams.cs:11` comment (full wiring lands with A2 in B2 — D-2).
7. **CW-9** `DataRootOverride` seam + real H2 boundary test (also de-risks Kirby's own data during development).
8. **DEAD-10** the 8-comment mechanical commit (kills the audit-misdirection class).
9. **TEST-10** literal-pin the five locked constants.
10. **SIDE-01** jump-filter unlatch (C4 unlock granted — D-11) — the freeze-at-screen-edge percept is tester-facing.
11. **CW-1 Settings pause/resume toggle** (decided — D-1): implement in B0 if time allows, otherwise first thing in B1.

### Wave B1 — Beta-1 (correctness & honesty)
- **CW-7** persistence family (overlay baseline, shader replay, atomic behavior log).
- **CW-8** liveness bundle (fault UX, tray clobber guard, `Closed` subscription, 5 s constant or doc amendment, resume watchdog).
- **CW-10** sidecar resilience (camera-loss retry, scheduler resync, host-side FaceDetected grace, health surfacing) — C4 unlock granted (D-11).
- **CW-11** shutdown bundle (early hooks, InvokeAsync, central dispose incl. both sensing providers + Mistral).
- **CW-12** clock unification commit.
- **UX P2 block:** UX-04…UX-11, UX-13, UX-14 (i18n completeness, typography/contrast floors, first-run card, card keyboard path, clear-all honesty).
- **SEC P2 block:** SEC-01 PathRedactor, SEC-02 collect-logs.ps1, SEC-03 scope doc, log-cap sentinel (FEAT-09).
- **PKG P2 block:** PKG-04 AppMutex, PKG-06 manifest scope, PKG-07 silent-uninstall guard.
- **LOGIC P2 block:** LOGIC-02 Start lock, LOGIC-03 IO-off-dispatcher, LOGIC-05 0.25-attenuation + test (D-4), INTENT-06 Advanced-expander surfacing (D-6, pairs with UX-18), FEAT-07 flag-off = TYPICAL/L5-seed semantics + pin test + doc updates (D-8) — same commit executes D-13: delete `ApplyCalibrationResult`/`Process` + their ~48 tests, porting the vestibular-guard tests to the live path (TEST-09).
- **Test hardening:** TEST-05/06/09/11/14.
- **Overlay motion coherence A1** (one-clock motion model) per `docs/BETA_FEATHERING_COHERENCE_PLAN.md` — first Beta increment; UVHS L1–L12 head sweep.

### Wave B2 — Beta-2 (debt, polish, the full cure)
- **Overlay A2** single-pass field (gate: 165 Hz perf spike; `ps_2_0` ShaderEffect vs WriteableBitmap decision) — **completes the CW-2 wiring (D-2): MaskDepth/FeatherWidth become field inputs**, with TEST-04's renderer round-trip test in the same commit.
- **Dead-code sweeps:** DEAD-01/03/04/05/06/08/09/11/12/14/15, FEAT-11/12/14, UX-16…UX-21 — including the decided retirements/wirings: **D-9** (Demo/Cursor modes + both providers; closes LOGIC-11 and the cursor half of LOGIC-14), **D-10** (tray-colour flag + the entire escape hatch, with handbook M5.1-B and `HARD_CONSTRAINTS.md` A3 doc updates; supersedes FEAT-06), **D-14** (delete `VestibularWarningShown` + Toggle-B doc refs; closes H45-for-field), **D-15** (wire `ResetToDefaults` into Settings with confirm).
- **Doc archive:** DEAD-13 + CHANGELOG backfill + INTENT-09/D-12 locked-doc amendment + SEC-12/SIDE-08 hygiene.
- **Deeper residuals:** LOGIC-12/SEC-09 (fold into H44/H45 tamper wave), SEC-11, FEAT-16 (selector data source — coordinate with M1 follow-up), LOGIC-13 L5-seed fast-release exemption + test (D-5), INTENT-08 regret-off advisory clear + parity test (D-7), INTENT-03 residual starvation heuristic, SIDE-06/07/09/10, PKG-05/08/09/10, TEST-12/13/15.

### Continuous / external-gated
- **SEC-10:** Authenticode cert (start procurement immediately — longest lead), llama.cpp rebuild, opencv bump, PyInstaller pin → regenerate manifest + hashes.
- **Carried open backlog (not re-litigated here):** D1 upper-band OFI capture + normFactor review, D2 ratio-magnitude test, D3 convergence tests, A7 full per-profile shader defaults, M1 capture runbook, UVHS confirmation for feathering P1 (still pending per sprint memory).

---

## 12. Constraints this plan honors (verified against `docs/design/HARD_CONSTRAINTS.md` + whitepaper locks)

No recommendation above changes: any `SenseDefaults` **value** (D-4 *adds* one constant, changes none) · the `VimsLevels` 12-tier depth table, 0.92 peak, ≥0.20 floor · the grid world-lock or `GridCeiling` curve · Core/Abstractions public API · the AppId · OBS-3's 8-scalar DTO · `head_tracker.py`'s locked EWMA/dead-zone/face-presence trio (the scoped unlock for the three CW-10 items was granted 2026-06-11 — D-11; the trio itself stays untouched) · Q4.5 (`_offset` is never cleared by anything proposed here — CW-1 strengthens it) · paused-by-default habituation, reactive-only chatbot, `HeadTrackingEnabled=false`, raw-trigger asymmetry (all treated as by-design).

All product decisions are **ratified 2026-06-11** (§1.5, D-1…D-15 — the three former micro-decisions were closed in the second round: D-13 delete the calibration plumbing + tests with the D-8 commit, D-14 delete `VestibularWarningShown` + doc refs, D-15 wire `ResetToDefaults` to Settings with confirm). **Zero open decision points remain in this plan.**

## 13. Method & evidence appendix

- Sweep run `wf_e365b1fc-df3` (2026-06-10): 44 agents, 1,399 tool uses, ~63 min. Doc phase read 40+ docs across 4 clusters; sweepers read the hot files **in full** (`MockInferenceEngine` 1,913 lines, `MainViewModel` 1,682 lines, all 14 App XAML files, `head_tracker.py`, the `.iss`, all storage stores); verifiers re-opened every citation and checked `git log --since=2026-06-01` per file.
- Raw per-dimension digests (including each finding's verbatim verifier verdict and corrections) are in `.claude/sweep-digest/` (untracked scratch; safe to delete after triage).
- **Verified-fixed since 2026-06-05 (do not re-open):** EM1–EM7 · NE3/NE4/NE7/NE10–NE16/NE20/NE22/NE25/NE27/NE30 · H1–H10/H12/H13/H16/H19/H24/H25/H28–H31/H34(Mistral)/H35–H40/H43/H44(partial)/H46/H49/H51 · W1W2 #1–#7/#9/#10/#13 · the banned-hex/ComfortMeter/ThemedDialog UI items. Still-open carried items are marked `carried` inline above.
- Coverage limits (honest gaps): `OverlayWindow` rendering internals beyond the head-pose/feather path were read but not exhaustively swept; the llama-server binary itself was not analyzed; no builds or tests were executed (AV constraint — static analysis only); real-OS logoff timing and installer-EXE contents were not exercised.

---

## 14. Post-decision coherence check (2026-06-11)

A full re-walk of the plan after folding in D-1…D-12.

**Cascades applied and consistent.**
- **D-9** (retire Cursor/Demo) makes **LOGIC-11 moot** (the local-time stamp dies with `MockSensingProvider`) and closes the cursor half of **LOGIC-14**; CW-12's fix now carries the conditional. If the DEAD-03 deletion happens to slip past the clock-unification commit, the two one-line interim fixes still apply — the ordering is safe either way.
- **D-10** (retire the escape hatch) **cancels FEAT-06** — the global-hotkey work is not deferred, it is not built. New obligations created and recorded at UX-20: handbook M5.1-B section removal (en + zh), `HARD_CONSTRAINTS.md` A3 note (the invariant constrained the hatch *if present*; full removal is the conservative direction — the mask is never weakened, and the vestibular force-disable becomes vacuous), deletion of the hidden hotkey panel (`SettingsContent.xaml:515`), and tolerant JSON reads for old prefs files.
- **D-4** introduces exactly one **new additive constant** (`CursorMoveOfiAttenuation = 0.25`). Additive is permitted under the SenseDefaults lock (no existing value changes); it intentionally mirrors the Driver-attenuation magnitude but stays a separate named constant so the two semantics can diverge later.
- **D-8** is a **small code change, not just docs**: the TYPICAL/L5 seeding must run (or be preserved) under flag-off, otherwise the newly-defined semantics are false on day one. FEAT-07's fix text now says so explicitly.
- **D-2** moves TEST-04's renderer round-trip test into the A2 wiring commit; the B0 interim keeps the honesty property until then.
- **D-3** closes UX-01 via the honest-copy rewrite; CW-3's cleanup list and DEAD-08/UX-18 no longer double-count the `ProfilePill`/picker debris (one commit, three IDs).

**Test obligations created by the decisions** (collected so none is lost): pause-preservation across selector moves (CW-1) · L5-seed stable-window behavior (D-5) · vestibular regret parity both directions (D-7) · flag-off TYPICAL/L5 seed pin (D-8) · renderer round-trip for MaskDepth/FeatherWidth (D-2/TEST-04, lands with A2) · 0.25-attenuation pressure-accumulation test (D-4). None conflicts with an existing pinned test; the D6 header fix (D-12) and TEST-05 are the same edit.

**Documentation obligations created by the decisions** (fold into the implementing commits): `SAFETY-PROVISIONS.md` (D-1 Settings toggle as sole opt-in path; D-7 §3.1 advisory rule) · `ALPHA2_CORE_ARCHITECTURE.md` + the `AppConfig` doc-comment (D-8) · whitepaper sensing-fallback framing (D-9) · `LOCKED_DIRECTION_ALPHA1.md` (D-12, verbatim override record) · `HANDBOOK_en/zh` (D-10 escape-hatch removal — same pass as the PKG-02 rewrite) · `HARD_CONSTRAINTS.md` A3 (D-10) · Alpha-2 addendum docs (D-5 onboarding-protection note).

**Lock audit.** No decision touches a locked value, the 12-tier depth table, Q4.5, Q4.6, OBS-3, the AppId, the grid world-lock, or the never-initiate contract. D-11 is a **scoped** unlock of three named sidecar functions — the C4 trio (EWMA smoothing, dead-zone gating, face-presence logic) remains locked and untouched. D-10's feature removal makes A3 vacuous rather than violated.

**Roadmap integrity.** Every P1 retains a wave (B0 items 1–10 unchanged; item 11 became "implement the decided D-1 toggle"). FEAT-06 leaves the work plan entirely (cancelled by D-10). INTENT-08 — previously unscheduled — is now explicitly in B2 residuals. Cross-check of all 127 IDs: each maps to a wave, a cancellation (FEAT-06), or a cascade-closure (UX-01, LOGIC-11, the cursor half of LOGIC-14); no orphans.

**Second ratification round (2026-06-11) — D-13/D-14/D-15 cascades.**
- **D-13** deletes ~48 calibration tests together with the plumbing. Two guards: (1) the vestibular-guard tests that ride `ApplyCalibrationResult` (TEST-09) are ported to the live `SetVestibularProtection` path **in the same commit** — vestibular coverage must never drop, even transiently; (2) the engine-consumed constants (`VestibularTriggerPressureOverride = 50`, `VestibularPersonalPressureScaleCap`) stay, and backlog **C1** (extract/pin the literal) still applies to them. TEST-15's dead-surface labeling is superseded (delete beats label). The headline test count drops by ~48 — expected, not a regression; note it in the commit message.
- **D-14 and D-15 interact by design:** D-15's "confirm clears `VestibularWarningShown`" clause comes from the original contract comment and is **transitional** — once D-14's deletion lands the clause is vacuous and the confirm copy must not mention the field. Implementation order is free; the end state is identical (field gone, reset wired, confirm enumerates theme/language/toggles/HUD position only).
- **D-14** closes backlog **H45 as moot** for this field; `POPUP-OVERLAY.md §4`'s Toggle-B dialog text is removed in the same change. JSON reads stay roll-forward tolerant so pre-deletion prefs files still load.

**Final status: all 15 decision points closed — zero open decisions remain in this plan.** Every finding now maps to a wave with decided semantics, a cancellation (FEAT-06), or a cascade-closure (UX-01, LOGIC-11, the cursor half of LOGIC-14, TEST-15's label half).
