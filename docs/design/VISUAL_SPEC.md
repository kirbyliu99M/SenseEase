# SenseEase — Pillar 1: Per-Surface Visual Redesign Spec

> **Rebuild header-note (2026-06-06):** As of 2026-06-06 the engine uses the NATIVE 12-level intensity scale (Q2=B) that REPLACED VimsTier 1–10 + the 3-band model, projected to the 5級 動態感 comfort display — so any "VimsTier 1–10 / 3-band" wording below describes the now-retired implementation detail, while the design INTENT (comfort display) still holds.

<!-- Generated 2026-06-05 by the senseease-rebrand-design workflow (31 agents). Companion to docs/UIUX_UPGRADE_PLAN.md. Draft for review — no code changed. -->

> Generated 2026-06-05 by the senseease-rebrand-design workflow (31 agents). Companion to docs/UIUX_UPGRADE_PLAN.md. Draft for review — no code changed.

# Home / Today (redesigned Dashboard tab)

`PressureChartCanvas` lives in the Eye-Protection tab (line 575), not the Dashboard tab — so the Dashboard redesign doesn't touch the chart code-behind. The Dashboard tab itself references no `x:Name` from code-behind. The only code-behind name in the Dashboard scope is none; `MainTabs` is the TabControl wrapper. I now have decision-grade evidence. Here is the spec.

---

# Home / Today — Redesigned Dashboard Tab (Calm Visual Redesign Spec)

*Surface: `MainWindow.xaml` Tab 1 (`Header="Dashboard"`, lines 168–480). Scored against Immersive-Design Guardrails A–D; order of authority Clinical > Privacy > Engineering > Product > brand.*

## 1. Purpose

**Single job:** Answer one question the user is silently asking every time they open this window — **"Am I protected right now, and is SenseEase quietly handling things?"**

The Dashboard is the **comfort-state home**, not a telemetry console. It must lead with the answer (a calm, plain-language status), then offer the two or three things the user might actually want to do (run setup, keep the overlay on, pace habituation), and bury raw numbers as opt-in detail. The redesign re-frames the surface from *"here are my five engineering scalars"* to *"you're protected — here's your comfort state and your controls."* This directly serves the North Star (Clarity: one idea per screen, plain language not telemetry) and resolves the audit fact that engineering telemetry (OFI, VIMS Pressure, Leaky Integrator, FMS, FOV Radius) is currently the dashboard headline.

The detailed numeric trace, session stats, and the live interception chart remain available — they already live on the **separate "護眼儀表板 / Eye Protection" tab** (lines 483–638), which is the correct home for diagnostics. The Dashboard stops competing with it.

---

## 2. Current Problems (cited)

**Telemetry-as-headline (Clarity violation).** The four hero cards are `Optical Flow Index` (`:213`), `VIMS Pressure` + `Leaky Integrator` sub-label (`:226,232`), `FOV Radius` + `Clear zone` (`:239,245`), and `Current Mode` (`:252`) — each rendered as a 32px `CardValue` number (`CardValue` style `:79–84`). The user's literal first impression is four raw sensor readouts with engineering names. There is **no plain-language comfort statement anywhere on the surface.**

**Two clashing palettes on one screen.** The dashboard canvas is light (`Background="#E2ECFF"` `:150`) but the first-run welcome banner is a **dark card** (`Background="#0E1320"`, `BorderBrush="#3D5AFE"` `:178`) with `#FF9B6B` orange eyebrow text (`:187`) and `#9AB6E0` skip text (`:194`). This is the documented light-console-vs-dark-popup clash, sitting at the very top of the first-run experience.

**Color-only status, below-contrast text (WCAG 2.2 / colorblind defects).** The mask indicator is a bare 8px `Ellipse` whose only state channel is `Fill="#E54747"` vs `#B0BFD8` (`:255–266`) — meaning is conveyed by **hue alone** (`AutomationProperties = 0` confirmed). Essential labels use `CardLabel` `Foreground="#7B8DB0"` (`:75`), ~3.6:1 on white — **fails AA** for the OFI/Pressure/FOV captions, the FMS strip, and the always-on/habituation labels.

**Alarm red as a comfort color.** `MaskActive` flips text and the dot to `#E54747` (`:261,274`); the VIMS Pressure bar foreground is `#E54747` on `#FFEAEA` (`:230`); the "Reset State" and (collapsed) "Stop Demo" buttons use `AlertBtn` = `#E54747` (`:64–66, 322, 332`). Per Guardrail D2/A5, comfort and destructive states must use **muted clay `#B65C4E`**, never alarm red.

**Duplicated, ad-hoc styles.** `Card` uses a **blue** drop shadow `#5B82C6` (`:28`) — retired by Elevation §3.3 (shadow is always neutral black). Radius soup: `Card` 14 (`:24`), `RoundBtn` 6 (`:37`), banner 10 (`:177`), status strip 8 (`:350`). Button padding `22,10` (`:58`) and `14,6` (`:196`) are ad-hoc. Three `Separator Background="#E8EEFF"` hairlines (`:375,413`) use a tinted separator, not the neutral `Separator` token.

**Bilingual strings crammed inline.** "保持遮罩開啟 / Keep overlay visible" (`:385`), "再適應 Habituation Engine" (`:414`), the 50-char bilingual helper (`:389`) — all packed into single `Text=` attributes with `/` separators, hard to scan and impossible to lay out per-language.

**Bare `CheckBox` for a settings boolean.** `PersistentOverlayEnabled` is a raw `CheckBox` (`:384`) — Design System §4 mandates `Toggle.Switch` for every settings boolean.

**Raw-number font-size floor violations.** Timestamp/sub-label sizing across the window uses `FontSize="10/11"` (e.g. chat `:694,706,815`); on this tab the `CardLabel` is 12 (OK) but the "/ 150" and "Leaky Integrator" sub-labels are pure telemetry noise regardless of size.

**Density.** Four equal-weight stat cards + a dense multi-control "Controls" card + a 6-column `GridView` event log (`:455–474`, columns Time/OFI/Pressure/FOV Radius/Mask/Action) stack with no clear hierarchy — everything shouts equally. The event log is a raw spreadsheet of `OFI / Pressure / FOV Radius` — diagnostics surfaced as primary content.

---

## 3. Redesign

All elements consume only Calm tokens / Design-System components. Tokens are merged **view-locally** per §7.2 (the `pack://…/Theme/SenseEaseTheme.xaml` bundle in `Window.Resources`), never `App.Resources`, so `MainWindowSmokeTests` still constructs the window.

### 3.1 Layout structure (top → bottom)

```
┌──────────────────────────────────────────────────────────────────────────┐
│  HEADER (existing app chrome, re-skinned: Surface bg, TextPrimary,        │
│          Accent #3A6EA5 sensing-mode Badge — not a #024AD8 band)          │
├──────────────────────────────────────────────────────────────────────────┤
│  Tabs:  [ Today ]  Insights   Assistant   Settings        ← renamed       │
│         (TabItem: Type.Body, TextSecondary rest / Accent + 2px underline   │
│          selected; FocusRing on keyboard focus)                            │
│                                                                            │
│  ScrollViewer ▸ StackPanel  (Pad: 24 sides, Gap.Stack 16 between cards)    │
│                                                                            │
│  ① FIRST-RUN CARD  (Card, Elev.2, only if ShowFirstRunBanner)             │
│  ┌────────────────────────────────────────────────────────────────────┐  │
│  │ ◐ Lightbulb   Welcome to SenseEase                  [Type.Heading]  │  │
│  │               歡迎使用 SenseEase                      [Type.Caption] │  │
│  │  Run the 60-second setup so SenseEase can suggest      [Type.Body]  │  │
│  │  comfort settings that fit you.                                     │  │
│  │  先跑 60 秒設定，依您的反應建議個人化預設值。           [Type.Caption]│  │
│  │                                   [ Skip ]  [ Run setup ]           │  │
│  │                                   Btn.Quiet  Btn.Primary            │  │
│  └────────────────────────────────────────────────────────────────────┘  │
│                                                                            │
│  ② COMFORT HERO  (Card, Elev.2, Radius.md)   ← the new headline           │
│  ┌────────────────────────────────────────────────────────────────────┐  │
│  │  You're protected                                  [Type.Display]   │  │
│  │  目前受到保護                                        [Type.Caption]  │  │
│  │                                                                     │  │
│  │  ┌─ StatusChip ─────────┐   ┌─ StatusChip (mode) ──────────────┐   │  │
│  │  │ ✓  Comfort steady    │   │ ⤺  Passenger · full protection   │   │  │
│  │  └──────────────────────┘   └──────────────────────────────────┘   │  │
│  │   (Steady teal / Rising amber / High clay — icon+label+color)       │  │
│  │                                                                     │  │
│  │  ──────── ComfortMeter (token-tinted bar, eased) ───────  ░░░       │  │
│  │  Easing strain · 緩解中            (one plain sentence)  Type.Body  │  │
│  └────────────────────────────────────────────────────────────────────┘  │
│                                                                            │
│  ③ PROTECTION CONTROLS  (Card)                                            │
│  ┌────────────────────────────────────────────────────────────────────┐  │
│  │  PROTECTION              ← Section.Header (Type.CaptionStrong)       │  │
│  │  ┌─ List.Row ───────────────────────────────────────────────────┐  │  │
│  │  │ Keep protection always on      [Toggle.Switch ●—]            │  │  │
│  │  │ Periphery stays gently dim; deepens on motion                │  │  │
│  │  ├─ List.Row (enabled only when toggle on) ────────────────────┤  │  │
│  │  │ Baseline dimming        ───────●────  L4   (Slider + value)  │  │  │
│  │  └──────────────────────────────────────────────────────────────┘  │  │
│  │                                                                     │  │
│  │  HABITUATION              ← Section.Header                          │  │
│  │  ┌─ List.Row ───────────────────────────────────────────────────┐  │  │
│  │  │ Building your tolerance        [ Pause ]  Btn.Quiet          │  │  │
│  │  │ ▓▓▓▓▓▓░░░░  62%  ·  builds over weeks of calm  ComfortSteady │  │  │
│  │  └──────────────────────────────────────────────────────────────┘  │  │
│  │                                                                     │  │
│  │                            [ Run setup again ]   Btn.Ghost          │  │
│  │  ·· Reset visual settings        ← Btn.Quiet, de-emphasised, last  │  │
│  └────────────────────────────────────────────────────────────────────┘  │
│                                                                            │
│  ④ RECENT ACTIVITY  (Card, COLLAPSED by default — disclosure)            │
│  ┌────────────────────────────────────────────────────────────────────┐  │
│  │ ▸ Recent activity                      Latest 12 entries  (chevron) │  │
│  │   (expanded → the existing ListView, re-skinned; plain-language      │  │
│  │    "Action" column leads; numeric columns optional/secondary)        │  │
│  └────────────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────────────┘
```

