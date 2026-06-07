# SenseEase — "Calm" Design System (token spec)

<!-- Generated 2026-06-05 by the senseease-rebrand-design workflow (31 agents). Companion to docs/UIUX_UPGRADE_PLAN.md. Draft for review — no code changed. -->

> Generated 2026-06-05 by the senseease-rebrand-design workflow (31 agents). Companion to docs/UIUX_UPGRADE_PLAN.md. Draft for review — no code changed.

Confirmed: color-only status via bare `Ellipse` fill (8-9px dots, lines 255, 543, 579), base font is plain `Segoe UI` (line 10, not Variable). I have all ground-truth evidence needed. Delivering the locked token spec.

# The "Calm" Design System — Locked Token Spec

This is the canonical, lockable token layer for SenseEase. All values are decision-grade and contrast-checked. Tokens are **semantic** (intent), not literal (hue), so Dark and High-Contrast are re-skins of the same names, never rewrites. Evidence citations reference the current Pre-Alpha v0.11.1 tree (241 inline hex literals across 10 XAML files; `Application.Resources` empty per `src/SenseEase.App/App.xaml:5–10`).

---

## 1. Color Tokens

### 1.1 Token roles (theme-independent names)

These names are the only color identifiers any view may reference. No view may declare a raw `#RRGGBB` (see §7 lint).

`Canvas` · `Surface` · `SurfaceMuted` · `SurfaceSunken` · `Separator` · `TextPrimary` · `TextSecondary` · `TextTertiary` · `TextOnAccent` · `TextOnComfort` · `Accent` · `AccentHover` · `AccentPressed` · `AccentWeak` · `AccentWeakBorder` · `ComfortSteady` · `ComfortSteadyWeak` · `ComfortRising` · `ComfortRisingWeak` · `ComfortHigh` · `ComfortHighWeak` · `Danger` · `FocusRing` · `OverlayScrim`.

> Note: `Danger` and `ComfortHigh` are distinct tokens but resolve to the **same muted clay** in Light/Dark. There is no alarm red anywhere. `Danger` exists only so `DangerButton` (destructive, e.g. "Delete all my data") has a semantic name; it is never used for comfort state.

### 1.2 LIGHT theme (default)

| Token | Hex | Replaces (current) | Role |
|---|---|---|---|
| `Canvas` | `#F6F7F9` | `#E2ECFF` | App background — warm near-white, not saturated blue |
| `Surface` | `#FFFFFF` | white | Cards, sheets, popups |
| `SurfaceMuted` | `#F1F3F6` | `#F3F6FC` / `#EEF3FF` | Secondary fills, status strips, list-row group bg |
| `SurfaceSunken` | `#ECEEF2` | `#F8FAFF` | TextField/input wells, unselected scale cells |
| `Separator` | `#E2E5EA` | `#E8EEFF` | 1px hairlines |
| `TextPrimary` | `#1A2230` | `#0A1F5C` | Headings + body |
| `TextSecondary` | `#525B6B` | `#7B8DB0` | Labels, helper text |
| `TextTertiary` | `#6E7787` | `#9AB6E0` | De-emphasis only, never essential alone |
| `TextOnAccent` | `#FFFFFF` | white | Text on `Accent` fills |
| `TextOnComfort` | `#FFFFFF` | white | Text on `Comfort*` solid chips |
| `Accent` | `#3A6EA5` | `#024AD8` | Primary actions — calm, low-chroma blue |
| `AccentHover` | `#33628F` | — | Primary hover |
| `AccentPressed` | `#2C5479` | — | Primary press |
| `AccentWeak` | `#E9EFF5` | `#EEF3FF` | Ghost-button surface, badge bg |
| `AccentWeakBorder` | `#C9D9E6` | `#B7CFE3` | Ghost/badge hairline |
| `ComfortSteady` | `#2F7D6E` | `#2BA672` | "All good" — muted teal-green |
| `ComfortSteadyWeak` | `#E2F0EC` | — | Steady chip bg (text uses `#1F5249`) |
| `ComfortRising` | `#9A6B22` | `#F0832E` | "Strain building" — muted amber (darkened for AA text) |
| `ComfortRisingWeak`| `#F6ECD8` | `#FFF1D6` | Rising chip bg |
| `ComfortHigh` | `#B65C4E` | `#E54747` | "Actively easing" — muted clay, **not** alarm red |
| `ComfortHighWeak` | `#F6E2DE` | `#FBD0CB` | High chip bg |
| `Danger` | `#B65C4E` | — | Destructive actions only (= clay) |
| `FocusRing` | `#3A6EA5` | — | 2px keyboard-focus stroke + 1px white inset |
| `OverlayScrim` | `#33000000` | — | Modal/sheet backdrop |

