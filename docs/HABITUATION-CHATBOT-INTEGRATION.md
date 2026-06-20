# Habituation × Chatbot Integration

> **Audited at**: `v0.9-phase7` (Habituation Phase 7 sealed) on top of
> `v0.9-phase6` (Phase 6 sealed). Patched per advisor review 2026-05-28.

Reference for Phase 6 (chatbot **reads** habituation state) + Phase 7 (chatbot
**proposes** engine-state changes that the user confirms).

This document is for engineers maintaining the chatbot ↔ engine seam. End-user
behavior lives in [POPUP-OVERLAY.md](POPUP-OVERLAY.md) and
[CALIBRATION.md](CALIBRATION.md). Cross-sprint integration risks +
lessons learned: see
[POPUP-AND-HABITUATION-INTEGRATION-AUDIT.md](POPUP-AND-HABITUATION-INTEGRATION-AUDIT.md).
System-wide safety design: see [SAFETY-PROVISIONS.md](SAFETY-PROVISIONS.md).

---

## 1. Architecture

```
┌─────────────────────┐   read       ┌──────────────────┐
│   ChatbotViewModel  │ ───────────► │ HabituationState │
│   BuildContextual-  │              │   BlockBuilder    │  (Phase 6 read-side)
│   Input             │ ◄─────────── │   + Trajectory    │
└──────────┬──────────┘    block     └──────────────────┘
           │
           │ user text + [habituation_state] block
           ▼
   ┌───────────────┐
   │  Mistral 3B   │  (or lexicon fallback for stub-LLM path)
   │  LLM          │
   └──────┬────────┘
          │ raw reply (envelope + structured blocks)
          ▼
┌───────────────────────────┐
│ HabituationOverrideParser │  Phase 7b: extracts up to one
│ TryParse                  │     [PROFILE_OVERRIDE], 0-2
│ StripAllOverrideBlocks    │     [OBSERVATION_ADD], optional
└──────────┬────────────────┘     [RESUME_ACKNOWLEDGE]
           │
           ├──► Observations → LongitudinalObservationWriter   (Phase 7g)
           │                          (Jaccard dedup, 50-entry FIFO)
           │
           ▼
┌───────────────────────────┐
│ HabituationOverride-      │  Phase 7c: 5 validation rules
│ Processor.Validate        │     (expiry / value-range / vestibular /
└──────────┬────────────────┘      pending / cooldown)
           │ accepted
           ▼
┌───────────────────────────┐
│ ProposalSurfaceRequested  │  MainViewModel clamps expiry to ≤ 7d
└──────────┬────────────────┘
           ▼
┌───────────────────────────┐    Card-embedded confirm UI
│ NotificationCardWindow    │    [接受] [拒絕] [稍後]
│ ActiveProposal section    │    + expiry countdown
└──────────┬────────────────┘
           │ user clicks Accept
           ▼
┌───────────────────────────┐  Phase 7d + 7f: re-checks safety
│ HabituationOverride-      │  intercept at apply time even after
│ Applicator.ApplyAccepted  │  Processor cleared (snapshot may
└──────────┬────────────────┘  have changed)
           │ not blocked
           ▼
┌───────────────────────────┐
│ MockInferenceEngine.      │  Single write path under engine lock:
│ ApplyChatbotOverride      │     AdjustSensitivity → EffectiveTrigger
└──────────┬────────────────┘     SwitchProfile     → SetProfile
           │                       AdjustStrikeLimit → SevereStrikeLimitOverride
           ▼
   ┌───────────────────┐         + ChatbotActionLog (Phase 7g)
   │  HabituationEngine │           + IEventLogStore.Append
   │  state changes     │           + profile snapshot persist
   └───────────────────┘
```

The chatbot is a **proposer, not an actor**. Every engine-state change
requires explicit user confirmation (Card click) AND passes through two
deterministic safety gates (Processor + Applicator). The LLM cannot
silently mutate habituation state — even a misbehaving model can only
*propose*; the user and the safety stack decide whether to apply.

---

## 2. Read-side specification (Phase 6 / `v0.9-phase6`)

### 2.1 `[habituation_state]` block format

