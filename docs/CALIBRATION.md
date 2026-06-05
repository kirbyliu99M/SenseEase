# Calibration System

Engineering reference for SenseEase's post-demo calibration. Audience: future
contributors modifying any file under `Inference/CalibrationProcessor.cs`,
`ViewModels/CalibrationCardViewModel.cs`, `ViewModels/CalibrationApplicator.cs`,
`Infrastructure/Real/CalibrationChatbotService.cs`, or the persistence layer
in `Infrastructure/Storage/FileHabituationProfileStore.cs`. Not for clinical
review or marketing.

Companion document: `docs/HABITUATION-PROFILE.md` (which covers the engine
that calibration configures). Read both together — they share the
`habituation_profile.json` schema and the rationale for why calibration is
the only path that switches a user into a non-default profile.

---

## 1 · Overview

Calibration converts a single post-demo experience into a persistent profile
that re-shapes the entire VIMS-mitigation stack for that user. After the
demo video finishes (`MainViewModel.OnDemoVideoFinished`), one of two paths
runs:

- **First-time calibration (buttons UI)** — the user has never completed
  calibration on this machine (`calibration_count == 0` in the persisted
  snapshot). A 5-card sequential form fires automatically in the chatbot
  tab, asking about Q1 (worst segment), Q2 (symptoms), Q3 (intensity
  1-5 — mandatory), Q4 (vs. baseline), and Q5 (long-term risk factors).
  On completion the responses route through `CalibrationProcessor` →
  `CalibrationApplicator` → `MockInferenceEngine.ApplyCalibrationResult`,
  the engine's profile + effective trigger + strike limit update, and a
  single bilingual summary message posts to the chat transcript.
- **Subsequent calibration (LLM-invited)** — `calibration_count >= 1`. A
  banner appears in the chatbot tab offering "Run calibration again?". The
  user can Start (which spins up `CalibrationChatbotService` for an 8-turn
  natural-language dialog with the local Ministral 3B model), Skip (silent
  dismiss), or do nothing (auto-dismiss after 30 seconds).

Both paths produce a `CalibrationResponses` record that the same
`CalibrationProcessor.Process` function consumes — the LLM path layers a
parser + a multi-turn merge on top, but the terminal contract is identical.
This is intentional: any future calibration entry point (voice, hand-drawn
sketch, anything) only needs to populate `CalibrationResponses` and the
downstream pipeline takes over.

---

## 2 · Architecture

```
                                          ┌─ ChatbotViewModel ─┐
                                          │ banner + 30s timer │
                                          │ ActiveLlm…         │
                                          └──────┬─────────────┘
                                                 │
   MainViewModel.OnDemoVideoFinished             │ subsequent
   ┌──────────────────────────┐                  │ (calibration_count ≥ 1)
   │ engine.LoadedCalibration │  first-time      │
   │ Count == 0 ?              ├──────────┬──────┘
   └─────────┬────────────────┘          │
             │ yes                       │ yes
             ▼                           ▼
   ┌──────────────────┐         ┌──────────────────────────┐
   │ Calibration       │        │ CalibrationChatbotService │
   │ CardViewModel     │        │   ↳ LlamaServer           │
   │   5-card state    │        │     CalibrationClient     │
   │   machine         │        │   ↳ CalibrationPrompts    │
   └────────┬──────────┘        │   ↳ Delta merge           │
            │                   └──────────┬───────────────┘
            ▼                              │
            ▼                              ▼
       CalibrationResponses ─────► CalibrationProcessor.Process
                                          │
                                          ▼
                                  CalibrationResult
                                          │
                                          ▼
                                  CalibrationApplicator.Apply
                                          │
                                          ▼
                              MockInferenceEngine
                                .ApplyCalibrationResult
                                          │
                                          ▼
                              HabituationEngine            FileHabituation
                              .SetProfile()                 ProfileStore.Save
                              .EffectiveTrigger = …         (atomic JSON v2)
                              .SevereStrikeLimit
                                Override = …
```

### 2.1 — Pure-logic core

`CalibrationProcessor` (in `Core/Inference/CalibrationProcessor.cs`) is a
static class with one public entry point, `Process(responses, sensor)`. It
takes a `CalibrationResponses` record + a `CalibrationSensorData` record
and returns a `CalibrationResult` — no I/O, no engine state, no UI
references. Every other component in the calibration pipeline orbits
around this single pure function; bugs in calibration math are pinned by
the unit tests in `CalibrationProcessorTests.cs` without needing to spin
up any infrastructure.

The processor encodes spec §5's four-step decision flow:
1. **Q3+Q4 → base profile** — Q3=4/5 always SENSITIVE; Q3=3+Q4=Worse →
   SENSITIVE; Q3=2/3 → TYPICAL; Q3=1+Q4=Worse → TYPICAL; else RESILIENT.
2. **Q5 risk-factor escalation** — vestibular hard-locks to SENSITIVE +
   override 50 + strike limit 1 + advisory `"vestibular"`; ≥2 non-
   vestibular risks escalate one profile level (and if Q3==5, set
   `"high_risk_combo"` advisory); =1 non-vestibular risk applies
   `triggerAdjust = -10`.
