# Habituation Profile System

Engineering reference for SenseEase's tiered habituation. Audience: future
contributors modifying `HabituationEngine.cs` or related state. Not for clinical
review, marketing, or onboarding documentation.

For the clinical / IRB-facing parameter snapshot, see `VALIDATION-REFERENCE.md`
(separate document — cross-ref added once both are committed together).

---

## 1 · Three profiles

| Parameter | RESILIENT | TYPICAL | SENSITIVE |
|---|---|---|---|
| StableWindowMs | 120 000 | 180 000 | 360 000 |
| StepIntervalMs | 30 000 | 45 000 | 60 000 |
| StepMagnitudePp | 2.0 | 1.5 | 1.0 |
| PostSpikePauseLowMs (tier ≤ 5) | 90 000 | 90 000 | 180 000 |
| PostSpikePauseMidMs (tier 6–7) | 120 000 | 180 000 | 300 000 |
| PostSpikePause (tier ≥ 8) | — | — | — *(awaits user confirm)* |
| HabOffsetCapPp | 30 | 25 | 15 |
| MinSamplesForFullWeight | 3 | 4 | 6 |
| SevereClearMode | halve | halve | halve |
| SevereStrikeLimit | 2 | 2 | 1 |

`SevereClearMode = halve` everywhere by design: the "zero offset" behaviour is
not a separate mode; it triggers when `_severeStrikeCount >= SevereStrikeLimit`
(see §4). The enum is retained as a future hook.

Tier ≥ 8 returns `-1` from `GetPostSpikePauseMs` and sets
`requireUserConfirm = true`. The engine then enters
`EngineState.PausedAwaitingUserConfirm` and stays there until
`AcknowledgeAndResume()` is called (typically routed via the chatbot, see §6).

### 1.1 — Default rationale

The engine defaults to **`RESILIENT`** when no calibration has been performed.
This reflects a deliberate design choice: untested users inherit the historical
baseline behaviour (preserving backward compatibility), while users who complete
the calibration demo are stratified into `TYPICAL` or `SENSITIVE` based on
measured response. The default is permissive, not cautious — caution is opt-in
via calibration or chatbot override.

A maximally-cautious default (`SENSITIVE` for all new installs) was rejected.
Chang-Hain progressive exposure requires gradual relaxation; defaulting to the
tightest profile would suppress long-term habituation progress, contradicting
the framework's intent. RESILIENT default + active downgrade-on-calibration
matches the clinical logic: assume the user can tolerate normal exposure until
measured evidence says otherwise.

### 1.2 — Evidence base

Citations supporting the profile thresholds and the tiered-pause logic:

- **Barány Society Consensus on MS / VIMS diagnostic criteria** (2023) — tier
  stratification framework
- **Chang & Hain (2020)** — progressive TDP (threshold-determined personalisation)
  framework; phased calibration
- **Pavlou (2010)** — intensive vestibular rehabilitation regimen reports 55 %
  dropout; argues for supervised graded exposure rather than aggressive one-shot
  protocols → motivates the SENSITIVE tier's longer windows and tier-9
  user-confirm gate
- **VIMSSQ-short norms** (Lukacova et al. 2023, 2025) — population distribution
  of VIMS susceptibility, informs the three-tier granularity

---

## 2 · Unit harmonisation

**Rule (locked at Phase 1):**

```
Internal engine state remains in SECONDS (preserves existing test invariants
and SenseDefaults numerics). The HabituationProfileParams POCO exposes *Ms
field names to match the design spec and external JSON schema. Conversion
happens ONCE, at the boundary where _params is consumed by the engine.

Rule: any field crossing the engine boundary uses seconds.
      Any field touching JSON / spec / chatbot uses ms.
      Never mix within the same scope.
```

`HabituationProfileParams` exposes `*Ms` fields plus `*Seconds` helper properties
(e.g. `StableWindowSeconds => StableWindowMs / 1000.0`) so engine consumers read
naturally without per-call division.

---

## 3 · Profile-switch triggers

Two and only two paths can change the active profile:

