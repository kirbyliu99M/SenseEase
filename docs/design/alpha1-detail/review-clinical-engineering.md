# Adversarial Review — Clinical / Engineering

> Generated 2026-06-05 by the senseease-rebrand-refine workflow (8 agents, adversarially reviewed). Draft for review — no code changed, nothing committed.

Everything is verified. The soften-seam test stub members all exist (`UserFovOffset` getter at 822/IInterface:46, `SetHabituationPaused` at 340/IInterface:40, `Current.HabituationLevel`), `Source = "user"` is at ChatbotViewModel.cs:809, `ComplaintFovDelta = -15` at SenseDefaults.cs:22, `HabituationSevereFovThreshold = 18` at :72, lexicon `"ReportDiscomfort"` at FeedbackLexicon.cs:171. 

One important catch worth flagging: at MainViewModel.cs:1453 and ChatbotViewModel.cs:800, the call sites compute `appliedFov` and route through `ApplyUserFeedback` — both feed into the *same* NLU result. The spec proposes gating `isDeliberatePreference: result.Intent != "ReportDiscomfort"`. I have everything needed to write the adversarial punch-list.

---

# Adversarial Review — Five Resolutions vs. HARD_CONSTRAINTS A–D + LOCKED Decisions

**Reviewer lens:** clinical-safety > privacy > engineering-locks > product-semantics > brand. Every code claim below was re-verified against current `main` HEAD; file:line citations are load-bearing. Verdicts are per-item **GO** (lock as written), **GO w/ FIX** (lock only after the named change), or **HOLD** (do not lock; blocking gap).

---

## Verification ledger (what I confirmed in-tree before judging)

| Spec claim | Verified? | Evidence |
|---|---|---|
| `OnTick` catch calls bare `Stop()`, silent | ✅ TRUE | `WgcSensingProvider.cs:579-583` — `WgcLog(...); Stop();`, no event |
| `_lastFrameProcessedAt` stamped only on real frame, read only by resume watchdog | ✅ TRUE | stamp `:524` (`if (realFrame)`), read `:271`; resume watchdog one-shot `Timer(... ,2000, Timeout.Infinite)` `:289` |
| `EnterSleepMode/ExitSleepMode` snapshot pattern, `SleepOverlay` dashed | ✅ TRUE | `PersistentBarWindow.xaml.cs:228-246`, `_preSleepBackground` guard `:230` |
| `SetPowerSaveIndicator(bool)` + `ToolTipText` refresh exist | ✅ TRUE | `TrayIconService.cs:313`, `:282`, `RefreshLabels :254` |
| L1 alpha = 0.00 (transparent at floor-off) | ✅ TRUE | `OverlayWindow.xaml.cs:56`; `MaskSmoothingFactor=0.25` `:77` |
| `_persistentOverlayEnabled` default FALSE; baseline tier default **4** | ✅ TRUE | `MainViewModel.cs:794`, `:807` |
| `ApplyUserFeedback(int fovDelta)` unconditionally erodes `_offset` on neg | ✅ TRUE | `MockInferenceEngine.cs:292-311`; `NotifyDiscomfort` halves/`-4`/zeros `:342-380` |
| Call sites both tag `Source="user"`, share one NLU `result` | ✅ TRUE | `ChatbotViewModel.cs:800,809`; `MainViewModel.cs:1453` |
| Soften-seam test stub members exist | ✅ TRUE | `UserFovOffset` `MockInferenceEngine.cs:822`/`IInferenceEngine.cs:46`; `SetHabituationPaused` `:340`/`:40` |
| Card expiry branch disables Accept+Later, **leaves Decline enabled** | ✅ TRUE | `NotificationCardWindow.xaml.cs:202-204` (only `ProposalAcceptBtn`,`ProposalLaterBtn`); `ProposalDeclineBtn` exists at XAML `:188` |
| Chat code-behind hard-depends on `ChatInputBox/ChatSendBtn/ChatStatusText` | ✅ TRUE | `OnQuickDiscomfortClicked` writes `ChatInputBox.Text` then `TrySendChatAsync` `:668-669`; reset block `:378-386` |
| Smoke test fires `OnStateChanged` w/ bare `SenseState` + `VimsTier 4/7` | ✅ TRUE | `MainWindowSmokeTests.cs:75-79` |

