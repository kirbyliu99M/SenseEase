# SenseEase Safety Provisions

Inventory of every safety mechanism in the system, who enforces it, and what
fails if it goes wrong. Audited at `v0.9-phase7`.

This document is the reference for:
- Engineers maintaining the safety stack — do not bypass these gates
- Code reviewers — verify changes don't weaken any layer
- Auditors / regulators — single doc covering the full safety surface

## 1. Safety floor (crisis-line short-circuit)

**Where**: `MistralChatbotService.TripsSafetyFloor` +
`CalibrationChatbotService.TripsSafetyFloor`. Both chatbot services have
their own copy of the phrase tables + check function (deliberately
duplicated rather than shared via utility, so each service is independently
auditable).

**What**: detects self-harm / severe-distress phrases in user text BEFORE
any LLM call. On match, returns a hardcoded crisis-line response:

```
我聽到你了。SenseEase 不是合適的對話對象,但有人可以聽你說。
台灣 1925 安心專線(24小時免費)
或 https://findahelpline.com
請和他們聊聊。
```

(EN variant for English-language input.)

**Phrase tables**:

```
zh: 自殘, 自殺, 結束生命, 跳下去, 跳樓, 不想活了, 不想活下去,
    不想再活, 想死了, 想去死, 我想死
en: suicide, self-harm, self harm, kill myself, end it all,
    end my life, not want to live, want to die, wanna die
```

**Invariants**:
- The triggering text NEVER reaches the LLM (zero LLM calls on safety floor)
- Crisis response is hardcoded — LLM never gets to paraphrase
- Match is substring (case-insensitive for en, exact for zh) — no fuzzy logic
- Floor sits BEFORE the lexicon, NLU, and chatbot send paths

**Test coverage**: 8 contract tests (21 Theory rows + 2 [Fact]) in
`MistralChatbotServiceTests` and `CalibrationChatbotServiceTests`. Includes
zh, en, mixed-case, and "must never call LLM" mock verification.

**Failure mode if disabled**: user expressing crisis intent gets a
hallucinated LLM reply about visual comfort settings. Reputational + harm
risk.

---

## 2. Medical advisory persistence

**Where**: `HabituationProfileSnapshot.MedicalAdvisoryShown` (enum, persisted
in `habituation_profile.json` schema v2).

**What**: stores which medical advisory (if any) the calibration flagged
for this user:

- `Vestibular` — calibration Q5 marked `RiskFactor.VestibularDiagnosed`
- `HighRiskCombo` — calibration triggered the high-risk-combo path
- `null` — no advisory

**Why persisted**: the spec's "show advisory at most once per kind per
user" rule needs to survive app restart. Also drives:

- Defensive intercept (see §4 below) — which proposals to block
- Vestibular hard cap — `severe_strike_limit = 1` set by vestibular
  calibration cannot be raised by the chatbot
- Chatbot system-prompt context — advisory is part of the
  `[habituation_state]` block when non-null

**Invariants**:
- Set once by `CalibrationProcessor.Process` when the calibration responses
  warrant
- NEVER cleared by chatbot
- NEVER cleared by re-calibration (Stage 7f rule)

**Failure mode if not persisted**: vestibular-diagnosed user reopens the
app, chatbot has no record of their condition, accepts a relaxation
proposal that would have been blocked.

---

## 3. Vestibular hard cap

**Where**: enforced at three layers (defense in depth):

1. `CalibrationProcessor` — sets `SevereStrikeLimit = 1` when
   `Q5RiskFactors` contains `VestibularDiagnosed`, AND caps
   `PersonalPressureScale ≤ 1.0`.
2. `HabituationOverrideProcessor.Validate` (Phase 7c rule 3) — rejects
   ANY relaxation proposal when snapshot has Vestibular advisory.
