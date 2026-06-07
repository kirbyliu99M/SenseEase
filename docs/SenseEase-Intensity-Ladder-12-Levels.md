# SenseEase Intensity Ladder — 12 Levels (canonical SSOT)

> **Single Source of Truth (SSOT)** for the **native 12-level intensity scale**.
> Adaptive Fit decision **Q2 = B** (2026-06-06).
>
> **Status: PROVISIONAL** — the level values, band split, and the discretizer below
> are canonical and match the live engine **today**, but the mapping is provisional
> *until M1 anchors the OFI norm-factors*. M1 calibration may re-anchor the
> `Pressure / TriggerPressure` normalization; the **ladder shape (12 rungs, RGB,
> alpha, 5級 band split, hue ramp) is locked** and will not change under M1 —
> only the pressure-→-level normalization input may.
>
> This document is the canonical reference cited by:
> - `docs/SenseEase-Adaptive-Fit-Layer-v1.md` (≈ lines 27 / 167)
> - `docs/Alpha_1_MASTERPLAN.md` §13.1 (B1)
>
> Everything here is **mirrored from code, not invented**. Verified against the live
> engine on 2026-06-06 (see [Source-of-truth & drift](#source-of-truth--drift-check)).

---

## 1. Discretizer (Pressure → level 1..12)

The continuous pressure signal is mapped to a real-valued level, then snapped to a
discrete tier by the engine's hysteresis/debounce logic.

```
t      = clamp(Pressure / TriggerPressure, 0, 1)
levelF = 1.0 + 11.0 * t          // real-valued level in [1.0, 12.0]
level  = round/hysteresis(levelF) // discrete integer 1..12 → state.VimsTier
```

- **`t = 0`** (no pressure)        → `levelF = 1.0`  → **L1** (day-1 floor, 0% mask).
- **`t = 1`** (`Pressure ≥ TriggerPressure`) → `levelF = 12.0` → **L12** (peak).
- The integer tier is held with hysteresis + debounce (anti-flicker) and exposed as
  `state.VimsTier`; a fast-onset bypass snaps directly to `round(levelF)` on a genuine
  surge. The discrete tier — not `levelF` — drives every visual below.

Source: `src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs`
(`UpdateVimsTier`, ≈ lines 1828–1837):

```csharp
double t      = Math.Clamp(Current.Pressure / SenseDefaults.TriggerPressure, 0.0, 1.0);
double scale  = _adaptiveFitSelectorEnabled                 // set0 ⇒ 11.0 (native 12-rung span)
    ? SetGrid.Curve(_activeSet).ForLevel(_vimsTier).Scale
    : (SetCurve.LevelCount - 1);
double levelF = 1.0 + (scale * t); // canonical: levelF = 1 + 11·t (1..12 — Adaptive Fit Q2=B)
```

The discretizer GAIN is the active Adaptive-Fit set's `Scale`; all 50 sets currently carry
`Scale = 11.0`, so the canonical mapping `levelF = 1 + 11·t` holds today. A future per-set
`Scale` would change the gain per set (it is honoured end-to-end as of the 2026-06-08 wiring).

---

## 2. The 12-level ladder (canonical table)

Two distinct visual channels are driven from the same level. Keep them separate:

- **MASK gradient** — the FOV dimming overlay (`OverlayWindow.VimsLevels[L-1]`).
  Ramps **neutral gray → cool-shifted near-black**. This is the *dimming*, not a hue
  indicator. RGB + per-level peak alpha below.
- **5級 ambient HUE** — the calm-blue level indicator on the bar / tray / card
  (`TierColorPalette`). Projected from the 12 levels to 5 bands. See §3.

| Level | Mask RGB (`OverlayWindow.VimsLevels`) | Mask hex | Mask alpha | 5級 band | Ambient hue |
|:-----:|:--------------------------------------|:--------:|:----------:|:--------:|:-----------:|
| **L1**  | rgb(102, 102, 102) | `#666666` | **0.00** | 極高 VeryHigh | `#9BB4D4` |
| **L2**  | rgb(102, 102, 102) | `#666666` | 0.14 | 極高 VeryHigh | `#9BB4D4` |
| **L3**  | rgb(85, 85, 85)    | `#555555` | 0.25 | 高 High      | `#6F93C2` |
| **L4**  | rgb(68, 68, 68)    | `#444444` | 0.34 | 高 High      | `#6F93C2` |
| **L5**  | rgb(68, 68, 68)    | `#444444` | 0.42 | 中 Medium    | `#4A77B5` |
| **L6**  | rgb(51, 51, 51)    | `#333333` | 0.50 | 中 Medium    | `#4A77B5` |
| **L7**  | rgb(34, 34, 34)    | `#222222` | 0.58 | 中 Medium    | `#4A77B5` |
| **L8**  | rgb(20, 20, 24)    | `#141418` | 0.66 | 低 Low       | `#345E97` |
| **L9**  | rgb(8, 8, 14)      | `#08080E` | 0.73 | 低 Low       | `#345E97` |
| **L10** | rgb(4, 4, 10)      | `#04040A` | 0.80 | 低 Low       | `#345E97` |
| **L11** | rgb(2, 2, 8)       | `#020208` | 0.86 | 極低 VeryLow  | `#243F6B` |
| **L12** | rgb(0, 0, 4)       | `#000004` | 0.92 | 極低 VeryLow  | `#243F6B` |

> The per-row **EN label** column was removed (2026-06-07): it carried the pre-correction
> "Most Natural … Most Steady" framing. The authoritative user-facing 5級 labels
> (Very Calm … Very Dynamic) now live in **§3** alone, to avoid the duplication that drifted.

Notes on the mask channel:
- **L1 alpha = 0.00 is a pinned floor** (day-1 protection). Asserted by
  `D6DayOneProtectionTests.BaselineTier1_FloorsToOne_AndIsZeroAlphaAtRest` — do not raise it.
- **L5 is the neutral default** resting level.
- **L12 is the peak** (deepest mask).
- Alpha climbs monotonically `0.00 → 0.92`.
- **Cool-shift begins at L8** (blue channel `+4..+6` above red/green): a pure-black
  mask reads as a flat gray wash on bright backgrounds, so the slight blue tint keeps
  the deep levels reading as a deliberate "out-of-focus dimming," drawing the eye to
  the clear center even on white content.

---

## 3. 5級 動態感 projection (the user-facing ambient indicator)

The 12 mask levels are projected to the **5 動態感 級** the user actually sees on the
persistent bar, the system-tray icon, and the notification card. This is a separate,
calm-blue **HUE** channel — it is *not* the gray mask gradient.

> **⚠️ Polarity CORRECTED 2026-06-07 (unified-measurement directive).** The user-facing
> 動態感 labels were flipped: **low tier = calm (極低 / Very Calm), high tier = dynamic
> (極高 / Very Dynamic)** — superseding the earlier reversed "L1–2 = 極高 / Most Natural"
> framing (the per-row EN-label column was removed from §2's table for this reason). Internal
> band IDs + hue ramp + α are UNCHANGED (the ramp now reads light = calm, deep = dynamic).
> The 5級 status is the ONLY status shown to users; the 12-level scale is internal/advanced.
> Authoritative: Alpha_1_MASTERPLAN §13.6.

| 5級 band (internal) | Levels | Ambient hue | EN label (user) | 中文 (user) |
|:--------------------|:------:|:-----------:|:----------------|:-----------:|
| **VeryHigh** | L1–L2  | `#9BB4D4` (airy blue-grey)  | Very Calm    | 動態感 · 極低 |
| **High**     | L3–L4  | `#6F93C2` (light calm blue) | Calm         | 動態感 · 低 |
| **Medium**   | L5–L7  | `#4A77B5` (mid calm blue)   | Moderate     | 動態感 · 中 |
| **Low**      | L8–L10 | `#345E97` (deeper blue)     | Dynamic      | 動態感 · 高 |
| **VeryLow**  | L11–L12| `#243F6B` (deep calm slate) | Very Dynamic | 動態感 · 極高 |

- **Default band is 中 Medium (L5).**
- Band boundaries match `TierColorPalette.BandForTier` exactly:
  `≤2 → VeryHigh`, `≤4 → High`, `≤7 → Medium`, `≤10 → Low`, `else → VeryLow`
  (defensive clamp: level ≤ 0 → VeryHigh, level ≥ 13 → VeryLow).

### Calm-cool ramp (plugin-grounded, MasterPlan §13.6)

A **deeper blue = more stabilized**. There is deliberately **NO alarm red**: the
ui-ux-pro-max palette DB reserves red for `Destructive` only, and every calm product
class (Mental Health / Meditation / Medical) uses calm blue/teal. The ramp is
harmonised with the app Accent `#3B6FC4` and the non-alarming "動態感" framing.

> **Two channels, do not conflate:** the **MASK gradient** (gray → cool-near-black,
> the dimming) is distinct from the **5級 ambient HUE** (the calm-blue level
> indicator). One dims the screen; the other quietly signals the current level.

---

## 4. FOV / mask / grid floors and notes

| Quantity | Floor / formula | Meaning |
|:---------|:----------------|:--------|
| **FOV floor** | **15%** | Minimum clear-zone radius — the center is never fully occluded. |
| **Mask floor** | **0.20** | Minimum effective mask strength once engaged. |
| **Grid fraction** | **`(level − 1) / 11`** | Peripheral grid intensity scales linearly L1→L12 (0.0 at L1, 1.0 at L12). |

- The FOV clear-zone radius is clamped to `[15, 100]` and composed from the
  pressure-driven radius plus the user feedback offset plus the habituation offset.
- The mask color/alpha cross-fades between discrete tiers via per-tick exponential
  smoothing (~250 ms convergence) so transitions feel continuous while the engine
  still snaps `state.VimsTier` discretely (hysteresis prevents sub-tier flicker).

---

## 5. Source-of-truth & drift check

Verified against the live engine on **2026-06-06**:

| Element | Source of truth | Verified |
|:--------|:----------------|:--------:|
| Mask RGB + alpha (L1–L12) | `src/SenseEase.App/Views/OverlayWindow.xaml.cs` → `VimsLevels` | ✅ exact match |
| 5級 hue + band split + EN labels | `src/SenseEase.App/Views/TierColorPalette.cs` → `BandForTier` / hue consts / `FeelLabelEn` | ✅ exact match |
| Discretizer `levelF = 1 + Scale·t` (set0 `Scale` = 11.0) | `src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs` → `UpdateVimsTier` (gain = active set's `Scale`) | ✅ exact match |
| L1 alpha = 0.00 floor | `D6DayOneProtectionTests.BaselineTier1_FloorsToOne_AndIsZeroAlphaAtRest` | ✅ pinned |
| 5級 ARGB | `TierColorPaletteTests.BrushForTier_MatchesSpecArgb` | ✅ pinned |
| Per-set `Scale` / `GridVisibility` honoured (no longer computed-but-ignored) | `AdaptiveFitGridTests` (uniformity pins) + `AdaptiveFitWiringTests` (set0 discretizer parity, grid re-seed) | ✅ wired 2026-06-08 |

**Drift found: none.** Every value in §2–§4 is byte-for-byte / formula-for-formula
identical to the code as of this verification. If the code tables change, update this
SSOT in the same change (this file is the canonical reference, not a copy).

### Read-only contract

This document is **descriptive**. It does not introduce new constants — it mirrors the
code. The authoritative numeric tables live in `OverlayWindow.VimsLevels` and
`TierColorPalette`; this SSOT exists so the spec and MasterPlan have one stable place to
point at. Edit code first, then reflect it here.