3. **`personal_pressure_scale` lookup** — a 5×3 table indexed by Q3
   intensity and sensor peak pressure (see §7 below for the verbatim
   table).
4. **Vestibular hard cap** — if vestibular, clamp the scale to ≤ 1.0.

### 2.2 — 5-card UI state machine

`CalibrationCardViewModel` (in `App/ViewModels/CalibrationCardViewModel.cs`)
is a strict 5-card sequential form. The user can advance with Next or
finalise on the last card with Complete; on every card except card 3
they can also leave the card blank and continue (per spec §3 most
questions are optional). Card 3 (Q3 intensity) is mandatory — the Next
button stays disabled until an intensity is picked.

Cancellation rules:
- **Cancel before Q3** → `CalibrationCompleteness.Abandoned`. No profile
  change, no log line, no `calibration_count` increment. Next demo end
  re-fires the first-time card.
- **Cancel after Q3** → `Partial`. Engine updates, profile lands, and a
  "partial completion" message posts to chat. Counts as a calibration for
  `calibration_count` purposes.
- **Complete all 5** → `Complete`. Full summary posted.

Q2/Q5 mutual-exclusivity is enforced at the VM level (matrix tested in
`CalibrationCardViewModelTests`). Q2's "都沒有" is mutually exclusive
with the six symptom options; Q5's "跳過不答" is mutually exclusive with
everything; Q5's "都沒有" is mutually exclusive with the five real risks.

### 2.3 — LLM dialog driver

`CalibrationChatbotService` (in `Infrastructure/Real/CalibrationChatbotService.cs`)
drives the subsequent-calibration LLM dialog. It is NOT a second LLM
process — it talks to the same `http://127.0.0.1:8181` `llama-server`
endpoint `MistralChatbotService` already brought up, via the same
`ICalibrationLlmClient` interface. If that server isn't running the
service fails gracefully and the caller (via `LlmCalibrationFactory`
returning null) treats it as "LLM unavailable" without surfacing an
error.

Key invariants:
- **Independent transcript** — `calibration_history.jsonl` is separate
  from `chat_history.jsonl` so the VIMS-monitoring chat context never
  bleeds into the calibration dialog's longitudinal-comparison reasoning.
- **Hard 8-turn budget + 1 convergence turn** — `NormalTurnBudget = 8`,
  `MaxLlmCallsPerSession = 9`. On the 9th call, a `[SYSTEM REMINDER]`
  pseudo-user line is injected demanding the model emit `is_complete=true`.
- **Single Completed event source** — this service is the ONLY place
  `Completed?.Invoke` ever fires. Q3 fallback defers it pending a merge
  call from the caller, but never delegates the event itself. See §5
  on safety floor for the same rule applied there.
- **Self-harm short-circuit** — `TripsSafetyFloor` runs against every
  user message BEFORE the LLM call. If matched, fires
  `SafetyFloorTriggered` synchronously, calls `EndSession`, returns.

### 2.4 — Completion handler

`CalibrationApplicator` (in `App/ViewModels/CalibrationApplicator.cs`) is
the shared completion path used by both the card UI's `Completed` event
and the LLM service's `Completed` event. It calls
`CalibrationProcessor.Process` to derive the result, dispatches the
result to the engine via `MockInferenceEngine.ApplyCalibrationResult`,
and appends the summary + (optional) medical-advisory bubbles to the
supplied `ObservableCollection<ChatMessageViewModel>`.

Living here (rather than inside `MainViewModel`) means headless tests can
exercise the end-to-end flow without booting WPF — confirmed by the
6-scenario suite in `CalibrationEndToEndSmoke.cs`.

### 2.5 — Persistence

`FileHabituationProfileStore` (in `Infrastructure/Storage/`) is the
single persistence point. It writes `habituation_profile.json` atomically
via `AtomicJsonWriter` (tmp + `File.Replace`). The store is unaware of
calibration semantics — it just round-trips a `HabituationProfileSnapshot`
shape. The snapshot DTO (Schema v2) is the wire contract.

Calibration writes piggy-back on the existing snapshot-write triggers in
`MockInferenceEngine.MaybePersistProfileSnapshot` — calling
`ApplyCalibrationResult` flips the profile, which in turn marks
`_profileSnapshotDirty = true` via the engine's `ProfileChanged` event,
and the next persistence tick writes the v2 snapshot.

---

## 3 · Schema v1 vs v2

`habituation_profile.json` had a v1 shape during Phases 3-4 (engine-only
fields: profile + strikes + state + params). Stage 6 added calibration-
related fields without breaking the existing v1 readers:

### 3.1 — What v2 added

- `calibration_count` — int, default 0. Bumped on Complete or Partial
  completions; Abandoned does NOT increment (spec §3).
- `last_calibration_at_iso` — ISO-8601 UTC, nullable. Null if no
  calibration has ever completed.
- `calibration_completeness` — enum: `complete` / `partial` / `abandoned`.
  Nullable, null if no calibration has run.
- `calibration_path` — enum: `buttons` / `llm` / `llm_with_button_fallback`.
  Nullable, null if no calibration has run.
- `calibration_responses` — the verbatim `CalibrationResponses` from the
  most recent session, including per-field source tags (`buttons` / `llm`
  / `llm_low_confidence` / `fallback_buttons`). Nullable.