`HabituationStateBlockBuilder.BuildBlock(snapshot, trajectorySummary, powerSaveActive)`
produces a token-diet block (~120 tokens typical) that ChatbotViewModel
prepends to every user-message turn:

```
[habituation_state]
tier: <1-10>
level: <F1>
profile: <REACTIVE|ADAPTING|RESILIENT>
state: <RUNNING|COOLDOWN|PAUSED_AWAITING_USER_CONFIRM>
[awaiting_user_confirm: true]               (omitted if false)
[power_save_active: true]                   (omitted if false)
[advisory: vestibular|high_risk_combo]      (omitted if null)
calibrations: <N>
last_cal: <YYYY-MM-DD>                      (omitted if never)
[recent_observations]
- <obs 1>
- ...
[trajectory: <one-line summary>]            (omitted if empty)
[/habituation_state]
```

Field omission saves tokens for the common-case "no advisory, no power save,
no observations" path. The system prompt's HABITUATION STATE CONTEXT section
documents these fields verbatim so the LLM can reason about them by name.

### 2.2 Trajectory summary

`HabituationTrajectoryBuilder.BuildSummary(eventLog, windowDays=30)` reads
`Habituation.*` events from `IEventLogStore` and produces a one-line summary
like `"3 level changes, 1 spike"`. Drives the LLM's "you've been progressing"
replies.

Requires the M1 habituation event-log writes (added in `v0.9-phase6` pre-work).
Without those events the summary returns `"no habituation activity in last N days"`.

### 2.3 Power Save state

Power Save is **session-scoped** (not persisted in `UserPreferences`) per
Pop-up sprint iter 2's design. MainWindow owns the source of truth;
ChatbotViewModel reads through a `Func<bool>` host callback set at construction
(`IsPowerSaveActive`). No schema bump — Power Save reflection happens via
caller-side dynamic query.

---

## 3. Write-side specification (Phase 7 / `v0.9-phase7`)

### 3.1 Structured token grammar

Three block types the LLM may emit AFTER the JSON envelope, on separate lines:

```
[PROFILE_OVERRIDE]
{ "intent": "adjust_sensitivity"|"switch_profile"|"adjust_strike_limit",
  "target_field": <snake_case engine field name>,
  "proposed_value": <number | string per intent>,
  "rationale": "<natural language reason>",
  "expires_at_iso": "<UTC ISO8601, ≤ 7 days from now>" }
[/PROFILE_OVERRIDE]

[OBSERVATION_ADD]
<one-line observation about the user, ≤ 200 chars>
[/OBSERVATION_ADD]

[RESUME_ACKNOWLEDGE]
{ "user_confirmed": true|false,
  "user_quote": "<verbatim slice of user text>" }
[/RESUME_ACKNOWLEDGE]
```

Spec rules:
- At most one `[PROFILE_OVERRIDE]` per reply (first wins if model misbehaves)
- 0-2 `[OBSERVATION_ADD]` per reply
- `[RESUME_ACKNOWLEDGE]` valid only when `awaiting_user_confirm=true`
- All blocks stripped from the visible reply by `StripAllOverrideBlocks`
  before the bubble renders

Locked decision Q4.5: `ResetOffset` is NOT an `OverrideIntent`. The parser
silently drops PROFILE_OVERRIDE blocks whose intent is `"reset_offset"` — a
chatbot proposing a full habituation reset is too high-impact for the
propose-confirm channel; users seeking that path use Settings.

### 3.2 Tolerance net

`HabituationOverrideParser` survives Mistral 3B's structured-output unreliability
via four layers:

1. **`RawModelOutput` carry**: `MistralChatbotService` stamps `NluResult.RawModelOutput`
   with the full LLM output (post-markdown-strip). The parser runs on raw so
   blocks survive envelope-deserialize failures.
2. **Tag-name tolerance**: regex matches `PROFILE_OVERRIDE` plus
   `OVERSIGHT|PROPOSAL|ADJUST` (case-insensitive) — known hallucinations
   verified from production traces.
3. **Expiry clamp**: `MainViewModel` clamps stored `ExpiresAtUtc` to
   `min(model_value, now + 7d)`. Mistral 3B reliably overshoots the 48h
   target by hours-to-days; we don't discard an otherwise-valid proposal
   over date arithmetic.
