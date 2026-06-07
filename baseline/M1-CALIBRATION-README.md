# M1 — WGC norm-factor calibration protocol

Closes the **reducible half of backlog D1**. This is the offline pipeline that
turns a real per-tick raw-MAD trace into a recommended
`SenseDefaults.WgcMadNormFactor`. It is **advisory only** — the live norm-factor
is unchanged until Kirby reviews a real capture and edits the constant by hand.
No data in this folder is fabricated.

## Why

`WgcSensingProvider.OnTick` derives optical-flow index (OFI) from the per-frame
pixel mean-absolute-difference:

```
rawOfi  = ComputeMad(current, prev) * SenseDefaults.WgcMadNormFactor;  // factor currently 4.0
clamped = Math.Clamp(rawOfi, 0, 150);
```

The factor `4.0` is a pre-real-data placeholder (see the `SenseDefaults`
comment: *"Tune after first real WGC data: idle desktop ≈ MAD 0-1, scrolling
video ≈ MAD 15-40"*). M1 replaces that guess with a number derived from a real
capture on Kirby's actual hardware/monitor.

The offline reduction mirrors the live formula exactly
(`MadCalibrationReducer.Reduce` = `mad × normFactor`, clamp `[0,150]`), so a
reduced row predicts the OFI band a candidate factor would yield in production.
If the live MAD→OFI formula or clamp range changes in `WgcSensingProvider`,
update `src/SenseEase.Core/Sensing/MadCalibration.cs` to match.

## The 5 canonical scenes

Capture each scene for ~20–30 s of representative activity. Use the snake_case
`scene_label` values verbatim so the recommender can match targets:

| `scene_label`      | What to do                                              | Expected MAD character     |
|--------------------|--------------------------------------------------------|----------------------------|
| `idle_desktop`     | Desktop visible, no input, no animations               | ~0 (static) — does NOT constrain the factor |
| `word_scroll`      | Scroll a text document (Word / browser article) steadily | low–moderate, localised   |
| `video_1080p`      | Play a 1080p video full of normal motion (not a slideshow) | moderate, sustained       |
| `fullscreen_game`  | Fullscreen game with camera/world motion               | high, sustained            |
| `scene_cut`        | Content with hard cuts (trailer / fast edits)          | spiky, high p95            |

`idle_desktop` is captured for completeness/sanity but a static screen has
mean MAD ≈ 0, so `NormFactorRecommender` skips it when intersecting admissible
factor ranges (a zero-motion scene can't constrain a multiplier). The
motion scenes (`video_1080p` / `fullscreen_game` / `scene_cut`) are what pin the
factor.

## Step 1 — capture a raw-MAD trace in-app

The in-app raw-MAD capture path is gated behind an env var
(**added by the engine-integrity orchestrator**, not this M1 work):

```
set SENSEEASE_MAD_CALIBRATION=1   &  run SenseEase.App.exe
```

When set, the WGC pipeline writes a per-tick raw-MAD trace (the MAD value
*before* the norm-factor multiply / clamp) to a trace file. Run one scene at a
time; note the `scene_label`, the wall-clock duration, and the trace file/segment
for each. Unset the env var (or restart) to return to production behaviour — the
trace path is read once at `Start()`, so toggling it mid-session requires a
restart, identical to the existing `SENSEEASE_WGC_BAR_VALIDATION` trace.

> Until that capture path lands, this protocol is dormant: the reducer/recommender
> and their tests are complete and green, but there is no real trace to feed them,
> so `baseline/wgc-mad-calibration.csv` ships **header-only**.

## Step 2 — reduce a trace to one row per scene

Feed each scene's raw MAD samples to `MadCalibrationReducer.Reduce`:

```csharp
using SenseEase.Core.Sensing;

MadSceneStats row = MadCalibrationReducer.Reduce(
    sceneLabel: "video_1080p",
    durationS:  27.0,
    rawMads:    samples,          // IReadOnlyList<double>, raw MAD per tick (0–255)
    normFactor: 4.0);             // the candidate factor to EVALUATE (current live value)

string csvLine = MadCalibrationReducer.ToCsvRow(row);   // append under the header
```

- `mean_mad` = arithmetic mean of the raw MAD samples.
- `p95_mad`  = **nearest-rank** 95th percentile (a real observed sample, never
  interpolated — calibration wants a true worst-case-ish value).
- `derived_ofi_mean` / `derived_ofi_p95` = `mad × normFactor` clamped to
  `[0,150]` — the OFI band that factor would produce live.

Append the reduced rows under the header in
**`baseline/wgc-mad-calibration.csv`** (header == `MadCalibrationReducer.CsvHeader`).
`#`-comment lines and blank lines are allowed and skipped by `ParseCsv`; record
the capture date and the norm-factor used as `#` comments for traceability.

## Step 3 — recommend a norm-factor

Decide a **target OFI band** per motion scene (the OFI range you want that
scene's *mean* to land in — e.g. `video_1080p` → roughly the mid OFI band,
`fullscreen_game`/`scene_cut` → upper band but below the 150 clamp). Then:

```csharp
IReadOnlyList<MadSceneStats> scenes = MadCalibrationReducer.ParseCsv(
    File.ReadLines("baseline/wgc-mad-calibration.csv"));

var targets = new (string SceneLabel, double TargetOfiMin, double TargetOfiMax)[]
{
    ("video_1080p",     /*min*/ 20, /*max*/ 60),
    ("fullscreen_game", /*min*/ 60, /*max*/ 110),
    ("scene_cut",       /*min*/ 50, /*max*/ 120),
};

NormFactorRecommendation rec = NormFactorRecommender.Recommend(scenes, targets);
```

For each matched scene the admissible factor range that maps its **mean MAD**
into the target band is `[TargetOfiMin / meanMad, TargetOfiMax / meanMad]`. The
recommender **intersects** these ranges across scenes and returns:

- `RecommendedFactor` — midpoint of the intersection (`0` on conflict).
- `FactorRangeLow` / `FactorRangeHigh` — the admissible window.
- `HasConflict` + `Notes` — set when no single factor satisfies every scene
  (e.g. the video band wants a low factor while the game band wants a high one).
  On conflict, widen a target band or re-capture the offending scene; **do not**
  pick an out-of-range factor.

## Step 4 — Kirby reviews and sets the factor (manual)

`NormFactorRecommender` **recommends only — it never mutates `SenseDefaults`.**
Kirby reviews `RecommendedFactor` + `Notes` against the per-scene derived-OFI
columns, then edits `src/SenseEase.Core/Configuration/SenseDefaults.cs`:

```csharp
public const double WgcMadNormFactor = <recommended>;  // M1 calibrated <date>, was 4.0
```

Until a real capture is reviewed, **the norm-factor stays at its current value.**

## Companion file — `cursor-velocity-calibration.csv`

Header-only scaffolding for the Phase-2.1 cursor-velocity OFI path
(`CursorVelocityProvider`), kept alongside the WGC table so both sensing-source
calibrations live in one place. Columns:

```
gesture_label,distance_px,duration_ms,derived_speed_pxs,derived_ofi
```

Mirrors the live cursor derivation:
`speed_pxs = distance_px / (duration_ms / 1000)`, then
`derived_ofi = Math.Clamp(speed_pxs / SenseDefaults.CursorVelocityNormFactor, 0, 150)`.
No reducer/recommender is shipped for the cursor path in M1 (WGC is the primary
real-sensing source); this file is a manual-capture scaffold only and ships
header-only (no fabricated rows).

## Files

- `src/SenseEase.Core/Sensing/MadCalibration.cs` — `MadSceneStats` record +
  `MadCalibrationReducer` (Reduce / ToCsvRow / ParseCsv).
- `src/SenseEase.Core/Sensing/NormFactorRecommender.cs` — `NormFactorRecommendation`
  record + `Recommend` (pure, advisory).
- `tests/SenseEase.Tests/MadCalibrationTests.cs` — xUnit coverage.
- `baseline/wgc-mad-calibration.csv` — reduced WGC table (header-only until capture).
- `baseline/cursor-velocity-calibration.csv` — cursor scaffold (header-only).
