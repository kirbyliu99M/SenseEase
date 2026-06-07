# Adversarial Review — Honesty / UX

> Generated 2026-06-05 by the senseease-rebrand-refine workflow (8 agents, adversarially reviewed). Draft for review — no code changed, nothing committed.

All claims verified. I have enough to issue the decision-grade adversarial punch-list. The code facts hold exactly. Note one cross-resolution inconsistency I confirmed: the discomfort ramp (`QuickDiscomfortPhrases`) routes through `SendChatMessage` → the chatbot lexicon → `ApplyUserFeedback`, so the popup-card resolution and the soften-engine-seam resolution must agree on whether a discomfort-ramp tap is a "preference" or a "complaint" — it is a complaint and must keep eroding `_offset`.

---

# Adversarial Review — Honesty / Calm-UX / Accessibility Punch-List

**Scope:** the five LOCKED-direction resolutions (liveness-trust-contract, benefit-legibility, soften-engine-seam, quiet-guardian-home, popup-card). Code claims spot-verified against the tree (`WgcSensingProvider.cs:579-583,259-293,187`; `MockInferenceEngine.cs:292-311`; `HabituationEngine.cs:342-380`; `NotificationCardWindow.xaml.cs:375-386,648-670`; `SenseState.cs:33,67,91`). **All cited file:line facts are accurate.** The specs are unusually disciplined — no fabricated efficacy numbers survive into shippable copy, the banned-phrase guard is correct, and motion/persistence guardrails are respected. The findings below are the residual gaps a nauseated user or a screen-reader user would still hit.

---

## P0 — Must fix before lock (honesty defects, trust-contract holes, or a11y blockers)

### P0-1 — Cross-resolution contradiction: the discomfort ramp is a COMPLAINT, never a "deliberate preference." Pin it so soften-engine-seam can't mis-flag it.
**Confirmed in code:** the popup-card discomfort ramp (`OnQuickDiscomfortClicked`, `:663-670`) stuffs `QuickDiscomfortPhrases[1..4]` ("我有點不舒服"…"我非常不舒服，請大力調整") into the send pipeline, which the lexicon maps to `ReportDiscomfort` → `ApplyUserFeedback(neg)` → `NotifyDiscomfort` → erodes `_offset`. That is **product-correct** (the user reported feeling sick). The soften-engine-seam resolution introduces `isDeliberatePreference: result.Intent != "ReportDiscomfort"`. These two specs are individually right but **only consistent if the discomfort-ramp send path is guaranteed to resolve to `Intent == "ReportDiscomfort"`** so it gets `isDeliberatePreference: false`.
**Fix:** Add an explicit cross-reference clause to BOTH specs:
> "The Zone-F discomfort ramp (severity 1–4) is a COMPLAINT path. Its phrases MUST resolve to `Intent == "ReportDiscomfort"`, therefore `isDeliberatePreference` is `false` and `_offset` erosion is intended. A 'deliberate preference' (`true`) is reserved for an explicit *control* (a 'more protection' button/slider whose intent is preference, not complaint) — NOT any path that emits a discomfort phrase. The ramp tap and the preference toggle must never share a code path."
Without this pin, a future refactor that routes the ramp through the same handler as a preference control silently stops counting genuine nausea — the exact failure the seam resolution warns about, inverted.

### P0-2 — Liveness "Faulted" copy makes a promise the retry can't keep: "tap to retry" with no guaranteed-recoverable action.
**Honesty gap.** The Faulted tray/self-check copy is *"Couldn't reach your screen for a moment — tap to retry."* But the self-heal is `Stop(); Start()` (the same path `ScheduleResumeWatchdog` runs, `:274-289`), which on a hard fault (GPU TDR mid-recovery, capture permission revoked, monitor physically gone) can fail again immediately. "Tap to retry" implies a user action reliably fixes it; if the retry loops back to Faulted, the user taps into a dead end and trust collapses harder than silence would.
**Fix:**
- First fault surface: `"暫時無法偵測畫面 · 點一下重試 / Can't see your screen right now — tap to try again"` (KEEP).
- **Add a second-failure copy state** (retry tapped, still faulted within the next window): `"還是連不上 · SenseEase 會自動持續嘗試 / Still can't connect — SenseEase will keep trying on its own."` This converts a dead-end tap into an honest "we've got it, you don't have to keep poking." Removes the anxiety-inducing infinite-retry loop.
- The spec's §2 already has the self-heal-then-surface ordering; this just adds the **after-manual-retry-also-failed** copy, which is currently missing.

