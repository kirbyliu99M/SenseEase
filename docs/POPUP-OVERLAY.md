# Pop-up Overlay (Persistent Bar + Tray)

Engineering reference for SenseEase's notification surfaces. Audience:
future contributors touching any file under [PersistentBarWindow](../src/SenseEase.App/Views/PersistentBarWindow.xaml.cs),
[NotificationCardWindow](../src/SenseEase.App/Views/NotificationCardWindow.xaml.cs),
[TrayIconService](../src/SenseEase.App/Services/TrayIconService.cs),
[SettingsWindow](../src/SenseEase.App/Views/SettingsWindow.xaml.cs),
[UserPreferences](../src/SenseEase.App/Configuration/UserPreferences.cs),
[TierColorPalette](../src/SenseEase.App/Views/TierColorPalette.cs),
[NotificationCardController](../src/SenseEase.App/Services/NotificationCardController.cs),
[PopupSurfaceGate](../src/SenseEase.App/Services/PopupSurfaceGate.cs), or
the WGC bar-mask seam in
[WgcSensingProvider](../src/SenseEase.Infrastructure/Real/WgcSensingProvider.cs).

Companion document: [docs/CALIBRATION.md](CALIBRATION.md) (calibration sprint that
preceded this one).

---

## 1 · Overview

The pop-up sprint replaced the spike-only `SpikePopupWindow` notification
with a three-layer system:

| Surface | Always present? | Purpose |
|---|---|---|
| **Persistent bar** | yes (Toggle B respected) | 4 dip × 80 dip top-centre Window. Tier-coloured. Ambient signal — glanceable status without interruption. |
| **System tray icon** | yes (Toggle B respected) | Same tier-colour mapping at full alpha. Status menu, show/hide-bar, settings, quit. |
| **Notification card** | only on click or (opt-in) auto-expand | 360×200 dip Window just below the bar. Title + body + Engage + Mute + Close. 60 s idle → 5 s reversible fade. |

The legacy `SpikePopupWindow` still ships but is gated behind a
`use_new_bar_overlay` feature flag (default true = new path; user can
edit JSON to roll back).

---

## 2 · Architecture

```
              ┌─ MainViewModel ──────────────────────────────┐
              │  PressureSpikeDetector  →  PressureSpike     │
              │  HabituationEngine      →  StateChanged       │
              └────────┬─────────────────────────────────────┘
                       │
                       ▼
              ┌─ MainWindow.xaml.cs ─────────────────────────┐
              │  PopupSurfaceGate decides which surfaces     │
              │  spawn at startup                            │
              │                                              │
              │   if validation env → BarStubWindow only     │
              │   elif new overlay  → PersistentBar + Tray   │
              │   else (legacy)     → SpikePopupWindow only  │
              └────────┬─────────────────────────────────────┘
                       │
       ┌───────────────┼───────────────────────────────┐
       ▼               ▼                               ▼
PersistentBarWindow  TrayIconService           NotificationCardWindow
  (Task 2)            (Task 3 via                (Task 4)
    │ SetTier()         H.NotifyIcon.Wpg)          ▲
    │ Clicked event     │ LeftClicked event        │
    │                   │ SettingsRequested        │ Show triggered
    └───────────────────┴───── ShowNotificationCard ┘
                                       ▲
                                       │
                          NotificationCardController
                          (mute state, 6h-mute logic)
                                       ▲
                                       │
                            UserPreferences.Current
                            (toggles A/B, vestibular flag,
                             use_new_bar_overlay)
```

The shared `TierColorPalette` lives one level below all four UI components
(bar, tray, card, settings preview) so VIMS tier → colour is a single
source of truth: changing a hue here changes every consuming surface in
lock step.

---

## 3 · Always-on mask invariant (architectural rule)

**Any SenseEase-owned visible UI element rendered on the captured monitor
MUST register its physical-pixel rect with
`WgcSensingProvider.SetMonitorExcludeRect` on first render. This prevents
the captured pixels of our own UI from feeding back into OFI computation.**

The rule (recorded as a sprint decision 2026-05-26, "Option II"):

- **Bar** engages the mask on `ContentRendered`, clears on `Closed`
- **Card** engages on `ContentRendered`, clears on `Closed` (and the bar
  re-engages its own rect when the card closes)
