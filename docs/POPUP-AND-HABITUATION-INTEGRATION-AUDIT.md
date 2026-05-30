# Pop-up Sprint × Habituation Sprint Integration Audit

> **Audited at**: `v0.9-phase7` (Habituation Phase 7 sealed) on top of
> `v0.9-phase6` (Phase 6 sealed) and `v0.8.x` (Pop-up sealed). Patched
> per advisor review 2026-05-28.

Cross-cutting concerns at the boundary between the two largest sprints in
the v0.9 cycle. Records which surface interacts with which, where the
contracts are documented, and what subtle interactions exist that aren't
obvious from either sprint's own spec.

---

## 1. Surface inventory

Pop-up sprint surfaces (all live, end-user-facing):

- `PersistentBarWindow` — the 4-dip ambient bar at top of screen
- `NotificationCardWindow` — the pop-up card opened on bar click /
  tier spike
- `TrayIconService` — system tray icon with context menu

Habituation Phase 6/7 surfaces (live, but mostly invisible — the work
is in the data flow):

- `[habituation_state]` block injected into chatbot user-message turns
- `ProposalSurfaceRequested` event raised when chatbot proposal
  accepted by Processor
- `ChatMessageViewModel.ProfileOptions` (Phase 7e+) — rich-option
  cards rendered as chat bubble

The integration points are where Habituation's data + events meet
Pop-up's UI surfaces.

---

## 2. Integration point: chatbot proposal Card section

**Location**: `NotificationCardWindow.ProposalPanel` (XAML, hidden by default).

**Trigger**: `MainViewModel.ProposalSurfaceRequested` event fires when the
chatbot proposes an engine-state change AND the Processor (Phase 7c)
accepts it. MainWindow's `OnProposalSurfaceRequested` handler:
1. Calls `ShowNotificationCardForCurrentTier(userInitiated: true)` — same
   path as a bar click
2. Sets `_cardWindow.ActiveProposal = proposal` — the setter reveals the
   panel + starts the expiry countdown timer

**Interactions**:

- **Auto-show timing**: Card auto-pops when the proposal arrives. Per
  user spec (M3 design call): "auto-pop card immediately on proposal
  arrival" (Recommended option). Mirrors the tier-spike auto-pop
  behavior already in Pop-up sprint Task 5.
- **Deferred proposal re-surface**: User clicks `稍後` (Later) → card
  closes BUT proposal stays pending in `MainViewModel._activeProposal`.
  Next bar click invokes `ShowNotificationCardForCurrentTier`, which
  checks `_viewModel.ActiveProposal` and re-sets
  `_cardWindow.ActiveProposal` if pending + not expired. Pop-up sprint's
  bar click semantic preserved (still opens the card), Habituation just
  attaches an extra payload.
- **Card lifecycle reset**: Pop-up sprint's v0.9.1 round-4 fix moved
  per-Show resets (Opacity, idle timer, in-flight flag) from `OnLoaded`
  to `OnIsVisibleChanged`. Habituation's ProposalPanel.Visibility flip
  benefits from the same fix — re-Show after Hide always renders the
  current proposal state cleanly.
- **`_cardProposalEventsWired` flag**: MainWindow subscribes
  Accept/Decline/Defer events once per card lifecycle (flag resets in
  `Closed` handler). Without this, repeated bar-clicks would multiply-
  subscribe and each Accept would fire N times.

**Click-outside auto-close**: Pop-up sprint's foreground-process poller
(`OnForegroundCheckTick`) still hides the card when focus moves to
another app. Affects the proposal flow: if the user opens a different
app mid-Card, the proposal stays pending (no Accept fired), and the
expiry watchdog handles eventual expiry.

---

## 3. Integration point: chatbot input + quick-discomfort buttons

**Location**: `NotificationCardWindow` rows 4+5 (added in v0.9.1 round 3-4).

**What**: Card has an inline chatbot text input + 4 discomfort buttons
(輕微/中等/明顯/嚴重). Both feed `MainViewModel.ChatbotVm.SendCommand`
via the card's `SendChatMessage` callback (host-wired in MainWindow's
card construction).

**Habituation integration via this surface**:

- Inline chat reaches the SAME chatbot pipeline as the dashboard's chat
  tab. A user typing `"profile"` in the card chat input → picker bubble
  appears IN THE DASHBOARD's chat thread (not in the card). The card's
  status line shows the chatbot's reply text.
- Quick-discomfort buttons send pre-canned zh phrases through the same
  pipeline. They feed FOV/mask/grid deltas via `FeedbackLexicon`, NOT
  PROFILE_OVERRIDE. (Quick discomfort is the discomfort-actuator path,
  not the proposal-request path.)