**No factual misstatement found in any of the five specs.** The specs are unusually well-grounded. The punch-list below is therefore about *residual risk, under-specification, and a few constraint edges the specs glide past* — not fabrication.

---

## P0 — MUST-FIX (blocks lock / release-blocking constraint risk)

### P0-1 — `resolve:liveness-trust-contract`: the continuous watchdog must NOT fight the `_paused`/Resume path, or Power-Save will render as Faulted

**Risk (clinical-trust + correctness).** The spec adds an always-on rearming watchdog that trips Faulted when `now - _lastFrameProcessedAt > 3000ms`. But `_lastFrameProcessedAt` is **also frozen during a legitimate user pause** (`Stop()` on Power-Save halts `OnTick`, so no real frame, so the stamp goes stale). The existing resume watchdog explicitly guards this with `if (_paused) return;` (`:270`). The new watchdog **must replicate that guard** plus the warm-up guard, or **Paused-by-user will be misclassified as Faulted** — the exact "calm-by-choice vs calm-by-accident" confusion the spec's own §1 invariant forbids.

**Exact fix.** In the new watchdog callback, first lines must be: `if (_paused) return; if (!_running) return; if ((DateTime.UtcNow - _resumedAt) < WarmupMs) return;` before the staleness check. Add a `LivenessContractTests` case: enter Power-Save → advance time past `StalledThresholdMs` → assert `SensingHealthChanged(false)` does **NOT** fire and `LivenessState == Paused-by-user`. Without this case the regression is invisible.

**Verdict:** GO w/ FIX. The watchdog itself is sound and flagged correctly as a behavior change; it cannot be locked until the pause/warm-up guard + its test are in scope.

### P0-2 — `tighten:quiet-guardian-home`: the two coupled default flips must be a SINGLE sign-off unit, and the test surface for default-ON is unspecified

**Risk (clinical A2 + product D2).** The spec correctly flags `_persistentOverlayEnabled FALSE→TRUE` (`:794`) **and** `_persistentOverlayBaselineTier 4→1` (`:807`) as coupled. This is right: flipping #1 without #2 ships every user a 43% vignette (`L4` alpha `0.43`, confirmed `OverlayWindow.xaml.cs:59`) — over-dimming low-strain users, violating LOCKED Decision 2's explicit "baseline tier 1 = 0% alpha so low-strain users are not over-dimmed." **The blocking gap:** no test pins that default-ON at baseline-1 actually composes to 0.00 alpha for a calm screen. A future baseline-tier-table edit could silently re-introduce dimming-at-rest. 

**Exact fix.** Before locking the flip: add a test asserting `ResolveDisplayedTier(naturalTier=1)` with `PersistentOverlayEnabled=true, BaselineTier=1` returns 1, AND that `VimsLevels[0].Alpha == 0.00`. Make the PR description state the two flips are **atomic** (reviewer must reject a PR that lands only one). Confirm `ResolveDisplayedTier` (`:819-822`) still lets natural higher tier win — verified TRUE, so a real spike still darkens. 

**Verdict:** GO w/ FIX (with explicit product-owner sign-off on the coupled flip per the memo). The UI is safe to build now against existing FALSE/4 defaults; the default flip is a separate, gated, atomic change.

---

## P1 — SHOULD-FIX (constraint-adjacent or test-breaking; fix before merge, not necessarily before lock)

### P1-1 — `resolve:soften-engine-seam`: the `Intent != "ReportDiscomfort"` gate is too coarse and risks silencing a real complaint