### P0-3 — Benefit-legibility "since you started" anchor is dishonest when the anchor field doesn't exist for upgrade users.
**Confirmed honesty risk, flagged in the spec's own §6 but not resolved in the copy.** Channel A copy says *"Your clear view has gently widened by {N} points **since you started**."* The spec admits there is no first-run date field and proposes reusing `LastCalibrationAtIso`. But `LastCalibrationAtIso` is the **last** calibration, not the first — for a user who recalibrated last week, "since you started" anchored to last week is a **false temporal claim** while showing weeks-old earned offset.
**Fix (one of two, pick before lock):**
- (A, no persistence change) **Drop the temporal claim entirely.** Copy → *"Your clear view has gently widened by {N} points as you've stayed calm."* (mechanism, no false anchor). This is the honest minimum and needs zero new state. **Recommended.**
- (B, flagged persistence change) Add a `FirstHabituationProgressAtIso` field — but this is a behavior change requiring sign-off and is **not** worth it for Alpha-1.
Any copy containing "since you started" / "自您開始以來" must be **rewritten** unless (B) ships. Add "since you started", "自您開始以來" to the banned-phrase lint until a true first-run anchor exists.

### P0-4 — AutomationProperties: the specs MANDATE names but don't pin LiveSetting politeness for the one surface that auto-changes — risking a screen-reader storm or silence.
**A11y blocker.** Both the home spec and card spec correctly add `AutomationProperties.Name` everywhere (good — current count is 0). But the popup-card sets `TitleText` `LiveSetting=Polite` AND `ProposalSafetyNoteText` `LiveSetting=Assertive`, while the liveness Faulted transition (the bar/tray) has **no announced live region at all**. A blind user gets the visual clay treatment they can't see and no audible signal that protection faulted.
**Fix:**
- Liveness: the in-window sensing `Status.Chip` (home §4 channel 3) MUST be `AutomationProperties.LiveSetting=Assertive` on transition to Faulted (it's a protection-integrity change, equal weight to the safety note). Steady↔Protecting transitions stay `Polite` or silent (they're both healthy — announcing every tier creep is its own nag).
- Card: confirm `TitleText` Polite is correct (band changes are meaningful but not urgent) — KEEP.
- **Rule to add to all specs:** "Exactly one Assertive live region per surface (the integrity/safety signal); everything else Polite or silent. Never announce a healthy→healthy comfort drift."

---

## P1 — Should fix before lock (calm-UX friction, weaker honesty, secondary a11y)

### P1-1 — "is it working?" anxiety is under-answered when the WINDOW IS CLOSED and nothing has faulted.
The liveness contract's pull-only affordances (tray hover, bar-click Card) are correct for the *active* user. But the Quiet-Guardian's defining user *never opens anything* — they rely entirely on the ambient bar. The spec distinguishes Protecting (tier color) from Steady-watching (faint grey) — but a healthy Steady-watching grey bar and a **frozen** Steady-watching grey bar are pixel-identical until the 3s watchdog trips and re-inits. During the 3s window + re-init window, the user has no signal. This is acceptable (transient), but the spec should **state the worst-case time-to-honest-signal** explicitly so it's a reviewed number, not an accident.
**Fix:** Add to liveness §2: "Worst-case time from silent pipeline death to Faulted visual = `StalledThresholdMs` (3000ms) + one self-heal window (≤2000ms re-init) ≈ 5s. This is the maximum duration a dead pipeline can render as healthy-calm. Documented and accepted; do not let it grow." This makes the residual gap a locked, bounded number rather than hand-waving.

### P1-2 — Benefit-legibility Channel B "felt-absence" line risks a subtle over-claim with the growth tag.
Copy: *"Nothing needed right now… and your clear view is a little wider than when you began."* The phrase "**than when you began**" reintroduces the same false-anchor problem as P0-3 in the EN growth tag, and "a little wider" + "than when you began" together edge toward implying *the app made you better* rather than *the app relaxed its own mask*.
**Fix:** Rewrite the growth tag to name the mechanism without the temporal anchor or the implied-improvement: EN → *"…and the app is easing off a little as things stay calm."* 繁中 → *"……而且隨著一切保持平穩，App 也在慢慢放鬆保護。"* This keeps it a **mechanism** statement (the app stepping back), not an outcome/temporal claim.