**Contrast — every text-on-surface pairing (sRGB WCAG 2.1):**

| Foreground | Background | Ratio | Verdict |
|---|---|---|---|
| `TextPrimary #1A2230` | `Surface #FFFFFF` | **15.3:1** | AAA (all sizes) |
| `TextPrimary #1A2230` | `Canvas #F6F7F9` | **14.3:1** | AAA |
| `TextPrimary #1A2230` | `SurfaceMuted #F1F3F6` | **13.6:1** | AAA |
| `TextSecondary #525B6B` | `Surface #FFFFFF` | **7.4:1** | AAA (body), AAA |
| `TextSecondary #525B6B` | `Canvas #F6F7F9` | **6.9:1** | AAA |
| `TextSecondary #525B6B` | `SurfaceMuted #F1F3F6` | **6.6:1** | AA (all), AAA (large) |
| `TextTertiary #6E7787` | `Surface #FFFFFF` | **4.9:1** | AA (all sizes) |
| `TextTertiary #6E7787` | `Canvas #F6F7F9` | **4.6:1** | AA (all sizes) |
| `Accent #3A6EA5` (link text) | `Surface #FFFFFF` | **4.9:1** | AA |
| `TextOnAccent #FFFFFF` | `Accent #3A6EA5` | **4.9:1** | AA |
| `TextOnComfort #FFFFFF` | `ComfortSteady #2F7D6E` | **4.7:1** | AA |
| `TextOnComfort #FFFFFF` | `ComfortHigh #B65C4E` | **4.5:1** | AA |
| `#1F5249` (steady chip text) | `ComfortSteadyWeak #E2F0EC` | **7.8:1** | AAA |
| `#7A4E16` (rising chip text) | `ComfortRisingWeak #F6ECD8` | **6.4:1** | AAA (large), AA (all) |
| `#8A3A2E` (high chip text) | `ComfortHighWeak #F6E2DE` | **6.0:1** | AA (all), AAA (large) |
| `Accent #3A6EA5` on `AccentWeak #E9EFF5` | — | **4.5:1** | AA |

> **Why `ComfortRising` darkened to `#9A6B22`:** the audit's proposed `#C08A3C` is only ~2.6:1 as text on white — fails AA. As a **fill** with white text it is also marginal. Locked value `#9A6B22` gives 4.6:1 white-on-fill and lets the weak-bg chip text hit AAA. The bright amber lives nowhere as text.
> **Retired contrast failures:** `#7B8DB0` (~3.6:1, used for every Q3 scale sub-label, `CalibrationCardView.xaml:231/258/285/312/338`, and the idle dot `MainWindow.xaml:579`) and `#9AB6E0` (Skip button text, `MainWindow.xaml:194`) are removed for any essential text.

### 1.3 DARK theme

Migraine/photophobic users frequently prefer dark; this is a first-class theme, not a tint. Same token names re-pointed.