**Risk (clinical A5 / product D4).** The seam itself (one optional `bool isDeliberatePreference = false`, guard the single `NotifyDiscomfort` call) is **minimal, correct, and respects Q4.5** — `_offset` is provably untouched on the deliberate path (verified: `NotifyDiscomfort` is the only `_offset` mutator reachable from `ApplyUserFeedback`). **But** the proposed call-site wiring `isDeliberatePreference: result.Intent != "ReportDiscomfort"` (`ChatbotViewModel.cs:800`, `MainViewModel.cs:1453`) means *any* non-`ReportDiscomfort` intent that happens to carry a negative `FovDelta` becomes neutral. If the NLU ever emits a negative delta under a different intent label for a genuine "I feel sick" phrase (lexicon coupling is not guaranteed stable), a real discomfort recurrence would silently stop eroding `_offset` — a clinical-signal loss. The lexicon emits `ComplaintFovDelta = -15` *only* under `"ReportDiscomfort"` today (verified `FeedbackLexicon.cs:171`), so it is safe **now**, but the gate is keyed on a label that can drift.

**Exact fix.** Prefer a **dedicated control handler** passing `isDeliberatePreference: true` *literally* (the explicit "Soften / more protection" button), and leave the NLU/chat path at the default `false` for Alpha-1. That keeps deliberate-preference neutrality scoped to a UI affordance whose intent is unambiguous, and keeps every chat-originated negative delta on the conservative (still-erodes) path. If the chat path must be gated, assert in a test that `FeedbackLexicon` returns a *non-negative* delta for every non-`ReportDiscomfort` intent (locks the coupling the gate relies on).

**Verdict:** GO w/ FIX on the wiring (engine change is GO, with sign-off as flagged). The flag default `false` correctly makes the engine edit byte-compatible for all existing callers/tests — confirmed.

### P1-2 — `tighten:popup-card`: BC-4 "collapse-don't-delete" leaves the deferred Just-Talk surface reachable by keyboard/automation — A11y + privacy edge

**Risk (privacy-adjacent + A11y).** Option A keeps `ChatInputBox/ChatSendBtn/ChatStatusText` in the visual tree at `Visibility="Collapsed" + IsHitTestVisible=False`. `Collapsed` does remove an element from tab order and the automation tree in WPF, so this is *mostly* safe — **but** the spec also says these elements are still written/read by `TrySendChatAsync` and the `OnIsVisibleChanged` reset block (`:378-386`). That means a *live, functional* chat-send pipeline remains wired to a hidden field. The Just-Talk front door is HARD-BLOCKED on `SafetyFloor.cs` (LOCKED Decision 4) precisely because *no user text should reach the LLM without the crisis floor*. The discomfort-ramp reuse sends **fixed `QuickDiscomfortPhrases[severity]`** (canned, not free-text) — that's safe — but leaving the typed-text path alive-but-hidden is a latent re-exposure risk if anyone later flips the visibility.

**Exact fix.** Adopt the spec's **option B refactor** (route `OnQuickDiscomfortClicked` directly to the host `SendChatMessage` with the canned phrase; delete the three chat elements) OR, if keeping option A for diff size, add an explicit guard in `TrySendChatAsync` that **hard-refuses any payload not in `QuickDiscomfortPhrases`** while `SafetyFloor` is absent. Either way the typed-text Just-Talk path must be *provably* dead in Alpha-1, not merely hidden. Add a test: the only strings reaching `SendChatMessage` from this card are the four canned phrases.

**Verdict:** GO w/ FIX. Layout/zone redesign is GO; the chat-defer mechanism needs the canned-only guarantee before it's locked.

### P1-3 — `tighten:popup-card`: NE27 description is correct, but the fix must also cover the user-driven Decline-after-expiry race