4. **Lexicon fallback** (`HabituationProposalLexicon.TryDetect`): if the LLM
   didn't emit a parseable block but the user explicitly named an action
   (e.g. "switch to RESILIENT", "loosen sensitivity"), C# builds the
   proposal deterministically.

### 3.3 Validation rules (Phase 7c `HabituationOverrideProcessor`)

Five gates, all unit-covered:

| # | Rule | Failure mode |
|---|------|--------------|
| 1 | `expires_at_iso` in the future | Past → reject |
| 2 | `proposed_value` in valid range for intent | Out of range → reject |
| 3 | Vestibular advisory + relaxation | → reject with `SafetyNote` |
| 4 | One proposal at a time | Another pending → reject |
| 5 | 7-day cooldown after a decline | Same intent declined < 7d ago → reject |

Rules 4 and 5 require host state — wired via `isAnotherProposalPending: bool`
and `mostRecentDeclineFor: Func<OverrideIntent, DateTime?>` parameters.

### 3.4 Defensive intercept (Phase 7f, inside `HabituationOverrideApplicator`)

Re-checks safety at **apply time** even after the Processor cleared at
propose time — the snapshot may have changed between propose and accept
(user runs a new calibration mid-Card etc.). Three rules in specificity
order:

1. `strike_limit == 1 + Vestibular` + `AdjustStrikeLimit` → block (vestibular hard cap)
2. `Vestibular` + ANY relaxation → block (delegates to `IsRelaxationProposal`)
3. `HighRiskCombo` + SIGNIFICANT relaxation (≥15% relative change) → block

Profile switches are always significant — they're enum-stepped, not continuous.

### 3.5 Engine write path

`MockInferenceEngine.ApplyChatbotOverride(intent, value)` performs the
write under the engine's existing lock:

| Intent | Engine action |
|---|---|
| `AdjustSensitivity` | `_personalPressureScale = scale; _habituation.EffectiveTrigger = TriggerPressure / scale` |
| `SwitchProfile` | `_habituation.SetProfile(profile, source: "chatbot_override")` |
| `AdjustStrikeLimit` | `_habituation.SevereStrikeLimitOverride = limit` |

Per Decision 7, `HabituationEngine` public API was NOT extended — the chatbot
path uses the same setters/methods calibration already does.

`_personalPressureScale` is a new field on `MockInferenceEngine`: previously
the calibration path computed the scale once for the trigger then discarded.
v0.9-phase7 tracks it so the snapshot's `personal_pressure_scale` reflects
the live value and subsequent chatbot adjustments have the right baseline.

### 3.6 Card UI (Phase 7e Flow A)

`NotificationCardWindow.ActiveProposal` setter reveals the proposal section
above the existing body text:

```
+----------------------------------------+
| 💡 SenseEase 建議                     |
| 把 sensitivity 調整為 0.9              |
| 理由：用戶最近 5 天 tier 穩定在 6-7    |
| [接受] [拒絕] [稍後]    剩 3 小時 59 分 |
+----------------------------------------+
```

- **Accept**: routes to `HabituationOverrideApplicator.ApplyAccepted`.
  Success → section clears. Safety block → section stays visible with
  ⚠ red `SafetyNote` + Accept button disabled.
- **Decline**: `RecordDeclined` (drives Processor rule 5 cooldown). Section
  clears immediately.
- **Later**: hides the card; proposal stays pending in MainViewModel until
  `ExpiresAtUtc`. Next bar-click re-surfaces it.

Expiry watchdog: `MainViewModel` runs a 1-minute tick. When `_activeProposal`
crosses `ExpiresAtUtc`, calls `applicator.RecordExpired` and clears.

### 3.7 In-chat profile picker

When user input matches `HabituationProposalLexicon.TryDetectProfilePickerRequest`
(bare word "profile" / "我想換 profile" / "show me profile options" / etc.),
`ChatbotViewModel.SendAsync` short-circuits BEFORE calling the LLM and
pushes a picker bubble into `Messages`:

