# Beta Plan — Overlay Motion Coherence (multilayer-halo → single coherent field)

**Status:** PLANNED for Beta. No code changes in Alpha (POC time constraint).
**Alpha baseline / rollback point:** commit `f381a43` — "Feathering halo fix (Phase 1): asymmetric radius envelope." UVHS-confirmed: visual fatigue greatly reduced, no lag. This plan builds **on top of** that commit; if anything below disappoints, revert to it.
**Scope:** pure rendering / motion. Touches none of the locked subsystems (see Constraints).

---

## 1. Context & problem

The head-tracked overlay (peripheral FOV dimming mask + screen-locked Chang-Hain anchor grid) feels great while still, but during head movement the user reports **"a lot of layers moving — heavy and unnatural"** (a multilayer / halo percept).

A multi-domain field consult (VR comfort-vignette research, vection/VIMS vision-science, foveated/gaze-contingent rendering, alpha-compositing theory, Gestalt perception; web-sourced 2026-06-08) returned a clear verdict: **this is a textbook, named failure mode, and the field's consensus fix is well-established.**

### Root cause (field framing)
A Gestalt **common-fate violation** plus **perceptual scission**: the effect is composited from **3+ independently-shaped, independently-timed translucent strata**, so during motion they appear to move semi-independently and the eye reads them as stacked layers. The separate near-black cell-fill additionally acts as a manufactured occlusion/halo depth cue (scission).

Verified in code (`src/SenseEase.App/Views/OverlayWindow.xaml.cs`, symbols are the durable anchors):
- **Three geometries:** `FovMask.Fill` (`_fovBrush` `RadialGradientBrush`, center-following, scalar radius rescaled per frame) · the full-screen `_cellFillRect` (`CellFillBrush` `#08080A`@120) · the white grid `Line`s under a **separate** `_gridOpacityMaskBrush` that *mirrors but does not share* the FOV brush.
- **Four divergent time-constants:** center 15 ms half-life lerp (`MainWindow.OnCompositionRendering`) · radius asymmetric 25 ms / 600 ms (`ApplyHeadPoseToBrush`, `SenseDefaults.OverlayRadius{Attack,Release}HalfLifeMs`) · grid `GridCanvas.Opacity` EMA α=0.85 (~1.7 s) + a 280 ms `DoubleAnimation` · mask color/alpha `MaskSmoothingFactor`=0.25 (~250 ms).
- **Scalar-radius rescaling:** `radius = max(0.85, farthestCorner·1.10)` recomputed every ~165 Hz frame (`ApplyHeadPoseToBrush`) — the whole gradient "breathes" as a second moving variable, because a single `RadialGradientBrush` radius cannot corner-cover without rescaling.

### The reference (why it feels coherent)
The known-good WebGL prototype (`…/SenseEase Workspace/src/core/DitheringShader.js`) renders mask **and** grid in **one per-pixel fragment pass**: corner-normalized per pixel, mask-vs-grid composited mutually-exclusively, one set of uniforms per frame → exactly one moving boundary, one alpha per pixel. Every production VR FOV restrictor does the same (one mesh / one shader, never stacked animated UI layers).

### Key correction from the consult (validates current locks)
**Mask depth is NOT the problem and is within field norms.** Production FOV restrictors go to near-opaque black at full restriction; the 0.92 peak is normal. The "heavy in motion" complaint is **edge-motion-coherence, not darkness** → **keep the depth locks; do not chase low-contrast.** (The "~0.4 opacity" comfort figure in the literature is for the *faint rest-frame grid*, not the mask — and the grid is already screen-locked + faint, which the literature validates.)

### Validated-correct existing choices (do NOT regress in Beta)
Asymmetric fast-attack/slow-release · screen-locked faint grid · wide smoothstep feather (0.35→0.95) · ≥0.20 always-on floor (deliberately stricter than VR norms — clinical choice).

---

## 2. Constraints (carry into Beta)

- Mask peak depth **≥ 0.20**; do **not** change the 12-tier `VimsLevels` depth table (peak 0.92 / floor 0.00), the 5-level 動態感 labels, the **grid opacity curve** (`GridCeiling`/`tierFraction`, test-pinned by `GridCeilingTests`), or the unified 度量衡.
- **No dithering**, **no position-freeze**, **no motion-time depth reduction**, **no hard edges**.
- Overlay is `AllowsTransparency=true`, topmost, click-through → **CPU software composition** (no GPU airspace). OBS-3: no new sidecar egress.
- Both options below are **topological** (how pixels are composited) and **preserve every locked number byte-for-byte**.

---

## A1 — Interim: unify the motion model (medium effort, WPF-only, no shader)

