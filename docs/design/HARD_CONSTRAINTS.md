# SenseEase — Design Hard Constraints & Locked Decisions

<!-- Generated 2026-06-05 by the senseease-rebrand-design workflow (31 agents). Companion to docs/UIUX_UPGRADE_PLAN.md. Draft for review — no code changed. -->

> Generated 2026-06-05 by the senseease-rebrand-design workflow (31 agents). Companion to docs/UIUX_UPGRADE_PLAN.md. Draft for review — no code changed.

I now have decision-grade evidence for all four constraint categories. I have enough verified detail: the escape-hatch dips to 0.30 opacity (not zero) for 3s, force-disabled for vestibular; the mask L1 = 0% alpha (resting) with depth floor governed by tier table; the calibration vestibular cap (`VestibularPersonalPressureScaleCap = 1.0`, `SevereStrikeLimitOverride`); the FOV clamp `[15,100]`; the inline-hex palette evidence; and the LOG/privacy/sidecar/AppId/grid invariants from the docs.

I'll write the guardrail section now.

---

# Immersive-Design Guardrails — Non-Negotiable Invariants & Per-Item Design Implications

Scope: every visual, copy, motion, and "immersive concept" proposal for the Alpha-1 "Calm" rebrand is scored against this checklist. An invariant marked **HARD-STOP** cannot be traded against aesthetics under any circumstances; a violation is a release blocker, not a review comment. Each item ends with a **Design implication** stating exactly what a designer **may** and **may not** do. File:line citations are against current `main` HEAD.

Source authority: `docs/ALPHA_HANDOVER.md §3-4`, `docs/SAFETY-PROVISIONS.md`, `docs/POPUP-OVERLAY.md`, `docs/QA-AND-TIERING.md`, `docs/HEAD-TRACKING-PRIVACY.md`, plus verified code.

---

## A. CLINICAL-SAFETY Invariants (HARD-STOP — these are the product's medical promises)

**A1. Crisis safety-floor short-circuit is the first thing any text input touches.**
The self-harm / crisis-line interception (`SAFETY-PROVISIONS.md §1`; Wave-1 hardening moves it to `src/SenseEase.Core/Safety/SafetyFloor.cs`, called at line 1 of `ChatbotViewModel.SendAsync`) MUST run **before** any LLM dispatch, lexicon, NLU, or profile-picker path. The hardcoded crisis response (Taiwan 1925 安心專線, US 988, `https://findahelpline.com`) is fixed text the LLM never paraphrases (`SAFETY-PROVISIONS.md §5`). The triggering utterance must never reach `chat_history.jsonl` (persist `[REDACTED: safety_floor]`).
- **Design implication:** A designer **may** restyle the crisis card's container, typography (≥14px body, AAA contrast), and spacing, and **may** add a calm icon. A designer **may NOT** add any animation, countdown, expiry, dismiss-on-click-outside, theming toggle, or "AI is thinking…" affordance to the crisis surface; **may NOT** route the crisis text through the chatbot bubble pipeline; **may NOT** change, shorten, A/B-test, or localize-away the hardcoded hotline strings; **may NOT** add a "tell me more" CTA that re-submits the user's text to the LLM. The crisis surface is the one screen that is allowed to be visually plain and instantly present.

**A2. Mask depth floor — the FOV mask is the protection; its depth is engine-owned, not decorative.**
The 10-tier alpha table is the single source of truth: `OverlayWindow.xaml.cs:54-66` (`VimsLevels`), peak alpha 0.92 at L10, resting 0.00 at L1, with a deliberate cool-near-black tint at L7+ so the mask "reads as deliberate dimming, not a uniform gray fog." The composed FOV radius is clamped `[15, 100]` (`SAFETY-PROVISIONS.md §8`) and the tightest natural radius is `TightestNaturalFovRadiusPercent = 15` (`SenseDefaults.cs:12`). G1's slash-command path enforces a **mask floor of 20%** opacity (`ALPHA_HANDOVER.md` Wave-10 G1).
- **Design implication:** A designer **may** propose a calmer *peripheral* readability treatment (e.g. softer gradient feather, the existing 11-stop smoothstep ramp) and **may** retune the L7-L10 *hue* toward the "Calm" cool-slate direction **as a proposal to engineering**, not a direct XAML edit. A designer **may NOT** reduce any tier's peak alpha below the engine table, cap the mask shallower than the engine commands, expose a user "make it lighter" control that drives effective opacity below the 20% floor, or replace the radial mask with a purely-aesthetic vignette that does not track `state.VimsTier`. The mask's *darkness at a given tier* is clinical, not a brand lever.