```
請選擇您想切換的 profile（目前: TYPICAL）：
┌──────────────────────────────────────┐
│ RESILIENT                            │
│ 為高耐受族群設計。視覺保護觸發較緩慢… │
└──────────────────────────────────────┘
┌──────────────────────────────────────┐
│ TYPICAL（目前）                      │
│ 一般使用者預設…                       │
└──────────────────────────────────────┘
┌──────────────────────────────────────┐
│ SENSITIVE                            │
│ 為敏感族群設計。視覺保護更早觸發…    │
└──────────────────────────────────────┘
```

User click → `MainViewModel.OnChatbotProfilePickerSelected` builds a
SwitchProfile proposal and routes it through the SAME `HabituationOverrideApplicator.ApplyAccepted`
path (same defensive intercept + same audit). Result pushed back as an
assistant message ("已切換到 RESILIENT profile" / "⚠ 無法切換到 RESILIENT：…").

The picker doesn't intermediate through the pop-up Card — the user already
explicitly chose, so the second confirmation would be busywork.

---

## 4. Safety provisions

See [SAFETY-PROVISIONS.md](SAFETY-PROVISIONS.md) for the system-wide safety
design covering safety floor, medical advisory persistence, defensive
intercept layer, and crisis resource directory.

Summary for the chatbot path:

- **Safety floor (M0)**: self-harm phrase short-circuit at the top of
  `MistralChatbotService.SendAsync` returns a hardcoded crisis resource
  response. The phrase NEVER reaches the LLM. Phrases tables duplicated
  with `CalibrationChatbotService` for independent auditability.
- **Vestibular hard cap**: `severe_strike_limit = 1` set by vestibular
  calibration cannot be raised by the chatbot. Processor rule 3 + Applicator
  intercept rule 1 both enforce.
- **Relaxation vs vestibular**: ANY relaxation proposal from a vestibular
  user is blocked — both at propose time (Processor) and apply time
  (Applicator). User sees the `SafetyNote` inline so the system isn't
  randomly stonewalling them.
- **High-risk-combo**: small adjustments allowed; significant relaxation
  (≥15% relative) blocked.

---

## 5. Failure modes

| Mode | Detection | Mitigation |
|---|---|---|
| LLM emits wrong envelope intent (e.g. `"adjust_sensitivity"` in outer envelope) | Envelope deserialize fails | Lexicon fallback path + RawModelOutput carry catches the structured block independently |
| LLM hallucinates tag name (`OVERSIGHT`/`PROPOSAL`/`ADJUST`) | Parser regex misses | Regex alternation explicitly accepts known variants |
| LLM overshoots expires_at_iso | Processor rule 1 would reject | `MainViewModel` clamps to `min(model, now + 7d)` before storing |
| LLM proposes vestibular-conflicting change | Processor rule 3 | Reject with `SafetyNote`; Applicator rule 1+2 second line of defense |
| User declines, LLM re-proposes immediately | Processor rule 5 | 7-day cooldown lookup via ChatbotActionLog |
| Concurrent proposals (multiple chatbot turns racing) | Processor rule 4 | `isAnotherProposalPending` flag rejects the second |
| `[OBSERVATION_ADD]` spam | Lexicon dedup | Jaccard similarity ≥ 0.7 against last 5 entries → skip |
| 50+ observations accumulated | Persistence-time | Writer FIFO-evicts beyond `MaxObservations = 50` |
| Card auto-popped over a hidden user click | OnIsVisibleChanged reset | All per-Show state (Opacity, idle timer, in-flight flag) resets on each Show |
| Applicator event-log writes invisible in dashboard | UI-layer mirror | `IEventLogStore.Appended` event lets MainViewModel mirror to bound `EventLog` collection |

---

## 6. Test coverage map

