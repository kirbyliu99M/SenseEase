# Resolution — Liveness / Trust Contract

> Generated 2026-06-05 by the senseease-rebrand-refine workflow (8 agents, adversarially reviewed). Draft for review — no code changed, nothing committed.

This confirms the critical adversarial gap precisely. The OnTick fatal-catch at line 579-583 calls `Stop()` — which silently kills the timer with **no callback to the UI**. `OpticalFlowSampled` simply stops firing, `StateChanged` goes quiet, and the bar/tray/mask freeze at their last value. To an anxious user this is identical to a calm screen. And `_lastFrameProcessedAt` is stamped but only ever read by the one-shot Resume watchdog — there is no continuous liveness check during normal running.

I have everything I need. Here is the decision-grade section.

---

## Quiet-Guardian Liveness / Trust Contract

**Status:** Proposed for lock. Respects all four LOCKED PRODUCT DECISIONS (Quiet Guardian posture, day-1 protection default, mask-only motion policy, Just-Talk deferred). Two items are flagged as behavior changes requiring product-owner sign-off; everything else is additive UI/diagnostic wiring with zero engine/persistence impact.

### 0. The adversarial gap, confirmed in code

Silent failure is not a hypothetical here — it is the current default behavior:

- **Engine fault is invisible.** `WgcSensingProvider.OnTick` wraps the whole capture path in a try/catch whose handler is `WgcLog(...); Stop();` (`WgcSensingProvider.cs:579-583`). `Stop()` kills the capture `Timer`. After that, `OpticalFlowSampled` never fires again, so `MainViewModel.StateChanged` goes silent, and `PersistentBarWindow.SetTier` / `TrayIconService.ApplyState` / `OverlayWindow.ApplyState` are simply **never called again**. The bar, tray tint, and mask all *freeze at their last value*. A frozen tier-1 grey bar over a calm screen is pixel-identical to a healthy tier-1 grey bar. The user has zero signal.
- **No continuous sensing watchdog exists.** The only WGC watchdog (`ScheduleResumeWatchdog`, `WgcSensingProvider.cs:259-293`) is a **one-shot, Resume-only** timer. `_lastFrameProcessedAt` (`:187`, stamped at `:524`) is read *only* by that resume watchdog (`:271`). During normal running, a stuck capture session (lock-screen recovery, monitor sleep, RDP, GPU TDR) updates nothing and trips nothing.
- **Sidecar fault is correctly silent — and that's the bug for trust.** `SidecarHeadPoseReader.ReadLatest()` returns `null` on dead/stale sidecar (`:269-284`), and consumers fall back to centered mask `(0.5,0.5)`. This is *correct* for the mask, but it means "head-tracking died" looks exactly like "head-tracking never on." No surface reflects it.

So "is it even working?" — itself a documented strain trigger — currently has **no honest answer anywhere in the UI**. This contract fixes that without adding nag or motion.

### 1. The four explicit liveness states

There is exactly one comfort scalar (VIMS tier 1–10) and now exactly one **liveness scalar**, a 4-value enum, orthogonal to tier. Both are forward-only display knobs.

| State | Meaning | Entry condition (signals in §4) | Ambient bar | Tray tint | Self-check verdict |
|---|---|---|---|---|---|
| **Protecting** | Sensing live AND mask actively dimming (`MaskActive` true / tier ≥ 2 above floor) | Fresh frames + `state.MaskActive` | Tier brush (existing `TierColorPalette.BrushForTier`) | Tier band icon (existing) | "Protecting you now" |
| **Steady-watching** | Sensing live, screen calm, mask at baseline floor (tier-1 / 0% alpha) | Fresh frames + not mask-active | Faint grey floor brush (existing tier-1 `GreyBrush`, 0x26) | Grey band icon | "Watching — all calm" |
| **Paused-by-user** | User chose Power Save; sensing intentionally off | `_powerSaveActiveLookup()` true | **`EnterSleepMode()`** dashed outline (exists, `PersistentBarWindow.xaml.cs:228`) | `SetPowerSaveIndicator(true)` (exists, `TrayIconService.cs:313`) | "Paused by you" |
| **Faulted** | Sensing/sidecar/engine died unexpectedly | Liveness watchdog trip (§2) | New **static** faulted treatment (§2) | New faulted tooltip + grey icon | "Couldn't reach your screen — tap to retry" |

