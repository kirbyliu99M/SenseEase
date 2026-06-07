# SenseEase — UI/UX Audit & Upgrade Plan

> **Author's framing:** Written as if by an Apple Human Interface designer doing a first pass on the product. SenseEase is a **clinical-grade calm tool for sensory-sensitive people** (VIMS / cybersickness / migraine / vestibular sensitivity). That single fact governs every recommendation below. The work is structured as a **design track (D0–D6)** that runs *alongside* the engineering waves in [ALPHA_HANDOVER.md](ALPHA_HANDOVER.md), honours every hard constraint in its §3, and never breaks prior works.
>
> Provenance: full read of the 11 production XAML views + [App.xaml](../src/SenseEase.App/App.xaml), [MainWindow.xaml](../src/SenseEase.App/MainWindow.xaml), live screenshots, and the existing audit/handover docs. Date: 2026-06-05.

---

## 0. The one idea

> **The app must embody the calm it sells.**

Today there is a credibility gap. SenseEase tells the user it is *reducing visual stimulation* — while presenting a full-bleed saturated royal-blue header (`#024AD8`), a live **animating** pressure chart, typing-dot animations, paragraphs of dense bilingual text at 10–12 px, and a dashboard that leads with *"Optical Flow Index / VIMS Pressure / Leaky Integrator."* For a person who feels dizzy and nauseated, this interface is itself a mild stressor.

Everything in this plan flows from closing that gap: **a tool for people who are overstimulated should be the quietest, most legible, most reassuring app on their machine.** That is also, not coincidentally, the Apple bar.

---

## 1. Design principles (the north star)

Apple's three HIG pillars, adapted for a sensory-comfort medical-adjacent product, plus a fourth that is non-negotiable here:

1. **Clarity** — One idea per screen. Plain human language, never engineering telemetry, on primary surfaces. Type that an eye-strained user can read without leaning in (body floor **14 px**, captions **12 px**, no 10/11 px anywhere user-facing).
2. **Deference** — The UI recedes; the user's *comfort state* is the hero. Quiet chrome, generous whitespace, desaturated color. Color is a scalpel, not a paintbrush.
3. **Depth (gentle)** — Soft, meaningful hierarchy through elevation and spacing — never through saturation or motion. Motion is rationed and always purposeful.
4. **Accessibility is the product, not a setting** — This app's users *are* the accessibility edge case. WCAG 2.2 AA is the floor, AAA for body contrast where feasible. **Reduce Motion** and color-blind-safe status are core features, not nice-to-haves.

**The litmus test for every pixel:** *Would this calm a nauseated person, or tax them?*

---

## 2. Audit — what's working, what isn't

### 2.1 What's already good (keep & systematize)
- **Card-based layout** with rounded corners and soft shadows is the right metaphor; it just needs consistency.
- The **NotificationCard** was already migrated dark→light (`overlay-bar-redesign D1`) — that instinct was correct and should become the rule everywhere.
- **Chat bubble** pattern in the AI Assistant tab is solid and familiar.
- The **calibration flow** (Q1–Q5 guided cards) is well-conceived UX; it only needs visual unification and a progress affordance.
- Real restraint already shows in places (collapsing dead controls, tooltips clarifying ambiguous glyphs).

### 2.2 Findings (grouped, with evidence)

**A — No design system (root cause of most issues).**
- [App.xaml](../src/SenseEase.App/App.xaml) `Application.Resources` is **empty**. There are **~250 inline hex color literals** across 11 XAML files (verified by grep). Button styles, card styles, and text styles are **redeclared per-window**.
- [CalibrationCardView.xaml](../src/SenseEase.App/Views/CalibrationCardView.xaml) carries an explicit confession: *"Local copies of the chatbot tab's button styles… If MainWindow palette changes, update both."* The Q3 intensity scale **duplicates the same 40-line `ControlTemplate` five times** (lines 220–355).
- This is the exact **C1/C3 "duplicate-source drift"** failure pattern the team already documented in [ALPHA_HANDOVER.md §4.1](ALPHA_HANDOVER.md) — but for *visual tokens*. The fix is the same instinct: **one source of truth + a parity test.**

