# Demo runbook — Ultra 5 box (Beta V1.3.2, OV-Cpp)

Branch: `demo/ultra5-parity`. **Scope: workaround only.** Nothing here fixes a defect;
it stages a box so two known defects stay off-stage. The fixes are separate decisions.

## The two defects

**Defect A — protection level does not change coverage (universal, ships everywhere).**
`VimsTier` (1–12) maps to peak alpha, RGB tint, grid opacity and cell strength — **zero
geometry**. The clear aperture is `featherStart × RestCornerDenom(aspect)`, and
`featherStart` answers only to `Shader.FeatherWidth`. `FovRadiusPercent` computes, logs,
displays and feeds OFI attenuation, but `OverlayWindow.xaml.cs` contains **zero** `Fov`
references — `/fov` is inert on screen.

All four installer variants ship **byte-identical** `SenseEase.App.dll` / `SenseEase.Core.dll`
(MD5-verified across `dist/beta-v1.3.2-{amd,auto,cpp,py,base}`). There is no per-variant
compile and no backend branch in the renderer. **The Ultra 7 and AMD boxes have this defect
too** — it is latent there because they sit at a low tier, where the frozen aperture is drawn
near-transparent.

**Defect B — durable stuck-at-level-5 (this is what made Ultra 5 look broken).**
On a genuine fresh install `ApplyAutoProfilingStartDefaults` seeds `VimsTier = 5` and holds
it 60 s. Displayed tier is `Math.Max(naturalTier, PersistentOverlayBaselineTier)`, so during
the hold **the slider cannot go below 5**. Any nudge in that window — off 5 and back, or the
± pills — makes `SetIntensityLevel` persist `persistent_overlay_baseline_tier = 5` and
`persistent_overlay_enabled = true` in `%LOCALAPPDATA%\SenseEase\user-preferences.json`.
That is a hard floor on every later boot, and nothing automatic lowers it.

The user's attempt to weaken protection is what makes it permanent. That is the whole
"stuck at level 5, had to reset the app" report.

## Pre-demo staging

App **closed**, then:

```bash
powershell -ExecutionPolicy Bypass -File scripts\stage-demo-box.ps1
```

Dry run first with `-DryRun`; `-Force` replaces an existing profile. It backs up anything it
touches to `*.pre-demo-bak` (first backup only, never clobbered).

What it does:
1. Writes a post-onboarding `habituation_profile.json` (`ADAPTING`, tier 1, engine **RUNNING,
   not paused**). `ApplyAutoProfilingStartDefaults` early-returns when a profile exists, so
   the tier-5 seed, the 60 s hold and the capture window never happen.
2. Stamps `first-run-complete.flag` with **now** — suppresses the welcome banner, and since
   the weekly recap debuts in the *second* week of use, guarantees it cannot fire mid-demo.
3. Surgically clears `persistent_overlay_enabled` / `persistent_overlay_baseline_tier`,
   preserving language, theme, head-tracking and everything else.

**Acceptance check — do not skip.** Launch and confirm it reads **Lv.01 at rest, not Lv.05**.
If it reads Lv.05, staging did not take; do not proceed.

Staging a post-onboarding profile is legitimate for a *demo* — it is the state a real user
occupies after onboarding. It is **not** evidence any bug is fixed, and must never be cited
as such. (An earlier field test was invalidated exactly this way, by a staged profile that
was also `externally_paused: true`. This script sets paused **false** for that reason.)

## On stage

**The lever is `/feather`, typed in the chat box.**

| Command | featherStart | Effect |
|---|---|---|
| `/feather 0` | 0.50 | widest aperture |
| `/feather 50` | 0.35 | baseline |
| `/feather 100` | 0.15 | tightest aperture |

A ~3.3× swing in clear-hole radius — the largest visible geometry lever in the build. Parsed
by `ShaderCommandLexicon` *before* the LLM path, so it is instant, needs no model round-trip,
and is not consent-gated. Integer only (`/feather 90.5` and `/feather 90%` reject). The token
is **ASCII in both languages** — type `/feather 90` even on the Chinese UI. Persists nothing;
gone on restart. `/reset shader` (zh 「重設遮罩」) snaps back to 0.50 between takes.

Optional, set before launch with the app closed:
- `head_tracking_follow_mode: "FullFollow"` in `user-preferences.json` — the aperture *centre*
  then tracks head pose across the full screen. Real geometry motion; size stays fixed.
- Pre-seed a starting aperture by appending one `FeatherWidth` line with `"Source":"user"` to
  `adjustments.jsonl` (back it up first). Survives restart and is immune to selector moves.

### Do not touch live

- **Popup-bar intensity slider and ± pills.** They change darkness only — never geometry — and
  they re-persist `persistent_overlay_enabled = true` plus a baseline tier, i.e. they re-arm the
  exact floor this runbook clears, and mark in-session consent. Recoverable (drag all the way
  to 1 clears both keys) but there is no reason to touch it.
- **Free-text "make the edge softer".** Goes down the LLM path and appends a *persisted* delta
  to `adjustments.jsonl`, replayed on next boot, with unpredictable magnitude.
- **`/fov`.** Confirmed inert on screen.

## What the build supports being said about it

Levels genuinely change **how dark** the mask is. They do **not** change **how much screen it
covers**. Any script line implying "higher levels narrow your field of view" does not survive
contact with this build — an audience watching the level move while the masked area stays put
reads it as the feature not working. Demo intensity via level, and coverage via `/feather`,
and the demo stays honest.

## After the demo

Restore from the `*.pre-demo-bak` files if you want the box back as it was. The staged state
is otherwise harmless to leave in place.

## Related

- Root-cause analysis and the three remediation forks for Defect A are open decisions — see the
  session notes; nothing in `src/` has been changed on this branch.
- `docs/TECHNICAL_WHITEPAPER.md` §8.5 documents an "Asymmetric Radius Envelope" renderer that
  was **deleted** (`3762a42`, `e5d418e`). `_smoothedRadius` no longer exists and the
  `OverlayRadius*HalfLifeMs` constants are dead config. Do not plan work against §8.5 — a prior
  field session burned two IL-patch rounds on that stale section.