- `longitudinal_observations` — list of free-form strings the chatbot
  accumulates across calibrations. Append-only in v2.
- `personal_pressure_scale` — double, default 1.0. Clamped to [0.7, 1.5];
  vestibular-diagnosed users hard-capped at 1.0. Applied as
  `effectiveTrigger = defaultTrigger / scale` at the engine boundary.
- `symptom_dominance_primary` — enum: `nausea` / `oculomotor` /
  `disorientation` / `mixed`. Null = no symptoms reported.
- `medical_advisory_shown` — enum: `vestibular` / `high_risk_combo`.
  Persisted so the "show at most once per kind per user" rule survives
  restarts.
- `calibration_confidence` — enum: `high` / `medium` / `low`. Null = no
  calibration has run.
- `was_recovered_from_wipe` — bool, one-shot loader flag (see §3.3).

### 3.2 — `LoadLegacyV1AsV2InMemory` semantics

When the loader sees `schema_version == 1` on disk, it deserialises the
file (v1 fields populate; v2 fields stay at their property-initialiser
defaults), then bumps the in-memory `SchemaVersion` to 2. The file on
disk stays v1 until the next `Save` upgrades it.

This is **NOT a migration** — there is no field-transformation logic. v2
fields arrive at the C# defaults (`CalibrationCount = 0`,
`PersonalPressureScale = 1.0`, `LongitudinalObservations = []`, all
nullable enums = `null`) which is correct for a user who used the app
before calibration shipped. Renaming the method to anything containing
"migrate" was rejected in Stage 6 review on the grounds that it would
mislead future readers into expecting per-field translation rules.

On-disk upgrade timing: the next time `MaybePersistProfileSnapshot`
fires (a habituation tick, a profile change, etc.) the file is rewritten
with `schema_version: 2` and all the v2 fields at their defaults.
Upgrades happen silently — no user-visible event.

### 3.3 — App-data-wipe edge case (Stage 6c)

Users on Windows occasionally clear `%LocalAppData%\SenseEase\` (or a
third-party "cleaner" does it for them). If `habituation_profile.json`
is missing BUT a sibling artifact suggesting prior usage (currently
`habituation.json`, the Phase 3 offset-only legacy file) still exists,
the loader synthesises a defaults-only snapshot with
`WasRecoveredFromWipe = true` instead of returning null.

This matters for the "first-time vs subsequent calibration" branch in
`MainViewModel.OnDemoVideoFinished`: a wipe-recovered user arrives with
`CalibrationCount = 0` (which routes them through the buttons UI, the
right call — they've effectively lost their calibration history) but is
flagged so upstream code knows they're not a genuine first-run.

The flag is one-shot: set by the loader, cleared on the next `Save`.
The next on-disk snapshot will have `was_recovered_from_wipe: false`.

**Stage 7b production wiring (REQUIRED for the wipe path to be live):**

```csharp
private readonly IHabituationProfileStore _habituationProfileStore =
    new FileHabituationProfileStore(
        AppConfig.HabituationProfilePath,
        AppConfig.HabituationStatePath); // ← wipe-probe artifact