| Token | Hex | Role / contrast note |
|---|---|---|
| `Canvas` | `#15181D` | App bg |
| `Surface` | `#1D2128` | Cards |
| `SurfaceMuted` | `#252A32` | Strips, row groups |
| `SurfaceSunken` | `#11141A` | Input wells |
| `Separator` | `#30353E` | Hairlines |
| `TextPrimary` | `#E6E9EE` | on Surface = **13.4:1** AAA |
| `TextSecondary` | `#AEB6C2` | on Surface = **7.6:1** AAA |
| `TextTertiary` | `#868E9B` | on Surface = **4.6:1** AA |
| `TextOnAccent` | `#0E141B` | on Accent = 8.9:1 AAA |
| `TextOnComfort` | `#11151A` | on comfort fills ≥ 6:1 AA |
| `Accent` | `#7CA9CE` | links on Surface = **6.1:1** AAA(large)/AA |
| `AccentHover` | `#8FB7D8` | — |
| `AccentPressed` | `#6B97BC` | — |
| `AccentWeak` | `#243240` | Ghost surface |
| `AccentWeakBorder` | `#33485C` | — |
| `ComfortSteady` | `#5FB3A1` | lightened ~14% |
| `ComfortSteadyWeak` | `#1E2E2B` | — |
| `ComfortRising` | `#D6A861` | text on Surface = 6.8:1 AAA(large) |
| `ComfortRisingWeak` | `#322A1B` | — |
| `ComfortHigh` | `#D8897B` | clay, lightened; on Surface = 6.4:1 |
| `ComfortHighWeak` | `#322320` | — |
| `Danger` | `#D8897B` | = clay |
| `FocusRing` | `#7CA9CE` | + 1px `#0E141B` inset |
| `OverlayScrim` | `#66000000` | — |

### 1.4 HIGH-CONTRAST theme

Activated by auto-reading `SystemParameters.HighContrast` (currently **0 occurrences**, grep-verified). Map tokens to system colors so the user's chosen HC scheme wins — do **not** hardcode the palette. Bind via `{DynamicResource}` so it can swap at runtime on `SystemParameters.StaticPropertyChanged`.

| Token | System brush | Floor |
|---|---|---|
| `Canvas` / `Surface` | `SystemColors.WindowBrush` | — |
| `SurfaceMuted` / `SurfaceSunken` | `SystemColors.ControlBrush` | — |
| `Separator` | `SystemColors.ActiveBorderBrush` | — |
| `TextPrimary` / `TextSecondary` | `SystemColors.WindowTextBrush` | (no tertiary — collapse to WindowText) |
| `Accent` / `Danger` / `Comfort*` | `SystemColors.HotTrackBrush` / `HighlightBrush` | text triple-coded so hue loss is non-fatal |
| `TextOnAccent` / `TextOnComfort` | `SystemColors.HighlightTextBrush` | — |
| `FocusRing` | `SystemColors.HighlightBrush` | 2px, always visible |

In HC, `Comfort*` semantic distinction is carried by **icon + label** (§4 StatusChip), since the OS owns the hue. This is the payoff of triple-coding.

---

## 2. Typography

**Family:** `Segoe UI Variable Text, Segoe UI, Segoe UI Variable, sans-serif`. Currently `MainWindow.xaml:10` uses plain `Segoe UI` — upgrade to the Variable optical-size family on Win11 with `Segoe UI` fallback for Win10. **Mono:** `Cascadia Code, Cascadia Mono, Consolas` — diagnostics only, never on primary surfaces.

| Role token | Size / Line | Weight | Use |
|---|---|---|---|
| `Type.Display` | 28 / 34 | SemiBold (600) | Comfort-state hero ("You're protected") |
| `Type.Title` | 20 / 26 | SemiBold | Window + section titles |
| `Type.Heading` | 16 / 22 | SemiBold | Card titles |
| `Type.Body` | **14 / 20** | Regular (400) | Default text — floor |
| `Type.BodyStrong` | 14 / 20 | SemiBold | Emphasis, inline values |
| `Type.Caption` | **12 / 16** | Regular | Helper text — floor |
| `Type.CaptionStrong`| 12 / 16 | SemiBold | Eyebrow labels, chip text |
| `Type.Mono` | 13 / 18 | Regular | Diagnostics / Advanced view only |