**B — Two clashing color worlds.**
- A light "console" theme (`#E2ECFF` bg, `#024AD8` royal blue, white cards, `#0A1F5C` navy text) collides with a dark "popup" theme (`#0E1320`, `#1A1A1A`, `#CC1A1A1A`, `#F0F4FA`, `#9AB6E0`).
- The **first-run welcome banner** ([MainWindow.xaml:177](../src/SenseEase.App/MainWindow.xaml)) and **calibration-invitation banner** (line 831) are **dark navy cards dropped onto the light blue dashboard** — visually jarring. [SpikePopupWindow.xaml](../src/SenseEase.App/Views/SpikePopupWindow.xaml) is still fully dark while its sibling NotificationCard is light.

**C — "Physician, heal thyself" (brand/tone).**
- The palette is **energetic, not soothing**: high-saturation royal blue header band, `#E54747` alert red, `#FF9B6B` orange "啟動保護" button, and a **four-hue discomfort scale** (blue→amber→orange→red, [NotificationCardWindow.xaml:249–304](../src/SenseEase.App/Views/NotificationCardWindow.xaml)). For a health-anxious audience this reads as alarm, not care.
- **Motion in a motion-sickness app:** a live-animating pressure chart ([MainWindow.xaml:574](../src/SenseEase.App/MainWindow.xaml)), typing-dot storyboard (line 717), and **zero** reduce-motion handling (grep: 0 occurrences of any motion-preference check).
- **Engineering telemetry as the headline:** the dashboard leads with *Optical Flow Index, VIMS Pressure, Leaky Integrator, FMS Score, FOV Radius / Clear zone.* This is researcher language pointed at a nauseated end-user.

**D — Information architecture.**
- **Four tabs, mixed-language labels:** `Dashboard`, `護眼儀表板 / Eye Protection`, `AI Assistant`, `設定`. The first two **overlap heavily** — both surface OFI / Pressure / FOV / Mask. Redundant.
- The dashboard answers *"here are 14 numbers"* when the user is asking *"am I protected, and is this going to make me feel worse?"* — the single most important question is buried.
- [SettingsContent.xaml](../src/SenseEase.App/Views/SettingsContent.xaml) is one long scroll of checkboxes with paragraph-length bilingual helper text; grouping is bold-text-only.

**E — Localization.**
- Almost every string is crammed `中文 / English` inline (e.g. `保持遮罩開啟 / Keep overlay visible`). This **doubles text length**, adds visual noise, and serves neither language well. There is no language preference and no `.resx` — strings are hard-coded in XAML. Voice is inconsistent (some controls Chinese-only: `啟動保護`, `6 小時內不再提示`; some English-only: `Send`, `Clear`).

**F — Accessibility (in an accessibility product).**
- **`AutomationProperties`: 0 occurrences across the entire `src` tree.** No screen-reader names, no help text — Narrator/NVDA users get raw control types or nothing.
- **Color-only status** everywhere: mask-active, activity-idle, and tier are signalled by an `Ellipse.Fill` hue alone (red/amber/green dots) — invisible to ~8% of men. No shape/icon/text redundancy.
- **Contrast failures:** `#7B8DB0` gray labels on white ≈ 3.6:1 (fails AAA, marginal AA at small sizes); `#8ABAFF` subtitle on `#024AD8`; `#9AB6E0` hint text — all used heavily for *secondary information a strained reader still needs.*
- **No high-contrast theme** (grep: 0 `SystemParameters.HighContrast`), **no focus-ring design**, **no keyboard-nav audit**, **10 px** captions in multiple flows.

**G — Component inconsistency (polish debt).**
- Corner radii in use: **2, 3, 6, 8, 10, 12, 14** (no rhythm). Shadows: colored `#5B82C6` *and* `Black`, opacity `0.08–0.4`. Button padding: `12,6 / 14,6 / 14,8 / 18,8 / 22,10`. Placeholder text via a `VisualBrush` hack. Hover states are mostly crude `Opacity` swaps.

