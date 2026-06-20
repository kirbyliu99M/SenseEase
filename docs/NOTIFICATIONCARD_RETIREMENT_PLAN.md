# NotificationCard Retirement — Plan (Option C, verified 2026-06-21)

**Status:** DRAFT / awaiting one decision. Item 5 of the settings-feedback batch escalated from
"remove a dead toggle" to "retire the legacy `NotificationCardWindow`."
**Verification:** workflow `wx9cvwwj4` (6 agents) + an independent adversarial agent. All load-bearing
claims CONFIRMED — the adversary could not refute any.

> Verdict on the premise ("the card's functions are outdated / already scattered into the popup, NPS
> card, and bubbles"): **partly correct.** 6 functions are genuinely re-homed; 4 are dead/obsolete;
> but **2 are not re-homed** and block a clean delete. The card is **NOT safe to delete as-is.**

---

## 1. Function-by-function map

| Card function | Status | New home (or why dead) |
|---|---|---|
| Status / band readout | ✅ re-homed | PopupBar `barNeonRim` + `SetTier` ([PopupBarWindow.xaml:709](src/SenseEase.App/Views/PopupBarWindow.xaml:709)) |
| Intensity `[− Lv.N +]` | ✅ re-homed | `IntensitySlider` → `SetIntensityLevel` (same engine path) |
| Pause / Resume | ✅ re-homed | `PlayPauseButton` → `PlayPauseCommand` → same `ApplyPowerSave` |
| Settings gear | ✅ re-homed | `SettingsBtn` → `SettingsFlyout` |
| Chat / input / send | ✅ re-homed | `ChatPanel` / `ChatInput` (same `ChatbotViewModel`) |
| FMS / spike check-in | ✅ re-homed | `FmsButtons` ← `MaybeShowSpikeCheckIn` → `RaiseChatFmsCheckIn` ([MainWindow.xaml.cs:1016](src/SenseEase.App/MainWindow.xaml.cs:1016)) |
| Numeric telemetry | ⚫ dead | was already hidden on the card |
| Mute "don't show 6h" | ⚫ dead | button `Collapsed`/`IsHitTestVisible=False` |
| Engage "緩和畫面" | ⚫ dead | `Collapsed`; obsolete (continuous engine intervention) |
| Open Dashboard | ⚫ dead | `Collapsed`; tray/main window cover it |
| **Proposal Accept/Decline/Defer** | 🔴 **GAP (live!)** | **no popup home — card is the sole surface** |
| **Crisis A1 panel** | 🔴 **GAP (safety)** | crisis *text* is in chat, but the A1 anti-dismiss guarantees are not |
| P5 set-move Apply/Later | 🟡 dormant | only surface was the dead persistent bar → safe to drop |

## 2. The two blockers (verified)

### BLOCKER 1 — the interactive habituation proposal is LIVE
A normal user can trigger an Accept/Decline/Defer proposal **today**, via two ungated paths:
- the LLM emitting `[PROFILE_OVERRIDE]` — directive is in the active system prompt + GBNF grammar, and
  `max_tokens` was raised to 512 to stop truncating it ("re-enabled 2026-06-19",
  [LocalLlmChatbotService.cs:949](src/SenseEase.Infrastructure/Real/LocalLlmChatbotService.cs:949));
- the deterministic `HabituationProposalLexicon.TryDetect` fallback ([ChatbotViewModel.cs:804](src/SenseEase.App/ViewModels/ChatbotViewModel.cs:804)) — fires on the user's own text **even with the model down**.

Wiring is intact end-to-end and the card is the **only** display: `ProposalReceived`
([ChatbotViewModel.cs:808](src/SenseEase.App/ViewModels/ChatbotViewModel.cs:808)) → `OnChatbotProposalReceived`
([MainViewModel.cs:517](src/SenseEase.App/ViewModels/MainViewModel.cs:517)) → `ProposalSurfaceRequested`
→ `OnProposalSurfaceRequested` opens the card ([MainWindow.xaml.cs:1689](src/SenseEase.App/MainWindow.xaml.cs:1689)) → card events → `Accept/Decline/Defer/ExpireActiveProposal` ([MainViewModel.cs:371-413](src/SenseEase.App/ViewModels/MainViewModel.cs:371)).
**Not** superseded by the B5 auto-tune (that's consent-gated default-OFF, tighten/sensitivity-only, never shows a picker). The W-G consent gate zeroes engine deltas but does **not** gate the proposal emission.

→ **Deleting the card silently drops a reachable, shipping feature.**

### BLOCKER 2 — the crisis panel's A1 invariant does not travel
The SafetyFloor crisis line is re-homed as plain chat text ([ChatbotViewModel.cs:456](src/SenseEase.App/ViewModels/ChatbotViewModel.cs:456)) with **no crisis flag**. The popup's fresh-outside-click light-dismiss ([PopupBarWindow.xaml.cs:492-495](src/SenseEase.App/Views/PopupBarWindow.xaml.cs:492)) collapses the whole bar — and it runs *before* the `IsChatOpen` guard (:507), so an open crisis chat does **not** protect it. The card's dedicated gate (`_crisisActive` → `IsDismissableByIdleOrProximity`, [NotificationCardWindow.xaml.cs:1081](src/SenseEase.App/Views/NotificationCardWindow.xaml.cs:1081)) is being deleted with it.

→ **A single desktop click would dismiss the crisis hotline numbers — exactly what A1 forbids. Safety regression.** Re-homing A1 (~40–60 LOC) is **mandatory** for any retirement path.

## 3. Plan — two gated waves

**Wave 1 — BUILD (no deletion yet):**
1. **Crisis A1 re-home (MANDATORY, ~40–60 LOC).** Add an `IsCrisisActive` flag set on `SafetyFloor`
   trip; short-circuit *all three* dismiss paths in `OnAutoCloseTick` (light-dismiss **first**, before
   :495) while crisis-active; force `IsChatOpen=true` on a trip; pin the crisis bubble so it can't
   scroll away. Add a regression test (no light-dismiss / no idle-fade / no truncation while crisis-active).
2. **Proposal channel — DECIDED 2026-06-21: DROP (product cut).** Decommission the interactive
   propose-confirm path: delete the `[PROFILE_OVERRIDE]` prompt directive + GBNF grammar block
   ([LocalLlmChatbotService.cs:256-322,433](src/SenseEase.Infrastructure/Real/LocalLlmChatbotService.cs:256)),
   the deterministic `HabituationProposalLexicon` + its invocation ([ChatbotViewModel.cs:804](src/SenseEase.App/ViewModels/ChatbotViewModel.cs:804)),
   the `ProposalReceived` wiring (`MainViewModel.OnChatbotProposalReceived` :517 + `ProposalSurfaceRequested`),
   and the `HabituationOverrideProcessor`/`Applicator`. Keep the engine's habituation mechanism itself —
   only the **chatbot-initiated picker** is removed. *Rationale (Kirby): habituation is governed by the
   漸進適應 quick-settings toggle; the interruptive proposal is invisible-process noise — one less thing
   to disturb the user.* No re-home build needed.

**Wave 2 — DELETE (only after Wave 1 lands green):**
3. Remove `NotificationCardWindow.xaml(.cs)`, `NotificationCardController.cs`, the `_cardWindow` field,
   `ShowNotificationCardForCurrentTier` + its 4 callers ([:195](src/SenseEase.App/MainWindow.xaml.cs:195), :1603, :1645, :1691), and the card proposal/set-move wiring (:1697–1762).
4. Coupled cleanup: the already-dead persistent-bar path — `StartPersistentBar` (:1542+), the
   `_persistentBar.Clicked` handler (:1603), `PersistentBarWindow.*`, and `AppConfig.PersistentBarEnabled`.
5. Let the **dormant** set-move die (or give it a home if you want the "step up protection" suggestion —
   minor, optional). Optionally stop the engine queuing dead set-moves ([MockInferenceEngine.cs:1136](src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs:1136)).
6. Re-run the suite. `AdaptiveFitProposeConfirmTests` stays green (engine-only). Retire the
   `NotificationCardWindow`/`Controller` UI tests.

## 4. Decisions (locked 2026-06-21)

- **Proposal:** DROP (decommission — §3 Wave 1 step 2). A conscious product cut: removes the
  chatbot-initiated propose-confirm picker; the underlying habituation mechanism stays.
- **Set-move:** LET IT DIE (already dormant; drop the card's set-move UI, optionally stop the engine
  queuing unseen proposals — no user-visible change).
- **Crisis A1 re-home:** MANDATORY (§3 Wave 1 step 1) — independent of the above.

## 5. ⚠️ Execution gate — DEFERRED to the B6 migration (2026-06-21)

**Update:** the concurrent **B6 Stage 1** commit (`3725970`, "re-activate relax — LLM proposes
direction, gate disposes") is actively migrating the habituation proposal **off the card onto a gate**:
a new `[ADAPT_DIRECTION]` grammar → `AdaptationGate.Evaluate` auto-disposes (measured data can veto a
relax) → an honest notice, **no Accept/Decline card** (test `Grammars_AreDisjoint_AdaptDirectionCannotReachTheLiveChatAcceptCard`).
This is the *same goal* as retiring the card's proposal, done as a staged B6 effort. The **old**
`[PROFILE_OVERRIDE]` card path is still live mid-migration.

**Decision (Kirby, 2026-06-21): let B6 own the proposal retirement.** Do NOT decommission
`[PROFILE_OVERRIDE]` or delete the card from here — it would break the still-live old path and collide
with active B6 edits on `ChatbotViewModel.cs`/`MainViewModel.cs`. Once B6's migration removes the
card's proposal dependency, the **card-shell deletion + crisis-A1 re-home + dead persistent-bar cleanup**
become a clean follow-up. The #7 settings reorg (SettingsContent-only) proceeds independently now.

### (historical) original gate note — concurrent chatbot work

Wave 1 edits the chatbot/safety pipeline — `ChatbotViewModel.cs`, `LocalLlmChatbotService.cs`,
`HabituationProposalLexicon.cs`, and the `SafetyFloor` crisis path. There is **active uncommitted work**
in those exact files (the EN-UI language-fidelity fix touched `ReplyComplianceScrubber`,
`FeedbackLexicon`, and `SafetyFloor`). Starting Wave 1 now risks colliding with / clobbering that work.
**Hold the card demolition until that tree is committed + clean.** The #7 settings reorg (self-contained
to `SettingsContent.xaml`) and the #5 toggle-row removal do **not** touch those files and can proceed
independently.

## 6. Effort

~1 day once unblocked: decommission the proposal trigger + crisis A1 re-home (~40–60 LOC) + delete the
card & coupled dead persistent-bar path + suite green. Set-move ≈ 0 (dormant).

---

*This retirement is the long-planned [FULL_SETTINGS_REDESIGN_PLAN.md](FULL_SETTINGS_REDESIGN_PLAN.md) §3
"NotificationCard demolition" (fuses F-N1 done, F-N2/F-N4 here). The #5 settings toggle
(`AutoExpandNotifications`) comes out in Wave 2. The #7 five-section settings reorg folds into the same
`SettingsContent.xaml` pass.*