**Subtle**: a chatbot proposal triggered by inline-card chat would auto-
pop the card... but the card is already open. The proposal section just
appears at the top.

---

## 4. Integration point: Power Save

**Location**: MainWindow's session-scoped `_powerSaveActive` field.

**Pop-up sprint design** (iter 2, locked at v0.8.1): Power Save is
session-scoped, NOT persisted in `UserPreferences`. Rationale: prevents
"user stuck in Power Save and forgot" — power-save state always resets
on app restart.

**Habituation reads it via**: `MainViewModel.SetPowerSaveActiveLookup(Func<bool>)`
wired in MainWindow constructor. Reads through to
`ChatbotViewModel.IsPowerSaveActive` which surfaces it in the
`[habituation_state]` block as `power_save_active: true` (omitted when
false).

**Why this matters for chatbot**: when Power Save is on, the WGC capture
pipeline is paused — pressure/level/tier data goes stale. The chatbot
needs to know so its replies don't ground in stale tier numbers (e.g.,
"you're at tier 6 now" when the tier display hasn't been updated for
30 minutes because sensing is paused).

**System-prompt rule**: "if `[habituation_state]` shows
`power_save_active: true`, acknowledge sensing is paused; treat tier/level
as stale; don't propose engine changes based on stale data."

---

## 5. Integration point: `IEventLogStore` dashboard table

**Issue at start of v0.9-phase7**: Pop-up sprint's dashboard had a bound
`EventLog` ObservableCollection updated only via
`MainViewModel.AppendLog`. Habituation Phase 7 wrote events via
`HabituationOverrideApplicator._eventLog.Append(...)` directly. Result:
Phase 7 events landed in the underlying store but never reached the
dashboard table.

**Same issue silently affected M1 habituation events**:
`MockInferenceEngine._eventLog.Append(...)` for profile changes / level
transitions / spikes / state changes was also bypassing the dashboard.

**Fix at `77163d9`** (the v0.9-phase7 dashboard fix): added
`IEventLogStore.Appended` event. `InMemoryEventLogStore` fires it after
every append (outside the lock so subscribers can marshal to dispatcher
without blocking). MainViewModel subscribes once and mirrors every
append into the dashboard collection.

**Bonus**: also retroactively surfaces all M1 habituation events that
had been silently invisible since Phase 6's merge.

---

## 6. Integration point: bar color / tier band

**Pop-up sprint design** (Task 5, v0.8.1): `BarTrayColorEnabled` toggle
in Settings controls whether the bar reflects tier-band color
transitions. Default OFF (user found color-changing bar disruptive in
v0.8.1 follow-up).

**Habituation interaction**: `HabituationEngine.SetProfile` raises
`ProfileChanged` event. Subscribed in `MockInferenceEngine` to write
the `Habituation.Profile` event log entry. Does NOT affect bar color
directly — bar reads `VimsTier` which is computed from real-time
pressure, not from profile.

**Subtle**: a chatbot SwitchProfile proposal that the user accepts
changes the engine's behavior (different debounce / hab cap / step
magnitude per profile) but the bar color won't shift visibly until
the new profile's parameters drive a new tier transition. The
dashboard event log + chatbot reply are the immediate feedback
channels.

---

## 7. Integration point: tray menu

**Pop-up sprint**: tray menu has Open Dashboard / Show Bar (toggle) /
Settings / Quit.

**No Habituation integration**: Phase 7's chatbot writes happen via chat
+ Card. The temporary `🧪 測試 chatbot 建議卡 (M3 7e)` tray item from
b108fa8 was removed in `dac8f00` once the production wiring landed.

---

## 8. Locked design decisions affecting both sprints

From v0.8.1 (Pop-up sprint hotfixes):
- Default `AppConfig.Mode = SensingMode.Wgc` — affects engine sampling
  which affects every Habituation event the chatbot reads
- Overlay engaged at startup — bar/card interaction baseline
- WGC 2-second warm-up window emits OFI=0 — Habituation events
  generated during warm-up should be filtered (currently they aren't
  written because the engine sees benign idle data).
  **Incidental, not enforced (advisor A2)**: warm-up filtering relies
  on OFI=0 being interpreted as benign idle data by the engine. There
  is no explicit `IsWarmingUp` guard at the event-write path. If WGC
  warm-up behaviour changes — e.g. emits a cached frame from the
  previous session instead of zero — habituation events could enter
  the trajectory during warm-up and pollute the chatbot's summary.
  **v1.0 candidate**: add an explicit `engine.IsWarmingUp` short-circuit
  on the event-write path so the safety is design-enforced rather than
  relying on a fortunate input pattern. Lesson learned L6
  (documented-but-incidental) applies.