3. `HabituationOverrideApplicator.CheckSafetyIntercepts` (Phase 7f
   rule 1+2) — re-checks at apply time:
   - Rule 1: `AdjustStrikeLimit` + current `severe_strike_limit == 1` +
     Vestibular advisory → block (the specific "vestibular hard cap"
     message)
   - Rule 2: Vestibular advisory + ANY relaxation → block

**Why defense in depth**: the Processor check is the proactive gate
(don't surface the Card if we'd just have to reject Accept). The
Applicator check is the belt-and-suspenders: the persisted snapshot may
have changed between propose time and accept time (user runs a new
calibration mid-Card etc.) — an apply-time re-check is the only correct
place to enforce.

**What counts as "relaxation"**: `HabituationOverrideProcessor.IsRelaxationProposal`
(public, reused by Applicator):

- `AdjustSensitivity`: proposed_value < current scale (lower scale =
  higher effective trigger = less sensitive = more relaxed)
- `SwitchProfile`: proposed profile is LESS RESTRICTIVE than current
  (RESILIENT < TYPICAL < SENSITIVE)
- `AdjustStrikeLimit`: proposed limit > current (more strikes
  tolerated = more relaxed)

**Failure mode if disabled**: vestibular-diagnosed user accepts a chatbot
relaxation suggestion; engine becomes less protective; user's symptoms
escalate without intervention.

---

## 4. Defensive intercept layer

**Where**: `HabituationOverrideApplicator.CheckSafetyIntercepts` (static,
exposed for tests + UI preview).

**Three rules in specificity order** (apply-time, not propose-time):

| # | Trigger | Block reason |
|---|---------|--------------|
| 1 | `AdjustStrikeLimit` + `severe_strike_limit == 1` + Vestibular | "strike limit was hard-set by vestibular calibration; chatbot cannot override" |
| 2 | Vestibular + ANY relaxation | "vestibular advisory active; relaxation proposals require manual Settings change" |
| 3 | HighRiskCombo + SIGNIFICANT (≥15% relative) relaxation | "high-risk-combo advisory; small adjustments only via this channel" |

**Significance threshold (rule 3)**: 15% relative change. Computed as
`|new - old| / old`. Profile switches are always significant (3-step
enum, no continuous gradient).

**Why apply-time, not propose-time only**: the user-facing UX is
"chatbot suggested X, system blocked X" rather than "chatbot silently
didn't suggest X" — more transparent and gives the user the context
(`SafetyNote`) for why.

**Side effects on block**:
- `EventLogEntry` with category `Habituation.SafetyIntercept` and the
  reason
