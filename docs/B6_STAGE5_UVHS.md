# B6 Stage 5 — UVHS (User-Visual Hard-Stop) checklist

**Gate:** the riskiest direction (auto-easing protection) gets eyes-on verification BEFORE the
`AdaptiveAutoTune` consent is ever shipped ON for a user. Nothing in Stages 1–4 changes a default user
(consent default-OFF is test-proven byte-identical), so this is the gate between "built + tested" and
"a real opted-in user could see it."

**Why the scope is small:** 4b is **default-OFF + go-silent + F6 audit-only**. There is no per-change
in-chat notice and no cohort-promotion card (both dropped). So the only NEW user-visible surfaces are:
(a) the opt-in toggle copy, (b) the Event Log audit rows for auto moves, (c) the *overlay itself* not
jumping when an auto move lands, and (d) a Reset visibly returning toward baseline.

---

## 0 · Anti-cheat clauses (from the UVHS playbook — non-negotiable)

1. **A human must actually SEE each surface render** — a passing unit test is NOT a UVHS pass. Trusting the
   Event Log JSON is not the same as watching the overlay not-jump.
2. **Capture proof** — screenshot or screen-record each numbered check; a claim without an artifact is a fail.
3. **Both themes AND both languages** — every visible-text/colour check runs in light+dark × ZH+EN (4 reps).
4. **The dev force-path is STRIPPED before any commit** — it exists only to make weeks-of-evidence happen on
   demand for the human; it must not ship. Confirm it is `#if DEBUG` / env-gated and removed from the
   protection-shipping commit.
5. **No "it should be fine"** — if a surface can't be driven to render, it is UNVERIFIED, not passed.

---

## 1 · The DEV force-path (how to make a multi-week event happen in 30 seconds)

Real auto moves need ~weeks of measured FMS + at-cap offset, so they can't be observed naturally in a sitting.
A DEBUG-only hook drives each visible surface deterministically. Keep it minimal and OFF in release:

- **Force an auto DOWN (tighten):** under `#if DEBUG`, a dev command that builds an `AdaptationCommitRequest`
  with `ProfileMove.Down` (target = one tier more cautious than live) against the live snapshot and calls
  `mock.ApplyGateOutcome(...)`. Watch the overlay + Event Log.
- **Force an auto UP (relax):** same with `ProfileMove.Up`. To satisfy the live-recheck, the dev path should
  first earn a near-cap offset (drive calm `Step`s) so `HabituationLevel ≥ 90` and the UP isn't aborted as
  `up-stale-protective-change`.
- **Force consent ON:** a dev toggle that sets `AdaptiveAutoTuneConsent = Enabled` so the real
  `MaybeRunAdaptationTuning` path is live (to verify the end-to-end wiring, not just `ApplyGateOutcome`).
- **Reset:** the existing user Reset button (no dev hook needed).

> Gate the hook behind `#if DEBUG` AND an env flag (e.g. `SENSEEASE_B6_FORCE=1`) so it can't fire in a
> shipped build even in a debug binary. Grep-confirm it's gone from the release diff before commit.

---

## 2 · The checklist (each = watch + capture; light+dark × ZH+EN where text/colour is involved)

### A — Opt-in toggle copy (the ONLY standing transparency surface)
- [ ] **A1** Settings → the "Adaptive auto-tune" toggle reads honestly: it discloses that the app *quietly*
      makes small automatic adjustments (both directions), capped, reversible by Reset — and does NOT promise
      an in-chat announcement (go-silent). Verify the corrected copy from Stage 1 is what renders. (×4: L/D×ZH/EN)
- [ ] **A2** Toggle OFF → ON → OFF is smooth, no fl​​icker/閃退, and the state persists across a restart.

### B — An auto DOWN move (more protection — the safe direction)
- [ ] **B1** Force a DOWN. The overlay must **not jump/flash** — F6 means no shader re-seed, so mask
      depth/grid/feather stay put; only the elasticity cap changed (the offset bleeds toward the lower cap
      *gradually* over the next ticks, not in one frame). Record the overlay across the move.
- [ ] **B2** The Event Log shows a `Habituation.Profile` row for the move with `(source: auto_elasticity)`
      (audit kept).
- [ ] **B3** The **chat** shows NOTHING about the move — open the chat, confirm no new system bubble and that
      asking the bot about recent changes doesn't surface the auto move (trajectory excludes auto_elasticity).

### C — An auto UP move (less protection — the eased direction, the risky one)
- [ ] **C1** Earn near-cap offset, then force an UP. The FOV widening must be **subtle/non-jarring** — the cap
      rises but the offset only grows via the normal slow relax cadence; there should be no sudden FOV pop.
      Record it. (This is the "slew claim made real" — the headline thing to watch.)
- [ ] **C2** Event Log shows the UP row `(source: auto_elasticity)`; **chat stays silent** (as B3).
- [ ] **C3** Sanity: with consent ON but the evidence bar *not* met, forcing the real tuning pass does NOTHING
      (no move) — confirm the gate/classifier actually withholds, i.e. easing isn't trivially reachable.

### D — Reset returns toward baseline
- [ ] **D1** After an auto UP (profile now more elastic + any auto-delta), hit Reset. The profile visibly
      returns toward the **Adapting** seed and the overlay returns toward base protection — in one visible step,
      no half-relaxed limbo. (RESET_AUTO_REVERT, made visible.)
- [ ] **D2** The Event Log shows the revert (`source: auto_profiling_default`) — a Reset IS announced (it's a
      deliberate user action), unlike the silent auto moves.

### E — Default-user no-op (the safety floor, visible)
- [ ] **E1** With consent OFF (the default), run the app normally for a session: zero auto moves, zero
      `auto_elasticity` Event Log rows, no `profile_eval_history.json` written. The app is indistinguishable
      from pre-B6.

---

## 3 · Sign-off

- [ ] All A–E checked with artifacts, ×4 reps where required.
- [ ] The DEV force-path is stripped/`#if DEBUG`+env-gated and confirmed absent from the shipping diff.
- [ ] Only AFTER this: the `AdaptiveAutoTune` consent default may be reconsidered for rollout (it stays OFF
      until Kirby explicitly flips it; UVHS does not itself flip it).

**Until sign-off:** B6 ships as built-and-tested, default-OFF — no user-facing protection change.
