# Overlay Idle Screenshot — Superseded

**Status**: DEFERRED to M5 per 2026-05-29 G0 finding.

The original spec assumed the comfort overlay is always visually present at idle. Code + user verification confirmed otherwise: the Chang-Hain FOV mask is tier-gated and effectively invisible at tier 1 (idle).

See [overlay-idle-observation.md](overlay-idle-observation.md) for the verified finding and rationale.

The real FOV-mask visual reference will be captured at **M5** (overlay-consumes-pose milestone) by deliberately triggering tier > 1 via demo mode, saved as `overlay-active-baseline.png` for G4 UVHS comparison.