**Risk (product D4).** The spec accurately states the expiry branch (`:202-204`) disables Accept + Later but leaves `ProposalDeclineBtn` enabled, and the NE27 fix adds `ProposalDeclineBtn.IsEnabled = false;` + nulls `_activeProposal`. Verified the gap is real. **But** note `OnProposalDeclineClicked` (`:242-248`) does `if (_activeProposal is null) return;` then `ActiveProposal = null`. After the expiry fix nulls `_activeProposal`, a late Decline click is already a no-op — good. The residual: the expiry branch sets visible tombstone text (`已過期`) but if `ProposalDeclineBtn` is left visually enabled for even one frame, an AAA-keyboard user can focus+activate it. 

**Exact fix.** In the expiry branch, set `ProposalDeclineBtn.IsEnabled = false` **and** `_activeProposal = null` (or rely on the existing `ActiveProposal` setter) in the same synchronous block as the Accept/Later disable. Add the assertion to the existing expiry test (`NotificationCardWindowTests`) that **all three** proposal buttons report `IsEnabled == false` post-expiry. This is the D4 "Decline button must not be visually active after expiry" letter.

**Verdict:** GO w/ FIX. The NE27 call-out is correct and must ship *in the same change* as the restyle (the spec already says this) — lock it with the three-button assertion added.

### P1-4 — `resolve:benefit-legibility`: the "since you started" anchor silently reuses calibration date — verify it cannot read as a health-streak

**Risk (clinical / honesty).** The spec's banned-fabrication discipline is excellent and the inventory is correct (`HabituationOffsetPp` is the only honest persisted benefit scalar; session counters are volatile — verified the framing intent). **But** §6 proposes anchoring "widened by N points *since you started*" to `LastCalibrationAtIso`/first `CalibrationCount`. That date is "last personal check-in," not "first run." If a user re-calibrates, the anchor *moves forward*, so "since you started" would silently shorten and the N could appear to **shrink relative to the new anchor** even though `_offset` (cumulative) didn't — reading as regression, the exact anti-pattern the spec bans elsewhere.

**Exact fix.** Drop the temporal "since you started" framing entirely for Alpha-1 (it needs a first-run-date field = a flagged persistence change the spec itself defers). Use the **atemporal** copy already in the spec: "Your clear view has gradually widened by **N points**." (no "since…"). N is the literal current `HabituationOffsetPp` — true regardless of anchor. This keeps Channels A+B at **zero persistence change** as the spec intends.

**Verdict:** GO w/ FIX (trivial copy narrowing). Channels A+B are otherwise GO and correctly require no sign-off; Channel C correctly deferred.

---

## P2 — NICE-TO-HAVE (polish; non-blocking)

### P2-1 — `liveness`: `FaultedBarBrush #B65C4E @0x40` vs A5 — confirm it's distinguishable from the high-strain mask tint, not just from alarm-red

