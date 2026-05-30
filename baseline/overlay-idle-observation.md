# Overlay Idle State — Verified Observation

**Captured**: 2026-05-29 at G0 review (user + code-verified).

## Finding

At cold-start idle (no calibration triggered, no VIMS pressure built up), the Chang-Hain FOV mask is **not visually present**. The user reported "overlay idle is nothing" during G0 manual verification, and code inspection confirms this is intentional design, not a regression.

## Code citation

The FOV mask alpha is driven by `state.VimsTier` (clamped 1-10) and `state.MaskActive` flag in `OverlayWindow.ApplyFovMask` at [src/SenseEase.App/Views/OverlayWindow.xaml.cs:316-326](../src/SenseEase.App/Views/OverlayWindow.xaml.cs#L316-L326). At tier 1 (idle), the mask alpha sits at the near-transparent L1 baseline; the grid is only drawn when `maskChanged` flips (line 303-309). Net visual at idle = essentially nothing, matching the user's observation.

## Implication for head-tracking sprint

The original `overlay-idle.png` capture spec was written assuming the overlay is always visible. It is not. Therefore:

- **No `overlay-idle.png` in M1 baseline.** A blank screenshot would provide zero regression value.
- **FOV-mask visual reference deferred to M5.** When the overlay first consumes head pose, we will deliberately trigger a tier > 1 state (via demo mode or a controlled fixture) and capture `overlay-active-baseline.png` showing the centered FOV mask. That becomes the human visual reference for G4 UVHS comparison.
- **M3 invariant remains parameter-based** (cx==0.5, cy==0.5, mask depth/radius/feather unchanged, 75-test green) — already independent of visual screenshot per the 2026-05-29 wording fix.

## Cross-references

- [head-tracking-sprint-state.md](../../../../.claude/projects/c--Users-kirby-Desktop-SenseEase-APP/memory/head-tracking-sprint-state.md) — M3 invariant wording fix, UVHS section
- [uvhs-playbook.md](../../../../.claude/projects/c--Users-kirby-Desktop-SenseEase-APP/memory/uvhs-playbook.md) — "reference auto-artifacts vs human-reference auto-artifacts" guidance
