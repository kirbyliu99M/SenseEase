# SenseEase Safety Provisions

> **Audited at**: `v0.9-phase7` (Habituation Phase 7 sealed) on top of
> `v0.9-phase6` (Phase 6 sealed). Patched per advisor review 2026-05-28.

Inventory of every safety mechanism in the system, who enforces it, and what
fails if it goes wrong.

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
- **Both services' phrase tables MUST stay in sync** (advisor B1). The
  "deliberately duplicated" design choice optimises for *auditability of
  the check function* — each service can be audited end-to-end without
  cross-file reading. But the *data* (phrase strings) doesn't benefit
  from duplication: a phrase added to one service and forgotten in the
  other is a silent gap. v1.0 candidate: share `SafetyFloorPhrasesZh`
  / `SafetyFloorPhrasesEn` as a `public static readonly` const in a
  Core/Safety location; both services consume the same const but keep
  their own check functions. Until then, a contract test that asserts
  both arrays are identical (string-by-string) catches drift in CI.

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

**Where**: enforced at three layers (defense in depth). Layers 1 and 2
are propose-time gates; layer 3 implements the apply-time intercept
rules detailed in §4 below — see the cross-reference notes:

1. **Layer 1 (calibration-time)** — `CalibrationProcessor` sets
   `SevereStrikeLimit = 1` when `Q5RiskFactors` contains
   `VestibularDiagnosed`, AND caps `PersonalPressureScale ≤ 1.0`.
   Establishes the hard-cap baseline at the moment the user's
   condition is first recorded.
2. **Layer 2 (propose-time)** — `HabituationOverrideProcessor.Validate`
   (Phase 7c rule 3) rejects ANY relaxation proposal when snapshot has
   Vestibular advisory. Gates whether the Card is even surfaced.
3. **Layer 3 (apply-time)** — `HabituationOverrideApplicator.CheckSafetyIntercepts`
   implements §4 intercept rules 1 and 2 (see §4 below for the full
   rule definitions). Re-checks at apply time because the snapshot
   may have changed between propose and accept.

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

## 3.1 User-initiated full Disable — informed-choice override (RULED 2026-06-06, Q3 = B)

**Scope distinction (important):** the §3 vestibular hard cap governs
**chatbot-/engine-proposed relaxation** — those proposals still pass through
all three intercept layers, unchanged. This §3.1 governs the **explicit
user-initiated "停用 / Disable" control** (Adaptive Fit Layer §7), which is a
*different mechanism*: a deliberate one-tap action by the user, not a
suggestion the engine surfaces.

**Policy:** any user — **including a vestibular-diagnosed user** — can one-tap
**fully disable all protection** with **no confirmation, no warning, and no
background recovery prompt**. This is a clean stop (mask / sidecar / chatbot /
adaptive fit all off; data collection paused, not zeroed). Resume returns to
the pre-disable active set and level. This formally **supersedes**
[HEAD-TRACKING-PRIVACY §3.4](HEAD-TRACKING-PRIVACY.md)'s prior "vestibular
protection cannot be turned off" stance.

**Why the hard cap still stands alongside it:** the user pressing their own
Disable button is an intentional, self-authored choice; a chatbot *proposing*
relaxation to a vestibular user is the engine nudging them toward less
protection. The first is honoured; the second is still blocked. The two are
not in tension.

**Informed-choice risk (Kirby 拍板, 2026-06-06):** the VIMS risk a
vestibular-diagnosed user takes on during full Disable is **borne by the
user**. Rationale: the app's intuitive usage model (turn it off cleanly, leave
nothing running). May be challenged at ATCC / OT / vestibular-specialist
review; retained for traceability. Implementation lands in **P2 (Alpha-1)**;
on Resume, any queued relaxation proposals MUST re-fire the three §4
apply-time intercepts (never auto-apply across a Disable boundary).

---

## 3.2 Graded-exposure opt-in — sole Settings toggle (D-1, 2026-06-11)

**What**: the graded-exposure (habituation) program — the engine slowly
relaxing protection while the user is comfortable — defaults **PAUSED** and is
opt-in. The **only** user-intent control that flips that pause is the Settings
toggle **設定 → 漸進適應 / Graded exposure** (`SettingsContent.xaml`,
`x:Name="ToggleGradedExposure"`). The old dashboard pause/resume toggle was
removed 2026-06-09, so this is the single opt-in/opt-out surface.

**Where**:
- **Pref (source of truth across restarts)**: `UserPreferences.HabituationEnabled`
  (`habituation_enabled`, default `false` = paused; additive field, rolls
  forward, no schema bump).
- **Engine flip**: `MainViewModel.GradedExposureEnabled` calls
  `IInferenceEngine.SetHabituationPaused(!enabled)` and persists the pref. The
  toggle reaches it via the host-wired `SettingsContent.SetGradedExposure`
  action (same pattern as the vestibular "regret" toggle); on the
  no-MainViewModel notification-card popup the handler persists the pref
  directly and the engine hydrates it at next launch.
- **Startup hydration**: `MainViewModel` ctor calls
  `SetHabituationPaused(!HabituationEnabled)` right after the engine is
  constructed — so the pause state matches the pref **before the first Update
  tick**, applied last among the engine-state seeds so it survives
  auto-profiling defaults.

**Contract — who may flip the pause (D-1)**: ONLY this Settings toggle and the
explicit **AcknowledgeAndResume** chat flow
(`MockInferenceEngine.AcknowledgeAndResumeHabituation`, gated to the
tier≥8 `PausedAwaitingUserConfirm` check-in) change the pause by user intent.
Adaptive-fit selector set-moves **preserve** the pause and never auto-unpause;
no other path may auto-resume graded exposure.