**Goal:** kill the common-fate "swim" by making every component move on **one clock from one smoothed pose value** — without merging the surfaces yet. Highest-leverage in-place change; field-named as independently load-bearing. Shippable as the first Beta increment (or an Alpha hotfix if time allows).

**Design**
1. Funnel head pose through **one** smoothing pass — a single **One-Euro filter** (start `fcmin ≈ 1.0 Hz`, `beta ≈ 0.007`) or one critically-damped lerp — producing one `(cx, cy)` per frame.
2. Derive **center, radius, mask alpha, cell shading, and grid visibility from that single value, in the same frame**, on the **same time-constant**.
3. **Remove the divergent clocks:** the 600 ms radius-release on a different clock than the 15 ms center-follow is itself a "two things moving" cue. Either drop the separate radius envelope or drive radius from the *same* smoothed center (keep the asymmetric attack/release only if the center shares it).
4. Fold `GridCanvas.Opacity`'s EMA(α=0.85)+280 ms animation onto the same clock so grid brightness stops lagging the mask.

**Touch points (Beta):** `OverlayWindow.ApplyHeadPoseToBrush`, `MainWindow.OnCompositionRendering`, `SenseDefaults` (one filter constant block). Grid stays screen-locked; `GridCeiling` curve untouched.

**Fixes:** the inter-stratum swim/parallax that appears only in motion (the "fine while still, heavy in motion" signature). **Does not fix:** the scission from the separate cell-fill, nor 8-bit banding — those need A2.

**Tests/UVHS:** extend `OverlayHeadPoseTests` (one-clock convergence; no per-component lag at a simulated frame cadence). UVHS: head sweep at L1–L12 → "one thing moving," no layer swim.

**Risk:** low–medium. Pure timing/derivation change; reversible to `f381a43`. Watch that unifying timing doesn't slow center-follow (keep it responsive — the follow latency is what 72ec9e7 originally fixed).

---

## A2 — Full single-pass field (the complete cure)

**Goal:** render mask + grid as **one per-pixel field** — the faithful WPF analogue of the reference `DitheringShader`. Eliminates the multilayer percept, the scission halo, and the scalar-radius "breathing" in one move; mitigates (not eliminates) banding.

**Field math (all inside one pass):**
- Per-pixel **corner-normalized radial coordinate**: `r = dist(px, center) / distanceToFarthestCorner(center)` — guarantees corner coverage with **no animated global radius**.
- **Continuous smoothstep** falloff (`s=t²(3−2t)`; optionally **smootherstep** `6t⁵−15t⁴+10t³` to further suppress Mach contour). Feather band a **fixed fraction of the normalized coordinate** (constant angular width — no breathing). Preserve 0.35→0.95 envelope shape and the `VimsLevels` depth/peak/floor as the falloff **output**.
- **Mutually-exclusive mask-vs-grid**: where mask alpha is below threshold, draw the **faint, screen-locked** grid (`mod(px, 40px)`); else the mask. One alpha per pixel → no stacking, no third moving stratum. `GridCeiling` stays the ceiling driver.

> **CORRECTION (UVHS 2026-06-11).** The "where mask alpha is below threshold, draw the grid" sentence above is **WRONG as a spatial instruction** and was taken literally in the first A2 field — it put the anchor grid in the *clean centre*, inverting the layout. The **authoritative spatial layout is the 3762a42 presentation**: the faint world-locked grid lines appear in the **PERIPHERY**, sharing the FOV mask's exact radial window (the reference's `_gridOpacityMaskBrush` was the *inverse* of the FOV mask — clear centre, revealed periphery — sharing identical center/radius/feather geometry), while the **CENTRE is CLEAN** (no grid, no dim). "One alpha per pixel" meant *no separate animated strata* (a periphery grid-line pixel renders the white line **blended over** the mask dim — one output pixel), **not** "grid where the mask isn't." Line **positions** stay world-locked (`mod(px, GridSizePx)`, never offset by the mask centre); only the *visibility window* follows the head. The grid term in `OverlayWindow.ComputeField` was reworked to scale the line alpha by the SAME shared radial window the FOV mask uses (so grid ≈ 0 in the clean centre, faint in the dimmed periphery); grid alpha still follows the `GridCeiling` × tier-fraction × `GridVisibility` pipeline byte-for-byte. UVHS 2026-06-11 caught the inversion.
- **Banding mitigation (best-effort, no dither):** do the dark-ramp interpolation in **linear light / gamma-shaped t (~2.2)** before 8-bit encode; keep non-pure-black (L12 already `(0,0,4)`).

