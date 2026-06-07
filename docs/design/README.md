# SenseEase UI/UX Rebrand — Design Package

<!-- Generated 2026-06-05 by the senseease-rebrand-design workflow (31 agents). Companion to docs/UIUX_UPGRADE_PLAN.md. Draft for review — no code changed. -->

> Generated 2026-06-05 by the senseease-rebrand-design workflow (31 agents). Companion to docs/UIUX_UPGRADE_PLAN.md. Draft for review — no code changed.

This folder is the Alpha-1 rebrand design brief across three pillars: **Visual upgrade**, **Language/wording**, and **Immersive experience**.

## ⭐ Start here
- [**LOCKED_DIRECTION_ALPHA1.md**](LOCKED_DIRECTION_ALPHA1.md) — **the source of truth.** Collapses the six-concept menu into one posture ("The Quiet Guardian that Learns You"), resolves the three open questions, finalizes the Home + pop-up Card specs, and gives the D0–D6 roadmap. Supersedes IMMERSIVE_CONCEPTS' menu. Engine-touching items needing sign-off are in §6.
- [alpha1-detail/](alpha1-detail/) — the underlying resolutions (liveness, legibility, soften-seam, Home, Card) + the two adversarial reviews.

## Reference documents
- [DESIGN_SYSTEM.md](DESIGN_SYSTEM.md) — the lockable "Calm" token spec (color/type/spacing/motion/components; light+dark+high-contrast).
- [VISUAL_SPEC.md](VISUAL_SPEC.md) — Pillar 1: per-surface redesign of all 9 surfaces incl. the pop-up Card.
- [CONTENT_AND_VOICE.md](CONTENT_AND_VOICE.md) — Pillar 2: voice guide + before/after copy for every string.
- [IMMERSIVE_CONCEPTS.md](IMMERSIVE_CONCEPTS.md) — Pillar 3: six concepts, adversarial verdicts, and the decision menu.
- [TECH_CAPABILITY_MAP.md](TECH_CAPABILITY_MAP.md) — every real signal & actuator the immersive work is grounded in.
- [HARD_CONSTRAINTS.md](HARD_CONSTRAINTS.md) — clinical-safety / privacy / engineering guardrails every proposal respects.
- [COMPLETENESS_PUNCHLIST.md](COMPLETENESS_PUNCHLIST.md) — missing states/surfaces to spec before build.

---

## Executive summary

I now have all the evidence I need. The bilingual inline string pattern is confirmed (e.g., `MainWindow.xaml:389` "顯示器周邊維持暗角...偵測到動態時自動加深 / Periphery stays dim..."), the security wave ordering is confirmed (H1+H2+H3+H4+H8 first PR, then loopback auth, then remainder), and all UI audit facts are verified with file:line evidence. I have enough to write the executive summary.

# Executive Summary — SenseEase Alpha-1 Rebrand: "Embody the Calm It Sells"

## The thesis in one sentence

SenseEase tells overstimulated, sometimes-nauseated users that it will be the quietest app on their machine — and today's UI contradicts that promise on first paint. The Alpha-1 rebrand makes the product *itself* the proof: every surface must read as calm, legible, and reassuring before it can credibly sell calm.

## Why now: the contradiction is measurable

The current build sells regulation while presenting dysregulation. Concretely, against the live tree:

- **No design system exists.** `App.xaml` `Application.Resources` is empty (`App.xaml:5-10`); **241 inline hex literals span 10 XAML files** (verified count), with per-window duplicated styles and a 40-line radio-cell `ControlTemplate` duplicated **5×** inside `CalibrationCardView.xaml` (severity cells at lines 224, 251, 278, 305, 331). There is no single source of truth, so "fix the color" today means 241 edits.
- **Two clashing palettes collide on the dashboard.** The shell is a light console (`MainWindow.xaml:9` `Background="#E2ECFF"`), but the first-run welcome card is a dark slab (`MainWindow.xaml:178` `Background="#0E1320"` with `#3D5AFE` border) — a dark card floating on a light canvas, exactly where a nervous first-time user lands. `SettingsWindow.xaml:11` re-declares yet another `#E2ECFF` surface inline.
- **Engineering telemetry is the headline.** The dashboard leads with "Optical Flow Index," "VIMS Pressure," "Leaky Integrator," "FOV Radius," and "FMS Score" (`MainWindow.xaml:213, 226, 232, 239, 352`). We hand a migraine-sensitive user a control-room readout instead of a comfort state.
- **Zero accessibility scaffolding.** `AutomationProperties` appears **0 times** across all XAML — no screen-reader support. Status is encoded color-only via bare `Ellipse` fills (`MainWindow.xaml:255-266, 543-557, 579-585`: `#7B8DB0` / `#024AD8` / `#E54747` dots and bars with no text or shape redundancy). Reduce-motion and high-contrast: not present.
- **Below-floor essential text.** `#7B8DB0` carries real labels throughout `CalibrationCardView.xaml` (lines 65, 94, 101, 142, 231…) and `SettingsContent.xaml:59, 74, 94`; `#9AB6E0` carries essential text on `SpikePopupWindow.xaml:32, 90` — both fail WCAG AA on their backgrounds.
- **Bilingual strings crammed inline.** Copy ships as `"中文 / English"` baked into XAML (e.g. `MainWindow.xaml:389` `"顯示器周邊維持暗角;偵測到動態時自動加深 / Periphery stays dim…"`), with no resource separation, blocking both localization and voice control.

## Three pillars, one thesis