```

Without the second arg, the loader's wipe synthesis never fires — unit
tests would pass against the seam but production users would silently
get null'd back to RESILIENT defaults. Scenario 6 in
`CalibrationEndToEndSmoke.cs` constructs the store the same way
`MainViewModel` does so a future refactor that drops the second arg
fails CI.

### 3.4 — The 4-quadrant null-rule for responses × completeness

`calibration_responses` and `calibration_completeness` MUST be consistent.
The four legal combinations:

```
responses=null  & completeness=null       → never started
responses≠null  & completeness=Abandoned  → started but no Q3
responses≠null  & completeness=Partial    → Q3 set, others missing
responses≠null  & completeness=Complete   → all five answered
```

Any other combination is a bug in the writer. `CalibrationProcessor`
classifies `Abandoned` when `Q3Intensity == null` and `Complete` when
every primary field is set; everything in between is `Partial`.

---

## 4 · Design rationales

### 4.1 — Why first calibration uses buttons, subsequent uses LLM

Structured 5-card buttons reduce decision fatigue on round 1 — the user
has just finished an unfamiliar vection demo and needs the lowest-friction
possible reporting surface to capture an accurate baseline reaction.
Buttons also build the user's mental model of "what is SenseEase asking
about?" which makes round 2's natural-language dialog substantially
easier: the LLM can reference Q1/Q2/Q3 by name in a follow-up like "last
time you said the middle was worst — how about this time?" without
needing to first teach the user what those mean.

LLM mode on subsequent calibrations then has a known answer baseline to
compare against. Round 2's `is_complete=true` doesn't have to re-derive
every dimension from scratch; it can probe selectively for what actually
changed since round 1's persisted snapshot.

The inverse — LLM on round 1, buttons on round 2 — was rejected in spec
review for two reasons. First, the LLM cannot reach 0.7 confidence on
Q3 without a Q3 fallback card anyway, and showing a card mid-LLM-dialog
on round 1 is a confusing UX. Second, the longitudinal probing the LLM
does well on round 2 has nothing to anchor to on round 1.

### 4.2 — Why subsequent calibration is banner-invited, not forced

A demo-end auto-trigger on every subsequent run would drive users to give
shallow answers — they finish a 60-second video, they want to get back to
what they were doing, and the LLM dialog is a 30-second-to-2-minute
commitment. Forcing it produces high-volume low-quality longitudinal data,
which silently degrades the signal precisely when it matters most.

The banner-invitation model trades calibration FREQUENCY for calibration
QUALITY. Users who recognise "yeah, this demo felt different than usual"
have a deliberate one-click entry; users who just wanted to test that
the demo still plays can dismiss with no friction. Better fewer-but-
deliberate calibrations than many-and-casual.

**Future-revert gate.** This decision should be revisitable, but the
revisit has prerequisites. Anyone wanting to revert to forced auto-
trigger MUST first show how to prevent answer-fatigue degradation —
something like a "you've calibrated 5 times this week, the next one is
optional" cap, or a confidence-weighted longitudinal model that
discounts low-deliberation runs. Without that safeguard, reverting
means trading away the LLM dialog's value entirely.

### 4.3 — Why `personal_pressure_scale` divides trigger (not multiplies pressure)

`effectiveTrigger = defaultTrigger / scale`, applied at the engine
boundary in `MockInferenceEngine.ApplyCalibrationResult`. Here
`defaultTrigger` is `SenseDefaults.TriggerPressure` (= 85), so an
unscaled user (`scale == 1.0`) keeps `EffectiveTrigger = 85`. The engine
itself stays ignorant of `personal_pressure_scale` — `HabituationEngine`
sees only the resulting `EffectiveTrigger` number.

**Scope of `EffectiveTrigger` (load-bearing).** This number does NOT
re-shape the whole sensor pipeline — it is consumed by exactly two sites,
both inside `HabituationEngine`: the spike-recurrence check and the
calm-pressure-fraction check. Everything else still reads the raw
`SenseDefaults.TriggerPressure` (85): `MockInferenceEngine`'s mask
hysteresis, the FOV-radius projection (`ComputeFovRadiusPercent`), and
the VIMS tier projection (`UpdateVimsTier`). Tier ownership is therefore
raw-pressure: a user with `scale != 1.0` sees a personalised spike
threshold but an unchanged mask / FOV / tier response. (`DisplayStressPercent`
divides live pressure by `EffectiveTrigger` purely as a display metric and
deliberately does not feed the tier.) See `docs/TECHNICAL_WHITEPAPER.md`
§6.5 for the same scope qualifier on the engine side.

This direction (scale on the threshold, not the pressure reading) keeps
sensor readings pristine in their raw form. Two downstream consumers
benefit:
- **Logs and the adjustment ledger** record the actual optical-flow
  index and pressure the leaky integrator emitted. A future engineer
  inspecting an event log can read "pressure 78 → mask off" and confirm
  the engine state matches without needing to mentally divide by the
  user's scale.
- **The chatbot's `[SenseEaseState]` block** can reference concrete
  pressure numbers without each user seeing a different "personalised
  pressure" definition. The model sees real pressure, real trigger,
  and can reason about both.

Multiplying the pressure reading would mislead every downstream consumer
that's downstream of the integrator — the alternative was rejected for
that reason.

### 4.4 — Why vestibular override ignores scale and adjustment

When `RiskFactor.VestibularDiagnosed` is in Q5, the processor sets
`TriggerThresholdOverride = 50` and `SevereStrikeLimitOverride = 1` and
ignores both `personal_pressure_scale` and `TriggerThresholdAdjustment`
in the engine application. This is a clinical safety floor: vestibular-
diagnosed users have a medically-documented vulnerability that
calibration self-reports cannot soften. The override-50 + strike-limit-1
combination encodes "treat any signal as severe immediately" — a single
sensor-grade severe event is enough to zero the offset and reset
counters.

Allowing scale to soften the override would weaken the floor. Concretely:
a vestibular-diagnosed user who reports Q3=1 ("barely noticed") in a
single session would otherwise get `scale > 1.0` (more permissive), which
would push their effective trigger above the spec'd 50. The override path
intentionally short-circuits this — the medical fact outranks the
self-report.

Tests in `CalibrationProcessorTests` pin this priority. The override
arrives via the integer field `TriggerThresholdOverride`; when non-null,
`MockInferenceEngine.ApplyCalibrationResult` uses it verbatim and skips
the scale/adjustment arithmetic entirely.

### 4.5 — Why confidence is per-axis object, not array

`[CALIBRATION_DELTA]` JSON's `confidence` field is a JSON object
(`{ "q1": 0.9, "q2": 0.5, ... }`), not an array (`[0.9, 0.5, ...]`).
The array form was an LLM footgun: the model would occasionally emit a
wrong-length array (4 elements instead of 5, or 6) and the parser would
silently shift values — Q3's confidence ended up in Q2's slot, the
NeedsQ3Fallback check would then fire on the wrong field.

Object form makes key errors visible in the JSON itself. A missing `q3`
key defaults to 0.0 (lowest confidence) and triggers Q3 fallback —
visibly, correctly. The C# representation stays a `double[5]` indexed
0..4 (Q1..Q5) internally so the existing
`CalibrationChatbotService.NeedsQ3Fallback` reads `confidence[2]`
without per-call lookup overhead; the conversion happens once in the
parser at deserialisation time.

### 4.6 — Direct-push-to-main vs integration-branch ceremony for parallel sprints

This Calibration sprint went DIRECT to main across Stages 1-7. Agents
A through E worked in parallel worktrees, each touching distinct files
(Agent A owned the card UI, Agent B owned the LLM service, Agent C
owned schema v2 + persistence, Agent D owned the banner + recalibrate
commands, Agent E owned acceptance + docs). The merge order at Merge
Point B was deliberate: B then C then D before E to keep contracts
stable.

This worked BECAUSE the file overlap matrix was empty. The only shared
edit point was `MockInferenceEngine.cs`, and only Agent C touched it
(removing one hardcoded line — see §6).

**Forward-looking rule:** future sprints touching shared files MUST use
integration-branch ceremony. The Habituation Phase 6/7/8 sprints, for
example, all touch `MistralChatbotService.cs` (Phase 6 reads
habituation state, Phase 7 writes profile overrides, Phase 8 writes
resume acknowledgements). Running those in parallel without an
integration branch would produce three-way merge conflicts in the
middle of `BuildContextualInput` and `ApplyModelDecision`. The
calibration sprint's direct-push success is NOT a precedent for those.

The bright line: if every agent's file ownership matrix has zero
overlap with every other agent's, direct-push is acceptable. Even one
shared file (especially a hot file like a chatbot service) flips it to
integration-branch.

---

## 5 · Safety floor design

User-life-safety check; runs BEFORE every LLM call in
`CalibrationChatbotService.SendUserMessageAsync`. If matched, fires
`SafetyFloorTriggered` synchronously with the localized crisis-line
response, calls `EndSession`, and returns without consulting the LLM.
`Completed` does NOT fire — the session is over but did not complete.

### 5.1 — Keyword list (verbatim)

Defined in `CalibrationChatbotService.SafetyFloorPhrasesZh` and
`SafetyFloorPhrasesEn`. Whole-phrase substring match, case-insensitive
for English (`ToLowerInvariant` is identity on Han characters):

```
zh: 自殘, 自殺, 結束生命, 跳下去, 跳樓,
    不想活了, 不想活下去, 不想再活,
    想死了, 想去死, 我想死

