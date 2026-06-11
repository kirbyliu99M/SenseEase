> **ARCHIVED 2026-06-11 — superseded by the v0.10 head-tracking tag; kept for historical
> reference; statements below may be factually wrong about the current code.**
> (Alpha-1 M5 design-adoption implementation plan for D0 tokens through D2 card; the
> executed work is recorded in the git log from the v0.10..v0.11 range.)

# SenseEase Alpha-1 — M5 Design-Adoption Implementation Plan

> Synthesized 2026-06-06 by the `scope-m5-visual` workflow (6 agents over the locked
> design specs + the real source tree). This is the authoritative execution guide for
> D0 tokens → D1 Home → D2 Card → D4 copy/a11y. Verified against the actual code, so it
> overrides spec line numbers where they drifted.

## Ground truth (verified — read before editing)
- **Greenfield:** no `Theme/`, no `Converters/`, no `Resources/*.resx`, no `BannedPhraseLintTests` exist yet. "Keep BannedPhraseLintTests green" means **create** that test (Step 7).
- `App.xaml` `<Application.Resources>` is **deliberately empty** (comment at App.xaml:5-10). This is the smoke-test contract — never add resources there.
- Inline styles already live in `MainWindow.Resources` (`Card` :22, `RoundBtn` :35, `PrimaryBtn` :53, `AlertBtn` bg `#E54747` :65, `CardLabel`). We **migrate** these, not create from scratch.
- **Line drift (re-grep, don't trust spec numbers):** `VimsTier` @777, `DisplayStressPercent` @783, `PersistentOverlayEnabled` @903, `PersistentOverlayBaselineTier` @918. Dashboard tab opens @168; telemetry cards ~210-280; `#E54747` pressure bar @230; `AlertBtn` `#E54747` @65; live inline `#024AD8` royal blue.
- `NotificationCardWindow.SetContent(TierColorPalette.TierBand band, int tier, double pressure)` @xaml.cs:453; `_lastBand` @455; `EngageClicked` @55, `MuteClicked` @60, `PowerSaveToggled` @65, `_userHasInteracted` @114, `ActiveProposal` @120, `StartExpiryCountdown` @194, `DescriptionForBand` @496, `QuickDiscomfortPhrases` @667; EM5 guard already partially @439.
- Pinned test strings: `NotificationCardWindowTests.cs:40` = `"VIMS 視覺壓力　第 3 級 / 10　·　壓力指數 27"`; Test 2 (:57-58) PowerSave override strings must stay byte-identical; all 9 proposal x:Names must survive.

## The cardinal rule (highest risk, R-A)
**`App.Resources` stays empty. Tokens merge VIEW-LOCALLY** into each `Window.Resources`/`UserControl.Resources`:
```xml
<ResourceDictionary.MergedDictionaries>
  <ResourceDictionary Source="pack://application:,,,/SenseEase.App;component/Theme/SenseEaseTheme.xaml"/>
</ResourceDictionary.MergedDictionaries>
```
Keep the existing `<BooleanToVisibilityConverter x:Key="BoolToVis"/>` alongside. `MainWindowSmokeTests` boots a bare `new Application()` with no App.Resources, so any App-level token fails it. **Run the smoke test after every dictionary-merge edit.**

## Sequence
Step 0 baseline → **Step 1 D0 tokens** → Step 2 converters + parity test → Step 7 lint scaffolding (so later steps ratchet) → **Step 3 D1 hero (UVHS)** → **Step 4 D2 card (UVHS + NE27/EM5/H7)** → Step 5 D4 resx/a11y → Step 6 calibration/settings/bar.
- **Visual/UVHS sign-off:** Steps 3, 4, visible parts of 6.
- **Objective (test-gated):** Steps 0, 1 (smoke), 2 (parity), 4's NE27/EM5/H7, 5 (key-parity), 7 (lint).

## Step 1 — D0 token foundation (DO FIRST)
New files under `src/SenseEase.App/Theme/`: `Colors.Light.xaml`, `Typography.xaml`, `Primitives.xaml` (spacing/radius/elevation/motion), `Controls.xaml` (components), `SenseEaseTheme.xaml` (bundle via MergedDictionaries). **Token names FROZEN once committed.**
- **Colors:** Canvas `#F6F7F9`, Surface `#FFFFFF`, SurfaceMuted `#F1F3F6`, Separator `#E2E5EA`, TextPrimary `#1A2230`, TextSecondary `#525B6B`, TextTertiary `#6E7787`, Accent `#3A6EA5`, TextOnComfort `#FFFFFF`. ComfortSteady `#2F7D6E`/Weak `#E2F0EC`/Text `#1F5249`; ComfortRising `#9A6B22`/Weak `#F6ECD8`/Text `#7A4E16`; ComfortHigh `#B65C4E`/Weak `#F6E2DE`/Text `#8A3A2E`. **No `#E54747`, `#024AD8`, `#7B8DB0` anywhere.**
- **Typography:** Type.Display 28/34 SemiBold, Type.Title 20/28 SemiBold, Type.Heading 16/22 SemiBold, Type.Body 14/20, Type.Caption 12/16, Type.Mono 13/18 (Consolas). Floor 14 body / 12 caption.
- **Primitives:** Pad.Card 24, Gap.Stack 16, Radius.md 12, Radius.sm 8, Radius.pill 999; Elev.2 DropShadow black Y2 Blur10 Opacity0.08 (NOT blue); Dur.fast 120ms, Dur.base 200ms, Ease.calmOut.
- **Components (Controls.xaml):** Card, Status.Chip (icon+label+color triple), Btn.Primary/Btn.Quiet/Btn.Ghost, Toggle.Switch (exposes IsChecked), List.Row (MinHeight 48), Section.Header, ComfortMeter bar, Badge.
- Gate: `MainWindowSmokeTests` green after merging into MainWindow.Resources.

## Step 2 — Converters (NEW `src/SenseEase.App/Converters/`, pure read-only)
`TierToHeadlineConverter`, `TierToSublineConverter`, `TierToComfortChipConverter`, `TierToComfortBrushConverter` — all `IValueConverter`, read `int VimsTier`, **delegate band logic to `TierColorPalette.BandForTier`** (no duplicated ≤5/6-7/8-10 thresholds). Tier 0 / `!MaskActive` → Idle.
- **Headline copy (app-action, NEVER felt-state — HARD_CONSTRAINTS wins over Brand):** ≤5 → "Watching your screen / 守護你的畫面" (NOT "You're protected"); 6-7 → "Easing the edges / 正在緩和邊緣"; 8-10 → "Strain is high — easing now / 正在強力緩和".
- Register converters in MainWindow.Resources (view-local). Bind everything `Mode=OneWay` (TwoWay→read-only crashes startup).
- Gate: `TierConverterParityTests` — every tier 1-10 maps to the same band across all four converters AND `BandForTier`.

## Step 3 — D1 Home hero (MainWindow.xaml Dashboard tab @168) — VISUAL/UVHS
Replace the 4 telemetry cards (OFI ~210, Pressure+`#E54747` bar @230, FOV ~236, Mode) with: first-run card (keep) → **Comfort Hero Card** (Elev.2, Radius.md, Pad.Card 24) → relocate Controls/Protection/Habituation below.
- Hero: headline Type.Display ← TierToHeadlineConverter; subline Type.Caption ← TierToSublineConverter; Comfort Status.Chip ← TierToComfortChipConverter; Mode chip ← Mode+AutoModeText (Passenger "Watching · full protection" / Driver "Active · lighter touch", never invert); **ComfortMeter** fill ← TierToComfortBrushConverter, value ← **VimsTier (1-10, NOT Pressure)** — retires the alarm-red bar.
- **Demote telemetry** (OFI/Pressure/Tier/FOV/FmsScore/OverlayVisible/ActivityStatus/EventLog) into a collapsed **Advanced Expander**, Type.Mono, **bindings byte-identical** (no property goes dead).
- `AutomationProperties.Name` full phrase on hero + each chip; min caption 12px; no fixed Height on text. All `Mode=OneWay`; no hover/click; no x:Name referenced from Dashboard code-behind.
- DisplayStressPercent (P1) is optional for MVP — VimsTier is the data source; surface the % as a secondary readout if clean, don't block on it.
- Gate: `MainWindowSmokeTests` green + **UVHS before/after sign-off**.

## Step 4 — D2 NotificationCard re-skin — VISUAL + behavior flags
Add pack:// merge to Window.Resources; convert ~40 inline hex to tokens; container Radius.lg 16, Pad.Card 24, Elev.3, **Width=480 locked** (positioning @xaml.cs:343). Six-zone layout. **Preserve every pinned x:Name:** TitleText, BodyText, PowerSaveToggle, ProposalPanel, ProposalSummaryText, ProposalRationaleText, ProposalAcceptBtn, ProposalDeclineBtn, ProposalSafetyNoteText.
- BC-1 (sign-off): title → plain-language off `_lastBand`, telemetry → Type.Mono subline; **update test :40 in lockstep; keep Test 2 PowerSave strings byte-identical**.
- BC-2 (sign-off): 4-hue ramp → single deepening hue (ComfortSteadyWeak…ComfortHighWeak), icon+label+position; **keep `Tag='1'..'4'` → OnQuickDiscomfortClicked byte-identical**.
- BC-3 (sign-off): primary "緩和畫面 / Ease my view now", EngageBtn/OnEngageClicked unchanged, retire `#FF9B6B`→Accent.
- BC-4: chat row `Visibility=Collapsed IsHitTestVisible=False` (keep code-behind compiling).
- **Objective (re-verify intact):** NE27 (disable all 3 proposal buttons + null `_activeProposal` before `ProposalExpired?.Invoke`), EM5 (`OnForegroundCheckTick.Hide()` gated on `_userHasInteracted`), H7 (`SanitizeRationale` cap 120 + strip tags + italic "AI suggests:" prefix).
- Safety note on ComfortHighWeak/Text (never red, never hidden). Host re-registers `SetMonitorExcludeRect` after height shrink.

## Step 5 — D4 copy / resx / a11y
New `Resources/Strings.zh-Hant.resx` + `Strings.en.resx`; migrate user-facing strings; one language per key (no "zh / en" slash-cram except calibration recall + the head-tracking restart note). Locked lexicon: Steady=平穩 / Rising=上升 / High=較高. Warm register 「你」 not 「您」. Sweep banned phrases (Alert/Warning/Danger/Detected-headline/`!`/Power Save; 警告/危險/偵測到高度視覺壓力/省電/請務必). `AutomationProperties.Name` on every interactive+status element; reflow at 125/150%. Gate: resx key-parity test + banned-phrase lint.

## Step 6 — Calibration / Settings / Bar token migration (no behavior change)
`CalibrationCardView.xaml` (collapse 5× templates → one Scale.Segmented; 10px→12px; `#7B8DB0`→TextSecondary — **migrate in the SAME commit as D0**, R-F shadowing), `SettingsContent.xaml` (CheckBox→Toggle.Switch; head-tracking opt-in OFF default + "Requires restart" + local-webcam caption), `PersistentBarWindow.xaml` (merge + token refs).

## Step 7 — Lint + parity gates (must be CREATED)
New `tests/SenseEase.Tests/DesignLintTests.cs`: orphan-hex **ratchet** (baseline ≈241, fail on increase; whitelist TierColorPalette defs); banned-phrase gate over resx+XAML; a11y gate (undecorated interactive/status, no 10/11px); tier-band parity. Full green bar also includes MainWindowSmokeTests, NotificationCardWindowTests, TierColorPaletteTests, HabituationTests (PressureSpike pin), LivenessContractTests.

## DEFERRED out of M5 (separate sign-off, do NOT self-merge)
- **D6 atomic two-flip:** `PersistentOverlayEnabled` FALSE→TRUE (@903) + `PersistentOverlayBaselineTier` 4→1 (@918) together, gated by a "0.00 alpha at rest" test. (This is M7.)
- **Reset-visual-settings confirm step (D3):** must call `ResetTransientState()`, never `_habituation.Reset()` (offset sacred).

## Top risks
1. **App.Resources smoke break (R-A):** pack:// view-local only; smoke after every merge.
2. **TwoWay→read-only startup crash:** all VimsTier/converter bindings `Mode=OneWay`.
3. **No-new-motion (LOCKED #3):** only motion = comfort-state eased color cross-fade (Dur.base) + chrome that snaps under Reduce-Motion. No breathing/pulse/count-up.
4. **Pinned test strings (BC-1):** update `NotificationCardWindowTests:40` in lockstep; PowerSave strings byte-identical; 9 x:Names survive.
5. **Band-threshold drift:** converters read `TierColorPalette.BandForTier`, never duplicate.
6. **Habituation offset wipe (D3 cardinal):** any reset → `ResetTransientState()` only.
7. **Orphan-hex ratchet:** migrate surface-by-surface; gate fails only on increase (don't convert all 241 at once).