**Hard rules:**
- **No 10px or 11px anywhere user-facing.** Currently violated 43× across 7 files (e.g. the entire Q3 scale at `FontSize="10"`, `CalibrationCardView.xaml:231–338`; the discomfort eyebrow at `FontSize="11"`, `NotificationCardWindow.xaml:238`; all four discomfort buttons at `FontSize="12"` are fine but their context is not). Migrate every `10`/`11` to `Caption` (12) minimum.
- Body raised from current 13/12px defaults to **14**.
- **Text scaling:** all sizes in DIPs; never set fixed `Height` on text containers — use `MinHeight` + `Auto`. Verify reflow at Windows 125% and 150% scaling and at the OS "Make text bigger" 100–225% slider. Cards must grow, not clip.
- `TextOptions.TextFormattingMode="Ideal"` and `TextRenderingMode="ClearType"` set once at the theme root for crisp small text.

---

## 3. Spacing, Radius, Elevation, Motion

### 3.1 Spacing — 4pt base, 8pt rhythm
`Space.1=4` · `Space.2=8` · `Space.3=12` · `Space.4=16` · `Space.5=20` · `Space.6=24` · `Space.7=28` · `Space.8=32`.
Standard card padding `Space.6` (24); compact `Space.4` (16); inter-card gap `Space.4`. Retires the current `6/8/12/14/18/20/22/24` ad-hoc soup (button padding alone spans `4,8`→`22,10` today, e.g. `NotificationCardWindow.xaml:257`). Shipped as `Thickness` resources (`Pad.Card`, `Pad.Button`, `Gap.Stack`) because XAML cannot do arithmetic on a `double` margin.

### 3.2 Radius — four steps
`Radius.sm=8` (buttons, inputs, scale cells) · `Radius.md=12` (cards) · `Radius.lg=16` (windows, sheets, popups) · `Radius.pill=999` (badges, chips). Retire 2/3/6/10/14 (current radii in use span seven values). Shipped as `CornerRadius` resources.

### 3.3 Elevation — one neutral shadow, three levels
Drop the blue `#5B82C6` shadow cast entirely; shadow color is always `#000000` at low opacity.

| Token | Y | Blur | Opacity | Use |
|---|---|---|---|---|
| `Elev.1` | 1 | 4 | 0.06 | Resting controls, list rows |
| `Elev.2` | 2 | 10 | 0.08 | Cards |
| `Elev.3` | 8 | 28 | 0.14 | Floating cards, sheets, popups, notification card |

Shipped as `DropShadowEffect` resources (`Direction=270, RenderingBias=Quality`). In Dark theme, `Elev.*` opacities go to `0.20 / 0.28 / 0.45` (shadows must be deeper on dark surfaces to read).

### 3.4 Motion — rationed and switchable off
`Dur.fast=120ms` · `Dur.base=200ms` · `Dur.slow=280ms` · `Dur.overlay=320ms`. Easing `Ease.calmOut` = cubic-bezier(0.2, 0, 0, 1), shipped as a reusable `KeySpline`.

**Reduce-Motion contract (binding, currently 0 motion-pref checks exist):**
1. A single app-level `bool ReduceMotion` that is `manualToggle OR NOT SystemParameters.ClientAreaAnimation`. Auto-read the Windows "Show animations" pref via `SystemParameters.ClientAreaAnimation` and subscribe to `SystemParameters.StaticPropertyChanged` so it tracks OS changes live.
2. When `ReduceMotion` is true: the dashboard pressure chart (`MainWindow.xaml:574`) renders **static**, updating on a slow tick; typing dots (storyboard ~line 717) become a static `…`; every transition becomes an **instant** swap (duration → 0), never a cross-fade-with-motion.
3. **Exempt and non-negotiable:** the overlay/mask brightness transition is **always** eased over `Dur.overlay` (≥300ms) — it must never snap, even under Reduce-Motion, because a hard luminance cut is itself a trigger. Codify as the `Dur.overlay` token so no future edit can introduce a hard cut. This is a safety token, not a polish token.
4. Expose `ReduceMotion` as a `{DynamicResource}` driving a `Duration` resource (`Dur.ui`) that views bind their storyboards to — so toggling it re-skins motion app-wide with no per-view code.