**Q4.5**: `SetHabituationPaused` toggles the pause flag only — it NEVER clears
the earned habituation `_offset`. Pausing then resuming returns the user to
their accumulated progress, not to zero.

**Failure mode if disabled**: graded exposure runs without the user opting in
(silent relaxation of protection) — the exact harm the §3 vestibular hard cap
and §4 intercepts exist to prevent for engine-/chatbot-proposed relaxation,
here applied to the program-level pause.

---

## 4. Defensive intercept layer

**Where**: `HabituationOverrideApplicator.CheckSafetyIntercepts` (static,
exposed for tests + UI preview).

This section defines the apply-time intercept rules. Rules 1 and 2
implement §3 layer 3 (vestibular hard cap, apply-time gate); rule 3
extends defense to the HighRiskCombo advisory.

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

**Known gap (advisor A3) — deferred proposal staleness**: the
Applicator re-checks the safety predicates at apply time (above), but
does NOT re-check whether the *contextual conditions* that justified
the proposal at propose-time still hold. Specifically, if the user
clicked 稍後 and enabled Power Save before re-engaging, the deferred
proposal applies to engine state based on data captured before sensing
was paused. The Card surface has no inline warning for this case.
v1.0 candidate: proposal carries a propose-time fingerprint
(power_save_active, current_tier, habituation_level snapshot); accept-
time compares against current state and surfaces an "older context"
warning before enabling Accept. See
[POPUP-AND-HABITUATION-INTEGRATION-AUDIT §9](POPUP-AND-HABITUATION-INTEGRATION-AUDIT.md#9-known-cross-sprint-risks)
for the cross-sprint analysis.

---

## 5. Crisis resource directory

**Where**: hardcoded inside `TripsSafetyFloor` (both chatbot services).

**Resources**:
- **Primary (in-country phone numbers)**: Taiwan `1925 安心專線`
  (24h free national mental-health hotline); US `988` (Suicide &
  Crisis Lifeline). These are the load-bearing resources — they
  work offline-from-the-app's-perspective (user dials directly) and
  don't depend on any third-party uptime.
- **Secondary (URL fallback)**: `https://findahelpline.com` — an
  international directory of crisis lines. Surfaces *additional*
  options for users outside TW/US or who prefer a chat/text channel.

**Why "primary + secondary" (advisor B2)**: even if findahelpline
goes down, every crisis response still carries a working in-country
phone number. The web URL is an enhancement, not a dependency. This
isn't an accident — it's the design choice that keeps the crisis
path robust against external single-point-of-failure.

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

**Cross-doc safety degradation (advisor A1)**: the in-memory cooldown
intersects badly with the vestibular hard cap. Flow:

1. Vestibular user is shown a chatbot relaxation proposal in session N
2. User declines → `ChatbotActionLog.Record(Declined, ...)` (in-memory)
3. App restarts → ChatbotActionLog wiped
4. Chatbot in session N+1 sees the same favourable trajectory → proposes
   the SAME relaxation
5. Card surfaces again — defensive intercept (§4) blocks the *apply*,
   but the *propose* repeats every session indefinitely

For ordinary proposals this is annoyance. For vestibular-relevant
relaxation proposals it's a **silent experience-layer safety erosion** —
the user is repeatedly nudged toward a direction known to be unsafe for
their condition, even though no single Apply ever succeeds. The
defensive intercept catches the engine-write but not the
psychological-pressure surface.

**Mitigation backlog** (v1.0 candidate, not v0.9 scope): split decline
history by safety-relevance.

- Ordinary decline → in-memory cooldown (current behaviour, fine to lose
  on restart)
- Safety-relevant decline (Vestibular advisory + relaxation, HighRiskCombo
  + significant relaxation, vestibular-hard-cap strike-limit) → persist
  to disk alongside `habituation_profile.json`, so cooldown survives
  restart

Reason for splitting rather than persisting everything: keeps the
chatbot's general "I tried X yesterday" recall transient (matches the
Q4.6 stateless decision) while elevating the recall of safety-blocked
proposals to persistent.

**Test gap**: this scenario is NOT covered by `Phase8AcceptanceTests.cs`.
Scenario D verifies the intercept blocks; it does NOT verify what
happens across restarts. A v1.0 follow-up test would simulate
session N decline → session N+1 re-propose → assert cooldown holds.

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

## 10. Forward-looking note — OBSERVATION privacy

`LongitudinalObservations` (Phase 7g) is a chatbot-written, user-invisible
list inside `habituation_profile.json`. When the user reports discomfort
(e.g. via the Card's quick-discomfort buttons or a chat message), the
chatbot may emit an `[OBSERVATION_ADD]` block — the writer persists it
silently and the user has no UI to read or delete the list.

**At v0.9 this is acceptable risk**: SenseEase is local-only (no cloud
sync, no profile export), the file is in `%LOCALAPPDATA%\SenseEase\`
under the user's own account ACL, and the observations are short
descriptive sentences not raw transcripts.

**If v1.0+ introduces cloud sync, profile export, multi-device, or
backup-to-cloud**: this silent-write behaviour must be re-evaluated.
Options include user-visible disclosure ("the chatbot has noted: X")
on observation-write, an opt-out toggle in Settings, or excluding
LongitudinalObservations from the synced/exported subset. The current
list cap (50 entries FIFO) prevents unbounded accumulation but doesn't
address the visibility concern.

This is forward-looking guard, not a current bug. Documented here so
the next sprint planning the cloud surface has the privacy constraint
in scope from day one.

---

## 11. Out-of-scope (deliberately)

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