**Two WPF-legal realizations (pick in Beta after a perf spike):**
- **(a) `ShaderEffect` (HLSL):** one host `Rectangle`, port the reference fragment shader. **Must compile to `ps_2_0`** — WPF's effect compiler rejects `ps_3_0`/`ps_2_a`/`ps_2_sw`. The mask+grid math is ALU-only (no loops/branches) and fits the 64-ALU/32-tex `ps_2_0` budget. Adds an **HLSL→`.ps` build step** (`fxc` from the Windows SDK, embedded as a resource). Closest to the reference, cheapest per pixel.
- **(b) Per-frame `WriteableBitmap`:** compute premultiplied BGRA in C# each pump tick (`Lock`/`AddDirtyRect`/`Unlock`), present as one `Image`. No build-step/shader-compiler dependency; pure C#; easiest to unit-test the field math. CPU cost higher per pixel.

**Feasibility / caveats (decision-relevant for Beta):**
- **Runs on the CPU software pipeline** (`AllowsTransparency` pins the window off the GPU). **Validate full-screen frame time at 165 Hz first** (perf spike). If marginal, compute the low-frequency field at **reduced resolution and smooth-upscale** (no perceptual loss for gradient + faint grid). Microsoft's "minimize pixel redraw" guidance: one merged pass is *cheaper* than today's N-stratum stack (each pixel drawn once).
- **`D3DImage`/true-GPU is ruled out** — D3D9-only and incompatible with `AllowsTransparency` (the classic "airspace" white-out). Don't pursue without abandoning layered transparency.
- **8-bit banding is a constraint ceiling, not a knowledge gap:** the field's real cures (≥10-bit output, blue-noise dithering) are **both unavailable** here (10-bit impossible on the layered CPU surface; dithering locked out). Mitigate only. The reference partly wins here via GPU/dither access we lack — acceptable for Beta.

**Locks preserved:** topological change only — 12-tier table, 0.92 peak, ≥0.20 floor, grid curve, labels all rendered byte-identically; no dithering/freeze/hard-edge/motion-depth-cut.

**Tests/UVHS/perf gate:** unit-test the field math (corner-norm, mutually-exclusive resolution, falloff values at sample radii) — easiest with realization (b). Perf gate: sustained 165 Hz full-screen frame-time budget on target hardware before merge. UVHS three-red-lines (halo / delayed-flicker / layering) across L1–L12 + motion.

**Risk:** medium–high (architecture + new build step or per-frame bitmap + perf validation). Mitigated by the `f381a43` rollback and by staging A1 first.

---

## 3. Sequencing recommendation

1. **A1 first** (one-clock motion model) — de-risks and delivers most of the coherence gain with low risk; can be its own Beta increment. *Or* fold A1 in as **stage 1 of A2** (the single pass naturally uses one clock).
2. **A2** (single-pass field) — the complete cure. Gate behind the **165 Hz perf spike**; choose `ShaderEffect` vs `WriteableBitmap` based on it.
3. Keep `f381a43` as the rollback baseline throughout.

## 4. Open decisions for Beta
- `ShaderEffect (ps_2_0)` vs `WriteableBitmap` — decide after the perf spike on target hardware.
- Perf budget at 165 Hz on the lowest-spec target; reduced-res-field fallback if needed.
- Keep gaze-following center? It's a **presence/exploration UX benefit, not a proven sickness reducer** (Adhanom et al. found no significant VR-sickness difference vs head-fixed) — retain for UX, but don't expect a comfort delta from it.

## 5. References (field consult, 2026-06-08)
- Fernandes & Feiner (2016), *Combating VR sickness through subtle dynamic field-of-view modification*, IEEE 3DUI — canonical dynamic FOV restrictor (fast-restrict / slow-restore; most users never noticed).
- Adhanom et al. (2020), *The Effect of a Foveated Field-of-view Restrictor on VR Sickness*, IEEE VR — foveated vs head-fixed: no significant sickness difference.
- Unity `TunnelingVignetteController` (XR Interaction Toolkit) — separate easeIn/easeOut/easeOutDelay; vignette as ONE element.
- Meta `LocomotionTunneling` — single-element comfort vignette.
- Prothero & Duh — Independent Visual Background (rest-frame) hypothesis; Cao et al. — static (not dynamic) net helps; Baek & Kim (2025) — rest-frame best ~0.4 opacity, density not critical.
- WPF perf: Microsoft, *Transparent Windows in WPF* (~30% CPU for a full-screen constantly-updating layered window) — software-composition ceiling.
- Gestalt **common-fate** + perceptual **scission/transparency** — the diagnostic framework for the "stacked moving layers" percept.

> Note: the named ui-ux-pro-max web/mobile design DB does **not** contain the load-bearing answer (only generic motion hygiene: animate 1–2 elements, honor reduced-motion, ease-out, avoid pure `#000`). The decisive knowledge came from the VR FOV-restrictor / rest-frame / foveated-rendering / compositing / Gestalt literature above.