---

## 4. Component Inventory

Each component is defined **once** in `Theme/Controls.xaml`, keyed, and consumes only the tokens above. States listed as rest / hover / press / focus / disabled.

### PrimaryButton (`Btn.Primary`)
Filled `Accent`, `TextOnAccent`, `Radius.sm`, `Pad.Button` (14,8), `Type.BodyStrong`, `Elev.1`.
- hover `AccentHover` · press `AccentPressed` · **focus** 2px `FocusRing` + 1px inset · disabled `SurfaceMuted` bg + `TextTertiary` text, no shadow, `IsHitTestVisible=false`.
Replaces ad-hoc `#3D5AFE`/`#024AD8` filled buttons (`MainWindow.xaml:200`).

### GhostButton (`Btn.Ghost`)
`AccentWeak` bg, `Accent` text, 1px `AccentWeakBorder`, `Radius.sm`, no shadow.
- hover bg darken to `#DFE8F1` (Light) · press `#D4E0EC` · focus ring · disabled `SurfaceMuted`/`TextTertiary`.
Replaces the four discomfort buttons' bespoke per-hue fills (`NotificationCardWindow.xaml:253–304`).

### QuietButton (`Btn.Quiet`)
Transparent bg, `TextSecondary`, no border, `Radius.sm`.
- hover bg `SurfaceMuted` · press `SurfaceSunken` · focus ring · disabled `TextTertiary`.
For "Skip", "Clear", snooze, low-emphasis dismissals. Replaces the transparent `#9AB6E0` Skip button (`MainWindow.xaml:192–197`).

### DangerButton (`Btn.Danger`)
**Muted** — outline style by default: transparent bg, `Danger` (clay) text + 1px `Danger` border. Solid variant `Btn.DangerSolid` (clay fill, white text) only for confirm-step of destructive flows (e.g. "Delete all my data").
- hover `ComfortHighWeak` bg · focus ring · disabled `TextTertiary`. Never red, never the loudest thing on screen.

### Card (`Card`)
`Surface` bg, `Radius.md`, `Pad.Card` (24), `Elev.2`, optional 1px `Separator` border in HC. No colored borders (retires `BorderBrush="#3D5AFE"` on the welcome banner, `MainWindow.xaml:178`). Compact variant `Card.Compact` = `Pad` 16.

### Badge (`Badge`)
`Radius.pill`, `Pad` 8,2, `Type.CaptionStrong`. Color variants: `Badge.Neutral` (`SurfaceMuted`/`TextSecondary`), `Badge.Accent` (`AccentWeak`/`Accent`). Count/label only — no interaction states.

### Switch (`Toggle.Switch`)
Replaces bare `CheckBox` for every settings boolean (currently ~17 checkboxes in `SettingsContent.xaml`). Pill track + thumb.
- off: track `SurfaceSunken`, thumb `Surface` + `Elev.1` · on: track `Accent`, thumb white · hover: thumb grows 1px · **focus** ring around track · disabled: track `SurfaceMuted`, 50% thumb.
- Thumb slides over `Dur.fast`; under Reduce-Motion it snaps. `AutomationProperties.Name` required (the row title).