| Layer | Tests | Where |
|---|---|---|
| Parser | 25 contract + 3 real-trace regression | `HabituationOverrideParser[RealLlmTrace]Tests` |
| Processor | 37 (5 rules × positive/negative + relaxation predicate) | `HabituationOverrideProcessorTests` |
| Applicator + 7f intercept | 14 (3 intent apply paths + 4 intercept rules + decline/expire) | `HabituationOverrideApplicatorTests` |
| Lexicon | 51 (specific/relax/tighten/profile/strike + picker-request + bare-word) | `HabituationProposalLexiconTests` |
| LongitudinalObservationWriter | 9 (dedup, FIFO, length cap, blank skip) | `LongitudinalObservationWriterTests` |
| InMemoryChatbotActionLog | 7 (MostRecentDeclineFor, Recent ordering) | `InMemoryChatbotActionLogTests` |
| HabituationStateBlockBuilder | 14 (Phase 6 scenarios) | `HabituationStateBlockBuilderTests` |
| HabituationTrajectoryBuilder | 7 | `HabituationTrajectoryBuilderTests` |
| Read-side scenarios | 5 (Phase 6e) | `ChatbotViewModelPhase6Tests` |
| Write-side acceptance | 11 scenarios + 4-week simulation | `Phase8AcceptanceTests` |

Total chatbot ↔ engine test count: 193. Full suite: 629 / 0 / 1 skipped.

---

## 7. Known limitations

- **Mistral 3B reliability**: the model gets the envelope+block contract right
  most of the time but not always. The four-layer tolerance net (raw carry,
  tag tolerance, expiry clamp, lexicon fallback) keeps the system functional
  but the failure modes are real and worth knowing about. Section 5 lists
  each mode + mitigation; the `model_trace.jsonl` log surfaces what the
  model actually emitted on each turn for debugging.
- **In-memory ChatbotActionLog**: process-scoped. App restart loses
  Proposed/Accepted/Declined/Expired/Intercepted history. Q4.6-locked
  stateless decision; the Processor's cooldown rule 5 silently degrades
  to "no cooldown" across restarts. If v1.0 wants cross-restart memory,
  swap for a JSONL-file-backed implementation.
  <br>**Safety-relevant intersection**: for vestibular / high-risk-combo
  users, the in-memory cooldown means the chatbot may re-propose the
  SAME relaxation that was declined last session — the defensive intercept
  (§3.4) catches the apply but the propose repeats indefinitely. See
  [SAFETY-PROVISIONS §7.2](SAFETY-PROVISIONS.md#72-ichatbotactionlog-process-scoped-in-memory-only-at-v09)
  for the full cross-doc analysis + v1.0 mitigation (split decline-
  history persistence by safety-relevance).
- **One active proposal at a time** (Decision 10): newer proposals don't
  auto-supersede; they're rejected by Processor rule 4. Means a chatbot
  trying to propose a fresh change while the user has a pending Later
  silently fails. Acceptable trade-off — prevents proposal spam.
- **Pending proposal NOT persisted** (Decision 9): app restart clears
  `_activeProposal`. Prevents ghost-applies but loses Later context
  across restarts.
- **No E2E with real LLM in CI**: scenarios use scripted raw outputs.
  Real-LLM verification is manual via `publish-test/SenseEase.App.exe`
  (model_trace.jsonl + dashboard event log).
- **Bar-click re-surface of Later proposal**: works via
  `MainViewModel.ActiveProposal` lookup in
  `MainWindow.ShowNotificationCardForCurrentTier`. Requires the proposal
  hasn't expired (filtered defensively even though the expiry watchdog
  should have cleared).

---

## 8. Locked decisions reference

- Q4.3: `expires_at_iso` default 48h, hard cap 7 days
- Q4.5: `ResetOffset` NOT in `OverrideIntent` enum
- Q4.6: stateless chatbot — InMemoryChatbotActionLog acceptable for v0.9
- Q4.7: separate `ChatbotActionLog` (distinct from `IAdjustmentLog`)
- Decision 1: Path I caller-side enrichment (no MistralChatbotService internal changes)
- Decision 2: Schema NOT bumped to v3
- Decision 3: Three structured token types (PROFILE_OVERRIDE / OBSERVATION_ADD / RESUME_ACKNOWLEDGE)
- Decision 4: Flow A (Card-embedded user confirm)
- Decision 5: Safety floor migration was Phase 7a (already done at v0.8.2)
- Decision 6: Defensive intercept at Applicator layer
- Decision 7: HabituationEngine public API NOT extended
- Decision 8: E2E test infra deferred to per-phase incremental work
- Decision 9: Pending proposal NOT persisted across app restart
- Decision 10: One active proposal at a time