> The raw OFI / VIMS Pressure / FOV Radius / FMS numbers are **removed from the Dashboard** and remain on the **Insights tab** (the existing Eye-Protection tab, which already shows all five at `:492–537`). This is a *relocation*, not a deletion — no binding is destroyed; the same `{Binding OpticalFlowIndex}` etc. still render on the other tab. Guardrail C5 (don't break prior works) is satisfied because every VM property is still bound somewhere.

### 3.2 Element-by-element spec (components, tokens, states)

**App header (re-skin only).** Replace `Background="#024AD8"` band (`:114`) with `Surface`; title "SenseEase" → `Type.Title` `TextPrimary`; tagline → `Type.Caption` `TextSecondary` (retire `#8ABAFF` `:125`, an engineering subtitle — soften to "Quietly easing visual strain"). The sensing-mode pill (`:128–145`) becomes a `Badge.Neutral` for live modes; DEMO uses `Badge` tinted `ComfortRisingWeak`/`#7A4E16`, SCREEN CAPTURE uses `Badge.Accent`. **Keep the `SensingModeLabel` binding and all three `DataTrigger` values verbatim** ("DEMO MODE", "SCREEN CAPTURE") — only the brushes change.

**Tabs.** Rename headers to plain language: `Dashboard → Today`, `護眼儀表板 / Eye Protection → Insights · 洞察`, keep `AI Assistant → Assistant`, `設定 → Settings · 設定`. `TabItem` style: `Type.Body`, rest `TextSecondary`, selected `Accent` + 2px `Accent` bottom border + `FocusRing` on keyboard focus. Retire `#7B8DB0`/`#024AD8` (`:155,161`).

**① First-run card.** `Card` (`Surface` bg, `Radius.md`, `Pad.Card` 24, `Elev.2`) — **eliminates the dark `#0E1320` clash** (`:178`). Leading `Icon.Lightbulb` (Fluent Regular, 20px, inherits `TextPrimary`). Title `Type.Heading` `TextPrimary`; Chinese line `Type.Caption` `TextSecondary` on its own line (un-cram the `/`). Body `Type.Body`. Buttons: **Skip = `Btn.Quiet`** (retires `#9AB6E0` transparent btn `:192`), **Run setup = `Btn.Primary`** (`Accent`, retires `#3D5AFE` `:200`). Keep `Visibility="{Binding ShowFirstRunBanner, Converter={StaticResource BoolToVis}}"`, `SkipSetupCommand`, `StartSetupDemoCommand` exactly.

**② Comfort Hero (new headline card).** `Card`, `Elev.2`, `Radius.md`, `Pad.Card`.
- **Hero line:** `Type.Display` (28/34 SemiBold) `TextPrimary`, driven by a new VM-computed `ComfortHeadline` string *or* a converter on the **existing `VimsTier`** — "You're protected" (tier ≤5) / "Easing strain" (6–7) / "Actively easing" (8–10). To stay within "don't add behavior," prefer a **pure `IValueConverter` (`TierToHeadlineConverter`) bound to `VimsTier`** so no VM change is required. Chinese sub-line `Type.Caption` `TextSecondary`.
- **Comfort StatusChip** (`Status.Chip`, mandatory triple-coding): bound to `VimsTier` via a converter selecting `Steady` (≤5, check-circle, `ComfortSteadyWeak`/`#1F5249`) / `Rising` (6–7, alert-triangle, `ComfortRisingWeak`/`#7A4E16`) / `High` (8–10, shield/waves, `ComfortHighWeak`/`#8A3A2E`). Icon + label + color — survives colorblindness/HC. Replaces the bare ellipse (`:255`).
- **Mode StatusChip:** bound to existing **`Mode`** / `AutoModeText` — "Passenger · full protection" vs "Driver · lighter protection" per Guardrail D1 (must not invert meaning). Use `Status.Chip` `Idle`/neutral variant styling. Keep `{Binding Mode}` and `{Binding AutoModeText}`.
- **ComfortMeter:** a slim token-tinted bar (height 6, `Radius.pill`) whose **fill brush** is `ComfortSteady`/`ComfortRising`/`ComfortHigh` by tier and whose value binds to the **existing `VimsTier`** (1–10) — *not* raw Pressure (Pressure is unbounded and clinical-internal). This replaces the alarm-red `#E54747` pressure bar (`:230`). One plain sentence below (`Type.Body`): "All calm" / "Strain building" / "Easing strain now · 緩解中".
- **States:** rest as above; **no hover** (it is a status display, not interactive); the meter and chip transition per §4 (always eased, Reduce-Motion-exempt for the comfort channel only). Empty/loading: before first state tick, show `Idle` chip ("Getting ready…") — never a blank or spinner.

**③ Protection Controls.** `Card`. Two `Section.Header`s (`Type.CaptionStrong` `TextSecondary`): "PROTECTION" and "HABITUATION", grouped `List.Row`s inside a `SurfaceMuted` container (`Radius.md`, 1px `Separator` between rows).
- **Row "Keep protection always on":** `List.Row` with trailing **`Toggle.Switch`** bound to **`PersistentOverlayEnabled`** (replaces bare `CheckBox` `:384`). Helper `Type.Caption` `TextSecondary`: "Periphery stays gently dim; deepens on motion · 周邊維持暗角，偵測動態時加深". `AutomationProperties.Name` = row title.
- **Row "Baseline dimming":** `List.Row`, `IsEnabled="{Binding PersistentOverlayEnabled}"` (keep), `Slider` bound to **`PersistentOverlayBaselineTier`** (Min 1 Max 10, snap-to-tick — keep verbatim), trailing value `Type.BodyStrong` `Accent` `L{0}` (keep `StringFormat='L{0}'`). Relabel "基準強度 / Baseline intensity" → "Baseline dimming · 基準暗度".
- **Row "Building your tolerance":** `List.Row`. `ProgressBar` bound to **`HabituationLevel`**, foreground `ComfortSteady` (retire `#2BA672` `:423`), track `SurfaceSunken`. Trailing **`Btn.Quiet`** bound to **`HabituationButtonText`** / **`ToggleHabituationCommand`** (replaces `GhostBtn` `:428`; Pause/Resume is low-emphasis). Helper: "Builds over weeks of calm · 持續累積耐受度".
- **"Run setup again":** `Btn.Ghost` → **`StartSetupDemoCommand`** (the existing "Calibration & Demo" button `:309`, relabeled). Keep tooltip intent.
- **"Reset visual settings":** `Btn.Quiet`, de-emphasized, placed last and visually separated. **Per Guardrail D3 this must NOT be a single ambiguous "Reset."** Keep it wired to **`ResetStateCommand`** (the existing Reset State, `:332`) — which already calls the narrowed transient reset, never `_habituation.Reset()`. **Retire its `AlertBtn` `#E54747` styling** (D3/A5: never alarm red). A confirm step is required if `ResetStateCommand` is destructive — flag to engineering (see Migration).
- The collapsed `Stop Demo` button (`Visibility="Collapsed"`, `:322`) stays collapsed; its `StopDemoCommand` binding is preserved untouched.
- **FMS strip removed** from Dashboard (`:349–369`) — pure telemetry; `FmsScore`/`OverlayVisible` still bound on Insights. (`OverlayVisible` currently appears only here; relocate the "Overlay: Visible/Hidden" line to Insights so the binding survives.)

**④ Recent activity (disclosure).** `Card` containing an `Expander`-style header (chevron `Icon.ChevronDown`, `Type.Heading` "Recent activity", trailing `{Binding EventLog.Count, StringFormat='Latest {0} entries'}` as `Type.Caption` — keep binding `:451`). **Collapsed by default.** Expanded → the existing `ListView ItemsSource="{Binding EventLog}"` (`:455`) re-skinned: `LogListView` foreground → `TextSecondary`, header style → `SurfaceMuted`/`TextSecondary` (retire `#EEF3FF`/`#024AD8` `:102–103`). **Reorder columns so the plain-language `Action`/`Message` column leads**; numeric columns (OFI/Pressure/FOV Radius) move right and narrow — diagnostics, not headline. Keep all `DisplayMemberBinding` paths (`Time/Ofi/Pressure/FovRadius/MaskSnapshot/Message`) verbatim.

### 3.3 Elevation / radius / spacing summary
- Cards: `Elev.2` (`Y2 Blur10 Op0.08`, **neutral black** — retires blue `#5B82C6` `:28`), `Radius.md` (12), `Pad.Card` (24), `Gap.Stack` (16) between.
- Buttons: `Radius.sm` (8), `Pad.Button` (14,8). Switch thumb/track per §4.
- Hairlines: `Separator` token `#E2E5EA` (retire `#E8EEFF` `:375,413`).
- Status strips / grouped containers: `SurfaceMuted`, `Radius.md`.

---

## 4. Motion & Reduce-Motion

**Tokens:** `Dur.fast=120ms`, `Dur.base=200ms`, `Dur.slow=280ms`, easing `Ease.calmOut` cubic-bezier(0.2,0,0,1).

| Element | Motion (normal) | Reduce-Motion |
|---|---|---|
| **ComfortMeter fill + ComfortChip color** | Cross-fade value/brush over `Dur.base` with `Ease.calmOut` | **Still eased over ≥`Dur.base`** — this is a *comfort-state luminance/hue* change; per Guardrail A4 it must never snap. Bind to the safety channel, not `Dur.ui`. |
| **First-run / hero card appear** | Fade+rise `Dur.slow` | Instant swap (duration→0) |
| **Toggle.Switch thumb** | Slide `Dur.fast` | Snap |
| **Baseline-row enable/disable, Recent-activity expand** | Height/opacity `Dur.base` | Instant |
| **StatusChip swap on tier-band crossing** | Cross-fade `Dur.fast` | Instant icon/label swap (no fade) |

**No flashing, pulsing, breathing, or color-cycling** anywhere on this surface (Guardrail A4). There is **no decorative motion at all** — the only animation is the eased comfort-state transition (safety) and the standard control micro-transitions (chrome, governed by `Dur.ui`/`ReduceMotion`). The Dashboard never animates the mask itself (the mask is `OverlayWindow`, out of scope).

`ReduceMotion = manualToggle OR NOT SystemParameters.ClientAreaAnimation`, driving the `Dur.ui` `DynamicResource` per §3.4. The comfort channel uses a **separate, non-overridable** `Dur.base`-floored duration so a future edit cannot introduce a hard comfort-state cut.

---

## 5. Accessibility

**AutomationProperties.Name (currently 0 on this surface):**
- Comfort hero card: `"Comfort steady, you're protected"` / `"Strain rising, easing now"` / `"High strain, actively easing"` (full phrase, swaps with tier band).
- Comfort StatusChip & Mode StatusChip: full state phrase each ("Passenger mode, full protection").
- Keep-protection Switch: `Name="Keep protection always on"`, `AutomationProperties.HelpText` = the helper sentence.
- Baseline slider: `Name="Baseline dimming level"`, announces `L{value}`.
- Habituation: progressbar `Name="Tolerance building progress"`; Pause/Resume button `Name` follows `HabituationButtonText`.
- Run-setup / Reset-visual-settings / Recent-activity expander: explicit `Name`s.
- Sensing-mode badge: `Name="Sensing mode: {SensingModeLabel}"`.

**Focus order (Tab key):** First-run Skip → Run setup → [hero card is not a tab stop; chips are `AutomationProperties.Name`'d but `IsTabStop=false` status displays] → Keep-protection Switch → Baseline slider → Pause/Resume → Run setup again → Reset visual settings → Recent-activity expander → (expanded) ListView. Every focusable control shows the 2px `FocusRing` + 1px inset (`#3A6EA5` light / `#7CA9CE` dark).

**Contrast (all verified in Calm spec §1.2):** `TextPrimary #1A2230` on `Surface` = 15.3:1 (AAA); `TextSecondary #525B6B` on `Surface` = 7.4:1 (AAA) — **fixes the `#7B8DB0` ~3.6:1 failures** at `:75` (every CardLabel) and `:579`; `Accent #3A6EA5` link/value on `Surface` = 4.9:1 (AA); `TextOnComfort` white on `ComfortSteady`/`ComfortHigh` ≥4.5:1 (AA); chip texts `#1F5249`/`#7A4E16`/`#8A3A2E` on their weak backgrounds 6.0–7.8:1 (AA/AAA).

**Colorblind-safe status (triple-coded):** every status now carries **icon + label + color** — comfort chip (check/triangle/shield glyph), mode chip (label), meter (paired with sentence). Replaces the hue-only ellipse (`:255`, `:543`). Under High-Contrast the OS owns hue; the glyph+label carry meaning (Calm §1.4).

**Min sizes & reflow:** body ≥14px (`Type.Body`), captions ≥12 (`Type.Caption`) — **no 10/11px** on this surface. List rows `MinHeight 48`; switch/slider hit targets ≥44px. **No fixed `Height` on text containers** — use `MinHeight`+`Auto`; verify reflow at Windows 125%/150% display scaling and the "Make text bigger" 100–225% slider. The current `Height="280"` chart (`:574`) is on Insights, not here; the redesigned Dashboard sets no fixed content heights.

---

## 6. Migration Notes (cannot break prior works)

**Bindings to preserve verbatim** (all confirmed present in `src/SenseEase.App/ViewModels/MainViewModel.cs`):
`SensingModeLabel` (:689), `ShowFirstRunBanner` (:943), `SkipSetupCommand`, `StartSetupDemoCommand`, `StopDemoCommand`, `ResetStateCommand`, `Mode` (:718), `MaskActive` (:734), `VimsTier` (:745), `AutoModeText` (:760), `ActivityStatus` (:754), `PersistentOverlayEnabled` (:795), `PersistentOverlayBaselineTier` (:810), `HabituationLevel` (:763), `HabituationButtonText` (:780), `ToggleHabituationCommand`, `EventLog` (:853) + `EventLogEntry` members `Time/Ofi/Pressure/FovRadius/MaskSnapshot/Message`. The `BoolToVis` converter **must remain in `Window.Resources`** (`:19`) — relied on by the smoke test path.

**Relocate, do not delete.** `OpticalFlowIndex`, `Pressure`, `FovRadiusPercent`, `FmsScore`, `OverlayVisible` leave the Dashboard but are still bound on the Insights/Eye-Protection tab (`:492–537`) — except `OverlayVisible` and `FmsScore`, which today render *only* on the Dashboard FMS strip (`:352–367`). **Action: move that "FMS Score / Overlay Visible" line to the Insights tab** so both bindings remain live. A property with **zero** XAML consumers won't break compilation (WPF binds are late-bound), but keeping them bound preserves intended UX and avoids dead VM members.

**New artifacts (visual-only, no behavior change — Guardrail C5):**
- `TierToHeadlineConverter`, `TierToComfortChipConverter`, `TierToComfortBrushConverter` — pure `IValueConverter`s reading the existing `VimsTier`. No VM edits.
- Design-system components (`Status.Chip`, `Toggle.Switch`, `List.Row`, `Section.Header`, `Btn.*`, `Card`) come from `Theme/Controls.xaml`, merged view-locally via `pack://…/SenseEaseTheme.xaml` (§7.2) so **`MainWindowSmokeTests` still constructs the window** (it builds a bare `new Application()` and never loads `App.Resources`).

**`x:Name` / code-behind safety:** the Dashboard tab references **no** `x:Name` from `MainWindow.xaml.cs`. The chart `PressureChartCanvas` (`:575`) and `ChatScroll`, `SettingsTabContent`, `MainTabs` all live in **other tabs** (Insights/Assistant/Settings) — none are touched by this redesign. `MainTabs.SelectedIndex = 2` logic (xaml.cs `:148`) depends on **tab order**; **keep Dashboard at index 0** and do not reorder tabs.

**Tests that touch this view — one, and how it stays green:**
- `tests/SenseEase.Tests/MainWindowSmokeTests.cs` constructs `MainWindow`, runs the dispatcher, and fires `OnStateChanged(..., new SenseState { VimsTier = 4 / 7 })` (`:75–79`). It asserts **no binding error / exception during construction + first layout**. Risks: (a) a `{StaticResource Token}` that resolves only from `App.Resources` would throw — **mitigated** by view-local merge; (b) a `TwoWay` binding to a read-only property would throw (the documented `VimsTier` regression `:14–15`) — **all new `VimsTier` bindings are `Mode=OneWay`** (converters are read-only), and `PersistentOverlayBaselineTier`/`PersistentOverlayEnabled` keep their existing two-way bindings to settable properties. Run `dotnet test` on this project before requesting visual review.

**Behavior-change flags for the PR (Guardrail C5/C6 — do not self-merge):**
1. **"Reset visual settings"** is re-styled from `AlertBtn` and (recommended) gains a **confirm step**. If `ResetStateCommand` currently executes immediately, adding a confirm is a **behavior change** — flag explicitly; per D3 a one-tap reset is disallowed but the *command itself* (transient reset) is unchanged.
2. Removing the FMS/Overlay strip from Dashboard and relocating to Insights is a **layout move** — call it out so the visual reviewer expects those numbers on the other tab.
3. Tab **renames** (Dashboard→Today, Eye Protection→Insights) are copy changes; confirm no test asserts on `TabItem.Header` text (none found).

**Hard-stop compliance:** no mask depth/floor touched (A2 — mask is `OverlayWindow`, out of scope); no escape-hatch surfaced (A3); no webcam/preview UI (B1/B3); `AppId` untouched (C3); grid untouched (C1); tier→band mapping consumed read-only via converters, not duplicated, no 4th band (D2); habituation offset never zeroed — Reset stays on the narrowed `ResetStateCommand` (D3); alarm red `#E54747` fully retired in favor of `ComfortHigh #B65C4E` (A5/D2). Commit only on explicit request; visual review is a user hard-stop before any tag/merge (C6).

---

**Evidence files read:** `src/SenseEase.App/MainWindow.xaml` (Dashboard tab lines 168–480; full file for context), `src/SenseEase.App/ViewModels/MainViewModel.cs` (binding signatures lines 689–943), `src/SenseEase.App/MainWindow.xaml.cs` (code-behind `x:Name` usage — `PressureChartCanvas`/`ChatScroll`/`SettingsTabContent`/`MainTabs`, none in Dashboard scope), `tests/SenseEase.Tests/MainWindowSmokeTests.cs`.

---

# Insights (merge of the Eye-Protection tab; chart + session summary)

I have complete decision-grade evidence. Delivering the spec.

# Insights — Visual Redesign Spec (Calm)

> Surface scope: the merge of the current **"護眼儀表板 / Eye Protection"** tab (`MainWindow.xaml:482–638`) into a single calm **Insights** surface — live trace chart + session summary. The cross-component live-metrics strip (`:488–565`) is folded in. The tab is renamed **Insights / 洞察** (display string only; per **C3** this never touches AppId, and per **D2** band semantics are preserved through `TierColorPalette`).

## 1. Purpose

**Single job:** answer the user's one question — *"Did SenseEase actually protect me, and is it working right now?"* — with a calm, plain-language reassurance ("Protected you N times today"), not an engineering oscilloscope.

This surface is **retrospective + at-a-glance live**, not a control panel: there are no actions to take here, no settings to change. It must read as a quiet "report card" the overstimulated user can glance at and feel reassured by, then leave. Everything that is currently engineering telemetry (Optical Flow Index, VIMS Pressure, Leaky Integrator, FOV Radius numerals) is demoted from headline to an opt-in **Advanced / 進階** disclosure — the comfort state and the protection count are the headline.

## 2. Current Problems (cited)

**Engineering telemetry as the headline (Clarity violation).**
- The live strip headlines five raw engineering scalars at 22px bold: `Optical Flow Index`, `VIMS Pressure`, `VIMS Tier … / 10`, `FOV Radius %`, `Mask State` (`MainWindow.xaml:490–538`). None of these is the question the user is asking. "VIMS Pressure", "Optical Flow", "FOV Radius" are internal model names exposed verbatim.
- The chart caption is raw jargon: `"即時 OFI 與 VIMS Pressure 軌跡;紅色虛線為 mask 啟動閾值 (85)"` (`:572`) and the section title `"VIMS 干預監測 / Interception Monitor"` (`:570`).
- Session summary stat labels are acceptable-ish but still telemetry-flavored: `Peak Optical Flow`, `Peak VIMS Pressure` (`:608, 615`).

**Two clashing palettes / dark-light clash.**
- This tab lives on the light `#E2ECFF` console canvas (`MainTabs`, `:150`) — itself a saturated blue that the Calm direction retires to `Canvas #F6F7F9`. The whole surface is saturated royal blue (`#024AD8`) for *every* number (`:493, 500, 506, 515, 521, 599, 606, 613, 620, 627`), which is the loud-chrome anti-pattern the Calm direction forbids (content should be the comfort state, not the chrome).

**Alarm red, used for status (D2 / A5 violation).**
- Mask ACTIVE state renders in alarm red `#E54747` (`:529`) — but "mask active" is the product *succeeding*, not an error. The chart trigger line is also alarm red `#E54747` (`MainWindow.xaml.cs:1252`; legend swatch `MainWindow.xaml:585`). Per the Calm system there is **no alarm red anywhere**; high-strain is muted clay `ComfortHigh #B65C4E`.

**Color-only status dots (WCAG 2.2 / colorblind-safe + A5 violation).**
- The OS-chrome suppression status is a bare 9px `Ellipse` whose only signal is fill color (`#F0832E` amber / `#2BA672` green, `:543–557`) with no icon and no `AutomationProperties`. The chart legend uses bare 8px dots for OFI/Pressure (`:579, 582`). `AutomationProperties = 0` across the file.

**Contrast failures on essential text.**
- Every stat sub-label uses `CardLabel` = `#7B8DB0` (`:74–77`), which is ~3.6:1 on white — fails AA for the small (12px) essential labels that name each number.
- The chart-area OFI polyline is drawn in `#7B8DB0` (`MainWindow.xaml.cs:1240`) — the same failing grey, used as an essential data series.

**Density + duplication.**
- Two near-identical 5-up `UniformGrid` stat blocks (`:490` live strip, `:596` session summary) with hand-repeated `HorizontalAlignment="Center"` + 22/26px bold + `#024AD8` on every cell — a per-cell copy-paste of the same pattern five-plus times each, with no shared component.
- Card style `Card` (`:22–32`) carries the retired blue `#5B82C6` drop-shadow and `CornerRadius="14"` (retired radius); `SectionTitle`/`CardLabel` are per-window styles using retired navy `#0A1F5C` and the failing grey.

**Motion.**
- The live chart is a continuous real-time polyline redrawn at ~15 Hz (`_chartTimer` 67ms, `MainWindow.xaml.cs:181–187`). For a motion-sensitive user this is a constantly-twitching line with no Reduce-Motion path — there are **0 motion-pref checks** today.

## 3. Redesign

### 3.1 Layout (top → bottom)

```
┌────────────────────────────────────────────────────────────────────────┐
│  Canvas (#F6F7F9)   ·   ScrollViewer, content max-width 760, centered    │
│                                                                          │
│  ┌── HERO  (Card, Elev.2, Radius.md, Pad.Card 24) ─────────────────────┐ │
│  │                                                                      │ │
│  │  Type.Caption  TextSecondary   洞察 / TODAY                          │ │
│  │                                                                      │ │
│  │  ┌ StatusChip (Steady/Rising/High/Idle) ─┐   Type.Display 28         │ │
│  │  │ ◎  Comfort steady                      │   "You're protected"     │ │
│  │  └────────────────────────────────────────┘   你正受到保護           │ │
│  │                                                                      │ │
│  │  Type.Body  TextSecondary                                            │ │
│  │  SenseEase eased your view 12 times this session · active 4m20s      │ │
│  │  本次工作階段已協助 12 次 · 遮罩開啟 4m20s                            │ │
│  └──────────────────────────────────────────────────────────────────────┘
│                                                                          │
│  ┌── SESSION SUMMARY ───────────────────────────────────────────────────┐
│  │ Section.Header  TextSecondary   本次工作階段 / THIS SESSION           │ │
│  │  ┌─────────┐ ┌─────────┐ ┌─────────┐ ┌─────────┐                      │ │
│  │  │  StatTile│ │ StatTile│ │ StatTile│ │ StatTile│   (4-up, wraps 2×2) │ │
│  │  │   12    │ │ 4m20s   │ │  42m    │ │  L7     │                      │ │
│  │  │ Times   │ │ Eased   │ │ Watched │ │ Strongest│                     │ │
│  │  │ eased   │ │ active  │ │ calmly  │ │ easing  │                      │ │
│  │  └─────────┘ └─────────┘ └─────────┘ └─────────┘                      │ │
│  └──────────────────────────────────────────────────────────────────────┘
│                                                                          │
│  ┌── COMFORT TIMELINE  (chart) ─────────────────────────────────────────┐
│  │ Section.Header  本次趨勢 / COMFORT OVER TIME                          │ │
│  │ Type.Caption TextSecondary                                           │ │
│  │  Higher = more on-screen motion. The line above the marker is when    │ │
│  │  SenseEase stepped in to ease your view.                              │ │
│  │  ┌── SurfaceSunken, Radius.sm, Height 200 ──────────────────────────┐ │ │
│  │  │                          (PressureChartCanvas — x:Name KEPT)      │ │ │
│  │  │   · · · · · · · · · easing-threshold marker (clay dashed) · · · · │ │ │
│  │  │     ╱╲      pressure (Accent)        ╱╲                            │ │ │
│  │  │  ╱     ╲___╱  motion (TextTertiary)                                │ │ │
│  │  └──────────────────────────────────────────────────────────────────┘ │ │
│  │  Legend (triple-coded chips, not bare dots):                          │ │
│  │   ▬ Strain level   ┄ On-screen motion   ┄┄ Eased here                 │ │
│  │  ▸ Advanced numbers (disclosure, collapsed)                           │ │
│  └──────────────────────────────────────────────────────────────────────┘
│                                                                          │
│  ┌── ADVANCED (collapsed by default; Expander) ─────────────────────────┐
│  │  Optical Flow 12.4 · Strain 41.0 · Tier 7/10 · Clear zone 38%        │ │
│  │  Sensing: Idle (StatusChip)   ·   Mode: Passenger — full protection  │ │
│  └──────────────────────────────────────────────────────────────────────┘
└────────────────────────────────────────────────────────────────────────┘
```

### 3.2 Element-by-element

**A. Hero card (new — the answer)**
- Component: `Card` (`Surface` bg, `Radius.md` 12, `Pad.Card` 24, `Elev.2`). Single card, full width of the centered 760-max column.
- Eyebrow: `Type.Caption` (12) `TextSecondary #525B6B` (AA 7.4:1 on Surface) — "洞察 / TODAY".
- **StatusChip** (`Status.Chip`, §4) — triple-coded, driven by `VimsTier` mapped through the existing 3-band `TierColorPalette` (D2): `Steady` (check-circle, `ComfortSteady`) for tiers 1–5, `Rising` (alert-triangle, `ComfortRising`) for 6–7, `High` (shield/waves, `ComfortHigh #B65C4E`) for 8–10. When `MaskActive` is false **and** tier ≤1, show `Idle` (moon, neutral). Color + icon + label, never color alone.
- Hero headline: `Type.Display` (28/34 SemiBold) `TextPrimary #1A2230` (AAA 15.3:1) — plain reassurance keyed off the band: Steady → "You're protected / 你正受到保護"; Rising → "Easing your view / 正在緩和畫面"; High → "Actively easing / 正在強力緩和". (Copy is a `DataTrigger` on the chip variant — no new binding.)
- Sub-line: `Type.Body` (14) `TextSecondary` — "SenseEase eased your view {SessionFovIntercepts} times · active {SessionMaskActiveText}". **Reuses existing bindings verbatim** (`SessionFovIntercepts`, `SessionMaskActiveText`).

**B. Session Summary (restyle of `:592–634`)**
- `Section.Header` (`Type.CaptionStrong` `TextSecondary`, pad top 24 / bottom 8) — "本次工作階段 / THIS SESSION".
- New reusable **`StatTile`** component (defined once in `Controls.xaml`, replaces the hand-repeated `UniformGrid` cells at `:596–632` and the live-strip cells at `:490–538`): `Surface` tile inside a `SurfaceMuted` grouped container, `Radius.sm`, `Pad` 16, `MinHeight 96`. Value = `Type.Title` (20 SemiBold) `TextPrimary` (demoted from 26px loud blue → calm primary). Label = `Type.Caption` `TextSecondary` (raised from failing `#7B8DB0`). Optional 16px Fluent leading icon inheriting `TextSecondary`.
- Four tiles in a `UniformGrid Rows="1" Columns="4"` that drops to 2×2 at narrow widths (use `MinWidth` per tile, not fixed). Map to plain language:
  - **Times eased** ← `SessionFovIntercepts` (icon `Shield`)
  - **Eased active** ← `SessionMaskActiveText` (icon `Shield`)
  - **Watched calmly** ← `SessionUptimeText` (icon `Clock`)
  - **Strongest easing** ← new readout from `VimsTier` peak, OR keep **Peak strain** ← `SessionPeakPressure` only inside Advanced. Recommend surfacing the *count and time* here; move `SessionPeakOfi`/`SessionPeakPressure` numerals into Advanced (§D).
- `FOV Intercepts` → "Times eased"; `Mask Active Time` → "Eased active"; `Session Uptime` → "Watched calmly"; `Peak Optical Flow` / `Peak VIMS Pressure` → Advanced. All are label changes only; **bindings unchanged**.

**C. Comfort Timeline (restyle of `:567–590`, keep the canvas)**
- `Section.Header` — "本次趨勢 / COMFORT OVER TIME" (replaces `"VIMS 干預監測 / Interception Monitor"`, `:570`).
- Plain-language caption (`Type.Caption` `TextSecondary`, replaces the jargon at `:572`): "Higher = more on-screen motion. The dashed line marks when SenseEase steps in to ease your view. / 數值越高代表畫面動態越多;虛線為系統介入點。"
- Chart well: `SurfaceSunken #ECEEF2` bg (replaces `#F3F6FC`, `:574`), `Radius.sm` 8 (replaces ad-hoc 8 — fine), `Pad` 16,10. **`Height` lowered 280 → 200** to de-emphasize the trace (it is supporting evidence, not the headline). `Canvas x:Name="PressureChartCanvas"` **kept verbatim** — code-behind `EnsureChartInitialized`/`RedrawChart` depends on the name (`MainWindow.xaml.cs:1236, 1257, 1266`).
- **Series recolor (code-behind, `MainWindow.xaml.cs:1237–1255`)** — change the three brush literals to Calm tokens (these are C# `Color.FromRgb` literals, not XAML hex, so the "no orphan hex" lint that excludes code-behind still applies; sourcing them from a `TryFindResource` token is preferred):
  - Strain/pressure polyline `#024AD8` → `Accent #3A6EA5`.
  - Motion/OFI polyline `#7B8DB0` (failing grey) → `TextTertiary #6E7787` (AA-legible) **and** raise `StrokeThickness` to 1.5 and render it under the pressure line so the essential series is the Accent one.
  - Trigger line `#E54747` (alarm red) → `ComfortHigh #B65C4E` (muted clay) — this is the one mandated hue swap per A5/D2; keep the dashed dash-array so it reads as a marker, not an alarm.
- **Legend → triple-coded** (replaces bare dots `:578–588`): three small `Badge.Neutral`-style chips each carrying a line/dash glyph **plus** label: "▬ Strain level (Accent)", "┄ On-screen motion (tertiary)", "┄┄ Eased here (clay)". Shape (solid line / dotted / dashed) carries meaning so it survives colorblindness and HC.

**D. Advanced (new `Expander`, collapsed by default)**
- `Section.Header` "進階 / ADVANCED". Collapsed by default so the telemetry never headlines (Clarity), but is never deleted (preserves engineer/QA visibility, and keeps all existing bindings live → no test breakage).
- Inside: a single `Card.Compact` row of `Type.Mono` (13) `TextSecondary` readouts reusing the *original* bindings the live strip used: `OpticalFlowIndex` ("Optical Flow"), `Pressure` ("Strain"), `VimsTier`/10 ("Tier"), `FovRadiusPercent` ("Clear zone"), plus `SessionPeakOfi`/`SessionPeakPressure`.
- The cross-component status row (`:539–563`) moves here: **`ActivityStatus`** rendered as a `StatusChip` (`Idle` variant = moon + neutral when "Idle"; `Rising` variant = alert-triangle + `ComfortRising` when "Suppressed: …") — replacing the bare color-only ellipse (`:543–557`). **`AutoModeText`** rendered as `Type.Caption` `TextSecondary`; copy stays engine-faithful per **D1** (Passenger = full protection, Driver = lighter). Bindings `ActivityStatus`, `AutoModeText` unchanged.

### 3.3 Tokens, hierarchy, spacing, elevation, radius

| Element | Component | Bg token | Text token | Radius | Elevation | Padding / gap |
|---|---|---|---|---|---|---|
| Surface canvas | — | `Canvas #F6F7F9` | — | — | — | content max-w 760, centered |
| Hero card | `Card` | `Surface` | `TextPrimary` / `TextSecondary` | `Radius.md` 12 | `Elev.2` | `Pad.Card` 24 |
| StatusChip | `Status.Chip` | `Comfort*Weak` | matching dark chip text (e.g. `#1F5249`/`#7A4E16`/`#8A3A2E`) | `Radius.pill` | none | `Pad` 8,2 |
| Stat tiles | `StatTile` | `Surface` in `SurfaceMuted` group | `TextPrimary` value / `TextSecondary` label | `Radius.sm` 8 | `Elev.1` | `Pad` 16, gap `Space.4` 16 |
| Timeline card | `Card` | `Surface` | `TextPrimary` / `TextSecondary` | `Radius.md` 12 | `Elev.2` | `Pad.Card` 24 |
| Chart well | — | `SurfaceSunken` | series = `Accent` / `TextTertiary` / `ComfortHigh` | `Radius.sm` 8 | none (sunken) | `Pad` 16,10; Height 200 |
| Legend chips | `Badge.Neutral` | `SurfaceMuted` | `TextSecondary` | `Radius.pill` | none | `Pad` 8,2 |
| Advanced | `Expander` + `Card.Compact` | `Surface` | `Type.Mono` `TextSecondary` | `Radius.md` | `Elev.1` | `Pad` 16 |

Hierarchy: **Display reassurance (hero) > StatusChip > StatTiles > timeline > advanced**. Saturation is never the hierarchy lever; space + type weight + one neutral shadow carry it (Calm Deference). Inter-card gap `Space.4` (16), section-header pad top `Space.6` (24) / bottom `Space.2` (8).

### 3.4 States

- **Rest:** as above.
- **Hover:** non-interactive surface — cards do not hover-elevate. The Advanced `Expander` header gets `SurfaceMuted` hover + `FocusRing`. StatTiles are display-only (no hover) unless a future drill-in is added (then `QuietButton` row behavior + focus ring).
- **Focus:** only the `Expander` header and the scroll viewport are tab stops; both show the 2px `FocusRing #3A6EA5` + 1px white inset. No focusable element loses a visible ring.
- **Empty (session just started, all zeros):** hero reads "Ready — watching for motion / 準備中,正在監看畫面"; StatTiles show `0` / `0s` (existing `FormatDuration` returns "0s", `MainViewModel.cs:875–880`); chart well shows a centered `Type.Caption` `TextTertiary` placeholder "No motion yet — your trend will appear here / 尚無資料" until `OfiHistory.Count ≥ 2` (the polyline already no-ops below 2 points, `MainWindow.xaml.cs:1286`). StatusChip = `Idle`.
- **Loading:** none — all data is live in-memory; no network/async. No spinner, no "AI is thinking" affordance (and none is permitted near any safety surface, A1 — though no safety surface lives on this tab).
- **Error:** if the chart canvas fails to size (`w<=0||h<=0`, already guarded `:1268`) the well simply shows the empty-state placeholder; never an error dialog. No engine value can error this read-only view.

## 4. Motion & Reduce-Motion

- **Default motion (rationed):** the comfort timeline updates on the existing 67ms coalesced timer (`MainWindow.xaml.cs:181–187`). The polyline simply re-points its existing `PointCollection` (no fade, no easing) — that's acceptable as a *data* update, but the continuous twitch is itself mildly provocative for this audience.
- **Reduce-Motion contract (new — Motion §3.4):** bind a single app `bool ReduceMotion` (= manual toggle **OR NOT `SystemParameters.ClientAreaAnimation`**). When true on this surface:
  1. The chart timer interval is **throttled from 67ms to 1000ms** (a static, slow-updating sparkline rather than a live oscilloscope) — implemented by reading `ReduceMotion` where `_chartTimer.Interval` is set (`MainWindow.xaml.cs:182`); no other code path changes.
  2. StatusChip variant changes swap **instantly** (no cross-fade).
  3. StatTile value changes update with no transition.
- **Hard exemption (A4 — safety, non-negotiable):** this surface contains **no** mask/brightness rendering, so the always-eased `Dur.overlay` mask cross-fade is unaffected here. Reduce-Motion governs only this view's *chrome* (chart cadence, chip swaps) and never the overlay's clinical smoothing.
- **Forbidden (A4):** no pulsing/breathing/color-cycling on any element; no animated count-up on the StatTiles; the legend and StatusChip never animate.

## 5. Accessibility

**AutomationProperties (currently 0 on this surface):**
- Hero card: `AutomationProperties.Name` = the full state phrase, e.g. "Comfort steady, you're protected. Eased your view 12 times this session, active 4 minutes 20 seconds." Bound from the same band + `SessionFovIntercepts` + `SessionMaskActiveText`.
- StatusChip: `AutomationProperties.Name` = "Comfort steady" / "Strain rising" / "Actively easing" / "Idle" (full phrase, not the icon).
- Each StatTile: `AutomationProperties.Name` = "{label}: {value}" (e.g. "Times eased: 12").
- Chart well: `AutomationProperties.Name` = "Comfort trend chart"; because a live polyline is not screen-reader-legible, add `AutomationProperties.HelpText` summarizing "Peak strain {SessionPeakPressure:F0}, eased {SessionFovIntercepts} times" so non-visual users get the substance from the surrounding text, not the canvas.
- Advanced `Expander`: `AutomationProperties.Name` = "Advanced numbers"; expanded/collapsed exposed natively.

**Focus order (top→bottom, left→right):** ScrollViewer → (hero is non-focusable text) → StatTiles are non-focusable display text → Advanced Expander header → (expanded) Advanced content is read-only text. Only the Expander and scroll are tab stops; order is DOM order, no `TabIndex` overrides needed.

**Contrast confirmations (Calm §1.2):**
- `TextPrimary #1A2230` on `Surface #FFFFFF` = **15.3:1** (AAA) — hero + StatTile values.
- `TextSecondary #525B6B` on `Surface` = **7.4:1** (AAA) — sub-line, StatTile labels (replaces failing `#7B8DB0` ~3.6:1).
- `TextSecondary` on `Canvas #F6F7F9` = **6.9:1** (AAA).
- StatusChip: `#1F5249` on `ComfortSteadyWeak #E2F0EC` = **7.8:1**; `#8A3A2E` on `ComfortHighWeak #F6E2DE` = **6.0:1**; rising chip text `#7A4E16` on `ComfortRisingWeak #F6ECD8` = **6.4:1** — all ≥ AA.
- Chart series: pressure `Accent #3A6EA5` on `SurfaceSunken #ECEEF2` ≈ 4.4:1, paired with a label (legend) so it is not the sole signal; motion `TextTertiary #6E7787` raised to 1.5px + distinct dash so shape carries it; clay trigger marker is dashed (shape-coded).

**Colorblind-safe / HC (D2, §1.4):** every status here is **triple-coded** — StatusChip (icon + label + hue), chart legend (line-style glyph + label + hue), Advanced sensing chip (icon + label). Under OS High-Contrast, `Comfort*` collapses to system Highlight and the icon+label carry the distinction (the triple-coding payoff). Bind every color via `{DynamicResource}` so HC swaps live on `SystemParameters.StaticPropertyChanged`.

**Min sizes:** body floor 14 (hero sub-line, Advanced labels lifted from the failing 10–12); StatTile labels at `Type.Caption` 12 floor (no 10/11px); StatTile `MinHeight 96`, chip hit-targets ≥ the pill height; chart Height fixed 200 but the card uses `MinHeight`+`Auto` elsewhere so text reflows at Windows 125%/150% and the "Make text bigger" 100–225% slider without clipping (no fixed `Height` on any text container).

## 6. Migration Notes

**Bindings/DataContext — keep all of these verbatim (no renames):**
- Tab `DataContext` stays `MainViewModel` (no `DataContext="{Binding …}"` on the tab; it inherits the window's VM). Do **not** wrap the content in a new DataContext.
- Preserve every binding path used on this tab: `OpticalFlowIndex`, `Pressure`, `VimsTier`, `FovRadiusPercent`, `MaskActive`, `ActivityStatus`, `AutoModeText`, `SessionFovIntercepts`, `SessionPeakOfi`, `SessionPeakPressure`, `SessionMaskActiveText`, `SessionUptimeText` (all read-only VM props, `MainViewModel.cs:745, 754, 760, 858–873`). Relabeling is text-only; the binding `Path` never changes.
- **`x:Name="PressureChartCanvas"` must be kept exactly** — `EnsureChartInitialized()`, `RedrawChart()`, and the `Loaded`/`SizeChanged` wiring resolve it by name (`MainWindow.xaml.cs:1236, 1257, 1259, 1266`). Moving it inside a new `Card`/`Expander` is fine **only if** it is not placed inside a virtualized or lazily-realized container (an `Expander` that is *collapsed by default* would defer realization and `EnsureChartInitialized` on `Loaded` would no-op). **Keep the chart in an always-realized card; only the Advanced numbers go in the collapsed Expander.**
- `OfiHistory` / `PressureHistory` `CollectionChanged` subscriptions (`MainWindow.xaml.cs:179–180`) and the 67ms timer must survive — do not remove or rename these VM members. The Reduce-Motion throttle is a *value* change to `_chartTimer.Interval` only.

**Chart code-behind:** the three series brushes and the trigger color are C# literals (`MainWindow.xaml.cs:1239–1252`); recolor them to the Calm tokens. Prefer `TryFindResource("Accent"/"TextTertiary"/"ComfortHigh")` so they track theme swaps; if that's infeasible at that call site, hardcode the Calm hex but mirror the token values exactly (`#3A6EA5`, `#6E7787`, `#B65C4E`). `ChartYMax = 200` and `ChartTriggerLine = 85` are behavioral constants — **do not change** (the 85 trigger is the engine threshold; altering it would misrepresent mask activation, touching D2/A2 semantics).

**Tests touching this view (verified):**
- **`tests/SenseEase.Tests/MainWindowSmokeTests.cs`** is the only test that constructs `MainWindow`. It (a) builds the window with a **bare `new Application()`** and never boots `SenseEase.App.App` — so theme tokens **must** be merged view-locally (per the design-system §7.2 locked pattern: merge `SenseEaseTheme.xaml` into each view's own `.Resources` by `pack://` URI; do **not** put tokens in `App.Resources` or the smoke test throws on every `{StaticResource}`). (b) It pumps `OnStateChanged(null, new SenseState{VimsTier=…})` three times and fails on any binding/layout exception (`MainWindowSmokeTests.cs:75–79`). **Implication:** every new binding must resolve against `MainViewModel` (no typos, no missing converters), every `{StaticResource}` must resolve from the view-local merged dictionary, and `PressureChartCanvas` must still initialize on `Loaded` — otherwise this test goes red. Run it after migration: `dotnet test --filter MainWindow_Constructs_And_FirstLayout_DoesNotThrow`.
- No test asserts on the Eye-Protection/Insights tab's *content* (grep of `tests/` for `PressureChartCanvas`, `護眼`, `SessionPeakOfi`, `ActivityStatus` against the tab returns no content assertions; `VimsTier` references are all engine/VM tests, not this view). The tab rename is therefore test-safe.

**"Cannot break prior works" (C5):** this is a pure visual refactor — relabel telemetry to plain language, recolor to Calm tokens, dedup the repeated `UniformGrid` cells into `StatTile`, triple-code the status dots, add the Advanced disclosure. **No behavior, threshold, binding, or engine value changes.** Per **D2**, band hues route only through `TierColorPalette`; per **C6**, stage on a branch and produce before/after evidence for the user's mandatory visual review — do not merge/tag on green unit tests alone.

**Evidence files read:** `src/SenseEase.App/MainWindow.xaml` (`:482–638` Eye-Protection tab + `:12–109` window resources), `src/SenseEase.App/MainWindow.xaml.cs` (`:172–189` chart wiring, `:1225–1295` chart render), `src/SenseEase.App/ViewModels/MainViewModel.cs` (`:566–579` activity/mode, `:745–889` session + history bindings), `tests/SenseEase.Tests/MainWindowSmokeTests.cs`.

---

# AI Assistant chat

I have all the evidence I need. The `x:Name="ChatScroll"` is load-bearing in code-behind (`MainWindow.xaml.cs:108-114`), so it must be preserved. I now have the complete picture of the current AI Assistant surface and can deliver the decision-grade spec.

# AI Assistant Chat — "Calm" Redesign Spec

> Surface: `MainWindow.xaml` Tab 3, `<TabItem Header="AI Assistant">` (`MainWindow.xaml:641–916`). DataContext = `ChatbotVm` (`:642`). This spec migrates the only conversational surface in the app to the locked "Calm" design system without altering a single binding name, command, or `x:Name`. Authority order applied throughout: Clinical safety (A) > Privacy (B) > Engineering locks (C) > Product semantics (D) > brand.

---

## 1. PURPOSE

**The single job:** let an overstimulated user say, in plain natural language, *"this is too much / make it calmer"* and feel heard and immediately adjusted — a conversational comfort-adjustment console for the local LLM (Ministral 3B), plus the home for the Q1–Q5 recalibration entry.

**The one question the user is asking when they open this tab:** *"Can I just tell it how I feel and have it fix the screen?"*

Everything secondary (recalibrate entry, calibration-invitation banner, profile-picker cards, status line) must defer to that one job. The chat transcript is the content; everything else is quiet chrome. This is precisely the surface where the DESIGN NORTH STAR ("the app must EMBODY the calm it sells") is tested hardest, because the user arriving here is, by definition, already strained.

---

## 2. CURRENT PROBLEMS

Concrete defects in the current tab, cited to `MainWindow.xaml` unless noted:

**2.1 Two clashing palettes inside one tab (the headline defect).** The tab body is light (`Background="#E2ECFF"`, `:642`) with white chat bubbles, but the **calibration-invitation banner is a near-black dark card** (`Background="#0E1320"` with `BorderBrush="#3D5AFE"`, `:833`) carrying light text `#F0F4FA`/`#9AB6E0` (`:842, :845`). A dark slab appearing mid-conversation on a light surface is the exact "two clashing palettes" audit fact, and it is alarming, not calm.

**2.2 Saturated alarm-grade accents.** Royal blue `#3D5AFE` on the Start button (`:858`) and banner border (`:833`); `#024AD8` on the assistant author label (`:706`) and inherited by every `PrimaryBtn`/`GhostBtn` (`:54, :70`). These are the retired loud blues — must become `Accent #3A6EA5`.

**2.3 Sub-floor type, repeatedly.** Timestamps at `FontSize="10"` (`:694, :815`); assistant author label at `FontSize="10"` (`:706`); status bar at `FontSize="11"` (`:883`); banner subtitle (`:845`), profile-card description (`:804`), and all buttons at `FontSize="11"`/`12` (`:664, :774, :860`). The 14px body floor is violated everywhere; message body text itself is only `13` (`:691, :750, :901`). Spec hard rule: "No 10px or 11px anywhere user-facing."

**2.4 Contrast failures on essential text.** `#7B8DB0` carries the **status bar** (`:883`), every timestamp (`:694, :815`), and the typing dots (`:738–742`) — ~3.6:1, fails AA. `#9AB6E0` carries the banner subtitle and Skip text (`:845, :852`) — fails AA. The status bar is functionally important ("model loading / load failed", `ChatbotViewModel.cs:236, 294`) and is currently the lowest-contrast text on the surface.

**2.5 Color-only / iconless status.** The status bar is a bare text strip with no glyph and no semantic color (`:882–883`). Model-available vs load-failed vs loading is conveyed by text content alone with no triple-coding. Typing state is three bare `Ellipse` dots (`:737–742`) — color-and-motion only, no text alternative.

**2.6 Motion with no Reduce-Motion path.** The typing indicator is a `RepeatBehavior="Forever"` storyboard pulsing three dots (`:717–736`) — exactly the "attention-grabbing, never-resting" motion the Calm spec rations. There are **zero** `ReduceMotion` checks; this animation cannot be switched off and runs forever while the model thinks.

**2.7 Ad-hoc, duplicated styling — no token discipline.** Six raw blue `DropShadowEffect Color="#5B82C6"` instances on this tab alone (chat history `:672`, status bar `:879`, input well `:896`) — the retired blue shadow. Radii are a soup: `14` (`:670`), `12`/`2` (`:687, :702`), `10` (`:832, :893`), `8` (`:877`), `6` (`RoundBtn`, `:37`). Padding is bespoke per element (`12,8`; `20,14`; `14,7`; `10,8`; `14,6`). The profile-picker button is a fully hand-rolled inline card (`:788–809`) with its own hex (`#FFFFFF`/`#B7CFE3`/`#1E5BB0`/`#3A4252`), duplicating what `Card`+`Option.Cell` should provide.

**2.8 Engineering-flavored author string.** Assistant author renders `"SenseEase AI"` (`ChatbotViewModel.cs:935`) in loud blue 10px — acceptable copy, but visually it reads as a system tag, not a calm companion.

**2.9 Placeholder/empty state is undefined.** There is no empty-state design; the transcript opens with a single welcome bubble (`ChatbotViewModel.cs:260`) and no affordance hinting *what* the user can say. A strained user staring at an empty box with a blinking cursor is a missed calm opportunity.

**2.10 No keyboard/AT support.** `AutomationProperties = 0` on this surface (audit fact, verified — no occurrences in the tab). The input `TextBox` (`:899`), Send/Clear (`:909, :912`), Recalibrate (`:660`), and every option/profile card have no `AutomationProperties.Name`; the transcript `ItemsControl` (`:676`) is not announced as a log.

---

## 3. REDESIGN

### 3.1 Layout structure (top → bottom)

The `Grid` keeps its **six existing `RowDefinition`s in the exact same order** (`:643–650`) so all `Grid.Row` assignments and visibility bindings are untouched — only the contents are re-skinned. Tab body background moves `#E2ECFF` → **`Canvas #F6F7F9`** (one calm console palette, killing 2.1's clash at its root).

| Row | Existing purpose (`:644–649`) | Redesigned element | Component / tokens |
|---|---|---|---|
| 0 | Recalibrate entry | Quiet recalibrate link, right-aligned, with `Icon.Settings` (16px) | `Btn.Quiet`, `Type.Caption`, `Pad.Button`, `TextSecondary` |
| 1 | Chat history (the content) | Transcript card — the one elevated, focal surface | `Card` (`Surface`, `Radius.lg 16`, `Elev.2`); bubbles inside |
| 2 | Calibration-invitation banner | **Re-skinned to a LIGHT card** (kills the dark slab) | `Card` `Surface` + `AccentWeak` accent strip, `Icon.Lightbulb` |
| 3 | Calibration card host | unchanged host; inner `CalibrationCardView` re-skinned separately | (host Border only; no style change here) |
| 4 | Status bar | `Status.Chip`-style strip (triple-coded), not bare text | `SurfaceMuted` strip + icon + `Type.Caption` `TextSecondary` |
| 5 | Input row | `Field.Text` well + `Btn.Primary` Send + `Btn.Quiet` Clear | `Field.Text`, `Btn.Primary`, `Btn.Quiet` |

### 3.2 Element-by-element

**Row 0 — Recalibrate (`:655–666`).**
Keep right-aligned. Replace `Style="{StaticResource GhostBtn}"` + `FontSize="12"` (`:663–664`) with **`Btn.Quiet`** (transparent, `TextSecondary`, hover `SurfaceMuted`). Add leading `Icon.Settings` (16px, inherits `TextSecondary`). Copy stays `重新校準 / Recalibrate` (`:661`) — bilingual is a product constraint, not changed here. Padding → `Pad.Button` (14,8). Caption-size label is acceptable on a low-emphasis link (`Type.Caption` 12 floor, not 10/11).

**Row 1 — Transcript card (`:669–824`).**
- Container Border: `Background White → Surface`, `CornerRadius="14" → Radius.lg (16)`, replace the blue `DropShadowEffect #5B82C6` (`:672–673`) with **`Elev.2`** (neutral `#000000`, y2/blur10/0.08). Keep `x:Name="ChatScroll"` on the inner `ScrollViewer` (`:675`) **verbatim** — it is load-bearing in `MainWindow.xaml.cs:108–114` (auto-scroll-to-end). `Padding="16,12"` → `Pad.Card`-derived (`16,12` is fine; keep as `Space.4,Space.3`).
- **User bubble (`:685–697`):** `Background="#EEF3FF" → AccentWeak (#E9EFF5)`, text `#0A1F5C → TextPrimary (#1A2230)`, raise `FontSize="13" → Type.Body (14)`. `CornerRadius="12,12,2,12"` → `Radius.md (12)` corners with the one sharpened tail corner = 2 (keep the asymmetric tail; it is a chat affordance, not a brand radius). Timestamp `#7B8DB0` 10px → **`TextTertiary (#6E7787)` at `Type.Caption (12)`** (timestamps are de-emphasis-only, so Tertiary is permitted; 12 is the floor).
- **Assistant bubble (`:698–818`):** `Background="#F8FAFF" → SurfaceMuted (#F1F3F6)` (a distinct-but-calm fill, clearly "not you"), text `#0A1F5C → TextPrimary`, `FontSize="13" → Type.Body (14)` on the `MarkdownInlines` block (`:748–751`). Author label `#024AD8` 10px (`:705–707`) → **`Accent` at `Type.CaptionStrong (12)`** with a leading 16px `Icon.Checkmark Circle`/companion glyph; this is the "eyebrow" label.
- **Typing indicator (`:709–743`):** see §4 — replaced by a Reduce-Motion-aware indicator that degrades to a static glyph + "Thinking…" text (`Type.Caption`, `TextSecondary`), so it is no longer color/motion-only (fixes 2.5, 2.6).
- **Followup option cards (`:757–777`):** keep the `ItemsControl`/`WrapPanel` and the `SelectFollowupCommand` relative-source binding **exactly** (`:768–770`). Restyle each `Button` `GhostBtn` → **`Btn.Ghost`** (`AccentWeak` bg, `Accent` text, 1px `AccentWeakBorder`, `Radius.sm 8`), `FontSize="12" → Type.BodyStrong (14)`, `Pad.Button`.
- **Profile picker cards (`:783–812`):** keep `SelectProfileCommand` binding and `ProfileOptions` source verbatim (`:788–790`). Replace the hand-rolled hex card (`#FFFFFF`/`#B7CFE3`/`#1E5BB0`/`#3A4252`, `:791–805`) with an **`Option.Cell`-styled selectable row**: `Surface` bg, 1px `Separator`, `Radius.md`, hover `SurfaceMuted`, focus ring. Name `#1E5BB0` 13 → `Type.Heading (16)` `TextPrimary`; Description `#3A4252` 11 → `Type.Caption (12)` `TextSecondary`. This is the only profile-selection surface, so it must be legible and AT-named.

**Row 2 — Calibration-invitation banner (`:831–863`) — the biggest single fix.**
Keep `Grid.Row="2"`, the `ShowCalibrationInvitationBanner` visibility binding, and both commands (`SkipCalibrationInvitationCommand`, `StartInvitedCalibrationCommand`) **verbatim** (`:834, :851, :857`). Re-skin from dark slab to **light `Card`**:
- `Background="#0E1320" → Surface`; `BorderBrush="#3D5AFE" → AccentWeak`/none; `CornerRadius="10" → Radius.md (12)`; add `Elev.2`. Leading **`Icon.Lightbulb`** (replaces the implied "tip" framing) at `Accent`.
- Title `#F0F4FA` 13 → `TextPrimary` `Type.Heading (16)`; subtitle `#9AB6E0` 11 → **`TextSecondary (#525B6B)` `Type.Body (14)`** (was a contrast failure).
- Skip button: transparent `#9AB6E0` (`:852`) → **`Btn.Quiet`**. Start button: `#3D5AFE` filled (`:858`) → **`Btn.Primary`** (`Accent`). Both copy strings unchanged (`:850, :856`).
- This is a new informational surface drawn on the captured monitor only insofar as the main window is — it sits inside the existing window, so **no new `SetMonitorExcludeRect` registration is required** (C2 satisfied: it is not a new top-level on-monitor window).

**Row 3 — Calibration card host (`:870–873`).** No style change here; `CalibrationCardView` is a separate surface with its own spec. Host Border keeps its `IsCalibrationCardActive` binding (`:871`) and `ActiveCalibrationCard` DataContext flow (`:872`) verbatim. Margin → `Gap.Stack`.

**Row 4 — Status strip (`:876–884`).**
Replace the bare white card + `#7B8DB0` 11px text with a **triple-coded status strip** so model availability survives colorblindness/HC:
- Container: `SurfaceMuted` fill, `Radius.sm (8)`, `Pad` 14,7 → `Space.4,Space.2`, **no shadow** (a status strip should recede, not float — drop the `#5B82C6` shadow at `:879–880`).
- Leading 16px glyph + `{Binding StatusText}` at `Type.Caption (12)` `TextSecondary (#525B6B)` (raised from the failing `#7B8DB0`). The glyph is chosen by status semantics: ready = `Icon.Checkmark Circle` (`ComfortSteady`), loading = `Icon.Settings`/spinner-glyph (neutral), failed = `Icon.Warning` (`ComfortHigh #B65C4E`, **never** alarm red `#E54747`). Because `StatusText` is a free string from the VM (`ChatbotViewModel.cs:236, 284, 294`), the glyph/color can key off a substring or, preferably, a tiny VM-exposed status enum **added non-breakingly** (see §6 — optional; the text-only fallback already satisfies the floor).

**Row 5 — Input row (`:887–914`).**
Keep all three columns and bindings (`InputText`, `SendCommand`, `ClearCommand`, the `KeyBinding Key="Return"`) **verbatim** (`:899, :904, :910, :913`).
- Input well: white `Border`+`#5B82C6` shadow (`:893–898`) → **`Field.Text`** styling (`SurfaceSunken` bg, 1px `Separator`, `Radius.sm 8`, focus → `Accent` 1.5px + `FocusRing` glow). Text `#0A1F5C` 13 → `TextPrimary` `Type.Body (14)`. Add a real **placeholder adorner** ("Tell me how the screen feels…") via the `Field.Text` empty-adorner pattern — fixes the undefined empty state (2.9) without a VisualBrush hack.
- Send: `PrimaryBtn → Btn.Primary` (`Accent`, `TextOnAccent`, `Radius.sm`, `Elev.1`; disabled when `IsGenerating || empty`, already enforced by `SendCommand.CanExecute`, `ChatbotViewModel.cs:228`).
- Clear: `GhostBtn → Btn.Quiet` (Clear is a low-emphasis destructive-ish dismissal of the transcript; Quiet is correct, not Ghost).

### 3.3 Hierarchy, spacing, elevation, radius

- **One focal surface:** only Row 1 (transcript) carries `Elev.2`; the invitation banner also `Elev.2` *only while present*; status strip and input well are flat/sunken. Depth comes from elevation + space, never saturation (Deference).
- **Spacing:** outer tab gutter `Space.6 (24)` (matches existing `Margin="24,…"`); inter-row gaps `Gap.Stack = Space.4 (16)` (replaces the current `8` jumble at `:669, :831, :876`). Card padding `Pad.Card (24)` for banner/profile cards; bubbles use `Pad` 12,8 (`Space.3,Space.2`).
- **Radius:** windows/cards `Radius.lg (16)`, sub-cards `Radius.md (12)`, buttons/fields/cells `Radius.sm (8)`, badges `Radius.pill`. Retires `14/10/6` (`:670, :832, :37`).
- **Elevation:** all shadows → neutral `#000000` `Elev.*`; the six `#5B82C6` blue shadows on this tab (`:672, :879, :896`) are deleted.

### 3.4 States (rest / hover / focus / empty / loading / error)

| State | Behavior |
|---|---|
| **Rest** | Transcript card `Elev.2` on `Canvas`; input well `SurfaceSunken` with placeholder; Send disabled (empty input). |
| **Hover** | Send → `AccentHover (#33628F)`; Clear/Recalibrate → bg `SurfaceMuted`; followup/profile cells → 1px `Accent` border / `SurfaceMuted` bg. |
| **Focus** | Every interactive element gets the 2px `FocusRing (#3A6EA5)` + 1px white inset. Input well border → `Accent` 1.5px. Keyboard tab order defined in §5. |
| **Empty** | First-open: welcome bubble (`ChatbotViewModel.cs:260`) + input placeholder "Tell me how the screen feels…". No engineering telemetry, no empty grey void. |
| **Loading** | Model-load in progress: status strip shows neutral glyph + `StatusText` ("Loading model…"); Send stays disabled via `CanExecute` (`:228`). In-flight reply: assistant placeholder bubble shows the Reduce-Motion-aware "Thinking…" indicator (§4). |
| **Error** | Load failure (`ChatbotViewModel.cs:294`) or `[Error]` reply (`:526`): status strip glyph = `Icon.Warning` in `ComfortHigh (#B65C4E)` clay + `StatusText`; the `[Error]` assistant bubble renders as normal `SurfaceMuted` body text (transparent failure, not hidden — satisfies A5's "transparent block reason" spirit). **Never** alarm red. |

### 3.5 ASCII wireframe (redesigned)

```
┌──────────────────────────────────────────────────────────────────────┐  Canvas #F6F7F9
│                                          [⚙ 重新校準 / Recalibrate]    │  Row0  Btn.Quiet, Type.Caption, TextSecondary
│                                                                        │
│  ┌────────────────────────────────────────────────────────────────┐  │  Row1  Card · Surface · Radius.lg · Elev.2
│  │  ✓ SenseEase                              (assistant eyebrow)    │  │        author = Accent, Type.CaptionStrong + icon
│  │  ┌──────────────────────────────────────────────────┐          │  │
│  │  │  I've softened the edges a little. Tell me if      │          │  │  assistant bubble  SurfaceMuted, Type.Body(14), TextPrimary
│  │  │  that feels easier on your eyes.                   │          │  │
│  │  └──────────────────────────────────────────────────┘ 14:32     │  │        timestamp Type.Caption, TextTertiary
│  │                                                                  │  │
│  │          ┌───────────────────────────────────────────────────┐  │  │  user bubble (right)  AccentWeak, Type.Body, TextPrimary
│  │          │  the motion is making me dizzy                     │  │  │
│  │          └───────────────────────────────────────────────────┘  │  │
│  │                                                          14:32   │  │
│  │  ✓ SenseEase                                                     │  │
│  │  ●  Thinking…   (static glyph + text when ReduceMotion)          │  │  typing → triple-coded, Type.Caption
│  │  [ Make it darker ] [ Keep it as is ]   (followup → Btn.Ghost)   │  │  Btn.Ghost, Radius.sm, AccentWeak
│  └────────────────────────────────────────────────────────────────┘  │
│                                                                        │
│  ┌────────────────────────────────────────────────────────────────┐  │  Row2  Card (LIGHT now, was #0E1320) · Elev.2
│  │  💡  Want to refresh your settings?            [Skip] [ Start ]  │  │        title Type.Heading TextPrimary; sub Type.Body TextSecondary
│  │      Chat about what you just felt.        Btn.Quiet  Btn.Primary│  │        (only visible when ShowCalibrationInvitationBanner)
│  └────────────────────────────────────────────────────────────────┘  │
│                                                                        │
│  ┌────────────────────────────────────────────────────────────────┐  │  Row4  SurfaceMuted strip (no shadow), triple-coded
│  │  ✓  Ready — model loaded locally                                │  │        Icon + StatusText, Type.Caption, TextSecondary
│  └────────────────────────────────────────────────────────────────┘  │
│                                                                        │
│  ┌──────────────────────────────────────────────┐ ┌──────┐ ┌───────┐ │  Row5  Field.Text · SurfaceSunken · Radius.sm
│  │  Tell me how the screen feels…                │ │ Send │ │ Clear │ │        Send=Btn.Primary  Clear=Btn.Quiet
│  └──────────────────────────────────────────────┘ └──────┘ └───────┘ │
└──────────────────────────────────────────────────────────────────────┘
        (Row3 calibration-card host is between Row2 and Row4, shown only
         when IsCalibrationCardActive — hosts the separately-spec'd card)
```

---

## 4. MOTION & REDUCE-MOTION

This surface has exactly one piece of decorative motion (the forever-pulsing typing dots, `:717–736`) plus implicit scroll/fade — all governed by the Calm motion contract.

- **Typing indicator (the only forever-loop):** Replace the three-dot `RepeatBehavior="Forever"` storyboard (`:717–736`) with an indicator bound to the app-level `ReduceMotion` `{DynamicResource}` (Calm §3.4). When `ReduceMotion = false`: a single, gentle, `Dur.slow (280ms)` calm-out opacity breathe on **one** glyph (no left-to-right chase, no `Forever` attention-pull beyond a slow ≤0.6 opacity floor) accompanied by **"Thinking…"** text. When `ReduceMotion = true`: the indicator is a **static** `●` glyph + "Thinking…" text — no animation at all (Calm §3.4 rule 2: typing dots become a static `…`). Either way it is now **triple-coded** (glyph + text), fixing the motion-/color-only defect (2.5/2.6).
- **Transitions:** assistant bubble appearance and the invitation banner show/hide use `Dur.base (200ms)` `Ease.calmOut` cross-fade when `ReduceMotion = false`, and **instant swap** (duration → 0) when true (Calm §3.4 rule 2). Bind storyboard durations to the `Dur.ui` dynamic resource so the master switch re-skins motion with no per-view code.
- **Auto-scroll:** `ChatScroll.ScrollToEnd()` (`MainWindow.xaml.cs:109`) stays — it is a jump-to-position, not decorative motion, and is acceptable under Reduce-Motion (a strained user wants the newest message in view immediately, not a smooth-scroll animation).
- **The streaming "fake type" reply** (`FakeStreamAsync`, `ChatbotViewModel.cs:512, 541–571`) appends text slice-by-slice. This is content delivery, not chrome motion; under `ReduceMotion = true` it should collapse to a single full-text set (skip the per-slice cadence) to avoid character-by-character motion — a VM-side `if (ReduceMotion)` early-set, flagged in §6 as the one optional VM touch.
- **Hard rule:** no pulsing/breathing/color-cycling on any element here (Guardrail A4). Nothing on this surface drives the clinical mask, so there is no safety-smoothing exemption to preserve — all motion here is chrome and fully Reduce-Motion-governed.

---

## 5. ACCESSIBILITY

**AutomationProperties (all currently absent — fixes 2.10):**
- Tab root: `AutomationProperties.Name="AI assistant chat"`.
- Transcript `ItemsControl` (`:676`): `AutomationProperties.Name="Conversation transcript"`; each bubble exposes `AutomationProperties.Name` = `"{Author}: {Content}"` (Author already = "You" / "SenseEase AI", `ChatbotViewModel.cs:935`) so screen readers announce speaker + text.
- Input `TextBox` (`:899`): `AutomationProperties.Name="Message to assistant"` + the placeholder as `HelpText`.
- Send (`:909`): `Name="Send message"`; Clear (`:912`): `Name="Clear conversation"`; Recalibrate (`:660`): `Name="Recalibrate — open calibration"`.
- Status strip (`:882`): `AutomationProperties.LiveSetting="Polite"` so load/error status is announced when it changes (`StatusText` updates at `ChatbotViewModel.cs:236/284/294`).
- Typing indicator: `AutomationProperties.Name="Assistant is thinking"` (the static-text path doubles as the AT label).
- Each followup card (`:767`) and profile card (`:788`): `AutomationProperties.Name` = the option/profile name + description.
- Invitation banner buttons: `Name` = full bilingual label already on `Content`.

**Focus order (tab sequence):** Recalibrate (Row0) → transcript scroll region (Row1, arrow-key scrollable; followup/profile cards inside are focusable in document order) → invitation banner Skip → Start (Row2, only when visible) → input `TextBox` (Row5) → Send → Clear. The input field receives initial focus on tab activation so a user can type immediately. `Enter` sends (existing `KeyBinding`, `:904`); `Shift+Enter` reserved if multiline is ever added (currently `AcceptsReturn="False"`, `:902` — unchanged). Every focusable element shows the 2px `FocusRing` + inset.

**Contrast confirmations (per Calm §1.2 locked table):**
- Body text `TextPrimary #1A2230` on `Surface`/`SurfaceMuted`/`AccentWeak` bubbles: **15.3 / 13.6 / ≥13:1** → AAA.
- Status + helper `TextSecondary #525B6B` on `SurfaceMuted`: **6.6:1** → AA (AAA large). Replaces the failing `#7B8DB0` ~3.6:1.
- Timestamp `TextTertiary #6E7787` on bubble fills: **4.6–4.9:1** → AA (de-emphasis-only role, permitted).
- Assistant eyebrow `Accent #3A6EA5` on `SurfaceMuted`: ≈**4.9:1** → AA.
- Error glyph `ComfortHigh #B65C4E` paired with `Icon.Warning` + text — never color-alone.

**Colorblind-safe status:** model status and typing state are **triple-coded** (icon + text + color); removing color leaves a complete message ("Ready", "Loading model…", "Load failed", "Thinking…"). High-Contrast theme: status color delegates to `SystemColors` per Calm §1.4; the icon+label carries meaning so hue loss is non-fatal.

**Minimum sizes:** body ≥14px, caption ≥12px (no 10/11 survives). Interactive targets ≥ `MinHeight 32` for buttons, profile/followup cells `MinHeight 48` (`Option.Cell`), input well `MinHeight` (no fixed `Height` on text — reflows at 125/150% OS scaling and the "Make text bigger" slider per Calm §2). Cards grow, never clip.

---

## 6. MIGRATION NOTES

**This is a "cannot break prior works" change (Guardrail C5/C6). Every binding name, command, `x:Name`, `Grid.Row`, and DataContext is preserved; only visual chrome changes.**

**Token delivery (the smoke-test trap, Calm §7.2):** The smoke test constructs `new SenseEase.App.MainWindow()` directly (`MainWindowSmokeTests.cs:41`) and **never boots `App`** — so tokens must NOT go in `App.Resources`. Merge `SenseEaseTheme.xaml` into **`MainWindow`'s own `<Window.Resources>`** (which already hosts `BoolToVis` view-locally for exactly this reason, `:12–19`) via the pack URI:
```xml
<ResourceDictionary.MergedDictionaries>
  <ResourceDictionary Source="pack://application:,,,/SenseEase.App;component/Theme/SenseEaseTheme.xaml"/>
</ResourceDictionary.MergedDictionaries>
```
WPF caches by source URI, so this is zero-cost and resolves under the smoke test. Keep `BoolToVis` (`:19`) in the same dictionary — `ShowCalibrationInvitationBanner` (`:834`) and `IsCalibrationCardActive` (`:871`) depend on it.

**Preserve verbatim (do NOT rename — these are load-bearing):**
- `x:Name="ChatScroll"` (`:675`) — referenced in `MainWindow.xaml.cs:108, 110, 111, 113` for auto-scroll. Renaming breaks scroll-to-end.
- `x:Name="Dot1/Dot2/Dot3"` (`:737–741`) — only if the typing storyboard is kept; since §4 replaces the indicator, these names can be retired **together with** the storyboard (no code-behind reference — grep-confirmed they live only in the XAML storyboard `:718–729`). Safe to remove as a unit.
- All bindings: `Messages`, `InputText`, `StatusText`, `SendCommand`, `ClearCommand`, `RecalibrateCommand`, `ShowCalibrationInvitationBanner`, `SkipCalibrationInvitationCommand`, `StartInvitedCalibrationCommand`, `IsCalibrationCardActive`, `ActiveCalibrationCard`, `SelectFollowupCommand`, `SelectProfileCommand`, `FollowupOptions`, `ProfileOptions`, and per-message `UserVisibility`/`AssistantVisibility`/`TypingVisibility`/`ContentVisibility`/`FollowupVisibility`/`ProfileOptionsVisibility`/`Author`/`Content`/`Timestamp` (`ChatbotViewModel.cs:935–987`). All retained; the redesign only changes the `Style`/`Foreground`/`FontSize`/`Background`/`CornerRadius` on the elements that carry them.
- The `RelativeSource AncestorType=ItemsControl, AncestorLevel=1` bindings on followup (`:768`) and profile (`:789`) commands — these are nesting-depth-sensitive. If a wrapper element is added around the `Button`, **re-verify `AncestorLevel`** or the command silently no-ops.
- `MarkdownInlines.Source="{Binding Content}"` attached property (`:749`, `views:`) — keep; only its `FontSize`/`Foreground` change.

**Tests that touch this view:**
- `tests/SenseEase.Tests/MainWindowSmokeTests.cs` (`:41`) is the **only** test that constructs this window. It asserts construction + first layout + three `OnStateChanged` fires throw nothing (`:75–79`). The redesign must keep every `{StaticResource …}`/`{DynamicResource …}` lookup resolvable at construction — which the view-local merge guarantees. **Run this test after migration; it is the CI gate for this surface.** Do not modify it (Calm §7.2: editing the smoke harness to boot `App` is explicitly forbidden).
- No test pins the AI-Assistant tab's hex literals, copy, or layout (grep-confirmed: bindings only). The "no orphan hex" lint (Calm §7.3) will count this tab's ~30 literals in its shrinking allowlist baseline; migrating them to tokens ratchets the count down.

**Optional non-breaking VM touches (flag in PR, not required for the visual floor):**
1. Add a small `ChatStatusKind { Ready, Loading, Error }` enum + `StatusKind` property on `ChatbotViewModel` (set alongside the existing `StatusText` writes at `:236/284/294`) so the status strip's icon/color binds semantically instead of substring-sniffing `StatusText`. Additive; no existing binding changes.
2. Wrap the per-slice cadence in `FakeStreamAsync` (`ChatbotViewModel.cs:541–571`) in `if (ReduceMotion) { bubble.Content = full; return; }` so character streaming respects Reduce-Motion. Additive guard; default behavior unchanged when motion is on.

**Behavior unchanged (D-invariants intact):** copy strings (bilingual `中文 / English`), the 6-row structure, command wiring, auto-scroll, model-status text, and the calibration/profile flows all behave identically. No tier mapping, mute semantics, proposal lifecycle, or safety path is touched — the **crisis safety-floor (A1)** does not render on this tab today and is **not** added here; this redesign restyles the comfort-adjustment chat only and must not route any text through, or add CTAs to, the crisis surface.

**Files touched:** `src/SenseEase.App/MainWindow.xaml` (`:641–916`, visual only) + new `src/SenseEase.App/Theme/*` token dictionaries (shared, not unique to this surface). No code-behind change required unless the optional VM touches are taken. Stage on a branch; produce before/after screenshots for the user's mandatory Layer-3 visual review before any merge (C6 — do not self-approve on green unit tests).

---

# Calibration guided sheet (Q1-Q5, collapse the 5x duplicated scale into one SegmentedScale)

I have all the ground-truth evidence I need. The view is hosted inside `MainWindow.xaml:872` as `<views:CalibrationCardView DataContext="{Binding ActiveCalibrationCard}"/>`, which means it IS constructed during the MainWindow smoke test — confirming the view-local merge pattern is mandatory (tokens in App.Resources would throw). I now have the complete picture: bindings, Click handlers, enum Tag bindings, mutual-exclusivity VM logic, and the test surface.

Here is the decision-grade spec.

---

# Calibration Guided Sheet (Q1–Q5) — "Calm" Visual Redesign Spec

## 1. Purpose

The calibration sheet is the **one-time personalization interview** shown inside the AI Assistant tab immediately after the 60s demo video, surfaced when `ChatbotViewModel.IsCalibrationCardActive == true` and hosted at `MainWindow.xaml:872`. It packs five answers (`CalibrationResponses`) for `CalibrationProcessor.Process`, which sets the user's `personal_pressure_scale`, trigger override, symptom dominance, and medical advisory.

**The single job:** ask the user — gently, one question at a time — *"How did that video make your body feel?"* so the app can tune its protection to them.

**The one question the user is asking when they see it:** *"This is asking about how I just felt — is it going to be quick, is it safe to be honest, and can I stop if I need to?"* The surface must answer **yes to all three** by being slow, plain, low-stakes, skippable, and honest that only Q3 is required. Target users are, by definition, **possibly nauseated right now** (they just watched a provocative demo) — so this is the highest-stakes "calm" surface in the app: it must never strobe, crowd, or alarm.

## 2. Current Problems (cited)

**Two-palette clash / dark-on-light is not present here, but the wrong palette is.** This card is a *light* card (`Background="White"`, `CalibrationCardView.xaml:118`) using the **old saturated console palette** — royal blue `#024AD8` and navy `#0A1F5C` — while the rest of the Alpha rebrand moves to calm slate. The header comment at `:16-19` explicitly states "No new colours are introduced," locking it to the legacy palette by design. It needs re-skinning to the "Calm" tokens.

**Contrast failures on essential text (WCAG 2.2 AA floor breach):**
- `#7B8DB0` is used for **every** Q3 scale sub-label (`:231, 258, 285, 312, 338`), the subtitle style (`CalibSubtitle :94`), the freetext label style (`:101`), and the header helper text (`:142`). On white this is ~3.6:1 — **fails AA** for the sub-labels which are the *only* text distinguishing "None" from "Severe" at the bottom of each scale cell. These carry essential meaning and fail.
- The Cancel button text is `#7B8DB0` on transparent (`CalibAlertBtn :65`) — low-contrast for an actionable control.

**Typography below the floor (43× violation class):**
- Q3 scale sub-labels are `FontSize="10"` (`:231, 258, 285, 312, 338`) — below the **12px Caption floor**. Five instances.
- `CalibSubtitle` (`:93`), `CalibFreetextLabel` (`:100`), header badge (`:139`), header helper (`:142`), step counter (`:146`) are all `FontSize="11"` — below floor.
- `CalibTitle` is `FontSize="14"` (`:86`) — at body size but used as a *question title*; should be `Type.Heading` (16). Body/option text is `12` (`:73, 80`) — below the 14 body floor.

**The load-bearing duplication — five hand-copied 40-line ControlTemplates.** The Q3 1–5 scale at `:220–355` repeats a near-identical `RadioButton.Template` **five times** (`:223–246, 250–273, 277–300, 304–326, 330–352`), differing only in the number glyph and sub-label string. Each carries its own `#F8FAFF` cell bg, `#E8EEFF` border, `CornerRadius="6"`, `MinHeight="58"`, and a four-line trigger block hardcoding `#024AD8` selected fill. ~200 lines that should be one data-driven `ItemsControl`.

**Per-window duplicated styles.** `:25–28` admits the button styles are "local copies of the chatbot tab's button styles … redeclared with identical values" — `CalibPrimaryBtn`, `CalibGhostBtn`, `CalibAlertBtn`, `CalibRoundBtn` (`:29–66`) all duplicate MainWindow chrome. The whole `UserControl.Resources` block is a private mini design-system that drifts from the real one.

**Density / radius / spacing soup.** Card padding `18,14` (`:118`); badge padding `8,3` (`:137`); button padding `18,8` (`:52`); freetext padding `8,6` (`:106`); cell padding `6,10` (`:226`); margins of `6/8/10/12/14` scattered (`:72, 96, 102, 131, 430`). Radii span `6/10/12` (`:31, 137, 118`). None align to the 4/8pt grid or the 8/12/16 radius scale.

**Color-only selection state.** A selected Q3 cell is conveyed **only** by fill flipping to `#024AD8` (`:237`) — no checkmark, no shape change, no border-weight delta beyond the hover border. For a colorblind user, "which intensity did I pick" is hue-only.

**Wrong-direction shadow + alarm-adjacent affordances.** Card shadow is the blue `#5B82C6` cast (`:120`) the spec retires for a neutral black `Elev.*`. Cancel styled via `CalibAlertBtn` (the "alert" naming) for a benign back-out.

**No accessibility plumbing.** Zero `AutomationProperties` on any element. The Q3 scale is five separate templated `RadioButton`s with no group label, no "Step 3 of 5" announcement, no per-cell accessible name. `ProgressDots` does not exist — progress is a bare text counter (`CardCounterText`, `:145`).

## 3. Redesign

### 3.1 Hosting & token plumbing (mandatory)

This view **is constructed inside the MainWindow smoke test** (it is a child of `MainWindow.xaml:872`, instantiated at first layout). Therefore tokens **must not** live in `App.Resources` — merge the theme bundle **view-locally** at the top of `CalibrationCardView`:

```xml
<UserControl.Resources>
  <ResourceDictionary>
    <ResourceDictionary.MergedDictionaries>
      <ResourceDictionary Source="pack://application:,,,/SenseEase.App;component/Theme/SenseEaseTheme.xaml"/>
    </ResourceDictionary.MergedDictionaries>
    <BooleanToVisibilityConverter x:Key="BoolToVis"/>
    <!-- view-local converters only; ALL color/type/space come from tokens -->
  </ResourceDictionary>
</UserControl.Resources>
```

Delete the entire `CalibRoundBtn / CalibPrimaryBtn / CalibGhostBtn / CalibAlertBtn / OptionRadioBtn / OptionCheckBox / CalibTitle / CalibSubtitle / CalibFreetextLabel / CalibFreetextBox` block (`:25–115`) and the inline shadow (`:119–121`). Consume the shared `Btn.Primary / Btn.Ghost / Btn.Quiet`, `Type.*`, `Card`, `Elev.*`, `Field.Text`, `Option.Cell`, `Scale.Segmented`, `Progress.Dots` from `Controls.xaml`.

### 3.2 Layout structure (top → bottom)

| Region | Component / tokens | Notes |
|---|---|---|
| **Container** | `Card` style — `Surface` bg, `Radius.md` (12), `Pad.Card` (24), `Elev.2` (neutral black, replaces blue `#5B82C6`) | Replaces `Border :118`. |
| **Header row** | left: `Badge.Accent` ("校正 / Calibration", `AccentWeak` bg + `Accent` text, `Type.CaptionStrong`, `Radius.pill`); right: **`Progress.Dots`** (5 dots) | Replaces the badge+counter at `:131–147`. The text counter `CardCounterText` becomes the dots' accessible name; the long helper sentence (`:141`) moves **under** the question as a subtitle, not in the header. |
| **Eyebrow / reassurance line** | `Type.Caption` `TextSecondary` (`#525B6B`, **7.4:1 AAA**) — "依您剛看完 demo 的感受作答 / Answer based on how the demo felt" | Was the low-contrast `#7B8DB0` header text. One calm line, full width. |
| **Question title** | `Type.Heading` (16/22 SemiBold) `TextPrimary` (`#1A2230`, **15.3:1**) | Replaces `CalibTitle :85`. One question per screen — Clarity. |
| **Question subtitle** | `Type.Body` (14/20) `TextSecondary` | Raised from 11px `#7B8DB0` (`CalibSubtitle`) to 14px AA-passing helper. |
| **Answer region** (one of five) | see §3.3 per-card | `Gap.Stack` (16) above the freetext. |
| **Optional notes** | label = `Type.Caption` `TextSecondary`; field = **`Field.Text`** (`SurfaceSunken` bg, 1px `Separator`, `Radius.sm`, `Pad` 12,8, `Type.Body`), real placeholder adorner "補充 (選填) / Notes (optional)" | Replaces `CalibFreetextBox` + separate label; placeholder retires the need for a standalone label line. `MinHeight 44`. |
| **Footer** | left: **`Btn.Quiet`** "取消 / Cancel"; right cluster: **`Btn.Ghost`** "上一題 / Back" + **`Btn.Primary`** "下一題 / Next" (or "完成 / Complete" on last card) | Cancel demoted from `CalibAlertBtn` to `Btn.Quiet` (it is a benign back-out, not an alert). `Pad.Button` (14,8). Next/Complete keep the existing `IsLastCard` `DataTrigger` swap (`:443–473`). Next is **disabled** (`Btn.Primary` disabled state: `SurfaceMuted` bg, `TextTertiary`, no shadow) whenever `CanAdvance == false` — i.e., Q3 unanswered. |

### 3.3 Per-card answer components

- **Q1 (single-select segment), Q4 (single-select comparison):** replace `OptionRadioBtn` rows with **`Option.Cell`** styled as a single-select group (radio semantics) — `Surface`/`SurfaceMuted` row, leading radio glyph, `Type.Body` label, `AutomationProperties.Name` = the bilingual option text. **Keep `GroupName`, `Tag={x:Static inf:…}`, and `Click=OnQ1OptionClicked/OnQ4OptionClicked`** exactly — the code-behind maps `Tag` to the typed enum.
- **Q2, Q5 (multi-select):** **`Option.Cell`** in checkbox mode — `AccentWeak` bg + `Accent` check when checked. **Keep every `IsChecked="{Binding Q2Dizziness}"` … binding verbatim** so the VM's mutual-exclusivity logic (`ClearQ2None`, `ClearQ5NoneAndSkipped`) keeps firing. "都沒有 / None" and "跳過不答 / Prefer not to share" are the same `Option.Cell`, visually identical (the exclusivity is VM-driven, not styling-driven).
- **Q3 (1–5 intensity, mandatory):** the dedup centerpiece. Replace the five 40-line templates (`:220–355`) with **one `Scale.Segmented`** `ItemsControl` over a 5-item model `{Value:int, Label:string}`:
  - `1 / 無 None`, `2 / 輕微 Mild`, `3 / 中等 Moderate`, `4 / 明顯 Strong`, `5 / 嚴重 Severe`.
  - Cell rest: `SurfaceSunken` bg, 1px `Separator`, `Radius.sm` (8), `MinHeight 58`. Number = `Type.Heading` `TextPrimary`; sub-label = **`Type.Caption` (12) `TextSecondary`** — raised from the failing `10px #7B8DB0`.
  - **Selected:** single-hue sequential ramp `AccentWeak → Accent` by index (1 lightest → 5 deepest), `TextOnAccent`, **plus a `Checkmark Circle` (Fluent) glyph** so selection is shape-coded, not color-only.
  - hover: 1px `Accent` border; focus: focus ring on the cell; **arrow keys** move selection (it is one radio group).
  - **Binding contract:** the cell's `Tag` carries `Value` (1–5) and fires `Click=OnQ3OptionClicked`, which already parses `Tag.ToString()` to int (`CalibrationCardView.xaml.cs:37-41`). Keep `GroupName="Q3"` and the `Click` handler — **do not** switch to a `Command`, because the existing code-behind shim is the tested contract.

### 3.4 ASCII wireframe (new layout, Q3 card shown — the dedup case)

```
┌─────────────────────────────────────────────────────────────┐  Card
│  [ 校正 · Calibration ]                       ● ● ● ○ ○      │  Badge.Accent  +  Progress.Dots (3 of 5)
│                                                               │  Pad.Card 24, Radius.md 12, Elev.2 (neutral)
│  依您剛看完 demo 的感受作答 · Answer based on how it felt      │  Type.Caption / TextSecondary (7.4:1)
│                                                               │
│  Q3 · 您感受的整體強度？                                       │  Type.Heading 16 / TextPrimary (15.3:1)
│  Overall intensity — required to continue.                    │  Type.Body 14 / TextSecondary
│                                                               │
│  ┌────────┬────────┬────────┬────────┬────────┐               │  Scale.Segmented (ONE ItemsControl)
│  │   1    │   2    │   3 ✓  │   4    │   5    │               │  MinHeight 58, Radius.sm 8
│  │  無    │ 輕微   │ 中等   │ 明顯   │ 嚴重   │               │  number=Heading, sub=Caption 12 (raised from 10)
│  │ None   │ Mild   │[Moder] │ Strong │ Severe │               │  selected = AccentWeak→Accent ramp + ✓ glyph
│  └────────┴────────┴────────┴────────┴────────┘               │  arrow-key navigable radio group
│                                                               │
│  ┌───────────────────────────────────────────────────────┐   │  Field.Text  (SurfaceSunken, Separator 1px)
│  │ 補充（選填）· Notes (optional)                         │   │  real placeholder adorner, Type.Body 14
│  └───────────────────────────────────────────────────────┘   │  MinHeight 44, Radius.sm 8
│                                                               │
│  取消·Cancel                       上一題·Back   下一題·Next   │  Btn.Quiet | Btn.Ghost | Btn.Primary
└─────────────────────────────────────────────────────────────┘  footer Pad.Button 14,8
        (Next greyed → disabled when CanAdvance==false on Q3)
```

For **multi-select cards (Q2/Q5)** the answer region is a vertical stack of `Option.Cell` checkbox rows inside a `SurfaceMuted` grouped container (`Radius.md`, 1px `Separator` between rows); for **single-select (Q1/Q4)** the same cells in radio mode.

### 3.5 States

| State | Treatment |
|---|---|
| **rest** | tokens above; `Surface` card on `Canvas`. |
| **hover** (option/cell) | `Option.Cell` → bg `SurfaceSunken`; `Scale.Segmented` cell → 1px `Accent` border. Buttons per `Controls.xaml` hover tokens (`AccentHover` etc.). |
| **focus** | 2px `FocusRing` (`#3A6EA5`) + 1px white inset on every interactive element (cells, options, fields, buttons). Currently **absent**. |
| **selected/checked** | single-select: filled ramp + `Checkmark Circle` glyph; multi-select: `AccentWeak` bg + `Accent` check glyph. Never hue-only. |
| **empty** | This sheet is never empty — it always shows a question. "Empty answer" on Q1/Q2/Q4/Q5 is **valid** (skipping allowed per VM `CanAdvance`); only Q3 gates. No empty-state art. |
| **loading** | None. The VM is synchronous in-memory; no async fetch. Do **not** add an "AI is thinking" affordance here. |
| **error / gate** | Q3 unanswered → **`Btn.Primary` Next renders disabled** (`SurfaceMuted`/`TextTertiary`, `IsHitTestVisible=false`), plus a `Type.Caption` `TextSecondary` hint under the scale: "請先選一個強度 / Pick an intensity to continue." This is a **gentle gate, not an error** — never red, never a `Danger` token, never a shake. (`CanAdvance` already drives `NextCommand.CanExecute`, so the disabled visual is automatic — just style the disabled state.) |

### 3.6 Elevation & radius summary

Card = `Elev.2` + `Radius.md` (12). Cells, fields, buttons = `Radius.sm` (8). Badge/dots = `Radius.pill`. Shadow color is neutral black (retires blue `#5B82C6` at `:120`). No element exceeds `Radius.lg`.

## 4. Motion & Reduce-Motion

This surface is **chrome**, not the clinical mask — so all its motion is governed by the global `ReduceMotion` switch (none of it is the safety-exempt `Dur.overlay` mask channel).

- **Card-to-card transition (Next/Back):** a single quiet cross-fade of the answer region over `Dur.base` (200ms) `Ease.calmOut`. Because only one `IsCardNVisible` block is ever visible (VM-driven via `BoolToVis`), this is a fade on the swapped `StackPanel`, never a horizontal slide/carousel (slide motion is itself a mild vection trigger for this audience).
- **Progress.Dots fill:** the active dot fills over `Dur.fast` (120ms) when the step advances.
- **Selection (`Scale.Segmented` / `Option.Cell`):** background/glyph cross-fade over `Dur.fast` (120ms) `Ease.calmOut` — never a scale-bounce or pulse.
- **No looping, breathing, pulsing, or color-cycling anywhere** (consistent with the bar/tray "no flashing" spec).

**Reduce-Motion (when `ReduceMotion == true`):** every transition above collapses to an **instant swap** (duration → 0) — card change is an immediate show/hide, dot fill snaps, selection snaps. Bind all storyboards to the shared `Dur.ui` `{DynamicResource}` so the toggle re-skins this view with zero per-view code. Nothing on this surface is exempt (the mask exemption does not apply here).

## 5. Accessibility

**AutomationProperties (currently 0 on this view):**
- Card root: `AutomationProperties.Name="Calibration, step N of 5"` bound off `CardCounterText`.
- Question title: it is the labeled-by target for the answer group — set `AutomationProperties.LabeledBy` from each option group to the title `TextBlock`.
- Each `Option.Cell` / scale cell: `AutomationProperties.Name` = its full bilingual label (e.g., "頭暈, Dizziness"; "Intensity 3, 中等 Moderate").
- `Progress.Dots`: `AutomationProperties.Name="Step 3 of 5"`.
- Next/Back/Complete/Cancel: accessible names from their bilingual content; when Next is gated, expose `AutomationProperties.HelpText="Select an intensity to continue."`
- Q3 scale container: `AutomationProperties.Name="Overall intensity, 1 none to 5 severe, required"`, role radio-group.

**Focus order (logical, top→bottom, left→right):** Progress.Dots are non-focusable (decorative + announced) → answer controls (option cells / scale cells, arrow-key navigable within the group, single Tab stop for the group) → notes `Field.Text` → Cancel → Back → Next/Complete. Tab never lands on the badge or static text.

**Contrast confirmations (all pairings on this surface):**
- `TextPrimary #1A2230` on `Surface #FFFFFF` = **15.3:1** (AAA) — title, scale numbers.
- `TextSecondary #525B6B` on `Surface` = **7.4:1** (AAA) — every helper/sub-label/eyebrow (replaces the failing ~3.6:1 `#7B8DB0`).
- `TextSecondary #525B6B` on `SurfaceMuted #F1F3F6` (grouped option container) = **6.6:1** (AA all, AAA large).
- `Accent #3A6EA5` scale-cell number area is backed by white text on the deepest ramp step: `TextOnAccent #FFFFFF` on `Accent #3A6EA5` = **4.9:1** (AA); lighter ramp steps use `TextPrimary` on `AccentWeak` (high-contrast).
- `Field.Text` value `TextPrimary` on `SurfaceSunken #ECEEF2` ≈ 13:1 (AAA).

**Colorblind-safe status:** Q3 selection and Q2/Q5 checks carry a **shape (checkmark glyph) + label**, not hue alone — survives deuteranopia/protanopia and OS High-Contrast (where the ramp collapses to a single system accent and the glyph carries the meaning).

**Minimum sizes:** body/option text ≥ **14px**; all sub-labels/captions ≥ **12px** (retires every `10`/`11`); scale cells `MinHeight 58`; option rows `MinHeight 48`; notes field `MinHeight 44`; buttons inherit `Pad.Button` (≥ 32px effective height). No fixed `Height` on any text container — use `MinHeight + Auto` so the card **grows, not clips** at Windows 125%/150% display scaling and the 100–225% text-size slider.

## 6. Migration Notes (cannot-break-prior-works)

**Binding/contract surface that MUST be preserved verbatim** (every name is exercised by `CalibrationCardViewModelTests` and/or the code-behind):

- **Visibility flags:** `IsCard1Visible … IsCard5Visible` (`BoolToVis` converter) — keep the five mutually-exclusive `StackPanel`s and their bindings. The redesign re-skins their *contents*, not the visibility mechanism.
- **Header/nav:** `CardCounterText`, `IsLastCard` (drives the Next↔Complete `DataTrigger`, `:451/467`). Keep both; `CardCounterText` now also feeds `Progress.Dots` + the root `AutomationProperties.Name`.
- **Commands:** `NextCommand`, `BackCommand`, `CompleteCommand`, `CancelCommand` — re-style the buttons, keep the `Command` bindings and the `IsLastCard` visibility triggers.
- **Single-select Click handlers:** `OnQ1OptionClicked`, `OnQ3OptionClicked`, `OnQ4OptionClicked` (`CalibrationCardView.xaml.cs:26-49`) rely on `RadioButton.Tag` being `UncomfortableSegment` / a parseable int / `BaselineComparison`. **Keep `GroupName`, the `Tag={x:Static inf:…}` / `Tag="1".."5"`, and the `Click=` wiring.** If `Scale.Segmented` is an `ItemsControl`, ensure each generated item is still a `RadioButton` (or that the cell raises the same `Click` with `Tag` carrying the int) so the code-behind shim is unchanged — **do not** rewrite these as `Command`s (the comment at `xaml.cs:9-15` documents *why* Click is used; that's the tested path).
- **Two-way bindings (keep exact property names):** Q2 — `Q2Dizziness, Q2Nausea, Q2EyeStrain, Q2Headache, Q2Imbalance, Q2Disorientation, Q2None`; Q4 — `Q4NoBaseline`; Q5 — `Q5MotionSickness, Q5Migraine, Q5VestibularDiagnosed, Q5VrSusceptible, Q5UnexplainedDizziness, Q5None, Q5Skipped`; freetext — `Q1Freetext … Q5Freetext` with `UpdateSourceTrigger=PropertyChanged`. The VM's mutual-exclusivity side-effects fire on set, so the bindings must stay `TwoWay`/`PropertyChanged`.
- **xmlns:** keep `xmlns:inf="clr-namespace:SenseEase.Core.Inference;assembly=SenseEase.Core"` (used by the `Tag` statics) and `xmlns:views` registration in `MainWindow.xaml`.

**Tests touching this view:**
- `tests/SenseEase.Tests/CalibrationCardViewModelTests.cs` — **24 tests, all against the VM, zero view dependency.** None instantiate `CalibrationCardView`, so they are unaffected as long as the VM property/command names above are untouched (they are — this is a pure XAML re-skin).
- `tests/SenseEase.Tests/MainWindowSmokeTests.cs` — **this view IS in scope.** `MainWindow` (`:872`) constructs `CalibrationCardView` during first layout. Therefore: **(a)** tokens go view-local (§3.1), **never** `App.Resources`, or `{StaticResource Token}` throws at construction → red smoke test; **(b)** every token key referenced (`Card`, `Btn.Primary`, `Type.Heading`, `Scale.Segmented`, etc.) must resolve from the merged `SenseEaseTheme.xaml`, or construction throws. Verify the smoke test stays green after the merge.

**"No orphan hex" gate:** after migration, this file must contain **zero** raw `#RRGGBB` literals and no `Background="White"` (`:118`) / `Foreground` named colors — currently ~30+ literals across the styles and five templates. It moves from the baseline allowlist to clean.

**Behavior invariants untouched (D-class):** no copy that changes Q3-mandatory semantics, no new fourth scale state, no reset/destructive action introduced here (this is an interview, not Settings). The five questions, their order, bilingual strings, and skip-allowed semantics are preserved — only typography, color, spacing, dedup, and a11y change.

**Evidence files read:** `src/SenseEase.App/Views/CalibrationCardView.xaml` (full), `src/SenseEase.App/Views/CalibrationCardView.xaml.cs`, `src/SenseEase.App/ViewModels/CalibrationCardViewModel.cs`, `src/SenseEase.App/MainWindow.xaml:855-884`, `src/SenseEase.Core/Inference/CalibrationTypes.cs:15-145`, `tests/SenseEase.Tests/CalibrationCardViewModelTests.cs`, `tests/SenseEase.Tests/MainWindowSmokeTests.cs`.

---

# Settings (grouped iOS-style lists + Switches)

I have complete, decision-grade evidence. Delivering the spec.

---

## Settings — Visual Redesign Spec ("Calm" Rebrand, Alpha-1)

*Surface:* `src/SenseEase.App/Views/SettingsContent.xaml` (shared UserControl), hosted by the main console "設定" tab (`MainWindow.xaml:926-929`, `x:Name="SettingsTabContent"`) **and** the topmost tray/Card-gear `SettingsWindow` (`SettingsWindow.xaml:30`, `x:Name="ContentHost"`). One file, two hosts, state shared through `UserPreferences.Current` + `AppConfig.*` + the `UserPreferences.Changed` event.

### 1. PURPOSE

The single job: **let the user adjust how SenseEase behaves in the background and how visible it is — calmly, reversibly, and without fear of breaking their protection.** The one question the user is asking: *"How do I make this fit my machine and my body, and can I trust that nothing here will hurt me?"* Every control is a low-stakes, instantly-reversible preference; the surface must read as a quiet utility shelf, not a control panel. It is the only place a head-tracking webcam opt-in (Privacy invariant B1), the escape-hatch toggle (Clinical A3), and the destructive-adjacent run-at-startup registry write live — so legibility and honest consequence labeling outrank density.

### 2. CURRENT PROBLEMS (cited)

**Palette / token violations (the "Calm" rebrand core):**
- Hardcoded dark navy `#0A1F5C` on every label and section title (`SettingsContent.xaml:46,53,69,82,88,103,116,123,163,168,180,194,238` — 14 occurrences) → must become `TextPrimary #1A2230`.
- Royal-blue accent `#024AD8` in the dev-table headers (`:258,260`) and the host Close button (`SettingsWindow.xaml:37`) → `Accent #3A6EA5`.
- Sub-label helper text `#7B8DB0` (`:59,73,94,108,150,174,186,200,242`) is the **retired ~3.6:1 contrast failure** flagged in the design system — fails WCAG AA as essential explanatory text → `TextSecondary #525B6B`.
- English caption `#9AB6E0` (`:155`) and the dev-table footnote `#9AB6E0` (`:276`) are the **retired Skip-button failure hue (~4.6:1 large only)**, used here at 11px body — fails AA → `TextSecondary`.
- Container `Background="White"` + blue `DropShadowEffect Color="#5B82C6"` (`:22-26`) → `Surface` + neutral `Elev.2` (the blue shadow cast is explicitly retired, design system §3.3).
- Restart button uses a bespoke amber-on-cream palette `#8B5A00 / #FFF4D6 / #E5A841` (`:140-142`) — none are tokens; the closest semantic is `ComfortRising` weak/text, but per Migration §8 this should not invent a fourth button style.
- Dev-table well `#F8FAFF / #E8EEFF` (`:245`) → `SurfaceSunken` / `Separator` (but this whole block is `Visibility="Collapsed"`, `:235`, so it is dead and should be deleted, see §6).
- Host `SettingsWindow` background `#E2ECFF` (`:11`) and the main tab `Background="#E2ECFF"` (`MainWindow.xaml:927`) → `Canvas #F6F7F9`. This `#E2ECFF` is the saturated blue canvas the rebrand explicitly replaces.

**Typography violations (14px body floor, no 10/11px):**
- Body labels at `FontSize="13"` (`:54,69,89,103,124,169,181,195`) — below the 14px floor → `Type.Body` (14).
- Every helper/explanatory `TextBlock` at `FontSize="11"` (`:58,73,93,107,149,154,162,173,185,199,206,241`) — **violates the no-10/11px rule** → `Type.Caption` (12) minimum.
- Dev-table footnote at `FontSize="10"` (`:276`) — the smallest violation.
- Section titles use bare `FontWeight="Bold"` at 16 / 14 (`:45,81,115,162,237`) instead of `Type.Heading` / `Section.Header` SemiBold.

**Component / structure problems:**
- **Raw `CheckBox` for all 6 booleans** (`:51,66,86,100,121,192`) — the design system mandates `Toggle.Switch` for every settings boolean (§4 Switch). Bare checkboxes have a 13px hit-target glyph, no focus ring spec, and `AutomationProperties = 0`.
- **Raw `RadioButton` follow-mode pair** (`:165,177`) with no grouped-row container.
- **No grouped iOS-style list structure** — controls are loose `StackPanel`s in a flat `Grid` (`:28-41`) with ad-hoc margins (`0,0,0,16` / `0,0,0,12` / `0,0,0,8` — the "spacing soup" §3.1 retires). No `Surface` row cards, no `Separator` hairlines, no `SectionHeader`.
- **Bilingual strings crammed inline** ("平衡 Balanced (建議 / recommended)", `:167`; "啟用頭部追蹤 / Enable Head Tracking", `:122`) — violates Clarity; the two-language jam doubles cognitive load on overstimulated users.
- **Engineering register in copy:** "Python sidecar"、"MediaPipe 推論臉部姿態"、"HKCU 註冊表項目" (`:106,148`) leak implementation detail the user does not need.
- **AutomationProperties = 0** across the entire file — no screen-reader names on any toggle, radio, or button.
- **Dark/light clash residue:** the comment at `:13-18` documents that this surface was *retrofitted* from the dark Pop-up theme to a light console palette — the rebrand finishes that job by tokenizing both, so the tab and the SettingsWindow stop being two near-identical hand-painted palettes.
- **Vestibular note color `#C2410C`** (`:207`) is an orange-red that is neither the retired alarm red nor a token → must become `ComfortHigh #B65C4E` (muted clay) per Clinical A5 / Product D2 (block-reason styling is muted clay, never alarm red).
- **Restart-button placement** (`:134`) sits *inline next to* the head-tracking checkbox, conflating "enable" with "restart now" — and its amber fill makes it the loudest element on the screen, violating Deference.
- **No Reduce-Motion / High-Contrast handling** anywhere.

### 3. REDESIGN

**Container & host:**
- Outer container → `Card` token: `Surface` bg, `Radius.lg` (16, it's a full window/sheet body), `Pad.Card` (24), `Elev.2` neutral shadow. Replace `Border Background="White" CornerRadius="14"` + blue shadow (`:22-26`).
- `SettingsWindow` background and the main-tab `Grid Background` → `Canvas` (`#F6F7F9`), via `{DynamicResource Canvas}`.
- Host Close button (`SettingsWindow.xaml:31-42`) → `Btn.Quiet` ("關閉 / Close" stays bilingual *only here* because it is window chrome; or split label). Retire `#024AD8` fill.

**Structure: three `Section.Header` + grouped `List.Row` containers (iOS grouped-list pattern).** Each section is a `SurfaceMuted` rounded container (`Radius.md` 12) holding `Surface` rows separated by 1px `Separator` hairlines (no hairline after the last row). Each row = `List.Row`: title `Type.Body` + one-line helper `Type.Caption TextSecondary` on the left, trailing `Toggle.Switch` on the right, `MinHeight 48`. The whole row is the focus/hit target.

**Section A — 通知與顯示 (Notifications & display)** `Section.Header`
- Row 1: **主動展開通知卡** — `Toggle.Switch` ↔ `ToggleAuto`. Helper: "偵測到較強不適時自動展開通知卡。預設關閉。" (`Type.Caption`).
- Row 2: **Bar 與 Tray 顏色隨壓力變化** — `Toggle.Switch` ↔ `ToggleColor`. Helper references the three comfort bands by **comfort language** ("灰 / 琥珀 / 紅" → "Steady / Rising / High" or "平穩 / 上升 / 偏高"), aligning with `TierColorPalette` (Product D2). Keep the vestibular-warning `MessageBox` intercept (code-behind `:148-184`) untouched.

**Section B — 背景常駐 (Runs in the background)** `Section.Header`
- Row 3: **關閉視窗時收到工作匣** — `Toggle.Switch` ↔ `ToggleMinimizeToTray`. Helper: plain language, drop nothing functional.
- Row 4: **登入時自動啟動** — `Toggle.Switch` ↔ `ToggleRunAtStartup`. Helper: drop "透過 HKCU 註冊表項目實作" engineering detail → "登入 Windows 時自動在背景啟動，不會彈出視窗。" Keep the registry-write-first + revert-on-failure logic (code-behind `:194-215`) and its failure `MessageBox` verbatim.

**Section C — 頭部追蹤 (Head tracking · webcam)** `Section.Header` with an eyebrow `Type.CaptionStrong` privacy line: **"使用本機 webcam；影像不會離開你的裝置。" (uses your webcam locally; nothing leaves your device)** — Privacy B1 honest caption, always visible above the toggle.
- Row 5: **啟用頭部追蹤** — `Toggle.Switch` ↔ `ToggleHeadTrackingEnabled`. Helper drops "Python sidecar / MediaPipe 推論臉部姿態" → "讓 FOV 遮罩跟著你的視線移動。預設關閉以保護隱私。". A **`Badge.Neutral`** "需重新啟動 / restart needed" sits in the row's trailing area *as a static label*, NOT a loud amber button.
- **Restart action** moves OUT of the row into a single full-width **`Btn.Ghost`** ("套用變更並重啟 / Restart to apply") rendered *only when the head-tracking toggle state differs from the running session* — quiet, not amber, not the loudest thing on screen. Wires to existing `OnRestartClicked` (`:258`). Retire the `#8B5A00/#FFF4D6/#E5A841` palette.
- Row 6 (sub-group, indented inside Section C): **跟隨模式 / Follow mode** — `SectionHeader` sub-label, then a 2-cell `Scale.Segmented`-style or two `Option.Cell` radios: **平衡 (建議)** ↔ `FollowModeBalanced` / **完整跟隨** ↔ `FollowModeFull`, each with a `Type.Caption` consequence line. Single-hue selected state (`AccentWeak → Accent`), never categorical color. This sub-group is visually de-emphasized (disabled-look) when head-tracking is off — but stays present so the user sees what opt-in unlocks.
- Row 7: **暫時透明（連點兩下 Ctrl）** — `Toggle.Switch` ↔ `ToggleEscapeHatch`. Helper: "短暫降低遮罩到 30% 透明度 3 秒。". **Disabled state for vestibular users** (Clinical A3): the Switch renders `disabled` (track `SurfaceMuted`, 50% thumb) and the `EscapeHatchVestibularNote` (`:203`) is shown as a **`Status.Chip` Idle/Info** style — muted clay `ComfortHigh #B65C4E` text + a shield/lock glyph + plain phrase "此功能不適用於你的設定檔，以維持視覺保護。" Retire `#C2410C`. The code-behind already drives `IsEnabled=false` + tooltip + note visibility (`:104-118`) — the Switch template just needs a disabled visual; no logic change.

**Elevation / radius summary:** outer card `Radius.lg`/`Elev.2`; each grouped section container `Radius.md`, `SurfaceMuted`, no shadow (grouping by space + fill, not elevation — gentle Depth); rows flat `Surface`; restart `Btn.Ghost` no shadow; switches `Elev.1` thumb at rest.

**States:**
- *rest:* tokens above.
- *hover (row):* row bg → `SurfaceMuted`; Switch thumb grows 1px.
- *focus:* 2px `FocusRing` (#3A6EA5) + 1px inset around the whole row (or the Switch track if the control is the only target).
- *empty/loading:* none — settings are always populated from `UserPreferences.Current`; there is no async load. (If `AutoStartRegistry.IsEnabled()` is slow, it is synchronous today; no spinner needed.)
- *error:* registry-write failure and vestibular-warning paths keep their existing `MessageBox` (code-behind `:161,205,277,301`); the in-surface error affordance is the **revert-the-toggle** behavior already coded (`:202-203`) — visually the Switch animates back to its prior state under `Dur.fast` (snaps under Reduce-Motion).
- *disabled:* escape-hatch Switch for vestibular profiles (above); follow-mode sub-group when head-tracking off.

**ASCII WIREFRAME (new layout):**

```
┌──────────────────────────────────────────────────────────┐  Card · Surface · Radius.lg · Elev.2 · Pad 24
│                                                            │
│  通知與顯示                                                │  Section.Header · Type.CaptionStrong · TextSecondary
│  ┌────────────────────────────────────────────────────┐  │  grouped container · SurfaceMuted · Radius.md
│  │ 主動展開通知卡                              (●——) │  │  List.Row · Type.Body + Switch · MinHeight 48
│  │ 偵測到較強不適時自動展開。預設關閉。              │  │  helper · Type.Caption · TextSecondary
│  ├────────────────────────────────────────────────────┤  │  1px Separator
│  │ Bar 與 Tray 顏色隨壓力變化                  (——○) │  │
│  │ 平穩(灰) / 上升(琥珀) / 偏高(clay)。              │  │
│  └────────────────────────────────────────────────────┘  │
│                                                            │  Gap.Stack (16)
│  背景常駐                                                  │  Section.Header
│  ┌────────────────────────────────────────────────────┐  │
│  │ 關閉視窗時收到工作匣                        (●——) │  │
│  │ 按 X 只收起，SenseEase 持續在背景偵測。          │  │
│  ├────────────────────────────────────────────────────┤  │
│  │ 登入時自動啟動                              (——○) │  │
│  │ 登入 Windows 時自動在背景啟動。                   │  │
│  └────────────────────────────────────────────────────┘  │
│                                                            │
│  頭部追蹤 · 使用本機 webcam，影像不會離開你的裝置          │  Section.Header + privacy eyebrow (CaptionStrong)
│  ┌────────────────────────────────────────────────────┐  │
│  │ 啟用頭部追蹤              [需重新啟動]      (——○) │  │  Badge.Neutral (static) + Switch
│  │ 讓遮罩跟著視線移動。預設關閉以保護隱私。          │  │
│  ├────────────────────────────────────────────────────┤  │
│  │   跟隨模式                                         │  │  sub-SectionHeader (indented)
│  │   (•) 平衡 Balanced (建議)                         │  │  Option.Cell radio · single-hue selected
│  │       中央 80% 區域，周邊維持部分遮蔽。            │  │
│  │   ( ) 完整跟隨 Full follow                         │  │
│  │       可看到邊緣，周邊變化較大。                   │  │
│  ├────────────────────────────────────────────────────┤  │
│  │ 暫時透明（連點兩下 Ctrl）                  (——○) │  │  Switch — disabled for vestibular
│  │ 短暫降低遮罩到 30% 透明度 3 秒。                  │  │
│  │ 🛡 此功能不適用於你的設定檔，以維持視覺保護。     │  │  Status.Chip · ComfortHigh clay + shield (only if vest)
│  └────────────────────────────────────────────────────┘  │
│                                                            │
│  [  套用變更並重啟 / Restart to apply  ]                  │  Btn.Ghost · full-width · shown only when HT state dirty
│                                                            │
└──────────────────────────────────────────────────────────┘
(Switch glyph:  (●——) = off · (——○) = on  — real control is a pill track + thumb, AAA-contrast, focus-ringed)
```

### 4. MOTION & REDUCE-MOTION

- **Switch thumb slide:** `Dur.fast` (120 ms) `Ease.calmOut`; under `ReduceMotion` it **snaps** (duration → 0).
- **Row hover bg fade:** `Dur.fast`; snaps under Reduce-Motion (instant swap, never a cross-fade-with-motion).
- **Follow-mode selected-cell fill:** `Dur.fast`; snaps under Reduce-Motion.
- **Restart `Btn.Ghost` appear/disappear** (when HT state goes dirty/clean): instant under Reduce-Motion; otherwise a `Dur.base` (200 ms) opacity fade — no slide, no size animation.
- **Vestibular `Status.Chip` reveal** (`EscapeHatchVestibularNote` becoming visible): instant; this is a safety/clarity affordance and must never animate in (it could read as a "new alert"). It simply *is present or not*.
- This surface has **no clinical-motion exemption** — there is no mask/brightness transition here, so `Dur.overlay` does not apply. All motion on Settings is decorative chrome and is fully governed by the global `ReduceMotion` switch (`Dur.ui` dynamic resource). Bind every storyboard `Duration` to `{DynamicResource Dur.ui}` so toggling Reduce-Motion re-skins this surface with zero per-control code.
- Nothing on Settings pulses, breathes, or color-cycles (consistent with the bar/tray "never animates" spec).

### 5. ACCESSIBILITY

**AutomationProperties (currently 0 — all new):**
- Each `Toggle.Switch`: `AutomationProperties.Name` = the row title phrase ("主動展開通知卡 / Auto-expand notification card"), and an `AutomationProperties.HelpText` = the helper line. State (on/off) is conveyed by the native ToggleButton automation peer.
- Follow-mode radios: `AutomationProperties.Name` = "跟隨模式：平衡（建議）" / "跟隨模式：完整跟隨"; the group exposes a radio-group peer.
- Restart `Btn.Ghost`: `AutomationProperties.Name` = "套用頭部追蹤變更並重啟 SenseEase".
- Escape-hatch Switch when disabled: `AutomationProperties.HelpText` = the full vestibular phrase so a screen reader announces *why* it is unavailable (mirrors the existing tooltip set at code-behind `:107-110`).
- Restart `Badge.Neutral` is decorative-adjacent but should carry `AutomationProperties.Name` = "需重新啟動" so its meaning is not lost.

**Focus order (top-to-bottom, single logical flow):** ToggleAuto → ToggleColor → ToggleMinimizeToTray → ToggleRunAtStartup → ToggleHeadTrackingEnabled → (restart button, only when present) → FollowModeBalanced → FollowModeFull → ToggleEscapeHatch → (host Close button, SettingsWindow only). Radios are a single tab-stop with arrow-key traversal (it's a radio group). Every row shows a visible `FocusRing`.

**Contrast confirmations (all from the locked Light table):** `TextPrimary #1A2230` on `Surface` = 15.3:1 (AAA); helper `TextSecondary #525B6B` on `Surface` = 7.4:1 / on `SurfaceMuted` row-group = 6.6:1 (AA all sizes, the fix for the failing `#7B8DB0`); `Accent #3A6EA5` on `Surface` = 4.9:1 (AA); `TextOnAccent #FFFFFF` on the on-Switch `Accent` track = 4.9:1; vestibular chip text `#8A3A2E` on `ComfortHighWeak` = 6.0:1 (AA), or `ComfortHigh #B65C4E` body text on `Surface` ≥ 4.5:1. Dark and HC themes re-skin via `{DynamicResource}`.

**Colorblind-safe status:** the only status-bearing element is the vestibular-disabled chip → triple-coded `Status.Chip` (shield/lock **glyph** + **text phrase** + clay color), never color-alone (fixes the implicit color-only cue). The Bar/Tray-color helper names the bands in **words** ("平穩 / 上升 / 偏高"), not just hue.

**Min sizes:** body `Type.Body` 14px floor; helper `Type.Caption` 12px floor (kills every 10/11px). Switch hit target ≥ 44×24 track inside a `MinHeight 48` row (whole row tappable). Use `MinHeight` + `Auto` height on all rows so they grow, not clip, at Windows 125/150% scaling and the "Make text bigger" 100–225% slider; verify reflow there. `TextOptions.TextFormattingMode="Ideal"`, `TextRenderingMode="ClearType"` at theme root.

### 6. MIGRATION NOTES (cannot-break-prior-works)

**Keep every `x:Name` and event handler signature byte-for-byte** — the code-behind (`SettingsContent.xaml.cs`) binds to fields by name and is **not** changing:
- Named elements that MUST survive the rewrite: `ToggleAuto`, `ToggleColor`, `ToggleMinimizeToTray`, `ToggleRunAtStartup`, `ToggleHeadTrackingEnabled`, `FollowModeBalanced`, `FollowModeFull`, `ToggleEscapeHatch`, `EscapeHatchVestibularNote`, `RestartButton`.
- `RefreshFromPreferences()` (`:76`) sets `.IsChecked` on the four `CheckBox`es and two `RadioButton`s, and `.IsEnabled` + `.ToolTip` + `.Visibility` on the escape-hatch controls. **A `Toggle.Switch` must therefore be a `ToggleButton`-derived control exposing `IsChecked` (bool?)** so `ToggleAuto.IsChecked = …` keeps compiling. The design-system `Toggle.Switch` is a styled `ToggleButton` / `CheckBox` re-template — implement it as a `Style`/`ControlTemplate` over `CheckBox` (or `ToggleButton`), NOT a new custom type, so the `.IsChecked` API, `Checked`/`Unchecked` events, and the `_wiring` guard all stay identical. The follow-mode radios stay `RadioButton` with `GroupName="HeadTrackingFollowMode"` (`:166,178`) and `Checked="OnFollowModeChanged"` — only restyled via `Option.Cell` template.
- `EscapeHatchVestibularNote` must remain a control whose `.Visibility` is set from code-behind (`:111,117`). If it becomes a `Status.Chip`, name the chip `EscapeHatchVestibularNote` and ensure `.Visibility` still works (it will — any FrameworkElement has it).
- `RestartButton` must keep `Click="OnRestartClicked"` (`:145,258`). It may be re-templated to `Btn.Ghost` and relocated in the visual tree; the handler is position-independent.

**Token delivery (the smoke-test trap):** merge `SenseEaseTheme.xaml` into **`UserControl.Resources`** of SettingsContent via the locked `pack://application:,,,/SenseEase.App;component/Theme/SenseEaseTheme.xaml` snippet — **never** into `App.Resources`. Rationale: `MainWindowSmokeTests.cs:29` constructs a bare `new Application()` and never instantiates `SenseEase.App.App`, and the smoke test reaches this control because `MainWindow` hosts `SettingsTabContent` (`MainWindow.xaml:928`). View-local merge resolves all `{StaticResource}`/`{DynamicResource}` tokens at construction time in that bare-Application path; `App.Resources` would throw and turn the smoke test red. Apply the same view-local merge to `SettingsWindow.xaml`.

**Tests that touch this view (flag list):**
1. **`tests/SenseEase.Tests/MainWindowSmokeTests.cs`** — constructs `MainWindow` on an STA thread and fails on any binding error / `TypeInitializer` / unhandled exception during construction + first layout (`:20,41`). Because `MainWindow` hosts `SettingsTabContent`, this test **will exercise the redesigned SettingsContent**. Any unresolved `{StaticResource}` token, a renamed `x:Name` the code-behind still references, or a `Toggle.Switch` that doesn't expose `IsChecked` → red CI. This is the gate the view-local merge protects.
2. **`tests/SenseEase.Tests/UserPreferencesTests.cs:252-270`** — validates the `UserPreferences.Changed` cross-instance sync that fires after `Save()` (the mechanism powering Settings-tab ↔ SettingsWindow live sync). The redesign **must not touch** `OnPreferencesChangedExternally` / `RefreshFromPreferences` / the `PreferencesChanged` event or the `_wiring` guard — keep them byte-identical so this test stays green.

**Host plumbing to preserve (not tests, but binding contracts):** `MainWindow.xaml.cs:100-101` sets `SettingsTabContent.HasVestibularDiagnosis` and subscribes `PreferencesChanged`; `SettingsWindow.xaml.cs:36-37` and `NotificationCardWindow.xaml.cs:588-593` do the same on `ContentHost`. The public `Func<bool>? HasVestibularDiagnosis` property and `event EventHandler? PreferencesChanged` (code-behind `:42,50`) are the cross-window contract — **do not rename or change their signatures.**

**Behavior locks NOT to touch (Product/Clinical invariants):** the `BarTrayColorEnabled` vestibular-warning `MessageBox` single-warning intercept (`:148-184`), the registry-write-first/revert-on-failure flow (`:194-215`), the head-tracking restart handoff (`:258-305`), and the escape-hatch vestibular force-disable (`:104-118`) are all behavior — restyle their *visuals* only; leave the logic and all `MessageBox` strings exactly as-is (the hotline/medical-advisory copy is locked).

**Dead-code cleanup (safe, in-scope):** the entire `Grid.Row="10"` developer-hotkey reference table (`:212-278`) is `Visibility="Collapsed"` and documents two non-functional hotkeys deferred to Beta (`:221-234`). It contains five `#024AD8`/`#7B8DB0`/`#9AB6E0`/`#F8FAFF` orphan-hex literals. It carries no `x:Name`, no binding, no handler — **delete it** in the rebrand (it is unreferenced by code-behind and tests) rather than tokenize a hidden block; this also shrinks the "no orphan hex" lint baseline. Re-exposing it is a separate Beta task per its own comment.

**Lint:** after migration this file should contribute **0** to the "no orphan hex" CI gate (design-system §7.3) — all ~30 hex literals here become tokens or are deleted with the dev table. The companion `SettingsWindow.xaml` (`#E2ECFF`, `#024AD8`) and the `MainWindow.xaml:927` tab `Background="#E2ECFF"` must be migrated in the same PR or they remain orphans on the allowlist.

**Evidence files read:** `src/SenseEase.App/Views/SettingsContent.xaml`, `src/SenseEase.App/Views/SettingsContent.xaml.cs`, `src/SenseEase.App/Views/SettingsWindow.xaml`, `src/SenseEase.App/MainWindow.xaml` (tab host `:918-930`), `src/SenseEase.App/MainWindow.xaml.cs` (`:100-101,646-648`), `src/SenseEase.App/Configuration/UserPreferences.cs` (`:64-153`), `tests/SenseEase.Tests/MainWindowSmokeTests.cs`, `tests/SenseEase.Tests/UserPreferencesTests.cs:252-270`.

---

# Notification / Pop-up Card  *** USER-PRIORITY SURFACE ***

I now have all the ground-truth evidence I need: the current XAML, the code-behind contract (every x:Name and event that must survive), and the 5 unit tests that pin specific x:Names (`TitleText`, `BodyText`, `PowerSaveToggle`, `ProposalPanel`, `ProposalSummaryText`, `ProposalRationaleText`, `ProposalAcceptBtn`, `ProposalDeclineBtn`, `ProposalSafetyNoteText`) and the title string format. I have everything required to write a decision-grade spec.

## Notification / Pop-up Card — Visual Redesign Spec (Calm)

*Surface:* `src/SenseEase.App/Views/NotificationCardWindow.xaml` (+ `.xaml.cs`). Status: USER-PRIORITY. Already light-themed; the work here is decluttering, retokenizing, and triple-coding — never a behavior change.

---

### 1. Purpose

The pop-up Card is the **single moment of contact** between the silent background app and a user who may already feel unwell. It auto-pops on a tier-up band crossing (or on bar/tray click) and answers exactly one question the user is asking:

> **"Something changed on my screen — what is happening to me, and how do I make it stop right now?"**

Its one job is to (a) name the current comfort state in plain language and (b) offer **one effortless primary action that eases the view immediately**. Everything else (proposal handling, fine-grained discomfort reporting, chat, power-save, settings) is secondary and must visually recede. Because it appears *when the user may be nauseated*, the cardinal sin is cognitive load: the current card stacks up to six competing sections, which is the opposite of effortless.

---

### 2. Current Problems (cited to current file)

**Density / cognitive load (the headline problem)**
- Six stacked rows compete with equal visual weight: title, proposal panel, body, 4-button discomfort scale, action row (3 buttons + toggle), chat input + status — `NotificationCardWindow.xaml:87–94` row definitions. No primary action stands out; `EngageBtn` ("啟動保護", `:345`) is a peer of Dashboard and Mute, not the hero.
- The action row crams a power toggle plus **three** buttons across 480 dip (`:311–365`), and the discomfort scale adds **four** more buttons (`:249–304`) — seven action targets visible at rest on a card meant to reduce strain.

**Two clashing palettes / four-hue alarm ramp**
- The discomfort scale is a categorical four-hue **alarm ramp** — blue `#E5F0F8` → amber `#FFF1D6` → orange `#FFE0CC` → clay `#FBD0CB` (`:253/267/281/295`) with four matching text colors (`:254/268/282/296`). This is the exact "categorical-alarm" pattern the design system forbids; it must become a single-hue sequential ramp (`Scale.Segmented`).
- `EngageBtn` uses a saturated CTA orange `#FF9B6B` on near-black text (`:348–349`) — loud, off-palette, and reads as "alarm," contradicting the Calm direction.
- The proposal panel is a blue-tinted card-within-a-card (`#F2F6FE` bg, `#C3D2EE` border, `:137–138`) with its own blue accent `#1E5BB0` (`:148`), visually a second palette layered on the white card.

**Contrast / type-scale violations**
- `FontSize="11"` on the discomfort eyebrow "視覺不適程度" (`:238`) and proposal rationale (`:159`) — below the 12px Caption floor.
- `FontSize="10"` on `ProposalExpiryText` (`:212`) and `ChatStatusText` (`:425`) — well below floor; expiry/status are essential.
- `#5A6478`, `#7A8499`, `#9AA3B0` placeholder (`:160/213/400`) are off-token secondary/tertiary greys; the `#9AA3B0` placeholder on `#F7F8FA` is borderline AA.
- Discomfort button text colors (`#7A5310`, `#9B4F1A`, `#8C2F23`, `:268/282/296`) are bespoke per-hue values not in the token set.

**Engineering jargon as headline**
- Title reads `"VIMS 視覺壓力　第 N 級 / 10　·　壓力指數 P"` (`.xaml.cs:467`) — "VIMS / pressure index P" is telemetry, not plain language, and is the first thing a nauseated user reads.

**Duplicated / ad-hoc styles**
- Bespoke inline buttons everywhere: proposal Accept/Decline/Later (`:177–207`), the four discomfort buttons (`:249–304`), Engage/Mute (`:345–364`) each re-declare `Background/Foreground/Padding/FontSize` instead of consuming a component style. Only `PrimaryDeepBlueButtonStyle` (`:31–67`) is shared, and it is off-token (`#1E5BB0` fill, `CornerRadius=3`).
- Radius vocabulary is inconsistent: card `10` (`:77`), proposal `6` (`:140`), primary button template `3` (`:46`) — none match `Radius.sm/md/lg` (8/12/16).
- Spacing is ad-hoc: `Padding="16"` card (`:78`), `12,10` proposal (`:141`), `4,8`/`12,6`/`14,8` buttons — the "6/8/12/14/16" soup the system retires.
- Shadow is `BlurRadius=14, Depth=3, Opacity=0.18` (`:83–84`) — not the `Elev.3` token (Y8/Blur28/0.14).

**Motion / accessibility gaps**
- `AutomationProperties = 0` on every control; close `×` (`:101`) and gear `⚙` (`:111`) are glyph-only buttons with no accessible name (tooltips only on gear).
- Status is conveyed by hue alone on the discomfort ramp — no icon/label channel (colorblind + HC fail).
- 220ms fade-in (`.xaml.cs:370`) and 5s idle fade (`:512`) are hardcoded, not tokenized, and ignore Reduce-Motion.
- `VisualBrush` placeholder hack on the chat box (`:398–412`) — the audit-flagged pattern to retire for a real `Field.Text` adorner placeholder.

---

### 3. Redesign

**Container.** `Window` stays (transparent, topmost, click-through-exempt). Root `Border` → `Card` token: `Surface` bg, `Radius.lg` (16, it's a floating popup), `Pad.Card` (24), `Elev.3` shadow (neutral black Y8/Blur28/0.14), 1px `Separator` border in HC only. Width stays **480** (binding/positioning math in `.xaml.cs:343` depends on it; do not change). `SizeToContent="Height"` stays so the card grows with text scaling.

**Core principle: progressive disclosure.** At rest the card shows only **four** zones — header, comfort state, one primary action, one quiet secondary row. The discomfort ramp, chat, power-save, and settings collapse into a single **"More options"** disclosure (`QuietButton` + chevron) so the default surface is calm. The proposal panel only appears when `ActiveProposal != null` (already conditional, keep it but restyle and lift it to the top as the hero when present).

#### Layout top-to-bottom

**Zone A — Header** (`Grid.Row=0`, retains `DockPanel`)
- Left: `TitleText` → `Type.Heading` (16/22 SemiBold) `TextPrimary`. **Copy change (logic-only, not a behavior pin):** drop telemetry. Replace `.xaml.cs:467` string with plain language keyed off band, e.g. Steady → "畫面平穩", Rising → "偵測到視覺壓力上升", High → "正在保護你的視野". Keep tier/pressure available only as a `Type.Caption` `TextSecondary` sub-line for power users — not the headline. *(This is allowed: copy/format lives in code-behind, and the only test pin is the exact title string in `NotificationCardWindowTests.cs:40` — see Migration.)*
- Right: gear → `Icon.Settings` (Fluent, 20px, `TextSecondary`), close → `Icon.Dismiss`. Both become `QuietButton` icon variants, `MinWidth/MinHeight 32` hit target (up from 22×22), `AutomationProperties.Name` = "設定 / Settings" and "關閉 / Close".

**Zone B — Proposal panel** (`Grid.Row=1`, `ProposalPanel`, conditional)
- Restyle the inner `Border` to `Card.Compact` (`SurfaceMuted` bg, `Radius.md` 12, `Pad` 16, 1px `Separator` — **not** the blue `#F2F6FE/#C3D2EE`). When visible it is the hero; sits above body.
- `ProposalHeaderText` → eyebrow `Type.CaptionStrong` `TextSecondary` with `Icon.Lightbulb`; prefix "AI suggests: " convention preserved.
- `ProposalSummaryText` → `Type.BodyStrong` `TextPrimary`. `ProposalRationaleText` → `Type.Caption` (12, **up from 11**) `TextSecondary`, italic.
- `ProposalSafetyNoteText` → `Type.Caption` `ComfortHigh` (`#B65C4E`, **not** the old `#8C2F23`) with `Icon.Warning` (the ⚠ from `.xaml.cs:148` becomes icon + clay text — triple-coded block reason per Guardrail A5). Stays `Collapsed` by default.
- Buttons: Accept → `PrimaryButton` (`Accent` fill), Decline → `GhostButton`, Later → `QuietButton`. `ProposalExpiryText` → `Type.Caption` (12, **up from 10**) `TextSecondary`.

**Zone C — Comfort state** (`Grid.Row=2`, replaces bare `BodyText`)
- A single `StatusChip` (`Status.Chip`, triple-coded) + `BodyText` as supporting `Type.Body` `TextPrimary`.
- Chip variant by band via `TierColorPalette`: Grey→`Idle/Steady` (moon/check, neutral/teal), Amber→`Rising` (alert-triangle, `ComfortRising`), Red→`High` (shield/waves, `ComfortHigh` clay). Icon + label + color — never hue alone. `BodyText` keeps the existing `DescriptionForBand` strings (`.xaml.cs:478–484`).

**Zone D — Primary action** (`Grid.Row=3`) — *the one effortless action*
- A **single, full-width** `PrimaryButton`: **"立即放鬆視野 / Ease my view now"** wired to the existing `EngageClicked` event (rename `EngageBtn` content only; keep x:Name + handler). `Type.BodyStrong`, `Accent` fill, `Radius.sm`, `Elev.1`, `MinHeight 44`. This replaces the loud orange `#FF9B6B` (`:348`). It is the visually dominant element on the card.

**Zone E — Quiet secondary row** (`Grid.Row=4`)
- Two `QuietButton`s only: **"開啟主視窗 / Open dashboard"** (`OpenDashboardBtn`) and **"6 小時內不再提示 / Mute 6h"** (`MuteBtn`). De-emphasized, `TextSecondary`, no fill. Keep `OpenDashboardBtn`'s shared style but repoint to `Btn.Quiet`.

**Zone F — Disclosure: "More options"** (`Grid.Row=5`, collapsed group)
- A `QuietButton` "更多選項 / More options" + chevron toggles a `StackPanel` containing the previously-always-visible secondary controls. Collapsed by default so a nauseated user never sees them unless they ask:
  - **Discomfort ramp** → `Scale.Segmented` (the load-bearing dedup). One `ItemsControl` over a 4-item model `{Tag 1–4, Label 輕微/中等/明顯/嚴重}`, **single-hue sequential ramp** `ComfortSteadyWeak → ComfortHigh` (sequential, never categorical), `Radius.sm`, `MinHeight 44`, keyboard radio-group. Each cell still fires `OnQuickDiscomfortClicked` via its `Tag` (handler reads `btn.Tag`, `.xaml.cs:666` — preserve the `Tag="1..4"` contract). Eyebrow "視覺不適程度" → `Type.CaptionStrong` (12, up from 11) `TextSecondary`.
  - **Chat input** → `Field.Text` (real adorner placeholder, retires the `VisualBrush` hack `:398–412`) + `PrimaryButton`-small "送出 / Send". `ChatStatusText` → `Type.Caption` (12, up from 10) `TextSecondary`. Keep `ChatInputBox`/`ChatSendBtn` x:Names + `KeyDown`/`Click` handlers.
  - **Power-save** → `Toggle.Switch` (replaces bare `CheckBox`) labeled "暫停感測 / Pause sensing", with `Icon.Power`. Keep `PowerSaveToggle` x:Name and `Checked/Unchecked` handlers (test `:74` sets `IsChecked` directly — a `ToggleButton`-based switch preserves `IsChecked`).

#### ASCII wireframe (rest state, disclosure collapsed)

```
┌────────────────────────────────────────────────────────┐  ← Card: Surface, Radius.lg(16), Pad.Card(24), Elev.3
│  偵測到視覺壓力上升              [⚙ settings] [✕ close]  │  Zone A: Title=Type.Heading TextPrimary
│  第 6 級 · 壓力指數 41                                   │          sub-line=Type.Caption TextSecondary (power-user)
│                                                        │
│  ┌─ 💡 AI suggests ─────────────────────────────────┐  │  Zone B: ProposalPanel (only if ActiveProposal≠null)
│  │ 把 sensitivity 調整為 0.8                          │  │          Card.Compact, SurfaceMuted, Radius.md
│  │ 理由：user reports easy nausea       剩 01:45     │  │          summary=BodyStrong / rationale=Caption italic
│  │ [ 接受 ] [ 拒絕 ]  稍後                            │  │          Accept=Primary, Decline=Ghost, Later=Quiet
│  └──────────────────────────────────────────────────┘  │
│                                                        │
│  ⚠ 偵測到中度視覺壓力                                    │  Zone C: StatusChip (icon+label+color) = Rising
│  建議短暫休息或暗化螢幕。                                 │          BodyText=Type.Body TextPrimary
│                                                        │
│  ┌──────────────────────────────────────────────────┐  │  Zone D: PRIMARY — full-width, Accent fill,
│  │            立即放鬆視野 / Ease my view now          │  │          Type.BodyStrong, MinHeight 44  ← the hero
│  └──────────────────────────────────────────────────┘  │
│                                                        │
│    開啟主視窗            6 小時內不再提示                  │  Zone E: two QuietButtons, TextSecondary
│                                                        │
│  ⌄ 更多選項 / More options                              │  Zone F: disclosure (collapsed at rest)
└────────────────────────────────────────────────────────┘
        ▼ expands to reveal ▼
        ┌ 視覺不適程度 ────────────────────────────┐
        │ [ 輕微 ][ 中等 ][ 明顯 ][ 嚴重 ]          │  Scale.Segmented, single-hue ramp Steady→High
        │  ┌──────────────────────────┐ [ 送出 ]    │  Field.Text placeholder + Send
        │  │ 跟 SenseEase 說話…        │            │
        │  └──────────────────────────┘             │
        │  狀態列 (Type.Caption)                     │  ChatStatusText
        │  [⏻]── 暫停感測 / Pause sensing            │  Toggle.Switch (was CheckBox)
        └──────────────────────────────────────────┘
```

#### States (per zone)

- **Rest:** disclosure collapsed; proposal hidden; primary action prominent.
- **Hover:** Primary → `AccentHover`; Quiet → `SurfaceMuted` bg; segmented cell → 1px `Accent` border; switch thumb +1px.
- **Focus:** every interactive element gets the 2px `FocusRing` + 1px inset (`Accent`/`#3A6EA5`).
- **Empty:** no `ActiveProposal` → Zone B collapsed (existing logic `.xaml.cs:121`); no chat text → adorner placeholder shows.
- **Loading:** chat "傳送中…" → `ChatStatusText` shows a static "傳送中…" (no spinner motion under Reduce-Motion). `ChatSendBtn`/`ChatInputBox` disabled (existing `.xaml.cs:704–705`).
- **Error:** chat failure → `ChatStatusText` in `ComfortHigh` clay caption (existing `.xaml.cs:717`); proposal blocked → `ProposalSafetyNoteText` clay + ⚠ icon, Accept disabled (existing `.xaml.cs:148–152`).
- **Power-save active:** title/body swap to the paused message (existing `ApplyContentToUi`, `.xaml.cs:446–449`); switch reads ON.
- **Proposal expired:** Accept/Later disabled, expiry text "已過期" (existing `.xaml.cs:202–204`) — rendered as `TextTertiary` caption tombstone.

**Elevation/radius summary:** Card = `Elev.3` + `Radius.lg`; proposal sub-card = `Elev.1` + `Radius.md`; buttons/cells/field = `Radius.sm`; chips/switch track = `Radius.pill`.

---

### 4. Motion & Reduce-Motion

- **Show:** fade-in 0→1 over `Dur.base` (200ms, was 220 hardcoded `.xaml.cs:370`) with `Ease.calmOut`. No slide/scale (would be a motion trigger).
- **Idle collapse:** fade 1→0 over `FadeDuration` (5s, keep) with `Ease.calmOut`; reversible-on-mousemove logic unchanged.
- **Disclosure expand:** height reveal over `Dur.base` `Ease.calmOut`.
- **Segmented selection / switch thumb / chip:** `Dur.fast` (120ms).
- **Proposal expiry countdown text:** updates on the existing 15s `DispatcherTimer` (`.xaml.cs:186`) — no animation.
- **Reduce-Motion (`ReduceMotion = manualToggle OR NOT SystemParameters.ClientAreaAnimation`):** all of the above collapse to **instant** swaps (`Dur.ui` → 0) — fade-in becomes an instant show, disclosure snaps open, switch thumb snaps, "傳送中…" never animates. Bind storyboard durations to the `Dur.ui` `{DynamicResource}` so no per-control code is needed.
- **Exempt — never affected by this card:** the clinical mask cross-fade lives in `OverlayWindow`, not here. This card has **no** mask-driving motion, so there is no `Dur.overlay` safety concern in this surface; do not add any pulsing/breathing to the comfort chip (Guardrail A4).

---

### 5. Accessibility

**AutomationProperties.Name (all currently 0):**
| Element | Name |
|---|---|
| Card (Window) | "SenseEase 通知 / SenseEase notification" |
| `TitleText` | (LiveSetting=Polite — announce band-change on update) |
| Close | "關閉 / Close" |
| Settings | "設定 / Settings" |
| `StatusChip` (Zone C) | full phrase, e.g. "視覺壓力上升，建議休息 / Rising strain, take a break" |
| Primary action | "立即放鬆視野 / Ease my view now" |
| `OpenDashboardBtn` | "開啟主視窗 / Open dashboard" |
| `MuteBtn` | "6 小時內不再提示 / Mute for 6 hours" |
| Disclosure | "更多選項 / More options" (+ `ExpandCollapseState`) |
| Segmented cells | "輕微/中等/明顯/嚴重 不適 / Slight/Mild/Strong/Severe discomfort" (radio group) |
| `ChatInputBox` | "跟 SenseEase 說話 / Talk to SenseEase" |
| `ChatSendBtn` | "送出 / Send" |
| `PowerSaveToggle` | "暫停感測 / Pause sensing" |
| Proposal Accept/Decline/Later | "接受建議 / Decline / 稍後" |
| `ProposalSafetyNoteText` | LiveSetting=Assertive (announce block reason) |

**Focus order (tab):** Title (skip) → Proposal Accept → Decline → Later (when visible) → Primary "Ease my view now" → Open dashboard → Mute → More options → [disclosure: segmented 1–4 as one radio group → chat input → send → power switch] → Settings → Close. Primary action reachable in ≤1 tab from open.

**Contrast (all token pairings AA+ floor, AAA where noted):**
- `TextPrimary #1A2230` on `Surface #FFFFFF` = 15.3:1 (AAA).
- `TextSecondary #525B6B` on `Surface` = 7.4:1 (AAA) — fixes the off-token `#5A6478/#7A8499`.
- Primary `TextOnAccent #FFFFFF` on `Accent #3A6EA5` = 4.9:1 (AA).
- Block reason `ComfortHigh` chip text `#8A3A2E` on `ComfortHighWeak #F6E2DE` = 6.0:1 (AA) — replaces the bespoke `#8C2F23`.
- Segmented sub-labels at `Type.Caption` 12px `TextSecondary` = AA — retires the 10/11px violations (`:159/212/238/425`).

**Colorblind-safe:** comfort state is icon + label + color (`StatusChip`); discomfort ramp is sequential single-hue + position + label, never categorical hue. Survives HC (system colors win; meaning carried by icon+label).

**Min sizes:** all hit targets `MinHeight 44`/`MinWidth 44` (close/gear raised from 22×22); body ≥14px, caption ≥12px floor; no fixed text-container `Height` (use `MinHeight + Auto`) so the card reflows, not clips, at OS text scaling 100–225%.

---

### 6. Migration Notes (cannot break prior works)

**Hard pins — preserve exactly (5 tests in `tests/SenseEase.Tests/NotificationCardWindowTests.cs`):**
1. **All x:Names referenced by tests must survive verbatim:** `TitleText`, `BodyText`, `PowerSaveToggle`, `ProposalPanel`, `ProposalSummaryText`, `ProposalRationaleText`, `ProposalAcceptBtn`, `ProposalDeclineBtn`, `ProposalSafetyNoteText`. Tests read `.Text`, `.Visibility`, `.IsEnabled`, `.IsChecked` and `RaiseEvent(ButtonBase.ClickEvent)` on these (`tests:40/57–58/74/95/107–110/138/167/197–199`). Restyling via component styles is fine; **renaming or removing any of these breaks CI.**
2. **`PowerSaveToggle` must remain a `ToggleButton`-derived control exposing `IsChecked`** (test `:74/77` sets `IsChecked` and expects `PowerSaveToggled`). The `Toggle.Switch` component must be `CheckBox`/`ToggleButton`-based, not a custom non-`ToggleButton` control — keep `Checked`/`Unchecked` handler wiring (`.xaml.cs:329–330`).
3. **Title string test pin:** `NotificationCardWindow_SetContent_FormatsNumericTitle` asserts the **exact** string `"VIMS 視覺壓力　第 3 級 / 10　·　壓力指數 27"` (`tests:40`). The plain-language title copy change in Zone A **is a test-touching change** — it must be flagged in the PR and the test updated in lockstep (this is a copy/logic change, permitted under C5 "only chatbot logic can be redefined" only if flagged; the safer path is to keep `TitleText` carrying the plain headline and update this one assertion, documenting it as an intentional Calm copy change). Do **not** silently change the string without updating the test. Power-save title test (`:57–58`) and band-description body (`:480–484`) strings must stay byte-identical unless similarly flagged.
4. **`ApplyTemplate()` reliance:** tests call `w.ApplyTemplate()` then touch named elements — all named elements must resolve from the template/visual tree as today (don't move them behind a lazy/virtualized container that defers realization).

**Event/property contract — keep every public member used by the host (`MainWindow.xaml.cs`, `MainViewModel.cs`):**
`EngageClicked`, `MuteClicked`, `PowerSaveToggled`, `ProposalAccepted/Declined/Deferred/Expired`, `ActiveProposal`, `ShowProposalApplyResult`, `SetContent`, `IsPowerSaveActive`, `SendChatMessage`, `HasVestibularDiagnosis`, `OnPreferencesChanged`, `IdleTimeout`, `FadeDuration`, `GetMonitorRectInPhysicalPixels`. All click handlers (`OnEngageClicked`, `OnMuteClicked`, `OnQuickDiscomfortClicked`, `OnChatSendClicked`, `OnChatInputKeyDown`, `OnPowerSaveToggled`, `OnSettingsClicked`, `OnOpenDashboardClicked`, `OnProposal*Clicked`, `OnCardMouseMove`, `OnCloseClicked`) keep their signatures; the redesign only re-skins the controls that fire them.

**Discomfort ramp dedup caveat:** `OnQuickDiscomfortClicked` reads `btn.Tag` (`.xaml.cs:666`). If migrating the 4 buttons to a data-driven `Scale.Segmented` `ItemsControl`, each generated cell must still expose `Tag="1".."4"` and route to `OnQuickDiscomfortClicked`, OR the handler is refactored to read the selected index — either is a behavior-preserving change but must keep the 1→4 severity → `QuickDiscomfortPhrases` mapping (`.xaml.cs:648–655`) intact.

**Theme plumbing (per the locked WPF pattern):** merge `SenseEaseTheme.xaml` into **this Window's own `.Resources`** via pack URI (NOT `App.Resources` — `MainWindowSmokeTests` boots a bare `Application`). Keep the existing `Window.Resources` block; replace `PrimaryDeepBlueButtonStyle` (`:31–67`) usages with `Btn.Primary`/`Btn.Quiet` from the theme, then delete the local style. The `VisualBrush` placeholder (`:398–412`) is replaced by `Field.Text`'s adorner placeholder.

**"No orphan hex" gate:** this file currently carries ~40 inline hex literals (every color cited in §2). Migrating to tokens drives them to 0; add this file to the lint allowlist only during migration, then remove it when green.

**Positioning invariants:** keep `Width=480` (positioning math `.xaml.cs:343`), `SizeToContent="Height"`, `WindowStartupLocation="Manual"`, transparency/topmost flags, and `OnCardMouseMove` on the root border (idle-timer reset, `.xaml.cs:486`). The card already registers its rect via `GetMonitorRectInPhysicalPixels` (`.xaml.cs:741`) for the WGC exclude mask (Guardrail C2) — adding height via the disclosure must keep that rect registration firing on resize (host calls it on render); flag to engineering that the disclosure-expand changes `ActualHeight` and must re-register the exclude rect.

**Do not regress (Guardrails):** keep the blocked-proposal `SafetyNote` visible and clay+⚠ (A5); mute copy stays "6 小時內" in-memory band-scoped semantics (D4); Decline/Accept disable-on-expiry behavior intact (D4); no escape-hatch/mask control added here (A2/A3); power-save stays a sensing pause, never framed as battery (existing tooltip `.xaml.cs:328`).

---

# Spike popup + Ambient bar + Tray icon + Validation banner

I now have complete, decision-grade evidence across all four surfaces, their code-behind contracts, the shared `TierColorPalette`, and every test that touches them. Delivering the spec.

---

# Spike Popup · Ambient Bar · Tray Icon · Validation Banner — "Calm" Redesign Spec

This section covers four small, top-most, transient surfaces that share one job — **ambient peripheral status with no demand for attention** — but today speak three clashing visual languages and one engineering-debug language. They are specced together because they form the "status-without-the-dashboard" layer of SenseEase: the bar is always present, the tray icon is always present, the spike popup interrupts rarely, and the validation banner is a developer/QA artifact that occasionally leaks to users.

Evidence base: `src/SenseEase.App/Views/SpikePopupWindow.xaml`, `PersistentBarWindow.xaml`, `BarStubWindow.xaml`, `ValidationModeBanner.xaml` and their code-behind; `src/SenseEase.App/Views/TierColorPalette.cs`; tests `PersistentBarWindowTests.cs`, `PopupSurfaceGateTests.cs`, `MainWindowSmokeTests.cs`; host wiring `MainWindow.xaml.cs:156, 682, 1070, 1105`.

---

## 1. PURPOSE

| Surface | Single job | The one question the user is asking |
|---|---|---|
| **Ambient bar** (`PersistentBarWindow`) | A glanceable, non-interactive 4-dip strip at the top-center edge that encodes the current comfort band (Steady / Rising / High) by color + height, and offers a click target to open the Card. | *"Am I OK right now — and is SenseEase watching?"* |
| **Tray icon** (`TierColorPalette.IconForTier_Win`) | The OS-level always-resident proof-of-life + comfort band, visible when the bar is off-screen or the user is in another app. | *"Is SenseEase running, and what's my state?"* |
| **Spike popup** (`SpikePopupWindow`) | A rare, gentle peripheral check-in fired by the spike detector that asks one question and routes the answer into the assistant. | *"Something just changed — how does that feel to you?"* |
| **Validation banner** (`ValidationModeBanner`) | A self-correcting reassurance that the user is seeing the *validation* build (cyan stub, no tray) on purpose — not a bug. | *"Why does my app look broken right now?"* |

`BarStubWindow` is a **QA-only** stimulus window (red/cyan flash for WGC optical-flow validation, `BarStubWindow.xaml.cs:43`). It is **out of scope for visual calm** — its job is to be an adversarial flashing target, and the validation banner exists precisely to tell the user it is intentional. The spec below leaves `BarStubWindow` visually untouched and instead hardens the banner that contextualizes it.

The unifying principle: per the Design North Star, **none of these surfaces may demand attention.** The bar and tray are Deference incarnate (quiet chrome, content is the comfort state); the spike popup is the single permitted *gentle* interruption; the banner must read as "informational," never "alarm."

---

## 2. CURRENT PROBLEMS

### 2.1 Spike popup (`SpikePopupWindow.xaml`)
- **Dark-card-on-light-app clash (HARD).** Container is `Background="#CC1A1A1A"` (`:13`) — a near-black floating card that lands over the light `#E2ECFF` dashboard. This is exactly the "two clashing palettes" audit fact. It belongs to the retired dark-popup palette.
- **Jargon headline.** Title is `"● VIMS spike"` (`:23`) — engineering telemetry ("VIMS", "spike") as the first thing an overstimulated user reads, violating Clarity ("plain language not engineering telemetry"). The leading `●` is a bare glyph, not a triple-coded status.
- **Alarm-adjacent accent.** `Foreground="#FF9B6B"` (`:24`) is bright orange — a retired CTA/alarm hue mapped in the migration crosswalk to `Accent`/`ComfortRising`, not raw `#FF9B6B`.
- **Contrast failures on essential text.** Close `×` uses `Foreground="#9AB6E0"` (`:32`) and the footer caption uses `#9AB6E0` at `FontSize="10"` (`:90–91`) — both retired contrast failures (`#9AB6E0` ≈ fails AA; 10px violates the 12px caption floor "No 10px or 11px anywhere user-facing").
- **Bare glyph close button.** `Content="×"` (`:28`) instead of the Fluent `Dismiss` icon.
- **Inline duplicated button style.** The option button carries a hand-rolled 25-line `ControlTemplate` with inline hex `#22FFFFFF` / `#3DFFFFFF` / `#F0F4FA` (`:58–84`) — exactly the per-view duplicated style the design system kills. It should be the shared `Btn.Ghost` / `Scale.Segmented` cell.
- **Hard-coded radius/spacing soup.** `CornerRadius="14"` (`:13`), `CornerRadius="6"` (`:70`), `Padding="14"`, `Padding="14,8"` — none on the 8/12/16 radius or 4/8pt spacing scale.
- **No AutomationProperties anywhere** (consistent with the app-wide `AutomationProperties = 0` defect). A screen-reader user gets "VIMS spike" read as a bullet and unlabeled buttons.

### 2.2 Ambient bar (`PersistentBarWindow.xaml`)
- **Raw inline hex bypasses the token layer.** `BarFill Background="#26666666"` (`:30`) and `SleepOverlay Stroke="#9AA0A8"` (`:35`) are literals. The live fill is set imperatively from `TierColorPalette` (correct single-source pattern) but the XAML defaults and the sleep-overlay stroke are orphan hex.
- **Tier hues are the old alarm palette.** `TierColorPalette.cs:43–45` defines Grey `102,102,102`, Amber `230,140,40`, Red `220,50,40` — the saturated console palette. Per D2 migration these must move to the Calm comfort tokens (`ComfortSteady #2F7D6E` / `ComfortRising #9A6B22` / `ComfortHigh #B65C4E`) **through `TierColorPalette` only** (the single source of truth all surfaces share). The red `220,50,40` is the alarm red the spec explicitly retires.
- **Radius off-scale.** `CornerRadius="2"` on both `BarFill` (`:31`) and `SleepOverlay` (`:37`) — retired (scale is 8/12/16/pill). For a 4-dip bar the correct token is `Radius.pill` (a 4-dip-tall pill reads as a soft lozenge, not a hard rectangle).
- **No accessibility.** The bar is `Focusable="False"` and click-only with zero `AutomationProperties` — a screen-reader user has no path to its meaning or its click action.

### 2.3 Tray icon (`TierColorPalette.IconForTier_Win`)
- **Color-only status (HARD, WCAG 2.2 / colorblind).** The tray icon is a flat filled square whose *only* signal is hue (`:189` `FillRectangle`, `:297` `DrawRoundedRectangle`). A deuteranope cannot distinguish amber from red. This is the same color-only-status defect flagged app-wide; the tray is the worst offender because the OS strips it to 16×16 and the user can't hover for a label easily.
- **Saturated hues** inherited from the shared `*Hue` table (same alarm palette as the bar).

### 2.4 Validation banner (`ValidationModeBanner.xaml`)
- **Pure engineering language + monospace.** `FontFamily="Consolas, Courier New, monospace"`, `FontSize="11"` (`:32–33`) renders raw diagnostic text — below the 12px caption floor and using Mono on a user-facing surface (spec: "Mono — diagnostics only, never on primary surfaces"; a banner the *user sees* is a primary surface even if its content is diagnostic).
- **Dark card + alarm border.** `Background="#E6201418"` (near-black, `:21`) with `BorderBrush="#FFFF9B6B"` (bright orange, `:24`) — dark-popup palette + retired alarm hue. The docstring even says it "matches the visual language of the bar's red tier" (`:21` of the `.cs`), which is precisely the alarm coupling to undo.
- **Inline hex throughout** (`#E6201418`, `#FFFF9B6B`, `#F0F4FA` at `:21/24/34`) and off-scale `CornerRadius="6"`, `Padding="14"` (`:22–23`).

---

## 3. REDESIGN

All four surfaces merge `Theme/SenseEaseTheme.xaml` **view-locally** (per the locked WPF delivery pattern §7.2 — never `App.Resources`, or `MainWindowSmokeTests.cs:29`'s bare `new Application()` will throw). All colors via `{DynamicResource}` so Dark / High-Contrast re-skin at runtime.

> **Theme note for floating surfaces:** the spike popup and validation banner sit over arbitrary content (any app, any wallpaper). They use `Surface` (a true card) with `Elev.3` and a 1px `Separator` border so they read as a deliberate floating card in **both** light and dark themes — resolving the dark-on-light clash by making them honest theme-following cards, not a fixed dark slab.

### 3.A Ambient bar

Structure is unchanged (one `Border` + one sleep `Rectangle` in a `Grid` — the test pins `BarFill` and `SleepOverlay` by name). Only tokens change.

```
            ┌────────────────────────────────┐   ← top-center, work-area top edge
            │▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁│   BarFill: 80dip × 4dip (6 on hover)
            └────────────────────────────────┘   Radius.pill, tier comfort tone
                                                  ┄┄┄ SleepOverlay (dashed) only in Power-Save
```

- **`BarFill`** — `Radius.pill` corner radius; default `Background` token = `ComfortSteadyWeak` (replaces orphan `#26666666`). Live fill driven by `TierColorPalette` as today (do not move tier logic into XAML — that is the single-source contract and the test target).
- **Tier tones (via `TierColorPalette`, the only place the mapping lives):**
  - Steady (tier 1–5): `ComfortSteady #2F7D6E` at α 0.15 → hover α 0.65
  - Rising (tier 6–7): `ComfortRising #9A6B22` at α 0.30 → hover α 0.92
  - High (tier 8–10): `ComfortHigh #B65C4E` at α 0.45 → hover α 1.00

  Alpha steps **0.15 / 0.30 / 0.45** and hover steps **0.65 / 0.92 / 1.00** are preserved exactly (they are pinned by `PersistentBarWindowTests`; only the RGB hue changes — see §6 for the precise test-migration handshake).
- **`SleepOverlay`** (Power-Save) — `Stroke` = `Separator` token (replaces `#9AA0A8`); `StrokeDashArray="4 2"` kept; `RadiusX/Y` = 4 (pill-equivalent at this height). The faint paused fill set in code (`Color.FromArgb(20, 0xDD,0xDD,0xDD)`, `.cs:234`) becomes `SurfaceMuted` at α 0.12 — calmer, theme-aware.
- **Elevation:** none (the bar is flush to the screen edge; a shadow on a 4-dip strip reads as a smudge). This is deliberate Deference.

### 3.B Tray icon — triple-coded glyph (fixes the color-only HARD defect)

The tray icon must carry meaning through **shape**, since the OS owns nothing else and color alone fails colorblind users. Render a **band-specific Fluent glyph** filled with the comfort tone, not a flat square:

| Band | Glyph (Fluent, the same set §5 mandates) | Tone |
|---|---|---|
| Steady (1–5) | `Checkmark Circle` outline | `ComfortSteady #2F7D6E` |
| Rising (6–7) | `Warning` (triangle) | `ComfortRising #9A6B22` |
| High (8–10) | `Shield` | `ComfortHigh #B65C4E` |

Render at 32×32 (existing `RenderIconBitmap` / `IconForTier_Win` pipeline, `:286, :162`) by replacing the `FillRectangle`/`DrawRoundedRectangle` call with a `Geometry` fill from the keyed `Icon.*` path. Shape survives the shell's 16×16 downscale far better than a hue shift, and the tray tooltip (`TrayIconService`) carries the full status phrase as the text channel. This is the §1.4 "triple-coding payoff" applied to the one surface the OS renders.

> This is a behavior-adjacent change to a shared table; per C5 it is a **visual** change (same band thresholds, same `BandForTier`), and the only test touching icon bytes (`GetIcoBytesForTesting` / `IconForTier_ReturnsTypeAcceptableToTrayLibrary`) asserts *container structure and type*, not pixels — see §6.

### 3.C Spike popup — calm card

```
 ┌───────────────────────────────────────────────┐  Surface card, Radius.lg(16), Elev.3
 │  ⚠  Checking in                            ✕   │  StatusChip(Rising) + QuietButton(Dismiss)
 │                                                 │
 │  Your screen got busier just now.               │  Type.Heading, TextPrimary  ← QuestionText
 │  How does that feel?                            │
 │                                                 │
 │  ┌──────────┐ ┌──────────┐ ┌──────────┐         │  Scale.Segmented cells (sequential ramp)
 │  │  Fine    │ │ A little │ │  Too     │   …     │  Btn.Ghost fallback for free-text options
 │  │          │ │  much    │ │  much    │         │  ← OptionList (WrapPanel preserved)
 │  └──────────┘ └──────────┘ └──────────┘         │
 │                                                 │
 │  Your answer goes to the assistant.             │  Type.Caption, TextSecondary  ← footer
 └───────────────────────────────────────────────┘
```

Top-to-bottom:
1. **Header row** (`DockPanel`, structure preserved):
   - **Leading:** `Status.Chip` variant `Rising` — `alert-triangle` Fluent icon + `Type.CaptionStrong` label **"Checking in"** (plain language; replaces `"● VIMS spike"`). `ComfortRisingWeak #F6ECD8` bg, `#7A4E16` text (AAA on weak bg). Triple-coded: icon + text + tone.
   - **Trailing:** `Btn.Quiet` close, **`Icon.Dismiss`** Fluent glyph (replaces bare `×`), `TextSecondary` (replaces failing `#9AB6E0`), 24×24 hit target, focus ring.
2. **Question** (`QuestionText`, x:Name preserved — set imperatively at `.cs:25`): `Type.Heading` (16/22 SemiBold), `TextPrimary`. The *content* string ("VIMS spike"-flavored) is built upstream by `FeedbackLexicon.BuildSpikeFollowup` (`MainWindow.xaml.cs:145`) — recommend the copy move to plain language ("Your screen got busier just now — how does that feel?") as a **lexicon proposal**, not a view edit, since the view only renders what it's handed.
3. **Options** (`OptionList`, x:Name + `ItemsSource` + `WrapPanel` preserved): each option becomes a **`Scale.Segmented` cell** (the de-duplicated, data-driven component) using the **single-hue sequential discomfort ramp `ComfortSteadyWeak → ComfortHigh`** — never the retired categorical alarm ramp. Cell: `SurfaceSunken` rest, 1px `Separator`, `Radius.sm`, `MinHeight 44`, `Type.Body` label, `TextPrimary`. The inline `ControlTemplate` (`:58–84`) is deleted; the click handler `OnOptionClicked` (`:47`, reads `Button.Content` as the label) is preserved by keeping each cell a `Button` whose `Content` is the option string.
4. **Footer caption** (preserved): plain-language **"Your answer goes to the assistant."** `Type.Caption` (12px floor, raised from 10px), `TextSecondary` (raised from failing `#9AB6E0`). Bilingual handled per app convention (not crammed as one inline string).

Container: `Surface` bg, **`Radius.lg` (16)**, `Pad.Card` (24, replaces `Padding="14"`), 1px `Separator` border, **`Elev.3`** (floating card; replaces the inline `DropShadowEffect`). `MaxWidth="360"` preserved.

### 3.D Validation banner — calm informational card

```
 ┌──────────────────────────────────────────────┐  Surface card, Radius.lg, Elev.3
 │  ⓘ  Validation mode                            │  StatusChip(Idle/neutral) — NOT alarm
 │                                                 │
 │  SenseEase is running a display test.           │  Type.Body, TextPrimary  (plain summary)
 │  The flashing bar and missing tray icon are     │
 │  expected and will clear shortly.               │
 │  ───────────────────────────────────────────   │  Separator hairline
 │  variable: SENSEEASE_WGC_BAR_VALIDATION = 1     │  Type.Mono 13px, TextSecondary
 │  scope: process · since 14:02:11Z               │  (diagnostic detail, de-emphasized)
 └──────────────────────────────────────────────┘
```

- **Header:** `Status.Chip` **Idle** variant (`info`/`moon` neutral glyph, `SurfaceMuted`/`TextSecondary`) labeled **"Validation mode"** — explicitly **not** the alarm/Rising tone. This banner is informational; coupling it to "the bar's red tier" (`.cs:21`) is the exact mistake to undo.
- **Plain-language summary** (new, `Type.Body`, `TextPrimary`): one sentence telling the user this is intentional. This is the calm answer to "why does my app look broken."
- **Separator** hairline.
- **Diagnostic detail** (`DiagnosticText`, x:Name + `.Text` setter at `.cs:58` preserved): keep `Type.Mono` (13px, raised from 11px — Mono is *allowed* here because it's genuinely a diagnostic sub-region, but it lives **below** a plain-language summary, not as the whole banner) in `TextSecondary`. Content still sourced from `ValidationModeDiagnostics.BuildDiagnosticText` (single source of truth, `.cs:25`).
- Container: `Surface` bg (replaces `#E6201418`), 1px `Separator` border (replaces alarm `#FFFF9B6B`), **`Radius.lg`**, `Pad.Card` (24), `Elev.3`. `MaxWidth="460"` preserved. Stays `IsHitTestVisible="False"` and click-through (`:14`) — unchanged.

### 3.E States (rest / hover / focus / empty / loading / error)

| Surface | rest | hover | focus | empty | loading | error |
|---|---|---|---|---|---|---|
| Bar | tier tone @ band α, 4dip | +height→6dip, hover-α brush (existing 100ms anim, `.cs:314`) | n/a (not focusable today; see A11y) | n/a (always present) | n/a | Power-Save = `SleepOverlay` dashed `Separator` + faint `SurfaceMuted` fill |
| Tray | band glyph @ tone | OS tooltip = status phrase | n/a (OS-owned) | n/a | n/a | grey `Checkmark Circle` when `BarTrayColorEnabled` off (existing `.cs:94` / `:249`) |
| Spike popup | Surface card, options at `SurfaceSunken` | option cell → 1px `Accent` border; close → `SurfaceMuted` bg | 2px `FocusRing` + 1px inset on close & each cell; arrow-key moves cell selection | if 0 options handed, hide `OptionList` row (no empty box) | n/a (synchronous) | n/a (answer routing is fire-and-forget at `.cs:51`) |
| Banner | Surface card, neutral chip | n/a (click-through) | n/a (click-through) | n/a | fade-in 220ms (existing) | n/a |

---

## 4. MOTION & REDUCE-MOTION

All durations move to tokens: `Dur.fast=120` · `Dur.base=200` · `Dur.slow=280`, easing `Ease.calmOut`.

| Surface | Motion today | Calm spec | Under Reduce-Motion |
|---|---|---|---|
| **Spike popup** | slide-down + fade-in 220ms cubic-ease-out (`.cs:40–44`) | Keep as a **fade-only** entrance over `Dur.base` (200ms), `Ease.calmOut`. Drop any vertical slide — a popup that *moves* toward an overstimulated user is a vection trigger. | **Instant** appear (opacity → 1, no animation). Per the contract, decorative entrance motion collapses to an instant swap. The popup must never *slide*. |
| **Bar hover** | height + opacity + brush over 100ms (`.cs:310–316`) | `Dur.fast` (120ms), `Ease.calmOut`. NO flashing/pulsing — this is already pinned by the bar's "NO animation" spec (`PersistentBarWindow.cs:16`). | Snap: height/opacity change instantly; brush swaps without tween. No pulsing ever (already invariant). |
| **Tray icon** | none ("never animates", per tray spec) | unchanged — never animates. | unchanged. |
| **Validation banner** | fade-in 220ms, hold 4s, fade-out 600ms (`.cs:74–88`) | fade-in `Dur.base`, fade-out `Dur.slow`, hold preserved (`HoldDuration`/`FadeDuration` props kept). | Instant show, instant hide after hold (fade-out → 0ms). Hold duration unchanged. |

**Safety note (A4):** none of these four surfaces drive the clinical FOV mask, so the `Dur.overlay` mask-smoothing exemption does not apply here — but the spike popup's entrance, the bar's hover, and the banner's fade **may** be collapsed under Reduce-Motion without touching any clinical path. Wire all four to the `Dur.ui` `{DynamicResource}` so the master switch re-skins them with zero per-view code.

---

## 5. ACCESSIBILITY

### AutomationProperties (currently 0 across all four — primary defect to fix)
- **Bar (`BarFill`):** `AutomationProperties.Name` = live status phrase, e.g. *"Comfort steady, you're protected"* / *"Strain rising"* / *"High strain, easing now."* Set imperatively alongside `SetTier` (one method already owns the tier→visual mapping, so add the name there). Because the bar becomes a click target that opens the Card, expose it as an invokable: `AutomationProperties.Name="Open SenseEase status"` on the click affordance + `HelpText` carrying the comfort phrase.
- **Tray icon:** status phrase as the tooltip + accessible name (the OS reads the NotifyIcon tooltip; ensure `TrayIconService` sets the band phrase, not "VIMS tier N").
- **Spike popup:** Window `AutomationProperties.Name="SenseEase check-in"`; `QuestionText` is the live region (`AutomationProperties.LiveSetting="Polite"` so it's announced on show without stealing focus); each option cell `AutomationProperties.Name` = its label; close button `AutomationProperties.Name="Dismiss check-in"`.
- **Banner:** Window `AutomationProperties.Name="Validation mode notice"`; summary as `LiveSetting="Polite"`. (It's click-through, so it must announce, not require focus.)

### Focus order (spike popup — the only focusable surface)
1. First option cell (so a keyboard user lands on the primary action), 2…N remaining cells (arrow-key navigable as a radio group per `Scale.Segmented`), final → Dismiss. The popup is non-modal and top-most; it must not steal focus from the user's active app on show (it currently doesn't — `Show()` not `ShowDialog()`, `MainWindow.xaml.cs:160`) — preserve that. Provide `Esc` → close (maps to `OnClose`).

### Contrast confirmations (all from the locked token table, Light theme)
- Spike popup `QuestionText` `TextPrimary #1A2230` on `Surface #FFFFFF` = **15.3:1** (AAA).
- Footer caption `TextSecondary #525B6B` on `Surface` = **7.4:1** (AAA) — replaces `#9AB6E0` ≈ 3.6:1 fail.
- Rising chip text `#7A4E16` on `ComfortRisingWeak #F6ECD8` = **6.4:1** (AAA large / AA all).
- Banner summary `TextPrimary` on `Surface` = **15.3:1**; mono detail `TextSecondary` on `Surface` = **7.4:1** (raised from `#F0F4FA`-on-near-black at 11px mono).

### Colorblind-safe status (HARD, fixes color-only defects)
- **Bar:** today color-only; mitigation is the **height channel** (4→6dip hover) plus the accessible name and the click-through-to-Card path. The bar is intentionally minimal, so its primary non-color channel is its accessible name + the tray glyph that shares its band.
- **Tray:** now **shape-coded** (`Checkmark Circle` / `Warning` / `Shield`) — the load-bearing colorblind fix, since the tray is the always-visible status and color alone failed.
- **Spike popup & banner:** every status is a `Status.Chip` = icon + label + tone (three channels).

### Minimum sizes
- Spike option cells `MinHeight 44` (touch/click floor), close button 24×24 visual with ≥32×32 hit area, all body ≥14px, all captions ≥12px (eliminates the 10px footer at `:90` and 11px mono at banner `:33`). Verify reflow at Windows 125%/150% and the "Make text bigger" 100–225% slider — use `MinHeight`+`Auto`, never fixed `Height` on text containers (the spike card `SizeToContent="WidthAndHeight"` already grows, which is correct).

---

## 6. MIGRATION NOTES — landing this without breaking prior works

This is a "cannot break prior works" shop (C5); below is the exact test/binding surface and how to keep it green.

### Preserved binding/member contracts (DO NOT rename)
- **`SpikePopupWindow`:** ctor `(string question, string[] options)`, x:Names `QuestionText` + `OptionList`, event `OptionSelected`, handlers `OnOptionClicked` / `OnClose`, and the contract that each option button's `Content` is the label string (read at `.cs:49`). Host wires all of these at `MainWindow.xaml.cs:156–160`. The `WrapPanel` items panel + `ItemsSource` assignment (`.cs:26`) must stay.
- **`PersistentBarWindow`:** x:Names **`BarFill`** and **`SleepOverlay`** are asserted directly by `PersistentBarWindowTests.cs:107–112` (`w.BarFill.Background`, `w.SleepOverlay.Visibility`). Methods `SetTier`, `EnterSleepMode`, `ExitSleepMode`, `BrushForTier`, and tunable props (`HoverProximityDip` etc.) must keep their signatures. **Do not move tier logic into XAML** — `SetTier` setting `BarFill.Background` imperatively is the tested contract.
- **`ValidationModeBanner`:** ctor `(string diagnosticText)`, x:Name `DiagnosticText` (`.cs:58`), props `HoldDuration`/`FadeDuration`. Host wires at `MainWindow.xaml.cs:1105`.
- **`BarStubWindow`:** x:Name `StimulusBorder` (`.cs:43`). **Leave visually unchanged** — it is the adversarial WGC stimulus; recoloring it would invalidate the optical-flow validation it exists for.

### The one behavior-adjacent change requiring a test handshake (flag in PR)
`PersistentBarWindowTests.cs:18–23` **pins exact RGB**: `Grey 0x66/0x66/0x66`, `Amber 0xE6/0x8C/0x28`, `Red 0xDC/0x32/0x28`, at alphas `0x26/0x4D/0x73`. Re-skinning `TierColorPalette` to the comfort tokens (`#2F7D6E` / `#9A6B22` / `#B65C4E`) **changes these RGB values** and will turn the test red.

This is a **deliberate, flagged** change under D2 ("re-skin the three bands to the desaturated comfort tokens … through `TierColorPalette` only") and C5 ("A designer may not alter the 3-band tier hue thresholds … without flagging it as a behavior change in the PR"). The **alphas (0.15/0.30/0.45) and band thresholds (1–5 / 6–7 / 8–10) are NOT changed** — only hue. Handshake:
1. Update the three RGB constants in `PersistentBarWindowTests.cs:19–23` to the new comfort hues **in the same PR**, with a comment citing the D2/Calm decision. This keeps the test as a pin on the *new* intended values, not a silent deletion.
2. Keep `BrushesAreFrozen_ShareableAcrossThreads` (`:69`) and the clamp tests (`:53–67`) **exactly as-is** — frozen + clamp behavior is unchanged.
3. `PopupSurfaceGateTests` and the `PersistentBarWindow_EnterExitSleepMode_RestoresBrush` STA test (`:91`) assert *structure/identity* (NotSame/Same instance, Visibility), not color — they pass unchanged.

### Tray icon change (visual, low test risk)
`IconForTier_Win` / `RenderIconBitmap` keep their signatures and the PNG-in-ICO container path; only the drawn geometry changes (rectangle → band glyph). The tray test `IconForTier_ReturnsTypeAcceptableToTrayLibrary` (referenced in `TierColorPalette.cs:313`) and `GetIcoBytesForTesting` assert **type and ICO container bytes**, not pixel content — unaffected. Flag as visual in PR per C6 (visual review is a user hard-stop; tests-green ≠ done for any rendered surface).

### App.Resources trap
Merge `SenseEaseTheme.xaml` into **each window's own `.Resources`** by pack URI (§7.2). Putting tokens in `App.Resources` breaks `MainWindowSmokeTests.cs:29` (bare `new Application()` never loads `App.xaml`). These four are `Window`s, so each gets its own `<Window.Resources><ResourceDictionary><MergedDictionaries>…</></></Window.Resources>` block.

### Orphan-hex lint
After migration, these four files must contain **zero** raw `#RRGGBB` and zero `Background="Black/White"` (per the §7.3 CI gate). Today they collectively carry ~12 orphan literals (`SpikePopup` `#CC1A1A1A`, `#33FFFFFF`, `#FF9B6B`, `#9AB6E0`, `#F0F4FA`, `#22/#3DFFFFFF`; `Bar` `#26666666`, `#9AA0A8`; `Banner` `#E6201418`, `#FFFF9B6B`, `#F0F4FA`). `BarStubWindow`'s `#FFFF0000` is the intentional exception and should be added to the lint allowlist with a comment (it is the adversarial validation stimulus, not UI chrome).

### Do not commit/push (C6)
Stage all four surfaces on a branch; produce before/after screenshots (light + dark + High-Contrast, and Reduce-Motion on) for the user's mandatory visual review. Green unit tests alone do not clear a visual change for merge.

---

**Files referenced:** `c:\Users\kirby\Desktop\SenseEase APP\src\SenseEase.App\Views\SpikePopupWindow.xaml`, `PersistentBarWindow.xaml`, `BarStubWindow.xaml`, `ValidationModeBanner.xaml` (+ their `.xaml.cs`); `c:\Users\kirby\Desktop\SenseEase APP\src\SenseEase.App\Views\TierColorPalette.cs`; `c:\Users\kirby\Desktop\SenseEase APP\src\SenseEase.App\MainWindow.xaml.cs` (host wiring lines 156, 682, 1070, 1105); tests `c:\Users\kirby\Desktop\SenseEase APP\tests\SenseEase.Tests\PersistentBarWindowTests.cs`, `PopupSurfaceGateTests.cs`, `MainWindowSmokeTests.cs`.

---

# FOV Overlay (the core protective field) + grid

I now have all the ground-truth evidence I need. Here is the decision-grade spec.

---

# FOV Overlay & Anchor Grid — Visual Redesign Spec ("Calm")

> Surface: `src/SenseEase.App/Views/OverlayWindow.xaml` (+ `.xaml.cs`). The CORE protective field: a transparent, topmost, click-through (`IsHitTestVisible="False"`, `OverlayWindow.xaml:12`) maximized window that renders the 3-zone radial FOV mask and the world-locked anchor grid. This is the one surface where **clinical-safety invariants (A1–A5, C1) dominate every aesthetic lever** — almost nothing here is a free brand choice.

## 1. PURPOSE

**The single job:** dim the visual periphery just enough, and exactly when needed, to suppress the peripheral optical-flow that drives vection/VIMS — while leaving a calm, readable clear-zone at the user's gaze center and a world-locked anchor grid that gives the vestibular system a stationary reference.

**The one question the user is asking when they see it:** *"Is the room steadying — and can I still see what I need to?"* The mask must answer "yes, I've got you" through depth and smoothness, never through alarm. The user must **never consciously notice a transition** — a noticed transition is itself a trigger.

Critically, this surface has **no chrome, no text, no controls, no DataContext binding** (it is driven imperatively via `IOverlayRenderer.ApplyState(SenseState)`, `IOverlayRenderer.cs:17`). So this "redesign" is **not** a layout exercise — it is a **token-palette + perceptual-tuning + safety-codification** exercise on two `Brush` color tables and one motion constant. Treat the wireframe as a *render-zone diagram*, not a control tree.

## 2. CURRENT PROBLEMS

| # | Problem | Evidence | Why it matters under "Calm" |
|---|---------|----------|------------------------------|
| P1 | **Mask color table is raw inline hex/ARGB, not tokenized.** The 10-tier `VimsLevels` table is hardcoded `(R,G,B,Alpha)` literals — `#666666 → #444444 → #222222 → #04040A → #000004` (`OverlayWindow.xaml.cs:54–66`). The "Calm" cool-slate direction is *approximated* by hand (+blue tint at L7+) rather than derived from the design system. | `OverlayWindow.xaml.cs:54–66` | This is the single most safety-laden color set in the app (A2: peak alpha 0.92 is a **clinical floor**, not a brand value). It must be a **named, documented, engineering-owned table** — but its L7–L10 *hue* is the one lever the "Calm" direction may retune (toward cool-slate) **as an engineering proposal** (A2 design-implication). |
| P2 | **Grid line + cell-fill colors are raw ARGB literals, palette-clashing with "Calm".** Grid lines `Color.FromArgb(130, 255, 255, 255)` (pure white, `:171`); cell-fill `Color.FromArgb(120, 8, 8, 10)` (near-black warm-neutral, `:85`). Neither references a token; pure-white lines read "console/technical," not "paper-calm." | `OverlayWindow.xaml.cs:85, 171` | The grid is a *calm reference*, not a HUD. Pure-white 51%-alpha lines are the loudest element on a bright screen. C1 allows retuning line weight + cell tone *as engineering proposals*. |
| P3 | **No `App.Resources` / theme token reach.** `OverlayWindow.xaml` declares zero `<Window.Resources>` and merges no theme dictionary (`OverlayWindow.xaml:1–19`). All color lives in code-behind static tables. So the "no orphan hex" lint (design-system §7.3) and the Light/Dark/HC re-skin path **cannot reach this surface at all** today. | `OverlayWindow.xaml:1–19` (empty resources) | The mask is **theme-invariant by clinical necessity** (it dims the screen regardless of app theme), but the *grid* and *cell-fill* tones are legitimate token consumers. Today neither can be themed. |
| P4 | **Mask cross-fade smoothing is an undocumented magic constant, not a safety token.** `MaskSmoothingFactor = 0.25` (~250 ms convergence, `:77`) is the **only thing standing between the user and a hard luminance cut** (A4 HARD-STOP). It is a bare `private const double` with no protection from a future edit, and it is **not** tied to the design-system `Dur.overlay` (≥300 ms) safety token. | `OverlayWindow.xaml.cs:77` | Design-system §3.4 clause 3 mandates `Dur.overlay` as a **safety token** "so no future edit can introduce a hard cut." Right now an innocent refactor could set this to `1.0` (instant snap) and no test would catch it. |
| P5 | **No Reduce-Motion seam — and the decorative motions are entangled with the clinical one.** Three motion paths exist: (a) clinical mask cross-fade `0.25`/tick (`:77`, **must stay**, A4); (b) grid-opacity `DoubleAnimation` 280 ms (`:191`, decorative); (c) first-show 380 ms `CubicEase` window fade-in (`:208–212`, decorative). There is **zero** `ReduceMotion` check anywhere in the file (grep-confirmed app-wide: "reduce-motion = 0"). | `:77, :189–192, :208–212` | A4 + design-system §3.4: Reduce-Motion must collapse (b) and (c) to instant **without ever touching (a)**. Today they are indistinguishable bare animations. |
| P6 | **First-show fade-in can produce a perceptible "pop" and is unconditional.** The 380 ms window-opacity fade from 0→1 (`:208`) eases the *whole window* in, but combined with the mask seeding (`_displayedSeeded`, `:364–368`) the first frame paints at full tier alpha before the window fade completes — and under Reduce-Motion this fade should be instant, yet a 0-duration window-opacity swap with a fully-seeded high-tier mask is exactly the hard cut A4 forbids. | `:208–212, :364–368` | Edge case: overlay shown for the first time *while already at tier 8*. Needs an explicit "fade the window in over `Dur.overlay`, but never let the mask itself snap" rule. |
| P7 | **Grid ceiling/feather constants are ad-hoc, not the radius/elevation token vocabulary.** Grid opacity ceiling `0.40 + 0.80×gv` (`:686`), feather ramp `[0.35, 0.95]` (`:645–646`), DPI step scaling off a `1920` baseline (`:233`). These are correct *engineering* but undocumented as a calm spec — line thickness `Math.Max(0.5, 1.2*scale)` (`:235`) is the only "weight" knob and it's buried. | `:233, :235, :645–646, :686` | C1 permits retuning grid line weight + feather radius + cell tone as proposals; today there's no named spec to propose *against*. |
| P8 | **No `AutomationProperties` and no accessibility intent — but this is correct and must be codified.** Zero automation properties (consistent with app-wide "AutomationProperties = 0" audit fact). For *this* surface that is actually **right** (a click-through decorative-safety layer must be invisible to the focus order and to screen-reader navigation), but it is currently *accidental*, not *declared*. | `OverlayWindow.xaml:1–19` | Must be made **intentional**: explicitly mark the window `IsHitTestVisible=False` (already true), `Focusable=False`, and `AutomationProperties.AccessibilityView=Raw` so assistive tech provably skips it — the *comfort-state announcement* belongs to the StatusChip on the dashboard/bar/card, never here. |

## 3. REDESIGN

This surface has **no component tree** — it is two render zones. The redesign tokenizes colors, codifies the safety constants, and adds the Reduce-Motion seam, **without changing the clinical alpha table values, the radial geometry, the world-lock, or any binding**.

### 3.1 Render-zone diagram (what the user actually sees)

```
┌──────────────────────────────────────────────────────────────────────┐
│  ███████████████████  PERIPHERY (mask peak)  ███████████████████████  │  ← Zone C: hold @ peakAlpha
│  ████████▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓████████████████  │     (radial 0.95→1.0)
│  ████▓▓▓▓░░░░░░ + + + ANCHOR GRID + + + ░░░░░░▓▓▓▓████████████████████ │  ← Zone B: smoothstep
│  ██▓▓░░ + + + +   world-locked, 40px,   + + + +  ░░▓▓██████████████  │     feather ramp
│  █▓░ + + + + +    grid hole tracks       + + + + + ░▓████████████████  │     (radial 0.35→0.95)
│  ▓░ + + + +       gaze center            + + + + +  ░▓██████████████  │
│  ░ + + + + + +  ┌───────────────────┐ + + + + + + + ░██████████████  │
│  ░ + + + + + +  │   CLEAR ZONE       │ + + + + + + + ░██████████████  │  ← Zone A: fully clear
│  ░ + + + + + +  │  (gaze center,     │ + + + + + + + ░██████████████  │     (radial 0.0→0.35,
│  ░ + + + + + +  │   no mask, faint   │ + + + + + + + ░██████████████  │      alpha 0; grid
│  ░ + + + + + +  │   grid only)       │ + + + + + + + ░██████████████  │      revealed via
│  ░ + + + + + +  └───────────────────┘ + + + + + + + ░██████████████  │      OpacityMask hole)
│  █▓░ + + + + +                          + + + + + ░▓████████████████  │
│  ██▓▓░░ + + + +                        + + + + ░░▓▓██████████████████  │
│  ████▓▓▓▓░░░░░░ + + + + + + + + + + ░░░░░░▓▓▓▓██████████████████████  │
│  ████████▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓████████████████████████  │
│  ███████████████████  PERIPHERY (mask peak)  ███████████████████████  │
└──────────────────────────────────────────────────────────────────────┘
   Legend:  ███ peak mask alpha (tier-driven)   ▓▓ smoothstep ramp
            ░░ near-clear feather    + + grid lines    [clear zone] = gaze center
   Window: WindowStyle=None, AllowsTransparency, Topmost, WindowState=Maximized,
           IsHitTestVisible=False, Background=Transparent  (UNCHANGED — OverlayWindow.xaml:4–12)
```

There is **no top-to-bottom layout** because there is no chrome. Hierarchy is purely **radial and depth-based**: clear center (most important — content lives here) → feather → peak periphery (suppression). This *is* the "gentle Depth via space/elevation not saturation" north-star, expressed as a luminance gradient.

### 3.2 Token mapping (the actual deliverable)

Because the mask is theme-invariant (it must dim regardless of Light/Dark/HC app theme), tokens here are delivered as **named, documented static brushes in a dedicated `Theme/OverlayMask.xaml` resource island** that the *code-behind reads once* — NOT as `{DynamicResource}` swapped per theme. Only the **grid line** and **cell-fill** tones may legitimately follow the active theme. This split is the load-bearing design decision.

| Element | Current literal | New token / named resource | Notes |
|---|---|---|---|
| **Mask tier table L1–L6** (gray ramp) | `#666 / #444 / #222` (`:56–61`) | `Overlay.MaskTier1..6` — keep values; **document** as engine-owned clinical floor. Alpha column is **A2 HARD-STOP, frozen**. | Color may shift toward neutral `#1A2230`-family slate *as an engineering proposal*; alpha untouched. |
| **Mask tier table L7–L10** (cool near-black) | `#04040A → #000004` (`:62–65`) | `Overlay.MaskTier7..10` — retune hue toward "Calm" cool-slate (blue-channel bias consistent with `Accent #3A6EA5` family, **not** saturated) **as a proposal to engineering** (A2). | Peak alpha **0.92 frozen** (A2). The cool tint is the *only* free lever and it already aligns with the direction. |
| **Grid line stroke** | `Argb(130,255,255,255)` pure white (`:171`) | `Overlay.GridLine` — desaturated paper-white in Light (`#FFFFFF` @ lower alpha ≈ 100/255), and in **Dark** a dimmed `Separator`-family `#30353E`-derived light line; **HC** → `SystemColors.WindowTextBrush`. | C1: line *weight* and *tone* retunable as proposal. Recommend dropping alpha 130→~100 for calmer read. |
| **Grid cell-fill** | `Argb(120,8,8,10)` (`:85`) | `Overlay.GridCellFill` — keep near-black neutral; document; allow ±tone toward cool-slate to match L7+ mask. | C1 proposal lever. |
| **Mask radial geometry** | `[0.0, 0.35]` clear / `[0.35,0.95]` ramp / `[0.95,1.0]` hold (`:108–111, :645–646`) | `Overlay.FeatherRampStart=0.35`, `Overlay.FeatherRampEnd=0.95` named consts | **MASK geometry frozen** — pinned by `ResolveFieldAlpha` (the FOV mask holds @ peak through `[0.95,1.0]` in the deep periphery — protection, byte-identical). **GRID window differs (Kirby band-pass ruling 2026-06-11):** `BuildLevelFeather` shares the `[0.35,0.95]` RISE for seam-alignment but then FALLS `[0.95,1.0]` smootherstep back to fully transparent (`SenseDefaults.GridWindowFadeOutStart`) so the grid LINES band-pass out at the corner while the mask dim stays. |
| **Mask cross-fade** | `MaskSmoothingFactor=0.25` (`:77`) | **`Overlay.MaskCrossFadeFactor`** + a companion `Dur.overlay` safety assertion (see §4) | A4 HARD-STOP. Rename + add a guard test so it can never become a snap. |
| **Radius / corner margin** | `BrushRadius=0.85`, `BrushRadiusCornerMargin=1.10` (`:20, :43`) | `Overlay.BrushRadius`, `Overlay.CornerMargin` named consts | Pinned by `OverlayHeadPoseTests` (radius 0.85 baseline, ×1.10 corner) — **values frozen**, rename-only. |

**Elevation / radius:** **N/A and must stay N/A.** This is a full-screen transparent window with no cards, no corners, no shadows. Introducing any `Elev.*` drop-shadow or `Radius.*` corner here would be a defect (it would either self-feed OFI as a visible edge or break click-through). Explicitly document: *the overlay has no elevation and no radius; its only depth cue is the radial luminance gradient.*

### 3.3 States (rest / hover / focus / empty / loading / error)

This surface's "states" are **engine-driven render states**, not interaction states (it cannot be hovered or focused — `IsHitTestVisible=False`). Mapping the standard state vocabulary:

| Standard state | This surface's meaning | Behavior |
|---|---|---|
| **Rest** | Tier 1 (`VimsLevels[0]`, alpha **0.00**, `:56`) OR always-on baseline tier (default 4 ≈ 43%, floored in `MainWindow.OnStateChanged:1217`) | Mask fully clear at true L1; grid faint or absent (tier-driven, `:683–688`). The calmest possible state — invisible. |
| **Active/Rising** | Tier 2–9, cross-fading | Per-tick smoothstep cross-fade via `MaskCrossFadeFactor`; grid opacity rises with tier (`:687`). Gaze-follow active if head-tracking on. |
| **Peak** | Tier 10 (alpha 0.92, `:65`) | Hold at clinical peak; cool-slate tint reads as "deliberate dimming." Never exceeds 0.92 (A2). |
| **"Hover"** → escape-hatch peek | User double-taps Ctrl (`MainWindow.xaml.cs:390`) | Mask dips to **0.30 opacity for 3 s** (`:495,507`), eased 250 ms down / 500 ms up — **force-disabled for vestibular users** (`:486`, A3). Never reaches 0%. |
| **"Focus"** | **Intentionally none** | Window is `Focusable=False`, `AccessibilityView=Raw`. It must never enter the focus order. |
| **"Empty"** | Overlay hidden (`Hide()`, `MainWindow.xaml.cs:1181`) | Window not visible; on re-show, mask **re-seeds** (`_displayedSeeded=false` path, `:364`) so it never eases up from black. |
| **"Loading"** | First show | 380 ms `Dur.overlay` window fade-in (`:208`), seeded to current tier so no pop (see P6 fix in §4). |
| **"Error"** | Head-pose sidecar dead / stale | Mask snaps gaze center to (0.5, 0.5) — the safe default (`ApplyHeadPoseToBrush` active=false branch, `:573,593`). Grid hole re-centers with it. **No error surface is drawn on the overlay** — error UX belongs to the dashboard/card, never the protective field. |

## 4. MOTION & REDUCE-MOTION

Three motion channels, **two collapsible, one inviolable** — this is the central safety distinction of the surface (A4 + design-system §3.4):

| Channel | Current | Reduce-Motion behavior | Token |
|---|---|---|---|
| **(CLINICAL) Mask luminance cross-fade** | `MaskSmoothingFactor=0.25`, ~250 ms convergence (`:77, :371–374`) | **NEVER collapses. Always eased ≥ `Dur.overlay` (320 ms).** A hard luminance cut is itself a VIMS trigger (A4 HARD-STOP). | **`Dur.overlay` = 320 ms** safety token. Add CI assertion: the effective convergence time implied by `MaskCrossFadeFactor` at 15 Hz must be ≥ 300 ms, so no edit can turn it into a snap. |
| **(DECORATIVE) Grid-opacity animation** | `DoubleAnimation` 280 ms (`:189–192, :693`) | **Collapses to instant** (Duration→0) — grid opacity becomes a direct property set on the tick. | Bind duration to `Dur.ui` (`{DynamicResource}` driven by app `ReduceMotion`); read once into `_gridOpacityAnim.Duration`. |
| **(DECORATIVE) First-show window fade-in** | 380 ms `CubicEase` 0→1 on `OpacityProperty` (`:208–212`) | **Collapses to instant window appearance — BUT** the *mask itself* must still be seeded to the current tier (not 0), so even an instant window-show presents an already-correct mask, never a black-then-clear pop (P6 fix). | Gate the fade-in `Duration` on `ReduceMotion`; the `_displayedSeeded` seed (`:364–368`) is **unconditional** and runs regardless. |

**Reduce-Motion plumbing:** the file reads a single app-level `bool ReduceMotion` (`manualToggle OR NOT SystemParameters.ClientAreaAnimation`, design-system §3.4 clause 1). Wire it as a constructor/property input or a `{DynamicResource Dur.ui}` lookup — **do not** add a per-tick `SystemParameters` read on the render path (perf: the 165 Hz `OnCompositionRendering` path must stay allocation-free, `MainWindow.xaml.cs:446`). Subscribe once to `SystemParameters.StaticPropertyChanged`.

**Absolute motion prohibitions on this surface (A4):** no pulsing, breathing, color-cycling, or attention-grabbing motion on either mask or grid — ever, under any theme or state. The grid never scrolls/parallaxes (C1 world-lock). The escape-hatch is the only user-triggered transition and it is bounded (3 s, ≥0.30 floor, vestibular-disabled).

## 5. ACCESSIBILITY

This surface's accessibility goal is **provable invisibility to assistive tech**, because it is a decorative-safety layer; the *meaningful* comfort-state announcement lives on the StatusChip (dashboard / bar / card), never here.

| Concern | Spec |
|---|---|
| **AutomationProperties** | Set on the `Window` root: `AutomationProperties.Name="SenseEase 視覺保護層 / Visual protection field"` for the rare tool that does surface it, **and** `AutomationProperties.AccessibilityView="Raw"` + `Focusable="False"` so the focus order and screen-reader tree **skip it entirely**. The grid `Canvas`, `FovMask` `Rectangle`, and all `Line` children inherit Raw — none are individually announced. |
| **Focus order** | **Not in tab order.** `IsHitTestVisible="False"` (already, `:12`) + `Focusable="False"` + `ShowInTaskbar="False"` (already, `:9`). The overlay can never steal focus from the app underneath (it would break click-through, C2). |
| **Contrast** | The mask **deliberately reduces** peripheral contrast (that is its job) — WCAG text-contrast rules **do not apply** because the overlay renders **no text or essential UI**. The clear zone (Zone A, alpha 0) guarantees the underlying app's own AA/AAA text is untouched. **Codify:** the clear-zone (radial 0.0→0.35) must remain at alpha 0 so it never degrades the underlying content's contrast (this is the accessibility guarantee of the surface). |
| **Colorblind-safe status** | The mask conveys state by **luminance/depth**, not hue — inherently colorblind-safe. The cool-slate tint (L7+) is a *perceptual* cue, never the *only* channel (depth carries the meaning). The user-facing **comfort-state label/icon** (Steady/Rising/High StatusChip, triple-coded) lives on the bar/card per D2 — this surface intentionally carries **no color-coded status**, so there is nothing here to fail colorblindness. |
| **Min sizes** | N/A for controls (none). Grid spacing `40px` DPI-scaled (`:234`) and line weight `≥0.5px` (`:235`) must stay legible-but-faint at 125%/150% OS scaling — verify the `physicalWidth/1920` scale (`:233`) holds on a 4K/150% display (grid must not become a dense moiré). |
| **Reduce-Motion as accessibility** | The Reduce-Motion seam (§4) is itself an accessibility feature (vestibular/photophobic users). The **clinical** cross-fade staying eased under Reduce-Motion is the accessibility-critical exception (A4). |

## 6. MIGRATION NOTES — land without breaking prior works

**Binding/DataContext:** there is **no `DataContext` and no XAML binding** on this surface — it is driven imperatively through `IOverlayRenderer.ApplyState(SenseState)` (`IOverlayRenderer.cs:17`) called from `MainWindow.OnStateChanged` (`:1218`) and `OnOverlayVisibilityChanged` (`:1177`), plus the 165 Hz `UpdateHeadGazeCenter` path (`MainWindow.xaml.cs:524`). **None of these signatures may change.** Keep `ApplyState`, `UpdateHeadGazeCenter(double,double,bool)`, `TriggerEscapeHatch()`, `HasVestibularDiagnosis` property, and `IsVisible/Show/Hide` byte-identical.

**Frozen public/internal surface the tests pin** (do not rename, do not change behavior):

- `FovBrushForTests` (`internal RadialGradientBrush?`, `:534`) — read by `OverlayHeadPoseTests` 5×. Keep.
- `UpdateHeadGazeCenter(headGazeX, headGazeY, active)` — pinned by `OverlayWindow_UpdateHeadGazeCenter_AppliesSameBrushMutation` (`OverlayHeadPoseTests.cs:453`). Keep signature + radius math.
- Brush `Center`/`GradientOrigin`/`RadiusX`/`RadiusY` semantics — pinned by 5 tests asserting exact values: center-follows-gaze `(0.6,0.4)`; Balanced clamp `[0.10,0.90]`; FullFollow `[0,1]`; **radius `0.85` baseline at center**; corner `√2×1.10≈1.556`; edge `√1.25×1.10≈1.230` (`OverlayHeadPoseTests.cs:219–486`). **These values are LOCKED** — the "Calm" rebrand touches *color and motion*, never geometry.
- `VimsLevels` **alpha column** — peak `0.92`, resting `0.00` are A2 clinical floors; while no test currently pins the exact alpha bytes, changing them is an A2 HARD-STOP behavior change requiring an explicit PR flag (C5).

**Tests that touch this view (flag in PR):**
1. `tests/SenseEase.Tests/OverlayHeadPoseTests.cs` — 6 tests, all on the geometry/gaze/radius path. A pure color+motion rebrand should leave **all green untouched**. If any change makes one go red, the change has exceeded "visual refactor."
2. `tests/SenseEase.Tests/HeadTrackingScaffoldingTests.cs` — flag-off invariant (no OverlayWindow construction). Untouched by this work.
3. `tests/SenseEase.Tests/MainWindowSmokeTests.cs` — constructs bare `new Application()` and **never instantiates `SenseEase.App.App`** (design-system §7.2). **Consequence for this surface:** if you add a `<Window.Resources>` merge of a theme dictionary to `OverlayWindow.xaml`, it must be **view-local** (the pack-URI merge pattern from §7.2), never moved to `App.Resources`, or the smoke harness throws at construction → red CI → a "broke prior works" violation. Safer still: keep the mask/grid color tables as **named static brushes in code-behind read from a frozen `Theme/OverlayMask.xaml` island merged view-locally**, so there is zero `{StaticResource}` lookup risk in the test path.

**Landing sequence (incremental, each step green):**
1. **Rename-only refactor** — extract magic constants to named consts (`MaskCrossFadeFactor`, `FeatherRampStart/End`, `BrushRadius`, `CornerMargin`, `Overlay.GridLine/GridCellFill` named brushes). No value change → all tests green, no visual diff. Land first.
2. **Add the `Dur.overlay` safety assertion** (a new unit test asserting `MaskCrossFadeFactor` implies ≥300 ms convergence at 15 Hz) — codifies A4 so future edits can't snap. New test, no behavior change.
3. **Reduce-Motion seam** — gate only the **grid-opacity duration** (`:191`) and **first-show window fade** (`:208`) on `ReduceMotion`; leave the mask cross-fade and the unconditional seed untouched. Add a test that `ReduceMotion=true` ⇒ grid/window durations are 0 **and** mask cross-fade factor is unchanged.
4. **Grid + cell-fill tone tokenization** (P2/P3) — view-local theme merge; verify smoke test still constructs.
5. **Mask L7–L10 cool-slate hue retune** — deliver as an **engineering proposal with before/after evidence** (A2 forbids a direct designer XAML edit to mask depth/hue; alpha column frozen). Requires the user's Layer-3 manual visual review before merge (C6, `QA-AND-TIERING.md §1.3`) — **tests green ≠ done** for this clinical surface.

**Do NOT:** change the radial geometry, the world-lock (C1 — grid lines stay world-locked; since the 2026-06-11 canvas-model unification the visibility window is the `_gridWindowBmp` ImageBrush written by the field's own pass, the gaze hole being the only sanctioned motion), the click-through (`:12`, C2), the escape-hatch floor (0.30 / 3 s / vestibular-disable, A3), any `VimsLevels` alpha, or the `SetMonitorExcludeRect` registration contract for this on-monitor window (C2 — overlay must remain registered so it doesn't self-feed OFI). Do NOT add any control, text, badge, or focusable element to the overlay.

---

**Key file references for this spec:** `c:\Users\kirby\Desktop\SenseEase APP\src\SenseEase.App\Views\OverlayWindow.xaml` (render zones, lines 12–18), `c:\Users\kirby\Desktop\SenseEase APP\src\SenseEase.App\Views\OverlayWindow.xaml.cs` (mask table :54–66, smoothing :77, grid colors :85/:171, escape-hatch :481–528, gaze/radius :553–618), `c:\Users\kirby\Desktop\SenseEase APP\src\SenseEase.Core\Abstractions\IOverlayRenderer.cs` (contract), `c:\Users\kirby\Desktop\SenseEase APP\src\SenseEase.Core\Models\SenseState.cs` (VimsTier :37, HeadGaze :81–91), `c:\Users\kirby\Desktop\SenseEase APP\src\SenseEase.App\MainWindow.xaml.cs` (integration :1161–1218, 165 Hz loop :499–527, escape-hatch hotkey :390), `c:\Users\kirby\Desktop\SenseEase APP\tests\SenseEase.Tests\OverlayHeadPoseTests.cs` (6 pinning tests — geometry LOCKED), `c:\Users\kirby\Desktop\SenseEase APP\tests\SenseEase.Tests\MainWindowSmokeTests.cs` (bare-Application smoke-test trap).

---

# First-run onboarding (replace the dark welcome banner) + demo video framing

I now have all the ground-truth evidence I need: the first-run banner (MainWindow.xaml:172-205), the DemoVideoWindow (XAML + code-behind), the bindings/commands (`ShowFirstRunBanner`, `StartSetupDemoCommand`, `SkipSetupCommand`, `DemoFinished` event, `SetMonitorExcludeRect` constraint), and the smoke test that constructs `MainWindow` via a bare `new Application()`. Let me also confirm the `SetMonitorExcludeRect`/window-on-monitor concern relative to the demo window and check the DemoFinished/ESC behavior is the only interrupt.

I have enough to write a decision-grade spec.

# First-Run Onboarding + Demo Video Framing — "Calm" Redesign Spec

> Surface scope: (a) the first-run welcome banner currently embedded in the Dashboard `StackPanel` (`MainWindow.xaml:172–205`), and (b) the demo-video presentation chrome (`DemoVideoWindow.xaml` + its in-video HUD). These are the user's very first contact with SenseEase and the only screen that sets the trust contract before the webcam/calibration asks begin. Evidence cited against current `main` HEAD (Pre-Alpha v0.11.1).

---

## 1. Purpose

**First-run banner — one job:** convert a brand-new, possibly already-overstimulated user into a *confident first action* (run the 60 s demo) **without** making them feel they've been dropped into a medical instrument. The single question the user is asking is: *"What is this, is it safe, and what do I do first?"* The banner must answer all three in one calm glance, in plain language, and make "Run the demo" obviously the recommended path while keeping "not now" genuinely available and non-punishing.

**Demo video framing — one job:** during the 60 s vection clip, reassure the user that *what they are about to see is intentionally uncomfortable, it is being watched on their behalf, and they can stop instantly at any moment.* The question here is: *"This is making me feel weird — is that supposed to happen, and how do I get out?"* The framing's whole purpose is to keep the user in the loop long enough to calibrate while guaranteeing a one-key, zero-friction exit (the wellbeing interrupt preserved at `DemoVideoWindow.xaml.cs:58` / `:48`).

Both surfaces are governed by the order of authority **A (clinical) > B (privacy) > C (engineering) > D (product) > brand**. Nothing below trades against a HARD-STOP.

---

## 2. Current Problems

### 2.1 First-run banner (`MainWindow.xaml:172–205`)

| # | Problem | Evidence |
|---|---|---|
| P1 | **Dark card on a light dashboard — the documented two-palette clash.** Banner is `Background="#0E1320"` with a `#3D5AFE` border, floating on the `#E2ECFF` canvas. It reads as an alien modal, the opposite of "calm." | `MainWindow.xaml:178` vs window `Background="#E2ECFF"` `:9` |
| P2 | **Engineering/marketing tone in the eyebrow + a bare color-only status dot.** The `●` glyph at `:186` is a decorative dot with no semantic meaning, colored `#FF9B6B` — an alarm-orange that violates the "no saturated orange, muted `ComfortRising` only" rule and is color-only (AutomationProperties = 0). | `:186–187` |
| P3 | **Contrast failures on essential text.** Eyebrow `#FF9B6B` on `#0E1320` and the Skip button text `#9AB6E0` on transparent/dark are both below AA for their size; `#9AB6E0` is on the retired-failure list. | `:187`, `:194` |
| P4 | **Bilingual strings crammed inline as one run** ("歡迎使用 SenseEase / Welcome to SenseEase", and a 40-word run mixing 中文 and English in a single `TextBlock`), violating the "Clarity: one idea per screen, plain language" star and hard to localize. | `:186`, `:188–189` |
| P5 | **Ad-hoc everything — no tokens.** `CornerRadius="10"`, `Padding="20,14"`, three raw hex fills, bespoke per-button `Background`/`BorderBrush`/`Padding="14,6"`/`FontSize="12"` on both buttons — none routed through a style. The two buttons are hand-built, not `PrimaryButton`/`QuietButton`. | `:177`, `:192–202` |
| P6 | **Sub-12px text.** Body copy is `FontSize="12"` (at the 12 floor but the eyebrow context is cramped); the whole banner sits below the 14px body floor mandated by the type scale. | `:187–189` |
| P7 | **No focus states, no keyboard affordance, no AutomationProperties.** The buttons are raw `<Button>` with inline styling; nothing names the surface for a screen reader. | `:192–202` |
| P8 | **Skip is framed as a bordered button equal in weight to a real action** (`BorderBrush="#2C3A55"`), giving a low-emphasis dismissal too much visual gravity and creating a false either/or symmetry. | `:192–197` |

### 2.2 Demo video framing (`DemoVideoWindow.xaml`)

| # | Problem | Evidence |
|---|---|---|
| P9 | **HUD is top-right, the corner most likely to be lost in peripheral motion** for exactly the population being stressed; it competes with the vection clip instead of being a calm anchor. | `:21–23` |
| P10 | **Alarm-coded eyebrow + bare dot.** `● SENSEEASE DEMO` in `#FFB347` (amber-orange, color-only dot, "alert" voice) on a population told this app *replaces* alarm aesthetics. | `:25–26` |
| P11 | **10–11px text on a moving background** — `FontSize="11"` body and `FontSize="10"` for the all-important "Press ESC to stop" line, the single most safety-relevant string on screen, rendered smallest and in low-contrast `#9AB6E0`. | `:27–32` |
| P12 | **The exit instruction is buried last and de-emphasized.** The wellbeing escape ("Press ESC or close window to stop") is the *least* prominent element, inverting the clinical priority that the stop affordance must be unmissable. | `:31–32` |
| P13 | **No pre-roll consent/readiness beat.** The clip auto-plays on `Loaded` (`xaml.cs:45`) with no calm "this will move, here's your exit, ready?" moment — a hard start straight into vection for a sensitive user. | `xaml.cs:44–45` |
| P14 | **Error/fallback text is alarm red `#FF6B6B`** centered — the one moment a user hits a failure, they're shown the loudest possible color. | `:37–39` |
| P15 | **All literals are raw hex / ad-hoc radius** (`#90000000`, `CornerRadius="6"`, `Padding="12,8"`), none tokenized; the window can't follow Light/Dark/HC. | `:22–23` |

---

## 3. Redesign

### 3.1 First-run welcome — from dark banner to a calm light **Card**

**Structural decision:** keep it an *inline dismissible Card at the top of the Dashboard `StackPanel`* (do **not** promote to a blocking modal — a modal on first launch is itself an overstimulation, and the existing `Visibility` binding + `StackPanel` flow is the lowest-risk migration). Re-skin it to a first-class light `Card` that belongs to the dashboard rather than fighting it.

**Top-to-bottom layout, tokens, hierarchy:**

| Element | Component / Token | Spec |
|---|---|---|
| Container | `Card` (§4) | `Surface #FFFFFF` bg, `Radius.md` (12), `Pad.Card` (24), `Elev.2` neutral shadow. **No colored border** (retires the `#3D5AFE` border, P1/P5). 1px `Separator` border only in High-Contrast. |
| Leading visual | `Icon.Lightbulb` (Fluent Regular, 24px) in an `AccentWeak #E9EFF5` rounded tile `Radius.sm` | Replaces the alarm `●` dot (P2). Decorative-only → `AutomationProperties.IsOffscreen` / no name. |
| Eyebrow | `Type.CaptionStrong` (12/16 SemiBold), `Accent #3A6EA5` | "GETTING STARTED" / "開始使用" — calm, low-chroma, AA on `Surface` (4.9:1). Replaces alarm `#FF9B6B` eyebrow. |
| Title | `Type.Heading` (16/22 SemiBold), `TextPrimary #1A2230` | "Let's set up your comfort" / "先設定你的視覺舒適". 15.3:1 AAA. |
| Body | `Type.Body` (14/20 Regular), `TextSecondary #525B6B` | Plain language, **one idea**: "A short 60-second clip plays so SenseEase can learn how on-screen motion affects you, then suggests gentle defaults. Nothing is recorded — it stays on your device." 7.4:1 AAA. Body raised to the 14 floor (fixes P6). |
| Reassurance caption | `Type.Caption` (12/16), `TextTertiary #6E7787` | "You can stop the clip any time by pressing Esc." — front-loads the exit promise. 4.9:1 AA. |
| Action row | right-aligned `WrapPanel` | Primary + Quiet, see below. |

**Buttons (replace the two hand-built buttons, P5/P8):**
- **Primary action:** `PrimaryButton` (`Btn.Primary`) — `Accent #3A6EA5` fill, `TextOnAccent #FFFFFF`, `Radius.sm`, `Pad.Button` (14,8), `Type.BodyStrong`, `Elev.1`. Label: "Start 60-second setup" / "開始 60 秒設定". Binds `StartSetupDemoCommand`.
- **Dismiss:** `QuietButton` (`Btn.Quiet`) — transparent bg, `TextSecondary` text, **no border** (removes the false-symmetry border P8). Label: "Not now" / "稍後再說". Binds `SkipSetupCommand`. Quiet, not equal-weight.

**Bilingual handling (P4):** stack 中文 and English as **two separate `TextBlock`s** (zh-Hant primary line `Type.Body`, en secondary line `Type.Caption` `TextSecondary`) per element, never one mixed run — mirrors the pattern already used well at `:841–846`. This also makes a future resource-dictionary localization trivial.

**States:**
- *rest* — as above.
- *hover* — Primary → `AccentHover #33628F`; Quiet → bg `SurfaceMuted #F1F3F6`.
- *press* — Primary → `AccentPressed #2C5479`; Quiet → bg `SurfaceSunken #ECEEF2`.
- *focus* — 2px `FocusRing #3A6EA5` + 1px white inset on whichever button has keyboard focus.
- *disabled* — Primary disables while `IsRunning` (the existing `_ => !IsRunning` guard at `MainViewModel.cs:585`): `SurfaceMuted` bg + `TextTertiary` text, no shadow, `IsHitTestVisible=false`.
- *empty/loading/error* — N/A for the banner itself; it has no async content. (Demo-launch failure is handled in the demo window, §3.2.)

**ASCII wireframe — first-run Card:**
```
┌─────────────────────────────────────────────────────────────────────────┐
│  Card · Surface #FFFFFF · Radius.md 12 · Pad.Card 24 · Elev.2            │
│                                                                          │
│  ┌────┐   GETTING STARTED            ← Type.CaptionStrong / Accent       │
│  │ 💡 │   開始使用                                                        │
│  └────┘   Let's set up your comfort  ← Type.Heading / TextPrimary        │
│  Accent   先設定你的視覺舒適                                              │
│  Weak                                                                     │
│  tile     A short 60-second clip plays so SenseEase can learn how         │
│           on-screen motion affects you, then suggests gentle defaults.    │
│           Nothing is recorded — it stays on your device.                  │
│           ← Type.Body / TextSecondary (14px, AAA)                         │
│                                                                          │
│           You can stop the clip any time by pressing Esc.                 │
│           ← Type.Caption / TextTertiary                                    │
│                                                                          │
│                                   [ Not now ]   [ Start 60-second setup ] │
│                                    QuietButton      PrimaryButton (Accent) │
└─────────────────────────────────────────────────────────────────────────┘
        ↑ sits at top of Dashboard StackPanel, gap Gap.Stack (16) below
```

### 3.2 Demo video framing — calm anchor HUD + optional ready beat

**Structural decision:** keep `DemoVideoWindow` a maximized borderless window (engineering lock — it must be a real on-screen surface so WGC optical flow drives the pipeline; per **C2**, if this window's rect ever sits on the captured monitor it must register via `WgcSensingProvider.SetMonitorExcludeRect` on first render — flag to engineering, see Migration). Redesign the **HUD overlay only**; the `MediaElement` and its Play/Stop/ESC plumbing (`xaml.cs:44–58`) are untouched.

**Two HUD zones:**

**(A) Persistent comfort anchor — bottom-center, not top-right (fixes P9/P12):**
A single low-chroma pill, `SurfaceMuted` at ~88% opacity over black (`OverlayScrim`-style backing), `Radius.pill`, `Pad` 16,10, `Elev.3`. Contents in priority order:
- `Icon.Eye` (Fluent, 16px) + `Type.CaptionStrong` "SenseEase is watching for you" / "SenseEase 正在為你監測" — `TextOnComfort`/light text, ≥4.5:1 on the scrim.
- **Stop instruction promoted to the most prominent line** (inverts P12): `Type.BodyStrong` (14px), high-contrast — "Press **Esc** to stop" / "按 Esc 立即停止", with a small `Icon.Dismiss` chip. This is the one line that must never be the smallest thing on screen.

Bottom-center is chosen because (1) it's out of the high-vection central field, (2) it is the calmest resting position for eyes already strained, and (3) it does not compete with the top-right corner where Windows' own camera/recording indicators may appear.

**(B) Optional 3-second "ready" pre-roll (addresses P13) — proposal, gated:**
Before `Player.Play()`, a centered calm card: `Surface`, `Radius.lg` (16), `Elev.3` — "This clip moves on purpose. Esc stops it instantly. Ready?" with a single `PrimaryButton` "Start". This converts the hard auto-start into a consented start. **Flagged as a behavior change** (it adds a click before playback) — ship behind a small refactor of `xaml.cs:45` so `Player.Play()` is invoked from the Start button rather than `Loaded`. If engineering prefers zero behavior change for Alpha-1, ship **A only** and keep auto-play; the anchor HUD alone resolves P9–P12.

**Tier-coupled tint (optional, proposal):** the HUD backing may *gently* deepen as the live `VimsTier` rises (read-only, `Comfort*Weak` tokens) so the user gets calm confirmation the system is responding — but **must never** add pulsing/breathing/color-cycling motion (**A4** forbids attention-grabbing motion), and must not touch the mask path.

**Error/fallback state (fixes P14):** the centered `ErrorText` moves from alarm `#FF6B6B` to `ComfortHigh #B65C4E` (muted clay) with an `Icon.Warning` glyph and `Type.Body` (14px), inside a `Surface` `Radius.md` card — a calm, legible failure, consistent with **A5**'s "muted clay, never alarm red" rule. Auto-close-after-2s behavior (`xaml.cs:34–39`) is preserved.

**ASCII wireframe — demo HUD:**
```
╔═══════════════════════════════════════════════════════════════════════════╗
║                         (full-screen vection video)                        ║
║                                                                            ║
║                    ┌──────────────────────────────────┐                    ║
║   optional ready → │  This clip moves on purpose.      │  ← Surface card    ║
║   beat (proposal)  │  Esc stops it instantly. Ready?   │     Radius.lg 16   ║
║                    │            [ Start ]              │     Elev.3         ║
║                    └──────────────────────────────────┘                    ║
║                                                                            ║
║                                                                            ║
║            ┌──────────────────────────────────────────────────┐            ║
║            │ 👁  SenseEase is watching for you                  │            ║
║  bottom →  │     SenseEase 正在為你監測                          │  pill      ║
║  center    │ ✕  Press Esc to stop  · 按 Esc 立即停止  (14px bold)│  Radius    ║
║  anchor    └──────────────────────────────────────────────────┘  .pill     ║
╚═══════════════════════════════════════════════════════════════════════════╝
```

---

## 4. Motion & Reduce-Motion

**First-run Card:**
- Appear: fade + 4px rise over `Dur.base` (200ms) `Ease.calmOut` — bound to the `Dur.ui` dynamic resource.
- Dismiss (Skip/Start): collapse-fade over `Dur.fast` (120ms).
- Button hover/press: color cross-fade over `Dur.fast`.
- **Reduce-Motion (`ReduceMotion` = manualToggle OR NOT `SystemParameters.ClientAreaAnimation`):** appearance/dismiss become **instant swaps** (duration → 0), no rise, no fade. Button state changes are instant color swaps. No content reflow or jump.

**Demo HUD:**
- Anchor pill fade-in over `Dur.base` after first frame.
- Optional ready-card fade over `Dur.overlay` (320ms).
- The optional tier-coupled tint, if shipped, eases over `Dur.overlay` and **is exempt from Reduce-Motion collapse** in the same spirit as the clinical mask cross-fade (**A4**: luminance changes must never snap). Practically, for Alpha-1 the safe choice is a **static** HUD tint to avoid any motion on an already-moving surface; ship motion here only if the ready-beat is also shipped.
- **The demo video clip itself is content, not chrome** — Reduce-Motion does **not** alter or freeze the vection clip (that would defeat calibration); it only quiets the HUD's own animations.

This surface introduces **no** new motion on the clinical mask path; all motion here is chrome governed by `Dur.ui` / Reduce-Motion, satisfying **A4**.

---

## 5. Accessibility

**AutomationProperties:**
- First-run Card root: `AutomationProperties.Name="First-run setup. SenseEase can run a 60-second demo to suggest comfort defaults."` (LiveSetting `Polite` if it appears post-load).
- Primary button: `Name="Start 60-second setup"`; Quiet button: `Name="Not now, skip setup"`.
- Decorative lightbulb tile: marked decorative (`AutomationProperties.IsOffscreen` or empty name) so SR doesn't announce it.
- Demo HUD anchor: `Name="SenseEase is monitoring this demo. Press Escape to stop at any time."` — the *stop method* is in the accessible name, not color/glyph alone.
- Demo error card: `Name` = the full failure phrase + recovery ("Demo could not start; returning to the dashboard.").

**Focus order (banner):** Title region (read-only) → **Primary "Start"** (default focus / `IsDefault` candidate) → **Quiet "Not now"**. Esc on the banner = Skip (maps to `SkipSetupCommand`). Tab cycles only the two buttons. In the demo window, focus stays on the video host; **Esc is a window-level `PreviewKeyDown` (already at `xaml.cs:58`)** so the stop key works regardless of focus — keep this; it is a wellbeing guarantee.

**Contrast confirmations (all from the locked Light table):**
- Eyebrow `Accent #3A6EA5` on `Surface` = 4.9:1 (AA).
- Title `TextPrimary #1A2230` on `Surface` = 15.3:1 (AAA).
- Body `TextSecondary #525B6B` on `Surface` = 7.4:1 (AAA).
- Caption `TextTertiary #6E7787` on `Surface` = 4.9:1 (AA) — used only for the non-essential reassurance line; the *essential* Esc promise also appears in the demo HUD at high contrast.
- Demo HUD: light text on dark scrim verified ≥4.5:1; stop line is `Type.BodyStrong` 14px (large-ish, bold) for headroom on a moving background.

**Colorblind-safe status:** every status cue is **triple-coded** — the old color-only `●`/`#FF9B6B`/`#FFB347` dots (`MainWindow.xaml:186`, `DemoVideoWindow.xaml:25`) are replaced by **icon + label + (muted) color**. No meaning is carried by hue alone, satisfying **D2** and the WCAG 2.2 colorblind requirement.

**Minimum sizes / scaling:** body 14px floor, caption 12px floor (no 10/11px anywhere — fixes P6/P11). Buttons `MinHeight 32` with `Pad.Button` so they grow with the OS "Make text bigger" slider; no fixed `Height` on any text container (use `MinHeight`+`Auto`), verified to reflow at 125%/150% Windows scale. Esc/stop hit targets ≥ 44px effective.

---

## 6. Migration Notes (cannot-break-prior-works)

**Bindings & DataContext — preserve verbatim:**
- First-run Card keeps `Visibility="{Binding ShowFirstRunBanner, Converter={StaticResource BoolToVis}}"`. `ShowFirstRunBanner` (`MainViewModel.cs:942–947`, seeded from `AppConfig.IsFirstRun`) and its reset to `false` in `SkipSetup`/post-demo (`:589`, `:1238`) are untouched.
- Primary button keeps `Command="{Binding StartSetupDemoCommand}"` (`:585`, alias of `StartDemo` per `:956`); Quiet button keeps `Command="{Binding SkipSetupCommand}"` (`:586`). The `_ => !IsRunning` CanExecute guard drives the disabled state for free — do **not** add a parallel enabled flag.
- Do **not** rename any of: `ShowFirstRunBanner`, `StartSetupDemoCommand`, `SkipSetupCommand`. The same commands are also bound on the dashboard "Calibration & Demo" button (`:309–310`) and the Stage-5a invitation banner reuses the identical dark-card pattern (`:831–863`) — re-skin that banner with the **same** Card tokens in the same PR so the two onboarding surfaces stop clashing, but keep its bindings (`ShowCalibrationInvitationBanner`, `StartInvitedCalibrationCommand`, `SkipCalibrationInvitationCommand`) intact.

**The `BoolToVis` / App.Resources trap (critical):** `MainWindowSmokeTests.cs:29` constructs `MainWindow` via `Application.Current ?? new Application()` and **never boots `SenseEase.App.App`**, so `App.xaml`'s `Application.Resources` are not loaded — this is exactly why `BoolToVis` lives in `MainWindow.Resources` (`:14–19`). When introducing the design-system tokens, **merge `SenseEaseTheme.xaml` into `MainWindow.Resources` (view-local), not `App.Resources`** (per the locked WPF delivery pattern), or every `{StaticResource Accent}` throws at construction → red smoke test → a prior-works violation. Keep `BoolToVis` where it is.

**DemoVideoWindow:**
- HUD is pure presentation; the constructor signature `DemoVideoWindow(string videoPath)` (`xaml.cs:25`), the `DemoFinished` event (`:21`), `Player` plumbing (`:44–58`), and `OnClosed` teardown (`:61–74`) are untouched. The window is created/owned/shown at `MainViewModel.cs:1013–1016`.
- **If you ship the optional ready-beat:** moving `Player.Play()` from `Loaded` (`xaml.cs:45`) to a Start button **is a behavior change** (adds a pre-roll click) — call it out explicitly in the PR per **C5**, and confirm it doesn't desync the `DemoFinished`→`OnDemoVideoFinished` restore path (`MainViewModel.cs:1014`, `:1069`) or the 60s-assumption in `CalibrationProcessor` (Q1-vs-sensor alignment assumes a 60s demo). If any test pins demo timing, default to **HUD-only** (no ready-beat) for Alpha-1.
- **Engineering flag (C2):** confirm whether the maximized `DemoVideoWindow` ever overlaps the WGC-captured monitor rect; if so, it must register/clear via `WgcSensingProvider.SetMonitorExcludeRect` on render/close to avoid OFI self-feedback. The HUD redesign does not change this contract but the spec must hand it to engineering.

**Tests that touch this view:**
- `tests/SenseEase.Tests/MainWindowSmokeTests.cs` — constructs `MainWindow` and fires `OnStateChanged` 3×; it will catch any `StaticResource`-not-found or binding error introduced by the Card refactor. **This is the gate.** Run it green before/after.
- No test directly instantiates `DemoVideoWindow` (grep: only `NotificationCardWindowTests` borrows the smoke pattern). `CalibrationEndToEndSmoke.cs:300` and `CalibrationChatbotServiceTests.cs:480` exercise the *first-run calibration pipeline* (not the banner XAML) — they must stay green, which they will as long as `AppConfig.IsFirstRun`/`ShowFirstRunBanner` semantics and command names are unchanged.

**Net:** this is a pure visual refactor — zero VM/command/event renames, zero behavior change (if shipped HUD-only), tokens merged view-local, all status triple-coded. It clears A–D and the smoke gate.

---

**Files read for this spec:** `src/SenseEase.App/MainWindow.xaml` (first-run banner `:172–205`, palette/styles `:9–109`, sibling invitation banner `:831–863`), `src/SenseEase.App/Views/DemoVideoWindow.xaml` (HUD `:20–42`), `src/SenseEase.App/Views/DemoVideoWindow.xaml.cs` (play/ESC/close plumbing), `src/SenseEase.App/ViewModels/MainViewModel.cs` (`ShowFirstRunBanner` `:942`, commands `:585–586`, `StartSetupDemo` alias `:956`, demo-window lifecycle `:1008–1019`), `tests/SenseEase.Tests/MainWindowSmokeTests.cs` (the App.Resources construction trap).