### SegmentedScale (`Scale.Segmented`) — the load-bearing dedup
The Q3 1–5 cell. **Defined ONCE, data-driven** via an `ItemsControl` over a 5-item model (`{Value, Label}`), replacing the **five hand-copied 40-line `ControlTemplate`s** at `CalibrationCardView.xaml:224–353` and reused for the NotificationCard discomfort ramp (`NotificationCardWindow.xaml:249–304`).
- Cell rest: `SurfaceSunken` bg, 1px `Separator`, `Radius.sm`, `MinHeight 58`. Number = `Type.Heading` `TextPrimary`; sub-label = `Type.Caption` `TextSecondary` (raised from the failing 10px `#7B8DB0`).
- **selected:** fill = a **single-hue sequential ramp** by index (one accent, increasing depth — `AccentWeak → Accent`), `TextOnAccent`. **Not** the current four-hue blue→amber→orange→red alarm ramp (`NotificationCardWindow.xaml:253/267/281/295`). For the discomfort context the ramp may walk `ComfortSteadyWeak → ComfortHigh` but stays sequential, never categorical-alarm.
- hover: 1px `Accent` border · focus: ring on the cell · keyboard: arrow-key moves selection (it's a radio group).

### OptionCell (`Option.Cell`)
Multi-select checkbox-as-row (Q2 symptom list, `CalibrationCardView.xaml:200–203`). `Surface`/`SurfaceMuted` row, leading check glyph, `Type.Body` label.
- rest/hover `SurfaceMuted`/`SurfaceSunken` · checked: `AccentWeak` bg + `Accent` check · focus ring · `AutomationProperties.Name` = label.

### TextField (`Field.Text`)
Real placeholder via templated `TextBox` with an adorner `TextBlock` shown when empty + unfocused — **retires the VisualBrush placeholder hack** noted in the audit. `SurfaceSunken` bg, 1px `Separator`, `Radius.sm`, `Pad` 12,8, `Type.Body`.
- focus: border → `Accent` 1.5px + `FocusRing` glow · disabled: `SurfaceMuted`, `TextTertiary` · invalid: 1px `Danger` border + caption error text below (never color-only).

### StatusChip (`Status.Chip`) — triple-coded, mandatory
Replaces every color-only `Ellipse` status dot (8–9px bare dots at `MainWindow.xaml:255, 543, 579`). Always **icon + text + color** so it survives colorblindness and High-Contrast.
- Layout: leading icon (16px line) + `Type.CaptionStrong` label, `Radius.pill`, `Comfort*Weak` bg + matching dark text.
- Variants: `Steady` (check-circle, teal) · `Rising` (alert-triangle, amber) · `High` (shield/waves, clay) · `Idle` (moon, neutral `SurfaceMuted`/`TextSecondary`). Icon and label carry the meaning; color is the third channel, never the only one.
- `AutomationProperties.Name` = full state phrase ("Comfort steady, you're protected").

### SectionHeader (`Section.Header`)
`Type.CaptionStrong` `TextSecondary`, all-caps optional, `Pad` top 24 bottom 8. Replaces the bold-text-only grouping in Settings. Sits above grouped `ListRow`s.

### ListRow (`List.Row`) — iOS-style grouped settings row
`Surface` bg inside a `SurfaceMuted` grouped container with `Radius.md`; 1px `Separator` between rows (not on last). Layout: title (`Type.Body`) + one-line helper (`Type.Caption` `TextSecondary`) on the left, trailing control (`Switch` / chevron / value) on the right, `MinHeight 48`. Tappable variant raises a `Command`; whole row is the hit target with focus ring.

### ProgressDots (`Progress.Dots`)
Q1–Q5 calibration progress. `ItemsControl` of 5 dots: done = filled `Accent`, current = `Accent` ring, pending = `Separator`. `Radius.pill`, 8px, 6px gap. `AutomationProperties.Name` = "Step 3 of 5". No motion beyond a `Dur.fast` fill (snaps under Reduce-Motion).

---

## 5. Iconography

**Adopt Fluent System Icons (Regular weight)** as the single set — it is Microsoft's first-party line set, ships under MIT, matches Segoe UI Variable's optical voice and Win11 visual language, and has a complete glyph range for this app's needs. Use the **Regular (not Filled)** variants at a uniform **1.5px stroke @ 20px nominal** (render at 16/20/24). One set, one weight, no mixing. (Lucide is the acceptable alternative if a web/SVG pipeline is preferred; do not use both.)

**Retire all ad-hoc glyphs** and map to Fluent:

| Current glyph | Where | Fluent icon |
|---|---|---|
| `× ✕` close | NotificationCard, popups | `Dismiss` |
| `⚙` settings | tray / tabs | `Settings` |
| `⏻ U+23FB` power-save | NotificationCard (`:318`) | `Battery Saver` / `Power` |
| `● ●` status dots | MainWindow `:255/543/579` | `Checkmark Circle` / `Warning` / `Shield` (per StatusChip) |
| `💡` tip emoji | banners | `Lightbulb` |
| pause/play (overlay) | overlay controls | `Pause` / `Play` |
| `🎤`/webcam ad-hoc | head-tracking settings | `Video` / `Eye` |
| `📊` chart | Insights | `Data Trending` |

Icons are `Path`/`PathIcon` resources keyed `Icon.*` in the theme so they inherit `TextPrimary` and recolor per theme automatically.

---

## 6. Themes & State Plumbing

- Light is `{StaticResource}`-safe (default). Dark and High-Contrast require **`{DynamicResource}`** for every color token so the app can swap at runtime without restart (HC and OS dark-mode changes fire `SystemParameters.StaticPropertyChanged` / `SystemEvents.UserPreferenceChanged`).
- Theme selection precedence: **OS High-Contrast** (forced) > user explicit Light/Dark > OS app theme > Light default.
- Comfort hue distinctness in HC is delegated to icon+label (§4), so a forced single-accent HC scheme stays legible.

---

## 7. WPF Delivery

### 7.1 File layout — `src/SenseEase.App/Theme/`
```
Theme/
  Colors.Light.xaml        (Color + SolidColorBrush, the §1.2 table)
  Colors.Dark.xaml         (§1.3)
  Colors.HighContrast.xaml (§1.4, system-color mapped)
  Typography.xaml          (FontFamily, FontSize doubles, TextBlock styles §2)
  Spacing.xaml             (Thickness + CornerRadius + double tokens §3.1–3.2)
  Elevation.xaml           (DropShadowEffect §3.3)
  Motion.xaml              (Duration + KeySpline + Dur.ui dynamic §3.4)
  Icons.xaml               (Geometry/PathIcon §5)
  Controls.xaml            (every component §4 — depends on all above)
  SenseEaseTheme.xaml      (MergedDictionaries bundle of all the above)
```
`SenseEaseTheme.xaml` merges `Colors.Light.xaml` by default; the active color file is swapped at runtime.

### 7.2 The smoke-test trap — why tokens must NOT go in `App.Resources`
`tests/SenseEase.Tests/MainWindowSmokeTests.cs:29` constructs a **bare `new Application()`** and **never instantiates `SenseEase.App.App`** — so `App.xaml`'s `Application.Resources` are never loaded in the test path (this is the documented reason `BoolToVis` lives in `MainWindow.Resources`, per `App.xaml:6–10`). If tokens land in `App.Resources`, every `{StaticResource Token}` in a view throws at construction → red CI → a "broke prior works" violation.

**Locked pattern:** merge `SenseEaseTheme.xaml` into **each view's own `.Resources`** by pack URI. WPF caches `ResourceDictionary` by source URI, so the bundle is parsed once regardless of how many views merge it — zero duplication cost, and resources resolve in the smoke test because they are view-local.

Exact snippet (paste verbatim at the top of every `Window`/`UserControl`, swapping the element type):
```xml
<UserControl.Resources>
  <ResourceDictionary>
    <ResourceDictionary.MergedDictionaries>
      <ResourceDictionary Source="pack://application:,,,/SenseEase.App;component/Theme/SenseEaseTheme.xaml"/>
    </ResourceDictionary.MergedDictionaries>
    <!-- view-local keys (e.g. BoolToVis) may still live here -->
  </ResourceDictionary>
</UserControl.Resources>
```
(The short form `Source="/SenseEase.App;component/Theme/SenseEaseTheme.xaml"` also works; the full `pack://` form is unambiguous across designer + runtime + test hosts.)

> Do **not** "fix" this by booting `SenseEase.App.App` in the smoke harness — that silently edits a test, which the handover requires be flagged explicitly. The view-local merge touches no test and is preferred.

### 7.3 "No orphan hex" lint test (CI gate)
Add a unit test (mirrors the existing C3 parity-test instinct) that fails CI if a raw color literal appears in any view file outside `Theme/`:
- Enumerate `src/SenseEase.App/**/*.xaml` excluding `Theme/`.
- Regex `#(?:[0-9A-Fa-f]{3}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})\b` on each.
- Also flag named brushes that bypass tokens (`Background="White"`, `Foreground="Black"`, raw `Red/Blue/etc`).
- Fail with the file:line of each orphan. Baseline starts at the current **241 occurrences across 10 files** and ratchets to **0** as views migrate (allow a shrinking allowlist during D2 migration so the gate goes green incrementally rather than blocking the first PR).
- Companion assertion: every keyed `Style`/`Brush` in `Theme/Controls.xaml` resolves against `Colors.Light/Dark/HighContrast` (no missing token) — the design-system analog of the duplicate-source parity test.

---

## 8. Migration crosswalk (current → token)

| Current literal | Found at (evidence) | Token |
|---|---|---|
| `#024AD8` royal blue | `MainWindow.xaml:161/217/237`, `CalibrationCardView.xaml:237–346` | `Accent` `#3A6EA5` |
| `#3D5AFE` | `MainWindow.xaml:178/200` | `Accent` |
| `#E2ECFF` canvas | dashboard bg | `Canvas` `#F6F7F9` |
| `#0A1F5C` navy text | `CalibrationCardView.xaml:230` etc. | `TextPrimary` `#1A2230` |
| `#7B8DB0` | scale sub-labels `:231–338`, idle dot `:579` | `TextSecondary` `#525B6B` |
| `#9AB6E0` | Skip btn `MainWindow.xaml:194` | `TextSecondary` / `TextTertiary` |
| `#0E1320` dark banner | first-run welcome `MainWindow.xaml:178` | `Surface` (re-skin to light card) |
| `#E54747` alert red | discomfort/tier | `ComfortHigh` `#B65C4E` |
| `#F0832E` / `#FF9B6B` orange | rising/CTA | `ComfortRising` `#9A6B22` / `Accent` |
| `#E5F0F8 / #FFF1D6 / #FFE0CC / #FBD0CB` four-hue ramp | `NotificationCardWindow.xaml:253/267/281/295` | single-hue `Scale.Segmented` ramp |
| `#F8FAFF` cell bg | `CalibrationCardView.xaml:225` ×5 | `SurfaceSunken` |
| `CornerRadius="6"` | scale cells | `Radius.sm` (8) |
| `FontSize="10"` | every scale sub-label | `Type.Caption` (12) |
| blue `#5B82C6` shadow | card shadows | neutral `Elev.2` |
| bare `Ellipse` status dots | `MainWindow.xaml:255/543/579` | `Status.Chip` (triple-coded) |

---

*This spec is lockable as-is: every color pairing is contrast-verified to its stated AA/AAA level, every component names the tokens it consumes, and the WPF delivery path provably survives `MainWindowSmokeTests` by keeping tokens out of `App.Resources` and merging them view-locally.*

Evidence files read: `docs/UIUX_UPGRADE_PLAN.md`, `src/SenseEase.App/App.xaml`, `src/SenseEase.App/MainWindow.xaml`, `src/SenseEase.App/Views/NotificationCardWindow.xaml`, `src/SenseEase.App/Views/CalibrationCardView.xaml`, `tests/SenseEase.Tests/MainWindowSmokeTests.cs`.
