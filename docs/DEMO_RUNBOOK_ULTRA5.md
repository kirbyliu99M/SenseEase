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

**Defect B — "stuck at level 5, cannot weaken" (re-diagnosed 2026-07-28 against field state).**

Field capture from the affected Ultra 5 box:
`prefs: enabled=False baseline_tier=1` · `profile: tier=5 paused=True`

There is **no durable on-disk stuck-at-5**. Both freeze mechanisms are session-scoped and a
process restart clears them. The symptom is the product of two things:

1. **The tier-5 seed re-fires on any failed profile load.**
   `ApplyAutoProfilingStartDefaults` runs on *every* launch and seeds `VimsTier = 5` + a 60 s
   hold whenever `_profileExistedAtLoad` is false — i.e. whenever
   `FileHabituationProfileStore.Load()` returns null. That happens if the profile is absent,
   malformed, has an out-of-range `schema_version`, is a refused v1 downgrade, **or is still
   locked after 4 read retries** (`AtomicJsonWriter.TryRead`). AV file-locking reproduces a
   fresh install on a correctly-staged box. The hold does always expire after 60 s.

2. **The strength slider is structurally incapable of weakening protection.**
   `SetIntensityLevel` writes only a *floor*, and display is
   `ResolveDisplayedTier = max(naturalTier, baseline)`. Dragging **below** the engine's
   natural tier is a guaranteed no-op. With natural tier seeded at 5, every attempt to
   weaken did nothing — which is exactly the reported symptom, and it is by-design behaviour
   rather than a glitch.

The field prefs reading `enabled=False, baseline_tier=1` are **the fossil of the user
dragging the slider to minimum** — that is precisely what the `target <= 1` branch writes.
They are evidence of the attempt to weaken, not evidence the floor was innocent.

Two corrections to earlier analysis, recorded so they are not repeated:
- `current_vims_tier: 5` in the profile is **write-only** — never re-applied on load. A stale
  5 there is a fossil, not a live floor.
- `externally_paused: true` is the **shipped default** (`habituation_enabled` defaults false;
  the engine ctor pauses unconditionally). It freezes only the habituation *offset* therapy —
  it does **not** freeze the tier and does **not** stop protection. Do not "fix" it.

Related genuine defect, not the match here: the slider's `target <= 1` branch clears
`PersistentOverlayEnabled` **without** calling `SetAutoLevelHold(false)`, orphaning an
invisible session freeze that pins tier **6**.

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

### The biggest remaining demo risk: AV file-lock

Staging works by making `Load()` succeed. It returns null — and the tier-5 seed fires anyway
— if the profile is still locked after 4 read retries. Trend Micro and similar transiently
lock files under `%LOCALAPPDATA%` at launch, which reproduces a fresh-install seed on a box
that is staged perfectly. **Add an AV exclusion for `%LOCALAPPDATA%\SenseEase` before the
demo**, and launch-and-check more than once — this failure is intermittent by nature, so a
single passing check does not clear it.

### Rehearse this: the slider cannot lower below the auto tier

Display is `max(auto tier, slider)`. If demo content drives the auto tier up, dragging the
slider down does nothing — structurally, not as a bug. If your script has anyone reducing
protection live, rehearse that exact moment or it will read on stage as a broken control.

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