---

## 3. The SenseEase Design System — "Calm" kit

A single, themeable token layer. Names are semantic (intent), not literal (hue) — so dark mode and high-contrast are re-skins, not rewrites.

### 3.1 Color — desaturate and soften

The move: **shift from energetic royal-blue + white to a calm, low-chroma slate-and-paper system.** Saturated color appears only in small, meaningful doses.

**Light theme (default)**
| Token | Value | Replaces | Role |
|---|---|---|---|
| `Canvas` | `#F6F7F9` | `#E2ECFF` | App background (warm near-white, not saturated blue) |
| `Surface` | `#FFFFFF` | white | Cards |
| `SurfaceMuted` | `#F1F3F6` | `#F3F6FC`/`#EEF3FF` | Secondary fills, status strips |
| `Separator` | `#E4E7EC` | `#E8EEFF` | Hairlines |
| `TextPrimary` | `#1A2230` | `#0A1F5C` | Headings/body (≈14:1 on Surface) |
| `TextSecondary` | `#566072` | `#7B8DB0` | Labels (passes AA on white) |
| `TextTertiary` | `#8A93A3` | `#9AB6E0` | Non-essential only |
| `Accent` | `#3A6EA5` | `#024AD8` | Primary actions — a calmer, lower-chroma blue |
| `AccentWeak` | `#E9EFF5` | `#EEF3FF` | Ghost-button surface, badges |
| `ComfortSteady` | `#3E8E7E` | `#2BA672` | "All good" (muted teal-green) |
| `ComfortRising` | `#C08A3C` | `#F0832E` | "Strain building" (muted amber) |
| `ComfortHigh` | `#B65C4E` | `#E54747` | "Actively easing" (muted clay, **not** fire-engine red) |
| `FocusRing` | `#3A6EA5` @ 2px | — | Keyboard focus (new) |

**Dark theme (build it — see §6.5).** Many photophobic / migraine users *prefer* dark. Same tokens, re-pointed: `Canvas #15181D`, `Surface #1D2128`, `SurfaceMuted #252A32`, `Separator #2F353D`, `TextPrimary #E6E9EE`, `TextSecondary #A6AEBB`, `Accent #7CA9CE`, comfort hues lightened ~12%.

**High-contrast theme.** Honour `SystemParameters.HighContrast`; map to system colors / AAA pairs.

> **Principle:** the *only* place SenseEase is allowed to be loud is when it must signal genuine high strain — and even then it is a muted clay, not an alarm red. Calm is the brand.

### 3.2 Typography — a real scale
Use **Segoe UI Variable** (Win 11) with **Segoe UI** fallback. Technical/diagnostic readouts use **Cascadia Code**, kept off primary surfaces.

| Role | Size / Line | Weight | Use |
|---|---|---|---|
| Display | 28 / 34 | SemiBold | Comfort-state hero |
| Title | 20 / 26 | SemiBold | Window & section titles |
| Heading | 16 / 22 | SemiBold | Card titles |
| Body | **14** / 20 | Regular | Default text (raised from 13) |
| BodyStrong | 14 / 20 | SemiBold | Emphasis, values |
| Caption | **12** / 16 | Regular | Helper text (floor) |
| Mono | 13 / 18 | Regular | Diagnostics only |

**Rule:** no 10 px or 11 px text in any user-facing surface. Respect Windows text-scaling; verify reflow at 125 %/150 %.

### 3.3 Spacing — 4 pt base, 8 pt rhythm
Tokens `space-1…8` = `4, 8, 12, 16, 20, 24, 28, 32`. Card padding standard `24`; compact `16`. Kills the current `6/8/12/14/18/20/22/24` ad-hoc soup.

### 3.4 Radius — three steps
`radius-sm 8` (controls), `radius-md 12` (cards), `radius-lg 16` (windows/sheets), `pill 999` (badges). Retire 2/3/6/14.