### P1-3 — Popup-card option (A) "collapse-don't-delete" the chat box leaves three dead controls in the visual tree — a11y + maintenance hazard.
The migration recommends keeping `ChatInputBox`/`ChatSendBtn`/`ChatStatusText` at `Visibility="Collapsed"` so code-behind compiles. Risk: a `Collapsed` `TextBox` is removed from layout but **`ChatStatusText.Text` is still written** by `TrySendChatAsync` (`:386`, and the send path). A screen reader set to read live regions could still surface a collapsed status element depending on automation peer behavior, and a future dev sees a "chat box" in XAML and assumes Just-Talk shipped.
**Fix:** If option (A) is taken, also set on all three: `IsTabStop="False"`, `AutomationProperties.IsOffscreenBehavior` / exclude from the automation tree (`AutomationProperties.AccessibilityView="Raw"` or a peer that reports `IsOffscreen`), and a `<!-- DEFERRED Alpha-2: Just-Talk input, hidden not deleted; do not surface -->` comment block. The status writes are harmless only if the element is provably not in the accessibility tree — make that explicit, don't assume.

### P1-4 — Home Comfort Hero "You're protected" at tier ≤5 can read as a guarantee; soften to a state, not a promise.
"You're protected" / 「保護中，放心」 at tier ≤5 is warm and good — but "放心" (rest assured) is a mild reassurance-as-outcome that, for an anxious user who *still feels unwell* while the tier reads low, creates a credibility gap ("it says I'm protected but I feel sick"). The tier is a screen-motion proxy, not a symptom reading.
**Fix:** Keep "You're protected" but make the sub-line carry the honesty: sub-line → *"Watching your screen · 守護你的畫面"* (describes what the app is doing) rather than a felt-state claim. Never let the hero assert how the *user* feels — only what the *app* is doing. This is the same mechanism-not-outcome discipline the benefit spec nails; apply it to the hero too.

### P1-5 — Faulted vs Paused color distance not contrast-verified; clay@0x40 over the bar background may be indistinguishable for low-vision users.
The spec correctly insists Faulted (solid `FaultOutline` + clay fill) ≠ Paused (dashed `SleepOverlay`) — but the distinguishability is partly **dash vs solid** (a 1px outline difference) and partly a **low-alpha clay fill (0x40)**. At 0x40 over a light Surface, the clay may not reach a perceptible delta for low-vision users, collapsing the distinction to "dashed vs solid 1px line" — which fails the "Paused and Faulted must never be confusable" invariant for exactly the users who most need it.
**Fix:** Require the `LivenessContractTests` distinguishability assertion to check **measured perceptual delta (ΔE or at minimum a luminance-contrast threshold), not just brush inequality.** And specify that Faulted's distinction must survive in High-Contrast mode (where alpha compositing is often overridden) — meaning the **outline style (solid vs dashed) must be the load-bearing differentiator, with color as reinforcement**, not the reverse. The test currently asserts only `brush != brush` and `visibility`; upgrade it to assert the outline-style token differs.

### P1-6 — Benefit "X% toward the app stepping back" can read as a countdown to losing protection — anxiety inversion.
*"About X% of the way to where the app steps back"* / 「距離 App 退場」. For a sensitive user, "the app stepping back / 退場" can read as *the protection abandoning me*, which is the opposite of reassurance — habituation progress framed as impending loss.
**Fix:** Reframe as the user's own resilience growing, not the app leaving: EN → *"You're about X% of the way to needing this less."* 繁中 → 「您已大約走完 X%，逐漸不那麼需要它了。」 This is still a literal ratio of two persisted values (honest), but framed as the user's gain, not the app's exit.

---

## P2 — Polish (worth doing, low risk)

