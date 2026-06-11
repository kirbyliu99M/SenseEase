# SenseEase Alpha-1 — Locked Direction (Source of Truth)

> **Rebuild header-note (2026-06-06):** As of 2026-06-06 the engine uses the NATIVE 12-level intensity scale (Q2=B) that REPLACED VimsTier 1–10 + the 3-band model, projected to the 5級 動態感 comfort display — so any "VimsTier 1–10 / 3-band" wording below describes the now-retired implementation detail, while the design INTENT (comfort display) still holds.

> Generated 2026-06-05 by the senseease-rebrand-refine workflow (8 agents, adversarially reviewed). Draft for review — no code changed, nothing committed.

I now have the full engineering wave sequence. The key collision points are clear: Wave 1 (SafetyFloor — Just Talk's hard blocker), Wave 2 (EM1/EM2/C5 engine-data integrity, where the soften-seam belongs), Wave 3 (sensing lifecycle, where the liveness watchdog belongs), and the design waves must thread between/after these. I have everything needed to write the canonical document.

---

# SenseEase Alpha-1 — Locked Direction (Source of Truth)

> **Status: LOCKED.** This document supersedes the six-concept menu and is the single buildable source of truth for the SenseEase rebrand. It synthesizes the five resolutions (liveness, benefit-legibility, soften-seam, Home, pop-up Card) and applies every P0/P1 from the clinical/engineering and honesty/UX adversarial reviews. Authority order for any conflict: **Clinical-safety > Privacy > Engineering-locks > Product-semantics > brand.** Every current-state claim cites `file:line`. Anything touching engine or persistence code is flagged **[BEHAVIOR CHANGE — sign-off]** and listed in §6.

---

## 1. North Star & Locked Decisions

**Design North Star.** SenseEase must *embody* the calm it sells: the quietest, most legible, most reassuring app on the machine. Apple HIG (Clarity / Deference / gentle Depth) + Accessibility-as-the-product. Visual direction **"Calm"**: desaturated slate-and-paper palette, accent `#3A6EA5` (never royal `#024AD8`), muted clay `#B65C4E` for high strain (**never alarm red `#E54747`**), body floor 14px, 4/8pt spacing, one neutral 3-level shadow, rationed motion. Tokens merge **view-locally** via `pack://…/Theme/SenseEaseTheme.xaml` into each window's own `.Resources`, **never `App.Resources`** (the smoke test boots a bare `new Application()`, so App-level resources never load).

**The four locked product decisions (FIXED — do not relitigate):**

1. **PRIMARY POSTURE = "Quiet Guardian."** The app disappears into the OS and protects silently; the user rarely interacts. The five-scalar engineering telemetry (OFI / VIMS Pressure / Leaky Integrator / FMS / FOV Radius) is **DEMOTED behind an Advanced/Clinician disclosure, NOT deleted** — `MainWindow` must stay instantiable for `MainWindowSmokeTests`; no prior work breaks.
2. **DAY-1 PROTECTION.** The always-on overlay "calm field" **defaults ON**, with a user-adjustable baseline (baseline tier 1 = 0% alpha so low-strain users are not over-dimmed). Today `_persistentOverlayEnabled` defaults **FALSE** (`MainViewModel.cs:794`) and baseline tier defaults **4** (`:807`, L4 alpha `0.43`, `OverlayWindow.xaml.cs:59`) — flipping both is a deliberate, flagged, **atomic** product change (§6). **2026-06-06 Kirby override: `_persistentOverlayEnabled = false`; rationale: default-OFF for fresh installs; sign-off: Kirby. (Recorded 2026-06-11 per D-12; reactive spike protection remains on day-1 — the overlay force-shows on Loaded.)**
3. **MOTION POLICY — hold the line.** The only routine motion is the clinical FOV mask cross-fade (`MaskSmoothingFactor=0.25`, `OverlayWindow.xaml.cs:77`). The calm field ships **perfectly static**. No breathing field, no comfort light, no decorative motion, no animated count-ups in Alpha-1.
4. **"Just Talk" deferred.** The conversational front door is the long-term vision but is **HARD-BLOCKED on `SafetyFloor.cs` (does not exist yet)** → deferred to Alpha-2, after the engineering Wave-1 safety floor lands (`ALPHA_HANDOVER.md:130`). Alpha-1 ships Quiet Guardian.

---

## 2. The One Direction — "The Quiet Guardian that Learns You"

The former six-concept menu collapses into **one posture**. SenseEase is a tray-resident guardian that watches your screen's motion silently (WGC optical flow 15Hz → leaky-integrator VIMS pressure → a single comfort scalar, VIMS tier 1–10) and protects you without asking: it gently dims the screen periphery with a static 3-zone radial calm field that deepens only when motion rises, and steps that protection *back* over weeks as your own tolerance grows (the habituation `_offset` — the only thing the product is genuinely building). It almost never speaks. When it *does* surface — a tray hover, a pop-up Card on a strain spike — it answers exactly one question with near-zero cognitive load: **"Are you protected right now, and is this actually working?"** It states comfort in plain language (never a number), offers one effortless action ("Ease my view now"), and tells the honest truth when sensing falters instead of freezing silently. It claims nothing about your health, fabricates no efficacy figure, and shows only numbers read directly from your own settings on your own device. It is the calm it sells: present, legible, reassuring, and quiet.

---

## 3. Resolved Open Questions

### 3.1 Liveness / Trust Contract — FINAL

**The gap (confirmed in code).** `WgcSensingProvider.OnTick` wraps the whole capture path in a try/catch whose handler is `WgcLog(...); Stop();` (`WgcSensingProvider.cs:579-583`, verified — bare `Stop()`, no event). After that, `OpticalFlowSampled` never fires, `StateChanged` goes silent, and bar/tray/mask **freeze at their last value** — a frozen tier-1 grey bar is pixel-identical to a healthy one. The only watchdog (`ScheduleResumeWatchdog`, `:259-293`) is **one-shot, Resume-only**; `_lastFrameProcessedAt` (`:524`) is read only by it (`:271`). "Is it even working?" — itself a strain trigger — has no honest answer in the UI today.

**The contract.** One new **liveness scalar** (4-value enum), orthogonal to the VIMS comfort scalar, **derived once in `MainViewModel`** alongside `StateChanged` and fanned out read-only to the three ambient surfaces exactly as `VimsTier` is fanned via `TierColorPalette`:

| State | Meaning | Bar / Tray treatment |
|---|---|---|
| **Protecting** | Sensing live + mask actively dimming (`SenseState.MaskActive`, `SenseState.cs:33`; tier ≥2 above floor) | Existing tier brush (`TierColorPalette.BrushForTier`) |
| **Steady-watching** | Sensing live, screen calm, mask at baseline floor | Existing faint grey floor brush |
| **Paused-by-user** | Power Save on; sensing intentionally off (`_powerSaveActiveLookup`, `MainViewModel.cs:57`) | Existing dashed `SleepOverlay` (`PersistentBarWindow.xaml.cs:228-247`); `SetPowerSaveIndicator(true)` (`TrayIconService.cs:313`) |
| **Faulted** | Sensing/sidecar/engine died unexpectedly | **NEW** static `FaultedBarBrush` + solid (non-dashed) `FaultOutline` |

Protecting and Steady-watching are both healthy and differ only by existing tier color. **Paused (calm-by-choice, dashed) and Faulted (calm-by-accident, solid outline) must never be confusable.**

**Surfacing a fault — auto-heal first, surface second, never alarm:**
- New token `FaultedBarBrush` = muted clay `#B65C4E` @ low alpha (≈0x40) added to `TierColorPalette` as a frozen brush (single color source; no new XAML hex). New idempotent `EnterFaultedMode()/ExitFaultedMode()` on `PersistentBarWindow` mirroring the `_preSleepBackground` snapshot pattern (`:228-247`): sets `BarFill.Background = FaultedBarBrush` + shows a **solid 1px** `FaultOutline`. No animation. **[BEHAVIOR CHANGE — sign-off: new bar methods + token.]**
- Tray: `TrayIconService.SetFaultIndicator(bool)` mirroring `SetPowerSaveIndicator` (`:313`); status/tooltip = reassurance copy, icon stays grey.
- **Continuous rearming watchdog in `WgcSensingProvider`** modeled on `ScheduleResumeWatchdog`: checks `(now - _lastFrameProcessedAt) > StalledThresholdMs` (3000ms). On first trip it does the same `Stop(); Start()` self-heal (`:276-278`) and raises a **new `SensingHealthChanged(bool)` event**; only a second failure propagates `healthy=false` to Faulted. Wire the `OnTick` catch (`:582`) to raise `SensingHealthChanged(false)` instead of dying silently — **the single highest-value line in the contract.** **[BEHAVIOR CHANGE — sign-off: always-on timer + event in sensing provider, ~25 lines, no OFI/tier math change, no persistence.]**
- New read-only `LivenessState` property + `LivenessChanged` event on `MainViewModel`, default **Steady-watching** so the bare-Application smoke path is unaffected. **[BEHAVIOR CHANGE — sign-off: new public VM surface; `MainWindowSmokeTests` must keep tolerating it.]**

**Honest confirmation is pull-only** (no toast, no nag, no new window): tray-hover tooltip leading with the verdict, and a bar-click Card self-check row (plain-language checkmarks for screen-sensing / calm-field / head-tracking + a verdict). Per **honesty-review P2-1**, the default self-check row shows a binary `✓ Screen sensing — live` / `✗ reconnecting…`; the raw `now - _lastFrameProcessedAt` age is shown **only behind the Advanced/Clinician disclosure** (no sub-second heartbeat for an anxious user).

**P0/P1 fixes applied:**
- **[P0-1, clinical] Watchdog must not fight the pause/warm-up path.** First lines of the new watchdog callback: `if (_paused) return; if (!_running) return; if ((now - _resumedAt) < WarmupMs) return;` (mirroring the resume watchdog's `if (_paused) return;` at `:270`). Otherwise Power-Save freezes `_lastFrameProcessedAt` and renders as **Faulted** — violating the invariant. Required test: enter Power-Save → advance past `StalledThresholdMs` → assert `SensingHealthChanged(false)` does **not** fire and `LivenessState == Paused-by-user`.
- **[P0-2/P0-4, honesty/a11y] Faulted retry must not dead-end, and must announce.** After a manually-tapped retry also fails, surface second-state copy: *"還是連不上 · SenseEase 會自動持續嘗試 / Still can't connect — SenseEase will keep trying on its own."* The Faulted transition is the surface's single `AutomationProperties.LiveSetting=Assertive` live region (protection-integrity = equal weight to a safety note); healthy→healthy comfort drift is **never** announced.
- **[P1-1, honesty] Bounded worst-case.** Time from silent pipeline death to Faulted visual = `StalledThresholdMs` (3000ms) + one self-heal window (≤2000ms) ≈ **5s**. This is the documented, accepted maximum a dead pipeline can render healthy-calm. Do not let it grow.
- **[P1-5/P2-2, a11y] Fault ≠ Paused ≠ high-strain must survive low-vision + High-Contrast.** The **outline style (solid vs dashed) is the load-bearing differentiator; color is reinforcement.** `LivenessContractTests` distinguishability assertions must check the outline-style token differs (not just `brush != brush`), and compare Faulted vs **high-tier-strain** (both clay-ish), not only Faulted vs calm. **[Open polish, non-blocking]** consider a desaturated/neutral-warm fault token so clay isn't overloaded for two orthogonal meanings (strain vs fault).
- **[P2-2, eng] Threading.** `SensingHealthChanged` is raised from a `Timer` callback and the `OnTick` catch (thread-pool); `MainViewModel`'s single derivation must re-post via the same dispatcher path the sample callback uses (`_ctx.Post`, `WgcSensingProvider.cs:574`) before touching bar/tray (they `VerifyAccess`).

**`LivenessContractTests`** (the mechanical guarantee that working ≠ broken can never render identically): fault-brush ≠ healthy-brush (anti-freeze); Paused ≠ Faulted (token + outline-style delta); stall trips Faulted within threshold (injected time, `PressureSpikeDetector.cs:36` pattern); **Power-Save ≠ Faulted (P0-1)**; OnTick-fatal raises `SensingHealthChanged(false)`; sidecar death (`TestHook_SimulateSidecarExit`, `SidecarHeadPoseReader.cs:721`) flips the head-tracking row but mask center stays `(0.5,0.5)`; smoke-compat (`LivenessState` defaults Steady-watching).

### 3.2 Benefit Legibility — FINAL (honest data, no dashboard, no fabricated numbers)

**HARD RULE (locked, verbatim):**
> Every number shown to the user MUST be a direct read of a persisted artifact under `%LocalAppData%\SenseEase\` and labelled as what it literally is (a setting, a count, a relaxation offset). No clinical, efficacy, symptom-reduction, or "motion absorbed/prevented" claim. No figure that is not a direct read of persisted state. No volatile session counter framed as cumulative benefit. When in doubt, describe the **mechanism**, not the **outcome**.

**The banned figure.** "12 minutes of motion you didn't absorb" is **forbidden** — a fabricated quasi-clinical efficacy figure the app cannot measure.

**The only honest benefit scalar in Alpha-1** is **earned clear-zone growth**: `HabituationOffsetPp` (`HabituationProfileSnapshot.cs:64` ← `HabituationEngine._offset` `:59`, read-only via `Snapshot()` `:424`) and its ratio `HabituationLevel` (`:60/:100`), plus **descriptive** facts (`Profile`/`ProfileSource`, `PersonalPressureScale`, `LastCalibrationAtIso`/`CalibrationCount`). **Banned as benefit numbers:** `CumulativePressureLoad` / `SessionFovIntercepts` (volatile, in-memory only, `MainViewModel.cs:858,884,1387,1391`); OFI/Pressure/Integrator/FMS/FOV-radius (demoted telemetry); spike counts (health-adjacent); "% time in tier ≤3" / streaks (**no persisted time-in-tier histogram exists** — event log capped at 20, `SenseDefaults.cs:24`).

**Three pull channels, never an always-open dashboard:**
- **Channel A — tray-menu header line** (read on open from the restored snapshot; `TrayIconService.cs:147`). Show growth clause only when `HabituationOffsetPp >= 1`; else profile line only (never "0 points").
- **Channel B — felt-absence line in the pop-up Card** on calm auto-collapse (`NotificationCardWindow.cs:512`, `tier ≤3`). Static text, existing Card animation only.
- **Channel C — opt-in "Your progress" reflection** behind the Card Settings gear (manual; never auto-pops). Only place richer (still descriptive) stats live. Deferred to Alpha-1.x.

**Ship A + B in Alpha-1 — zero engine/persistence change** (pure reads of already-restored state, `MockInferenceEngine.cs:111,123-142`); Q4.5 untouched (read `_offset`, never write/reset). Mandatory honesty footer: *"These come from your own settings on this device. SenseEase doesn't track symptoms or claim a health benefit. / 以上數據皆來自此裝置上您自己的設定。SenseEase 不追蹤症狀，也不宣稱任何健康療效。"* CI **banned-phrase lint** (a unit test scanning the copy table, per honesty-review **P2-4**) rejects: `absorbed, prevented, reduced your, motion you didn't, symptoms, less sick, {N} minutes`.

**P0/P1 honesty fixes applied (copy rewrites — see §3.4):**
- **[P0-3] Drop the false "since you started" anchor.** No first-run-date field exists; `LastCalibrationAtIso` is the *last* (not first) check-in and moves forward on recalibration. Channel A copy → atemporal *"Your clear view has gently widened by {N} points as you've stayed calm. / 您的清晰範圍已輕柔地拓寬了 {N} 個刻度。"* Add `since you started` / `自您開始以來` to the banned-phrase lint until a true first-run anchor ships.
- **[P1-2] Channel B growth tag** → mechanism, not temporal/outcome: *"…and the app is easing off a little as things stay calm. / ……而且隨著一切保持平穩，App 也在慢慢放鬆保護。"*
- **[P1-6] Channel C progress** reframed as the user's resilience, not the app abandoning them: *"You're about X% of the way to needing this less. / 您已大約走完 X%，逐漸不那麼需要它了。"*

### 3.3 Soften / "more protection" engine seam — **[BEHAVIOR CHANGE — ENGINE — sign-off]**

**The contradiction (confirmed).** `MockInferenceEngine.ApplyUserFeedback(int fovDelta)` (`:292-311`) treats **every** negative delta as a discomfort recurrence: `if (fovDelta < 0) _habituation.NotifyDiscomfort(...)` (`:299-302`), which erodes `_offset` (halve on severe `|delta|≥18`, `-4` on mild; `HabituationEngine.cs:342-380`) and trips a pause. So a calm user asking for "a little more protection" silently burns down weeks of progress while the "you're improving" copy contradicts the engine on the same tick.

**The minimal seam.** One optional boolean, default = today's behavior, guard the single `NotifyDiscomfort` call — **no second entry point** (avoids the C5 duplicate-clamp divergence):
```csharp
void ApplyUserFeedback(int fovDelta, bool isDeliberatePreference = false);   // IInferenceEngine.cs:28
...
if (fovDelta < 0 && !isDeliberatePreference)                                 // MockInferenceEngine.cs:299
    _habituation.NotifyDiscomfort(Math.Abs(fovDelta) >= SenseDefaults.HabituationSevereFovThreshold, _vimsTier);
```
The FOV offset still moves (`_userFovOffset`, `:296`; composed at `:830`) — the user gets the protection — but the progress ledger isn't charged. Sensor-driven recurrence lives entirely in `HabituationEngine.Step` (`:273-277`, weight 1.0) and **never reaches this method**, so the clinical sensor-discomfort guarantee is preserved for free. Q4.5 preserved by construction (deliberate path never calls `NotifyDiscomfort`; `Reset()` never invoked).

**P0/P1 fixes applied — call-site wiring is the load-bearing risk:**
- **[honesty-review P0-1 — CROSS-RESOLUTION CONTRACT, highest priority].** The pop-up Card discomfort ramp (severity 1–4, `OnQuickDiscomfortClicked`, `NotificationCardWindow.xaml.cs:663-670`) is a **COMPLAINT path**: its `QuickDiscomfortPhrases` resolve to `Intent == "ReportDiscomfort"` and **must keep eroding `_offset`** (the user reported nausea). It is product-correct. Therefore: *the ramp tap and any "deliberate preference" control must never share a code path, and the ramp must resolve to `isDeliberatePreference: false`.*
- **[clinical-review P1-1] Do NOT ship the coarse `Intent != "ReportDiscomfort"` gate at the chat call sites** (`ChatbotViewModel.cs:800`, `MainViewModel.cs:1453`). It keys neutrality on a label that can drift; if the NLU ever emits a negative delta under a different intent for a genuine "I feel sick," a real recurrence would silently stop eroding `_offset`. **Instead:** reserve `isDeliberatePreference: true` for an **explicit, literal "more protection" control** whose intent is unambiguously a preference; leave every chat/NLU-originated negative delta on the default `false` (still-erodes) path for Alpha-1. Keep `AdjustmentRecord.Source = "user"` (`ChatbotViewModel.cs:809`) — it correctly feeds `SetPersonalization`'s non-punitive caution model (`HabituationEngine.cs:387-405`), which is the right home for "this user tends to tighten."

**Tests:** `ApplyUserFeedback_DeliberatePreference_DoesNotErodeHabituationOrPause` (Q4.5 offset preserved, no pause, `UserFovOffset < 0`); `ApplyUserFeedback_DefaultPath_StillCountsAsDiscomfort` (default erodes + pauses — proves byte-compatibility); sensor pin `PressureSpike_HalvesOffsetAndPauses` (`HabituationTests.cs:147-161`) stays green.

---

## 4. Headline Surface Specs

### 4.1 Home / Today — `MainWindow.xaml` Tab 1 (Dashboard, lines 168–480; **keep at index 0**)

**Job (one sentence):** answer "Am I protected right now, and is SenseEase working?" with a plain-language comfort state (not telemetry), the calm-field control presented as default, an at-a-glance liveness read, and all five engineering scalars demoted behind one Advanced disclosure. **Status home, not a console.**

```
┌──────────────────────────────────────────────────────────────────────────────┐
│  HEADER  (Surface bg, NOT #024AD8 band :114)                                   │
│  SenseEase                              ┌─ LivenessChip (Badge) ───────────┐   │ ← SensingModeLabel (:689, verbatim)
│  Quiet visual comfort, always on        │ ● Watching · screen capture      │   │
├──────────────────────────────────────────────────────────────────────────────┤
│  [ Today ]   Insights · 洞察   Assistant   Settings · 設定   (renames only)    │
│                                                                                │
│  ① FIRST-RUN CARD  (Card · only if ShowFirstRunBanner :943)  [Skip][Run setup] │
│                                                                                │
│  ② COMFORT HERO  (Card)  ← THE HEADLINE                                        │
│  │  Watching your screen           Type.Display  ← TierToHeadlineConverter(VimsTier) │
│  │  守護你的畫面                     Type.Caption                                 │
│  │  ┌ Status.Chip ✓ Steady ┐  ┌ Status.Chip ◐ Watching · full protection ┐    │ ← VimsTier / Mode+AutoModeText
│  │  ─── ComfortMeter (tier-tinted, h6) ─── ░░  Screen is calm · 畫面很平穩      │ ← VimsTier OneWay, NOT Pressure
│                                                                                │
│  ③ PROTECTION  (Card)                                                          │
│  │  Keep protection always on                         [Toggle.Switch ●—]       │ ← PersistentOverlayEnabled (:795)
│  │  Baseline dimming   L1 ●────────── L10   L1  (IsEnabled=PersistentOverlay…) │ ← PersistentOverlayBaselineTier (:810)
│  │  Building your tolerance   ▓▓▓▓▓▓░░ 62%             [ Pause ]               │ ← HabituationLevel / HabituationButtonText
│  │  [ Run setup again ]   ·· Reset visual settings (confirm-gated, de-emph.)   │
│                                                                                │
│  ④ ADVANCED (engineering)  ▸ Expander · COLLAPSED · Type.Mono                  │ ← OFI/Pressure/Tier/FOV/FMS +
│     OFI 12.4 · Pressure 41.0 · Tier 7/10 · FOV 38% · FMS 2 · Overlay/Sensing  │   OverlayVisible/ActivityStatus/EventLog
└──────────────────────────────────────────────────────────────────────────────┘
```

**Comfort hero** driven read-only off existing `VimsTier` (`:745`, private setter) via pure `Mode=OneWay` `IValueConverter`s (`TierToHeadlineConverter` / `…Subline` / `…ComfortChip` / `…ComfortBrush`) — **zero VM change** (keeps it a pure visual refactor; `ComfortHeadline`/`ComfortSubline` confirmed absent today). Comfort `Status.Chip` is **triple-coded** (icon + label + color), replacing the hue-only ellipse (`:255`): Steady ≤5 / Rising 6–7 / High 8–10, 3-band thresholds consumed read-only via `TierColorPalette` (never a 4th band). ComfortMeter bound to **`VimsTier` (1–10), not `Pressure`** — retires the alarm-red `#E54747` pressure bar (`:230`).

**[P1-4, honesty] Hero copy = app-action, never a felt-state guarantee.** Headline at tier ≤5 → "Watching your screen / 守護你的畫面" rather than "You're protected / 放心" (the tier is a screen-motion proxy, not a symptom reading; never assert how the *user* feels — only what the *app* is doing).

**Liveness** surfaced via the header `LivenessChip` (re-skin of `:128-145`, **keep all three `DataTrigger` values verbatim**), the hero comfort/mode chips (in-window echo of the bar/tray), and the Advanced self-check (`ActivityStatus`, `:754`). All four bindings (`SensingModeLabel`, `VimsTier`, `Mode`/`AutoModeText`, `ActivityStatus`) already exist.

**Telemetry demoted** to one collapsed `Expander` in `Type.Mono`: all five scalars + `OverlayVisible` (`:783`) + `ActivityStatus` + the `EventLog` ListView (`:455`) relocate here, **bindings unchanged** (`Time/Ofi/Pressure/FovRadius/MaskSnapshot/Message` verbatim) — demoted, not deleted; no VM member goes dark. Advanced references no `x:Name` from code-behind, so `MainWindow` stays instantiable. The full `PressureChartCanvas` (`:575`) stays on Insights, out of scope.

**Tokens/a11y:** `Card`/`List.Row`/`Section.Header`/`Status.Chip`/`Toggle.Switch` from `Theme/Controls.xaml`; alarm red retired for `ComfortHigh #B65C4E`; `#7B8DB0` (fails AA) → `TextSecondary #525B6B`; `AutomationProperties.Name` on every interactive element (currently 0); body ≥14, caption ≥12, `List.Row` MinHeight 48. Smoke test stays green: tokens merge view-locally, all new `VimsTier` bindings are `OneWay` (cannot re-trip the documented TwoWay→read-only regression, `MainWindowSmokeTests.cs:75-79`), two-way bindings only to settable `PersistentOverlay*`.

### 4.2 Pop-up Card — `NotificationCardWindow.xaml(.cs)` *** USER-PRIORITY SURFACE ***

**Job:** the single moment of contact with a possibly-unwell user. Four zones at rest, no disclosure drawer, **no chat**. Answers "Something changed — how do I ease it now?" with (a) plain-language comfort state and (b) one effortless primary action.

```
┌────────────────────────────────────────────────────────┐ Card: Surface · Radius.lg(16) · Pad.Card(24) · Elev.3
│  視覺壓力上升 / Strain is building   [⚙ 設定] [✕ 關閉]   │ A  Title=Type.Heading (plain lang, BC-1)
│  第 6 級 · 壓力指數 41                                   │    telemetry → Type.Mono 13 sub-line (demoted)
│  ┌─ 💡 AI 建議 / AI suggests ──────────────────────┐   │ B  ProposalPanel — only if ActiveProposal≠null
│  │ 把保護稍微調強一點   AI suggests:…(≤120)  剩 01:45│   │    (H7 truncation/strip KEEP; all 3 btns disable on expiry — NE27)
│  │ [ 接受 / Accept ]  先不要 / Not now   等一下      │   │
│  └─────────────────────────────────────────────────┘   │
│  ▲ 視覺壓力上升 / Rising                                 │ C  Status.Chip (icon+label+color, ComfortRising)
│  偵測到中度視覺壓力。建議短暫休息或暗化螢幕。              │    BodyText=Type.Body (DescriptionForBand verbatim)
│  ┌──────────────────────────────────────────────────┐ │ D  PRIMARY full-width · Accent · MinHeight 44
│  │            緩和畫面 / Ease my view now             │ │    → EngageClicked  ← the hero
│  └──────────────────────────────────────────────────┘ │
│  開啟主視窗      6 小時內安靜      [⏻ 暫停感測 ]          │ E  three Btn.Quiet / Toggle.Switch
│  現在感覺如何？ / How does it feel?                      │ F  Scale.Segmented — ONE deepening hue
│  ┌·一點點─┬··有一些─┬···蠻明顯┬····受不了┐                │    position+label+dot-fill icon (colorblind-safe)
│  └────────┴────────┴────────┴────────┘                 │    each cell Tag=1..4 → OnQuickDiscomfortClicked
└────────────────────────────────────────────────────────┘
   ── REMOVED Alpha-1 (deferred Alpha-2 / SafetyFloor): chat box · 送出 · ChatStatusText
```

**Behavior changes (sign-off):** **BC-1** plain-language title keyed off `_lastBand` (`.xaml.cs:467`; telemetry → `Type.Mono` sub-line; **test `NotificationCardWindowTests.cs:40` assertion updated in lockstep**; power-save branch `:448-449` byte-identical). **BC-2** 4-hue alarm grid (`:242-305`) → one `Scale.Segmented` single deepening hue, colorblind-safe by **position + label + filled-dot count** (hue-deepen is reinforcement only, per **P2-3**); cells keep `Tag="1".."4"` → `OnQuickDiscomfortClicked`. **BC-3** primary "啟動保護" → "緩和畫面 / Ease my view now" (`EngageBtn`/`OnEngageClicked` verbatim; retire `#FF9B6B` → `Accent`). **BC-4** remove inline chat (Just-Talk deferred, LOCKED #4).

**Card P0/P1 fixes:**
- **[clinical P1-3 / NE27] All three proposal buttons disable on expiry.** Today the expiry branch (`.xaml.cs:202-204`) disables Accept + Later but **leaves `ProposalDeclineBtn` enabled** (XAML `:188`). Fix: in the same synchronous block, `ProposalDeclineBtn.IsEnabled = false` **and** null `_activeProposal`; add the assertion that **all three** report `IsEnabled == false` post-expiry. Ships in the same change as the restyle.
- **[clinical P1-2 / honesty P1-3 — BC-4 the load-bearing risk] Just-Talk typed path must be *provably dead*, not just hidden.** The chat code-behind hard-depends on `ChatInputBox`/`ChatSendBtn`/`ChatStatusText` (`OnQuickDiscomfortClicked` writes `ChatInputBox.Text` then `TrySendChatAsync`, `:668-669`; reset block `:378-386`), so they cannot be naively deleted. **Resolution:** the discomfort ramp must route the canned `QuickDiscomfortPhrases[severity]` to the host send path such that **the only strings that can reach `SendChatMessage` from this Card are the four canned phrases.** Either (B) refactor `OnQuickDiscomfortClicked`/`TrySendChatAsync` to call the host directly and delete the three controls, **or** (A) collapse-don't-delete **plus** a hard guard in `TrySendChatAsync` that refuses any payload not in `QuickDiscomfortPhrases` while `SafetyFloor` is absent, **plus** `IsTabStop="False"` + exclude from the automation tree (`AutomationProperties.AccessibilityView="Raw"`) + a `<!-- DEFERRED Alpha-2: Just-Talk input, hidden not deleted -->` marker. Add a test asserting the canned-only invariant. Free-text Just-Talk stays Alpha-2; the **button-driven discomfort report is Alpha-1-valid and must keep working.**
- **[EM5] Auto-pop survival.** The auto-surfaced Card is hidden within 500ms by `OnForegroundCheckTick.Hide()` (`.xaml.cs:410-424`). Gate `Hide()` on a `_userHasInteracted` flag (set in `OnCardMouseMove`) or skip the foreground poll while `ActiveProposal != null` — must ship alongside the restyle, since auto-pop is the Card's primary scenario.

**Migration pins:** `Width=480` (positioning math, `.xaml.cs:343`), `SizeToContent="Height"`, `OnCardMouseMove` on root, transparency/topmost flags all preserved; re-fire `GetMonitorRectInPhysicalPixels` exclude-rect registration after the new (smaller) layout settles (`:741`, Guardrail C2). x:Names that **must survive verbatim** (test-pinned, `NotificationCardWindowTests.cs:40/57-58/74/95/107-110/138/167/197-199`): `TitleText, BodyText, PowerSaveToggle (ToggleButton-derived, exposes IsChecked), ProposalPanel, ProposalSummaryText, ProposalRationaleText, ProposalAcceptBtn, ProposalDeclineBtn, ProposalSafetyNoteText`. The discomfort-ramp send path ultimately reaches `ApplyUserFeedback(neg) → NotifyDiscomfort` (`MockInferenceEngine.cs:292-311`) — **do not alter that mapping** (a discomfort report is *supposed* to erode `_offset`); never call `_habituation.Reset()`.

### 4.3 Every other surface — one-line status (full drafts in `docs/design/VISUAL_SPEC.md`)

| Surface | Alpha-1 status |
|---|---|
| **Insights** | Re-skin per `VISUAL_SPEC.md`; hosts the live `PressureChartCanvas` (`:575`); pure-presentational — out of headline scope. |
| **Assistant** | Re-skin only; **Just-Talk front door deferred to Alpha-2** (blocked on `SafetyFloor.cs`). Existing chatbot stays as-is behind its tab. |
| **Calibration** | Re-skin per `VISUAL_SPEC.md`; collapse the 5×-duplicated 40-line `ControlTemplate` (`CalibrationCardView` `:224-353`) to one shared template. Pure-presentational. |
| **Settings** | Re-skin to `List.Row`/`Toggle.Switch`/`Section.Header` per `VISUAL_SPEC.md`. Pure-presentational. |
| **Ambient bar** | `PersistentBarWindow` gains `FaultedBarBrush` + `FaultOutline` (§3.1); **never animates** (`TECH_CAPABILITY_MAP.md:48`). Engine-touching (new methods/token) — sign-off. |
| **Tray** | `TrayIconService` gains `SetFaultIndicator` + verdict-led tooltip (§3.1, §3.2 Channel A); 3-band tint **never animates** (`:50`). |
| **Overlay (mask)** | Untouched. Only routine motion = clinical cross-fade (`MaskSmoothingFactor=0.25`, `:77`); never overridable by Reduce-Motion (safety channel). |
| **Onboarding / first-run** | Re-skin the first-run Card per `VISUAL_SPEC.md`; pure-presentational. |

---

## 5. Revised Roadmap D0–D6

Design waves are sequenced to **not collide** with the engineering safety/security waves in `ALPHA_HANDOVER.md` and to honor "cannot break prior works." Legend: **[PRES]** pure-presentational (ships in Alpha-1, no sign-off) · **[ENG]** engine/persistence-touching (needs §6 sign-off) · **[DEF]** deferred.

| Wave | Scope | Type | Sequencing vs engineering waves | Verification gate (one line) |
|---|---|---|---|---|
| **D0 — Calm token foundation** | Merge `SenseEaseTheme.xaml`/`Controls.xaml` view-locally; define `Card`/`Status.Chip`/`Toggle.Switch`/`List.Row`/`Btn.*`; add `FaultedBarBrush` frozen brush to `TierColorPalette`. | [PRES] | Anytime; independent of eng Waves 1–9. | `dotnet test` green incl. `MainWindowSmokeTests` (tokens load view-locally, App.Resources stays empty). |
| **D1 — Home re-skin (visual-only)** | §4.1 Home with `OneWay` converters; telemetry → Advanced Expander; triple-coded chips; AutomationProperties. **No default flip.** | [PRES] | After eng **Wave 4** (UX-breakers) so Home isn't re-skinned over a known-buggy dashboard. | Smoke test green; visual review (UVHS hard-stop); every demoted binding still live under Advanced. |
| **D2 — Pop-up Card re-skin** | §4.2 four-zone layout, BC-1/2/3, **NE27 + EM5** in the same change; chat **collapsed/guarded** (canned-only). | [PRES]* | After eng **Wave 4** (NE27/EM5 live here) and **Wave 5** (intent direction). *BC-1 touches one test assertion; canned-only is the gating guard. | `NotificationCardWindowTests` green (`:40` updated); canned-only test passes; auto-pop survives; all 3 proposal buttons disabled post-expiry. |
| **D3 — Liveness contract** | §3.1 — `FaultedBarBrush`/`FaultOutline`/`EnterFaultedMode`, `SetFaultIndicator`, rearming watchdog + `SensingHealthChanged`, `LivenessState`/`LivenessChanged`, self-check, tray verdict. | **[ENG]** | **After eng Wave 3 (Sensing lifecycle, EM3/EM4)** — must not collide with native-crash/lifecycle fixes; the watchdog sits next to `ScheduleResumeWatchdog`. | `LivenessContractTests` all pass incl. **Power-Save ≠ Faulted (P0-1)**, outline-style delta, OnTick-fatal raises event; smoke green. |
| **D4 — Benefit legibility A+B** | §3.2 Channels A (tray header) + B (Card felt-absence); honesty-corrected copy; CI banned-phrase lint. | [PRES] | After eng **Wave 2** (engine-data integrity, so `HabituationOffsetPp` reads are reliable) and D2 (Card). | Banned-phrase lint test green; A shows growth only when `offset≥1`; zero persistence diff. |
| **D5 — Soften-engine seam** | §3.3 — `isDeliberatePreference` flag + guard; wire a **literal "more protection" control** only; ramp stays complaint-path. | **[ENG]** | **After eng Wave 2 (C5/EM1/EM2 engine-data integrity merged)** — same file `MockInferenceEngine.cs`; rebases cleanly on the C5 narrowed-reset work. | Both `ApplyUserFeedback_*` tests green; sensor pin `PressureSpike_HalvesOffsetAndPauses` stays green; ramp resolves `isDeliberatePreference:false`. |
| **D6 — Calm-field default-ON** | §6 item 1 — flip `_persistentOverlayEnabled` FALSE→TRUE (`:794`) **and** baseline tier 4→1 (`:807`), **atomic**. | **[ENG]** | **Last design wave; after eng Wave 3** so day-1-on protection rides over the hardened sensing lifecycle. | "0.00 alpha at rest" test (`ResolveDisplayedTier(1)`==1 + `VimsLevels[0].Alpha==0.00`); natural higher tier still wins. |
| **— Insights / Calibration / Settings / Onboarding re-skins** | §4.3 per `VISUAL_SPEC.md`; Calibration template de-dup. | [PRES] | Interleave with D1/D2; no eng dependency. | Per-surface visual review; smoke green. |
| **— Just-Talk front door** | Conversational entry in Assistant + Card. | **[DEF]** | **Alpha-2, after eng Wave 1 SafetyFloor.cs lands.** | N/A this cycle. |
| **— Breathing field / Focus / comfort light** | Any decorative/ambient motion. | **[DEF]** | **Research-gated; not Alpha-1** (violates LOCKED #3 until evidence + sign-off). | N/A this cycle. |

---

## 6. Residual Decisions / Sign-offs Needed

The product owner must approve these before the engine/persistence-touching changes land. Each is minimal, additive, and individually revertible.

1. **[D6] Calm-field default-ON — ATOMIC two-flip (clinical-review P0-2).** `_persistentOverlayEnabled` FALSE→TRUE (`MainViewModel.cs:794`) **and** `_persistentOverlayBaselineTier` 4→1 (`:807`) must land together. Flipping #1 without #2 ships every user a ~43% vignette at rest (over-dimming low-strain users — violates LOCKED #2). Gate: PR must be rejected if it lands only one; "0.00 alpha at rest" test required. *This is the deliberate Day-1-protection change.* **2026-06-06 Kirby override: `_persistentOverlayEnabled = false`; rationale: default-OFF for fresh installs; sign-off: Kirby. (Recorded 2026-06-11 per D-12; reactive spike protection remains on day-1 — the overlay force-shows on Loaded.)**
2. **[D3] Liveness behavior changes.** (a) Continuous rearming watchdog + `SensingHealthChanged` event in `WgcSensingProvider` (must include the **P0-1 pause/warm-up guard**); (b) new read-only `LivenessState`/`LivenessChanged` on `MainViewModel` (default Steady-watching); (c) `EnterFaultedMode`/`ExitFaultedMode` + `FaultedBarBrush`/`FaultOutline` on `PersistentBarWindow`; (d) `SetFaultIndicator` on `TrayIconService`. No OFI/tier math change, no persistence, no habituation impact.
3. **[D5] Soften-seam engine flag.** `IInferenceEngine.ApplyUserFeedback` gains `bool isDeliberatePreference = false`; one guard in `MockInferenceEngine.cs:299`. Approve the **scoping decision (clinical P1-1):** `true` is wired **only** to a literal "more protection" control — **not** the coarse `Intent != "ReportDiscomfort"` gate at the chat sites. Confirms the §3.3/honesty-P0-1 cross-resolution contract: the discomfort ramp stays a complaint (erodes `_offset`).
4. **[D2/D4] Copy & test-assertion changes that touch behavior.** Approve: BC-1 plain-language Card title (updates `NotificationCardWindowTests.cs:40`); the "Reset visual settings" confirm-gate (if `ResetStateCommand` currently executes immediately — still narrowed-transient, never `_habituation.Reset()`); the five honesty-corrected copy strings (P0-3/P1-2/P1-4/P1-6 + the Faulted retry-failed state).

**Explicit residual risk (no open P0 unresolved):** all four review P0s are **resolved with fixes in scope** above (P0-1 watchdog pause-guard → D3 test; P0-2 atomic flip → D6 gate; honesty-P0-1 ramp-is-complaint → §3.3 contract + D5 scoping; P0-3 false anchor → §3.2/§3.4 copy; P0-4 live-region → §3.1 Assertive rule). The one **standing watch-item**, not a blocker: the BC-4 canned-only guarantee is the seam between two resolutions — if a future change re-enables the hidden chat path before `SafetyFloor.cs` exists, it silently re-opens the deferred Just-Talk surface. Mitigation: the canned-only test (D2 gate) is the regression lock; do not weaken it until Alpha-2 Wave-1 SafetyFloor lands.

---

*Source-of-truth references: `docs/design/HARD_CONSTRAINTS.md` (A–D guardrails), `docs/design/DESIGN_SYSTEM.md` (Calm tokens/components/§7.2 smoke trap), `docs/design/VISUAL_SPEC.md` (per-surface drafts), `docs/design/TECH_CAPABILITY_MAP.md` (signals/actuators, `:48/:50` bar+tray never animate), `docs/design/CONTENT_AND_VOICE.md` (band copy/localization), `docs/ALPHA_HANDOVER.md` (engineering Waves 1–12; SafetyFloor = Wave 1 `:130`, engine-data integrity = Wave 2, sensing lifecycle = Wave 3).*