**A3. Vestibular escape-hatch is force-disabled for diagnosed users and never reaches zero opacity.**
`OverlayWindow.xaml.cs:481-528`: `TriggerEscapeHatch` returns early when `HasVestibularDiagnosis()` is true (`:486`), and even when allowed it dips `FovMask.Opacity` only to **0.30** (not 0) for a 3 s hold before easing back to 1.0 (`:495, :507-510`). The Settings checkbox is greyed and the Ctrl+Ctrl chord no-ops for diagnosed users (`HEAD-TRACKING-PRIVACY.md §3.4`).
- **Design implication:** A designer **may** style the escape-hatch toggle's disabled state with a clear, non-alarming "unavailable for your profile" affordance and a plain-language tooltip. A designer **may NOT** design an escape-hatch that fully clears the mask (0% opacity), remove the greyed/disabled visual for vestibular users, add a prominent "tap to clear the mask" CTA that invites overriding it, or build a marketing flow that frames mask-clearing as a primary feature for vestibular users.

**A4. No brightness snapping / abrupt visual transitions — motion is rationed and eased.**
Mask color+alpha cross-fade via `MaskSmoothingFactor = 0.25` per tick (`OverlayWindow.xaml.cs:77`, ~250 ms ease); tier hysteresis uses asymmetric attack/release debounce (`SenseDefaults.cs:87-106`, fast-tighten/slow-release "audio compressor" model). The "Calm" direction mandates a Reduce-Motion master switch and 120/200/280 ms calm-out easings.
- **Design implication:** A designer **may** specify the 120/200/280 ms easing tokens and a global Reduce-Motion switch that collapses *decorative* motion. A designer **may NOT** introduce instant alpha/brightness jumps on tier changes, snap the mask on/off without the smoothing path, animate the mask with attention-grabbing motion (pulsing, breathing, color cycling), or let Reduce-Motion disable the *clinical* mask cross-fade (Reduce-Motion governs chrome, not the safety smoothing — propose them as separate channels).

**A5. Defensive intercepts surface a transparent block reason, not silent suppression.**
Apply-time intercepts (`SAFETY-PROVISIONS.md §4`, Decision 6 §9) deliberately show "chatbot suggested X but it was blocked" with the `SafetyNote` rendered as ⚠ red text on the Card, rather than hiding the proposal. Vestibular hard cap is defense-in-depth across calibration (`CalibrationProcessor.cs:95-96`, `VestibularPersonalPressureScaleCap = 1.0`; `SevereStrikeLimitOverride`), propose-time, and apply-time.
- **Design implication:** A designer **may** redesign the block-reason styling to the "Calm" semantic — but it must remain a *muted clay* (`#B65C4E`) high-strain token with a warning glyph, **never** the old alarm red `#E54747` (currently `AlertBtn` at `MainWindow.xaml:65`) and **never** removed for being "negative." A designer **may NOT** hide blocked proposals, downgrade the ⚠ affordance to invisible, or strip the `SafetyNote` text in favor of a generic "couldn't do that."

---

## B. PRIVACY Invariants (HARD-STOP — webcam + PII)