### 3.5 Elevation — one shadow, three levels
Neutral shadow color (drop the blue `#5B82C6` cast):
- `e1` y1 blur4 @6 % — resting controls
- `e2` y2 blur10 @8 % — cards
- `e3` y8 blur28 @14 % — floating cards / sheets / popups

### 3.6 Motion — rationed, and switchable off
- Durations `fast 120 / base 200 / slow 280 ms`; easing `cubic-bezier(0.2, 0, 0, 1)` ("calm-out").
- **`Reduce Motion`** master switch — manual toggle **and** auto-read of the Windows "show animations" pref (`SystemParameters.ClientAreaAnimation` / `SPI_GETCLIENTAREAANIMATION`). When on: pressure chart stops animating (renders static, updates on a slow tick), typing dots become a static "…", all transitions become instant cross-fades.
- **The overlay/mask itself** (the core product) must transition **slowly and eased (≥300 ms)** — never snap brightness. Codify this as a token so no future change can introduce a hard cut.

### 3.7 Components (the kit)
One canonical definition each, in the shared dictionary: `PrimaryButton`, `GhostButton`, `QuietButton`, `DangerButton` (muted), `Card`, `Badge`, `Switch` (replace bare CheckBox for settings), `SegmentedScale` (the Q3 1–5 cell — defined **once**, data-driven, replacing the 5× copy), `OptionCell`, `TextField` (with a real placeholder, retiring the VisualBrush hack), `StatusChip` (icon + text + color, triple-coded), `SectionHeader`, `ListRow` (iOS-style grouped settings row). Unify iconography on one line set (Fluent System Icons or Lucide) at a single stroke weight; retire ad-hoc glyphs `× ⚙ ⏻ ● 💡`.

---

## 4. Screen-by-screen redesign direction

### 4.1 Dashboard → **"Home / Today"** (reassurance, not telemetry)
- **Hero:** one large, calm card answering *"How are you, and are you protected?"* — e.g. **"You're protected · Comfort steady"** with a single soft state color and a plain sub-line. This is the Display-type moment.
- **Demote the metrics.** OFI / Pressure / FOV / Mask move into an **"Details"** disclosure or the Insights tab. Translate on primary surfaces:
  - `Optical Flow Index` → **"Screen motion"**
  - `VIMS Pressure` → **"Strain building"**
  - `Mask active` → **"Easing your view"**
  - `FOV Radius / Clear zone` → **"Comfort zone"**
- **Clinician / Advanced view toggle** (see §7 decision): one switch reveals the raw engineering values + chart for researchers and power users — the lineage is preserved, just not the default.
- Reduce primary actions to **Calibrate** and **Pause protection**; everything else is secondary.

### 4.2 Merge **Dashboard + Eye-Protection** → **Home** + **Insights**
They duplicate the same metrics. Split by *intent*: **Home** = reassurance + current state + quick actions; **Insights** = the chart, session summary, history, for users who want the data. Removes the redundant tab and clarifies IA.

### 4.3 AI Assistant
- Keep the bubble chat; re-skin to tokens, body to 14 px, calmer accent.
- The **dark invitation/recalibrate banners** ([MainWindow.xaml:831](../src/SenseEase.App/MainWindow.xaml)) adopt the **light Surface** language — no more dark cards on a light tab.
- Calibration card becomes a proper **guided sheet** with a **Q1–Q5 progress dotrow** and the single `SegmentedScale`/`OptionCell` components.
- Typing dots respect Reduce Motion.

### 4.4 Settings → grouped lists
- Restructure into iOS-style **grouped sections**: *Notifications · Background & startup · Head tracking & privacy · Comfort · Data & privacy* (fold in the H24 "Delete all my data" control here).
- Each row = title + one-line helper + a real **Switch**; long paragraphs become concise helper text + a **"Learn more"** link to the handbook.
- The collapsed dev-hotkey table stays out of the user build (gate behind dev mode).