en: suicide, self-harm, self harm, selfharm,
    kill myself, end it all, end my life,
    not want to live, do not want to live, don't want to live,
    want to die, wanna die
```

### 5.2 — False-positive guards

The list is deliberately phrase-level, not keyword-level. Examples of
inputs that look ambiguous but do NOT trigger the floor:

- `暈到不想活動` ("feel so dizzy I don't want to do anything") — contains
  `不想活`, which is a substring of the watch-list entry `不想活了`,
  but the watch entry includes the trailing `了` so a partial substring
  match fails. We use `userText.Contains(p, StringComparison.Ordinal)`
  which requires the full phrase.
- `想死啊這 demo` ("the demo is so bad I could die") — does NOT contain
  any of `想死了` / `想去死` / `我想死` as a contiguous substring.
- English "kills me" by itself doesn't trip; only `kill myself` does.

This bias trades a small false-negative risk (a user reaching out in
distress with a phrase NOT in the list) for a near-zero false-positive
rate. The tradeoff is the right direction because false-positives here
have a high cost — the floor RESPONDS WITH A CRISIS LINE. False-
positives turn the calibration UX into an unsolicited mental-health
intervention, which is worse than the floor not firing on a borderline
phrase.

### 5.3 — Escalation behaviour

When triggered:
1. `SafetyFloorTriggered?.Invoke(response)` fires synchronously with the
   bilingual crisis-line text. The host (chat tab) routes this to the
   chat transcript with the system role.
2. `EndSession()` is called, flipping `IsActive` to false. Any subsequent
   `SendUserMessageAsync` is a no-op.
3. `Completed` is NOT invoked. The session "ended" but did not
   "complete" — no profile change, no JSON write, no `calibration_count`
   increment.

Event-ownership invariant: the same single-event-source rule that applies
to the normal Completed path also applies here. `Completed` is owned
exclusively by `CalibrationChatbotService`; the safety floor path
deliberately does NOT fire it as a "completion with zero responses".
Callers wanting "was the calibration finished cleanly?" check
`completedFired == true && safetyResponseSeen == null`.

The LLM is also instructed via the system prompt's `[SAFETY_FLOOR]`
block to refuse, emit the crisis line, and set `is_complete=true` if the
user-side check ever misses something. The two layers are belt-and-
suspenders: the user-side filter catches what the prompt missed, the
prompt catches what the filter missed, and both produce the same
bilingual crisis-line text.

---

## 6 · Out-of-scope edits during the sprint

PR-description-style "out-of-scope edits" disclosure, per the user's
sprint-review checklist. Two edits did NOT belong to their respective
agent's stated scope but were necessary:

- **Agent C (Schema v2)** touched `Infrastructure/Mock/MockInferenceEngine.cs`
  to remove one hardcoded line — the `SchemaVersion = 1` literal that
  used to live inside `BuildProfileSnapshot`. With v2 the property
  default initialiser owns the version; an explicit literal would have
  silently desynced from the DTO. The line removal was the surgical
  minimum needed for Schema v2 to land.

- **Agent E (this stage)** touched `Infrastructure/Mock/MockInferenceEngine.cs`
  to add `LoadedCalibrationCount` (Stage 7a) and touched
  `App/ViewModels/MainViewModel.cs` in two surgical sites: the
  `_habituationProfileStore` field initialiser (Stage 7b wipe-probe
  wiring) and the `OnDemoVideoFinished` first-time-vs-subsequent branch
  (Stage 7a `IsFirstRun` proxy replacement). All three are small
  additive changes documented inline at the edit site.

Neither edit modified existing public contracts (Stage 1-6 types are
untouched). Adding the `LoadedCalibrationCount` getter to
`MockInferenceEngine` is additive on a concrete class, not on the
`IInferenceEngine` interface, so test code that uses the interface form
is unaffected.

---

## 7 · Appendix A — Verbatim system prompts

The two templates below are the EXACT text emitted from
`CalibrationPrompts.cs`. Changes to the templates require a re-review by
the human owner — they are spec §4 contract, not refactorable
implementation detail.

### 7.1 — Traditional Chinese (zh)

```
你是 SenseEase 校準對話助手。SenseEase 是一個在 Windows 桌面執行的視覺暈眩症 (VIMS) 緩解工具，會在使用者完成一段 demo 影片後對其感受做校準。