Crucially **Protecting** and **Steady-watching** are *both healthy* and are distinguished only by the existing tier color — no new visual. The only genuinely new visual is **Faulted**, and it must be *visibly, statically different from a frozen healthy bar* — that is the entire point.

Key invariant: **Paused-by-user and Faulted must never be confusable.** Paused is calm-by-choice (dashed outline = "I turned this off"); Faulted is calm-by-accident (must read as "something needs a tap"). The dashed `SleepOverlay` is already reserved for Paused, so Faulted needs its own token (below), not a reuse.

### 2. Surfacing a fault reassuringly, not alarmingly

**The faulted visual (token-driven, motion-free).** Add one Calm token and one method, mirroring the existing `EnterSleepMode/ExitSleepMode` idempotent-snapshot pattern (`PersistentBarWindow.xaml.cs:228-247`):

- New token `FaultedBarBrush` = the muted clay `#B65C4E` from the DESIGN NORTH STAR at **low alpha (≈0x40)** — "needs attention," explicitly *never alarm red*, consistent with the locked palette's "muted clay for high strain, never alarm red." Add to `TierColorPalette` as a frozen brush so it shares the single color source; do **not** introduce a new hex literal in XAML.
- New `PersistentBarWindow.EnterFaultedMode()` / `ExitFaultedMode()`: same `_preSleepBackground`-style snapshot guard, sets `BarFill.Background = FaultedBarBrush` and shows a **second** reserved rectangle (`FaultOutline`, solid 1px clay, *not* dashed — dashed is Paused's identity). No animation, no pulse. It is a static, slightly warmer, slightly more-present bar. That static-but-different quality is exactly what breaks the "frozen looks fine" trap.
- Tray: reuse the existing status-label snapshot mechanism. Add `TrayIconService.SetFaultIndicator(bool)` mirroring `SetPowerSaveIndicator` (`:313`) — status header becomes `"暫時無法偵測畫面 / Sensing paused — tap to retry"`, tooltip mirrors it. Icon stays grey (never flips to a scary color).
- **Reassurance copy, not error copy.** Self-check and tray say *"Couldn't reach your screen for a moment — tap to retry"*, never "ERROR" / "FAILED" / a red badge. The app owns the failure calmly. This is the Quiet Guardian admitting a gap without transferring anxiety.

**Auto-heal first, surface second.** A fault should attempt one silent self-heal before it is ever shown, so transient hiccups (monitor wake, brief TDR) never reach the user:

- Add a **continuous** sensing-liveness watchdog in `WgcSensingProvider` modeled on `ScheduleResumeWatchdog` but rearming: a `Timer` that checks `(DateTime.UtcNow - _lastFrameProcessedAt) > StalledThresholdMs` (propose 3000 ms — comfortably above the 15 Hz ≈ 66 ms cadence and the 2 s warm-up). On first trip it does the same `Stop(); Start();` self-heal the resume watchdog already does (`:276-278`), logs `"Sensing stalled → re-init"`, and raises a **new `SensingHealthChanged(bool healthy)` event**. Only if re-init does not restore frames within the next window does `healthy=false` propagate to the Faulted visual. **[BEHAVIOR CHANGE — needs sign-off: adds an always-on timer + one event to the sensing provider. Minimal: ~25 lines, reuses the existing watchdog shape, no change to the OFI/tier math, no persistence.]**
- Wire the existing fatal path: in the `OnTick` catch (`:579-583`), after `Stop()`, raise `SensingHealthChanged(false)` instead of dying silently. This is the highest-value single line in the whole contract.

**Engine exception coverage.** `MainViewModel` already owns the `IInferenceEngine`. Wrap the per-tick `engine.Update(...)` call site in a guard that, on exception, sets liveness=`Faulted` and raises through the same event rather than letting the dispatcher swallow it. (Read-only consumer; no engine-internal change — respects "engine + snapshot types not modified.")

### 3. The one-tap "Am I protected?" affordance

Two honest, instant confirmations, both already half-wired:

1. **Tray hover (zero-tap, the primary ambient check).** `TrayIconService` already maintains `_tray.ToolTipText` and refreshes it on every state push (`RefreshLabels`, `:282`). Extend the tooltip to lead with the **liveness verdict**, then tier:
   - Healthy active: `"SenseEase — 正在保護 Protecting · Tier 5 · 中度"`
   - Healthy calm: `"SenseEase — 守護中 Watching · all calm"`
   - Paused: `"SenseEase — 已暫停 Paused by you"`
   - Faulted: `"SenseEase — 暫時無法偵測 · 點一下重試 tap to retry"`
   This is the cheapest honest answer in the product: hover the tray, read one line. It is *live* because it's driven by the same `StateChanged`/`SensingHealthChanged` stream that drives the visuals — it cannot lie about a frozen pipeline because a frozen pipeline flips it to Faulted.

2. **Bar click → notification Card "self-check" line (the deliberate one-tap).** The bar already raises `Clicked` (`PersistentBarWindow.xaml.cs:75,197`) and `MainWindow` already opens the Card on click (`StartPersistentBar`, bar.Clicked → `ShowNotificationCardForCurrentTier`). Add a single **at-a-glance self-check row** at the top of that Card showing the four signals as plain-language checkmarks (no telemetry numbers — those stay demoted to the Advanced/Clinician view per LOCKED decision 1):
   - `✓ 畫面感測 Screen sensing — live (last frame 0.1s ago)`
   - `✓ 保護圖層 Calm field — on` (overlay window visible)
   - `— 頭部追蹤 Head-tracking — off` (or `✓ live` / `✗ paused`)
   - One-line verdict mirroring the tray.
   The freshness number (`now - _lastFrameProcessedAt`) is the honest proof: if sensing is dead, that number visibly climbs and the row flips to `✗ reconnecting…`. A frozen-but-fine bar cannot fake a 0.1s-fresh frame timestamp.

   The Card already supports a tier-spike auto-show path; the self-check row appears in the *same* Card regardless of how it was opened, so the click affordance and the spike notification converge on one honest surface.

**No new top-level window, no nag, no toast.** Both affordances are *pull* (user hovers / clicks), never *push*. The only thing that changes without user action is the ambient bar/tray treatment on a genuine sustained fault — which is the minimum honesty the trust contract requires.

### 4. Exact signals to bind

| Liveness fact | Authoritative signal | File:line |
|---|---|---|
| Sensing frames fresh | `now - _lastFrameProcessedAt < StalledThresholdMs` | `WgcSensingProvider.cs:187,524` |
| Sensing healthy (post self-heal) | new `SensingHealthChanged(bool)` event | `WgcSensingProvider.cs` (OnTick catch `:582`, new watchdog) |
| Mask actively protecting vs baseline | `SenseState.MaskActive` | `SenseState.cs:33` |
| Effective tier (with floor) | `MainViewModel.ResolveDisplayedTier(...)` | `MainWindow.xaml.cs:1217` |
| Paused by user | `_powerSaveActiveLookup()` | `MainViewModel.cs:57` |
| Head-tracking live vs dead/stale | `SidecarHeadPoseReader.ReadLatest() != null` → surfaced as `SenseState.HeadTrackingActive` | `SidecarHeadPoseReader.cs:269`; `SenseState.cs:91` |
| Engine alive | last `StateChanged` timestamp / `SenseState.LastUpdatedAt` | `SenseState.cs:67` |

**Single derivation point.** Compute the liveness enum in **one** place — `MainViewModel`, alongside the existing `StateChanged` raise — and expose it as a new `LivenessState` property + `LivenessChanged` event, exactly paralleling how `VimsTier` is the single comfort scalar fanned out to bar+tray+mask. Bar, tray, and Card all *read* it; none re-derive it. This guarantees the three surfaces "move in lock-step" the same way `TierColorPalette` guarantees color lock-step. **[BEHAVIOR CHANGE — minor: adds one VM property + event, read-only over existing signals; needs sign-off only because it's new public VM surface that `MainWindowSmokeTests` must keep tolerating — keep it default `Steady-watching` so the bare-Application smoke path is unaffected.]**

### 5. The liveness-contract test (so working ≠ broken can never look the same)

A focused test class `LivenessContractTests`. The load-bearing assertion is **distinguishability**: a faulted render must not equal a healthy render on any bound surface.

1. **Fault ≠ healthy on the bar (the anti-freeze test).** Construct `PersistentBarWindow`, `SetTier(1)` (healthy calm grey), snapshot `BarFill.Background`. Call `EnterFaultedMode()`, snapshot again. **Assert the two brushes differ** (color and/or the `FaultOutline` visibility). This is the test that makes "frozen healthy bar" and "faulted bar" provably non-identical — the exact gap §0 identified. Symmetric `ExitFaultedMode()` restores the snapshot (idempotency, mirroring the existing sleep-mode tests).

2. **Paused ≠ Faulted.** Assert `EnterSleepMode()` (dashed `SleepOverlay`) and `EnterFaultedMode()` (solid `FaultOutline` + clay fill) produce distinct visual states — no token overlap. Guards against the "calm-by-choice vs calm-by-accident" confusion.

3. **Stall trips Faulted within threshold.** Drive a fake `ISensingProvider` whose `_lastFrameProcessedAt` is frozen; advance injected time past `StalledThresholdMs`; assert `SensingHealthChanged(false)` fires and `MainViewModel.LivenessState == Faulted`. (Time is injectable — the spike detector already uses this `nowUtc`-injection pattern, `PressureSpikeDetector.cs:36`.)

4. **OnTick fatal surfaces a fault.** Force the `OnTick` catch path; assert `SensingHealthChanged(false)` fires (regression lock on the `:582` one-liner — proves `Stop()` is no longer silent).

5. **Sidecar death → head-tracking row flips, mask unaffected.** Use the existing `TestHook_SimulateSidecarExit()` (`SidecarHeadPoseReader.cs:721`); assert `ReadLatest()==null`, `HeadTrackingActive==false`, the self-check head-tracking row reads `✗`, AND the FOV mask center stays `(0.5,0.5)` (protection intact). Locks "sidecar fault is surfaced, but never weakens the mask."

6. **Self-check honesty.** Assert the Card's freshness string is computed from the real `_lastFrameProcessedAt` delta, not a static literal — a frozen pipeline must make the number climb. (Bind-source assertion, not a sleep.)

7. **Smoke-compat.** `MainWindowSmokeTests` still constructs; `LivenessState` defaults to `Steady-watching` under the bare `new Application()` path (App.Resources empty by design). No Calm token loads at App level — `FaultedBarBrush` lives in `TierColorPalette` code (frozen brush), not App.Resources, consistent with the merge-view-locally rule.

### 6. What this explicitly does NOT do (guardrail compliance)

- **No motion.** Faulted is a *static* color/outline change. No pulse, no breathing, no fade loop. Only the locked mask cross-fade (`MaskSmoothingFactor=0.25`) and the existing 100 ms hover grow remain. Mask-only policy held.
- **No nag.** Zero toasts, zero modal, zero auto-opening window. Faulted changes ambient appearance only; all confirmation is pull (hover/click). Self-heal runs once silently before anything is shown.
- **No telemetry promotion.** The self-check shows plain-language checkmarks + a freshness age, never OFI/Pressure/Integrator/FMS/radius numbers — those stay demoted behind Advanced/Clinician (LOCKED decision 1). `MainWindow` stays instantiable.
- **No habituation impact.** Nothing here calls `_habituation.*`, touches `_offset`, or routes through `ApplyUserFeedback` (`MockInferenceEngine.cs:292-311`). Liveness is orthogonal to comfort and to weeks-of-progress. Q4.5 invariant untouched.
- **No persistence change, no default flip.** Independent of the `_persistentOverlayEnabled` day-1 default (`MainViewModel.cs:794`); whichever way that lands, the liveness contract reports the truth of whatever protection is configured.

### 7. Recommended final design (one paragraph)

Adopt a single 4-value **liveness scalar** derived once in `MainViewModel` (Protecting / Steady-watching / Paused-by-user / Faulted) and fanned out in lock-step to the three existing ambient surfaces exactly as `VimsTier` already is. Healthy states reuse current tier color (no new visual); Paused reuses the shipped dashed `SleepMode`; **Faulted** gets one new low-alpha clay token (`FaultedBarBrush`, `#B65C4E`@0x40) plus a solid (non-dashed) `FaultOutline`, surfaced only after one silent `Stop();Start()` self-heal fails. Honest confirmation is pull-only: a live tray-hover tooltip leading with the verdict, and a bar-click Card self-check row showing plain-language signal checks plus a real frame-freshness age. The two flagged behavior changes — a continuous rearming sensing watchdog + `SensingHealthChanged` event in `WgcSensingProvider`, and the new read-only `LivenessState`/`LivenessChanged` VM surface — are minimal, additive, and the only way to make a dead pipeline stop looking alive. The `LivenessContractTests` distinguishability assertions (fault-brush ≠ healthy-brush; paused ≠ faulted; OnTick-fatal raises a fault) are the mechanical guarantee that "working" and "broken" can never again render identically.

### 8. Accepted residual gap — non-throwing frame starvation on an idle screen (Wave B1a, 2026-06-11)

The continuous liveness watchdog (`OnLivenessWatchdog`) keys off **loop liveness** (`_lastPollAt`, stamped every tick), NOT new-frame arrival. This is a deliberate anti-flap trade-off: WGC delivers **no new frames** on an idle/static screen (also monitor-sleep, lock-screen, RDP) — which is *healthy*, not a stall — so a frame-arrival watchdog would Stop()/Start() the capture every few seconds on a quiet desktop ("capture flaps on/off"). The cost of that trade-off is a genuine **residual gap**: a *non-throwing* frame starvation — the capture loop keeps polling fine (so `_lastPollAt` stays fresh and the watchdog is satisfied) but the framepool silently never yields another frame, with no exception thrown and no item-close raised — is **undetectable by design** on an idle screen, because it is pixel-indistinguishable from a legitimately static screen.

This gap is **accepted**, and narrowed by two covers:
- **Throwing faults** (GPU TDR, device-removed, display-mode change, RDP reconnect) are caught by the `OnTick` catch → `MarkUnhealthy` + `ScheduleFaultRecovery` (independent of the poll clock).
- **CW-8/INTENT-03 (this wave):** the **disconnect class** — display disconnect / capture-session close — is now covered by subscribing `GraphicsCaptureItem.Closed → MarkUnhealthy + ScheduleFaultRecovery`. WGC raises `Closed` when the captured target goes away, surfacing exactly the silent disconnect a poll-keyed watchdog could not see.

What remains uncovered is only the narrow intersection: a *silent, non-throwing, non-closing* framepool stall on a screen that also happens to be idle — for which there is no honest signal to distinguish "dead pipeline" from "calm screen" without re-introducing the flap. The locked `LOGIC-08` decision (delete the frame-arrival resume watchdog) and this Closed-event cover are the accepted resolution; the `_lastPollAt`-keyed watchdog still self-heals then surfaces Faulted within the locked ~5 s bound for any stall that actually wedges the capture loop.

---

**Key files referenced:** `c:\Users\kirby\Desktop\SenseEase APP\src\SenseEase.App\Views\PersistentBarWindow.xaml.cs` (sleep-mode pattern to mirror, `:228-247`), `c:\Users\kirby\Desktop\SenseEase APP\src\SenseEase.App\Services\TrayIconService.cs` (`:282` tooltip, `:313` power-save-indicator pattern to mirror), `c:\Users\kirby\Desktop\SenseEase APP\src\SenseEase.App\Views\OverlayWindow.xaml.cs` (mask-only motion budget), `c:\Users\kirby\Desktop\SenseEase APP\src\SenseEase.Infrastructure\Real\WgcSensingProvider.cs` (`:259-293` watchdog to generalize, `:579-583` silent-death catch to fix, `:524` `_lastFrameProcessedAt`), `c:\Users\kirby\Desktop\SenseEase APP\src\SenseEase.Infrastructure\Real\SidecarHeadPoseReader.cs` (`:269-284` null-fallback, `:721` test hook), `c:\Users\kirby\Desktop\SenseEase APP\src\SenseEase.App\Views\TierColorPalette.cs` (single color source for new token), `c:\Users\kirby\Desktop\SenseEase APP\src\SenseEase.Core\Models\SenseState.cs` (`MaskActive`, `HeadTrackingActive`, `LastUpdatedAt`), `c:\Users\kirby\Desktop\SenseEase APP\src\SenseEase.App\MainWindow.xaml.cs` (`:1195-1223` OnStateChanged fan-out, `:711` bar wiring).