**B1. Head-tracking is OFF by default; webcam never opens until explicit opt-in.**
`AppConfig.HeadTrackingEnabled = false` (M3 invariant; `ALPHA_HANDOVER.md §3.3`; `HEAD-TRACKING-PRIVACY.md §1`). Until opt-in the codepath is dead (no `MediaCapture`, no sidecar spawn, mask center hardcoded (0.5, 0.5)). The opt-in is a single checkbox annotated "需重新啟動 / Requires restart" (`§3.1, §5`).
- **Design implication:** A designer **may** make the opt-in checkbox legible, calm, and honestly labeled with the restart note and a plain-language "uses your webcam locally; nothing leaves your device" caption. A designer **may NOT** pre-check the box, default head-tracking on in any theme/onboarding/first-run flow, frame the webcam feature as the headline experience, use a dark-pattern "Enable for the best experience" nudge, or hide the restart consequence.

**B2. No PII in any log the design or its telemetry touches.**
`LOG-PATHS.md:156-159` whitelists only `chat_history.jsonl`, `calibration_history.jsonl`, `model_trace.jsonl` as content-bearing by design; everything else must be PII-free (`ALPHA_HANDOVER.md §3.2, §3.11`). Log `pattern_id` not raw utterance, `<LOCALAPPDATA>` not full path, `DateTime.UtcNow.ToString("o")` not `DateTime.Now`.
- **Design implication:** A designer **may** add usability instrumentation only via PII-free `pattern_id`/intent tokens and aggregate counters. A designer **may NOT** introduce any new analytics, A/B-test beacon, screenshot capture, session-replay, "share my settings" export, or crash-attachment flow that writes window titles, notification content, chat text, calibration freetext, or absolute paths to a non-whitelisted log — and **may NOT** add a "send feedback with logs" button without the explicit whitelist (`crash.log`, `wgc_diag*.txt`, `llama-server.log`, `wgc_ofi_trace.txt` only — H22).

**B3. Sidecar emits ONLY luma + landmark-derived scalars — never frame data, landmarks, or blendshapes.**
OBS-3 (`HEAD-TRACKING-PRIVACY.md §2.1-2.5`): the per-frame JSON allowlist is `t_ms, cx, cy, luma, face_detected, face_present` (+ `yaw_deg/pitch_deg` under `--debug` only). No raw landmarks, no image bytes, no `output_face_blendshapes=True`, no file writes of face data under any flag.
- **Design implication:** A designer **may** build a "head-tracking is active" status affordance driven by `face_present`/`cx`/`cy` scalars and **may** add a calm self-verification hint ("Windows shows a camera indicator; you can confirm in Task Manager"). A designer **may NOT** request a live webcam preview, a face-mesh visualization, a "see what the tracker sees" panel, a gaze heat-map, or any UI that would require the sidecar to emit landmarks or frames. Any concept that needs richer-than-scalar face data is rejected at intake.

---

## C. ENGINEERING Invariants (HARD-STOP for grid/interop/AppId; STRICT for the rest)

**C1. The anchor grid is world-locked — never viewport-relative.**
Decision 2, overlay sprint (`ALPHA_HANDOVER.md §3.6`). `GridSizePx = 40`, `GridOpacity = 0.75` (`SenseDefaults.cs:14-15`); the grid uses a frozen radial `OpacityMask` feather pinned independently of the moving FOV center (`OverlayWindow.xaml.cs:141-152`).
- **Design implication:** A designer **may** retune grid line weight, the feather radius, and the cell-fill calm tone (currently `Color.FromArgb(120, 8, 8, 10)`, `OverlayWindow.xaml.cs:85`) **as engineering proposals**. A designer **may NOT** make the grid scroll/parallax with content, anchor it to the viewport or to the mask center, or add motion to grid cells — world-lock is a VIMS-correctness property, not a style choice.

**C2. Overlay is a click-through, topmost, transparent Window — interop must not regress.**
`OverlayWindow` is documented as the click-through topmost core (`OverlayWindow.xaml.cs:14-18`), and every SenseEase-owned visible window rendered on the captured monitor MUST register its physical-pixel rect via `WgcSensingProvider.SetMonitorExcludeRect` on first render to avoid OFI feedback (`POPUP-OVERLAY.md §3`; `QA-AND-TIERING.md §3.1`).
- **Design implication:** A designer **may** add new informational surfaces (banners, cards, badges) **only if** the spec hands engineering the mask-registration requirement for any window drawn on the captured monitor. A designer **may NOT** introduce an overlay element that captures mouse input (breaking click-through), a full-screen scrim that would self-feed OFI, or a new on-monitor window without the `SetMonitorExcludeRect` engage-on-render / clear-on-close contract.