- **Tray icon** does NOT need to engage (it's in the system shell area, not
  the captured monitor surface, but verify this assumption if Windows
  ever changes shell rendering)
- **Settings window** does NOT need to engage (centred dialog; user
  interaction with settings is brief and intentional — feedback risk is
  bounded). Future polish could engage it, but not required.

**Future UI work (Habituation Phase 6 chatbot notifications, accessibility
indicators, etc.) MUST follow this rule.** A new visible Window that
forgets to engage the mask will produce a closed-loop feedback where
its own pixels drive OFI changes which drive UI updates which drive
more OFI changes.

The mask seam was validated empirically in Task 1; see Section 7 for
the trace evidence.

---

## 4 · Toggle behaviour matrix

The settings window exposes two independent toggles persisted in
`user-preferences.json`. Their combinations:

| Toggle A (auto-expand) | Toggle B (colour-on) | Bar colour | Tray icon | Auto-expand on spike? | Auto-expand on tier-up? |
|:---:|:---:|---|---|:---:|:---:|
| OFF (default) | ON (default) | 3-tier | 3-tier | NO | NO |
| OFF | OFF | grey only | grey only | NO | NO |
| ON | ON | 3-tier | 3-tier | YES (if not muted) | YES (grey → non-grey edge) |
| ON | OFF | grey only | grey only | YES (if not muted) | YES |

Notes:

- **User-initiated card open always works**, regardless of toggles or
  mute. Clicking the bar or tray opens the card immediately. The toggles
  only gate AUTO paths.
- **6-hour mute** is band-scoped (grey / amber / red). Muting amber
  doesn't suppress red, and vice versa.
- **Mute applies to auto-show only.** User-initiated clicks bypass mute.
- **Mute does not persist across restarts** (in-memory only). Future
  Task 5 polish could move mute into `UserPreferences` if user feedback
  shows it's needed.

---

## 5 · Vestibular safety warning

Single-warning dialog that fires once when the user attempts to turn
Toggle B (colour-on) OFF AND their calibration profile records a
vestibular diagnosis (Q5 `VestibularDiagnosed` risk factor).

- Dialog wording is bilingual-ready (currently zh-only — see Section 9
  for i18n TODO list)
- **Whichever the user picks**, `VestibularWarningShown` is persisted
  TRUE so the second OFF attempt doesn't re-nag (no-nagging principle)
- **Cancel reverts the checkbox** but still persists the warning-shown
  flag (warning was visually shown; the rule fires regardless of choice)
- `UserPreferences.ResetToDefaults()` ALSO clears
  `VestibularWarningShown` — a reset user is functionally a new install
  and should be re-warned

---

## 6 · Emergency rollback flag (`use_new_bar_overlay`)

Default `true` = new bar/tray/card path. Set to `false` in
`user-preferences.json` to fall back to the legacy `SpikePopupWindow`
behaviour entirely:

- Bar and tray do NOT spawn at app start (`PopupSurfaceGate` enforces)
- Spike events route through the legacy path: tab-switch to AI Assistant
  + chat bubble via `vm.ChatbotVm.RaiseFollowup` + `SpikePopupWindow.Show`
- Tier-up auto-expand is also skipped (legacy mode has no card)

**Not exposed in the settings UI** — this is emergency rollback only.
Two sprints from now (post Habituation Phase 8), if no rollback usage
surfaces, the flag and the legacy `SpikePopupWindow` path can both be
deleted together. The legacy path is feature-flagged precisely to avoid
deleting it before its replacement has soaked.

---

## 7 · WGC mask validation (Task 1 evidence)

The bar's pixels can in principle feed back into OFI via WGC capture
of the monitor. Spec §5 proposed three mitigations; Task 1 validated
Option B (pixel-mask the bar rect in the OFI pipeline) and the user
approved it.

### 7.1 Validation harness

Two env vars control the validation:

```
SENSEEASE_WGC_BAR_VALIDATION=1    # spawn BarStubWindow, write OFI trace
SENSEEASE_WGC_BAR_MASK=1          # also engage the mask (Step B run)
```

When `SENSEEASE_WGC_BAR_VALIDATION=1`:
- BarStubWindow spawns top-centre with a 200 ms red↔cyan adversarial
  animation
- `WgcSensingProvider.OnTick` writes one CSV line per sample to
  `%TEMP%/wgc_ofi_trace.txt`:
  `epoch_ms,smoothed_ofi,mask_left,mask_top,mask_w,mask_h`