- `ChatbotActionLog.Record(ChatbotActionKind.Intercepted, ...)` for
  audit + chatbot context grounding ("you tried this last week, was
  blocked")
- `ApplyResult.Blocked(reason)` returned to caller — Card surfaces the
  reason as ⚠ red text

**Failure mode if disabled**: relies entirely on Processor (rule 3
vestibular check). Loses the apply-time re-check that catches
post-Processor snapshot changes.

---

## 5. Crisis resource directory

**Where**: hardcoded inside `TripsSafetyFloor` (both chatbot services).

**Resources**:
- Taiwan: `1925 安心專線` (24h free, the national mental health hotline)
- International: `https://findahelpline.com`

**Why hardcoded**: a "directory" stored externally could be edited or
broken; the crisis path needs deterministic, always-available text.
Resource list is intentionally short — overloading at a crisis moment
hurts more than helps.

**When to update**:
- If 1925 changes (national policy change)
- If findahelpline.com goes down (replace with another reputable
  international directory)
- NEVER expand to commercial resources or unverified hotlines

---

## 6. Self-harm / discomfort lexicon

**Where**: `FeedbackLexicon.cs` — chatbot's deterministic fallback when
the LLM isn't loaded or its output fails to parse.

**Relevance to safety**: the lexicon recognizes discomfort severity
(MildAdverbs / SevereAdverbs) so a user reporting acute discomfort gets
appropriate FOV/mask deltas even when the LLM is offline. NOT a crisis
floor — that's §1's job; lexicon handles "I have a headache" not
"I want to die".

---

## 7. Audit trails

Two independent logs for chatbot-related actions:

### 7.1 IEventLogStore (process-scoped, dashboard-visible)

Categories relevant to safety:
- `Habituation.SafetyIntercept` — applicator-time block fired
- `Habituation.Profile` — profile changes (calibration or chatbot)
- `Habituation.StateChange` — engine state transitions
- `Habituation.Spike` — severe spike registered
- `Habituation.LevelChanged` — habituation level transitions

**Visibility fix at v0.9-phase7** (`77163d9`): `IEventLogStore.Appended`
event lets `MainViewModel` mirror every append into the dashboard's
bound `EventLog` ObservableCollection — entries from
`MockInferenceEngine` (M1 events) and `HabituationOverrideApplicator`
(Phase 7d/7f events) now reach the UI table they previously bypassed.

### 7.2 IChatbotActionLog (process-scoped, in-memory only at v0.9)

Tracks proposal lifecycle separately from FOV adjustments:
- `Proposed` — Processor accepted, Card about to show
- `Accepted` — user clicked Accept, Applicator applied
- `Declined` — user clicked Decline
- `Expired` — proposal lapsed without user action
- `Intercepted` — Applicator's 7f intercept blocked the Apply

**Used by**: Processor rule 5 (7-day cooldown lookup via
`MostRecentDeclineFor`).

**Persistence Q4.6 locked**: in-memory only at v0.9. Process restart
loses history; cooldown silently degrades to "no cooldown" until the
chatbot proposes again in this session.

---

## 8. Engine state safety invariants

`HabituationEngine` (pure logic, no IO) maintains these invariants
regardless of chatbot input:

- `_severeStrikeCount` increments only inside `RegisterSevereStrike`
  (sensor weight 1.0, chatbot weight 0.5 per spec §7c)
- `EffectiveTrigger` accepts arbitrary positive doubles but the
  composition path (`ComposeFovRadius`) clamps the resulting FOV to
  `[15, 100]`
- `SetProfile(profile, source)` raises `ProfileChanged` event with the
  source string ("calibration_demo" / "chatbot_override" / "default" /
  "force") — never silently swaps
- `PausedAwaitingUserConfirm` state can ONLY exit via
  `AcknowledgeAndResume()` — automatic recovery is impossible by
  design

These are unit-covered in `HabituationTests.cs` and not weakenable
through the chatbot path.

---

## 9. Locked safety decisions

- **Q4.5 ResetOffset NOT a chatbot proposal intent**: full habituation
  reset is too high-impact for the propose-confirm channel. Users
  seeking it must go through Settings.
- **Decision 5 Safety floor migration before Phase 7 write-side**:
  enforced at v0.8.2 (M0). MistralChatbotService got the safety floor
  BEFORE the new write capabilities landed — production safety gap
  closed before new attack surface added.
- **Decision 6 Defensive intercept at Applicator layer (not Parser
  layer)**: Parser parses and UI shows the proposal; only the Apply
  click is gated. Lets the user see "chatbot tried to suggest X but
  it was blocked" — more transparent than silent suppression.
- **Decision 7 HabituationEngine API NOT extended for chatbot**: the
  chatbot path uses the same setters/methods calibration uses. No new
  privileged engine method that bypasses existing safety.

---

## 10. Out-of-scope (deliberately)

These are NOT safety provisions and shouldn't be confused with the above:

- **FeedbackLexicon adverb detection**: severity calibration, not a
  safety gate.
- **Pop-up Card click-outside auto-close**: UX hygiene, not safety.
- **Idle-fade timer**: UX hygiene.
- **PreCommit signing on the build**: integrity concern, not behavioral
  safety.
- **WGC `BarTrayColorEnabled` toggle**: user-preference UI, not safety.

If a future change wants to repurpose any of these as safety controls,
they need to be added to this document with the same rigor (where /
what / invariant / failure mode).
