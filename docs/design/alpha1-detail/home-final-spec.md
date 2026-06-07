# Final Spec — Quiet Guardian Home

> Generated 2026-06-05 by the senseease-rebrand-refine workflow (8 agents, adversarially reviewed). Draft for review — no code changed, nothing committed.

I have all the decision-grade evidence confirmed:
- L1 alpha = 0.00 at `OverlayWindow.xaml.cs:56`, smoothing 0.25 at `:77`.
- Persistent overlay default false (`MainViewModel.cs:794`), baseline tier default 4 (`:807`) — LOCKED decision #2 wants default ON with baseline tier 1 = 0% alpha.
- `ComfortHeadline`/`ComfortSubline`/`ComfortChipState` do not exist on the VM (so converters are the visual-only path).
- Liveness contract = always-on bar + tray tint + self-check affordance (IMMERSIVE_CONCEPTS.md open-question #1, recommend tested liveness contract); bar/tray never animate (TECH_CAPABILITY_MAP `:48,50`).
- All exact bindings, copy, and tokens verified.

Now I'll write the final decision-grade spec.

---

# Home / Today — FINAL Locked Spec (Quiet Guardian, Alpha-1)

*Surface: `MainWindow.xaml` Tab 1 (`Header="Dashboard"`, lines 168–480). This section supersedes the `VISUAL_SPEC.md` "Home / Today" draft (that file's §1–§6) and is the lockable build target. Authority order: Clinical (A) > Privacy (B) > Engineering (C) > Product (D) > brand. Every binding/method/token below is verified against the current tree; file:line citations are load-bearing.*

## 0. The job this surface does (one sentence)

When the Quiet Guardian's user opens the window, Home answers **"Am I protected right now, and is SenseEase actually working?"** — with a plain-language comfort state (not telemetry), the calm-field control presented as the default, an at-a-glance liveness read, and all five engineering scalars demoted behind one Advanced disclosure. It is a **status home, not a console**. (Confirms `IMMERSIVE_CONCEPTS.md §5 Q1` recommendation: a *tested liveness contract*, not rarer feedback.)

---

## 1. Final layout (recommended) — ASCII wireframe

```
┌──────────────────────────────────────────────────────────────────────────────┐
│  HEADER  (re-skin: Surface bg, NOT #024AD8 band :114)                          │
│  SenseEase                              ┌─ LivenessChip (Badge) ───────────┐   │
│  Quiet visual comfort, always on        │ ● Watching · screen capture      │   │  ← SensingModeLabel
│  Type.Title / Type.Caption TextSecondary└──────────────────────────────────┘   │     (binding KEPT verbatim)
├──────────────────────────────────────────────────────────────────────────────┤
│  Tabs:  [ Today ]   Insights · 洞察    Assistant    Settings · 設定            │  ← renames only, index 0 fixed
│                                                                                │
│  ScrollViewer ▸ StackPanel  (Margin 24,20,24,24 — UNCHANGED; Gap.Stack 16)     │
│                                                                                │
│  ① FIRST-RUN CARD  (Card · Elev.2 · only if ShowFirstRunBanner)               │
│  ┌──────────────────────────────────────────────────────────────────────────┐ │
│  │ ◐ Welcome to SenseEase                                      Type.Heading  │ │
│  │   歡迎使用 SenseEase                                          Type.Caption │ │
│  │   Run the 60-second setup so SenseEase can suggest comfort   Type.Body    │ │
│  │   settings that fit you. · 先跑 60 秒設定，建議個人化預設值。 Type.Caption │ │
│  │                                            [ Skip ]   [ Run setup ]        │ │
│  │                                            Btn.Quiet  Btn.Primary          │ │
│  └──────────────────────────────────────────────────────────────────────────┘ │
│                                                                                │
│  ② COMFORT HERO  (Card · Elev.2 · Radius.md · Pad.Card)   ← THE HEADLINE      │
│  ┌──────────────────────────────────────────────────────────────────────────┐ │
│  │  You're protected                                          Type.Display    │ │ ← TierToHeadlineConverter(VimsTier)
│  │  保護中，放心                                                Type.Caption   │ │
│  │                                                                            │ │
│  │  ┌ Status.Chip (comfort) ──────┐  ┌ Status.Chip (mode) ──────────────┐    │ │
│  │  │ ✓  Steady                   │  │ ◐  Watching · full protection    │    │ │ ← VimsTier / Mode + AutoModeText
│  │  └─────────────────────────────┘  └──────────────────────────────────┘    │ │
│  │   Steady ≤5 teal · Rising 6–7 amber · High 8–10 clay (icon+label+color)    │ │
│  │                                                                            │ │
│  │  ───────── ComfortMeter (tier-tinted, height 6, Radius.pill) ──── ░░░      │ │ ← VimsTier (OneWay), NOT Pressure
│  │  Screen is calm · 畫面很平穩                                  Type.Body    │ │
│  └──────────────────────────────────────────────────────────────────────────┘ │
│                                                                                │
│  ③ PROTECTION  (Card)                                                          │
│  ┌──────────────────────────────────────────────────────────────────────────┐ │
│  │  PROTECTION                                          Section.Header        │ │
│  │  ┌ List.Row (SurfaceMuted group, Separator between) ────────────────────┐ │ │
│  │  │ Keep protection always on                         [Toggle.Switch ●—]  │ │ │ ← PersistentOverlayEnabled
│  │  │ Periphery stays gently dim; deepens on motion       Type.Caption      │ │ │
│  │  ├ List.Row  (IsEnabled = PersistentOverlayEnabled) ─────────────────────┤ │ │
│  │  │ Baseline dimming        L1 ●────────── L10            L1               │ │ │ ← PersistentOverlayBaselineTier
│  │  │ At L1 the screen is fully clear until motion arrives  Type.Caption     │ │ │
│  │  └──────────────────────────────────────────────────────────────────────┘ │ │
│  │                                                                            │ │
│  │  HABITUATION                                         Section.Header        │ │
│  │  ┌ List.Row ──────────────────────────────────────────────────────────┐  │ │
│  │  │ Building your tolerance                          [ Pause ] Btn.Quiet │  │ │ ← HabituationButtonText / cmd
│  │  │ ▓▓▓▓▓▓░░░░  62%  ·  builds over weeks of calm     ComfortSteady fill │  │ │ ← HabituationLevel
│  │  └──────────────────────────────────────────────────────────────────────┘ │ │
│  │                                                                            │ │
│  │  [ Run setup again ]  Btn.Ghost                                            │ │ ← StartSetupDemoCommand
│  │  ·· Reset visual settings  Btn.Quiet, de-emphasised, last, confirm-gated   │ │ ← ResetStateCommand
│  └──────────────────────────────────────────────────────────────────────────┘ │
│                                                                                │
│  ④ ADVANCED / CLINICIAN  (Card · Expander · COLLAPSED by default · Type.Mono) │
│  ┌──────────────────────────────────────────────────────────────────────────┐ │
│  │ ▸ Advanced (engineering)                                  Type.Mono       │ │
│  │   (expanded) OFI 12.4 · Pressure 41.0 · Tier 7/10 · FOV 38% · FMS 2      │ │ ← OpticalFlowIndex/Pressure/
│  │   Overlay: Visible · Sensing: Idle (Status.Chip) · Mode: Passenger        │ │   VimsTier/FovRadiusPercent/
│  │   ▸ Event log (existing ListView, Action column leads)                    │ │   FmsScore/OverlayVisible/
│  └──────────────────────────────────────────────────────────────────────────┘ │   ActivityStatus/EventLog
└──────────────────────────────────────────────────────────────────────────────┘
```

**Recommendation:** ship exactly this 4-block order (First-run → Comfort Hero → Protection → Advanced). It is the minimum that satisfies every LOCKED decision: the hero answers "am I protected" first (Decision 1, Clarity), Protection makes the calm field the default-presented control with the baseline slider (Decision 2), the header LivenessChip + comfort chips + the Advanced self-check answer "is it working" (Decision 1 + Q1 liveness contract), and the single Advanced disclosure houses **all** demoted telemetry in `Type.Mono` while keeping every binding live (Decision 1: demoted, not deleted; smoke test stays green).

---

## 2. (a) The comfort hero — plain-language state from VIMS tier, never telemetry

- **Hero line** — `Type.Display` (28/34 SemiBold, `DESIGN_SYSTEM.md §2`) `TextPrimary #1A2230` (15.3:1 AAA). Driven **read-only** off the existing `VimsTier` (`MainViewModel.cs:745`, private setter) through a new pure `TierToHeadlineConverter` (`Mode=OneWay`):
  - tier ≤5 → "You're protected" / 「保護中，放心」
  - tier 6–7 → "Easing the edges" / 「正在緩和邊緣」
  - tier 8–10 → "Strain is high — easing now" / 「正在強力緩和」
  Copy is locked by `CONTENT_AND_VOICE.md §4` band table (`:82–87`) and the example fix at `:184`. **Never** caps, never "detected," never a raw number (S-1/S-3, VP-2).
- **Sub-line** — `Type.Caption` `TextSecondary #525B6B` (7.4:1), e.g. "Screen is calm · 畫面很平穩" (from `CONTENT_AND_VOICE.md :347`), via the same converter family.
- **Comfort `Status.Chip`** (triple-coded, mandatory — `DESIGN_SYSTEM.md §4 StatusChip`): icon + label + color via `TierToComfortChipConverter(VimsTier)` → `Steady` (check-circle, `ComfortSteadyWeak`/text `#1F5249`) / `Rising` (alert-triangle, `ComfortRisingWeak`/`#7A4E16`) / `High` (shield, `ComfortHighWeak`/`#8A3A2E`). Replaces the bare hue-only ellipse at `:255`. **3-band thresholds are consumed read-only through the converter — never re-mapped, never a 4th band** (D2; `TierColorPalette` stays the single source).
- **Mode `Status.Chip`** — bound to existing `Mode` (`:718`) + `AutoModeText` (`:760`). Copy per D1 / `CONTENT_AND_VOICE.md :354`: Passenger → "Watching · full protection", Driver → "Active · lighter touch". **Meaning must not invert** (D1: Driver = lighter, Passenger = full).
- **ComfortMeter** — slim bar height 6, `Radius.pill`, fill `ComfortSteady/Rising/High` by band, **value bound to `VimsTier` (1–10), not `Pressure`** (Pressure is unbounded clinical-internal; this replaces the alarm-red `#E54747` pressure bar at `:230`). One `Type.Body` sentence below.
- **No hover** (status display, not interactive); the meter/chip use only the eased comfort transition (§6). Empty/first-tick state: `Idle` chip "Getting ready…", never a spinner.

> **Optional behavior change (flag, not required):** if you want the hero copy localized VM-side per `CONTENT_AND_VOICE.md :283`, add read-only `ComfortHeadline`/`ComfortSubline`/`ComfortChipState` properties to `MainViewModel` (computed in the existing `VimsTier` setter). These do **not exist today** (grep-verified). **Recommended path: converters, zero VM change** — keeps this a pure visual refactor (C5) and keeps every new binding `OneWay` so it cannot re-trip the documented `VimsTier` TwoWay-to-read-only regression the smoke test guards (`MainWindowSmokeTests.cs:75–79`).

---

## 3. (b) The calm field — default-ON, adjustable baseline, L1 = 0% alpha

- **Control:** "Keep protection always on" `List.Row` with trailing **`Toggle.Switch`** bound to `PersistentOverlayEnabled` (`:795`) — replaces the bare `CheckBox` at `:384` (`DESIGN_SYSTEM.md §4` mandates Switch for every settings boolean). `AutomationProperties.Name="Keep protection always on"`, `HelpText` = the helper sentence.
- **Baseline:** "Baseline dimming" `List.Row`, `IsEnabled="{Binding PersistentOverlayEnabled}"` (keep `:392`), `Slider` bound to `PersistentOverlayBaselineTier` (`:810`, Min 1 / Max 10 / `IsSnapToTickEnabled` — keep verbatim), trailing value `Type.BodyStrong Accent` `StringFormat='L{0}'` (keep `:407`). Relabel "基準強度 / Baseline intensity" → "Baseline dimming · 基準暗度". Add helper at L1: "At L1 the screen is fully clear until motion arrives" — this is the literal truth of the engine (L1 alpha = `0.00`, `OverlayWindow.xaml.cs:56`), so a default-ON field at baseline 1 does **not** over-dim low-strain users (Decision 2).

> **⚑ BEHAVIOR CHANGE — requires user sign-off (two coupled flips, engine/persistence):**
> 1. **Default `_persistentOverlayEnabled` FALSE → TRUE** (`MainViewModel.cs:794`). This is the Day-1-protection flip the LOCKED memo explicitly flags. It changes the shipped default protective posture.
> 2. **Default `_persistentOverlayBaselineTier` 4 → 1** (`MainViewModel.cs:807`, currently "subtle but noticeable ~43% alpha"). Decision 2 requires baseline tier **1 = 0% alpha** so default-ON is *not* over-dimming. Without this second flip, default-ON would ship every user a 43% vignette — contradicting Decision 2.
> Both are one-line default changes; neither touches the tier table, the mask floor, the `[15,100]` FOV clamp, `ResolveDisplayedTier` (`:819`, natural higher tier still wins — unchanged), or any test pin. **Do not self-merge** (C5/C6). The visual spec below assumes these two flips are approved; if approval is withheld, ship the same UI with the existing FALSE/4 defaults and the hero/toggle still read correctly.

---

## 4. (c) Liveness, surfaced at-a-glance — the bar/tray/self-check contract

Implements `IMMERSIVE_CONCEPTS.md §5 Q1` (recommend: a *first-class, explicitly-tested liveness contract*). Three coordinated, **non-animating** channels (bar and tray never pulse — `TECH_CAPABILITY_MAP.md :48, :50`; PersistentBarWindow spec `:16`, TrayIconService spec `:23`):

1. **Header `LivenessChip`** (`Badge.Neutral`/`Badge.Accent`): bound to existing `SensingModeLabel` (`:689`) — re-skins the `:128–145` pill. **Keep all three `DataTrigger` values verbatim** ("DEMO MODE", "SCREEN CAPTURE"); only brushes change (DEMO → `ComfortRisingWeak`/`#7A4E16`, SCREEN CAPTURE → `Badge.Accent`, live default → `Badge.Neutral`). Copy: "Watching · {mode}" so a glance confirms the sensor is alive. `AutomationProperties.Name="Sensing mode: {SensingModeLabel}"`.
2. **Comfort + Mode chips** (in the hero, §2) are the in-window mirror of the **ambient bar** and **tray tint** — all three consume `TierColorPalette` in lock-step (the bar/tray are the out-of-window liveness the user sees when the window is closed; the hero chips are the in-window echo). This is the "quiet guardian is awake" signal without adding motion.
3. **Self-check** lives in the Advanced card (§5): `ActivityStatus` (`:754`) rendered as a `Status.Chip` (`Idle` = moon/neutral; "Suppressed: …" = alert-triangle/`ComfortRising`) — a plain "sensing is paused vs running" read for the anxious "is it even working?" user. For head-tracking liveness, **only** a scalar-driven "camera active" affordance is permitted (B3) — and head-tracking is OFF by default (B1), so no webcam/preview UI appears here.

No new VM property is required for liveness; all four bindings (`SensingModeLabel`, `VimsTier`, `Mode`/`AutoModeText`, `ActivityStatus`) already exist.

---

## 5. (d) Telemetry demoted to Advanced/Clinician view (Type.Mono), smoke test stays green

- **One `Expander`, collapsed by default**, titled "Advanced (engineering)" in `Type.Mono` (13, `DESIGN_SYSTEM.md §2` — "Diagnostics / Advanced view only"). This is the *demotion behind an Advanced/Clinician view, NOT deletion* mandated by LOCKED Decision 1.
- **All five scalars relocate here, bindings unchanged:** `OpticalFlowIndex` ("OFI"), `Pressure` ("Pressure"), `VimsTier` ("Tier {0}/10"), `FovRadiusPercent` ("FOV"), `FmsScore` ("FMS"). Plus `OverlayVisible` (`:783`) and `ActivityStatus` (`:754`) — both today render **only** on the Dashboard FMS strip (`:352–367`) and the Eye-Protection tab respectively; moving them into Advanced keeps both bindings live so no VM member goes dark.
- **Event log** moves inside Advanced (re-skinned `ListView ItemsSource="{Binding EventLog}"`, `:455`): reorder so the plain-language `Action`/`Message` column leads; numeric columns (OFI/Pressure/FOV) narrow to the right. **Keep every `DisplayMemberBinding` path verbatim** (`Time/Ofi/Pressure/FovRadius/MaskSnapshot/Message`).
- **MainWindow stays instantiable:** the Advanced card is plain XAML in Tab 0; it references **no `x:Name`** from `MainWindow.xaml.cs` (`PressureChartCanvas`, `ChatScroll`, `SettingsTabContent`, `MainTabs` all live in other tabs). The full live chart (`PressureChartCanvas`, `:575`) remains on the **Insights** tab and is out of this surface's scope — Home's Advanced shows numeric readouts only, no chart, so no chart-redraw code is touched.

---

## 6. (e) Tokens, components, triple-coded chips, AutomationProperties

All tokens from `DESIGN_SYSTEM.md §1–§4`, merged **view-locally** per §7.2 (`pack://…/Theme/SenseEaseTheme.xaml` in `Window.Resources`, **never** `App.Resources` — the smoke test boots a bare `new Application()` so App-level resources never load). `BoolToVis` (`:19`) stays in `Window.Resources`.

| Element | Component | Retires (file:line) |
|---|---|---|
| Cards | `Card` (`Surface`, `Radius.md` 12, `Pad.Card` 24, `Elev.2` neutral black) | blue `#5B82C6` shadow `:28`, radius soup |
| Header band | `Surface` bg + `Type.Title`/`Type.Caption` | `#024AD8` band `:114`, `#8ABAFF` subtitle `:125` |
| First-run card | `Card` (light) + `Btn.Quiet`/`Btn.Primary` | dark `#0E1320`/`#3D5AFE` clash `:178,200`, `#9AB6E0` Skip `:194` |
| Comfort/Mode/Sensing status | `Status.Chip` (icon+label+color) | hue-only `Ellipse` `:255`; `#E54747` text `:261,274` |
| ComfortMeter | tier-tinted bar | alarm-red pressure bar `#E54747` `:230` |
| Toggle | `Toggle.Switch` | bare `CheckBox` `:384` |
| Rows / grouping | `List.Row` + `Section.Header` (`SurfaceMuted`, `Separator`) | tinted `#E8EEFF` separators `:375,413` |
| Habituation bar | `ProgressBar` fill `ComfortSteady` | `#2BA672` `:423` |
| Reset / Pause | `Btn.Quiet`; Run-setup `Btn.Ghost` | `AlertBtn #E54747` `:64–66,332`, `GhostBtn` `:428` |
| Advanced readouts | `Type.Mono` `TextSecondary` | n/a (new disclosure) |

**Color-only → triple-coded:** every status now carries icon + label + color (comfort chip, mode chip, sensing chip), eliminating the three bare dots (`:255`, and `:543/:579` on the relocated content). Alarm red `#E54747` is fully retired in favor of `ComfortHigh #B65C4E` (A5/D2). `#7B8DB0` (~3.6:1, fails AA, `:75`) → `TextSecondary #525B6B` (7.4:1).

**AutomationProperties.Name** (currently 0 on this surface — `AutomationProperties = 0` is a known defect): hero card = full phrase ("Comfort steady, you're protected"); comfort chip / mode chip / sensing chip = full state phrase each; Keep-protection Switch = "Keep protection always on" + `HelpText`; Baseline slider = "Baseline dimming level", announces `L{value}`; habituation progressbar = "Tolerance building progress", Pause button follows `HabituationButtonText`; Run-setup / Reset / Advanced expander = explicit names; LivenessChip = "Sensing mode: {SensingModeLabel}". Min sizes: body ≥14, caption ≥12 — **no 10/11px** on this surface; `List.Row` MinHeight 48; no fixed `Height` on text containers (use `MinHeight`+`Auto`; verify reflow at 125/150% and "Make text bigger" 100–225%).

---

## 7. (f) Static field only — no decorative motion

The calm field ships **perfectly static** (Decision 3). The **only** motion on Home: (1) the eased comfort-state transition on the ComfortMeter/chip on a **band crossing** (`Dur.base` 200ms, `Ease.calmOut`, `DESIGN_SYSTEM.md §3.4`), and (2) standard control micro-transitions (Switch thumb slide `Dur.fast`, expander height) governed by `Dur.ui`/Reduce-Motion. **No** breathing field, comfort light, pulsing, color-cycling, or animated count-up anywhere (A4). The mask itself is `OverlayWindow` — out of scope; the clinical `Dur.overlay` cross-fade is untouched and never overridable by Reduce-Motion (A4, safety channel). Bar and tray never animate (`TECH_CAPABILITY_MAP.md :48,50`). Under Reduce-Motion every chrome transition becomes an instant swap; the comfort-channel transition remains eased over ≥`Dur.base` (it is a comfort-luminance change, never a hard cut).

---

## 8. Migration notes — keep bindings & tests green

**Bindings preserved verbatim** (all confirmed in `MainViewModel.cs`): `SensingModeLabel` (:689), `ShowFirstRunBanner` (:943), `SkipSetupCommand`, `StartSetupDemoCommand`, `StopDemoCommand` (stays `Collapsed`, `:322`), `ResetStateCommand`, `Mode` (:718), `MaskActive` (:734), `VimsTier` (:745), `AutoModeText` (:760), `ActivityStatus` (:754), `PersistentOverlayEnabled` (:795), `PersistentOverlayBaselineTier` (:810), `HabituationLevel` (:763), `HabituationButtonText` (:780), `ToggleHabituationCommand`, `OverlayVisible` (:783), `FmsScore` (:737), `OpticalFlowIndex`/`Pressure`/`FovRadiusPercent`, `EventLog` + entry members (`Time/Ofi/Pressure/FovRadius/MaskSnapshot/Message`). `BoolToVis` stays in `Window.Resources` (:19).

**Relocate, never delete:** every demoted scalar still binds inside the Advanced card → no dead VM member, no `App.Resources` dependency.

**New artifacts (visual-only, no behavior change, C5):** `TierToHeadlineConverter`, `TierToSublineConverter`, `TierToComfortChipConverter`, `TierToComfortBrushConverter` — pure `IValueConverter`s reading `VimsTier`, all consumers `Mode=OneWay`. Design-system components from `Theme/Controls.xaml` via the view-local merge.

**`x:Name` / tab-order safety:** Dashboard tab references no `x:Name` from code-behind; **keep Dashboard at index 0** (`MainWindow.xaml.cs` `MainTabs.SelectedIndex` logic depends on tab order). Tab renames (Dashboard→Today, Eye Protection→Insights) are copy-only; no test asserts on `TabItem.Header`.

**Smoke test (`MainWindowSmokeTests.cs`):** constructs `MainWindow`, runs the dispatcher, fires `OnStateChanged(..., new SenseState { VimsTier = 4 / 7 })` and asserts no binding error/exception. Stays green because (a) tokens merge view-locally not from `App.Resources`; (b) all new `VimsTier` bindings are `OneWay` read-only converters (cannot re-trip the documented TwoWay-to-read-only regression); (c) `PersistentOverlayEnabled`/`PersistentOverlayBaselineTier` keep their existing two-way bindings to settable properties. Run `dotnet test` before requesting visual review (C6: visual review is a user hard-stop before any tag/merge).

**Behavior-change flags requiring user sign-off (do not self-merge — C5/C6):**
1. **Default-ON calm field + baseline tier 1** — the two coupled default flips at `MainViewModel.cs:794` and `:807` (§3 above). The deliberate, memo-flagged Day-1 protection change.
2. **"Reset visual settings" gains a confirm step** — re-styled off `AlertBtn`; per D3 a one-tap reset is disallowed. `ResetStateCommand` stays wired to the narrowed transient reset (never `_habituation.Reset()`; Q4.5 `_offset` preserved). If the command currently executes immediately, adding the confirm is a behavior change — flag it.
3. **Telemetry relocation** Dashboard→Advanced (and FMS/Overlay/ActivityStatus moves) — a layout move; call it out so the reviewer expects those numbers under the disclosure, not deleted.

**Hard-stop compliance:** no mask depth/floor touched (A2); no escape-hatch surfaced (A3); no webcam/preview UI (B1/B3, head-tracking OFF by default); `AppId` untouched (C3); world-locked grid untouched (C1); tier→3-band consumed read-only via converters, no 4th band (D2); habituation `_offset` never zeroed (D3); alarm red retired for `ComfortHigh #B65C4E` (A5/D2); calm field static (Decision 3); the only routine motion remains the out-of-scope clinical mask cross-fade (Decision 3). Commit only on explicit request.

---

**Verified evidence:** `src/SenseEase.App/MainWindow.xaml` (full, Dashboard tab 168–480), `ViewModels/MainViewModel.cs` (:689–943 — all bindings; `_persistentOverlayEnabled` FALSE :794, baseline tier 4 :807, `ResolveDisplayedTier` :819; `ComfortHeadline`/`ComfortSubline` confirmed absent), `Views/OverlayWindow.xaml.cs` (L1 alpha 0.00 :56, `MaskSmoothingFactor` 0.25 :77), `tests/SenseEase.Tests/MainWindowSmokeTests.cs` (:75–79 OnStateChanged/VimsTier), `docs/design/DESIGN_SYSTEM.md` (tokens/components/§7.2 smoke-test trap), `docs/design/HARD_CONSTRAINTS.md` (A–D guardrails), `docs/design/CONTENT_AND_VOICE.md` (:82–87 band copy, :184/:347/:354 fixes, :283 localization pattern), `docs/design/TECH_CAPABILITY_MAP.md` (:48/:50 bar+tray never animate), `docs/design/IMMERSIVE_CONCEPTS.md` (§5 Q1 liveness contract).