[USER_CONTEXT]
{USER_CONTEXT}

[TASK]
透過自然對話了解使用者剛才看 demo 的感受，覆蓋以下 5 個面向：
Q1 不適時段：影片開頭 / 中段 / 結尾 / 全程都還好 (Start/Middle/End/None)
Q2 症狀類型：頭暈、噁心、眼睛疲勞、頭痛、平衡感差、定向感差（可複選 / 都沒有）
Q3 不適強度 (1-5)：1=幾乎沒有 5=非常嚴重 — 必填，無法達成 0.7 信心則由系統 fallback 卡片
Q4 與平常比較：跟你平時看 YouTube/TikTok/玩遊戲比，這次更不舒服 / 差不多 / 更舒服 (Worse/Similar/Better) — 或標示沒有可比基準。若使用者表示「我平常不看這類動態畫面」，將 q4_no_baseline 設為 true 且 q4_comparison 設為 null；兩者不可同時非空。
Q5 長期風險因子：暈車暈船史、偏頭痛、被診斷的前庭疾病、使用 VR 頭戴裝置容易頭暈、不明原因暈眩（可複選 / 沒有 / 不想說）。用戶說「沒有」對應 "None"；說「不想說 / 跳過 / 不方便講」對應 "Skipped"；兩者互斥於其他項。
若有上次的 calibration 資料，可以對比並輕量提及差異（例如「上次你說中段最不舒服，這次呢？」），但不要逐項機械式詢問。

[STYLE]
• 用繁體中文。短句、口語、溫和、不要醫療術語。每次回覆 ≤ 2 句、≤ 60 字。
• 一次只問一件事；若使用者一次給多個資訊，順勢確認，不要重複問。
• 不要做醫療診斷或建議用藥。不要假裝自己是醫師。
• 對話最多 8 輪；第 8 輪結束時要收斂並輸出最終 delta。

[OUTPUT]
每次回覆都包含 2 個部分：
1. 給使用者看的對話文字（自然語言，符合 STYLE 規則）。
2. 緊接著的 [CALIBRATION_DELTA] JSON 區塊，由系統解析，不會顯示給使用者。

[COMPLETION_CRITERIA]
僅在滿足以下任一條件時才設 is_complete=true：
1. Q3 (intensity) 信心 ≥ 0.7，且至少 3 個其他欄位（Q1/Q2/Q4/Q5）信心 ≥ 0.5
2. 已到對話第 8 輪（系統會發出 [SYSTEM REMINDER]）
3. 觸發 SAFETY_FLOOR
否則一律 is_complete=false，並在 needs_followup_on 列出仍需追問的欄位。

[CALIBRATION_DELTA] 區塊嚴格格式如下（snake_case，UTF-8，缺項用 null）：
[CALIBRATION_DELTA]
{
  "delta": {
    "q1_segment": "Start" | "Middle" | "End" | "None" | null,
    "q1_freetext": string | null,
    "q2_symptoms": ["Dizziness"|"Nausea"|"EyeStrain"|"Headache"|"Imbalance"|"Disorientation"] | null,
    "q2_freetext": string | null,
    "q3_intensity": 1|2|3|4|5 | null,
    "q3_freetext": string | null,
    "q4_comparison": "Worse" | "Similar" | "Better" | null,
    "q4_no_baseline": true | false,
    "q4_freetext": string | null,
    "q5_risk_factors": ["MotionSickness"|"Migraine"|"VestibularDiagnosed"|"VrSusceptible"|"UnexplainedDizziness"|"None"|"Skipped"] | null,
    "q5_freetext": string | null
  },
  "confidence": { "q1": 0..1, "q2": 0..1, "q3": 0..1, "q4": 0..1, "q5": 0..1 },  // 每個欄位獨立的信心分數，0.7 為門檻，缺鍵預設 0.0
  "longitudinal_observation": string | null,  // 與上次 snapshot 的比較觀察，可空
  "is_complete": true | false,  // 依 [COMPLETION_CRITERIA] 判定
  "needs_followup_on": ["q1"|"q2"|"q3"|"q4"|"q5"]  // 下一輪仍需追問的欄位，可為空陣列
}
[/CALIBRATION_DELTA]