### 4.5 Notification Card (already light — finish the job)
- Re-skin to tokens; tighten the up-to-six stacked sections into a clear hierarchy: **title → what's happening (plain) → one primary action ("Ease my view now") → secondary (snooze / open)**.
- Replace the **four-hue discomfort scale** with **one calm sequential ramp** (single hue, increasing depth) + icon + label — color-blind-safe and far less alarming.

### 4.6 Spike popup & ambient bar
- Migrate [SpikePopupWindow.xaml](../src/SenseEase.App/Views/SpikePopupWindow.xaml) off the dark `#CC1A1A1A` theme onto the calm Surface so it matches the card.
- The ambient bar (`#26666666`) binds to the `Comfort*` semantic tokens and stays whisper-quiet.

### 4.7 The overlay (core product, code-driven)
Design guidance only: neutral (untinted) mask color, eased ≥300 ms transitions (§3.6), low grid opacity, and a discoverable, gentle one-keystroke "peek" (the escape hatch exists; make it work and findable). The overlay is where the product *is* the calm — it must be flawless.

### 4.8 First-run / onboarding
Replace the dark welcome banner with a proper warm three-card first run: **"What SenseEase does"** (plain) → **"60-second comfort check"** (calibration) → **"Your privacy"** (webcam off by default). This is where the emotional tone is set.

---

## 5. Accessibility & localization commitments

- **WCAG 2.2 AA** minimum; **AAA body-text contrast** where feasible.
- **Reduce Motion** mode (auto + manual) — §3.6.
- **Triple-coded status**: every state = icon **and** text **and** color. No color-only signals.
- **Body ≥ 14 px**; respect Windows text scaling; verify reflow at 125/150 %.
- **Full keyboard navigation** + a visible **FocusRing** token on every interactive control.
- **`AutomationProperties.Name` / `HelpText`** on all controls; a Narrator smoke pass as a gate (closes the 0-coverage finding).
- **High-contrast theme** honouring `SystemParameters.HighContrast`.
- **Localization:** introduce `.resx` resources and a **single-language mode** with a toggle (default from OS locale), retiring inline `中文 / English` cramming. Unify content voice across both languages.

---

## 6. The rollout — design waves D0–D6

Sequenced to run **beside** the engineering waves and to honour *"cannot break prior works."* Each wave is **additive first, migrate incrementally, ≤500 lines/PR**, with a **UVHS screenshot gate** ([uvhs-playbook](../../.claude/projects/...) protocol) per screen.

### D0 — Foundations & decisions (no code)
This document + a one-page style tile (palette, type, components) + sign-off on the §7 decisions. **Output:** approved token spec.

### D1 — Design system in code (non-breaking, additive)
Create `src/SenseEase.App/Theme/`:
- `Colors.xaml`, `Typography.xaml`, `Spacing.xaml`, `Elevation.xaml`, `Controls.xaml`, bundled by `SenseEaseTheme.xaml`.
- **Critical (see §6.x):** merge the theme dictionary into each Window/UserControl's **own `.Resources`** via pack-URI `Source`, **not** `App.Resources` — so the existing smoke test still constructs windows.
- Add a **"no orphan hex" lint test** (mirrors the C3 parity-test instinct): fail CI if a new `#RRGGBB` literal appears in a view outside the theme files.
- **Nothing visually changes yet** — tokens exist alongside current inline values.

### D2 — Adopt tokens, screen by screen
Migrate MainWindow → CalibrationCardView (collapse the 5× `SegmentedScale`) → NotificationCard → Settings → popups, one PR each, deleting duplicated styles as you go. UVHS screenshot diff per screen; all existing tests stay green.

### D3 — Accessibility pass
Reduce-Motion master switch; status triple-coding; contrast fixes (retire `#7B8DB0`/`#9AB6E0` for essential text); FocusRing; `AutomationProperties` everywhere; Narrator smoke gate.

### D4 — IA & content
Merge Dashboard + Eye-Protection → Home + Insights; plain-language layer + Clinician toggle; `.resx` + single-language mode; onboarding redesign.

### D5 — Themes
Dark mode + high-contrast theme (pure re-skin on D1 tokens).