The spec correctly avoids alarm-red (A5 compliance: muted clay `#B65C4E`, verified that's the sanctioned high-strain token in HARD_CONSTRAINTS A5/D2). **Polish gap:** clay is *also* the high-strain comfort color. A user at genuine tier-8 strain (clay-ish mask + bar) could see a clay Faulted bar and conflate "high strain" with "sensor dead." Recommend the `FaultOutline` (solid 1px) carry the distinguishing load (the spec already adds it), and the `LivenessContractTests` distinguishability assertion #2 explicitly compare Faulted-vs-high-tier, not only Faulted-vs-calm. Non-blocking because the outline + tray tooltip already disambiguate.

### P2-2 — `liveness`: `SensingHealthChanged` event threading

The new event is raised from a `Timer` callback (thread-pool) and from the `OnTick` catch. `MainViewModel` must marshal to the dispatcher before touching bar/tray (the smoke test exists precisely because `SetTier`/`ApplyState` do `VerifyAccess`, `MainWindowSmokeTests.cs:66-67`). Spec implies the single-derivation-point in `MainViewModel` — ensure that derivation re-posts via the same `_ctx.Post`/dispatcher path the sample callback uses (`WgcSensingProvider.cs:574`). Flag for the implementer; not a design defect.

### P2-3 — `home` / `popup-card`: converter-only path is the right call — keep every new `VimsTier` binding `OneWay`

Both specs correctly choose pure `IValueConverter`s over new VM properties to avoid re-tripping the documented `VimsTier` TwoWay→read-only smoke regression. Verified `VimsTier` has a private setter (`MainViewModel.cs`) and the smoke test drives it via `OnStateChanged`. Confirm in code review that none of `TierToHeadlineConverter`/`TierToComfortChipConverter` is bound `Mode=TwoWay` anywhere. Pure polish/discipline.

### P2-4 — `benefit-legibility`: banned-phrase lint should run in CI, not just "copy review"

The banned-phrase guard ("absorbed/prevented/reduced your/{N} minutes/symptoms") is a genuine safety asset. Recommend a unit test scanning the resx/copy table rather than relying on human review — cheap insurance against a future contributor reintroducing a fabricated-efficacy string.

---

## Cross-cutting confirmations (no action — these specs got it right)

- **No new actuator / no decorative motion** across all five. Liveness Faulted is static (color+outline, no pulse); Home calm field ships static; Card has no mask-driving motion; benefit numbers never animate. Holds LOCKED Decision 3 / A4. ✅
- **No engine-loop math touched.** Soften-seam edits exactly one guard; liveness watchdog reuses `Stop();Start()` with no OFI/tier change; benefit channels are read-only over persisted state. ✅
- **Q4.5 `_offset` never zeroed/reset** by any of the five. Soften-seam *preserves* it (the whole point); benefit channels only **read** `HabituationOffsetPp` via read-only `Snapshot()`; Home's Reset stays on narrowed transient reset. Holds D3. ✅
- **MainWindow stays instantiable / smoke green.** Telemetry demoted not deleted (Home §5); all bindings relocated live; tokens merge view-locally not `App.Resources`. Holds LOCKED Decision 1 + the empty-`App.Resources` trap. ✅
- **No webcam preview / head-tracking off by default.** Liveness head-tracking row is scalar-driven (`HeadTrackingActive` bool), no frame/landmark surface. Holds B1/B3. ✅

---

## Final verdict table

| Item | Verdict | Gating condition to lock |
|---|---|---|
| **resolve:liveness-trust-contract** | **GO w/ FIX (P0-1)** | Watchdog must guard `_paused`+warm-up so Power-Save ≠ Faulted; add that test. Behavior-change sign-off (watchdog + `SensingHealthChanged` + `LivenessState`) as flagged. |
| **resolve:benefit-legibility** | **GO w/ FIX (P1-4)** | Drop "since you started" temporal anchor; keep atemporal "+N points." Channels A+B lock with zero sign-off; Channel C deferred. |
| **resolve:soften-engine-seam** | **GO w/ FIX (P1-1)** | Engine flag is GO with sign-off (minimal, Q4.5-safe). Re-scope call-site to a literal deliberate-preference control, not the coarse `Intent !=` gate. |
| **tighten:quiet-guardian-home** | **GO w/ FIX (P0-2)** | UI builds now on existing defaults. Default-ON+baseline-1 flip is an **atomic** product-owner-signed change with a "0.00 alpha at rest" test. |
| **tighten:popup-card** | **GO w/ FIX (P1-2, P1-3)** | Chat-defer must be provably canned-only (refactor or hard guard), not just hidden; NE27 fix disables all three proposal buttons on expiry, shipped in the same change. |

**Bottom line:** all five are architecturally sound and constraint-respecting — none introduces a new actuator, decorative motion, engine-math edit, Q4.5 violation, or `App.Resources` dependency, and every flagged behavior change is correctly called out. The blocking work is two P0 guards (Power-Save≠Faulted; atomic+tested default flip) and three P1 tightenings (literal soften control; canned-only card send; three-button expiry). Nothing here is a HOLD.