- Production bar/tray suppressed (mutex via `PopupSurfaceGate`)

### 7.2 What the traces showed

- **Run A (no mask)**: OFI mostly 0.0000 with sporadic small spikes
  (0.1–1.5 range) during bar-flashing period
- **Run B (mask on)**: similar pattern, mask cols populated correctly
- **Run C (sanity, mask on + Notepad drag)**: ~50 OFI spike during
  drag, confirming mask is targeted (doesn't suppress non-bar motion)

The math explains why A and B look similar: the bar's 4 dip × 80 dip
footprint maps to ~5 cells of the 14,400-cell downsample buffer
(0.035% of MAD denominator). Worst-case red↔cyan delta contributes
≤0.089 raw MAD — below the noise floor.

**Conclusion**: the mask is over-engineered for the current 4 dip bar
but cheap to keep. It earns its keep for the expanded card (360×200 dip
→ ~10% of MAD denominator → real feedback risk) and future surfaces
that might grow larger.

---

## 8 · BarStubWindow validation scaffolding (kept, gated)

[BarStubWindow](../src/SenseEase.App/Views/BarStubWindow.xaml.cs) and the
`MaybeStartWgcValidation` orchestration in MainWindow stay in the repo
post-Task 7. Justification:

- Pure scaffolding, env-var-gated, invisible in production
- Future "did WGC change behaviour after a Windows update?" investigations
  can re-run the same A/B/C protocol without re-implementing
- Cost of keeping = a few hundred lines of mostly-comments code
- Cost of deleting = re-write next time the question comes up

If a future cleanup decides to retire it, the production seam
(`WgcSensingProvider.SetMonitorExcludeRect`, `_ofiTraceEnabled` field,
`OnTick` trace write) must STAY — those are production-grade always-on
hooks the bar relies on. Only the harness side (BarStubWindow + the
env-var-gated `MaybeStartWgcValidation`) is removable.

---

## 9 · Known open work / i18n TODO

- **i18n**: tray menu items, settings window labels + descriptions, and
  notification card content are currently zh-only. When a localization
  service lands in a future sprint, replace the string literals at:
  - `TrayIconService.cs` (status / show-bar-toggle / settings / quit)
  - `SettingsWindow.xaml` (toggle labels + descriptions)
  - `NotificationCardWindow.cs` `SetContent` (title/body per tier)
  - `SettingsWindow.xaml.cs` vestibular warning text
- **Win11 taskbar overflow**: tray icon may land in the overflow flyout
  by default (shell policy). We do NOT auto-promote (would be a shell-
  policy violation). Onboarding documentation should suggest users
  drag the SenseEase tray icon into the visible tray if they rely on it.
- **Mute persistence**: 6-hour mute is in-memory. If user feedback
  shows session-restart-clearing-mute is a problem, move mute state
  into `UserPreferences` (one Dictionary<TierBand, DateTime> field).
- **Auto-promotion for vestibular users**: spec didn't ask for it, but
  worth considering — vestibular-diagnosed users currently get the
  same defaults as everyone else (Toggle A OFF). A future safety polish
  could default Toggle A to ON for diagnosed users.
- **NotificationCardWindow Engage action**: spec is ambiguous on what
  "啟動保護" actually triggers per tier. Task 4 ships it as a
  debug-log placeholder + instant card close. A future sprint owns
  defining the action set (overlay engagement, mode toggle, etc).

---

## 10 · Phase ordering (historical)

| Task | Goal | Commit |
|---|---|---|
| 1 | WGC mask validation harness (Option B) | `4395cac` |
| 2 | PersistentBarWindow + 3-tier colour + always-on mask | `9f10f23` |
| 3 | Tray icon + TierColorPalette refactor (H.NotifyIcon.Wpf) | `dd30d28` |
| 4 | NotificationCard + hover + click + mute + fade | `870643c` |
| 5 | UserPreferences + SettingsWindow + Toggle A/B + safety warning + auto-show | `3cf46e2` |
| 6 | Sensitivity 30→50 + legacy popup feature-flag dispatcher | `4936abb` |
| 7 | docs + PopupSurfaceGate extraction + mutex tests (this commit) | this commit |

Branch: `overlay-bar-redesign`. Sprint sealed by tag (TBD after user
visual review).
