# SenseEase — Pillar 3: Immersive Experience Concepts (DISCUSSION)

> **Rebuild header-note (2026-06-06):** As of 2026-06-06 the engine uses the NATIVE 12-level intensity scale (Q2=B) that REPLACED VimsTier 1–10 + the 3-band model, projected to the 5級 動態感 comfort display — so any "VimsTier 1–10 / 3-band" wording below describes the now-retired implementation detail, while the design INTENT (comfort display) still holds.

<!-- Generated 2026-06-05 by the senseease-rebrand-design workflow (31 agents). Companion to docs/UIUX_UPGRADE_PLAN.md. Draft for review — no code changed. -->

> Generated 2026-06-05 by the senseease-rebrand-design workflow (31 agents). Companion to docs/UIUX_UPGRADE_PLAN.md. Draft for review — no code changed.

## Concept 1 — ambient-invisible

# THE INVISIBLE GUARDIAN — Immersive-Experience Concept

## 1. One-line essence

A guardian you never look at: SenseEase senses the storm and softens the screen's edges before you feel sick, then disappears — the only thing you ever *see* is the world getting gentler, not an app telling you it did something.

---

## 2. The experience — an end-to-end day

**Install (90 seconds, zero telemetry shown).** The Inno installer (AppId `DB7DBFD5-66A1-40CA-A320-4FD9AF0B431C`, locked, C3) finishes and the app starts tray-resident. No dashboard auto-opens. The first and only thing the user sees is a single calm card — `Card` token (`Surface` bg, `Radius.md` 12, `Elev.2`, `Pad.Card` 24) — that says, in `Type.Title` (20/26 SemiBold): **"SenseEase is watching the edges for you."** Body (`Type.Body` 14/20): "When the screen starts to swim, I'll quietly dim the corners so your eyes can rest. You don't have to do anything." One `PrimaryButton` (`Btn.Primary`, `Accent` #3A6EA5): "OK, fade into the background." That button literally collapses the card to the tray. This replaces today's telemetry-headline dashboard, where the headline is the `#024AD8` console band (`MainWindow.xaml:114`) over 32px `CardValue` numbers (`MainWindow.xaml:79-84`) labeled "Optical Flow Index / VIMS Pressure."

**First run — a 60-second felt calibration, not a quiz.** The existing demo-video + Q1–Q5 flow (`CalibrationProcessor.Process :29`) runs **once**, but reframed: the user watches the 60s demo and the only ask is to drag a single slider when "it starts to feel like too much." That timestamp is fed to the existing Q1-vs-sensor-peak alignment heuristic (`CalibrationProcessor.cs:333`, which already assumes a 60s demo) to set `personal_pressure_scale` ∈ [0.7, 1.5]. Q2–Q5 are still asked (they set the medical advisory + vestibular cap, `CalibrationProcessor.cs:95-96`), but presented as `Option.Cell` / `Scale.Segmented` rows, not the five hand-copied 40-line templates at `CalibrationCardView.xaml:224-353`. After calibration, the app states once, plainly: "Calibrated. I'll take it from here," and dissolves. **It never opens itself again unprompted.**

**Daily passive use — the guardian is invisible.** For hours, there is *no window, no toast, no number*. The user works. Below the comfort threshold the overlay is genuinely transparent: mask L1 = alpha 0.00 (`OverlayWindow.xaml.cs:54-66`, `VimsLevels` resting). The only persistent trace of the app is the **ambient bar** (`PersistentBarWindow`, 4dip top-center, click-through when "far," `PersistentBarWindow.cs:59`) holding at its quietest steady state — brush alpha 0.15 grey band (`TierColorPalette.cs:49`), no pulsing, no flashing (spec-locked `PersistentBarWindow.cs:16`). Most of the day the user forgets it exists. That is the product working.

**A strain moment — protection arrives *before* the conscious "ugh."** The user opens a graphics-heavy site that auto-plays a parallax scroll. WGC optical flow (`WgcSensingProvider.cs:485`, 15 Hz) sees the motion; the leaky integrator (`LeakyIntegrator.cs:40`, τ≈2.81s) climbs; the VIMS tier (`MockInferenceEngine.UpdateVimsTier :897`) steps up with fast-onset confirm (200ms, `SenseDefaults.cs:87-106`). The screen's periphery **eases** darker over ~250ms (`MaskSmoothingFactor=0.25`, `OverlayWindow.xaml.cs:77`) — never a snap (A4). Simultaneously the ambient bar drifts grey→amber (band 6–7, alpha 0.30, `TierColorPalette.cs:340`) in the user's far peripheral vision. **No card pops. No sound.** The user's eyes relax into the cleared center without ever consciously registering *why* — they just notice they didn't get the headache they'd have gotten. The guardian acted; it did not announce.

**The one time it speaks.** Only a true **spike** (`PressureSpikeDetector.cs:36`: +50% relative rise, 3s window, 30s cooldown) — the rare "this is a lot, fast" event — earns a single whisper: the `NotificationCardWindow` fades in (220ms, `:370`) low and unobtrusive, **once**, with one line: "That last bit was rough — I've dimmed the edges. Tap if you want them darker." It auto-collapses after the existing 60s idle (`:45,512`). This is the *only* unsolicited interruption the entire system is permitted, and it is gated behind the spike detector's 30s cooldown so it can never nag.

**Improvement over weeks — felt as "I can take more now," never as a chart.** The habituation engine (`HabituationEngine.cs`) runs silently. After sustained contiguous calm windows (120–360s, profile-dependent) it relaxes `_offset` (FOV widens, cap 30/25/15pp by profile). The user never sees a progress bar. What they feel: the same parallax site that triggered a tier-7 dim in week 1 now only nudges to tier 5 — the edges stay clearer, longer. **Once a month**, and only if the engine has earned a meaningful offset, the guardian whispers a single optional line via the same spike-grade card: "You've been handling motion better lately — I've widened your clear zone a little. (See how →)" The "See how" is the *only* path into the opt-in dashboard. Progress is delivered as a changed *experience*, with the data available strictly on demand.

---

## 3. Signal → actuator orchestration

The Invisible Guardian rides **one primary closed loop** plus **two whisper channels**, all built on existing real-time paths.

### Primary loop — continuous, silent, the 99% case
```
WGC optical flow (15 Hz, WgcSensingProvider.cs:485)
  → effective-OFI attenuation (Driver ×0.25 / mask-active ×0.15–0.45, MockInferenceEngine.cs:217-225)
  → LeakyIntegrator (τ≈2.81s, LeakyIntegrator.cs:40)
  → VIMS Pressure (unbounded) → UpdateVimsTier 1–10 (MockInferenceEngine.cs:897, hysteresis SenseDefaults.cs:87-106)
  → [ACTUATOR] FOV radial mask alpha+radius (OverlayWindow.ApplyFovMask:351),
       eased over ~250ms (MaskSmoothingFactor=0.25, :77) — NEVER snaps (A4)
  → [ACTUATOR] Ambient bar 3-band color (TierColorPalette.cs:340), no pulse (PersistentBarWindow.cs:16)
  → [ACTUATOR] Tray icon 3-band tint (TrayIconService.IconForTier_Win:147), never animates (:23)
```
Timing: sensing 15 Hz; onset latency ~133ms median-tap (`SenseDefaults.cs:81`) + 250ms mask ease ≈ protection lands well under half a second after motion crosses the noise gate (OFI ≥ 8, `SenseDefaults.cs:16`). **VIMS tier is the single comfort scalar** driving mask, bar, and tray in lock-step through one shared table (Headroom item 8) — this is what makes "one guardian, many quiet surfaces" coherent rather than five UIs.

### Driver/Passenger as silent context — no UI, just gain
`UserActivityModeDetector.DetectMode:111` (1 Hz). When the user is actively driving (typing/mousing), OFI is pre-attenuated ×0.25 (`MockInferenceEngine.cs:217`) so self-driven motion barely registers — the guardian *backs off* exactly when the user is in control, with no visible mode switch at all (respecting D1: Driver = lighter protection, never inverted).

### Whisper channel 1 — the spike (the only unsolicited speech)
```
PressureSpikeDetector (+50% rise / 3s window / 30s cooldown, PressureSpikeDetector.cs:36)
  → ONE NotificationCardWindow fade-in (220ms, :370), auto-collapse 60s (:45)
```
Suppressed during demo runs (`MainViewModel.cs:1422`), reset on Power-Save resume. `LastNormalizedSpike` (0–1, currently unused, Headroom item 5) modulates *only* the whisper card's copy intensity ("a bit rough" vs "that was a lot") — never adds motion to the mask.

### Whisper channel 2 — earned-progress (≤ monthly, opt-in deeper)
```
HabituationEngine offset Δ over a profile calm window (HabituationEngine.cs)
  → IF offset gain ≥ meaningful threshold AND ≥30 days since last:
       ONE progress whisper card → "See how →" is the SOLE entry to the opt-in dashboard
```

**What I deliberately do NOT wire:** no head-tracking by default (B1, OFF), no webcam-luma adaptive dimming (tempting Headroom item 2, but it needs the webcam on — rejected for the invisible default), no idle-ms presence visualization. Invisibility means *fewer* signals surfaced, not more.

---

## 4. "Use freely with actual benefit" — real protection, near-zero effort

**The effort budget is one slider drag, once, ever.** After the 60s calibration the user takes zero further action to be protected. The protection is not gated behind opening anything: the overlay mask is a topmost click-through window (`OverlayWindow.xaml:12`, `IsHitTestVisible=False`) that acts on every tier change autonomously. The user can use *any* app, full-screen, all day, and the guardian works underneath it.

**The benefit is real, not cosmetic, because it rides the clinical mask.** The thing that protects is the engine-owned FOV depth table (A2) — peripheral dimming demonstrably reduces vection-driven VIMS. The Invisible Guardian does not weaken that; it *removes everything around it*. The protection that exists today is exactly the protection delivered here.

**How the benefit is made felt/legible without a dashboard:**
- **Felt in the body:** the headache/nausea that *doesn't* arrive. The clearest signal of success is absence — and that is precisely what this concept optimizes for.
- **Felt at the edge of vision:** the ambient bar's calm-grey → amber drift (`TierColorPalette.cs`) is a pre-attentive cue. The user learns, without instruction, "amber edge = the screen's getting busy, and it's already being handled."
- **Legible on demand:** the dashboard is not deleted — it is **opt-in**. The tray menu (`TrayIconService`) keeps a single "Show details" entry, and the monthly progress whisper offers "See how →." A user who *wants* the Optical Flow Index / pressure chart can still get it; it is simply no longer the default face of the product. This satisfies "calm through absence" without destroying the C5 "cannot break prior works" contract — the telemetry view still exists, demoted not deleted.
- **The honesty test:** because success is invisible, the *one* permitted whisper (spike) and the *one* monthly progress line are the system's entire vocabulary for saying "I did something." They are rationed precisely so the user trusts that when the guardian speaks, it matters.

---

## 5. How it embodies Calm

- **Clarity (one idea per screen):** the default screen *is* the user's own desktop. The only SenseEase-authored idea ever on screen is "your edges are being cared for," carried by the mask itself. No telemetry headline (retires `MainWindow.xaml:79-84` as the *default* face).
- **Deference (quiet chrome):** the app's resting chrome is a 4dip bar and a tray icon. Content — the user's actual work — is never occluded. The `#024AD8` console band (`MainWindow.xaml:114`) is gone from the default path.
- **Gentle Depth (hierarchy via space/elevation):** when the guardian *does* surface (calibration card, spike whisper, progress whisper), it uses exactly one elevation step (`Elev.3` for the floating card) and `Dur.overlay` (320ms) easing — depth via shadow and space, never saturation or motion.
- **Accessibility-as-the-product:** every surface that *does* appear is triple-coded (`Status.Chip`, icon + label + color) so the amber/clay comfort states survive colorblindness and High-Contrast (D2). The ambient bar's color is paired with the tray tooltip text and `AutomationProperties.Name` state phrase, never hue-alone (fixing the color-only `Ellipse` dots at `MainWindow.xaml:255,543,579`).
- **Rationed motion:** the mask cross-fade is the *only* routine motion, and it is a safety token (`Dur.overlay`, always eased even under Reduce-Motion, A4). Everything decorative collapses to instant under the Reduce-Motion master switch.

The deepest calm here is structural: **an overstimulated user cannot be overstimulated by an app that is not there.** Most "calm" apps add a calm *surface*; this one subtracts the surface entirely.

---

## 6. Feasibility — exists today vs must-build

| Capability | State | Notes |
|---|---|---|
| Silent mask loop (sense→tier→mask/bar/tray) | **EXISTS** | Full primary loop is live (`WgcSensingProvider`, `LeakyIntegrator`, `UpdateVimsTier`, `ApplyFovMask`, `TierColorPalette`, `TrayIconService`). |
| Ambient bar at quiet steady state | **EXISTS** | `PersistentBarWindow`, no-pulse spec already enforced. |
| Spike-gated single whisper | **EXISTS (re-scope)** | `PressureSpikeDetector` + `NotificationCardWindow` exist; must change *policy* so spike is the only unsolicited card. Chatbot logic is the redefinable surface (C5). |
| 60s slider-calibration reframe | **MOSTLY EXISTS** | Demo + Q1–Q5 + 60s alignment heuristic exist (`CalibrationProcessor.cs:333`); needs UI reframe to single-slider-during-demo and `Scale.Segmented`/`Option.Cell` rebuild. |
| Suppress auto-opening dashboard; make it opt-in | **MUST BUILD (S)** | Change first-run + startup so no window auto-shows post-calibration; add tray "Show details" + dashboard-on-demand. Pure UI/flow change, no engine edits. |
| Monthly earned-progress whisper | **MUST BUILD (M)** | New gate: read `HabituationEngine` offset delta + 30-day cadence + meaningful-threshold; reuse the card. No engine algorithm change (D3 — read-only of offset, never `Reset()`). |
| Calm token system applied to the 3 surfaced cards | **MUST BUILD (M)** | Apply `Theme/*` tokens to calibration card, spike whisper, progress whisper, ambient bar. Per the §7.2 view-local merge to survive `MainWindowSmokeTests`. |

**Honest effort tier: M (Medium).** The hard part — the real-time protective loop — already exists and is untouched. The work is *subtractive UI* (stop auto-showing the console), one new read-only progress gate, and applying the locked token layer to the ~3 surfaces that ever appear. No new sensing, no new actuator, no engine algorithm edit. The single largest risk is purely policy/flow plumbing, not new technology.

---

## 7. VIMS-safety & privacy risks + mitigations

| Risk | Severity | Mitigation |
|---|---|---|
| **"Invisible" tempts hiding the mask itself** → user can't tell if protection is on | High | The mask's *visible dimming* IS the feedback. Never reduce mask depth below the engine table (A2) or below the 20% G1 floor. The ambient bar + tray tint remain as a triple-coded "I'm active" signal — invisibility applies to *chrome*, never to the clinical actuator. |
| **Silent protection → user distrust ("is it even working?")** | Med | Rationed-but-present whispers (spike + monthly progress) plus an always-available tray "Show details." The opt-in dashboard is the trust escape-valve; nothing is hidden, only demoted. |
| **Brightness snap if mask is driven harder for "felt" protection** | High (A4 HARD-STOP) | Mask is *only ever* driven through the existing `MaskSmoothingFactor=0.25` ease + `Dur.overlay`; the concept adds zero new direct alpha writes. |
| **Escape-hatch as a "make it invisible" CTA** | High (A3) | No "clear the mask" CTA anywhere; escape-hatch stays force-disabled for vestibular users and never reaches 0 opacity. The Invisible Guardian hides the *app*, never the *protection*. |
| **Webcam-luma temptation for ambient adaptation** | High (B1/B3) | Explicitly **out of scope** for the default. Head-tracking stays OFF; no luma consumer added; no preview, no presence widget. Invisibility is achieved by surfacing *fewer* signals, not by opening the camera. |
| **Spike whisper copy leaking PII / routing to LLM** | Med (B2/A1) | Whisper copy is templated from `LastNormalizedSpike` intensity only — no user text. Any future free-text reply still hits `SafetyFloor` line-1 (A1) and logs `pattern_id`, never the utterance. |
| **Progress whisper implying a "reset/start over"** | Med (D3) | Progress whisper is read-only of `_offset`; it never offers a reset action. Full reset stays Settings-only, two-step, distinct from "Delete all my data" (D3). |
| **New on-monitor card self-feeding OFI** | Med (C2) | Every surfaced card (calibration, spike, progress) must register its rect via `WgcSensingProvider.SetMonitorExcludeRect` on render and clear on close (POPUP-OVERLAY §3). Engineering contract handed over with the spec. |

---

## 8. Why someone would love it

- **It respects that they're tired.** An overstimulated, migraine-prone user does not want one more dashboard to monitor. The Invisible Guardian is the only app on their machine that asks for *nothing* after the first minute — and the one thing it removes is exactly the thing they came for: the nausea.
- **It earns trust by silence.** Because it speaks at most twice a month, the rare time it does whisper *means something*. Compare a notification-spamming wellness app: this is the opposite of attention-extraction. It is attention-*returning*.
- **Progress feels like personal strength, not a score.** "The same site that wrecked me last month barely fazes me now" is a felt, embodied win the user owns — far more motivating than a habituation progress bar. The data is there if they want it; the feeling is there whether they look or not.
- **It is the literal embodiment of the product promise.** SenseEase sells calm. The boldest, most honest expression of calm is an app that disappears into the OS and lets the user's own desktop — now gently softened at the edges — be the entire interface. The guardian you never see is the guardian you never have to manage.

---

## Concept 2 — living-overlay

# THE LIVING CALM FIELD

## One-line essence

Your screen grows a soft, breathing dim-field at its edges that quietly deepens when on-screen motion threatens to make you sick and relaxes when it passes — so the protective FOV mask stops being "an overlay that turns on" and becomes a calm ambient field you live inside, with chrome reduced to a single honest status line you almost never need to open.

---

## 1. The Experience — a user day, end to end

### Install (60 seconds, no decisions)
The Inno Setup installer (AppId `DB7DBFD5-66A1-40CA-A320-4FD9AF0B431C`, locked per C3) finishes; the app launches tray-resident. There is no webcam prompt, no permission wall, no telemetry dashboard. Head-tracking stays OFF (`AppConfig.HeadTrackingEnabled = false`, B1) — the field works fully without a camera. The first thing the user sees is **not** the current `#024AD8` engineering header band (`MainWindow.xaml:114`) with 32px telemetry numbers (`CardValue`, `:79-84`); it's a single calm card on `Canvas #F6F7F9`:

> **You're protected.** (`Type.Display` 28/34, `TextPrimary #1A2230`)
> SenseEase is watching the screen for the kind of motion that causes strain. When it builds, the edges of your screen will gently soften. You don't have to do anything. (`Type.Body` 14/20, `TextSecondary #525B6B`)
> `[ Show me ]` (`Btn.Primary`, `Accent #3A6EA5`) `[ Skip the demo ]` (`Btn.Quiet`, replacing the `#9AB6E0` Skip at `MainWindow.xaml:194`)

### First run — the field introduces itself, then the 60s demo
Tapping **Show me** runs the existing 60s demo video, but reframed: instead of "watch these numbers move," the copy is **"Watch the edges of your screen, not the video."** As demo OFI rises through the leaky integrator (`MockInferenceEngine.Update`, `:203`), the user watches the real mask deepen for the first time — the L1→L10 alpha table (`OverlayWindow.xaml.cs:54-66`, 0% → 92%) eased over `MaskSmoothingFactor=0.25` (~250ms, `:77`). They feel it: the field is not a popup, it's the screen breathing. Then Q1–Q5 calibration (`CalibrationProcessor.Process`, `:29`) runs through the de-duplicated `Scale.Segmented` component (one data-driven `ItemsControl`, retiring the five hand-copied 40-line templates at `CalibrationCardView.xaml:224-353`), mapping to `personal_pressure_scale ∈ [0.7,1.5]`.

The single new idea introduced here: **"This is your calm field. It has a resting depth, and it deepens when the screen does."** That sentence is the whole mental model.

### Daily passive use (the 95% case — near-zero attention)
The user works normally. The field sits at its **always-on resting floor**: `PersistentOverlayBaselineTier` default 4 ≈ 43% alpha at the periphery (`MainViewModel.ResolveDisplayedTier:819`, `:807`). This is the key reframe — the mask is **never fully off** during a session, so there is no jarring "it just turned on" moment. The periphery is permanently, gently softened; the user's foveal work area is untouched (clear-zone radius clamped `[15,100]`, `:831`).

Crucially, the field **breathes** at rest. Not a gimmick — a clinically-bounded ambient micro-motion (see §3 the Calm-Breath loop) that makes a *static* dim-field feel alive and intentional rather than like a stuck gray fog (the exact failure mode the L7+ cool tint at `OverlayWindow.xaml.cs:54-66` was designed to avoid). When nothing is happening, the field is so quiet the user forgets it exists — which is the goal.

The tray icon (3-band, `IconForTier_Win:147`, never animates per spec `:23`) and the 4px ambient bar (`PersistentBarWindow`, no flashing per `:16`) are the only chrome. The dashboard is something you open maybe once a week.

### A strain moment (a scroll-heavy webpage, a panning video, a game cutscene)
On-screen motion spikes. WGC optical flow (`WgcSensingProvider` OnTick `:485`, MAD `:765`) at 15 Hz feeds the leaky integrator; `Pressure` climbs, `VimsTier` rises (`UpdateVimsTier:897`). The field **deepens from the edges inward** over ~250ms — never a snap (A4 HARD-STOP). The world-locked anchor grid (`DrawGrid:224`, 40px, world-locked per C1) fades up underneath at its tier-driven opacity, giving the eye a stable static reference frame — the single most effective anti-vection cue, and it's already built.

The user's peripheral vision quiets. The nauseating motion is now happening in a darkened, gridded periphery they're not foveating. **They did nothing.** If they glance up, the ambient bar has shifted from grey to the muted Rising amber band (`TierColorPalette`, re-skinned to `ComfortRising`, D2) — *not* the old alarm hues (`#E6 8C 28` / `#DC 32 28`). No popup interrupts them unless a true spike crosses a band (`PressureSpikeDetector:36`, 30s cooldown).

### With head-tracking opted in (power users, fully optional)
If — and only if — the user explicitly checked the honestly-labeled opt-in ("uses your webcam locally; nothing leaves your device · 需重新啟動 / Requires restart", B1), the clear-zone now **follows their gaze**. The 165 Hz `CompositionTarget.Rendering` path (`MainWindow.OnCompositionRendering:499`, `ApplyHeadPoseToBrush:553`) re-centers the field at display refresh with a 15ms half-life lerp. Look at the chaotic corner of a video and the field's clear-zone *moves with you*, keeping the calm aperture where your eyes actually are — clamped to Balanced `[0.10, 0.90]` so the periphery is never fully cleared (`:564`, respecting A2/A3).

### Improvement over weeks (the field quietly widens)
This is the emotional payoff. The habituation engine (`HabituationEngine.cs`) earns FOV offset during sustained calm windows (120–360s, `HabituationProfile.cs:138`), widening the clear-zone (`_offset`, cap 30/25/15pp by profile). **The field literally grows roomier over weeks** as the user's tolerance builds. Once a week, when they open the dashboard, the hero line reads not in telemetry but in plain language:

> **Your calm field has grown.** Your clear area is about 18% wider than three weeks ago. You're tolerating more motion comfortably. (`Type.Heading` + `Type.Body`)

The progress is *felt in the product itself* (more screen is clear, every day) and *narrated honestly* once a week. The offset is sacred (D3) — no reset control can zero it; "Reset visual settings" (`ResetTransientState()`) is kept distinct from "Delete all my data" (H24), each with an honest consequence preview.

---

## 2. Signal → Actuator Orchestration

Three concurrent loops, each at its native rate, all converging on the one field. **The clinical spine (Loop A) is untouched engine behavior; the immersion is additive (Loops B and C).**

### Loop A — The Strain Loop (clinical core, ~15 Hz) — EXISTS, untouched
```
WgcSensingProvider OnTick (15Hz, :485)  →  MAD×4 clamp 0-150 (:528,540)
  →  WindowsActivityFilter.ShouldSuppressOfi (zero on chrome/typing, :537)
  →  MockInferenceEngine.Update(ofi, mode, sampledAt) (:203)
  →  LeakyIntegrator (τ≈2.81s, α=0.965, :21)  →  Pressure (unbounded)
  →  UpdateVimsTier (1-10, hysteresis up 200ms / down 500ms, :897 + SenseDefaults:87-106)
  →  OverlayWindow.ApplyFovMask (:351): VimsLevels alpha table (:54-66)
       cross-faded MaskSmoothingFactor=0.25 (~250ms, :77)
  →  field DEPTH at the periphery
```
Onset latency ~133ms from the 3-tap median (`SenseDefaults.cs:81`) + EMA α=0.55. **VimsTier (1-10) is the single comfort scalar** that already drives mask, grid, bar, and tray in lockstep through one shared table (Headroom §8) — this concept adds no new authority over depth; depth stays engine-owned (A2 HARD-STOP).

### Loop B — The Gaze Loop (optional, 165 Hz) — EXISTS, repurposed
```
head_tracker.py run() (15Hz target, :514)  →  emits {cx, cy, luma, face_present} ONLY (B3 allowlist)
  →  SidecarHeadPoseReader.ReadLatest (:269, staleness→null after 2000ms, :55)
  →  MainWindow.OnCompositionRendering (165Hz, :499)
  →  OverlayWindow.UpdateHeadGazeCenter (:542) / ApplyHeadPoseToBrush (:553)
       15ms half-life lerp, radius = farthestCorner×1.10 (:591)
  →  field CENTER follows gaze, clamped Balanced [0.10,0.90] (:564)
```
This is the "only existing frame-rate-decoupled animation path" and the "proven foundation for any immersive loop" (Tech map §4, Headroom §1). It is reused as-is. **No change to `head_tracker.py` core** (C4 HARD-STOP) — gaze→center interpretation is host-side only.

### Loop C — The Calm-Breath Loop (NEW, 165 Hz, the one new render behavior)
The single net-new actuator behavior. A **bounded sinusoidal modulation of the *resting-floor* clear-zone radius** (NOT depth, NOT color) that makes the at-rest field feel alive:

```
CompositionTarget.Rendering (already running for Loop B, :499)
  →  when state.VimsTier <= PersistentOverlayBaselineTier (4) AND mask not actively easing:
       radiusOffset = A · sin(2π · t / period)
       A = 0.6% of clear-zone radius (≈ 4-8px on 1080p)   ← clinically tiny
       period = 11s (0.09 Hz, far below any vection-inducing band)
  →  amplitude → 0 the instant tier rises above floor (strain always wins, breath yields)
  →  HARD GATE: ReduceMotion (manual OR NOT SystemParameters.ClientAreaAnimation) ⇒ A=0, dead still
```
The breath is a *spatial* micro-pulse of the aperture edge, eased on the existing 165 Hz path — it costs ~0.17% of one core (the path is already proven cheap, §4). It never touches alpha/brightness (so it cannot create a luminance trigger), never pulses color (A4 forbids "breathing/color-cycling" of the *mask state* — this is a sub-perceptual spatial drift of the *resting aperture*, distinct from and yielding to clinical depth), and it is the **first thing Reduce-Motion kills** (`Dur.ui` dynamic, §3.4). Under Reduce-Motion the field is a perfectly static dim-ring — equally valid, just not "breathing."

### What drives what — at a glance

| Sensed signal | → Actuator | Rate | Authority |
|---|---|---|---|
| OFI → Pressure → **VimsTier 1-10** | Field **depth** (`VimsLevels` alpha, `:54-66`) | 15 Hz, 250ms ease | Engine-owned, clinical (A2) |
| VimsTier | Anchor **grid** opacity (`ApplyGridIntensity:666`) | 280ms anim | Engine, world-locked (C1) |
| VimsTier → 3-band | **Bar** + **tray** color (`TierColorPalette`) | on change | Re-skin only (D2) |
| `cx`/`cy` (opt-in) | Field **center** (`UpdateHeadGazeCenter:542`) | 165 Hz | Host-side interp (C4) |
| Tier at-rest + ReduceMotion off | Field **breath** (NEW: aperture radius ±0.6%) | 165 Hz, 11s period | New, yields to strain |
| Habituation `_offset` | Field **resting width** (clear-zone) | per calm window | Sacred (D3) |
| Spike `LastNormalizedSpike` 0-1 | (see §6 honest-no) | — | Deliberately NOT used for field motion |

---

## 3. "Use freely with actual benefit" — real protection, near-zero effort

**The effort floor is genuinely zero.** The user installs and works. The field protects passively because Loop A is the unmodified clinical engine: every nauseating scroll/pan/cutscene is detected by WGC optical flow and answered by peripheral dimming + the world-locked grid, with no click, no toggle, no attention. This is not aspirational — it is the *current* shipping behavior, merely re-presented so the user *understands and trusts* what's protecting them.

**How the benefit is made felt and legible — four channels, none of them telemetry:**

1. **Felt continuously, in the body.** The always-on resting floor (tier 4, ~43% alpha) means the periphery is *already* gentle before any strain — so when motion hits, there's no startle, just a deepening. The user's peripheral visual field is quieted exactly when vection would otherwise build. The benefit is *the absence of the nausea they used to get*, which is the most powerful possible signal.

2. **Legible at a glance, triple-coded.** One look at the ambient bar or tray tells comfort state via the mandatory `Status.Chip` pattern — **icon + label + color**, never color-only (D2, fixing the bare `Ellipse` dots at `MainWindow.xaml:255,543,579`). "Comfort steady, you're protected" / "Strain rising, easing the edges" / "High strain, full protection on." `AutomationProperties.Name` carries the full phrase for screen readers (fixing `AutomationProperties = 0`).

3. **Legible on demand, in plain language.** The dashboard, when opened, leads with a comfort sentence (`Type.Display`), **not** Optical Flow Index / VIMS Pressure / Leaky Integrator / FMS / FOV Radius (the current telemetry headline, `MainWindow.xaml:75-84`). Engineering telemetry moves to an "Advanced" view in `Type.Mono` for the curious — present, honest, but never the headline.

4. **Legible over time, as growth.** The weekly "Your calm field has grown · clear area ~18% wider than three weeks ago" line turns invisible therapeutic progress (`HabituationEngine` `_offset`) into a visible, motivating, *honest* story. The user *sees more of their screen* every week — protection and progress are the same felt thing.

The chatbot stays available for the rare "the field feels too heavy today" moment — applying bounded `fov_delta` (−30..0, clamped −60..0, `:296`) — but it is a fallback for the 5% case, never the primary loop. **The field protects you whether or not you ever speak to it.**

---

## 4. How it embodies Calm

- **One idea per screen, content is the comfort state.** The field *is* the content; chrome is vestigial (one bar, one tray icon, a rarely-opened card). This is Deference made literal — the UI is the user's comfort state rendered as softened light, not a console of numbers.
- **The quietest app on the machine.** No flashing (`PersistentBarWindow:16`), no animating tray (`:23`), no popups except on true band-crossing spikes. The breath is sub-perceptual and the first casualty of Reduce-Motion.
- **Gentle depth via space and light, never saturation.** The whole concept is depth-as-shadow: a soft peripheral elevation, not a colored alert. Palette is the locked Calm tokens — `Canvas #F6F7F9`, `Accent #3A6EA5`, comfort hues muted (`ComfortHigh #B65C4E`, never alarm `#E54747`).
- **Rationed, switchable motion.** The only motion is the 250ms clinical mask ease (a *safety* token, `Dur.overlay`, never disabled) and the sub-perceptual 11s breath (decorative, killed by Reduce-Motion). Tier transitions never snap (A4).
- **Accessibility as the product.** Triple-coded status, AAA body contrast, 14px floor, screen-reader phrases, colorblind-safe, high-contrast theme via `SystemParameters.HighContrast`. The mask itself is a low-vision and photophobia aid by construction.
- **Reassurance as the default emotional tone.** "You're protected." "Your calm field has grown." Never "Alert," never "Pressure 247," never a number where a sentence will do.

---

## 5. Feasibility — exists today vs must be built

**Effort tier: M (medium).** The clinical spine and both fast render paths already exist; the work is presentation + one bounded new render behavior.

### Already exists (reuse as-is — zero new clinical risk)
- Full strain loop A: WGC sensing, leaky integrator, tier mapping, mask alpha table, 250ms cross-fade (`OverlayWindow.xaml.cs`, `MockInferenceEngine.cs`).
- Always-on resting floor (`ResolveDisplayedTier:819`, baseline tier 4).
- World-locked anchor grid (`DrawGrid:224`, `ApplyGridIntensity:666`).
- The 165 Hz `CompositionTarget.Rendering` gaze path + head-pose follow (`:499`, `:542`, `:553`) — proven, ~0.17% core.
- Habituation offset widening (`HabituationEngine`, `_offset`).
- 3-band tier→color, tray, bar (`TierColorPalette`, `IconForTier_Win`).

### Must be built
| Item | Effort | Notes |
|---|---|---|
| **Calm-Breath loop** (§3 Loop C) | **S** | ~40 lines on the existing `OnCompositionRendering` path; bounded sin on resting clear-zone radius, hard-gated by `ReduceMotion` and tier-above-floor. The only new actuator behavior. |
| **Calm Design System** token layer (`Theme/*.xaml`, 10 files) | **M** | Per the locked Calm spec; view-local merge to survive `MainWindowSmokeTests:29` (do NOT put in `App.Resources`). |
| **Dashboard reframe** — comfort hero replaces telemetry headline | **M** | Move OFI/Pressure/FMS/FOV to an Advanced view; new `Type.Display` comfort sentence; "calm field has grown" weekly line from `_offset` delta. |
| **First-run "watch the edges" reframe** + demo copy | **S** | Copy + flow only; demo engine unchanged. |
| **`Status.Chip` triple-coding** everywhere (bar/tray/dashboard) | **M** | Replaces bare `Ellipse` dots (`:255,543,579`); adds `AutomationProperties`. |
| **`Scale.Segmented` de-dup** for calibration | **M** | Retires 5× 40-line templates (`CalibrationCardView.xaml:224-353`). |
| Reduce-Motion master + high-contrast plumbing | **M** | `SystemParameters.ClientAreaAnimation` / `.HighContrast`, `Dur.ui` dynamic. |

No engine math changes. No `head_tracker.py` changes (C4). No tier-threshold or band changes (C5/D2). Everything net-new is either presentation (tokens/copy/components) or the single additive, hard-gated breath render — so existing tests stay green (C5) and no HARD-STOP is touched.

---

## 6. VIMS-Safety & Privacy risks and mitigations

| Risk | Severity | Mitigation |
|---|---|---|
| **The breath becomes a vection trigger.** Any periodic visual motion can theoretically induce strain. | High if unbounded | Amplitude capped at **0.6% of radius** (4-8px), period **11s (0.09 Hz)** — an order of magnitude below vection-relevant temporal frequencies; it modulates *aperture edge position*, never brightness or color. **Yields to zero the instant tier > floor.** **First thing Reduce-Motion disables.** If any doubt survives review, ship the field perfectly static — the concept stands without the breath; breath is a "nice", not a "need." |
| **Breath reads as the mask "snapping"/flickering.** | Med | It's on the same 165 Hz eased path as gaze follow; sub-perceptual by design. UVHS visual hard-stop review required before any tag (C6). |
| **Resting floor (always-on dim) annoys low-strain users.** | Med | It's the existing `PersistentOverlayBaselineTier` default 4 — already shipping behavior, user-adjustable in Settings; tier 1 = 0% alpha is the floor option for those who want it. Never forced below the 20% G1 slash-floor (A2). |
| **Mask depth must never be weakened by the breath or by "make it calmer" copy.** | HARD-STOP | Depth stays 100% engine-owned (A2). Breath touches *radius* of the resting aperture only, never the `VimsLevels` alpha. No user control drives effective opacity below the 20% floor. |
| **Head-tracking framed as the headline → privacy dark-pattern.** | HARD-STOP | The field is fully functional **without** a camera; gaze-follow is presented as an optional enhancement, never "enable for the best experience" (B1). Opt-in stays unchecked, honestly labeled with restart + local-only caption. |
| **Sidecar / preview temptation.** A "see the field follow you" demo could invite a webcam preview. | HARD-STOP | Explicitly rejected: no preview, no face-mesh, no "what the tracker sees" (B3). The only presence affordance is a `face_present`-driven status chip. `luma` stays unused by this concept (no new consumer; respects C4 "still emits luma even though no C# consumer reads it"). |
| **New on-monitor surface self-feeds OFI.** | High | The field/overlay already registers via `SetMonitorExcludeRect` (C2); any new dashboard/banner drawn on the captured monitor must engage-on-render / clear-on-close. No full-screen scrim. |
| **Reset wipes weeks of field growth.** | HARD-STOP | "Reset visual settings" (`ResetTransientState()`) is kept distinct from "Delete all my data"; neither chatbot nor a one-tap control may zero `_offset` (D3). |

---

## 7. Why someone would love it

- **It protects them without asking anything of them.** Overstimulated, sometimes-nauseated users have zero budget for configuration. The field just works — install, and the screen quietly takes care of your eyes. The single most loved property of any tool for this audience is "I didn't have to fight it."
- **It finally feels intentional, not broken.** Today the mask can read as "my screen is dimming weirdly." Reframed as *your calm field*, with the resting floor and the sub-perceptual breath, it reads as a deliberate, alive, protective presence — the opposite of a glitch.
- **They watch themselves get better.** The field widening over weeks is a rare thing in accessibility software: visible, motivating, honest proof that their tolerance is growing — and it's the product *getting out of their way*, not nagging them with streaks or badges.
- **It respects them.** No webcam unless they say so. No PII. No alarm red. No numbers shouting at someone with a migraine. Plain language, AAA contrast, Reduce-Motion, high-contrast, screen-reader support — the accessibility *is* the product.
- **It's the calm it sells.** A user with vestibular sensitivity opens their laptop and the quietest, gentlest, most legible thing on the machine is the very app whose job is to keep them from feeling sick. That coherence — the medium *being* the message — is what turns a utility into something people are loyal to.

---

## Concept 3 — adaptive-companion

# THE COMPANION THAT LEARNS YOU

*Immersive-experience concept for the Alpha-1 "Calm" rebrand. Codename in-doc: "Companion." This is concept work for discussion — pushed to its best form, scored line-by-line against the A–D guardrails.*

---

## 1. One-line essence

**A protector that earns its own retirement: it watches with you, eases when you strain, learns your tolerance over weeks, and visibly *steps back* as you get stronger — so the felt experience is "I need this less than I used to," not "an app is managing me."**

---

## 2. The experience — an end-to-end narrative

### Install → first run (Day 0, ~3 minutes, then it disappears)

The installer keeps the locked AppId `DB7DBFD5-66A1-40CA-A320-4FD9AF0B431C` (C3) and only the display name/icon change. On first launch the dashboard is **not** the current telemetry console — today it headlines `Optical Flow Index`, `VIMS Pressure`, `Leaky Integrator`, `FMS`, `FOV Radius` as 32px `CardValue` numbers (`MainWindow.xaml:79-84`) under a saturated `#024AD8` band (`:114`) with essential labels at failing `#7B8DB0` (`:75`). The Companion replaces that with **one sentence in `Type.Display` (28/34 SemiBold)**: *"I'll keep watch. You don't have to."* on `Canvas #F6F7F9`, `TextPrimary #1A2230` (14.3:1 AAA).

The 60s demo video runs, then the Q1–Q5 calibration flow (`CalibrationProcessor.Process`, `:29`) — but re-skinned: the five hand-copied 40-line `ControlTemplate`s at `CalibrationCardView.xaml:224-353` collapse into one data-driven `Scale.Segmented`, the Q3 sub-labels move off the failing 10px `#7B8DB0` (`:231-338`) to `Type.Caption` `TextSecondary`, and `Progress.Dots` shows "Step 3 of 5" with `AutomationProperties.Name`. Calibration writes `personal_pressure_scale ∈ [0.7,1.5]`, profile, trigger override, and the vestibular medical advisory (`CalibrationProcessor` → `habituation_profile.json`). **Webcam head-tracking stays OFF** (B1); the opt-in is one honest checkbox with the "需重新啟動 / Requires restart" and "uses your webcam locally; nothing leaves your device" caption — never the headline.

The last calibration card sets the relationship: *"I'm starting cautious. As you get steadier, I'll ask less and show less. You won't have to do anything."* Then the window closes to the tray. The habituation engine boots **Paused** by default (`MockInferenceEngine.cs:178`).

### Daily passive use (Weeks 1–2 — "it's just there")

The user works. The Companion is tray-resident. The only ambient presence is the **4dip top-center bar** (`PersistentBarWindow`, height 4→6 on hover, **no flashing/pulsing** by spec `:16`) re-skinned from the alarm `#E6 8C 28`/`#DC 32 28` hues to the comfort tokens (Steady `#2F7D6E` / Rising `#9A6B22` / High `#B65C4E`) via `TierColorPalette` only (D2). At a steady desk it sits at the **persistent overlay floor** — `ResolveDisplayedTier` floors to `PersistentOverlayBaselineTier` (default 4 ≈ 43% alpha, `MainViewModel.cs:807`), so there is always a barely-there periphery softening even when pressure is low. The user mostly forgets it exists. That is the point.

**The first weekly check-in (end of Week 1)** is the Companion's signature gesture. It is *not* a nag and *not* a popup mid-task. It is a single `NotificationCardWindow` that appears once, after a sustained-idle moment (gated on `UserActivityModeDetector.LastIdleMs`, the live 1 Hz idle signal that is otherwise collapsed to Driver/Passenger — Headroom item 7), with **one plain sentence and one number that is about *them*, not the engine**:

> **This week, you stayed steady 86% of the time.**
> Last week it was 79%. You're trending calmer.
> *[Nothing to do — I'll keep watching.]*

No "VIMS Pressure," no "Leaky Integrator." The headline is a **comfort-state summary derived from the smooth tier scalar** (VIMS tier 1–10, the debounced comfort scalar, Headroom item 8), aggregated from the capped event log (`SenseDefaults.cs:24`, 20-event cap) plus an in-memory weekly ring. The card auto-collapses after 60s idle (`NotificationCardWindow.cs:45`) — read it or ignore it, both are fine.

### A strain moment (any day — the protection users actually feel)

The user opens a fast-scrolling site or a parallax-heavy app. `WgcSensingProvider` optical flow climbs at 15 Hz (`OnTick :485`, MAD×4 clamped 0–150 `:528`). The leaky integrator (`LeakyIntegrator.cs:40`, τ≈2.81s) drives pressure up; `UpdateVimsTier` (`:897`) climbs the comfort scalar with fast-attack/slow-release debounce (`SenseDefaults.cs:87-106`). At pressure ≥85 the mask activates (`MockInferenceEngine.cs:243`, 8s cooldown) and the **3-zone radial FOV mask** deepens along the engine-owned `VimsLevels` table (`OverlayWindow.xaml.cs:54-66`) with the **always-eased `Dur.overlay` ≥320ms cross-fade** (`MaskSmoothingFactor=0.25`, `:77`) — never a snap (A4).

Here is the Companion's *learning* behavior, and it is entirely **read-side interpretation of existing signals** — no new sensing. If `PressureSpikeDetector` fires (`:36`, +50% relative rise, 30s cooldown), the Companion does **not** immediately throw a chatbot bubble (its only current consumer, suppressed in demos, `MainViewModel.cs:1422`). Instead it **silently logs the trigger context** and, *only if the same trigger-shape recurs across days*, the next idle-gated check-in says:

> **I've noticed fast-scrolling pages are your main trigger.**
> I've started easing a half-second earlier when one opens.
> *[Sounds right] [Not quite]*

That "easing earlier" is the chatbot's structured `fov_delta` / `mask_depth_delta` knobs (`MistralChatbotService.cs`, applied via `ApplyShaderAdjustment`, `MockInferenceEngine.cs:313`) — *proposed* and accepted, never silently forced, and clamped (`fov` −60..0, shader knobs [0,1]). The user said nothing in free text; the Companion learned from the **shape of the pressure signal alone**.

### Improvement over weeks (the emotional payoff — "I need you less")

This is where the concept earns its name. The habituation engine (`HabituationEngine.cs`) needs sustained contiguous calm (profile window 120–360s) to leave **Observing → Relaxing** and earn `_offset` (FOV widen, cap 30/25/15pp by profile, `HabituationProfile.cs:138`). As weeks pass and the user's real tolerance grows, three things **visibly recede**, and the Companion *names each recession as a win*:

1. **Check-ins get rarer — by design, and it says so.** Week 1: one check-in. Week 3 (if `HabituationLevel` rising and strike count low): *"You've been steady enough that I'll check in every other week now."* The shrinking cadence is the feature. Week 6 at high level: *"You haven't needed me to step in for 11 days. I'll go quiet — tap the tray icon any time you want me."*

2. **The persistent floor lifts.** As `_offset` accumulates, the earned FOV widening means the same pressure produces a *wider clear zone* — the periphery the user lives inside literally opens up. The Companion surfaces this as **the felt headline of a monthly check-in**: *"A month ago, this page would have dimmed your edges to here [thin ghost ring]. Today, it doesn't dim at all."* This is the **one visualization that makes invisible progress visible** — and it is honest, because it is reading real earned `_offset`.

3. **The relationship inverts.** Early on the Companion is present (floor tier 4, weekly check-ins). Late, it is nearly absent — bar at rest, no check-ins, mask only on genuine spikes. The final state is *almost no app at all*, which the Companion frames as success, not abandonment.

Crucially, when a hard day comes (illness, a migraine, a bad VR session) and pressure spikes at tier ≥8, the habituation engine does **not** plow ahead — it enters `PausedAwaitingUserConfirm` (manual resume only, `HabituationEngine.cs:451`), and the Companion's copy is gentle, never punitive: *"Today was rougher — I've eased back to give you more room. We'll pick up when you're ready."* Progress (`_offset`) is **never zeroed** by this (D3); strikes are deliberately not even restored across sessions (`MockInferenceEngine.cs:149`).

---

## 3. Signal → actuator orchestration

The Companion adds **zero new sensors**. It is a *read-side interpretation and cadence layer* over signals that already converge on `MockInferenceEngine.Update(ofi, mode, sampledAt)` (`:203`). Four loops:

### Loop 1 — Continuous protection (already exists, 15 Hz, untouched)
`WgcSensingProvider` OFI (15 Hz, `OnTick :485`) → `LeakyIntegrator` pressure (`:40`) → `UpdateVimsTier` 1–10 (`:897`) → **FOV mask alpha** (`VimsLevels` `OverlayWindow.xaml.cs:54-66`) + **bar** (`TierColorPalette`) + **tray** (`IconForTier_Win :147`), all in lock-step via the one shared tier table (Headroom 8). Cross-fade always eased `Dur.overlay` (`MaskSmoothingFactor=0.25 :77`). **The Companion does not touch this loop** — it is the clinical protection floor (A2).

### Loop 2 — Weekly comfort-state aggregation (NEW, ~0 Hz / cron-like)
**Signal in:** the smooth VIMS tier scalar sampled at engine tick, accumulated into an in-memory weekly ring (mirrors the existing `MainViewModel` OFI/pressure ring history, `:1391`) → reduced to a single **"% time steady" scalar** per week, persisted as one new aggregate field on the habituation profile snapshot (`habituation_profile.json`, which already carries longitudinal observations).
**Actuator out:** the **check-in copy** in `NotificationCardWindow` — title re-templated from the engineering `VIMS 視覺壓力 第N級 / 壓力指數P` (`:467`) to a comfort sentence.
**Timing:** computed lazily on week-boundary; surfaced only at the next **idle-gated** moment (`UserActivityModeDetector.LastIdleMs` ≥ a few seconds, never mid-task), respecting the 6-hour band-scoped mute (D4) and the 60s-idle auto-collapse (`:45`).

### Loop 3 — Trigger-shape learning → proactive ease (NEW interpretation, existing actuator)
**Signal in:** `PressureSpikeDetector` fire + `LastNormalizedSpike` 0–1 (`:36/:70`, currently unused by any visual — Headroom 5) + Driver/Passenger mode (`UserActivityModeDetector :111`) + the coarse activity context, logged per spike to the event store (`MockInferenceEngine.cs:749`, OFI/pressure/FOV snapshot).
**Interpretation:** across days, detect a recurring spike *shape* (repeated high `LastNormalizedSpike` under Passenger mode). This is pattern-counting over the existing capped event log — no new sensing.
**Actuator out:** a **proposal** to nudge `mask_depth_delta` / `fov_delta` earlier on that context, via the chatbot's structured knobs (`ApplyShaderAdjustment :313`, clamped). Surfaced as the standard expire-able proposal card (15s countdown `:186`, Decline disables on expiry, D4), with the "AI suggests:" prefix + 120-char truncation (H7). **Accept can be blocked by safety intercept** (`:147`) and shows the `SafetyNote` as muted-clay `#B65C4E` ⚠ (A5).
**Timing:** never instant; only after multi-day recurrence; always user-confirmed.

### Loop 4 — Habituation recession → "stepping back" UI (NEW surfacing of existing state)
**Signal in:** `HabituationLevel` 0–100, `Phase {Observing,Relaxing,Paused}`, `CurrentEngineState`, and the earned `_offset` (`HabituationEngine.cs:100/118/132`, snapshot `:424`).
**Actuator out, two channels:**
 (a) **Check-in cadence** — a derived interval (weekly → biweekly → dormant) driven by `HabituationLevel` × low strike count. Higher level = rarer card.
 (b) **The "look how far you've come" ghost-ring** — a *one-shot, opt-in, monthly* still comparison rendered inside the `NotificationCardWindow` (an on-app surface, **not** a new on-monitor overlay window — avoids the `SetMonitorExcludeRect` feedback contract, C2), showing the clear-zone radius a month ago vs now, computed from the `_offset` delta. Static image, no motion under Reduce-Motion.
**Timing:** 1 Hz-or-slower state; surfaced only at check-ins.

> **Note on what is NOT done:** the Companion never reads head-pose to "know you're there" beyond the OFF-by-default opt-in (B1/B3), never opens a webcam preview, never persists raw utterances for trigger learning (it counts spike *shapes*, not words — B2), and never edits `head_tracker.py` to feel snappier (C4).

---

## 4. "Use freely with actual benefit" — protection at near-zero effort

**The effort floor is genuinely zero.** From the moment calibration closes, the user does nothing. Loop 1 protects continuously at 15 Hz whether or not the user ever opens a card. The persistent floor (tier 4, `MainViewModel.cs:807`) means there is *always* a gentle periphery softening, so even a user who never reads a single check-in is materially protected on day 1 and every day after.

**The benefit is made felt and legible three ways, each grounded in real state:**

1. **Felt in the moment** — when a real strain spike comes, the mask deepens (eased, A4) and the edges of the screen go quiet; the nausea-inducing peripheral motion is physically attenuated (Driver ×0.25 / mask-active ×0.15–0.45 pre-attenuation, `MockInferenceEngine.cs:217-225`). The user doesn't read about protection; they *stop feeling sick*.

2. **Legible weekly** — the "% time steady, trending calmer" check-in (Loop 2) converts the invisible leaky-integrator math into one human number about the user's own week. It is the difference between "VIMS Pressure 142" (`NotificationCardWindow.cs:464`) and "you stayed steady 86% of the time."

3. **Legible longitudinally** — the monthly ghost-ring (Loop 4b) is the single artifact that makes *weeks of invisible adaptation* visible: "this page used to dim your edges; now it doesn't." It is the proof that the protection is *working its way out of a job*.

**Near-zero effort is enforced structurally:** every Companion-initiated surface is idle-gated (never mid-task), auto-collapsing (60s, `:45`), band-mute-respecting (6h, D4), and shrinking in frequency by design (Loop 4a). The app's *ambition is to ask less over time*, which is the opposite of every engagement-maximizing app. The user "uses it freely" precisely because using it costs nothing and demands nothing.

---

## 5. How it embodies Calm

- **Clarity (one idea per screen, plain language):** every Companion surface carries **one sentence and at most one number about the user**, in `Type.Display`/`Type.Body` (14px floor). It retires the five-telemetry headline (`Optical Flow Index / VIMS Pressure / Leaky Integrator / FMS / FOV Radius`, `MainWindow.xaml:79-84`) for "you stayed steady 86% of the time." Engineering telemetry survives only in an Advanced view in `Type.Mono`.
- **Deference (quiet chrome, content is the comfort state):** the loudest pixel is never the app — the bar is 4dip and never pulses (`PersistentBarWindow.cs:16`); the tray never animates (`TrayIconService.cs:23`); check-ins are rare, idle-gated, and self-collapsing. The *content* is the user's own calming trend.
- **Gentle Depth (hierarchy via space/elevation, not saturation/motion):** check-in cards use `Card` (`Surface`, `Radius.md`, `Pad.Card` 24, `Elev.2`) and the floating notification uses `Elev.3` (Y8/blur28/0.14) — one neutral black shadow, retiring the blue `#5B82C6` cast. No saturated accents; `Accent #3A6EA5`, comfort tokens desaturated.
- **Accessibility-as-the-product:** every check-in status is a triple-coded `Status.Chip` (icon + label + color, `AutomationProperties.Name` = "Comfort steady, you're protected"), never a bare `Ellipse` dot (retiring `MainWindow.xaml:255/543/579`). Progress uses `Progress.Dots` with "Step N of 5". All motion (ghost-ring reveal, dot fills) collapses to instant under the `ReduceMotion` master switch — **except** the clinical mask cross-fade, which stays eased `Dur.overlay` (A4, safety token).
- **The deepest Calm move:** the app's *success metric is its own disappearance*. An app that brags about how little you now need it is, structurally, the calmest possible relationship a tool can offer an overstimulated user.

---

## 6. Feasibility — exists vs build, honest effort tier

**Exists today (reuse, no new sensing):**
- The entire 15 Hz protection loop, tier scalar, mask/bar/tray actuation, leaky integrator, spike detector, habituation engine with `_offset`/level/phase/state, profile persistence, chatbot structured knobs, proposal-card lifecycle, idle sampling, event log. Every signal the Companion needs already converges on `MockInferenceEngine.Update` and `SenseState`.
- The `NotificationCardWindow` surface, its title templating (`:467`), auto-collapse (`:45`), proposal expiry (`:186`), and safety intercept (`:147`).
- The idle signal (`UserActivityModeDetector.LastIdleMs`) exists live at 1 Hz (Headroom 7) — needs only a new consumer.

**Must be built:**
1. **Weekly "% steady" aggregator** — an in-memory tier ring (mirror of `:1391`) + one reduce + one new aggregate field on `HabituationProfileSnapshot` (it already carries longitudinal observations). **(S)**
2. **Idle-gated check-in scheduler** — a small cadence service: week-boundary trigger → wait for `LastIdleMs` window → emit one card, respecting 6h band mute. Cadence interval derived from `HabituationLevel` + strike count. **(M)**
3. **Trigger-shape recurrence detector** — count repeated `LastNormalizedSpike`-under-context patterns across the capped event log; emit a proposal via the *existing* knob/proposal path. This is the only genuinely new *interpretation* logic, but it rides entirely on existing actuators and is **chatbot-logic-redefinable territory (C5 carve-out)**. **(M)**
4. **Check-in copy templating + ghost-ring still** — re-template card title/body to comfort language; render the static `_offset`-delta clear-zone comparison inside the card (a `Path`/`Shape` still, no new window, no `SetMonitorExcludeRect`). **(M)**
5. **The "Calm" token layer + component refactor** the copy depends on (Display/Body type, `Card`, `Status.Chip`, `Progress.Dots`, `Scale.Segmented` dedup) — large but already mandated by the rebrand, shared cost. **(L, shared)**

**Net Companion-specific effort: M** (the aggregator + scheduler + recurrence detector + copy), riding on an **L** shared design-system refactor that the rebrand pays for regardless. No new sensing, no new on-monitor windows, no engine-loop changes, no `head_tracker.py` edits.

---

## 7. VIMS-safety & privacy risks → mitigations

| Risk | Guardrail | Mitigation |
|---|---|---|
| A check-in popping mid-strain or mid-task becomes its own stressor | A4 / Deference | **Idle-gated only** (`LastIdleMs`), never during a tier-up; respects 6h band mute (D4); auto-collapses 60s (`:45`). Never animated, never pulsing. |
| "Easing earlier" (Loop 3) could push effective mask below the clinical floor | **A2** | Deltas ride the clamped chatbot path (`fov` −60..0, shader [0,1]); the engine-owned `VimsLevels` table and 20% mask floor (G1) still govern — the Companion can only *propose earlier onset*, never *shallower depth*. |
| Celebrating progress could nudge a user to over-widen FOV / disable protection on a good streak | A2 / D3 | The Companion **never** offers a "you're cured, turn it off" CTA; recession is the *app* stepping back (rarer check-ins), not the *protection* weakening. Earned `_offset` is capped (30/25/15pp) and engine-owned; a hard day auto-pauses habituation (`:451`) without zeroing progress (D3). |
| Trigger-shape learning could imply persisting user content / utterances | **B2** | It counts **spike shapes** (`LastNormalizedSpike`, mode, OFI snapshot from the event log), **never raw text**; no new log, no PII, no window titles. The existing whitelist (`LOG-PATHS.md:156-159`) is untouched. |
| Progress-tracking might tempt a webcam "are you there?" presence widget | **B1/B3** | Explicitly rejected. Presence is inferred from `LastIdleMs` (input-derived), not the camera; head-tracking stays OFF by default, scalar-only, no preview. |
| Ghost-ring "look how far you've come" as a new on-monitor overlay → OFI feedback | **C2** | Rendered **inside `NotificationCardWindow`** (an existing app surface), not a new topmost window on the captured monitor — no `SetMonitorExcludeRect` contract triggered; it's a static `Shape`, not a live mask. |
| A bad-day pause reads as punishment / failure | D3 / Calm copy | Copy is non-punitive ("today was rougher, I've eased back to give you more room"); strikes aren't even shown; `_offset` never decreases. |
| Crisis text in a check-in free-text reply | **A1** | The Companion adds no new text-input path; any in-card chatbot send still routes through `SafetyFloor` at line 1 of `SendAsync` before any LLM/lexicon, persisting `[REDACTED: safety_floor]`. |

---

## 8. Why someone would love it

Because it is the first app that treats **needing it less as the goal**. An overstimulated, sometimes-nauseated user is exhausted by software that demands attention, streaks, and engagement. The Companion does the opposite: it protects silently at 15 Hz from minute one, asks for nothing, and over weeks **gets quieter and rarer on purpose** — then *tells the user that's a win they earned*. The weekly "you stayed steady 86% of the time, trending calmer" turns invisible vestibular math into a gentle, true story about getting better. The monthly ghost-ring is the rare moment a user can *see* their own nervous system adapting — "this page used to dim my edges; now it doesn't." And on a hard day it eases back without judgment and never erases the progress. It is a protector that is quietly, visibly **working itself out of a job** — which is exactly what a person who is tired of being managed by their tools has always wanted, and almost never gets.

---

## Concept 4 — conversational

This is a design concept task. The output is a self-contained markdown design document section grounded in the tech map, design system, and hard constraints already provided. No code changes, no commits — pure concept work. Let me deliver the JUST TALK concept.

# JUST TALK — The Chatbot Is the Front Door

## 1. Essence

**One sentence:** You never open a dashboard or flip a toggle — you just say "this is a bit much" in plain language, and SenseEase quietly reads the room (your screen's motion, your strain trend), eases the protection it's already running, and tells you in one calm line what it did — so the entire app collapses into a single, reassuring conversation that mostly listens and occasionally whispers.

---

## 2. The Experience — A User Day

### Install → First Run (60–90 seconds, zero configuration)

Mei has vestibular migraine. She installs SenseEase and the only window that opens is a single calm Card on a `Canvas #F6F7F9` background (not the current saturated `#E2ECFF`) — `Surface #FFFFFF`, `Radius.lg` (16), `Elev.3` shadow, `Type.Display` (28/34 SemiBold) heading that says **"Let's find your comfort. Just talk to me."** Below it, a `Type.Body` (14/20) line: *"Watch this 60-second clip. Tell me how it feels — in your own words. No sliders."*

The 60s demo video plays (the existing post-demo Q1–Q5 calibration flow, `CalibrationProcessor.Process` at `:29`). But instead of the five hand-copied 40-line `ControlTemplate` scale cells (`CalibrationCardView.xaml:224–353`, the FontSize=10 sub-labels at `:231–338`), JUST TALK presents **one** `Field.Text` (`SurfaceSunken` well, 1px `Separator`, `Radius.sm`, real placeholder adorner) with the prompt *"How did that feel?"* Mei types: *"the spinning bits at the end made my head go fuzzy."*

That free-text answer is mapped by the chatbot's deterministic lexicon to the same structured `CalibrationResult` the Q1–Q5 flow produces — `personal_pressure_scale`, SSQ symptom dominance, vestibular advisory — and because she said "head go fuzzy" near the demo's high-motion segment, the Q1-vs-sensor-peak alignment heuristic (`CalibrationProcessor.cs:333`, which already assumes a 60s demo) flags vestibular dominance. The system applies the `VestibularPersonalPressureScaleCap = 1.0` cap (`CalibrationProcessor.cs:95-96`) **silently and correctly** — and crucially, the chatbot replies in one `Type.Body` line: *"Got it — I'll keep your protection a little firmer and never let it drop too low. You don't need to do anything else."* The escape-hatch is force-disabled for her (`OverlayWindow.xaml.cs:486`); the chatbot never mentions a "clear the mask" feature she shouldn't be nudged toward (per **A3** design implication).

She is never shown "Optical Flow Index," "VIMS Pressure," "Leaky Integrator," or "FOV Radius" — the current dashboard headline telemetry (`MainWindow.xaml` `CardValue` 32px numbers at `:79-84`, low-contrast `#7B8DB0` labels at `:75`) is gone from her path entirely. The app goes to tray.

### Daily Passive Use (she forgets it exists — by design)

SenseEase is tray-resident. The WGC optical-flow sensor runs at 15 Hz (`WgcSensingProvider.cs` OnTick `:485`), the leaky integrator computes pressure every tick (`LeakyIntegrator.cs:40`), the tier (1–10) updates with hysteresis (`MockInferenceEngine.UpdateVimsTier :897`), and the FOV mask eases in and out on its own (`OverlayWindow.ApplyFovMask :351`, 250ms cross-fade `MaskSmoothingFactor=0.25`). **Mei does nothing.** She scrolls Twitter, watches YouTube, joins video calls. When a fast-scrolling feed pushes pressure past 85, the mask gently darkens her periphery (`MockInferenceEngine.cs:243`); when she stops, it eases back. This is the existing always-on protection — JUST TALK doesn't replace it, it removes the *dashboard* she'd otherwise stare at.

The only persistent chrome is the ambient bar (`PersistentBarWindow`, 4dip top-center, `TierColorPalette` 3-band, **no flashing** per spec `:16`) re-skinned to the Calm comfort tokens — grey `ComfortSteady #2F7D6E`-adjacent neutral ≤ tier 5, `ComfortRising #9A6B22` at 6–7, `ComfortHigh #B65C4E` at 8–10 (never alarm red `#E54747`). It's a 4-pixel hairline she never consciously notices.

### A Strain Moment (the conversation, finally)

A coworker shares a screen-recording full of camera shake. Mei's pressure climbs; the spike detector fires (`PressureSpikeDetector.cs:36`, +50% relative rise, 3s window). The mask tightens. Mei feels it's still rough and — instead of hunting for a settings panel — clicks the tray or the bar once, and the **Notification Card** (`NotificationCardWindow`, 220ms fade-in `:370`, re-skinned to `Card` token, `Elev.3`, `Radius.lg`) opens with the in-card chatbot already focused. She types: **"this is really rough right now."**

Here's the orchestration the user never sees:

1. **SafetyFloor runs first** (`SafetyFloor.cs`, line 1 of `SendAsync`, per **A1**) — "rough" is not a crisis token, so it passes through. (If she'd typed self-harm language, the hardcoded crisis card — Taiwan 1925 / US 988 / findahelpline — would appear instantly, plain, no animation, no LLM, and her text would persist as `[REDACTED: safety_floor]`.)
2. The chatbot reads **live context it already has**: current `VimsTier` (say 8), the `LastNormalizedSpike` intensity (`PressureSpikeDetector.cs:70`), and Driver/Passenger mode (`UserActivityModeDetector :111`). It knows she's a Passenger getting full protection and that a spike just fired.
3. It emits a `mask_depth_delta` and/or `fov_delta` (`MistralChatbotService.cs`, applied via `ApplyShaderAdjustment`/`ApplyUserFeedback`, `MockInferenceEngine.cs:292,313`), clamped to engine limits (`fov` accumulator −60..0 `:296`, shader knobs [0,1]). Because Mei is vestibular, the **20% mask floor (G1)** and the engine alpha table (`OverlayWindow.xaml.cs:54-66`) cap how light it can go — the chatbot *cannot* over-clear her mask (**A2**).
4. It replies in **one `Type.Body` line**: *"That looked like a sudden jolt. I've deepened your edges and softened the center — better?"* with two `Btn.Ghost` chips: **"Yes, thanks"** / **"Still too much."**

If she taps "Still too much," it deepens once more (within floor) and, if the request would breach a safety cap, surfaces the **transparent block reason** as muted clay `#B65C4E` ⚠ text on the card (**A5**, never hidden, never alarm red). She types a final *"that's better"* and closes it. Total interaction: ~8 seconds, two sentences, no toggles.

### Improvement Over Weeks (the system grows quieter, not louder)

The habituation engine (`HabituationEngine.cs`) runs underneath — paused by default at startup (`MockInferenceEngine.cs:178`), it earns FOV-widening offset after sustained calm windows (120–360s by profile). JUST TALK surfaces this **only as occasional, opt-in proactive nudges** — never a progress dashboard. After two weeks of Mei tolerating tier-6 scrolling that used to spike her to tier-8, the engine has earned offset. The chatbot waits for a calm moment (low pressure, Passenger, no recent spike) and shows a single gentle Card: *"You've been steadier with busy screens lately. Want me to relax the edges a touch so things feel more open? You can always tell me to put them back."* — a proposal with accept/decline (`NotificationCardWindow` proposal path, 15s expiry countdown `:186`, Decline disables on expiry per **D4**).

Crucially, her *progress* (`_offset`) is **sacred** — no chatbot quick-action ever reads as "start over" (**D3**). "Reset visual settings" lives only in Settings, distinct from "Delete all my data," each with an honest consequence preview. Over weeks, Mei talks to SenseEase less and less, because it's gotten her comfort right — the success metric is *silence*.

---

## 3. Signal → Actuator Orchestration

JUST TALK introduces **no new sensors and no new actuators** — it re-routes existing ones through conversational intent. Three concrete loops:

### Loop 1 — Passive protection (already exists; JUST TALK just hides the dashboard)
```
WGC optical flow (15 Hz, WgcSensingProvider:485)
  → MAD×4 clamp 0–150 (:528,540)
  → LeakyIntegrator pressure (:40, τ≈2.81s decay, noise gate OFI<8)
  → VimsTier 1–10 (UpdateVimsTier:897, hysteresis 200/500ms)
  → ApplyFovMask (:351) cross-fade 250ms (MaskSmoothingFactor=0.25)
  → ambient bar 3-band (TierColorPalette) + tray icon (IconForTier_Win:147)
```
**Timing:** ~133ms onset latency (3-tap median + EMA α=0.55, `SenseDefaults.cs:81,33`) + 250ms mask convergence. **User effort: zero.** The chatbot is *narrator*, not driver, of this loop.

### Loop 2 — Conversational adjustment (the front door)
```
User utterance (Notification Card in-card chatbot)
  → SafetyFloor.cs (line 1, BEFORE any LLM — A1) — crisis short-circuit
  → MistralChatbotService reads live context:
       VimsTier (smooth comfort scalar §1 row "best comfort state scalar"),
       LastNormalizedSpike 0–1 (PressureSpikeDetector:70),
       Driver/Passenger (UserActivityModeDetector:111)
  → emits fov_delta (−30..0) / mask_depth_delta / grid_visibility_delta /
       feather_width_delta (±0.3) (system prompt MistralChatbotService:41)
  → ApplyShaderAdjustment / ApplyUserFeedback (MockInferenceEngine:292,313)
  → clamped: fov accumulator −60..0 (:296), shader [0,1] (ShaderParams.Clamp),
       G1 mask floor 20%, engine alpha table cap (A2)
  → mask re-renders via existing 165 Hz CompositionTarget.Rendering path
       (MainWindow:499) — sub-frame, smooth, no snap (A4)
  → one-line confirmation in chatbot bubble (Type.Body, 14/20)
```
**Timing:** LLM round-trip is seconds (Vulkan llama-server, isolated process); the **deterministic lexicon fallback is instant** (`MistralChatbotService` fallback) so "too dark"/"too much"/"more open" map to deltas with no perceptible lag even if the model is slow. Mask change itself is the always-eased `Dur.overlay` (≥300ms) transition — **never snaps, even under Reduce-Motion** (**A4**, safety token).

### Loop 3 — Proactive nudge (the system speaks first, rarely)
```
Spike detector (PressureSpikeDetector:36, +50% rise, 30s cooldown)
   OR HabituationEngine earned-offset event (sustained-calm window 120–360s)
  → gated by: Passenger mode + low current pressure + no demo run
       (spike suppressed during demos, MainViewModel:1422)
  → single calm Card proposal (NotificationCardWindow, 220ms fade,
       15s expiry countdown :186, accept/decline/defer)
  → on accept → habituation _offset widen (cap 30/25/15pp by profile)
       OR shader delta; persists (adjustments.jsonl)
```
**Timing:** Nudges fire at most once per 30s spike cooldown, and habituation nudges only after multi-minute calm windows — **rationed by design** so the app stays quiet.

**Unused-but-available signals JUST TALK deliberately leaves alone** (honesty): webcam `Luma` (computed, thrown away, `head_tracker.py:167` — §5 Headroom #2) and live `idle-ms` (collapsed to binary Driver/Passenger, §5 #7) could enrich proactive timing later, but JUST TALK ships on the *existing* continuous signals (tier, pressure, spike) to stay technically honest and avoid opening the webcam (**B1**).

---

## 4. "Use Freely With Actual Benefit" — Near-Zero Effort, Real Protection

**The protection is real and automatic before the user says a single word.** Loop 1 is the existing clinical FOV mask, running 15 Hz, easing the periphery whenever on-screen motion drives pressure past 85 — this is the CORE product and it needs no conversation. JUST TALK's claim is *not* "talk to get protected"; it's "**you're already protected; talk only to fine-tune.**"

**Effort accounting:**
- **First run:** one free-text sentence instead of five 1–5 scale questions (`CalibrationCardView` Q3 ×5). ~15 seconds.
- **Daily:** zero. The mask, bar, and tray act autonomously.
- **A strain moment:** one sentence ("this is rough"), one tap on a confirm chip. ~8 seconds.
- **Over weeks:** occasionally tap "Yes" on a gentle nudge. Seconds per week.

**How the benefit is made felt and legible** (because "tests green ≠ felt benefit"):
1. **Immediate sensory confirmation** — when she says "too much," the mask visibly, *smoothly* deepens within `Dur.overlay`. She *feels* the room get calmer in under a second. The body is the readout.
2. **One-line plain-language receipt** — every conversational change returns a `Type.Body` confirmation ("I've deepened your edges and softened the center"). She always knows what happened, in human words, never telemetry.
3. **The bar as ambient truth** — the 4dip `TierColorPalette` bar gives her a peripheral, glanceable comfort-state without a dashboard. Steady neutral = "I'm handling it."
4. **Legibility of progress without a chart** — the weeks-later nudge ("you've been steadier lately") *tells her she's improving* in a reassuring sentence, replacing the habituation-level number she'd otherwise have to interpret.

The benefit is felt because the **VIMS tier (1–10) is a smooth, debounced comfort scalar** (§1, "single best comfort state scalar") that already drives mask, grid, bar, and tray *in lock-step via one shared table* — so every surface tells one coherent story, and the chatbot narrates that single story in plain language.

---

## 5. How It Embodies Calm

- **One idea per screen (Clarity):** the entire app reduces to *one conversation*. First run is one question. A strain moment is one card with one input. No tabbed console, no five telemetry tiles. This directly resolves the audit fact that engineering telemetry (OFI, VIMS Pressure, Leaky Integrator, FMS, FOV Radius) is currently the dashboard *headline*.
- **Plain language, not telemetry:** "this is a bit too dark" → action, with a "I've softened the center" reply. The words "Optical Flow Index" never appear in the user path. Body text is `Type.Body` 14/20 floor — no 10px/11px (current 43× violations across 7 files, e.g. `CalibrationCardView.xaml:231–338`).
- **Deference (quiet chrome):** the front door is a single `Card` (`Surface #FFFFFF`, `Elev.2/3`, `Radius.md/lg`) on `Canvas #F6F7F9`. No saturated `#024AD8` header band (`MainWindow.xaml:114`), no `#3D5AFE` colored card borders (`:178`). The content is the user's comfort state.
- **Gentle Depth via space/elevation:** hierarchy comes from the three-level neutral shadow (`Elev.1/2/3`, always `#000000` low-opacity — the blue `#5B82C6` shadow cast is dropped) and 4/8pt spacing, never saturation or motion.
- **Rationed motion + Reduce-Motion master switch:** chat fade-ins use `Dur.base` (200ms) `Ease.calmOut`; typing dots become a static `…` under Reduce-Motion; the clinical mask cross-fade is *exempt and always eased* (`Dur.overlay` ≥300ms safety token, **A4**). Nothing pulses, breathes, or color-cycles.
- **The app's success state is silence.** A calm app talks less over time. JUST TALK's north star — fewer conversations as comfort dials in — *is* the embodiment of the calm it sells.
- **Status is never color-only:** the conversational confirmations + `Status.Chip` (icon + label + color, replacing the bare 8–9px `Ellipse` dots at `MainWindow.xaml:255/543/579`) keep state legible for colorblind and High-Contrast users.

---

## 6. Feasibility — Exists vs Build, Effort Tier

### Exists today (no new capability needed)
- **Conversational delta application** — `fov_delta`, `mask_depth_delta`, `grid_visibility_delta`, `feather_width_delta` are wired end-to-end (`MistralChatbotService` system prompt `:41` → `ApplyShaderAdjustment`/`ApplyUserFeedback`, `MockInferenceEngine:292,313`); §5 Headroom #6 confirms mask-depth/feather are "wired end-to-end yet never shown." **JUST TALK's job is to surface them conversationally.**
- **In-card chatbot, accept/decline proposals, expiry, deterministic fallback** — all in `NotificationCardWindow` + `MistralChatbotService`.
- **SafetyFloor crisis short-circuit** — exists (`SAFETY-PROVISIONS.md §1`; Wave-1 moves to `SafetyFloor.cs`).
- **Live comfort scalars** — `VimsTier`, `Pressure`, `LastNormalizedSpike`, Driver/Passenger — all on `SenseState`, all readable by the chatbot context.
- **Passive mask/bar/tray loop** — fully shipped.
- **Calibration → structured result** — `CalibrationProcessor.Process:29` already exists; the Q-flow is just an input *form* over it.

### Must be built
| Item | What | Effort |
|---|---|---|
| **First-run free-text calibration** | Replace the 5× scale-cell form with one `Field.Text` whose text maps through the chatbot lexicon into the existing `CalibrationResult`. The *processor* exists; only the input adapter + lexicon→answer mapping is new. Must preserve the Q1-vs-sensor-peak 60s alignment heuristic (`:333`) and vestibular cap (`:95-96`). | **M** |
| **Chatbot live-context injection** | Feed current `VimsTier`/spike/mode into the system prompt so replies are situation-aware ("that looked like a sudden jolt"). Read-only context, no engine change. | **S** |
| **One-line confirmation contract** | Every applied delta returns a `Type.Body` plain-language receipt with optional Yes/Still-too-much `Btn.Ghost` chips. Copy + binding. | **S** |
| **Proactive nudge gating** | Wire spike-fire + habituation-earned-offset events to the existing proposal-card path, gated on Passenger + low-pressure + no-demo. Logic glue; card path exists. | **M** |
| **Design-system migration of the two cards** | Re-skin `NotificationCardWindow` + first-run Card to `Card`/`Btn.*`/`Field.Text`/`Status.Chip` tokens; merge `SenseEaseTheme.xaml` view-locally (per §7.2 smoke-test trap). | **M** |
| **"Dashboard-off" path** | Hide/retire the telemetry console as the default surface (keep it behind an "Advanced/Diagnostics" view using `Type.Mono`), so the conversation is the home. | **M** |

**Overall effort: M (Medium).** No new sensor, no new actuator, no new persisted data class, no sidecar change (**C4** intact), no AppId touch (**C3**). The hardest piece is the free-text→`CalibrationResult` lexicon mapping, and even that rides an existing processor. The "logic of chatbot can be redefined" allowance (**C5**, `ALPHA_HANDOVER.md §3.8`) is *exactly* the surface JUST TALK redefines — making it the lowest-friction immersive concept against the hard constraints.

---

## 7. VIMS-Safety & Privacy Risks → Mitigations

| Risk | Mitigation (grounded in invariants) |
|---|---|
| **Chatbot over-clears the mask** ("make it way lighter" → unsafe) | Hard-capped by the **G1 20% mask floor** + engine alpha table (`OverlayWindow.xaml.cs:54-66`) + `fov` accumulator clamp −60..0 (`:296`). The LLM *cannot* drive effective opacity below floor (**A2**). For vestibular users the escape-hatch is force-disabled (`:486`) and never reaches 0 anyway (dips to 0.30, **A3**). |
| **Conversational delta snaps brightness** (trigger risk) | All mask changes route through the always-eased `Dur.overlay` (≥300ms) cross-fade; Reduce-Motion **cannot** disable the clinical smoothing (**A4**, safety token). No instant alpha jump is possible. |
| **Crisis utterance reaches the LLM or logs** | `SafetyFloor.cs` runs at **line 1** of `SendAsync` before any dispatch (**A1**); triggering text persists as `[REDACTED: safety_floor]`, never in `chat_history.jsonl`. Crisis card is plain, instant, no animation/expiry/LLM. |
| **Blocked proposal silently swallowed** | Apply-time intercepts surface the `SafetyNote` as muted-clay `#B65C4E` ⚠ text (**A5**) — never hidden, never alarm red `#E54747`. |
| **Proactive nudge becomes nagging / dark pattern** | Nudges gated to Passenger + low-pressure + no-demo, rationed by the 30s spike cooldown and multi-minute habituation windows; every nudge is decline-able with honest reversal copy ("you can always tell me to put them back"). No "Enable for the best experience" nudge (**B1**). |
| **Free-text calibration leaks PII to non-whitelisted logs** | Calibration free-text is allowed only in the whitelisted `calibration_history.jsonl` (`LOG-PATHS.md:156-159`); the conversational layer logs `pattern_id`/intent, not raw utterances, elsewhere (**B2**). |
| **"Just talk" invites a webcam-preview / presence widget** | Explicitly **out of scope.** JUST TALK ships on screen-OFI + tier + spike, never opening the webcam (**B1**); no face-mesh, no "see what the tracker sees" panel (**B3**). Head-tracking stays OFF by default and is never framed as the headline. |
| **Habituation progress wiped by an ambiguous "reset"** | No chatbot quick-action reads as "start over" (**D3**); "Reset visual settings" (narrowed `ResetTransientState()`) and "Delete all my data" are distinct Settings-only controls with consequence previews; neither chatbot path can zero `_offset`. |
| **Hiding the dashboard hides the mask-state from the user** | The ambient `TierColorPalette` bar + one-line confirmations keep comfort-state legible at all times; "telemetry replaced by a marketing animation that hides mask state" is explicitly rejected by the cross-cutting note — JUST TALK *narrates* mask state, never conceals it. |

---

## 8. Why Someone Would Love It

- **It meets them where they're already nauseated.** An overstimulated, dizzy user cannot parse a five-tile telemetry console or hunt through 17 settings checkboxes (`SettingsContent.xaml`). They *can* type "this is rough." JUST TALK turns the worst moment into the easiest possible action — one sentence.
- **Zero learning curve, zero configuration debt.** No sliders to misconfigure, no toggle matrix to understand. The protection is on by default; the conversation is optional polish. You literally cannot set it up wrong.
- **It feels like a calm companion, not a control panel.** "That looked like a sudden jolt — better now?" is the voice of something *on your side*, not an instrument reading. For users whose condition is invalidated daily ("just look away from the screen"), an app that *believes the jolt was real and acts on it* is profoundly reassuring.
- **The plain-language receipt builds trust.** Every change is explained in one human sentence, so the user always understands and never feels the app is doing mysterious things to their screen — critical for a population that's anxious about sensory triggers.
- **It gets quieter as you get better.** Most apps demand more engagement over time; JUST TALK demands *less*. The weeks-later "you've been steadier lately" nudge is a quiet celebration, and then it recedes. The app's love language is leaving you alone once it's got you right.
- **It's honest and safe by construction.** It can't over-clear your mask, can't snap your brightness, can't miss a crisis line, can't open your webcam behind your back. The user doesn't have to trust marketing — the invariants make the calm *structural*.

**The pitch in one breath:** *Other apps give you dials. SenseEase gives you a sentence — and quietly, smoothly, makes the room feel safe to look at again.*

---

## Concept 5 — ritual-loop

THE COMFORT RITUAL — a trust-building emotional arc layered on SenseEase's existing sensing-and-masking loop. It turns three moments — first run, the strain spike, and end-of-session — into small, optional rituals that make the invisible protection *felt*, without ever touching a HARD-STOP invariant.

## Essence

> **One line:** SenseEase greets you with a 30-second settling breath, quietly soaks up motion strain all day, offers a single warm gesture ("Soften") when a spike hits, and ends each session with a one-card reflection that shows — in plain language — how much vection it absorbed *for* you.

---

## The Experience — an end-to-end user day

A persona: **Mei**, vestibular-sensitive, gets nauseous scrolling long documents and watching autoplay video. She installs SenseEase after a migraine week.

### Install → first launch (the Settling Ritual, ~60s, skippable)
The installer finishes (AppId `DB7DBFD5-66A1-40CA-A320-4FD9AF0B431C` untouched, C3). On first run, instead of the current telemetry console — today's dashboard headlines raw engineering numbers via `CardValue` 32px values (`MainWindow.xaml:79-84`) over a saturated `#024AD8` band (`MainWindow.xaml:114`) — Mei sees **one calm card** on `Canvas #F6F7F9`, centered, `Card` component (`Surface #FFFFFF`, `Radius.md 12`, `Elev.2`):

> **`Type.Display 28/34`:** "Let's settle in."
> **`Type.Body 14/20` `TextSecondary #525B6B`:** "SenseEase watches the screen for motion that can make you queasy, and quietly dims the edges when it builds up. Nothing leaves your device."

A single `Btn.Primary` ("Begin") and a `Btn.Quiet` ("Skip the intro"). This replaces the dark-card-on-light-dashboard palette clash (the `#0E1320` welcome banner, `MainWindow.xaml:178`) with one coherent light surface.

**Begin** runs the existing 60s demo video (the calibration demo path, `CalibrationProcessor` assumes a 60s demo, `:333`) — but reframed as a *breathing rehearsal*, not a test. As the demo's optical flow rises and the mask engages for the first time, a soft caption fades in (`Dur.base 200ms`, `Ease.calmOut`): "Feel that? The edges just softened. That's the protection." Mei **watches the mask do its job once, on purpose**, so the very first mask appearance is expected and explained, not startling. The Q1–Q5 calibration (`CalibrationProcessor.Process :29`) follows, restyled with `Scale.Segmented` (killing the 5× copy-pasted 40-line templates at `CalibrationCardView.xaml:224-353`) and `Progress.Dots` showing "Step 3 of 5."

Head-tracking stays **OFF** — the opt-in checkbox is present, honestly labeled "uses your webcam locally; nothing leaves your device · 需重新啟動 / Requires restart," never pre-checked (B1).

### Daily passive use (the ritual disappears)
Mei does nothing. SenseEase is tray-resident. WGC optical flow runs at 15 Hz (`WgcSensingProvider` OnTick `:485`), the leaky integrator accrues pressure (`LeakyIntegrator.cs:40`), and the mask engages automatically at pressure ≥ 85 (`MockInferenceEngine.cs:243`) with the engine-owned 10-tier alpha table (`OverlayWindow.xaml.cs:54-66`). The **ambient bar** (`PersistentBarWindow`, 4dip top-center, never pulses per spec `:16`) shows her comfort band in the desaturated `ComfortSteady #2F7D6E` / `ComfortRising #9A6B22` / `ComfortHigh #B65C4E` tokens via `TierColorPalette` (D2). She forgets the app exists. **This is the point** — the ritual is bookends, never a tax on the middle.

### A strain moment (the Soften gesture)
Mei opens a 40-page report and scroll-flicks through it. OFI climbs, pressure crosses a spike (`PressureSpikeDetector.cs:36`, +50% relative rise, 30s cooldown), tier rises into the Rising band. The mask tightens automatically — she's already protected. But the spike also fires the existing **NotificationCard** (`NotificationCardWindow`), today titled with raw telemetry "VIMS 視覺壓力 第N級 / 壓力指數P" (`:467`).

The Ritual reframes this single card. Instead of a pressure number, it reads:

> **`Type.Heading 16/22`:** "Strain's building — I've got the edges."
> **One `Btn.Ghost` (`Scale.Segmented`-styled, single-hue ramp):** **"Soften a little more"**

One tap on **Soften** sends a deterministic `fov_delta` of −8 through the *existing* chatbot feedback path (`ApplyUserFeedback`, `MockInferenceEngine.cs:292`, `fov` accumulator clamped −60..0 `:296`) — no LLM round-trip, instant. The mask narrows one notch beyond the engine's automatic response, eased over `Dur.overlay 320ms` (never snapping — A4). A second `Btn.Quiet` reads **"Take a breath"**: tapping it overlays a **4-7-8 breathing guide** drawn *only inside the already-cleared central FOV* (never dimming further, never touching the mask's engine-owned depth — A2), a single expanding/contracting ring at `Accent #3A6EA5`, 4s inhale / 7s hold / 8s exhale, that **honors Reduce-Motion** by collapsing to a static "Breathe slowly" caption (`ReduceMotion` master switch, §3.4). The card auto-collapses after 60s idle (existing behavior `:45,512`); nothing is forced.

Crucially: Mei can ignore all of it. The protection already happened automatically. Soften and Breath are *offered*, never *required*.

### Improvement over weeks (the Reflection + habituation made legible)
Every time Mei closes her laptop or the session goes idle, an **end-of-session Reflection card** appears once (gated, never nagging):

> **`Type.Title 20/26`:** "You're a little steadier today."
> **`Type.Body`:** "I softened the edges **7 times** while you worked — about **12 minutes** of motion you didn't have to absorb. Your steady stretches are getting longer."
> A tiny `Progress.Dots`-style **habituation ribbon** shows her tolerance level (`HabituationEngine.HabituationLevel :100`, 0–100) creeping up week over week — the *sacred* `_offset` (D3) rendered as growth, never as something she can accidentally wipe.

This is the payoff: the benefit becomes **legible**. She *sees* that the app earned 12 minutes of relief and that her vestibular tolerance is genuinely widening (the habituation engine relaxing the FOV offset after sustained calm, `HabituationProfile.cs:138`). Over six weeks the Reflection's numbers shift — fewer Softens needed, longer steady stretches — and Mei *feels* she's getting better, because the data says so in human words.

---

## Signal → Actuator Orchestration

Three distinct loops, each grounded in real capabilities. **No new sensing** is introduced; everything reuses existing signals and the existing 165 Hz / 15 Hz actuation paths.

### Loop 1 — The Settling Ritual (first-run, one-shot)
| Stage | Signal (source) | Actuator (target) | Timing |
|---|---|---|---|
| Demo plays | OFI from WGC (`WgcSensingProvider` OnTick `:485`, 15 Hz) | FOV mask engages naturally (`OverlayWindow.ApplyFovMask :351`) | 15 Hz mask cross-fade `MaskSmoothingFactor=0.25` (~250ms `:77`); first-show 380ms cubic fade-in (`:208`) |
| "Feel that?" caption | mask-active boolean (`MockInferenceEngine.cs:243`) | in-demo caption fade | `Dur.base 200ms` calm-out |
| Calibration | Q1–Q5 (`CalibrationProcessor.Process :29`) | `personal_pressure_scale` ∈ [0.7,1.5], medical advisory (`:107`) | one-shot post-demo |

**Why it works:** the first mask appearance — historically the most startling event — is *pre-explained and rehearsed* using the real OFI→mask path, so the automatic protection later in the week never surprises her.

### Loop 2 — The Soften gesture (in-the-moment, on spike)
| Signal | Source | Drives | Latency |
|---|---|---|---|
| Spike (sharp relative rise) | `PressureSpikeDetector.cs:36` (+50%, 3s window, 30s cooldown) | Reframed NotificationCard surfaces (existing consumer, `MainViewModel.cs:1422`) | engine-tick (~15 Hz) |
| User taps **Soften** | UI command | deterministic `fov_delta −8` via `ApplyUserFeedback` (`MockInferenceEngine.cs:292`), clamped −60..0 (`:296`) | **instant** (no LLM) |
| Mask responds | composed FOV radius, clamped `[15,100]` (`:831`) | `ApplyFovMask` cross-fade | `Dur.overlay 320ms`, never snaps (A4) |
| **Take a breath** | UI command | breathing ring rendered *inside* the cleared FOV via the existing 165 Hz render path (`OverlayWindow.UpdateHeadGazeCenter` entry `:542`, `CompositionTarget.Rendering` `MainWindow.xaml.cs:499`) | display refresh; collapses to static text under Reduce-Motion |

**Key headroom use:** the breathing ring rides the *existing* 165 Hz `CompositionTarget.Rendering` path (tech map §4, §5.1) — the one proven frame-rate-decoupled animation channel — costing ~0.17% of a core, with **zero** new WGC readbacks (the iGPU ceiling, §4). The Soften delta uses the **already-wired** chatbot feedback accumulator, so no new actuator is built.

### Loop 3 — The Reflection (end-of-session, legibility)
| Signal | Source | Drives |
|---|---|---|
| Idle / session-end | `UserActivityModeDetector.LastIdleMs` (`:100`), idle event `:85` | trigger Reflection card once per session |
| Softens this session | in-memory counter on `MainViewModel` session stats (`:1391`) | "I softened the edges N times" |
| Mask-active duration | mask-active boolean integrated over session (volatile, `MainViewModel.cs`) | "~12 minutes of motion you didn't absorb" |
| Habituation level | `HabituationEngine.HabituationLevel :100` (0–100), event log (`:749`) | weekly tolerance ribbon |
| Steady-stretch length | longest contiguous low-tier run (derive from existing tier stream, `MockInferenceEngine.UpdateVimsTier :897`) | "Your steady stretches are getting longer" |

**Why it works:** every number is sourced from signals that *already exist*; the Reflection is pure presentation of state the engine already tracks. The continuous idle signal (currently collapsed to binary Driver/Passenger, headroom §7) is used only as a *trigger*, not re-plumbed.

---

## "Use Freely With Actual Benefit"

**Near-zero effort, real protection:**
- The protection is **100% automatic and unchanged.** The mask engages at pressure ≥ 85 (`MockInferenceEngine.cs:243`) with engine-owned depth (A2) whether or not Mei ever taps anything. The Ritual adds **zero** required interaction to the core safety loop — Soften and Breath are strictly additive offers. A user who skips the intro, ignores every card, and never opens settings is *fully protected* by the same WGC→integrator→mask path as today.
- This satisfies the design mandate directly: the benefit accrues from passive sensing; the rituals only make it *legible and emotionally felt*.

**Benefit made felt/legible — three escalating proofs:**
1. **Rehearsed once** (first run): she *watches* the mask soften the demo and is told "that's the protection" — so she trusts it the first time it fires for real.
2. **Acknowledged in the moment** (Soften): the one card that interrupts her reframes from a number ("壓力指數P", `:467`) to a reassurance ("I've got the edges") — the app speaks *to* her, not *at* her with telemetry.
3. **Proven over time** (Reflection): the end-of-session card converts invisible work into a human sentence ("7 softens, ~12 minutes absorbed") and shows tolerance genuinely widening — the single most persuasive evidence that the product *works*, delivered without a chart of `VIMS Pressure` or `Leaky Integrator` (the telemetry currently dominating `MainWindow.xaml:79-84`).

The legibility *is* the retention mechanism: a VIMS app's core problem is that **when it works, you feel nothing** — so users can't tell it's helping and churn. The Reflection solves exactly this by making "nothing happened to you" into a visible, accruing benefit.

---

## How It Embodies Calm

- **One idea per screen** (Clarity): the Settling card holds one sentence and one button; the Soften card holds one reassurance and one gesture; the Reflection holds one number. No screen shows two competing palettes (resolving the light-console-vs-dark-popup clash, `MainWindow.xaml:178` vs the light dashboard).
- **Plain language, not telemetry** (Clarity): "12 minutes of motion you didn't absorb" replaces "VIMS Pressure / Optical Flow Index / Leaky Integrator." Body never below `Type.Body 14/20`; AAA contrast (`TextPrimary #1A2230` on `Surface #FFFFFF` = 15.3:1).
- **Quiet chrome** (Deference): the bar never pulses (`PersistentBarWindow.cs:16`), the tray never animates (`TrayIconService` spec `:23`), comfort state uses desaturated tokens — no alarm red `#E54747` anywhere (replaced by `ComfortHigh #B65C4E`, D2).
- **Rationed, switchable motion** (gentle Depth): the breathing ring is the *only* decorative motion and it obeys the `ReduceMotion` master switch; the clinical mask cross-fade is exempt and always eased over `Dur.overlay` (A4 — a hard luminance cut is itself a trigger, so it's a safety token).
- **Accessibility-as-product**: every Ritual surface is triple-coded (`Status.Chip` icon+label+color), `AutomationProperties.Name` on the breathing ring ("Breathing guide, 4 seconds in") and the Reflection ("Softened 7 times today"), full Light/Dark/HC support since migraine users prefer dark.

---

## Feasibility

**Exists today (reuse, no new engineering):**
- 60s demo + Q1–Q5 calibration flow (`CalibrationProcessor`) — *reframe copy only*.
- NotificationCard surfacing on spike (`PressureSpikeDetector` → `MainViewModel.cs:1422`) — *reframe copy + restyle*.
- Soften delta path: `ApplyUserFeedback` `fov_delta` accumulator (`MockInferenceEngine.cs:292`) — *wire one button to a fixed −8*.
- 165 Hz render path for the breathing ring (`CompositionTarget.Rendering`, `MainWindow.xaml.cs:499`) — *the proven channel exists*.
- Habituation level + event log for the Reflection (`HabituationEngine :100`, `:749`) — *read existing state*.
- Session stats counters (`MainViewModel.cs:1391`) — *already volatile-tracked; add a Soften counter + mask-active timer*.

**Must be built:**
- **Copy + token restyle** of three surfaces (Settling card, Soften card, Reflection card) using the locked `Theme/` system — **Tier S** (pure XAML + tokens, the rebrand work the mandate already requires).
- **Breathing-ring control** drawn inside the cleared FOV on the render path, with Reduce-Motion fallback — **Tier M** (new visual, but on an existing path; must not touch mask depth).
- **Session-end trigger + Reflection aggregation** (count Softens, integrate mask-active time, compute longest steady stretch, gate to once-per-session) — **Tier M** (new view-model logic over existing signals; all in-memory/volatile so no new persistence-privacy surface).
- **Soften button → fixed `fov_delta −8`** wiring — **Tier S**.

**Honest overall: Tier M.** The heaviest item is the breathing ring; everything else is copy, tokens, and read-only aggregation of state the engine already produces. No new sensing, no schema changes, no sidecar edits (C4), no LLM on the render path (§4 ceiling respected).

---

## VIMS-Safety & Privacy Risks → Mitigations

| Risk | Mitigation |
|---|---|
| **Breathing ring motion could itself induce vection** (a moving element is the exact thing we prevent) | Ring is **slow** (4-7-8 = 19s cycle, far below any flicker/vection threshold), **small**, **centered in the already-cleared FOV** (never in the dimmed periphery), single low-chroma `Accent` color, and **fully collapses to static text under Reduce-Motion** (§3.4). It never moves the mask. |
| **Soften could drive mask below clinical floor** | `fov_delta` rides the engine's `fov` accumulator clamped −60..0 (`:296`) and composed radius clamped `[15,100]` (`:831`); the G1 20% mask floor (A2) is unaffected because Soften narrows the *clear zone*, deepening protection, never lightening below the engine table. |
| **Reflection card could nag / become a chore** | Fires **once** per session on idle, `Btn.Quiet` dismiss, 60s auto-collapse (existing `:45,512`); never on every tier tick (D4). A "show fewer reflections" `Toggle.Switch` in Settings. |
| **"You're getting better" framing could read as medical claim** | Copy stays experiential ("steadier today," "steady stretches longer"), never diagnostic; the vestibular medical advisory and `personal_pressure_scale` cap (`VestibularPersonalPressureScaleCap = 1.0`) are untouched (A5/D3). |
| **Reflection aggregates session data — privacy** | All counters are **in-memory/volatile** (`MainViewModel` session stats, *not* persisted, tech map §3); no new log, no PII, no new file under `%LocalAppData%\SenseEase\` (B2). Habituation level read is already-persisted derived therapeutic state (Low class). |
| **First-run could feel like a webcam pitch** | Head-tracking opt-in stays a single honest checkbox, never pre-checked, never the headline (B1); the Settling Ritual never mentions the webcam as the "best experience." |
| **Crisis safety unaffected** | The Ritual adds no text-input path; any free-text still routes through `SafetyFloor` first (A1), untouched. |

---

## Why Someone Would Love It

Mei installs a dozen "wellness" apps a year and deletes them in a week because she can't tell if they do anything. SenseEase is different on day one: it **greets her like a calm friend** instead of a dashboard, **rehearses the protection** so the first mask never scares her, and then **gets out of the way completely.** When strain hits, it doesn't alarm her — it says *"I've got the edges"* and offers a single breath. And at the end of the day it does the one thing no other VIMS tool does: it **shows her the relief it bought her** — "12 minutes you didn't have to absorb" — and proof, week over week, that **she is actually getting better.** The protection was always there; the Ritual is what makes her *believe* it, *trust* it, and *keep* it. It is the app embodying the exact calm it sells — quiet in the middle, warm at the edges of the day.

---

## Concept 6 — focus-mode

# Immersive Focus Mode — "The Field"

## 1. Essence

**One deliberate gesture that strips the app down to nothing but the protective field and a single breath-slow comfort light — so a sensory-sensitive user can watch something intense and trust the screen is actively guarding them, without touching a thing.**

---

## 2. The Experience — One User's Arc

Meet **Lena**, vestibular-migraine, a hobbyist FPV-drone-video watcher who currently has to pause every reel because the swooping motion makes her nauseous within ninety seconds.

**Install → First run.** Lena installs SenseEase (AppId untouched, `DB7DBFD5-66A1-40CA-A320-4FD9AF0B431C`, constraint C3). The dashboard opens to the new **Calm** light theme — `Canvas #F6F7F9`, not the old saturated `#E2ECFF`, and *not* the engineering-telemetry headline that currently dominates (`MainWindow.xaml:79-84` shows 32px `CardValue` "Optical Flow Index / VIMS Pressure" numbers). Instead the hero is a single `Type.Display` (28/34 SemiBold) comfort line — "You're protected" — with a `Status.Chip` reading **Steady** (check-circle glyph + label + `ComfortSteady #2F7D6E`, never color-only). After a 60s demo video she answers Q1–Q5 calibration; she discloses vestibular sensitivity, so `CalibrationProcessor` sets `VestibularPersonalPressureScaleCap = 1.0` and flags her profile (`CalibrationProcessor.cs:95-96`). **Crucially, this flag now also force-disables the escape-hatch and biases Focus Mode toward deeper protection** — she never has to know that plumbing exists.

**Discovering the mode.** On the dashboard, below the comfort hero, sits one calm `Card` (`Surface #FFFFFF`, `Radius.md 12`, `Elev.2`) titled **"Watching something intense?"** with one `PrimaryButton` (`Btn.Primary`, `Accent #3A6EA5`): **"Enter Focus Mode."** No webcam framing, no "best experience" dark-pattern nudge (constraint B1). A plain `Type.Caption` line: "Full-screen calm protection. Press Esc anytime to leave."

**The gesture.** Lena starts a 12-minute FPV reel full-screen and taps the button (or her global chord). Over `Dur.overlay` (320ms, `Ease.calmOut`) the dashboard, top bar (`PersistentBarWindow`), and tray chrome **recede**: the bar fades its hover affordances, the Card auto-collapses. What remains on screen is exactly two things layered above her video: **(a) the protective FOV mask** — the real `OverlayWindow` radial mask, engine-owned, unchanged in its clinical depth table (`OverlayWindow.xaml.cs:54-66`) — and **(b) one Breath Light**: a single soft ~10px dot of comfort color, low-opacity, resting in a corner she chose, expanding and contracting on a slow ~5.5-second cycle like a calm exhale. That's the entire UI. No numbers. No buttons visible. No telemetry.

**Daily passive use.** This becomes her default for anything fast. She hits Focus Mode, the field protects her, and she *forgets the app exists* — which is the point. The Breath Light sits at the edge of awareness: when comfort is **Steady** it glows faint teal (`ComfortSteady`) and breathes slow; she never consciously reads it, but her nervous system gets a continuous, honest "you're safe" signal that is *not* alarming.

**A strain moment.** Ten minutes in, the reel cuts to a tight canyon-weave — high optical flow. OFI climbs (`WgcSensingProvider`, 15 Hz), the leaky integrator pushes `Pressure` past the trigger, `VimsTier` rises from 4 → 7 (`MockInferenceEngine.UpdateVimsTier:897`). Three things happen, all eased, none abrupt: the **mask deepens** to its L7 tier (cool-slate tint kicks in, `:54-66`) over the clinical `MaskSmoothingFactor=0.25` cross-fade (~250ms, constraint A4 — *never* snaps); the **Breath Light shifts hue** from teal toward muted amber (`ComfortRising #9A6B22`) and its breathing *slows further* (a longer exhale — physiologically down-regulating, never a fast pulse/alarm); and if a true spike fires (`PressureSpikeDetector`, `LastNormalizedSpike` 0–1), the light gives **one single slow swell** — not a flash. Lena's peripheral vision registers "it's working harder" without a single word, glyph-flash, or pop-up interrupting her video. She keeps watching. She does not get sick.

**Leaving.** The canyon section ends, flow drops, tier eases back to 4 over the slow-release debounce (`SenseDefaults.cs:87-106`), mask lightens, the light returns to slow teal. When the reel finishes she taps **Esc**; over `Dur.overlay` the dashboard fades back in, now showing a quiet one-line summary in `Type.Body`: "Focus Mode protected you for 12 minutes. Peak strain: Rising." No graph, no FMS score, no "VIMS Pressure: 247."

**Improvement over weeks.** Because Lena now *uses the app instead of avoiding it*, the `HabituationEngine` finally gets the sustained contiguous-calm windows it needs (120–360s, `HabituationProfile.cs:138`) — Focus Mode sessions are exactly that: long, monitored, calm-biased. Over weeks her earned `_offset` grows (FOV widens toward her profile cap, capped 30/25/15pp by profile), persisted in `habituation_profile.json`. The same canyon-weave reel that forced a deep L7 mask in week one only needs L5 by week six. The dashboard's habituation line (plain language, not telemetry) says: "Your tolerance has grown. The field protects a little less because you need a little less." Her progress is **sacred** — no Focus Mode control ever calls `_habituation.Reset()` (constraint D3).

---

## 3. Signal → Actuator Orchestration

Focus Mode is **not a new sensing or actuation system** — it is a *presentation state* that re-composites existing, locked actuators and adds exactly one new lightweight actuator (the Breath Light). Every signal below already exists; nothing in the clinical loop changes.

### Loop 1 — The protective field (UNCHANGED, clinical, engine-tick ~15 Hz)
| Signal | Source | → Actuator | Timing |
|---|---|---|---|
| OFI (screen pixel-diff) | `WgcSensingProvider` OnTick (`:485`), MAD (`:765`) | feeds → | 15 Hz (`WgcCaptureIntervalMs=66`) |
| Pressure (leaky integrator) | `LeakyIntegrator:40` on `MockInferenceEngine:15` | → VimsTier | per tick, dt-corrected |
| **VimsTier 1–10** | `MockInferenceEngine.UpdateVimsTier:897` | → **FOV radial mask depth/alpha** (`OverlayWindow.ApplyFovMask:351`, table `:54-66`) | mask cross-fade `MaskSmoothingFactor=0.25` ~250ms (`:77`); **A4 floor: always eased, never under Reduce-Motion does this snap** |
| Driver/Passenger | `UserActivityModeDetector.DetectMode:111` | → OFI pre-attenuation (`MockInferenceEngine:217-225`) | 1 Hz; in Focus Mode the user is almost always **Passenger** (watching, idle input) → full protection, correct by D1 |

> **No regression:** Focus Mode consumes `state.VimsTier` *read-only*. It does not cap, reduce, or override mask depth (constraint A2). The mask the user sees in Focus Mode is bit-for-bit the mask the engine commands outside it.

### Loop 2 — The Breath Light (NEW actuator, render-rate, decoupled)
This is the one genuinely new visual, and it is deliberately built on the **proven, cheap 165 Hz render path that already exists** (`CompositionTarget.Rendering`, `MainWindow.xaml.cs:446/499`, ~0.17% of one core) — *not* a new timer, *not* a new sensing tap.

| Signal | Source | → Breath Light property | Timing |
|---|---|---|---|
| **VimsTier 1–10** (the spec-blessed "single best comfort-state scalar," tech map §1 & §5.8) | `state.VimsTier` (already on `SenseState`) | → **hue**: maps to comfort tokens — tier ≤5 `ComfortSteady #2F7D6E`, 6–7 `ComfortRising #9A6B22`, 8–10 `ComfortHigh #B65C4E` (the *exact* 3-band `TierColorPalette` mapping, D2 — no new band, no copy-paste lookup) | hue lerps over `Dur.slow` 280ms on band change |
| **VimsTier** | same | → **breath period**: Steady ~5.5s, Rising ~6.5s, High ~7.5s (calmer = slower exhale; strain *slows* breathing, the opposite of an alarm) | period eased on change |
| **`LastNormalizedSpike` 0–1** (built, today only feeds the chatbot bubble — tech map §5.5, *unused by any visual*) | `PressureSpikeDetector:70` | → **one single slow swell** of the light's max radius (≈14px), scaled by spike intensity, then settle | one ≥600ms swell, hard cooldown = the detector's own 30s (`PressureSpikeDetector:22`) — structurally cannot strobe |
| **Reduce-Motion** (`bool ReduceMotion = manualToggle OR NOT SystemParameters.ClientAreaAnimation`) | new app token (`Motion.xaml`, spec §3.4) | → breathing **stops**; light becomes a **static** small dot whose *color* still tracks tier | instant; **the clinical mask cross-fade is exempt and still eases (A4)** |

The Breath Light's animation runs on the existing `CompositionTarget.Rendering` lerp loop (15ms half-life, `OverlayWindow.cs:519`) so it adds no new full-monitor WGC readback — the one thing the perf envelope says we *cannot* afford (tech map §4). It is a single `Ellipse` with an animated `RadiusX/Y` + `Fill` brush; in-place brush mutation, no per-tick allocation, mirroring the existing mask-brush discipline (`:401`).

### Loop 3 — Optional gaze centering (only if user already opted in; OFF by default)
If — and only if — head-tracking is already enabled (`AppConfig.HeadTrackingEnabled=false` by default, B1), Focus Mode inherits the existing gaze-follow with **zero new privacy surface**: `cx,cy` drive mask center via the existing `UpdateHeadGazeCenter` 165 Hz path (`OverlayWindow.cs:542`), Balanced clamp `[0.10,0.90]` intact (`:564`). Focus Mode does **not** unlock FullFollow, does **not** add a webcam preview, does **not** request landmarks (B3). For vestibular-diagnosed users the escape-hatch stays force-disabled inside Focus Mode exactly as outside it (`OverlayWindow.cs:486`, A3).

---

## 4. "Use Freely With Actual Benefit"

**The effort is one tap, then zero.** The entire interaction budget is: tap "Enter Focus Mode" (or a chord) → watch → tap Esc. Between those, the user does *nothing*. There are no sliders, no per-scene adjustments, no dismissing pop-ups (the Card's 60s-idle collapse and proposal interrupts are *suppressed* in Focus Mode — see safety note). The protection is **fully autonomous**: the same 15 Hz sense→integrate→tier→mask loop that protects on the dashboard protects here, untouched.

**The benefit is real, not theatrical.** This is the critical honesty point demanded by the guardrails: Focus Mode does **not** replace the mask with a "purely-aesthetic vignette" (explicitly forbidden, A2). The thing dimming her periphery *is* the clinical mask at its engine-commanded depth. She is getting exactly the VIMS protection she'd get anywhere in the app — the mode just removes everything that *isn't* protection from her field of view, which is itself therapeutic for an overstimulated user.

**The benefit is made felt three ways, none of them telemetry:**
1. **Continuously, pre-consciously** — the Breath Light is an ambient "I've got you" presence at the edge of vision. Its *slowing* under strain is a co-regulation cue (slow breath = parasympathetic calm), so the app's hardest-working moment *feels* like the calmest.
2. **In the moment of relief** — she physically does not get sick on content that used to force a pause within 90 seconds. That is the benefit, and it needs no UI.
3. **Honestly summarized on exit** — one `Type.Body` line ("protected you for 12 minutes, peak strain: Rising"), and over weeks the plain-language habituation line. No `Optical Flow Index`, no `FMS`, no `VIMS Pressure: 247` (retiring exactly the telemetry headline at `MainWindow.xaml:79-84`).

**The compounding benefit:** because the mode makes intense content *usable*, Lena uses the app daily instead of avoiding triggers. That daily, monitored, calm-biased usage is precisely the contiguous-calm fuel the `HabituationEngine` needs (`HabituationProfile.cs:138`) — so near-zero effort today *buys* widening tolerance over weeks, persisted and sacred (D3).

---

## 5. How It Embodies Calm

- **One idea per screen, taken to its limit.** Focus Mode is the purest possible expression of the Clarity principle: the screen contains *one* idea — "you are protected" — rendered as the field plus one light. Everything else is gone.
- **Deference, literally.** The chrome doesn't just quiet down; it *leaves*. Content (her video) and her comfort state are the only things present. This is the North Star — "content is the user comfort state" — made literal.
- **Depth via space and a single elevation, zero saturation.** The Breath Light is the *only* added pixel, low-opacity, desaturated comfort tokens only. No `#024AD8` royal blue, no `#E54747` alarm red anywhere — strain reads as muted clay `ComfortHigh #B65C4E`, the loudest the app ever gets.
- **Rationed, switchable motion.** Exactly one decorative motion exists (the breath), it is slow (5.5–7.5s), it *slows* under strain rather than speeding up, and it is fully killed by Reduce-Motion — while the safety-critical mask cross-fade stays eased per the `Dur.overlay` safety token (A4). Motion is a feature here, but a *down-regulating* one.
- **Plain language, never engineering.** Entry copy ("Watching something intense?"), exit copy ("protected you for 12 minutes"), strain language ("Rising / High") — body ≥14px, AAA where feasible. The telemetry vocabulary never appears.

---

## 6. Feasibility

| Piece | Exists today? | Build | Effort |
|---|---|---|---|
| FOV radial mask at engine-commanded depth | **YES**, fully (`OverlayWindow.ApplyFovMask:351`, table `:54-66`) | none — reuse read-only | — |
| Click-through topmost transparent overlay window | **YES** (`OverlayWindow`, `IsHitTestVisible=False`) | the Focus surface *is* the existing overlay, restyled — must register its rect via `SetMonitorExcludeRect` on show (C2) | S |
| 165 Hz decoupled render loop for smooth animation | **YES** (`CompositionTarget.Rendering`, `MainWindow.xaml.cs:446/499/519`) | reuse — add Breath Light lerp to it | S |
| VimsTier comfort scalar on `SenseState` | **YES** (`UpdateVimsTier:897`) | read-only consume | — |
| `LastNormalizedSpike` 0–1 intensity | **YES**, built but visually unused (tech map §5.5) | wire to one light swell | S |
| 3-band comfort hue mapping | **YES** (`TierColorPalette`) — must re-skin to comfort tokens (D2) | consume same lookup, no new band | S |
| **Breath Light actuator** (the one new visual) | **NO** | new: single animated `Ellipse`, hue/period from VimsTier, swell from spike, in-place brush mutation, Reduce-Motion aware | **M** |
| **Focus Mode state + enter/exit choreography** (fade chrome out/in over `Dur.overlay`, suppress Card auto-expand & proposals while active, restore on Esc, persist "was in focus" for crash-resume) | **NO** | new mode controller; must NOT touch tier mapping, mute semantics, or proposal lifecycle (C5/D4) — only *defer* surfacing them | **M** |
| Reduce-Motion master switch + `Dur.ui` dynamic resource | **NO** (0 motion-pref checks today) | build per spec §3.4 (shared with whole rebrand, not Focus-specific) | M (shared cost) |
| Global enter/exit chord | partial (Ctrl-double-tap exists for escape-hatch, `MainWindow.xaml.cs:390`) | add a distinct, non-conflicting chord | S |
| Calibration vestibular flag → Focus protection bias | flag **exists** (`CalibrationProcessor.cs:95-96`); reading it to bias Focus is new | small read-side hook | S |

**Honest overall: M.** No new sensing, no new clinical actuator, no perf risk (rides the existing cheap render path, adds zero WGC readbacks). The real work is (a) the Breath Light component and (b) the enter/exit state choreography that *defers* existing chrome/proposals without altering their locked behavior. The single biggest correctness risk is **not** rendering — it's making sure suppressing the Card/proposals in Focus Mode doesn't violate D4 (mute is band-scoped/in-memory, user-initiated open must always work, proposals must still expire correctly underneath). That's a state-discipline problem, not a graphics one.

---

## 7. VIMS-Safety & Privacy Risks → Mitigations

| Risk | Mitigation |
|---|---|
| **Full-screen mode read as "zen mode that clears the mask"** (explicitly rejected concept, cross-cutting note) | Focus Mode *deepens nothing and lightens nothing* — it shows the engine's mask at its commanded depth. There is no "make it lighter" control, no path to drop below the 20% G1 floor (A2). The mode hides chrome, never protection. |
| **Breath Light becomes an alarm** (pulsing/flashing = a trigger) | Light *slows* under strain, never speeds; spike → one ≥600ms swell gated by the detector's own 30s cooldown (`PressureSpikeDetector:22`) so it *structurally cannot strobe*; killed entirely by Reduce-Motion. No flashing/pulsing, mirroring the bar's spec (`PersistentBarWindow.cs:16`). |
| **Brightness snap on tier change in a chrome-free view feels worse** (nothing else to look at) | The clinical mask cross-fade (`MaskSmoothingFactor=0.25`, ~250ms) is *exempt from Reduce-Motion* and always eased (A4) — Focus Mode changes nothing here; it can only make the existing smoothing *more* visible, never remove it. |
| **Full-screen overlay self-feeds OFI** (the overlay's own dimming captured as motion) | The Focus surface registers its physical-pixel rect via `WgcSensingProvider.SetMonitorExcludeRect` on show and clears on exit (C2, `POPUP-OVERLAY.md §3`) — same contract every on-monitor window already follows. |
| **Suppressing pop-ups hides a safety intercept** (a blocked-proposal ⚠ or crisis surface) | Focus Mode defers *proactive* cards only. The crisis safety-floor (A1) and apply-time block-reason ⚠ (A5) are **never** suppressed — if a safety surface must show, Focus Mode yields to it (fades chrome back enough to present it, plain and instant per A1). Proposals still expire correctly underneath (D4); they're just not surfaced until exit. |
| **Vestibular user invited to override protection** | Escape-hatch stays force-disabled in Focus Mode (`OverlayWindow.cs:486`, A3); no "tap to clear" CTA exists anywhere in the mode. |
| **Webcam framed as the headline of an "immersive" mode** | Focus Mode adds **zero** new webcam surface. Gaze-follow only inherits if *already* opted-in (B1); no preview, no face-mesh, no FullFollow unlock, no landmark request (B3). The mode is fully functional and fully protective with the webcam OFF — its default state. |
| **Crash leaves user stuck in chrome-free mode** | Persist a "was in Focus" flag; on relaunch, restore to the *dashboard* (chrome present) by default, not back into Focus — fail safe toward more chrome, not less. Esc always exits; exit is never gated behind a proposal or animation. |

---

## 8. Why Someone Would Love It

Because for the first time, a sensory-sensitive person can press **one button** and watch the exact content that used to make them sick — and the screen visibly, honestly *has their back* without shouting about it. It is the rare "accessibility feature" that feels like a **luxury**: the calmest, most cinematic way to watch anything, where the app's hardest-working moment is also its most soothing (the protection deepens while the comfort light's breath *slows*). It asks for nothing — no tuning, no reading numbers, no decisions — and gives back the thing these users have quietly given up: the ability to watch intense, beautiful, fast things on purpose. And every minute they spend in it is a minute the habituation engine uses to make them *need it a little less next time* — so the mode that protects them today is also, invisibly, the mode that slowly sets them free.

---

*Concept grounds every actuator in locked capabilities: FOV mask (`OverlayWindow.xaml.cs:351/54-66`), 165 Hz render path (`MainWindow.xaml.cs:446/499/519`), VimsTier scalar (`MockInferenceEngine.cs:897`), spike intensity (`PressureSpikeDetector.cs:70`), gaze path (`OverlayWindow.cs:542/564`). It adds exactly one new actuator (Breath Light) on the existing cheap render loop, introduces zero new sensing taps and zero new WGC readbacks, and scores clean against every A-D HARD-STOP: no mask weakening (A2), no escape-hatch override (A3), no motion-snap (A4), no suppressed safety surfaces (A1/A5), no new webcam surface (B1/B3), world-locked grid untouched (C1), mask-rect registration honored (C2), AppId/tests/tier-mapping preserved (C3/C5/D2), habituation progress sacred (D3).*

---

# Adversarial Verdicts

### The Invisible Guardian
- Feasibility: partial | Effort: M
- VIMS-safety: Low VIMS risk by construction, and arguably the safest of any "immersive" framing because it adds ZERO new actuators and ZERO new direct alpha/brightness writes. All mask motion still flows through the verified eased path (MaskSmoothingFactor=0.25, ~250ms, OverlayWindow.xaml.cs:77) — no snapping (A4 respected). The spike whisper modulates only card COPY via LastNormalizedSpike, never mask motion (verified PressureSpikeDetector.cs:34,70). Three residual VIMS-adjacent concerns: (1) The whisper card itself is on-screen motion (220ms fade-in) that is NOT excluded from OFI unless C2 SetMonitorExcludeRect is honored — the concept names this but it is a build requirement, not free. (2) "Protection arrives before the conscious ugh" oversells onset: verified pipeline is ~133ms median-tap + 2.0s warm-up suppression + 250ms ease; the very first motion burst after a cold/idle period can be partly suppressed, so the felt promise must not be marketed as pre-emptive in the warm-up window. (3) Making the dashboard opt-in must not weaken the user's ability to CONFIRM protection is live — for a nauseated user, "is it even working?" is itself an anxiety/strain trigger; the ambient bar + tray tint must remain the always-on liveness proof.
- Constraint check: No HARD-STOP violation found; the concept is explicitly built to respect A-D and even pre-rejects the tempting Headroom items (webcam luma B1/B3, escape-hatch-as-CTA A3, head-tracking default). Two SOFT issues to fix in spec, not blockers: (a) The "daily passive use is genuinely invisible, mask L1=0.00" claim is FALSE when PersistentOverlayEnabled is on — verified ResolveDisplayedTier (MainViewModel.cs:819-822) floors the displayed tier to PersistentOverlayBaselineTier=4 (~43% alpha) regardless of pressure. The "invisible default" only holds with that toggle OFF; the concept must state which is the default and not promise a clear screen to a user who has the always-on floor enabled. (b) C5 risk: suppressing the auto-opening dashboard and making it opt-in must keep MainWindow instantiable and not regress MainWindowSmokeTests / the toggle-behavior matrix — demoting the console is fine, but the dead "first-run auto-show" path and any test that asserts the dashboard appears must be reconciled. (c) D3 is respected (progress whisper is read-only of _offset; verified Reset() at HabituationEngine.cs:433 zeroes _offset while the separate transient path at :200 preserves it) — but the spec MUST forbid the whisper from ever offering a "reset/start over" action, which it correctly states.
- Privacy/Perf: Privacy: strong. Surfaces FEWER signals, not more; explicitly keeps head-tracking OFF, adds no luma consumer, no preview, no new logs. Whisper copy is templated from a scalar (LastNormalizedSpike), so no PII path and no new non-whitelisted log — B2/B3 respected. One watch item: the monthly progress whisper reads HabituationEngine offset, which is health-adjacent state; the copy must stay generic ("widened your clear zone") and not surface verbatim Q1-Q5 symptom self-reports that live in habituation_profile.json. Performance: excellent — adds no new full-monitor WGC readback (the verified iGPU ceiling) and no synchronous LLM on the render path. It is net subtractive (fewer windows shown), so it can only reduce UI-thread load. The only new on-screen surfaces are the ~3 rationed cards, each cheap and infrequent.
- Standout: It is the only concept that achieves "Calm" by SUBTRACTION rather than by adding a calm surface, and it does so almost entirely on existing, verified real-time paths with no engine algorithm edits and no new actuator — making it the lowest-clinical-risk, lowest-perf-cost immersive option. It also correctly and proactively fences off every dangerous Headroom temptation.
- Fatal flaw: No fatal flaw; the biggest real risk is a trust/legibility failure, not a safety one: by rationing feedback to "twice a month," a silent failure mode (sidecar/engine dead, WGC watchdog tripped, mask never engaging) becomes INVISIBLE to the user — the concept optimizes for "success = absence," which makes "failure" and "working" look identical to an anxious user. The mitigation (always-on bar + tray tint + opt-in dashboard) exists but must be elevated from afterthought to a first-class, explicitly-tested liveness contract.
- Verdict: adopt-with-changes — adopt as the default product posture, but (1) make the always-on liveness signal (bar + tray + a self-check affordance) a tested first-class contract so silent failure is detectable, (2) correct the "invisible" copy to account for PersistentOverlayEnabled flooring to tier 4, (3) hand engineering the C2 SetMonitorExcludeRect contract for all three whisper cards, and (4) soften the "protection arrives before you feel it" promise to respect the 2.0s warm-up + 133ms onset reality.

### The Living Calm Field
- Feasibility: partial | Effort: M
- VIMS-safety: Two of three loops are clinically safe and additive: Loop A (strain) is the unmodified engine; Loop B (gaze) reuses the existing 165 Hz path with no head_tracker.py change. The NEW Loop C "Calm-Breath" is the VIMS risk and its central safety claim is FALSE as written. The concept asserts a radius-only breath "never touches alpha/brightness" so it "cannot create a luminance trigger." Verified against code this is wrong: the breath modulates _fovBrush.RadiusX/Y, which slides the smoothstep ramp (rampStart 0.35 -> rampEnd 0.95, OverlayWindow.xaml.cs:645-661). Any fixed peripheral pixel sitting on that ramp band sees its effective alpha oscillate at the breath period — i.e. it IS a low-amplitude peripheral LUMINANCE modulation, not a pure edge drift. At 0.6% radius / 11 s / 0.09 Hz the per-pixel luminance delta is tiny and the temporal frequency is an order of magnitude below the ~0.2–0.4 Hz vection-relevant band, so the residual risk is low — but the safety rationale must be corrected and the amplitude must be justified in luminance terms (delta-L per pixel on the ramp), not hand-waved as "spatial only." Mitigations the concept already states are sound and necessary: amplitude -> 0 the instant tier > floor; first thing Reduce-Motion kills; ships perfectly static as the fallback. Net: keep the field; treat the breath as research-needed (measure actual peripheral delta-L and grid-hole travel under UVHS) before it is anything but OFF-by-default and Reduce-Motion-gated.
- Constraint check: No HARD-STOP is outright violated, but two items are grazed by the breath and must be fixed before adopt: (1) C1 world-lock — the breath modulates _fovBrush.Radius, and the grid OpacityMask Center/Radius is MIRRORED to the FOV brush (OverlayWindow.xaml.cs:608-617). So a radius breath drags the grid's transparency hole (and thus the visible/feathered grid lines near the aperture edge) in and out every 11 s. The anchor grid's stable static reference frame is its anti-vection value (C1, Decision 2); making its transparency window breathe is contrary to world-lock intent even if cell positions stay fixed. The breath must either (a) drive a dedicated radius channel that does NOT propagate to _gridOpacityMaskBrush, or (b) be explicitly scoped to FOV-only with the grid hole pinned — and that decoupling is an engineering proposal, not a XAML tweak (C5/A2: depth/geometry are engine-owned). (2) A2 — the 20% G1 mask floor and the engine alpha table must remain the authority; the breath touches geometry adjacent to depth, so QA must confirm the breath cannot push effective peripheral opacity below the floor at the ramp inner edge. Everything else checks out: B1 head-tracking stays OFF/unchecked/honestly-labeled and the field works camera-free (confirmed: CompositionTarget.Rendering is subscribed UNCONDITIONALLY at MainWindow.xaml.cs:452, ReadLatest returns null with active=false when off, so the path genuinely runs in the 95% case); B3 no preview/face-mesh, luma stays unconsumed (respects C4); C2 new on-monitor surfaces carry SetMonitorExcludeRect; C3 AppId untouched; C4 sidecar core untouched; D2 status triple-coded via TierColorPalette only; D3 _offset sacred, Reset split from Delete; A1/A5 crisis + block-reason untouched. The token work correctly avoids App.Resources (confirmed empty with an explicit smoke-test-fragility comment, App.xaml:5-10; tests MainWindowSmokeTests/PersistentOverlayTests pin this).
- Privacy/Perf: Privacy: clean. The field is fully functional without a camera, opt-in stays unchecked with restart + local-only caption, no preview/landmark/blendshape surface, luma stays unconsumed — all B1/B3/C4 invariants honored, no new PII log, no analytics beacon. Perf: the 165 Hz CompositionTarget.Rendering path already exists and is cheap (~0.17% core, confirmed at MainWindow.xaml.cs:446 and unconditionally subscribed at :452), so adding a bounded sin on the radius costs near-nothing AND adds no new full-monitor WGC readback (the real iGPU ceiling). One perf caveat to verify: the breath mutating _fovBrush.Radius every frame will defeat the existing _lastFovBrushKey "skip mutation when unchanged" optimization and force a brush re-render each frame even at rest — currently at rest the brush is static and cheap; this is acceptable on the proven path but should be measured on the iGPU baseline (m5.1-c-perf-isolated.json) since "at rest" is the 95% state where the machine should be idlest.
- Standout: The reframe is genuinely strong and almost entirely free: it re-presents the ALREADY-SHIPPING clinical behavior (always-on resting floor at tier 4 / ~43% alpha, confirmed ResolveDisplayedTier MainViewModel.cs:819; engine-owned mask; world-locked grid; habituation _offset widening) as a calm ambient field the user lives inside, with zero new authority over depth and zero engine-math change. The "your calm field has grown ~18% wider than three weeks ago" line turns invisible therapeutic _offset progress into honest, motivating, non-gamified feedback — a rare, high-value accessibility-software property — and the effort-floor-is-zero passive protection is real, not aspirational.
- Fatal flaw: No fatal flaw — but the breath's headline safety claim ("radius-only, never a luminance trigger, world-lock-safe") is factually wrong against the code: radius modulation slides the smoothstep ramp (peripheral luminance oscillates) and the mirrored grid OpacityMask makes the world-locked grid's transparency hole breathe. Low residual clinical risk at the stated bounds, but the rationale and the grid coupling must be fixed, and the breath must ship OFF/static-fallback pending a UVHS luminance + grid-travel measurement.
- Verdict: adopt-with-changes — Adopt the full presentation layer (Calm tokens via view-local merge, dashboard de-telemetry, Status.Chip triple-coding + AutomationProperties, Scale.Segmented de-dup, first-run "watch the edges" reframe, weekly growth line) now as an M-effort visual refactor that touches no HARD-STOP. SPLIT OFF the Calm-Breath loop: do not ship it on by default. Required before the breath is anything but a Reduce-Motion-gated, OFF-by-default experiment: (1) decouple the breath's radius channel from the grid OpacityMask so the world-locked grid hole does NOT breathe (C1), as an engineering proposal not a XAML edit; (2) re-state and quantify the safety case in luminance terms (per-pixel delta-L on the smoothstep ramp), since the "never touches brightness" claim is false; (3) UVHS visual hard-stop measuring actual peripheral luminance oscillation + grid travel + floor-compliance at the ramp inner edge before any tag (C6). The concept's own escape valve — "ship the field perfectly static, breath is a nice not a need" — is the correct default; take it. Field: adopt. Breath: blend-only/defer until measured.

### The Companion That Learns You — protector that earns its own retirement (weekly comfort check-ins + trigger-shape learning + habituation "stepping back" UI)
- Feasibility: partial | Effort: L
- VIMS-safety: see above
- Constraint check: No HARD-STOP (A/B/C) violation found by intent — the concept is unusually guardrail-literate (correctly preserves AppId C3, head_tracker core C4, OFF-by-default webcam B1/B3, no new on-monitor window C2, SafetyFloor-first A1, never-zero _offset D3, TierColorPalette-only D2). BUT three substantive problems: (1) PRIVACY/B2 SOFT-VIOLATION — Loop 3 silently persists per-spike "trigger context across days" and surfaces "fast-scrolling pages are your main trigger," which implies capturing foreground app/window context. B2 forbids writing window titles / new analytics logs to non-whitelisted artifacts. The mitigation ("counts spike shapes, not words") does not cover the app-identity/context capture the copy promises. Must either drop app-context attribution or prove the "trigger" label derives only from PII-free OFI-shape + mode scalars. (2) FACTUAL ERROR undermining the core pillar — the concept claims the persistent overlay floor (tier 4) protects "from day 1, whether or not the user opens a card." VERIFIED FALSE: _persistentOverlayEnabled defaults to false (MainViewModel.cs:794, no initializer). The day-1 zero-effort protection guarantee is opt-in, not default. (3) DATA-SOURCE ERROR — Loops 2 and 3 are sourced to the event log, but InMemoryEventLogStore is in-memory, 20-entry capped, lost on every restart (InMemoryEventLogStore.cs:24, EventLogMaxCount=20). It cannot aggregate a week or detect cross-day recurrence. Both new loops need a NEW persisted longitudinal store, which re-opens B2 review and is not the "S/M riding on existing actuators" the effort tier claims.
- Privacy/Perf: Privacy: the framing is good but the implementation as described leaks. A persisted weekly tier-history aggregate is low-risk (a single %-steady scalar is fine on habituation_profile.json). The cross-day trigger-shape store is the problem: to say "fast-scrolling pages are your main trigger" it needs durable, dated, context-tagged spike records — a new behavioral log that B2 governs. Re-scope to PII-free intent/pattern_id tokens + aggregate counters only, no app/window attribution, or drop the trigger-naming copy. Performance: excellent. Adds zero new sensors, zero new WGC readbacks (the only iGPU ceiling), no synchronous LLM on the render path. All new work is ~0 Hz cron-like aggregation + idle-gated card emission. No frame-rate impact; does not touch the 165 Hz CompositionTarget.Rendering path. Performance headroom is not a concern.
- Standout: The "success metric is its own disappearance" thesis is genuinely the strongest Calm-aligned framing of any anti-engagement product stance — it inverts streak/nag patterns and is emotionally right for an exhausted, over-managed cohort. And the read-side-only discipline (no new sensors, no engine-loop edits, no new on-monitor windows, clamped chatbot knobs) is exactly the correct architectural posture; the habituation recession → rarer-check-in cadence is a clean, honest surfacing of real _offset/level state that no current UI exposes.
- Fatal flaw: The two genuinely-new "learning" loops (weekly %-steady, cross-day trigger recurrence) are sourced to an in-memory, 20-entry, restart-volatile event store and cannot function as written. Making them work requires a new persisted longitudinal log, which (a) re-opens the B2 privacy review the concept claims to sidestep and (b) breaks the stated "no new log, whitelist untouched, M effort" feasibility. Combined with the verified-false "protected by default on day 1" claim (floor is opt-in/off), the concept's two headline promises — "zero-effort benefit from minute one" and "it learns your triggers over weeks" — both rest on misread infrastructure.
- Verdict: adopt-with-changes — keep the "stepping-back/needs-it-less" framing and the habituation-recession cadence (Loop 4) which ride real persisted state honestly; but before adopt: (1) fix the day-1 claim — either default the persistent floor on or stop promising default protection; (2) re-architect Loops 2/3 onto a new explicitly PII-scrubbed persisted aggregate (single %-steady scalar + pattern_id counters), routed through B2 review, NOT the in-memory event log, and drop app/window-context trigger naming; (3) ghost-ring must be a static Shape, no animated radius; (4) re-tier effort to L (new persisted store + privacy review), not M.

### JUST TALK — The Chatbot Is the Front Door
- Feasibility: partial | Effort: M
- VIMS-safety: LOW residual risk; the concept is unusually safe-by-construction and the mask-driving claims verify against real code. Confirmed: A2 mask alpha table at Views/OverlayWindow.xaml.cs:54-66 (L1 0.00 -> L10 0.92, cool tint from L7) is exactly as cited; fov accumulator clamp -60..0 and shader [0,1] clamps are real; mask cross-fade is the eased MaskSmoothingFactor=0.25 path. The LLM literally cannot snap brightness or drive opacity below the engine floor because deltas are clamped before rendering and the render path is the always-eased cross-fade. A4 (no brightness snapping) is honored. ONE genuine VIMS concern the concept under-states: the in-card chatbot lets a user iteratively press 'Still too much' to deepen the mask. Repeatedly DEEPENING is clinically safe (more protection). But the deterministic-lexicon fallback maps 'more open'/'too dark' to mask-LIGHTENING deltas instantly with NO LLM in the loop; the safety net here is solely the engine clamp + G1 20% floor + vestibular cap, not the LLM's caution. That is acceptable ONLY if the lexicon-fallback path is verified to respect the same clamps as the LLM path — this must be a test assertion, not an assumption. Recommend an explicit regression test: 'lexicon-fallback lighten request cannot push effective alpha below floor for a vestibular profile.'
- Constraint check: No HARD-STOP violations found; the concept actively designs to A1-A5/B1-B3/C/D. Caveats: (1) A1 dependency risk — SafetyFloor.cs does NOT yet exist in the tree (confirmed: glob returns no file); it is a planned Wave-1 artifact. The whole 'chatbot is the front door' thesis makes EVERY text entry a crisis-interception surface, so JUST TALK cannot ship before SafetyFloor.cs lands and is the line-1 gate of SendAsync. The concept correctly names this as a prerequisite but should flag it as a HARD blocking dependency, not 'exists.' (2) B2 sharpened — making the chatbot the primary surface multiplies free-text volume; the calibration free-text already persists verbatim in habituation_profile.json (Medium-high health-adjacent per the state map) and adjustments.jsonl stores raw utterances. Routing MORE of the UX through free text increases the verbatim-PII surface. Mitigation must be explicit: only whitelisted logs (calibration_history.jsonl, chat_history.jsonl) carry content; the 'narration/receipt' layer logs pattern_id only. (3) D2 color-only is correctly addressed via Status.Chip. No A2/A3/B1/B3/C3/C4/D3 violations — all verified safe against code.
- Privacy/Perf: PRIVACY: net-neutral-to-slightly-elevated. No webcam (B1 honored — head-tracking stays OFF/headline-free), no landmarks/frames (B3 honored). The elevation is the larger free-text footprint (see constraint check). Manageable with the existing whitelist discipline. PERFORMANCE: clean. No new sensor, no new WGC readback (the iGPU ceiling is untouched), no new actuator, no new persisted data class. Mask changes ride the existing 165 Hz CompositionTarget.Rendering path which is ~0.17% of one core. LLM round-trip is seconds but isolated to the llama-server process and the instant deterministic lexicon fallback covers latency — verified the system prompt and fallback architecture exist. The only perf-adjacent risk is conversational chattiness inviting more frequent LLM calls, but those are user-initiated and off the render path.
- Standout: Feasibility is BETTER than the concept claims, which is rare for an immersive pitch. The 'hardest must-build M piece' (free-text -> structured CalibrationResult) is ALREADY BUILT and unit-tested: CalibrationLlmDeltaParser.cs parses an LLM [CALIBRATION_DELTA] block carrying q1_segment + q1_freetext + q2_symptoms + q5_risk_factors directly into the typed CalibrationResponses that CalibrationProcessor.Process consumes, and a CalibrationChatbotService is referenced. So JUST TALK is mostly a WIRING + DESIGN-SYSTEM-MIGRATION job over existing logic, not new inference. Combined with C5's explicit allowance ('only the logic of chatbot can be redefined'), this is the lowest-friction immersive concept against the hard constraints: it redefines exactly the one surface the user said is redefinable, touches no sensor/actuator/AppId/sidecar, and directly dissolves the headline audit defect (engineering telemetry as the dashboard headline) by making one calm conversation the home.
- Fatal flaw: No fatal flaw. The single biggest real risk is a CALIBRATION-QUALITY regression the concept glosses, not a safety/constraint break: the Q1-vs-sensor-peak alignment heuristic (CalibrationProcessor.cs:328-345) requires a DISCRETE UncomfortableSegment enum (Start/Middle/End/None), and DetectQ1SignalConflict (:146-158) explicitly DROPS calibration confidence to Low when free-text names a symptom but the segment resolves to None. One free-text sentence ('the spinning bits at the end made my head fuzzy') must be reliably parsed into Q1Segment=End for the vestibular-dominance + alignment logic to fire correctly; a fuzzier sentence yields segment=None + conflict -> Low confidence -> weaker personalization on the exact users (vestibular) who most need a correct cap. The concept assumes lexicon mapping 'just works'; in reality the safety-critical vestibular cap fires on the structured 'vestibular' flag from Q5 risk factors, NOT on free text, so the CAP itself is safe — but the personalization quality degrades silently. Mitigation: keep a minimal structured fallback (at least the Q5 risk-factor checkbox set and a coarse segment pick) behind the free-text field so confidence never silently collapses to Low.
- Verdict: adopt-with-changes — strongest and most constraint-aligned concept reviewed; ship it, but gate on: (1) SafetyFloor.cs landing as the verified line-1 gate before any 'front door' framing; (2) a regression test proving the deterministic lexicon-fallback lighten path respects the G1 floor + vestibular cap identically to the LLM path; (3) retaining a minimal structured calibration fallback (Q5 risk checkboxes + coarse segment) behind the free-text field so vestibular personalization confidence never silently collapses to Low; (4) explicit B2 logging contract (receipts log pattern_id only; verbatim text only in the two whitelisted logs). Do NOT fully retire the diagnostics console — keep it behind an Advanced view (the concept already proposes this) so mask-state remains inspectable. With these four changes it is the recommended primary direction for the Alpha-1 rebrand.

### The Comfort Ritual — Settling / Soften / Reflection trust arc
- Feasibility: partial | Effort: L
- VIMS-safety: Low residual risk after one mandatory fix. The breathing ring is the only new decorative motion: a 19s 4-7-8 cycle (~0.05 Hz) is far below any flicker/vection threshold, single low-chroma, small, centered, Reduce-Motion-collapsible, and rides the existing 165 Hz CompositionTarget.Rendering path (~0.17% core, zero new WGC readbacks). It does not move the mask. HARD REQUIREMENT: the ring must render strictly inside the resting clear zone and never animate scale large enough to graze the gradient feather edge, or it becomes peripheral motion under a dimming gradient — the exact stimulus the product exists to suppress. With head-tracking OFF (default) the FOV center is fixed (0.5,0.5), so "inside the cleared FOV" is well-defined; but a designer must NOT let the ring follow gaze when head-tracking is ON (it would put moving content in a tracked periphery). Soften narrows the clear zone (deepens protection) and is clamped to floor 15 — it can never lighten below the engine table or the G1 20% floor, verified at MockInferenceEngine.cs:296,830. Mask cross-fade (MaskSmoothingFactor=0.25) and Soften's 320ms ease keep A4 (no brightness snapping) intact.
- Constraint check: No HARD-STOP (A/B/C) violation found; protection stays automatic and engine-owned, head-tracking stays OFF/honest (B1), all Reflection counters are volatile in-memory so no new PII log (B2), no webcam preview / face-mesh requested (B3), AppId untouched (C3), sidecar untouched (C4), crisis SafetyFloor path not touched (A1). TWO real D-tier semantic problems the concept understates: (1) D3/habituation conflict — Soften routes through ApplyUserFeedback(-8), and the live code (MockInferenceEngine.cs:299-302) fires _habituation.NotifyDiscomfort on EVERY negative delta, backing off habituation. So each Soften tap actively pushes tolerance DOWN while the same session's Reflection card claims tolerance is "creeping up / steadier today." A user tapping the offered Soften gesture repeatedly would erode the very _offset the Reflection celebrates. The concept's headline ("she is actually getting better") can be contradicted by its own primary interaction. Soften must either (a) use a delta path that does NOT call NotifyDiscomfort (new engine seam = behavior change, must be PR-flagged under C5, not a pure copy/token refactor as claimed), or (b) the Reflection copy must not assert improvement on high-Soften sessions. (2) D4 — "12 minutes of motion you didn't absorb" is a fabricated benefit figure: mask-active duration is NOT motion-absorbed time, and stating a specific minutes number risks reading as a quasi-clinical efficacy claim; keep it qualitative or clearly framed as "edges dimmed for ~N min," not "motion you didn't absorb."
- Privacy/Perf: Privacy: clean. Reflection aggregates only volatile session stats (MainViewModel.cs:1391 ring) plus already-persisted derived habituation level — no new file under %LocalAppData%\SenseEase, no PII, no new analytics beacon. Performance: the breathing ring on the existing CompositionTarget.Rendering path is the proven cheap channel and adds no WGC readbacks (the iGPU ceiling). Soften is instant (no LLM). The only perf note: ensure the ring's per-frame brush mutation is in-place (mirror the existing no-per-tick-allocation pattern) so it does not regress the render loop on low-end iGPUs.
- Standout: Directly solves the VIMS-app retention paradox ("when it works, you feel nothing, so users churn") by making invisible protection legible — and does so almost entirely by reusing existing signals/actuators with no new sensing, no schema change, no sidecar edit. The first-run "rehearse the mask once, on purpose" beat genuinely de-risks the most startling event (first unexpected dim) and is clinically sound.
- Fatal flaw: Not fatal, but the single biggest flaw: the Soften gesture's underlying path (ApplyUserFeedback) silently backs off habituation via NotifyDiscomfort, so the concept's two emotional payoffs — "soften when it spikes" and "you're getting steadier" — are mechanically in tension and can mislead the user about their own progress unless the engine seam is changed (a flagged behavior change, not the "Tier S copy wiring" the concept claims).
- Verdict: adopt-with-changes — strong, safe, on-mandate trust arc; adopt the Settling rehearsal and Reflection legibility wholesale, but (1) re-route or re-scope Soften so it does not erode habituation (flag as a C5 behavior change, do not ship as pure copy refactor), (2) constrain the breathing ring to the resting clear zone and disable it when head-tracking is ON, and (3) drop the fabricated "12 minutes of motion you didn't absorb" figure for honest qualitative copy.

### Immersive Focus Mode — "The Field"
- Feasibility: partial | Effort: L
- VIMS-safety: Two genuine VIMS risks, one of them load-bearing. (1) BREATH-LIGHT SENSOR FEEDBACK (real, under-addressed): the Breath Light is a SenseEase-owned animated element drawn on the captured monitor — it brightens on a 5.5-7.5s cycle and gives a 600ms swell on spike. That swell is on-screen luminance change the WGC pipeline CAN capture (WgcSensingProvider downsamples the full monitor to 160x90 every 66ms), raising OFI -> pressure -> and it can itself trip PressureSpikeDetector.Push (relative +50% over a 3s window, minBaseline 5; cooldown 30s only limits RE-fire, not the first self-induced spike). The concept's "structurally cannot strobe" claim is true for the visual but never addresses this sensor-feedback path: the comfort light can manufacture the very spike it is meant to depict — a self-reinforcing loop, the exact A2/C2 failure the cross-cutting note warns about. POPUP-OVERLAY.md §3 REQUIRES any on-monitor SenseEase element to be exclude-rect-masked, but a moving/breathing ellipse cannot be covered by a static rect without either under-covering (self-feeds) or over-covering a sensing region. (2) Otherwise the concept is genuinely VIMS-cautious: it consumes VimsTier read-only and does NOT weaken the clinical mask (A2 preserved), the mask cross-fade (MaskSmoothingFactor=0.25, ~250ms) stays exempt from Reduce-Motion and never snaps (A4 preserved, verified OverlayWindow.xaml.cs:77 / table :54-66), strain SLOWS the breath rather than speeding it (correct down-regulation direction, no strobe), and a chrome-free view making the existing eased cross-fade MORE visible is benign. Brightness-flicker risk from the breath cycle itself is low (slow, low-opacity, desaturated) PROVIDED it is sensor-excluded — which is unsolved.
- Constraint check: C2 VIOLATION (engineering HARD-STOP, the fatal one): the concept's C2 mitigation — "Focus surface registers its rect via SetMonitorExcludeRect on show" — is incoherent against the verified implementation. WgcSensingProvider holds exactly ONE exclude rect: a single volatile MaskRect? _excludeRect (WgcSensingProvider.cs:130-144), last-writer-wins, NOT a collection. The existing bar/card flow already fights over this single slot (MainWindow.xaml.cs:685-848: card clears its rect on close THEN re-engages the bar's rect, precisely because only one can be active). Focus Mode adds a THIRD claimant (the Focus surface) plus a FOURTH (the moving Breath Light) to a one-slot resource. Worse: a near-full-screen Focus exclude rect would zero most of the 160x90 MAD buffer and BLIND the OFI sensor that drives the mask the whole concept depends on. The concept never reconciles "chrome recedes/leaves" (bar hidden -> its rect must clear) with "Focus registers its rect" with "Breath Light must also be excluded" — three/four mutually-exclusive uses of one slot. RESOLVABLE only by widening the provider to support multiple rects (a Core change touching the locked sensing pipeline — must be flagged as behavior change under C5) OR by deciding the Focus overlay is the existing transparent click-through OverlayWindow which is NOT exclude-masked today (it relies on the noise gate / activity filter instead) and accepting the Breath Light needs its own handling. RENDER-LOOP WIRING ERROR (not a constraint violation but breaks the "S effort / just reuse the loop" claim): the 165 Hz CompositionTarget.Rendering loop is owned by MainWindow's lifecycle — subscribed on MainWindow.Loaded, unsubscribed on MainWindow.Unloaded (MainWindow.xaml.cs:447-468). The concept's "dashboard recedes / user forgets the app exists" choreography, read literally as hiding/closing MainWindow, tears down the very loop the Breath Light rides on AND the head-gaze follow path. The Breath Light must instead live on OverlayWindow's own render path; concept assigns it to a loop it does not own. A1/A3/A5/B1/B3/C1/C3/D2/D3: NO violations found — escape-hatch force-disable for vestibular is correctly preserved (verified OverlayWindow.xaml.cs:486, dips to 0.30 not 0), no new webcam surface, gaze inherits only if already opted-in, world-locked grid untouched, AppId untouched, comfort-token re-skin routes through TierColorPalette with no new band, habituation _offset never reset. D4 is a STRICT risk the concept itself correctly flags (suppressing the Card must not break band-scoped mute / user-initiated open / proposal expiry) but does not fully resolve.
- Privacy/Perf: PRIVACY: clean. Adds zero new webcam surface; gaze-follow inherits ONLY if head-tracking already opted-in (B1 preserved, HeadTrackingEnabled=false default); no preview, no face-mesh, no FullFollow unlock, no landmark request (B3 preserved); no new PII log. The crash-resume "restore to dashboard, not back into Focus" fail-safe is correct. PERF: the concept's headline claim "zero new WGC readbacks, rides the cheap 165 Hz path" is HALF true and half wrong. True: a single in-place-mutated Ellipse on a render-rate lerp is cheap (the existing path is ~0.17% of one core). WRONG/UNADDRESSED: (a) the Breath Light feeds the existing full-monitor WGC readback as new captured motion (sensor cost is unchanged but the SIGNAL is polluted — the actual problem); (b) the render loop it names (CompositionTarget.Rendering on MainWindow) is torn down if the chrome "leaves," so the Light must be re-hosted on OverlayWindow with its own subscription. No raw perf-budget violation, but the "free ride" framing hides the exclude-rect and self-feedback costs.
- Standout: The honesty discipline is exemplary and rare: it refuses the forbidden "zen mode that clears the mask" — the periphery dimming IS the engine-commanded clinical mask at full depth, read-only, with no "make it lighter" control and no path below the 20% G1 floor. The strain-makes-the-breath-SLOWER inversion is a genuinely sophisticated co-regulation insight (parasympathetic cue, the app's hardest-working moment feels calmest) and is the correct anti-alarm direction. It also productively retires the engineering-telemetry headline (MainWindow.xaml:79-84) for plain-language comfort copy, and correctly identifies that daily use of intense content is exactly the contiguous-calm fuel the HabituationEngine needs (120-360s windows) — a real compounding therapeutic benefit, not theater.
- Fatal flaw: The single-slot SetMonitorExcludeRect (one volatile MaskRect?, last-writer-wins, WgcSensingProvider.cs:130) cannot satisfy the concept's own C2 mitigation: the Focus surface, the (to-be-hidden) bar, and the moving/breathing Breath Light are three/four mutually-exclusive claimants on ONE rect — and a full-screen Focus rect would blind the OFI sensor the mask depends on. Compounded by the Breath-Light->OFI->spike-detector self-feedback loop (the comfort light can manufacture the spike it depicts). Both are resolvable but require Core sensing-pipeline changes the concept assumes away, which is why its "M, no perf risk, just reuse" feasibility is overstated to L.
- Verdict: adopt-with-changes — Strong, constraint-literate concept with the right calm/honesty instincts and a real therapeutic loop, but it ships with a hard C2 engineering violation (single-slot exclude rect cannot serve a full-screen Focus surface + hidden bar + a moving Breath Light, and a full-screen rect blinds the OFI sensor) plus an unaddressed Breath-Light->OFI->spike self-feedback risk. Do NOT adopt as written. Adopt the Focus-state choreography + plain-language framing + read-only mask reuse; before any build, REQUIRE engineering to (1) decide the exclude-rect strategy — widen WgcSensingProvider to multiple rects (flagged C5 behavior change) or confirm the Focus overlay reuses the un-masked transparent OverlayWindow and the Breath Light gets explicit anti-feedback handling; (2) prove the Breath Light cannot trip PressureSpikeDetector (exclude its bounding region from MAD, or move it into the OverlayWindow's own non-captured compositing, or cap its luminance delta below the noise gate); (3) re-host the render loop on OverlayWindow, not MainWindow; (4) close the D4 mute/proposal-expiry state-discipline gap. Until (1)-(2) are resolved on paper, this is partial-feasibility, L effort, not the claimed M.

---

# Decision Menu

All load-bearing facts confirmed against code: the L1 alpha is 0.00 (truly transparent only when persistent floor is off), `ApplyUserFeedback(fovDelta<0)` fires `NotifyDiscomfort` (so Soften erodes habituation), `_excludeRect` is single-slot, `SafetyFloor.cs` is unbuilt, and `_persistentOverlayEnabled` defaults false. I have enough verified evidence to write the decision menu.

---

# Immersive-Experience Decision Menu — Alpha-1 "Calm" Rebrand

*Pillar 3 ("immersive concept") is a discussion item, not a settled decision. This section presents the six concepts, their adversarial verdicts, a recommended blend, a phased path, and the open questions that must be resolved with the product owner before any build. Every UI claim is cited to current `main` HEAD.*

## 1. What "immersive" must mean for a calm / VIMS product

For an overstimulated, sometimes-nauseated user, **immersion is not flashy motion, ambient animation, or a "living" interface — those are the exact stimuli the product exists to suppress.** Immersion here is the *absence of friction between the user and relief*: it is **effortless trust** (the user believes, without checking, that protection is live), **felt benefit** (the headache that does not arrive; the intense video they can finally watch), and **the app disappearing into the OS** so the user's own softened desktop becomes the entire interface. The deepest immersive move available to this product is *subtraction* — retiring the engineering-telemetry headline (the `#024AD8` console band at `MainWindow.xaml:114` over 32px `CardValue` numbers at `MainWindow.xaml:79-84` labeled "Optical Flow Index / VIMS Pressure") and replacing five competing surfaces with one coherent, plainly-worded comfort state. An "immersive" concept that achieves calm by *adding* a surface (a breathing field, a comfort light, a moving ring) carries inherent VIMS risk and must clear a higher bar than a concept that achieves calm by *removing* surfaces. The order of authority is fixed: **Clinical safety (A) > Privacy (B) > Engineering locks (C) > Product semantics (D) > visual/brand preference.**

## 2. Comparison table

| Concept | Essence | Effort (verdict) | Feasibility | VIMS-safety | Standout | Top risk |
|---|---|---|---|---|---|---|
| **1. Invisible Guardian** | App disappears; only the softened screen is ever seen; speaks ≤2×/month (spike + monthly progress whisper) | **M** | Partial — net *subtractive* (hide dashboard, make opt-in) | **Lowest of all six.** Zero new actuators, zero new direct alpha writes; whisper modulates card *copy* only via `LastNormalizedSpike`, never mask motion | Achieves Calm by **subtraction**, almost entirely on verified existing paths; pre-fences every dangerous Headroom temptation | **Silent-failure invisibility**: if sensing/mask dies, "working" and "broken" look identical to an anxious user. Needs a *tested* liveness contract |
| **2. Living Calm Field** | Always-on resting dim-field (tier-4 floor) that "breathes" at rest and deepens on strain | **M** (field) / defer (breath) | Partial — field reuses shipping behavior; breath is new | Field safe; **the "Calm-Breath" loop's core safety claim is FALSE** — radius modulation slides the smoothstep ramp = peripheral *luminance* oscillation, and the mirrored grid `OpacityMask` makes the world-locked grid hole breathe (grazes C1) | The "your calm field has grown ~18% wider" reframe of `_offset` progress is honest, high-value, non-gamified | The breath must ship **OFF / static-fallback** pending a UVHS luminance + grid-travel measurement; the field alone is the win |
| **3. Companion That Learns You** | Protector that "earns its retirement": weekly comfort check-ins + cross-day trigger learning + visible stepping-back | **L** (re-tiered up) | Partial — two new loops rest on misread infra | Low VIMS risk, but two headline loops are **architecturally broken as written** | "Success = its own disappearance" is the strongest anti-engagement framing; recession→rarer-check-in cadence honestly surfaces real `_offset`/level state | **Day-1 protection claim is FALSE** (`_persistentOverlayEnabled` defaults false, `MainViewModel.cs:794`); weekly/trigger loops are sourced to a 20-entry in-memory store that **cannot aggregate a week**; trigger-naming grazes B2 |
| **4. JUST TALK** | The chatbot *is* the front door; free-text calibration + situation-aware one-line receipts; dashboard demoted | **M** | **Best of all six** — the "hardest piece" (free-text→`CalibrationResult`) is already built and unit-tested (`CalibrationLlmDeltaParser.cs`) | Low residual; LLM *cannot* snap brightness or breach the floor (deltas clamped −60..0 before the always-eased cross-fade) | Redefines exactly the one surface C5 says is redefinable; directly dissolves the telemetry-headline defect; lowest-friction against hard constraints | **Hard blocking dependency**: `SafetyFloor.cs` does not yet exist (Glob: no file) — every text entry is a crisis surface, so it cannot ship before A1 lands. Plus silent vestibular calibration-confidence degradation if free-text parses to segment=None |
| **5. Comfort Ritual** | Settling rehearsal (first run) + "Soften" gesture (spike) + end-of-session Reflection | **L** | Partial — strong arc, one mechanical contradiction | Low after one fix; breathing ring at ~0.05 Hz is safe **only if** kept strictly inside the resting clear zone and OFF when head-tracking is ON | First-run "rehearse the mask once, on purpose" genuinely de-risks the most startling event (first unexpected dim); solves the VIMS retention paradox | **Soften erodes the progress it celebrates**: `ApplyUserFeedback(fovDelta<0)` fires `_habituation.NotifyDiscomfort` (`MockInferenceEngine.cs:299-302`), so "Soften" backs off `_offset` while the Reflection claims "steadier today" — a flagged C5 behavior change, not copy. Also the "12 minutes of motion you didn't absorb" figure is fabricated |
| **6. Focus Mode "The Field"** | One gesture strips to mask + a single breath-slow comfort light for watching intense content | **L** (overstated as M) | Partial — two unresolved Core problems | Two real risks: **(a)** Breath Light → OFI → spike self-feedback (WGC downsamples the full monitor; the light can manufacture the spike it depicts); **(b)** otherwise VIMS-cautious, strain *slows* the breath (correct anti-alarm direction) | Exemplary honesty — refuses the forbidden "zen mode that clears the mask"; the strain-slows-breath co-regulation insight is sophisticated | **Hard C2 violation**: `_excludeRect` is a single volatile slot (`WgcSensingProvider.cs:130`, last-writer-wins) — Focus surface + hidden bar + moving Breath Light are 3–4 claimants on one slot, and a full-screen Focus rect would *blind the OFI sensor the mask depends on*. Render loop also mis-hosted on MainWindow (torn down when chrome "leaves") |

**Adversarial verdict summary:** all six are *adopt-with-changes*; none is *reject*, none is *adopt-as-written*. Concepts **1** and **4** are the lowest-risk and most constraint-aligned. Concepts **2, 5, 6** each carry exactly one load-bearing flaw that converts a claimed-M feature into a deferred/L item (breath, Soften-erosion, exclude-rect). Concept **3**'s two headline promises both rest on misread infrastructure.

## 3. Recommended direction — "The Quiet Guardian that Learns You"

**Recommendation: do not pick one concept. Fuse the verified-safe core of Concepts 1, 4, 2 (field only), and 3 (framing only) into a single north-star posture, and treat Concepts 2-breath, 5-Soften, and 6 as opt-in / later-phase experiments gated behind their unresolved engineering questions.**

The blended experience, concretely:

**(a) Default posture = Invisible Guardian (Concept 1), by subtraction.** After a ~60s calibration, the app dissolves to tray. There is no auto-opening dashboard. The user's own desktop — periphery softened by the engine-owned mask when strain rises — is the interface. The telemetry console (OFI / VIMS Pressure / Leaky Integrator / FMS / FOV Radius headline) is **demoted, not deleted**, behind a tray "Show details" / Advanced view in `Type.Mono`, satisfying C5 ("cannot break prior works" — `MainWindow` stays instantiable, `MainWindowSmokeTests` reconciled). The resting chrome is the 4dip ambient bar (`PersistentBarWindow`, no pulse per `:16`) and the 3-band tray tint (`IconForTier_Win:147`, never animates per `:23`).

**(b) The front door, when the user *does* engage, = JUST TALK (Concept 4).** A strain moment the user wants to fine-tune is one sentence ("this is rough"), not a settings hunt. Free-text calibration replaces the five hand-copied 40-line `ControlTemplate`s (`CalibrationCardView.xaml:224-353`) with one `Field.Text` mapped through the already-built `CalibrationLlmDeltaParser.cs`. The chatbot is *narrator*, not driver — every applied delta returns one `Type.Body` plain-language receipt. This is the lowest-friction path against the hard constraints because it redefines exactly the one surface C5 permits.

**(c) The strain response = the Living *Field*, breath OFF (Concept 2, de-risked).** Adopt the *presentation* reframe — the always-on resting floor (`PersistentOverlayBaselineTier`, default-4 ≈ 43% alpha at L4, `OverlayWindow.xaml.cs:59`) re-presented as "your calm field" so the mask reads as deliberate dimming, not a glitch — **without** the Calm-Breath loop. The breath's safety rationale is false against code (radius modulation slides the smoothstep ramp = peripheral luminance oscillation; the mirrored grid `OpacityMask` breathes, grazing world-lock C1). Ship the field perfectly static; the breath is a "nice," not a "need."

**(d) The arc = "Learns You / earns its retirement" framing (Concept 3), honest surface only.** Keep the emotionally-correct anti-engagement thesis — *the app's success metric is its own disappearance* — surfaced **only** through the habituation-recession cadence that rides real persisted `_offset`/level state (Loop 4). Drop the cross-day trigger-naming and the day-1-protection claim until a properly PII-scrubbed persisted aggregate replaces the 20-entry in-memory event store.

The four fuse cleanly because they share one spine: **VIMS tier (1-10) is the single comfort scalar** driving mask, bar, and tray in lock-step through one shared table (`TierColorPalette`, D2). Concept 1 removes the chrome around it; Concept 4 lets the user talk to it; Concept 2 reframes its resting state; Concept 3 narrates its long-term recession. None adds a new actuator. None edits the engine loop. The unified product is **"the quietest, most trustworthy app on the machine: it protects silently, speaks plainly only when you speak to it or when something truly matters, reframes its own dimming as a calm field you live inside, and quietly works itself out of a job."**

## 4. Phased path

**Alpha-1 (ship now — pure visual/flow refactor, no HARD-STOP touched, all existing tests green):**
- Calm design-system token layer (`Theme/*.xaml`, view-local merge — **not** `App.xaml Application.Resources`, which is empty by design with a smoke-test-fragility comment; `MainWindowSmokeTests`/`PersistentOverlayTests` pin this).
- Kill ~250 inline hex literals and per-window duplicate styles; de-dup the 5× `CalibrationCardView` template into one `Scale.Segmented`.
- De-telemetry the dashboard: comfort hero in `Type.Display`; OFI/Pressure/FMS/FOV moved to an Advanced view (`Type.Mono`); demote auto-open (Concept 1 subtraction).
- `Status.Chip` triple-coding (icon + label + color) + `AutomationProperties.Name` everywhere, retiring the color-only `Ellipse` dots (`MainWindow.xaml:255,543,579`) and fixing `AutomationProperties = 0`.
- Re-skin the 3 bands to comfort tokens (Steady `#2F7D6E` / Rising `#9A6B22` / High `#B65C4E`) through `TierColorPalette` only — never alarm red `#E54747` (`AlertBtn`, `MainWindow.xaml:65`).
- Concept 2's **static** Living-Field reframe + weekly "your clear zone has grown" line (read-only of `_offset`, D3-safe).
- Reduce-Motion master switch + high-contrast plumbing; the clinical mask cross-fade (`MaskSmoothingFactor=0.25`) stays exempt (A4 safety token).
- Hand engineering the C2 `SetMonitorExcludeRect` engage-on-render / clear-on-close contract for every new on-monitor surface.

**Alpha-2 / gated on prerequisites:**
- **JUST TALK front door (Concept 4)** — gated on (1) `SafetyFloor.cs` landing as the verified line-1 gate of `SendAsync`; (2) a regression test proving the deterministic lexicon-fallback *lighten* path respects the G1 20% floor + vestibular cap identically to the LLM path; (3) a minimal structured calibration fallback (Q5 risk checkboxes + coarse segment) so vestibular confidence never silently collapses to Low.
- **Comfort Ritual Settling rehearsal + Reflection (Concept 5)** — adopt wholesale; but re-route "Soften" so it does not fire `NotifyDiscomfort` (flag as a C5 behavior change, not copy), and drop the fabricated "12 minutes absorbed" figure for honest qualitative copy.
- **Companion cadence (Concept 3, Loop 4 only)** — habituation-recession → rarer check-ins, on real persisted state.

**Later / research-gated (do not commit on paper):**
- **Concept 2 Calm-Breath loop** — only after a UVHS measurement of per-pixel ΔL on the smoothstep ramp + grid-hole travel + floor-compliance, with the breath radius channel decoupled from the grid `OpacityMask` (engineering proposal, not XAML).
- **Concept 6 Focus Mode** — only after engineering resolves the single-slot exclude-rect (widen `WgcSensingProvider` to multiple rects, flagged C5, *or* confirm the Focus overlay reuses the un-masked click-through `OverlayWindow`), proves the Breath Light cannot trip `PressureSpikeDetector`, and re-hosts the render loop on `OverlayWindow` rather than the MainWindow-lifecycle-bound `CompositionTarget.Rendering`.

## 5. Open questions for the user (decide before building)

1. **How invisible vs. how legible?** Concept 1's "speak ≤2×/month" optimizes for calm but makes silent failure invisible to an anxious user — "is it even working?" is itself a strain trigger. **Decision needed:** do we elevate the always-on bar + tray tint + a self-check affordance to a *first-class, explicitly-tested liveness contract*, or accept rarer feedback? (Recommend: tested liveness contract.)

2. **Is the persistent overlay floor ON or OFF by default?** This single toggle decides whether "zero-effort protection from day 1" is true. Today `_persistentOverlayEnabled` defaults **false** (`MainViewModel.cs:794`), so the "always-on field" and "protected on day 1" promises are currently false. **Decision needed:** default the floor ON (delivering the Concept 2/3 promise but always-dimming low-strain users — mitigated by user-adjustable baseline tier 1 = 0% alpha), or keep it OFF and stop promising default protection?

3. **Conversational front door vs. silent guardian as the *primary* posture?** Concepts 1 and 4 are complementary but imply different defaults: does the user mostly never interact (1), or is "just say it" the headline interaction (4)? **Decision needed:** which is the marketed primary, given that 4 is hard-blocked on `SafetyFloor.cs` and 1 ships today.

4. **How do we make benefit legible without a dashboard or fabricated numbers?** Options on the table: the felt absence of nausea (Concept 1); a weekly "% time steady" / "clear zone grown ~18%" line from `_offset` (Concepts 2/3); an end-of-session Reflection (Concept 5). The Reflection's "12 minutes of motion you didn't absorb" is a fabricated efficacy figure and risks a quasi-clinical claim. **Decision needed:** which legibility channel(s), and a hard rule that any number is honestly derived (no "motion absorbed" claims).

5. **Sound / haptics / decorative motion — yes or no?** The breathing ring (Concept 5), Breath Light (Concept 6), and Calm-Breath (Concept 2) all add motion the product otherwise exists to suppress, and each has an unresolved safety question. **Decision needed:** do we permit *any* decorative motion (gated behind Reduce-Motion and a UVHS luminance measurement), or hold the line that the *only* routine motion is the clinical mask cross-fade? (Recommend: no decorative motion in Alpha-1; the field ships static.)

6. **Does "Soften" / any user-tightening gesture count as a discomfort signal?** Today every negative `fov_delta` fires `_habituation.NotifyDiscomfort` (`MockInferenceEngine.cs:299-302`), so a user fine-tuning toward more protection *erodes* the tolerance progress we celebrate. **Decision needed:** is a deliberate "give me more protection right now" gesture a discomfort recurrence (current behavior) or a neutral preference (requires a new engine seam, flagged C5)? This decision is shared by Concepts 4, 5, and 6.

---

*Files cited for current-UI claims: `MainWindow.xaml:65,114,255,543,579` (alarm-red AlertBtn, console band, color-only dots), `MainWindow.xaml:79-84` (telemetry headline), `CalibrationCardView.xaml:224-353` (5× duplicated template), `App.xaml:5-10` (empty Application.Resources, smoke-test-fragile). Verified code facts: `MainViewModel.cs:794,819-822` (persistent floor defaults false), `OverlayWindow.xaml.cs:54-66,77` (alpha table L1=0.00/L10=0.92, MaskSmoothingFactor=0.25), `MockInferenceEngine.cs:292-311` (ApplyUserFeedback fires NotifyDiscomfort on negative delta), `WgcSensingProvider.cs:130,136-148` (single volatile `_excludeRect`, last-writer-wins). `SafetyFloor.cs` confirmed absent from the tree (Glob: no file) — a hard blocking dependency for Concept 4.*