From v0.9-phase7:
- Q4.6 stateless chatbot — ChatbotActionLog is process-scoped; Card
  must remember its current `ActiveProposal` in-memory only.
  **Cross-doc safety implication**: the in-memory cooldown is benign
  for ordinary proposals but lets the chatbot indefinitely re-propose
  safety-blocked relaxations to vestibular users after restart. Defensive
  intercept catches each apply but the repeated propose surface is an
  experience-layer safety erosion not covered by any single sprint's
  spec. See
  [SAFETY-PROVISIONS §7.2](SAFETY-PROVISIONS.md#72-ichatbotactionlog-process-scoped-in-memory-only-at-v09)
  for the v1.0 mitigation backlog (split safety-relevant decline
  history into persisted storage).

---

## 9. Known cross-sprint risks

| Risk | Where surfaced | Mitigation |
|---|---|---|
| User accepts chatbot proposal while WGC is paused (Power Save on) → proposal applies to engine but no immediate sensing feedback to validate | Habituation × Power Save | System prompt rule discourages chatbot from proposing with stale data; user-facing UX has Card's ⚠ note explaining safety blocks. **Gap (advisor A3)**: this is a propose-time mitigation. Accept-time has no stale-state check. The dangerous path: chatbot proposes while sensing is normal → user clicks 稍後 → user enables Power Save → 3 days later user re-opens bar and the deferred proposal re-surfaces → user accepts a proposal based on data that's now 3 days stale. v1.0 candidate: deferred proposal carries propose-time snapshot fingerprint; on re-surface, if `power_save_active` flipped on (or pressure/tier moved significantly), Card surfaces "此建議基於 N 天前資料，sensing 已暫停 — 確定套用？" before Accept is enabled. |
| Bar color change on profile-driven tier transition appears "delayed" relative to chatbot's "已切換到 X profile" reply | Pop-up × Habituation | Acceptable — chatbot reply is authoritative on the apply, bar follows on next tier transition |
| Tier spike during a Later-deferred proposal would auto-open the Card. Card re-surfaces the deferred proposal AND the new tier content | Pop-up × Habituation | `ShowNotificationCardForCurrentTier` calls `SetContent(tier, pressure)` first then optionally sets `ActiveProposal` — both render |
| Foreground-process poller hides the Card while a chatbot proposal is pending — proposal stays pending, user might miss it | Pop-up × Habituation | Bar-click re-surfaces; expiry watchdog handles eventual cleanup |
| Quick-discomfort button (Card) routes to chatbot which might emit OBSERVATION_ADD — observation lands in `habituation_profile.json` even though user thought they were just reporting discomfort | Pop-up × Habituation | OBSERVATION_ADD is silent and the observation list isn't surfaced in user UI — acceptable side effect |

---

## 10. Lessons learned carry-forward (Pop-up → Habituation)

From the Pop-up sprint's accumulated lessons:

| Lesson | Status in Habituation sprint |
|---|---|
| L1 env var leak (validation-mode banner) | N/A — Habituation has no env-var gated paths |
| L2 IsType test pinning impl (test asserts internal type, refactor breaks tests) | Mitigated — all Phase 7 parsers tested via behavior (call real consumer, assert outcome) not type pinning |
| L3 library routing path (silent fall-through on misconfigured route) | Mitigated — defensive intercept layer doesn't trust LLM output; both Processor and Applicator have safety gates |
| L4 UX not unit-testable (bar hover, card auto-pop) | Partial carry-forward — Phase 8 scenarios use stub LLM + pipeline-component tests; full WPF E2E deferred to manual `publish-test` verification |
| L5a library/OS desync (DPI scaling, monitor changes mid-session) | Carry-forward — no new OS surface in Habituation but adopted same defensive-clamp posture in expiry handling |
| L5b dotnet run vs publish (debug build behaves differently from publish) | Active discipline — every M3 commit republished to `publish-test/` and user-verified there |
| L6 documented but unimplemented (`.NET API`-shaped APIs that quietly don't work) | Watch — no .NET API used in Habituation has known gaps but worth checking on next refactor |
| L7 lead false positive (coordinator asserts without verifying) | Active discipline — coordinator requires source paste before strong assertion; multiple "let me re-read the code first" pauses during M3 |
| L8 capability gap mistaken as overreach | Active discipline — agent transparency in tool usage |

New lessons learned in Habituation sprint:

| Lesson | Detail | Mitigation in sprint |
|---|---|---|
| L9  | LLM structured output is not deterministic at the 3B parameter scale | Defensive parser tolerance (tag variants, raw-output carry), deterministic lexicon fallback, expiry clamp instead of reject |
| L10 | Prompt rules alone are insufficient — every safety rule needs a code-side guard | All vestibular / high-risk / strike-limit safety duplicated as code, not just prompt |
| L11 | Safety floor MUST precede the LLM call | Hardcoded crisis response, never LLM-generated, short-circuit at top of SendAsync |
| L12 | Event log → dashboard visibility hole | Surfaced when M3 7e+ user noticed missing entries. Generalized fix: IEventLogStore.Appended event lets MainViewModel mirror appends regardless of caller |
| L13 | Mistral 3B reliably overshoots time durations | Clamp instead of reject; concrete date examples in prompt; lexicon-built proposals use deterministic 48h |
| L14 | Test STA threading is painful — keep integration tests pipeline-focused, not UI-pump-focused | Phase 8 scenarios bypass SendCommand for pipeline tests; reserve STA-thread tests for WPF widgets where unavoidable |
| L15 | Cross-doc audit catches gaps that single-doc reviews miss | Advisor pass after the three docs went up flagged A1 (in-memory cooldown × vestibular nudge) — a gap that touched all three docs but no single doc owned it. Future sprints: dedicated cross-cutting audit document, written *last*, is the right place to catch these. |

New lessons learned in Head-Tracking sprint:

| Lesson | Detail | Mitigation in sprint |
|---|---|---|
| L16 | Pose-sign decisions require empirical CSV verification, not code-reading + visual demo | M5.1-A day 1: Q3 pitch-sign was approved as self-consistent via documentation-comment reading + visual demo. Day 1 deploy: chin-up made the mask go down instead of up. Inversion only surfaced from the M5.1-A `--diag` CSV, which logged numerical pitch_deg per frame: chin-up produced pitch ≈ −40° but documentation said positive pitch = chin up. Fix: pitch negation in sidecar. **Discipline now**: any sign convention for sensor input requires a CSV-instrumented diagnose run with concrete numbers before the convention is locked. "Looks consistent with the docs" is not sufficient; "I see the brush move when I tilt" is not sufficient either (the user saw it move — they didn't see it move the *wrong direction* until the symptom was specific). |
| L17 | Long-pinned sprint branches must check `main` for safety hotfixes at each segment boundary | head-tracking sprint pinned its branch at v0.9 (8ae37b0) for baseline-diff stability per advisor SEG1 guidance. v0.9.3 (cross-thread Dispatcher marshal hotfix) shipped to `main` mid-sprint but stayed off `feature/headtracking-mediapipe`. The anti-pattern v0.9.3 fixed was therefore still present on the sprint branch — surfaced as `MainWindowSmokeTests` "pre-existing STA pollution" at G4. Cherry-pick at G4 cleaned it up but should have been preemptive. **Discipline now**: at each SEG boundary, `git log main --since=<sprint-start-date>` for "hotfix" / "fix" commits and check whether each one needs cherry-pick onto the sprint branch. Backport-status defaults to "assume not applied until verified". |
| L18 | **Empirical > inferred** (meta-lesson for fact-shaped decisions) | This sprint was burned by "looks reasonable" reasoning three times: Q3 pitch sign (autonomous decision, demo-verified, day-1 wrong — L16), v0.9.3 backport status (assumed done, wasn't, until G4 investigation — L17), and the NPU "保留路徑" wording on the perf side (autonomous answer was "保留路徑" until M7 measurement reframed CPU as sustaining 15 Hz under LLM-hot and reduced NPU to a smoothness optimisation rather than a viability path). Each time the fix was the same: MEASURE — CSV instrumentation, git-log investigation, real-runtime perf data. **Discipline now**: when a decision is about a FACT (sign convention, perf number, hotfix status, runtime device choice), require empirical evidence before lock-in. "Looks consistent with docs" / "theoretically should work" / "the code reads like it does X" are NOT sufficient for fact-shaped decisions. (They're fine for design-shaped decisions where the answer is a preference.) |

Brief notes (not standalone lessons but referenced by L16-L18):

- UVHS mechanism (User-Visual Hard-Stop, see `uvhs-playbook.md`) caught two regressions in this sprint that only the user's actual hardware could surface: M5.1-C iter 2's per-frame stepping on a 165 Hz monitor (Coding Agent's dev box couldn't see it — the symptom only existed at high refresh rate), and the LLM-hot smoothness verification at M7 (advisor required visual confirmation alongside the perf numbers, not perf numbers alone). UVHS is the operational arm of L18 for visual outputs.
- Escape × vestibular cross-sprint risk was pre-noted in the M5.1-B close (sprint state notes 2026-05-30) and is closed in M8 as a read-only consumer of `MainViewModel.HasVestibularDiagnosis` — `HabituationEngine` and `HabituationProfileSnapshot` were verified empty diff at M8 close. Pattern for future sprints touching habituation data: surface a new read-only consumer; never extend the engine API for a cross-cutting safety guard.