1. **Calibration demo** — `MainViewModel.DeriveProfile(peakPressure, maskActivations)`
   maps the demo run's reactivity to a profile, then calls
   `HabituationEngine.SetProfile(profile, source: "calibration_demo")`. Tier
   mapping mirrors the preset-delta mapping (peak > 85 OR activations ≥ 3 →
   SENSITIVE, > 50 OR ≥ 1 → TYPICAL, else RESILIENT).
2. **Chatbot override** — model emits an optional `profile_override` field
   (SENSITIVE / TYPICAL / RESILIENT / null). When non-null, the chatbot service
   calls `SetProfile(profile, source: "chatbot_override")`. Conservative bias:
   the system prompt instructs the model to only emit a non-null override when
   the user explicitly requests it ("too sensitive", "too tight", "I've adapted",
   etc.); never as an inference.

There is no manual UI selector for profile. The dashboard never shows a
profile-picker dropdown — this is intentional to prevent SENSITIVE / RESILIENT
misuse by users who don't understand the trade-offs.

A profile switch resets `_severeStrikeCount` only. The earned `_offset` (long-term
habituation progress) is preserved across profile changes.

---

## 4 · State machine

Two parallel enums:

- **`HabituationPhase { Observing, Relaxing, Paused }`** — existing, consumed by UI.
  Unchanged. Reflects what the user sees in the dashboard's habituation widget.
- **`EngineState { Running, Cooldown, PausedAwaitingUserConfirm }`** — new in
  Phase 3. Surfaced in the JSON snapshot and the chatbot prompt's
  `[habituation_state]` block. Internal book-keeping; not directly bound to UI.

`EngineState` derivation (subject to Phase 3 implementation):

| Internal condition | EngineState |
|---|---|
| `_pauseCooldown > 0 && !_awaitingUserConfirm` | `Cooldown` |
| `_awaitingUserConfirm == true` | `PausedAwaitingUserConfirm` |
| otherwise | `Running` |

`_externallyPaused` (manual dashboard pause) does NOT mask `Cooldown`. The JSON
exposes `externally_paused` as a parallel boolean so the chatbot can see both
facts independently.

### 4.1 — Severe-strike logic

- Every severe event (pressure spike OR chatbot-reported severe discomfort)
  halves `_offset` and increments `_severeStrikeCount`.
- Chatbot-derived severe events are weighted 0.5 (count as half a strike) —
  LLM signals are softer ground truth than sensor-derived spikes.
- `_severeStrikeCount` is a `double` to accommodate the weighting.
- When `_severeStrikeCount >= _params.SevereStrikeLimit`, the next severe event
  zeros `_offset` and resets the counter to 0.
- Any full pass through the stable-window (i.e. `_stableElapsed >= _effectiveWindow`)
  also resets `_severeStrikeCount` to 0.
- A profile switch resets `_severeStrikeCount` to 0 (per §3).

---

## 5 · Persistence

New file: `%LocalAppData%\SenseEase\habituation_profile.json`. Independent of
the existing `habituation.json` (which continues to store offset only).

### 5.1 — Schema v1 (Phase 4 will commit the canonical sample)

```json
{
  "schema_version": 1,
  "updated_at_iso": "2026-05-24T12:34:56Z",
  "profile": "RESILIENT",
  "profile_source": "default",
  "habituation_level": 23.5,
  "habituation_offset_pp": 8.0,
  "habituation_offset_cap_pp": 30,
  "current_vims_tier": 4,
  "severe_strike_count": 0,
  "severe_strike_limit": 2,
  "engine_state": "RUNNING",
  "externally_paused": false,
  "awaiting_user_confirm": false,
  "last_spike_at_iso": null,
  "next_step_eligible_at_iso": null,
  "params_snapshot": {
    "stable_window_ms": 120000,
    "step_interval_ms": 30000,
    "step_magnitude_pp": 2.0
  }
}
```

`profile_source` ∈ `{"calibration_demo", "chatbot_override", "default"}`.
`engine_state` ∈ `{"RUNNING", "COOLDOWN", "PAUSED_AWAITING_USER_CONFIRM"}`.

### 5.2 — Write triggers

The engine writes the full snapshot (atomic temp+rename) when any of:

- `SetProfile()` is called
- `HabituationLevel` changes by more than 5 since the last persisted write
- A spike fires
- `EngineState` enters or exits `PausedAwaitingUserConfirm`
- `_severeStrikeCount` changes

### 5.3 — Restore on startup

Read order:

1. `habituation.json` → `_offset` (existing path, unchanged)
2. `habituation_profile.json` → profile + strike count + state

If `habituation.json` exists but `habituation_profile.json` does not (upgrade
scenario for existing users), the engine defaults to `RESILIENT` with
`profile_source: "default"` and immediately writes the new file. **Existing
users are NOT pushed back to the calibration demo** — that would be a forced
onboarding regression. The two files are version-independent; a read failure
on one does not roll back the other.

### 5.4 — Schema version mismatch

Unknown `schema_version` → fall back to RESILIENT + default state, write a
one-line message via `System.Diagnostics.Debug.WriteLine` (visible only when a
debugger is attached). No separate log file is opened for this; the case is
expected to be near-zero frequency within the v1 lifecycle.

### 5.5 — Atomic write requirement

`File.Move` is not guaranteed atomic across all Windows versions. Phase 4 uses
`File.Replace` (or equivalent transactional rename), and Phase 4 acceptance
includes a `Process.Kill`-during-write test to verify no half-written state can
land on disk.

---

## 6 · Chatbot integration

### 6.1 — Read path

Phase 6 injects a `[habituation_state]` … `[/habituation_state]` block into the
chat completion request body, between the `historyPrefix` and the `State:`
line. The block is part of the user-turn payload but the system prompt
explicitly marks it as read-only context, not user input. Block contents:

- `profile`
- `habituation_level`
- `current_vims_tier`
- `engine_state`
- `awaiting_user_confirm`

Other JSON fields are not injected — keeps the prompt token budget tight.

### 6.2 — Write path

Phase 7 extends `ModelDecision` with two optional fields:

- `profile_override` (string, nullable) — when non-null, must match one of
  `RESILIENT / TYPICAL / SENSITIVE`. Causes `SetProfile(value, source: "chatbot_override")`.
- `resume_acknowledged` (bool, default false) — only meaningful when
  `engine_state == PAUSED_AWAITING_USER_CONFIRM`. Causes `AcknowledgeAndResume()`.

System prompt additions (zh + en, both required):

- Profile-override conservative-bias clause: model emits non-null only on
  explicit user request.
- Awaiting-user-confirm safety floor: when `awaiting_user_confirm == true`, the
  model's primary task is to detect user willingness to continue; until the user
  explicitly says so, the model must not emit `resume_acknowledged = true` and
  must not suggest any FOV / mask parameter changes. This is a safety floor,
  not a recommendation — the wording may vary between zh and en but the
  semantics may not weaken.

Belt-and-suspenders: a lexicon-based resume detector (FeedbackLexicon
`ResumeKeywords`: "好了 / 可以了 / 繼續 / 沒事了 / I'm ok / I feel better /
let's continue") also triggers `AcknowledgeAndResume()`. The two paths are
idempotent — if both fire on the same turn the second call is a no-op.

---

## 7 · Phase ordering

| Phase | Goal | Merge point |
|---|---|---|
| 0 | This document + Phase 1 code comments | — |
| 1 | `HabituationProfile.cs` POCO + tests | Merge Point A part 1 |
| 2 | Engine param-injection layer | Merge Point A part 2 |
| 3 | Strike counter + EngineState + tier in `Step()` | Standalone PR (review required) |
| 4 | Profile persistence (`FileHabituationProfileStore`) | Merge Point B start |
| 5 | Calibration → profile mapping | Merge Point B |
| 6 | Chatbot read path | Merge Point B |
| 7 | Chatbot write path | Merge Point B |
| 8 | Test sweep + acceptance smoke + this document finalisation | Merge Point B end |

**Merge Point A hard rule**: `HabituationTests.cs` passes one-line-unchanged.
Achievable because the engine default (RESILIENT) matches current SenseDefaults
numerics exactly. Phase 2 swaps the *source* of those numbers (from
SenseDefaults to `_params`) without changing any value.

**Phase 3 hard rule**: PR description lists every re-pinned test with old → new
behaviour and rationale. Reviewers check the table, not the diff.