- **P2-1 — Self-check freshness "(last frame 0.1s ago)":** showing a sub-second number to an anxious user is faintly clinical and invites watching it like a heartbeat monitor. Replace the numeric age with a binary plain-language state in the default view: `✓ 畫面感測 Screen sensing — live`. Keep the exact `now - _lastFrameProcessedAt` age behind the Advanced/Clinician disclosure only. (Honesty preserved — the row still flips to `✗ reconnecting…` on a real stall; the proof number just doesn't loom.)
- **P2-2 — "FaultedBarBrush #B65C4E@0x40" naming:** the token is clay (high-strain color) reused for "fault." A user who has learned clay = "high strain / easing hard" may misread a fault as a strain spike. Consider a distinct *desaturated* clay or a neutral-warm token so fault ≠ strain in the user's learned color vocabulary. Minor, but the spec's own "single color source must move in lock-step" principle argues against overloading clay for two orthogonal meanings (strain vs fault).
- **P2-3 — Card Zone-F single-hue ramp icons:** the "one→four filled dots" deepening is good for colorblind-safety, but verify the four steps are distinguishable by **count alone at 14px** (dot size + spacing) for users who also have reduced acuity — don't rely on the hue-deepening doing the work. Spec it as "filled-dot count is the primary differentiator, hue-deepen is reinforcement."
- **P2-4 — "暫停感測 / Pause sensing" tooltip:** confirm the copy never implies battery/performance benefit (the spec says so for `MuteBtn` D4 but the `PowerSaveToggle` rename to "Pause sensing" should carry the same guard — it's a *sensing* pause, and the historical name "Power Save" must not leak back into copy).
- **P2-5 — Consistency:** the home spec calls the comfort bands Steady/Rising/High; the card spec uses Steady/Rising/High; the benefit spec uses tier numbers. Good. But verify the tray tooltip verdicts ("守護中 Watching", "正在保護 Protecting") use the **same** band vocabulary so a user hovering the tray, opening the card, and opening the window sees one consistent lexicon, not three synonyms for "calm."

---

## Copy that MUST be rewritten before lock (consolidated)

| Location | Current | Problem | Required rewrite |
|---|---|---|---|
| Benefit Channel A (EN/繁中) | "…widened by {N} points **since you started** / 自您開始以來…" | False temporal anchor (P0-3) | "…widened by {N} points as you've stayed calm." / drop 「自您開始以來」 |
| Benefit Channel B growth tag | "…wider than **when you began**" | False anchor + implied-improvement (P1-2) | "…and the app is easing off a little as things stay calm." |
| Benefit Channel C progress | "…toward where **the app steps back / App 退場**" | Reads as protection-loss (P1-6) | "…toward needing this less / 逐漸不那麼需要它了" |
| Liveness Faulted (retry-failed) | (missing) | Dead-end retry loop (P0-2) | Add: "Still can't connect — SenseEase will keep trying on its own." |
| Home hero sub-line @tier≤5 | "保護中，放心 / rest assured" | Outcome/felt-state claim (P1-4) | App-action sub-line: "Watching your screen · 守護你的畫面" |

## What the specs got right (do not relitigate)
- Banned-phrase guard ("absorbed/prevented/{N} minutes/symptoms") is correct and the "12 minutes of motion you didn't absorb" ban is the single most important honesty call — keep it.
- The liveness `OnTick` catch fix (`:582`, raise `SensingHealthChanged(false)` instead of silent `Stop()`) is correctly identified as the highest-value one-liner; verified the catch does call bare `Stop()` today.
- The soften-engine-seam's **optional-flag-default-false** design is genuinely minimal and preserves the sensor-discomfort path (which lives in `Step`, not `ApplyUserFeedback` — confirmed) and Q4.5 by construction.
- Triple-coding (icon+label+color), 12px caption floor, 44px targets, view-local theme merge (never `App.Resources`), and mask-only-motion are all correctly and consistently applied.
- EM5/NE27/H7 preservation in the card migration is correct and the `_userHasInteracted` gate is the right EM5 fix.

**Net:** 4 P0 (one cross-resolution contradiction, two honesty/copy defects, one a11y live-region gap), 6 P1, 5 P2. No fabricated clinical number survives to ship. Five copy strings must be rewritten. The single highest-priority fix is **P0-1** (pin the discomfort-ramp-is-a-complaint contract) because it sits at the seam between two of the five resolutions and a wrong flag there silently destroys the genuine nausea signal.