**C3. AppId is locked forever.**
Inno Setup AppId `DB7DBFD5-66A1-40CA-A320-4FD9AF0B431C` (`ALPHA_HANDOVER.md §3.7`). Branding the rebrand "SenseEase Calm" or similar must not touch identity used for upgrade matching.
- **Design implication:** A designer **may** change product display name, window title (`MainWindow.xaml:5`), icon art, and installer wizard imagery. A designer **may NOT** request an AppId regeneration, a new install GUID, or any rename that would orphan existing testers into a parallel install.

**C4. `head_tracker.py` algorithm core is untouched.**
Locked head-tracking decision (`ALPHA_HANDOVER.md §3.4`): no changes to EWMA smoothing, dead-zone gating, or face-presence logic; the sidecar still emits `luma` even though no C# consumer reads it post-v0.11 (`HEAD-TRACKING-PRIVACY.md §5 v0.11 note`).
- **Design implication:** A designer **may** change how the *host* interprets `cx`/`cy` into mask center and **may** request UX tuning of follow-mode labels (Balanced / Full follow). A designer **may NOT** request changes to sidecar smoothing/dead-zone behavior to "make the mask feel snappier" — perceived lag is solved host-side (the 30 Hz `UpdateHeadGazeCenter` path, `OverlayWindow.xaml.cs:542`), not by editing the locked core.

**C5. Cannot break prior works; existing tests must pass; the chatbot logic is the only redefinable surface.**
`ALPHA_HANDOVER.md §3.8` (user: "All advised edits cannot break anything in the prior works. Only the logic of chatbot can be redefined."). The "Calm" rebrand must not regress tier mapping (`TierColorPalette`, `QA-AND-TIERING.md §2.2`), the toggle-behavior matrix (`POPUP-OVERLAY.md §4`), or the mask-feedback seam.
- **Design implication:** A designer **may** centralize tokens into the empty `App.xaml Application.Resources`, kill the ~250 inline hex literals and per-window duplicate styles, and de-duplicate the 5× `CalibrationCardView` 40-line control template — provided every change is a *visual* refactor that leaves behavior and existing tests green. A designer **may NOT** alter the 3-band tier hue thresholds, the auto-expand-on-band-crossing rule, the 6-hour band-scoped mute semantics, or any value a test pins, without flagging it as a behavior change in the PR.

**C6. Do not commit / push until explicitly asked; no `--no-verify`.**
`ALPHA_HANDOVER.md §3.9-3.10`. Visual review is a user hard-stop (`QA-AND-TIERING.md §1.3`, Layer-3 manual smoke before any tag/merge).
- **Design implication:** A designer/agent **may** stage all rebrand work on a branch and produce before/after evidence for the user's visual review. A designer **may NOT** self-approve, merge, or tag a visual change on green unit tests alone — "tests green ≠ working feature" for any user-facing UI (`QA-AND-TIERING.md §1.1, §1.5`).

---

## D. PRODUCT Invariants (STRICT — semantics the design must preserve, not reinvent)

**D1. Driver/Passenger semantics are engine-defined and must read correctly in copy.**
`SenseDefaults.DriverFlowAttenuation` and the 8× passenger:driver pressure ratio at steady state are pinned by tests (`ALPHA_HANDOVER.md` Wave-10 D2; `PassiveFlowWeight = 2.0`, `SenseDefaults.cs:18`). Driver mode applies attenuation; Passenger does not.
- **Design implication:** A designer **may** translate Driver/Passenger into plain, reassuring language ("You're driving — lighter protection" vs "You're a passenger — full protection") in the "Clarity: plain language not engineering telemetry" spirit. A designer **may NOT** invert the meaning, imply Driver gets *more* masking, or merge the two modes into a single ambiguous control.

