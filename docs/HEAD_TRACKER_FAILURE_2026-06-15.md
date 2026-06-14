# Head-tracker failure record — 2026-06-14/15

**Status:** UNRESOLVED on the dev build. Repo head-tracker restored to **exact 0611**
(installer commit `4729cea`) and head-tracking work **paused** at Kirby's request
("we'll see what we can do later"). The known-working artifacts are the **0609 and 0611
installers** (`installer/SenseEase_Setup-Alpha_Test.0609.exe` / `...0611.exe`).

## What happened

Kirby reported the head tracker behaving badly after expert testing. Across **many** rounds
of changes it never reached "usable" on the **dev build**, despite the dev source ending up
**byte-identical to the 0611 installer** that *does* work. The failure therefore is almost
certainly **environmental (dev build ≠ installer)**, not in the head-tracker source.

## Timeline of attempts (all on the dev build)

| Attempt | Change | Kirby's verdict |
|---|---|---|
| theme-4 `62e64e8` | sidecar IMAGE→VIDEO mode + multi-face + exposure-hardening | wrong facing, corner-lock, jitter, lag |
| B `2e283a7` | SVD-orthonormalize pose matrix (facing) | — |
| C/D `a824fb4` | deadzone+expo curve + One-Euro Beta 0.05 | — |
| recenter `087e66f` | velocity-gated neutral recenter | — |
| (combined) | the above batch | "barely detects + laggy + doesn't reach the point" |
| regression fix `f0c2463` | removed exposure pin, seed-only recenter, dropped curve | "functions way better, but still far from usable" |
| revert `a8a77d8`/`d283609` | full revert to pure og (IMAGE, One-Euro-only) | "failed to follow" |
| enhancements `7ef65ab` | closest-face + ALPHA_LOW 0.11 + Beta 0.03 | "still flawed" |
| synthesis `25a91ba` | One-Euro retuned to 0609 feel (FcMin 8.0, Beta 0.5) | "still failed" |
| restore (this) | **exact 0611** (FcMin 1.0, Beta 0.015) | paused |

## Verified findings (code-level, high confidence)

1. **Sidecar source is NOT polluted.** `head_tracker.py` is **byte-identical** between the
   current tree and the 0611 installer commit (`git diff 4729cea -- head_tracker.py` is empty).
2. **0609 vs 0611 sidecar tracking logic is identical** — `decompose_yaw_pitch_roll`,
   `YAW_TO_CX_GAIN=0.0233`, `PITCH_TO_CY_GAIN=0.035`, `AdaptiveEma2D`. The only 0609→0611
   sidecar diffs are resilience (CaptureManager re-open, scheduler resync) + the JumpFilter
   extraction (`afc6f3e`/SIDE-01), which made it **strictly more forgiving** — not a cause.
3. **The one real source difference between 0609 and 0611 is the C# pump:**
   - 0609 (`f819617`): a fast **15 ms half-life lerp** → snappy follow.
   - 0611 (`4729cea`): the **One-Euro** one-clock filter at `FcMin=1.0`, `Beta=0.015`
     (≈159 ms rest time-constant; barely opens even at 120°/s) → heavily smoothed.
   A 4-agent comparison computed `FcMin 8.0 / Beta 0.5` to replicate the 0609 lerp feel.
4. **But the One-Euro retune STILL failed on the dev build** — which means the dev failure is
   **not** explained by the pump tuning alone.

## Leading theory: dev build ≠ installer (environmental)

The dev build failed to follow across **every** source variant tried — pure-og, exact-0611
settings, the 0609-equivalent retune, and the enhanced builds — **while the 0609 and 0611
installers both follow**. Since the dev source is now proven identical to the 0611 installer,
the difference must be in the **build/run environment**:

1. **Debug vs Release.** The dev build runs **Debug** (unoptimized); the installers are
   **Release**. The 165 Hz composition pump + software overlay composition + sidecar IPC may
   lag enough in Debug to break the follow feel. *(Not yet isolated.)*
2. **PyInstaller venv drift.** The sidecar exe is rebuilt from the local `.venv`. Repeated
   rebuilds may pull a different `mediapipe`/`opencv`/`numpy` than the installer's original
   build → a behaviourally-different frozen exe. Evidence: the current dev sidecar exe is
   `18,545,302 B` vs the 5/31 staged exe `18,539,950 B` (different builds). *(Not yet isolated.)*
3. **Runtime wiring.** The dev app could be loading a stale/other sidecar, or differ in
   head-tracking-enabled / camera state. *(Not yet isolated.)*

## Ruled out

- Sidecar source (identical to 0611), pose math/signs/gains (identical), the JumpFilter
  (0611 strictly more forgiving), and "it's just the One-Euro tuning" (a 0609-equivalent
  retune still failed).

## Next steps (deferred — for "later")

1. **Isolate Debug-vs-Release:** run `dotnet run -c Release --project src/SenseEase.App` and
   A/B vs the Debug dev build. If Release follows like the installer, the dev Debug build is
   the culprit (not the code).
2. **Check venv drift:** capture `pip freeze` from `.venv` and compare `mediapipe`/`opencv-python`/
   `numpy` versions to whatever the 0611 installer was built with; recreate the venv from a
   pinned `requirements.txt` if drifted, then rebuild.
3. **Isolate the exe:** extract the 0611 installer's `head_tracker.exe` (e.g. `innoextract`)
   and run it directly against the dev app vs the local rebuild.
4. **Live `--diag`:** run the sidecar with `--diag` during a dev session to confirm it detects
   (face_detected), the achieved frame rate, and the raw cx/cy it emits — distinguishes
   "not detecting" from "detecting but pump-smoothed".

## Current state

- Repo head-tracker = **exact 0611** (`SenseDefaults` FcMin=1.0/Beta=0.015 + 0611 sidecar
  source), committed. Builds green (1263 non-overlay + 147 overlay).
- Pure-og baseline also tagged in history at `d283609`.
- **Sure working head-tracker = the 0609/0611 installers.** Dev-build root-cause deferred.