### D6 — Polish & motion
Eased overlay transitions; micro-interactions within the reduce-motion budget; icon-set unification; final visual QA at 100/125/150 % DPI, light/dark/high-contrast.

**Suggested fit against engineering waves:** D0–D1 can start now (pure additive scaffolding, low risk). D2 should trail behind any *open* wave that edits the same view to avoid merge churn — in particular **EM5 / NE26 / NE27 / NE30 / H7 (Wave 4)** touch [NotificationCardWindow](../src/SenseEase.App/Views/NotificationCardWindow.xaml) and [OverlayWindow](../src/SenseEase.App/Views/OverlayWindow.xaml.cs); land those first, then re-skin. D3–D6 are post-Alpha-cut polish that materially improve the tester experience without touching safety/persistence code.

---

## 6.x The one real architectural risk — and how to clear it

[App.xaml](../src/SenseEase.App/App.xaml) deliberately keeps `Application.Resources` empty, and the `BooleanToVisibilityConverter` lives in `MainWindow.Resources`, *specifically* so [MainWindowSmokeTests.cs](../tests/SenseEase.Tests/MainWindowSmokeTests.cs) can construct the window. That test creates a **bare `new Application()`** (line 29) — it never instantiates `SenseEase.App.App`, so **App.xaml resources are never loaded in the test path.**

**Therefore:** if the design tokens go into `App.Resources`, every `{StaticResource Token}` in a view will throw at construction during the smoke test → red CI → an [ALPHA_HANDOVER §3.8](ALPHA_HANDOVER.md) "broke prior works" violation.

**The safe pattern (D1):** ship tokens in a standalone `Theme/SenseEaseTheme.xaml` and merge it into **each view's own `.Resources`** by pack URI:
```xml
<UserControl.Resources>
  <ResourceDictionary>
    <ResourceDictionary.MergedDictionaries>
      <ResourceDictionary Source="/SenseEase.App;component/Theme/SenseEaseTheme.xaml"/>
    </ResourceDictionary.MergedDictionaries>
  </ResourceDictionary>
</UserControl.Resources>
```
WPF caches by source URI, so there is no duplication cost, and the resources resolve in the smoke test because they are view-local — exactly the reasoning that already governs the converter's placement. (Alternative: update the smoke harness to boot `SenseEase.App.App` — but that edits a test, which §3.8 says must be **flagged explicitly**, not done silently. The view-local merge avoids touching tests at all and is preferred.)

This is the kind of codebase-specific trap the team's own handover warns about; calling it out up front is the difference between a clean design track and a week of regression churn.

---

## 7. Decisions I need from you (product owner)

1. **Ambition:** full visual re-brand toward "Calm" (recommended — the §3 palette), or polish-within-the-current-language (faster, smaller delta)?
2. **Dark mode:** build it for Alpha (recommended for this audience), or defer to Beta?
3. **Language strategy:** move to single-language + toggle + `.resx` (recommended; better for both audiences), or keep bilingual-inline for the closed tester pool?
4. **Engineering telemetry:** hide behind a **Clinician/Advanced** toggle (recommended), or keep it on the default dashboard for the research stakeholders?
5. **Timing:** start D0–D1 now in parallel with the safety waves, or hold the entire design track until after Alpha cut?

---

## 8. Appendix — quick references

**Plain-language translation table (for D4):**
| Engineering term | User-facing |
|---|---|
| Optical Flow Index | Screen motion |
| VIMS Pressure / Leaky Integrator | Strain building |
| FOV Radius / Clear zone | Comfort zone |
| Mask active | Easing your view |
| FMS Score | Comfort score |
| Habituation Engine / progress | Building tolerance |
| Driver / Passenger mode | Active / Watching |

**Token starter values:** see §3.1–3.6. These are a designer's opening proposal — to be locked on a contrast-checked style tile in D0 before any code lands.

---

*End of plan. Nothing here changes engineering behaviour, safety floors, or persistence; the design track is purely presentational and is structured to land without breaking a single existing test.*