**D2. The 1-10 tier → 3-band mapping is the single source of truth; status is never color-only.**
`TierColorPalette` (`QA-AND-TIERING.md §2.2`): Grey 1-5, Amber 6-7, Red 8-10, with documented alphas; all surfaces consume it, no copy-paste mappings. Current alarm hues (`#E6 8C 28` amber, `#DC 32 28` red) must move to the "Calm" semantic comfort tokens (Steady teal-green / Rising amber / High clay `#B65C4E`). The known audit fact "color-only status dots" is a WCAG 2.2 / colorblind-safe violation to fix.
- **Design implication:** A designer **may** re-skin the three bands to the desaturated comfort tokens and **may** rename bands to comfort language ("Steady / Rising / High") — through `TierColorPalette` only. A designer **may NOT** add a fourth band (user already rejected grey-warm, `QA-AND-TIERING.md §2.3`), duplicate the band lookup in XAML, or ship any status indicator that conveys state by hue alone — every status must pair color with a shape/glyph/label (AutomationProperties + icon), since `AutomationProperties = 0` and color-only dots are current defects.

**D3. Habituation offset (Q4.5) is sacred — no design action may zero weeks of progress.**
`SAFETY-PROVISIONS.md §9` Q4.5 + `ALPHA_HANDOVER.md §3.1`: any "reset"-like control must call the narrowed `ResetTransientState()` and never `_habituation.Reset()`; full reset (`_offset` clear) is Settings-only, never a chatbot proposal intent. The "Delete all my data" button (H24) must not zero `_offset` (`ALPHA_HANDOVER.md` Wave-9).
- **Design implication:** A designer **may** add a calm, plainly-worded "Reset visual settings" control distinct from "Delete all my data," each with an honest consequence preview. A designer **may NOT** design a single ambiguous "Reset" button that conflates shader reset with habituation wipe, a one-tap reset without a confirm step, or any chatbot quick-action that reads as "start over" and would invite the over-broad reset (the C5 failure pattern, `§4.1`).

**D4. Tiering, mute, and proposal lifecycle copy must match locked behavior.**
6-hour mute is band-scoped and in-memory (`POPUP-OVERLAY.md §4`); user-initiated card open always works regardless of toggles/mute; auto-expand fires on band crossing, not within-band; proposals expire and the Decline button disables in the expiry branch (`ALPHA_HANDOVER.md` Wave-4 NE27). Proposal `Rationale` is truncated to 120 chars, prompt-injection lines stripped, rendered italic with an "AI suggests: " prefix (H7).
- **Design implication:** A designer **may** style the proposal card, the "AI suggests:" affordance, mute controls, and expiry states to the "Calm" elevation/space hierarchy. A designer **may NOT** write mute copy implying it persists across restarts, present auto-expand as firing on every tier tick, render unbounded/un-prefixed model rationale text, or leave a Decline button visually active after expiry.

---

## Cross-cutting note for concept scoring

The current UI contains two clashing palettes (light console dashboard vs dark popup cards) and surfaces engineering telemetry (Optical Flow Index, VIMS Pressure, Leaky Integrator, FMS, FOV Radius) as the dashboard headline (`MainWindow.xaml` header `#024AD8` band at `:114`, `CardValue` 32px telemetry numbers at `:79-84`, low-contrast `#7B8DB0` essential labels at `:75`). Resolving these is *encouraged* by the "Calm" direction — but every resolution is scored against A-D above. A concept that achieves visual calm by **weakening any HARD-STOP invariant** (e.g. a "zen mode" that fully clears the mask, a webcam-preview "presence" widget, an instant-fade tier transition, a single unified "Reset," or telemetry replaced by a marketing animation that hides the mask state) is rejected regardless of aesthetic quality. The order of authority is: **Clinical safety (A) > Privacy (B) > Engineering locks (C) > Product semantics (D) > visual/brand preference.**