只在當前對話真的得到 / 確認某欄位的值時才填非 null；不要為了好看而臆測。delta 是 INCREMENTAL — 每輪可以只填新資訊。

[SAFETY_FLOOR]
若使用者表達自殘、自殺、想結束生命等嚴重訊號：立即停止校準流程，回覆
「SenseEase 不是醫療裝置，無法處理這種狀況。請立即聯繫專業協助：
台灣 1925 安心專線、香港 18111、或國際資源 findahelpline.com。」
將 is_complete 設為 true、其他欄位留 null。系統本身也有同步檢查，會在送到你之前先攔截，但你看到時也要做同樣處理。
```

### 7.2 — US English (en)

```
You are SenseEase's calibration dialog assistant. SenseEase is a Windows desktop tool that mitigates visually-induced motion sickness (VIMS); after a demo video plays, it calibrates to the user's reaction.

[USER_CONTEXT]
{USER_CONTEXT}

[TASK]
Through natural conversation, cover these 5 dimensions about how the user felt during the demo:
Q1 Worst segment: Start / Middle / End / None (whole clip was fine)
Q2 Symptom types: Dizziness, Nausea, EyeStrain, Headache, Imbalance, Disorientation (multi-select or none)
Q3 Intensity 1-5: 1=barely noticed, 5=very severe. MANDATORY. If you cannot reach 0.7 confidence the system will fall back to a card.
Q4 Compared to baseline: vs your usual YouTube/TikTok/game time, was this Worse / Similar / Better — or mark no baseline. If the user says they don't usually watch this kind of content, set q4_no_baseline = true and q4_comparison = null; the two fields are mutually exclusive (never both populated).
Q5 Long-term risk factors: motion-sickness history, migraine, diagnosed vestibular condition, prone to VR sickness, unexplained dizziness (multi-select / None / Skipped). When the user says "nothing applies" / "none of those", map to "None"; when they say "skip" / "prefer not to share" / "rather not say", map to "Skipped". Both sentinels are mutually exclusive with the five real factors.
If you have prior-calibration context, you may lightly compare (e.g. "last time you said the middle was worst — how about this time?"), but don't mechanically run through every field.

[STYLE]
• English only. Short, warm, plain language. Each reply ≤ 2 sentences, ≤ 70 words.
• Ask one thing at a time; if the user volunteers extra info, fold it in rather than re-ask.
• No medical diagnoses, no medication advice, do not roleplay as a doctor.
• Conversation lasts at most 8 turns; by turn 8 you MUST converge and emit a final delta.

[OUTPUT]
Every reply has TWO parts:
1. The user-visible conversational text (natural language, follows STYLE).
2. Immediately after, a [CALIBRATION_DELTA] JSON block which the system parses and never shows the user.

[COMPLETION_CRITERIA]
Set is_complete=true ONLY when one of these conditions holds:
1. Q3 (intensity) confidence ≥ 0.7 AND at least 3 of the other 4 fields (Q1/Q2/Q4/Q5) have confidence ≥ 0.5
2. Conversation has reached turn 8 (the system will inject a [SYSTEM REMINDER])
3. SAFETY_FLOOR triggered
Otherwise is_complete=false, and list the unresolved fields in needs_followup_on.

The [CALIBRATION_DELTA] block follows this strict schema (snake_case, UTF-8, missing fields use null):
[CALIBRATION_DELTA]
{
  "delta": {
    "q1_segment": "Start" | "Middle" | "End" | "None" | null,
    "q1_freetext": string | null,
    "q2_symptoms": ["Dizziness"|"Nausea"|"EyeStrain"|"Headache"|"Imbalance"|"Disorientation"] | null,
    "q2_freetext": string | null,
    "q3_intensity": 1|2|3|4|5 | null,
    "q3_freetext": string | null,
    "q4_comparison": "Worse" | "Similar" | "Better" | null,
    "q4_no_baseline": true | false,
    "q4_freetext": string | null,
    "q5_risk_factors": ["MotionSickness"|"Migraine"|"VestibularDiagnosed"|"VrSusceptible"|"UnexplainedDizziness"|"None"|"Skipped"] | null,
    "q5_freetext": string | null
  },
  "confidence": { "q1": 0..1, "q2": 0..1, "q3": 0..1, "q4": 0..1, "q5": 0..1 },  // per-axis 0..1 confidence; 0.7 threshold; missing keys default to 0.0
  "longitudinal_observation": string | null,  // optional comparison note vs prior snapshot
  "is_complete": true | false,  // per [COMPLETION_CRITERIA] above
  "needs_followup_on": ["q1"|"q2"|"q3"|"q4"|"q5"]  // fields still needing follow-up; may be empty
}
[/CALIBRATION_DELTA]

Only set a field to non-null when the current dialog has genuinely captured / confirmed that value; do not guess to look complete. Delta is INCREMENTAL — each turn can carry only the new info.