**Pillar 1 — Total Visual Upgrade (incl. the pop-up Card).** Replace 241 inline hexes and the two-palette clash with the **"Calm" direction**: desaturated slate-and-paper (`Canvas #F6F7F9`), accent `#3A6EA5` (retiring royal `#024AD8`), muted clay `#B65C4E` for high-strain (retiring alarm `#E54747`), one neutral 3-level shadow, radii 8/12/16, a 4/8pt spacing grid, and a 14px body floor. The notification/pop-up Card (`NotificationCardWindow.xaml`, `SpikePopupWindow.xaml`) and the first-run/calibration-invite banners adopt the *same* light surface so nothing dark ever floats on the dashboard. See **DESIGN_SYSTEM** (tokens) and **VISUAL_SPEC** (per-surface redlines).

**Pillar 2 — Language & Wording.** Demote telemetry from headline to an opt-in "Details" disclosure; lead every surface with a plain-language comfort state (**Steady / Rising / High**, one idea per screen). Extract all `中文 / English` inline strings into a localization resource so each surface renders one language at a time. See **CONTENT_AND_VOICE**.

**Pillar 3 — Immersive Experience.** Rationed, calm-out motion (120/200/280ms) under a master **Reduce-Motion** switch; gentle depth via space and elevation, never saturation or motion; semantic comfort tokens that animate state transitions softly. See **IMMERSIVE_CONCEPTS** and **TECH_CAPABILITY_MAP** for WPF feasibility per effect.

The pillars are one move: a token dictionary (Pillar 1) is the substrate that lets wording (Pillar 2) and motion (Pillar 3) be applied once, consistently, and reversibly.

## Phased design-track roadmap (non-breaking; sequenced around the engineering waves)

The engineering Alpha-1 waves run in parallel and own *behavior*: the security first-PR (**H1+H2+H3+H4+H8** safety-floor bypass), then loopback auth (**H9-H11, H29**), then functional gaps (**C1, C3, C5, E1/E3/E4, F1**) per `ALPHA_SECURITY_BACKLOG.md` and `ALPHA_BACKLOG.md`. The design track touches **only XAML resources, copy, and styling** — never the files those waves edit (`ChatbotViewModel.cs`, `MockInferenceEngine.cs`, `SafetyFloor.cs`, services). Per **HARD_CONSTRAINTS**, every design step is additive and "cannot break prior works."

| Phase | Deliverable | Verification gate |
|---|---|---|
| **D0** | Lock open product-owner decisions (below) + freeze the "Calm" token values | Sign-off recorded; no token churn after D1 begins |
| **D1** | Non-breaking token dictionary in `App.xaml` `Application.Resources` (colors, type ramp, spacing, radii, shadow) + a **lint test** that fails the build on any *new* inline hex | Build is green; lint baseline = 241, ratchets down only |
| **D2** | Per-surface token adoption, surface by surface (dashboard → Card/pop-up → settings → calibration), each retiring its inline hexes | Pixel-diff each surface vs VISUAL_SPEC redline; inline-hex count strictly decreases; smoke tests still construct every window |
| **D3** | Accessibility: `AutomationProperties` on all interactive/status nodes; status gets text + shape redundancy (not color-only); Reduce-Motion master switch wired | Screen-reader walkthrough names every control; status legible in grayscale; AA floor met (AAA body where feasible) |
| **D4** | IA + content + localization: telemetry demoted to "Details"; comfort-state headlines; `中文 / English` strings extracted to resources | Each surface shows one idea + one language; no `/ English` literals remain in XAML |
| **D5** | Dark + high-contrast theme variants from the same token keys | All three themes pass AA; no surface regresses; theme switch is instant and total |
| **D6** | Immersive build + polish: 120/200/280ms calm-out motion, gentle depth, comfort-token transitions | Reduce-Motion fully disables animation; no surface exceeds the motion budget; final HIG pass |

Each phase merges independently and is individually revertible; D1's lint test is the guardrail that keeps later engineering merges from re-introducing inline hexes.

## Product-owner decisions still open (D0 blockers)

1. **Accent value — final.** Confirm `#3A6EA5` (vs a half-step warmer/cooler) before D1 freezes the token.
2. **Telemetry visibility.** Is the Optical-Flow/Pressure readout *hidden by default behind "Details,"* or *removed from the user build entirely* and kept only in a dev/validation mode?
3. **Localization model.** Per-OS-locale auto-switch, an explicit in-app language toggle, or both — and is Simplified Chinese in Alpha-1 scope (it is already an engineering must-fix under H3) or English + Traditional only?
4. **Theme default.** Does Alpha-1 ship light-only with dark/high-contrast as D5 follow-ons, or are all three required at the Alpha cut?
5. **Reduce-Motion default state.** On or off out of the box for this specifically motion-sensitive audience?
6. **Pop-up Card scope.** Does the rebrand restyle the Card in place, or also revisit its dwell/countdown choreography (which intersects engineering `F1`'s `NotificationCardCountdownSec` work — design must not move that timing)?

---

Evidence cited from the live tree: `src/SenseEase.App/App.xaml:5-10`; `src/SenseEase.App/MainWindow.xaml:9, 178, 213-352, 255-266, 543-557, 579-585, 389`; `src/SenseEase.App/Views/CalibrationCardView.xaml:48-101, 224-345`; `src/SenseEase.App/Views/SettingsWindow.xaml:11`; `src/SenseEase.App/Views/SettingsContent.xaml:59-94`; `src/SenseEase.App/Views/SpikePopupWindow.xaml:32, 90`. Companion docs: **DESIGN_SYSTEM**, **VISUAL_SPEC**, **CONTENT_AND_VOICE**, **IMMERSIVE_CONCEPTS**, **HARD_CONSTRAINTS**, **TECH_CAPABILITY_MAP**.