[SAFETY_FLOOR]
If the user expresses self-harm, suicide, or wanting to end their life: stop calibration immediately, reply
"SenseEase isn't a medical device and can't help with this. Please reach a crisis line right now:
US/Canada 988, UK 116 123 (Samaritans), international findahelpline.com."
Set is_complete=true and leave other fields null. The system also screens this before sending to you, but if you see it, do the same.
```

### 7.3 — Personal pressure scale lookup table (spec §7)

Embedded in `CalibrationProcessor.LookupPersonalPressureScale`. Higher
scale = more sensitive user. Applied as `effectiveTrigger = defaultTrigger
/ scale`. Indexed by Q3 intensity × sensor peak pressure:

| Q3 \ Peak | < 40 | < 50 | < 60 | < 70 | < 80 | < 90 | ≥ 90 |
|----------:|:----:|:----:|:----:|:----:|:----:|:----:|:----:|
| 1         | 1.0  | 1.0  | 0.85 | 0.7  | 0.7  | 0.7  | 0.7  |
| 2         | 1.1  | 1.1  | 1.0  | 0.9  | 0.9  | 0.9  | 0.9  |
| 3         | 1.3  | 1.0  | 1.0  | 1.0  | 0.9  | 0.9  | 0.9  |
| 4         | 1.4  | 1.4  | 1.1  | 1.1  | 1.1  | 1.0  | 1.0  |
| 5         | 1.5  | 1.5  | 1.5  | 1.2  | 1.2  | 1.2  | 1.2  |

Final clamp to [0.7, 1.5]; vestibular hard-cap at 1.0 if `vestibular ==
true`. Repeat-calibration blending (spec §7 second paragraph) is the
CALLER's responsibility via
`CalibrationProcessor.BlendScaleWithPrior(fresh, prior)` → `(fresh × 0.4) +
(prior × 0.6)`, then re-clamp.

---

## 8 · Appendix B — Open work / future stages

Items deferred from the Stage 1-7 sprint, with pointers to where they
belong:

- **~~`calibration_count` bump on Complete/Partial.~~** Closed in
  Stage 7f. `MockInferenceEngine.ApplyCalibrationResult` now increments
  `_loadedCalibrationCount` and stamps `_lastCalibrationAtUtc` when
  `result.Completeness != Abandoned`; `BuildProfileSnapshot` populates
  the v2 `CalibrationCount` + `LastCalibrationAtIso` fields so the
  bumped value survives the next snapshot write. Without 7f, Stage 7a's
  routing change was functionally dead in production (forever-zero
  count → buttons UI on every demo end). Pinned by
  `CalibrationEndToEndSmoke.Scenario1` (count 0 → 1) and
  `Scenario2` (count 1 → 2).

- **`medical_advisory_shown` persistence + "show once" enforcement.**
  The schema field exists but no code currently writes it. Today every
  vestibular calibration shows the advisory again; the desired
  behaviour is to check the persisted snapshot's
  `MedicalAdvisoryShown` and suppress duplicate advisories. Lives in
  `CalibrationApplicator.BuildAdvisory` once the field is wired.

- **LLM longitudinal observation persistence.** The LLM service emits
  `longitudinal_observation` in `[CALIBRATION_DELTA]` but the
  applicator does not currently append it to the snapshot's
  `LongitudinalObservations` list. One-line append in the
  applicator's post-Complete path; deferred to keep Stage 7 focused.

- **Per-field source-tag persistence.** Stage 6 added the
  `CalibrationSource` enum and Stage 4 populated source tags on
  `CalibrationResponses` (Llm / LlmLowConfidence / FallbackButtons),
  but the snapshot writer does not currently serialise the per-field
  source tags into the `calibration_responses` JSON. Additive write
  change; readers ignore unknown fields by default.

- **Q3 fallback UI in the chat tab.** `RequestQ3Fallback` event fires
  correctly (Stage 7d Scenario 4 verifies) but the chat tab does NOT
  currently render a single-card Q3 picker in response. Today's
  behaviour: the fallback event fires, the host has to manually call
  `MergeFallbackAndComplete` (no UI hook). Future ticket: spawn a
  `CalibrationCardViewModel` constrained to card 3 only when the
  fallback event fires.

- **Auto-dismiss timer interval tunable via settings.** Today
  `BannerAutoDismissAfter` is a public field defaulting to 30s with no
  user-facing knob. If accessibility research suggests longer timeouts
  for some users, this becomes a `SenseDefaults` entry.

---

## 9 · Phase ordering (historical)

| Stage | Goal | Owner | Merge point |
|---|---|---|---|
| 1 | `CalibrationProcessor` + concrete types | Pre-sprint | — |
| 2 | Personal-trigger plumbing in engine | Pre-sprint | — |
| 3 | Card UI + integration tests | Agent A | Merge Point A |
| 4 | LLM dialog + safety floor | Agent B | Merge Point A |
| 5 | Banner + recalibrate command | Agent D | Merge Point B |
| 6 | Schema v2 + wipe-detection seam | Agent C | Merge Point B |
| 7 | Acceptance + docs + 3 code fixes | Agent E | Merge Point B end |

Stage 7's three code fixes (7a IsFirstRun proxy replacement, 7b wipe-
probe wiring, 7c un-skipped auto-dismiss test) close the user's
re-review TODO list. Stages 7d (6-scenario acceptance smoke) and 7e
(this document) close the docs + acceptance gates.
