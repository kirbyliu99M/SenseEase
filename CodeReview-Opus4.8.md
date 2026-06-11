# SenseEase — Code Review Sweep (Opus 4.8)

**Date:** 2026-06-05
**Reviewer:** Claude Opus 4.8 (Claude Code)
**Scope:** Full `src/` tree — Core / Infrastructure / App / Views (~20K LOC, 95 C# files)
**Baseline read for "intended behaviour":** `docs/TECHNICAL_WHITEPAPER.md`, `docs/archive/Habituation plan.md` (archived; locked decisions Q4.1–Q4.7 — see `docs/HABITUATION-PROFILE.md` and whitepaper §7 for current state), `docs/HABITUATION-PROFILE.md`, `docs/CALIBRATION.md`, `docs/SAFETY-PROVISIONS.md`, `docs/HABITUATION-CHATBOT-INTEGRATION.md`, `docs/HEAD-TRACKING-PRIVACY.md`, `docs/POPUP-OVERLAY.md`, `docs/LOG-PATHS.md`.
**Method:** 1 reviewer on the inference-core math + 4 parallel deep-review agents (sensing/IPC, storage/persistence, chatbot/NLU, UI/ViewModels). Every EMERGENCY finding below was re-read and verified by hand against the source. Findings already catalogued in `docs/ALPHA_BACKLOG.md` (A1–A8, B1–B7, C1–C7, D1–D3, E1–E4, F1–F2, G1) were deliberately **excluded** — everything here is new.

---

## How to read this document

Each finding is labelled **EMERGENCY** or **NON-EMERGENCY**.

- **EMERGENCY** = must be fixed before / at the very start of Alpha. Crashes, data loss, resource storms, a documented-policy (privacy) violation, a safety-path defect, or a headline feature silently broken in its *primary* scenario.
- **NON-EMERGENCY** = correctness in edge/fallback paths, quality, performance, dead code, doc drift, hardening. Real bugs, but they don't crash, lose data, or break a headline path for the typical user.

Categories used: `bug`, `dead-code`, `misconception`, `ux-breaker`, `functional-improvement`.

Confidence is stated per finding. Line numbers are against the working tree at review time.

---

## Summary table

### Emergencies (7)

| ID | Title | Category | Primary location | Conf. |
|----|-------|----------|------------------|-------|
| EM1 | Per-tick severe-strike storm → constant disk writes + event-log flood + offset wipe | bug | `HabituationEngine.cs:273` + `MockInferenceEngine.cs:286,648` | high |
| EM2 | Engine snapshot `Save` silently wipes `longitudinal_observations` | bug | `MockInferenceEngine.cs:767` (BuildProfileSnapshot) | high |
| EM3 | COM use-after-free race between `Stop()`/watchdog and in-flight `OnTick` | bug | `WgcSensingProvider.cs:454,485` | high |
| EM4 | One transient `OnTick` exception permanently disables WGC sensing (silent) | bug | `WgcSensingProvider.cs:579,273` | med-high |
| EM5 | Auto-surfaced notification card hidden within 500 ms by the foreground poll | ux-breaker | `NotificationCardWindow.cs:410` | high |
| EM6 | `ExtractJson` breaks envelope parse on every `[PROFILE_OVERRIDE]` turn → generic reply | bug | `MistralChatbotService.cs:730` | high |
| EM7 | Raw user utterances persisted to `adjustments.jsonl` (PII-posture violation) | bug | `AdjustmentRecord.cs:22` + `FileAdjustmentLog.cs` | high |

### Non-emergencies (31) — see detail below, grouped by subsystem.

---

# EMERGENCY FINDINGS

## EM1 — Per-tick severe-strike storm: constant disk rewrites, event-log flooding, and habituation-offset destruction

- **Category:** bug
- **Severity:** EMERGENCY — runtime resource storm + data churn triggered by *normal use* (watching any video / scrolling any feed in Passenger mode), affecting **every** user regardless of whether they ever enabled habituation.
- **Location:** root cause `src/SenseEase.Core/Inference/HabituationEngine.cs:273`; amplified by `src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs:265` (calls `Step` every tick), `:286` + `:648-693` (`MaybePersistProfileSnapshot`), `:359-380` (`RegisterSevereStrike`).
- **Confidence:** high (traced end-to-end and verified the persistence path writes to disk).

**Intended behaviour.** A "severe strike" is a *discrete* event — a pressure spike crossing the recurrence threshold. The whitepaper (§7.4, B5/B7) explicitly states the post-spike pause acts as a "de-facto debounce": *"During the pause, the next Step call hits the early-return at line :279 … and cannot register another strike. Effective per-profile severe debounce equals these pause durations."* `MaybePersistProfileSnapshot` is meant to write "on profile change / level Δ>5 / spike / state transition" — i.e. occasionally.

**Actual behaviour.** The spike check runs *every tick* on a *level* (not an edge) and *precedes* the pause/paused guards:

```csharp
// HabituationEngine.Step()  (called ~15 Hz from MockInferenceEngine.Update)
double trigger = EffectiveTrigger;
if (pressure >= trigger * SenseDefaults.HabituationRecurrencePressureFraction)  // 0.60 → 51 at default
{
    RegisterSevereStrike(weight: 1.0, vimsTier);   // line 275
    return;
}
if (_externallyPaused || _pauseCooldown > 0.0 || _awaitingUserConfirm)          // line 279 — TOO LATE
{ _phase = HabituationPhase.Paused; return; }
```

Because the check at `:273` runs *before* the guard at `:279`, **any sustained pressure ≥ 0.60 × trigger calls `RegisterSevereStrike` on every Update tick** (~15/sec). A sustained Passenger-mode OFI of only ~9 (just above the noise gate of 8) drives equilibrium pressure to ≥ 51, so this fires during essentially any non-trivial on-screen motion. The whitepaper's "the pause debounces strikes" claim is **false** whenever pressure stays elevated — the pause only begins counting down once pressure drops back under 51.

`RegisterSevereStrike` (`:361`) sets `_lastSpikeAtUtc = DateTime.UtcNow` **every call**. Then in `MockInferenceEngine.MaybePersistProfileSnapshot` (`:665-671`):

```csharp
bool dirty = _profileSnapshotDirty
          || Math.Abs(level - _lastSavedLevel) > 5.0
          || strikes != _lastSavedStrikeCount      // cycles 1→0→1 every ~2 ticks
          || es != _lastSavedEngineState
          || profile != _lastSavedProfile
          || source != _lastSavedProfileSource
          || spike != _lastSavedSpikeAtUtc;         // NEW UtcNow every tick → always true
```

`spike != _lastSavedSpikeAtUtc` is true on **every tick** during the storm, so `dirty` is always true, so:

1. **`_profileStore.Save(snap)` runs ~15×/sec** — `FileHabituationProfileStore.Save` → `AtomicJsonWriter.TryWrite` does a full `File.WriteAllText(tmp)` + `File.Replace` on **every call** (no dedup, no throttle — verified `AtomicJsonWriter.cs:44-81`). That is ~54,000 atomic file rewrites per hour of video. SSD write-amplification, IO contention, and UI-thread jank.
2. **The in-memory event log (`InMemoryEventLogStore`, cap 20) is flooded** with `Habituation.Spike` entries ~15×/sec (`WriteHabituationLifecycleEvents` → `AppendHabituationEvent`), evicting every other lifecycle event within ~1.3 s. The chatbot's `HabituationTrajectoryBuilder` (which greps `Habituation.*`) therefore sees nothing but spike spam — the longitudinal-trajectory feature is effectively destroyed. Each append also raises `_log.Appended`, marshalled to the UI thread → ~15 UI refreshes/sec.
3. **Habituation offset is annihilated.** With Resilient `SevereStrikeLimit = 2`, two consecutive ticks zero `_offset` (≈132 ms), and `EnterPause` resets `_pauseCooldown` every 2 ticks so the recovery clock never elapses while pressure stays high. Worse, because the check precedes the `_externallyPaused` guard, this runs **even though habituation is paused by default at startup** (`MockInferenceEngine.cs:178`) — so even users who never opted into habituation pay the disk/event-log storm.

**Fix.**
1. Make strike registration **edge-triggered**: track whether pressure was below the recurrence threshold on the previous tick and only `RegisterSevereStrike` on the upward crossing; or move the spike check *after* the `_externallyPaused/_pauseCooldown/_awaitingUserConfirm` guard so an active pause genuinely suppresses re-strikes (this is what the whitepaper already claims happens).
2. Independently, throttle `MaybePersistProfileSnapshot` (e.g. min interval, or stop treating a per-tick `last_spike_at` change as a write trigger).
3. Add a regression test: drive sustained `pressure ≥ 0.6×trigger` for N ticks and assert `RegisterSevereStrike` fires **once**, `_profileStore.Save` is called O(1) not O(N), and the event log isn't flooded.

> **Note:** EM1 and EM2 share the same code path (`MaybePersistProfileSnapshot` → `BuildProfileSnapshot` → `Save`). EM1 makes the save fire constantly; EM2 makes each save destructive. Together, during ordinary video watching, the chatbot's longitudinal memory is wiped ~15×/sec.

---

## EM2 — Engine snapshot `Save` silently wipes `longitudinal_observations`

- **Category:** bug
- **Severity:** EMERGENCY — silent, total loss of a shipped feature's persisted data (chatbot longitudinal memory).
- **Location:** `src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs:767-808` (`BuildProfileSnapshot`) and `:682-683` (`Save` call); collides with `src/SenseEase.Infrastructure/Storage/LongitudinalObservationWriter.cs:64-79`. Both write the *same file* via the *same* `IHabituationProfileStore` instance — wired at `src/SenseEase.App/ViewModels/MainViewModel.cs:470` (engine) and `:497` (writer).
- **Confidence:** high (verified the shared instance and that `BuildProfileSnapshot` never sets the field).

**Intended behaviour.** `habituation_profile.json` durably accumulates `longitudinal_observations[]` — the chatbot's `[OBSERVATION_ADD]` summaries, capped at 50, FIFO (`LongitudinalObservationWriter`, Phase 7g). `LOG-PATHS.md:109` lists these observations as one of only three durable evidence stores.

**Actual behaviour.** `LongitudinalObservationWriter.Append` does a read-modify-write: `Load()` → add to `snapshot.LongitudinalObservations` → `Save(snapshot)`. But the **engine's** `BuildProfileSnapshot()` constructs a *fresh* `HabituationProfileSnapshot` from in-memory trackers and **never sets `LongitudinalObservations`** (it defaults to an empty list via the property initialiser). The moment any habituation activity makes `MaybePersistProfileSnapshot` dirty (level Δ>5, a single strike, an engine-state change — and per EM1, every tick during motion), `_profileStore.Save(snap)` full-file-replaces the JSON with empty observations.

So: chatbot writes an observation → seconds later the engine persists → observation gone. The reverse is also a lost-update race (the two writers run on different threads with no shared lock across the read-modify-write). The same omission drops the v2 calibration_* fields the comment at `:791-794` defers to "Appendix B."

Tests miss this because `LongitudinalObservationWriterTests` uses a `FakeProfileStore` and never exercises the engine writer against the same file.

**Fix.** Have `BuildProfileSnapshot` merge-preserve engine-foreign fields: load the current on-disk snapshot (or cache the last-loaded one) and copy `LongitudinalObservations` + calibration_* into the new DTO before saving. Better: route *all* writes through one store method that read-merges under a single lock (load existing, overlay only engine-owned fields, save). Add a regression test that appends an observation, triggers an engine persist, and asserts the observation survives against a real `FileHabituationProfileStore`.

---

## EM3 — COM use-after-free race between `Stop()`/watchdog and in-flight `OnTick`

- **Category:** bug
- **Severity:** EMERGENCY — native access violation / process crash, reachable by ordinary actions (Power Save toggle, sensing-mode swap, ResetState) and even autonomously (the resume watchdog calls `Stop()/Start()` from its own thread).
- **Location:** `src/SenseEase.Infrastructure/Real/WgcSensingProvider.cs:454-480` (`Stop`), `:485-583` (`OnTick`, raw vtable derefs in `CaptureAndDownsample`), `:266-289` (watchdog calls `Stop()/Start()`).
- **Confidence:** high.

**Intended behaviour.** Tearing down the capture pipeline must not free COM resources while a capture tick is dereferencing them.

**Actual behaviour.** `Stop()` runs synchronously on the caller's thread and immediately frees the raw pointers and RCWs:

```csharp
public void Stop()
{
    _running = false;
    _timer.Change(Timeout.Infinite, Timeout.Infinite);  // does NOT wait for an in-flight callback
    SafeRelease(ref _ctxPtr);                            // ID3D11DeviceContext*
    SafeRelease(ref _stagingResPtr);
    ...
    Marshal.ReleaseComObject(_d3dContext);  // also released from OnTick's catch on another thread
}
```

`_timer.Change(Infinite)` only blocks *future* callbacks; an `OnTick` already past its `if (!_running) return;` guard (`:487`) can be mid-`CopyResource`/`Map` through `_ctxPtr`/`_stagingResPtr` when `Stop()` frees them → use-after-free on a raw native pointer. Additionally `Stop()` is reachable from **three** threads concurrently — the UI thread, `OnTick`'s `catch { … Stop(); }` (`:582`, ThreadPool timer thread), and the resume watchdog (`:277`, a third Timer thread) — and `SafeRelease`'s check-then-act is unguarded, so two threads can double-release (refcount underflow → premature device free).

**Fix.** Serialise teardown against the callback: dispose the timer with a wait handle (`_timer.Dispose(waitHandle)`) and block until the in-flight callback drains before releasing pointers; or guard all pointer access + release with one lock; or use an `Interlocked` "in-tick" gate that `Stop()` spins on. Make `Stop()` itself idempotent under concurrency. (Ties to NE11 — the timer is never disposed today, so there is no clean drain primitive.)

---

## EM4 — One transient `OnTick` exception permanently disables WGC sensing (silently)

- **Category:** bug
- **Severity:** EMERGENCY — the core VIMS protection (default `SensingMode.Wgc`) can silently die for the rest of the session on a routine GPU/display hiccup, with no user-visible signal.
- **Location:** `src/SenseEase.Infrastructure/Real/WgcSensingProvider.cs:579-583` (catch → `Stop()`), recovery gate `:273` (`if (!_running) return;`).
- **Confidence:** med-high (mechanism certain; frequency of triggering exceptions depends on environment).

**Intended behaviour.** A transient capture failure (GPU TDR reset, display-mode change, RDP reconnect, a one-off `Map` HRESULT throwing `COMException`) should be recoverable — the resume-watchdog doc-comment (`:245-249`) even claims to cover "lock-screen recovery, monitor sleep wake, RDP reconnect."

**Actual behaviour.** Any exception in `OnTick` calls `Stop()`, which sets `_running = false`. Nothing re-arms it: the watchdog's first guard is `if (!_running) return;`, so it cannot recover a stopped provider, and nothing else calls `Start()` except a user Power-Save toggle (`Resume()`) or a sensing-mode swap. Until then OFI is frozen at its last value, the FOV mask stops responding to on-screen motion, and the only trace is a `Debug.WriteLine` + a temp-file log line.

**Fix.** Distinguish fatal from transient: on a caught `OnTick` exception, schedule a `Stop()+Start()` re-init on a short delay instead of a terminal stop, or keep the timer alive and rebuild the session after N consecutive failures. At minimum, set a `_faulted` flag the watchdog can act on even when `_running` is false. (Coordinate with EM3 so the re-init path is itself race-free.)

---

## EM5 — Auto-surfaced notification card is hidden within 500 ms by the foreground poll

- **Category:** ux-breaker
- **Severity:** EMERGENCY — defeats the headline auto-expand notification feature in its *primary* scenario (a spike while the user is in another app).
- **Location:** `src/SenseEase.App/Views/NotificationCardWindow.xaml.cs:410-424` (`OnForegroundCheckTick`, 500 ms poll started on show at `:392-395`), interacting with the auto-surface paths in `src/SenseEase.App/MainWindow.xaml.cs` (tier-up auto-expand `:755-761`; proposal surface `:773-779`) and `ShowActivated="False"` in `NotificationCardWindow.xaml:15`.
- **Confidence:** high.

**Intended behaviour.** Per `docs/POPUP-OVERLAY.md §4`, auto-expand (Toggle A) and the chatbot proposal flow are meant to proactively surface the card *while the user is doing something else* — that's the purpose of an ambient monitor. Dismissal is the 60 s idle → 5 s reversible fade.

**Actual behaviour.** The card is shown with `ShowActivated="False"` (so showing it does **not** move foreground away from the user's current app). A 500 ms `DispatcherTimer` then runs:

```csharp
private void OnForegroundCheckTick(object? sender, EventArgs e)
{
    IntPtr fg = GetForegroundWindow();
    GetWindowThreadProcessId(fg, out uint pid);
    if (pid != OurProcessId) Hide();   // any external app focused → hide
}
```

This "click-outside-auto-close" was intentionally added for *user-opened* cards (comment `:403-409`), but there is **no distinction** between user-opened and auto-surfaced cards. When a tier-up auto-expand fires while the user is watching provocative content in another app — exactly the design scenario — the very first poll tick sees a foreign PID and `Hide()`s the card within ≤500 ms. The user never sees the notification. (Chatbot proposals are less affected because the user is usually focused on the SenseEase chat when a proposal arrives, but the tier auto-expand path is broken outright.)

**Fix.** Gate the foreground-auto-close on user interaction: set a `_userHasInteracted` flag in `OnCardMouseMove` and only `Hide()` in `OnForegroundCheckTick` once it's true; or don't start `_foregroundCheckTimer` until the first mouse-over; or skip the foreground check while `ActiveProposal is not null`. The click-outside behaviour should apply only to user-initiated opens. (The overlay FOV mask is unaffected — it engages independently — so this is a notification UX break, not a loss of protection.)

---

## EM6 — `ExtractJson` breaks envelope parsing on every `[PROFILE_OVERRIDE]` turn → model reply replaced by generic fallback

- **Category:** bug (+ misconception — the in-code comment documents the *opposite* of the actual behaviour)
- **Severity:** EMERGENCY — the flagship LLM write-side path silently degrades: on every turn where the model correctly proposes a change, the chat bubble shows generic lexicon text instead of the model's tailored explanation, mismatched against the Card it surfaces.
- **Location:** `src/SenseEase.Infrastructure/Real/MistralChatbotService.cs:730-736` (`ExtractJson`), consumed by `TryParseModelDecision` `:531`/`:536`, fallback at `SendAsync` `:396-397`.
- **Confidence:** high (verified the format against `HabituationOverrideParserRealLlmTraceTests` and the System.Text.Json trailing-content behaviour).

**Intended behaviour.** Per `docs/HABITUATION-CHATBOT-INTEGRATION.md §3.1` and the system prompt, a proposal turn emits `{envelope-json}\n[PROFILE_OVERRIDE]\n{override-json}\n[/PROFILE_OVERRIDE]`. The envelope's `reply` becomes the chat bubble; the override drives the Card. The code comment at `:387-394` asserts case (a) — `[PROFILE_OVERRIDE]` after the envelope — is *"invisible in result.Response"* (i.e. envelope parse succeeds).

**Actual behaviour.**

```csharp
private static string ExtractJson(string raw)
{
    int start = raw.IndexOf('{');     // envelope open
    int end = raw.LastIndexOf('}');   // the OVERRIDE object's close brace
    if (start < 0 || end <= start) return string.Empty;
    return raw[start..(end + 1)];     // spans BOTH JSON objects + "[PROFILE_OVERRIDE]" text
}
```

When a `[PROFILE_OVERRIDE]` block trails, the last `}` is the override object's, so the extracted string is *two* JSON objects with `}\n[PROFILE_OVERRIDE]\n{` between them. `JsonSerializer.Deserialize<ModelDecision>` (`:536`) throws on trailing content after the first value → `catch` (`:669`) → `TryParseModelDecision` returns false → `SendAsync` calls `BuildFallbackResult` (generic lexicon reply). The model's tailored text is discarded. The Card still appears (it's parsed separately by `ChatbotViewModel` from `RawModelOutput`), so the user sees a generic bubble next to a specific proposal Card — incoherent. The dev comment's claim that this case is "invisible" is provably wrong; this is the intended happy path.

**Fix.** Extract only the first balanced JSON object (brace-depth scan from the first `{`, stop at depth 0), or strip the `[PROFILE_OVERRIDE]`/`[OBSERVATION_ADD]`/`[RESUME_ACKNOWLEDGE]` blocks before `ExtractJson` (reuse `HabituationOverrideParser.StripAllOverrideBlocks`). Add a test feeding the documented `{envelope}…[PROFILE_OVERRIDE]{…}` format and asserting `result.Response` equals the envelope's `reply`.

---

## EM7 — Raw user utterances are persisted to `adjustments.jsonl` (PII-posture violation)

- **Category:** bug (privacy)
- **Severity:** EMERGENCY for an Alpha gate — violates the project's documented "no PII in system logs" invariant in a health-adjacent app. (Mitigating context: local-only storage, and the same text already exists by design in `chat_history.jsonl`/`model_trace.jsonl`, so incremental exposure is limited.)
- **Location:** `src/SenseEase.Core/Models/AdjustmentRecord.cs:22` (`Utterance`), serialised whole by `src/SenseEase.Infrastructure/Storage/FileAdjustmentLog.cs`; populated at `ChatbotViewModel.cs` (~`:786,809,867`) and `MainViewModel.cs` (~`:1446,1461,1488`).
- **Confidence:** high (on the fact); the policy classification is backed by `LOG-PATHS.md §7`.

**Intended behaviour.** `LOG-PATHS.md:156-159` states the no-PII rule and enumerates the *only* logs exempt as "user content by design": `chat_history.jsonl`, `calibration_history.jsonl`, `model_trace.jsonl`. `adjustments.jsonl` is classified separately (`:23,:54`) as a **personalization/system log** ("personalization deltas"), so the no-PII rule applies to it. The line-158 note even claims a "Phase 4 audit found 0 PII leaks."

**Actual behaviour.** Every chatbot/feedback-driven adjustment writes the raw user discomfort utterance (e.g. "I feel sick scrolling Instagram at night") verbatim into `%LOCALAPPDATA%\SenseEase\adjustments.jsonl` — a permanent, unbounded, never-rotated file in the system-log class. The field is **write-only**: `IAdjustmentLog.WeightFor` only sums `Delta`, so the stored text is never read back — pure liability. The "0 PII leaks" audit missed it.

**Fix.** Stop persisting raw text in the adjustment log: drop `Utterance` from `AdjustmentRecord`, or replace it with a non-PII `pattern_id`/intent token (mirroring the event/action logs). If genuinely needed for debugging, gate it behind an explicit opt-in and re-classify `adjustments.jsonl` as user-content in `LOG-PATHS.md §7`. Add a test asserting a serialized adjustment line contains no raw-utterance text.

---

# NON-EMERGENCY FINDINGS

## Inference / engine

### NE1 — Stale, incorrect mask-hysteresis comment ("trigger at 100, release at 85")
- **Category:** misconception · **Location:** `MockInferenceEngine.cs:242` · **Confidence:** high
- The comment reads `// Hysteresis: trigger at 100, release at 85`, but `TriggerPressure = 85` so the code triggers at 85 and releases at `0.85×85 = 72.25` (matches whitepaper §7.3). The "100/85" numbers are leftover from a prior `TriggerPressure = 100`. Misleading for anyone reasoning about thresholds. **Fix:** update the comment to "trigger at 85, release at 72.25 (0.85×)".

### NE2 — Chatbot shader adjustments are never persisted and don't refresh `Current`
- **Category:** bug / functional-improvement · **Location:** `MockInferenceEngine.cs:313-322` (`ApplyShaderAdjustment`), persistence absent (no shader fields in `BuildProfileSnapshot`; grep of `Storage/` for `MaskDepth|GridVisibility|FeatherWidth|ShaderParams` → no matches) · **Confidence:** high (fact); medium (intent)
- `ApplyShaderAdjustment` mutates `_shader` and clamps, but (a) the new values are **not persisted anywhere**, so a chatbot "make the mask darker / hide the grid" is lost on restart — inconsistent with FOV offset (re-seeded from the adjustment log at `:162-173`) and habituation offset (persisted), and (b) unlike `ApplyUserFeedback` (`:303-307`), it does **not** refresh `Current.*` or raise any change signal, so if the sensing loop is stopped the overlay won't repaint until the next `Update` tick. **Fix:** persist the three shader knobs in the profile snapshot (and re-seed on load), and refresh `Current.Shader`/notify after `ApplyShaderAdjustment` as `ApplyUserFeedback` does. (Relates to backlog A7/G1 but the *persistence* gap is not in the backlog.)

## Chatbot / NLU

### NE3 — Raw server fetch isn't fail-safe; malformed-200 / network error throws past the lexicon fallback
- **Category:** bug · **Location:** `MistralChatbotService.cs:484-489` (`AskServerForDecisionAsync` has only `try/finally`, no `catch`) · **Confidence:** medium
- `JsonDocument.Parse(respBody)` throws on a non-JSON 200 body (proxy/AV interception, truncated stream); a dropped connection throws `HttpRequestException`; a timeout throws `TaskCanceledException`. All propagate out of `SendAsync` and surface as a raw `[Error]` bubble instead of the designed graceful degradation to `FeedbackLexicon` (the sibling `LlamaServerCalibrationClient.cs:80-85` is correctly guarded; this one isn't). **Fix:** wrap the fetch+parse in `try { … } catch { return null; }` — `null` already routes to `BuildFallbackResult`.

### NE4 — Lexicon "darker / too bright" *lightens* the mask (inverted sign), and darker/lighter are indistinguishable
- **Category:** bug · **Location:** `FeedbackLexicon.cs:152-153` (consumed live via `DeriveAdjustments` → `ClassifyShaderHints` at `:215`) · **Confidence:** high
- ```csharp
  if (ContainsAny(t, DarkerKeywords))  md -= 0.15;   // should be += (positive deepens, per MistralChatbotService.cs:48)
  if (ContainsAny(t, LighterKeywords)) md -= 0.15;
  ```
  Both branches subtract, so a "darker / 更暗 / too bright" request *lightens* the mask, and the two opposite intents produce identical deltas. Verified the deltas are live (used by `DeriveAdjustments`, which feeds `BuildFallbackResult`) — and the fallback path is hit more often than expected because of EM6. **Fix:** `DarkerKeywords` → `md += 0.15`; add tests pinning both directions.

### NE5 — Profile-switch lexicon hijacks "change the sensitivity" → SwitchProfile **SENSITIVE** (substring collision)
- **Category:** bug · **Location:** `HabituationProposalLexicon.cs:134-144` (switch checked before the sensitivity branch at `:173`), keyword `"sensitive"` at `:216`, `MentionsSwitch` at `:257-267` · **Confidence:** medium
- `"sensitivity"` contains the substring `"sensitive"`. An utterance like "change/switch the sensitivity (down)" matches the `sensitive` profile keyword **and** `MentionsSwitch`, so the earlier-evaluated profile-switch branch proposes switching to the **most-restrictive** SENSITIVE profile — the opposite of a "relax" request — and the dedicated `AdjustSensitivity` branch is never reached. If the user taps Accept they get the inverse of their intent. **Fix:** require whole-word profile-token matching for the switch branch, and/or evaluate the sensitivity-noun branch first.

### NE6 — Interactive LLM turns are uncancellable (and calibration holds a lock while awaiting)
- **Category:** ux-breaker / functional-improvement · **Location:** `ChatbotViewModel.cs:375` passes `CancellationToken.None` (the prepared `_streamCts` token at `:352-355` is unused for HTTP); `CalibrationChatbotService.cs:169` passes no token at all · **Confidence:** high
- The `HttpClient` timeout is 5 minutes and `MistralChatbotService.SendAsync` honours a `ct`, but the chat path passes `None`, so `_streamCts.Cancel()` only stops the cosmetic fake-stream animation, never the HTTP request. On a slow/stuck llama-server (the handbook documents 5–30 s/turn on Iris Xe iGPU) the user can't abort or supersede an in-flight turn for up to 5 minutes. The calibration path holds `_lock` across its await, freezing that session. (Not a whole-app freeze — the calls are async — so non-emergency, but it's a real responsiveness gap.) **Fix:** pass `streamToken` at `:375`; thread a `CancellationToken` through `CalibrationChatbotService.SendUserMessageAsync` → `_llm.SendChatAsync`; consider a shorter interactive timeout.

### NE7 — `MockNluService` and the `ChatbotNluService` catch-path bypass the crisis safety floor
- **Category:** bug (defense-in-depth) · **Location:** `Mock/MockNluService.cs:12-21`; `Real/ChatbotNluService.cs:33-42` (catch fallback) · **Confidence:** medium
- `SAFETY-PROVISIONS.md §1` requires the crisis short-circuit to sit *before* the lexicon/NLU. `MockNluService.InterpretAsync` calls `FeedbackLexicon.Classify` directly with **no** `TripsSafetyFloor` check, and `ChatbotNluService`'s exception fallback does the same. Not currently exploited (live wiring routes through `ChatbotNluService(mistral)` whose `SendAsync` floors first, and `MockNluService` isn't constructed in the app), but `docs/archive/ARCHITECTURE.md:59` lists `MockNluService` as *the* `INluService` impl — if it's ever swapped in, crisis phrases reach the lexicon. **Fix:** add a `TripsSafetyFloor` short-circuit to both entry points, ideally via a shared `SafetyFloorVocabulary` (dovetails with backlog C3).

### NE8 — Calibration convergence reminder is sent to the LLM but never written to the transcript
- **Category:** bug (fidelity) · **Location:** `CalibrationChatbotService.cs:156-162` · **Confidence:** high
- The injected `[SYSTEM REMINDER]` pseudo-user turn is added to `_history` (and thus influences the model's final turn) but `AppendTranscriptLine` is not called for it, so `calibration_history.jsonl` is no longer a faithful replay of what the model saw. **Fix:** persist the reminder line too, or document it as intentionally non-persisted.

## Sensing / IPC

### NE9 — WGC cursor-move suppression rests on a false premise and over-suppresses real motion
- **Category:** misconception / bug · **Location:** `WindowsActivityFilter.cs:30-33,275-291`; WGC disables cursor capture at `WgcSensingProvider.cs:393` (`IsCursorCaptureEnabled = false`); default mode is `Wgc` · **Confidence:** medium
- The filter justifies a 300 ms OFI-suppression-on-cursor-move window by "the cursor sprite redrawing … WGC pixel-diff incidental noise from the cursor itself" — but WGC excludes the cursor sprite, so that noise doesn't exist. Meanwhile the 20 Hz poll + 300 ms window means *continuous mouse movement holds OFI at 0 permanently* in WGC mode, so moving the mouse while watching motion-heavy content suppresses VIMS detection and the mask never engages — the opposite of the product's purpose. (The cursor-velocity provider already handles self-driven cursor motion via button/capture checks.) **Fix:** drop or sharply shorten this gate for WGC mode, or gate it on actual user-drive evidence (button/capture), and correct the comment.

### NE10 — Sidecar staleness uses wall-clock `DateTime.UtcNow`, not a monotonic clock
- **Category:** bug · **Location:** `SidecarHeadPoseReader.cs:278-281` (and store sites `:534,:710`) · **Confidence:** high
- Both "stored at" and "now" use `DateTime.UtcNow`, despite comments calling it a "monotonic timestamp." A backward clock step (NTP, VM resume, manual change) makes `ageMs` negative → a stale frozen head-pose is treated as fresh; a forward step prematurely rejects valid samples — defeating the 2 s OBS-2 safety fallback. **Fix:** use `Environment.TickCount64`/`Stopwatch.GetTimestamp()` for both timestamps (as `UserActivityModeDetector.GetIdleMilliseconds` already does).

### NE11 — Capture `_timer` (and per-Resume watchdog timers) are never disposed
- **Category:** bug · **Location:** `WgcSensingProvider.cs:454-483` (`Stop`/`Dispose` only `Change`, never `Dispose`); watchdog `:266-289` · **Confidence:** high
- `Dispose() => Stop();` and `Stop()` only calls `_timer.Change(Infinite,Infinite)` — the `System.Threading.Timer` is leaked; each `Resume()` allocates a one-shot watchdog timer that's only collectible after it fires. Low steady-state impact (provider is ~singleton) but it's the same gap that blocks a clean fix for EM3 (no wait-handle drain). **Fix:** `_timer.Dispose(waitHandle)` in `Dispose()`; track/reuse the watchdog timer.

### NE12 — Typing→Driver override misses active typing when a higher-priority suppression reason is set
- **Category:** bug · **Location:** `UserActivityModeDetector.cs:169-171` depends on `WindowsActivityFilter.SuppressionReason` priority order (`:333-346`) · **Confidence:** high
- `isTypingNow = SuppressionReason == "typing"`, but `SuppressionReason` returns the single *dominant* reason (`window-switch > window-drag > menu > toast > typing > cursor`). If the user types while a toast shows or the mouse just moved, `isTypingNow` is false even with a key down, so the typing→Driver override silently doesn't apply → brief misclassification as Passenger (×2.0 weight). **Fix:** expose a dedicated `IsTypingActive` boolean checked independent of the dominant-reason ranking.

### NE13 — `SidecarHeadPoseReader` constructor XML docs reference removed parameters
- **Category:** dead-code / misconception · **Location:** `SidecarHeadPoseReader.cs:139-141` · **Confidence:** high
- Two `<param>` tags name pre-v0.11.1 params (`pythonExePath`, `sidecarScriptPath`) removed when the sidecar became a single PyInstaller exe; the actual `sidecarExePath` is undocumented → CS1572/CS1573 warnings. **Fix:** replace with one `<param name="sidecarExePath">`.

### NE14 — Mask exclusion can write into the shared static `EmptyFrameSentinel`
- **Category:** bug (latent) · **Location:** `WgcSensingProvider.cs:113-115` (static `readonly` sentinel "never mutated"), mask write `:705-725`, aliasing at `:530` · **Confidence:** low
- After a null-frame tick aliases `_prevFrame = EmptyFrameSentinel`, the next exclude-rect mask pass writes zeros into the shared static buffer. Value-harmless today (zeros into zeros) but violates the "never mutated" invariant and is a foot-gun if the sentinel ever becomes non-zero or instances carry different masks. **Fix:** guard with `if (!ReferenceEquals(_prevFrame, EmptyFrameSentinel))`, or never alias the sentinel into `_prevFrame`.

## Storage / persistence / config

### NE15 — `FileHabituationStore` uses non-atomic `File.WriteAllText` + `File.Move` (not the crash-safe writer)
- **Category:** bug · **Location:** `FileHabituationStore.cs:49-52` · **Confidence:** medium
- The durable offset file (Q4.2) — which is also the wipe-detection probe artifact — is written with no fsync and `File.Move(overwrite:true)`, which lacks `File.Replace`'s single-transaction semantics; an ill-timed crash/power-loss can promote a truncated file or leave `.tmp` residue (no residue test exists, unlike the profile store). **Fix:** route through `AtomicJsonWriter.TryWrite`/`TryRead`.

### NE16 — `AtomicJsonWriter` doesn't fsync the temp file before replace (doc over-promises "+ flushed")
- **Category:** bug · **Location:** `AtomicJsonWriter.cs:54-71` · **Confidence:** medium
- `File.WriteAllText(tmp)` closes the handle but doesn't force contents to disk before `File.Replace`. The doc-comment claims "Once tmp is fully written + flushed…", but on power loss the renamed file's data blocks may not be persisted → a zero-length/truncated `habituation_profile.json`/`user-preferences.json` on next boot (readers fall back to defaults, so it degrades to lost personalization, not a crash). `Process.Kill` *is* safe (OS cache survives). **Fix:** write via `FileStream` + `Flush(flushToDisk:true)` (or `FileOptions.WriteThrough`) before replace, or soften the comment.

### NE17 — `UserPreferences` roll-forward only accepts `{1, Current}`; a future v3 silently wipes all v2 prefs
- **Category:** bug (latent) · **Location:** `UserPreferences.cs:209-220` · **Confidence:** medium
- The guard is `SchemaVersion != 1 && != CurrentSchemaVersion → defaults`. When `CurrentSchemaVersion` bumps to 3, a readable **v2** file fails the guard and is discarded back to defaults — wiping every preference for everyone upgrading from the immediately-previous version, the exact data-loss the roll-forward design was meant to prevent. Dormant today (Current=2). **Fix:** accept any `SchemaVersion ∈ [1, CurrentSchemaVersion]` and roll forward; reserve defaults for `> Current` / `< 1` / malformed.

### NE18 — `chat_history.jsonl` trim is one-shot at startup; in-session growth is unbounded and the file is triple-read
- **Category:** functional-improvement · **Location:** `ChatbotViewModel.cs:245-251` (only `Trim` call), `FileChatHistoryStore.cs` (`Trim` re-`Load`s) · **Confidence:** medium
- `Trim(PersistedHistoryMax=100)` runs once at construction; a long single session appends without bound until the next launch trims. `Trim` also `Load`s twice and the caller re-`Load`s — three full reads of a potentially large transcript on the UI-constructing path. `Append` swallows all IO errors silently. **Fix:** trim opportunistically after append (counter or every-N); reuse the loaded list in `Trim`.

### NE19 — `InMemoryChatbotActionLog` grows unbounded
- **Category:** functional-improvement · **Location:** `InMemoryChatbotActionLog.cs:25,37` · **Confidence:** medium
- The class doc itself notes the list "grows unbounded today." This app is designed to run tray-resident indefinitely; every proposal-lifecycle event is appended forever, with O(n) scans in `MostRecentDeclineFor`. **Fix:** add the FIFO soft-cap (~500) the doc already proposes.

### NE20 — Longitudinal-observation dedup mixes the date prefix into similarity tokens
- **Category:** bug · **Location:** `LongitudinalObservationWriter.cs:67-72` (date-stamps *before* `IsDuplicateOfRecent`; `JaccardSimilarity` tokenizes the whole stamped string) · **Confidence:** medium
- The same observation emitted on different days carries differing `"YYYY-MM-DD:"` tokens, lowering similarity below the 0.7 threshold and defeating dedup exactly across the day boundary the feature most wants to collapse; same-day repeats get a spurious shared token. **Fix:** dedup on the trimmed body *before* prepending the date stamp.

### NE21 — Persisted timestamps use `DateTime.Now` (local, no offset) in several models vs UTC everywhere else
- **Category:** bug · **Location:** `ChatMessage.cs:17`, `EventLogEntry.cs:6,20`, `MockSensingProvider.cs:27` (and `ISensingProvider` default) · **Confidence:** medium
- These default to `DateTime.Now` (`Kind=Local`); System.Text.Json writes local `DateTime` without an offset, so on reload the kind becomes `Unspecified` and the instant is reinterpreted. A user crossing a timezone/DST boundary gets wrong transcript times, and mixing local-kind with the UTC-kind used everywhere else (`AdjustmentRecord`, snapshot `*Iso`, etc.) breaks any future "age of last X" logic. In Demo mode the mismatched mock timestamp can even feed a wrong/negative `dt` into the integrator across a mode swap. **Fix:** store `DateTime.UtcNow`/`DateTimeOffset`; convert to local only at display time.

### NE22 — `AutoStartRegistry` is not self-healing — a stale Run-key path is never corrected
- **Category:** functional-improvement · **Location:** `AutoStartRegistry.cs:38-50` (`IsEnabled` only checks value presence), consumed `SettingsContent.xaml.cs:84-86` · **Confidence:** medium
- `IsEnabled()` returns true if *any* value exists, regardless of whether it still points at the current EXE. After an in-place update to a new versioned folder, a reinstall, or a portable move, the Run key points at the old/missing EXE, the toggle still shows "on," and `SetEnabled` is only called on user toggle — so autostart silently launches nothing. (Quoting itself is correct.) **Fix:** compare the stored value against `MainModule.FileName` and rewrite when enabled-but-stale (or rewrite on every startup when the toggle is on).

### NE23 — `AtomicJsonWriter` first-write TOCTOU can silently drop a write
- **Category:** bug · **Location:** `AtomicJsonWriter.cs:57-71` · **Confidence:** low
- Between `File.Exists(path)` and `File.Move(tmp, path, overwrite:false)`, if the destination races into existence the `File.Move` throws, the outer `catch` swallows it, and the write is silently lost. **Fix:** use `File.Move(tmp, path, overwrite:true)` in the fallback (atomic same-volume rename; harmless on race), making the `File.Exists` branch a mere optimization.

## UI / Views

### NE24 — Card gear icon can open unlimited concurrent `SettingsWindow` instances
- **Category:** bug · **Location:** `NotificationCardWindow.xaml.cs:575-599` (`OnSettingsClicked`) · **Confidence:** high
- Unlike `MainWindow.ShowSettingsWindow` (`:639-650`, which guards against duplicates), the card's gear constructs and shows a new non-modal `SettingsWindow` on every click. N clicks → N stacked windows, each re-arming the card idle timer on close and each subscribing `PreferencesChanged` (so one toggle fires N repaints); concurrent edits race on `UserPreferences`. **Fix:** mirror the single-instance guard, or route the gear through the host's `ShowSettingsWindow`.

### NE25 — `CompositionTarget.Rendering` head-pose pump runs every frame even when head-tracking is off (the default)
- **Category:** functional-improvement · **Location:** `MainWindow.xaml.cs:447-460` (unconditional subscribe), `:499-563` (`OnCompositionRendering`) · **Confidence:** high
- The per-frame handler is subscribed unconditionally, not gated on `AppConfig.HeadTrackingEnabled` (off by default → `DefaultHeadPoseReader.ReadLatest()` always returns null). On a 165 Hz panel that's ~165 needless delegate invocations/sec (+ `DateTime.UtcNow`, `ReadLatest`, no-op brush calls) for the whole app lifetime — wasted CPU/battery for users who never enabled head tracking. **Fix:** subscribe only when head tracking is enabled (subscribe/unsubscribe alongside the sidecar start/stop in `ApplyPowerSave`). **See NE31** — this pump is also a confirmed residual cause of demo/calibration-video stutter.

### NE26 — `MarkdownInlines` renders underscores mid-token as italic, corrupting technical chatbot replies
- **Category:** ux-breaker · **Location:** `MarkdownInlines.cs:136-146` · **Confidence:** medium
- Any line with two `_` is italicized between them regardless of word boundaries. Replies routinely mention `personal_pressure_scale`, `severe_strike_limit`, `expires_at_iso`, `[OBSERVATION_ADD]` — these render with spurious italics and swallowed underscores, making identifiers unreadable. **Fix:** require word-boundary conditions for `_` emphasis, or drop `_` italic support (keep `*italic*`).

### NE27 — Expired-proposal tombstone leaves the Decline button enabled while disabling Accept/Later
- **Category:** ux-breaker · **Location:** `NotificationCardWindow.xaml.cs:200-206` · **Confidence:** medium
- On expiry, Accept/Later are disabled and `_activeProposal` is kept as a tombstone, but `ProposalDeclineBtn` stays clickable; clicking it fires `ProposalDeclined` and prematurely collapses the tombstone panel (the VM side is idempotent, so it's harmless beyond the inconsistency). **Fix:** also disable `ProposalDeclineBtn` in the expiry branch (and null `_activeProposal`).

### NE28 — In-card chat reply poll can surface the system-adjustment table or a partial stream instead of the assistant reply
- **Category:** bug · **Location:** `MainWindow.xaml.cs:886-908` · **Confidence:** low
- The poll returns as soon as `Messages.Count > beforeCount+1` and reads the last bubble, but `SendAsync` can append a separate "系統調整 / System adjustment" summary bubble and lexicon replies are fake-streamed, so the card may show a markdown-table fragment or a mid-animation string. **Fix:** track the specific assistant bubble for the turn (or skip system-adjustment-marked bubbles and wait for `IsGenerating == false`).

### NE29 — `ClickThrough` uses the 32-bit `GetWindowLong`/`SetWindowLong` on x64
- **Category:** functional-improvement · **Location:** `Interop/ClickThrough.cs:18-33` · **Confidence:** low
- The app is x64; the correct API for window longs is `GetWindowLongPtr`/`SetWindowLongPtr`. The 32-bit variants happen to work for `GWL_EXSTYLE` but this is the pattern MS warns against, and it's the most safety-critical interop in the UI (a regression here = overlay steals all input). **Fix:** use the `*Ptr` variants with `nint`, and verify success.

### NE30 — Overlay grid has no default `OpacityMask` → possible full-screen grid flash on DPI/monitor change
- **Category:** ux-breaker · **Location:** `OverlayWindow.xaml:13-18` (no mask on `GridCanvas`), `.xaml.cs:215` (`OnLoaded → DrawGrid`), `:620-631` (mask attached only in `ApplyState`) · **Confidence:** low
- `DrawGrid` runs at opacity 1.0 with no radial mask; the feather is only applied inside `ApplyState`. A `SizeChanged`-triggered `DrawGrid` (DPI/monitor change) with no immediately-following `ApplyState` can paint full-strength grid lines across the whole screen. **Fix:** define a default radial `OpacityMask` + low default opacity on `GridCanvas` in XAML, or re-apply the falloff at the end of `DrawGrid`.

## Performance / demo playback

### NE31 — Calibration/demo video stutters: GPU/render-thread self-contention (dominant) + WPF `MediaElement` has no hardware video overlay (residual)
- **Category:** ux-breaker / functional-improvement
- **Severity:** NON-EMERGENCY (degrades the calibration *experience*; calibration still completes and measures pressure)
- **Location:** `src/SenseEase.App/Views/DemoVideoWindow.xaml:14` (`MediaElement` renderer); `src/SenseEase.Infrastructure/Real/WgcSensingProvider.cs:398-412,651` (full-resolution `CopyResource`+`Map` readback); `src/SenseEase.App/MainWindow.xaml.cs:447-460,499-563` (per-frame pump, = NE25). Asset resolved at `AppConfig.cs:116`.
- **Confidence:** high — empirically isolated (ffprobe + external-player comparison + Power-Save A/B).

**Symptom.** The demo/calibration clip stutters and drops frames during playback.

**The file and codec are exonerated.** The played asset `samples/FPV-vid-h264.mp4` is **H.264 High@L4.0, 1920×1080, 29.97 fps, ~15.8 Mbps, yuv420p 8-bit** (ffprobe) — the most universally hardware-decoded format on Windows. The same clip — and even the gitignored 4K AV1 master `samples/FPV-vid.mp4` (3840×2160, AV1 Main) — plays smoothly in YouTube and the Windows Media Player app. (The AV1 master was a genuine codec problem for WPF and is already worked around by the H.264 transcode per `AppConfig.cs:110-115`; it is not the played file.)

**Root cause, in empirically-confirmed order of impact:**
1. **(Dominant) GPU / WPF-render-thread self-contention.** The demo runs with sensing live by design, so while `MediaElement` decodes + composites the video through the WPF visual tree (it has **no** hardware video overlay / flip-model swapchain — the classic WPF "airspace" limitation), `WgcSensingProvider` performs a **full-monitor-resolution** `CopyResource`+`Map` GPU→CPU readback **15×/sec** on the same GPU, and the overlay mask composites a full-screen radial gradient+grid on top when pressure spikes. **A/B test (user-run):** toggling Power Save — which pauses WGC and stops the mask engaging — removes *most* of the lag, confirming this is the dominant factor. Worst on shared-memory iGPUs (Iris Xe) and high-resolution monitors (readback cost scales with native resolution).
2. **(Residual) WPF `MediaElement` is renderer-coupled.** Real Media Foundation players (YouTube/WMP) present video on an independent hardware layer; `MediaElement` is coupled to the WPF render thread, so it hitches on any render-thread work that *survives* Power Save — notably the unconditional per-frame `CompositionTarget.Rendering` pump (**NE25**), the chart/metrics `DispatcherTimer`s, and GC pauses. This is the "still stutters sometimes with the overlay off" residual the user observed.
3. **(Aggravator — live runs only) EM1.** In a non-Power-Save calibration run, when the clip's motion drives pressure past ~0.6×trigger, **EM1**'s per-tick strike storm rewrites `habituation_profile.json` ~15×/sec and marshals event-log appends to the UI thread ~15×/sec — an intermittent disk + UI-thread storm landing during exactly the high-motion segments, adding to the stutter.

**Fix (prioritized):**
1. **Downsample the WGC frame on the GPU** (pixel/compute shader, or create the `FramePool`/staging texture at the target size) and `Map` only the **160×90** result (~14 KB) instead of the full-resolution staging texture — removes ~99% of the readback cost. Highest-impact, and a general perf win.
2. **Quiet the WPF render thread while `DemoVideoWindow` is open** — suspend the per-frame `CompositionTarget.Rendering` pump (NE25) and the chart/metrics timers for the demo's duration. Directly targets the residual stutter.
3. **Fix EM1** (edge-trigger strikes / throttle the snapshot save) to remove the intermittent disk+UI storm in live runs.
4. **Only if 1–3 are insufficient:** render the clip via a Media Foundation path (`MediaPlayerElement` interop / LibVLCSharp) instead of `MediaElement`. Items 1–3 are wanted for general performance regardless and don't require swapping the renderer.

**Cross-refs:** NE25 (per-frame render pump), EM1 (strike/persist storm), NE9 (WGC capture-mode behaviour).

---

## Cross-cutting themes

1. **`MaybePersistProfileSnapshot`/`BuildProfileSnapshot` is the single most dangerous code path** — it's the root of EM1 (storm), EM2 (observation wipe), and the calibration-field omission. Any fix should treat the profile snapshot as a *merge over existing on-disk state on a throttled, edge-triggered cadence*, with one lock owning the read-modify-write.
2. **Time handling is inconsistent** — wall-clock vs monotonic for staleness (NE10), local vs UTC for persisted timestamps (NE21). Pick one convention per purpose (monotonic for elapsed/staleness, UTC for persisted instants) and apply it everywhere.
3. **Sign/direction correctness in the NL → actuator mapping is untested** — EM6 (envelope parse), NE4 (darker inverts), NE5 (sensitivity→profile) all silently do the *opposite* of the user's intent and none are pinned by a direction test. A small "intent → expected delta sign" test matrix would catch all three.
4. **Lifecycle teardown lacks synchronization** — EM3 (COM race), EM4 (permanent disable), NE11 (timer leak), NE24 (window leak). The sensing provider and card windows need disciplined dispose/drain.
5. **Fail-safe degradation has gaps** — EM6 + NE3 mean the chatbot falls back to the lexicon far more than intended, and the lexicon itself has the NE4 inversion, so the fallback is also wrong.

## Dead code / misconceptions (quick index)
- NE1 — stale "trigger at 100" comment (actual 85).
- NE9 — WGC cursor-suppression comment cites cursor-sprite noise that can't occur (cursor capture disabled).
- NE13 — `SidecarHeadPoseReader` `<param>` docs reference deleted parameters.
- EM6 — `MistralChatbotService.cs:387-394` comment claims the override-after-envelope case is "invisible," but it actually breaks the parse.
- `CalibrationConfidence` remains computed-but-unconsumed (backlog C4) — its hardcoded 60 s assumed demo duration in `Q1AlignsWithSensorPeak` (`CalibrationProcessor.cs:333`) is fragile, but it's dead until C4 wires a consumer.

## Scope checked and cleared (no new finding)
- **OBS-3 privacy on the C# side:** `SidecarFrameDto` carries only allowlisted scalars; `yaw_deg`/`pitch_deg` are discarded in `TryParseSampleLine`; the only sidecar disk writes are the lifecycle log. (The missing *enforcement test* is backlog E3.)
- **Safety-floor phrase tables** are byte-identical between the two services apart from the known C3 colon; the floor short-circuits before the LLM on the live path.
- **Q4.6 stateless contract** holds: `BuildContextualInput` sends only the bounded `[RecentAdjustments]`/`[RecentEvents]` (≤6) + state + current turn; `_history` is never sent to the server.
- **Engine→UI threading** is correctly marshalled (Dispatcher) at the `StateChanged`, persistent-bar, tray, and `_log.Appended` seams; tray static-event subscription is unsubscribed on dispose; HICON cache is bounded/disposed in order.
- **Tier/band thresholds** (1–5 Grey / 6–7 Amber / 8–10 Red) and mask hysteresis (85 / 72.25 / 8000 ms cooldown) match the spec; `UpdateVimsTier`'s `Math.Clamp` bounds can't invert (verified the `min > max` cases are unreachable).
- **`LeakyIntegrator`** math matches the whitepaper; `dt` is clamped to `[0.001, 2.0]` so a sleep/resume gap can't spike pressure.
- **JSON parsing** (override/calibration/feedback) is culture-invariant and rejects NaN/Infinity; regexes have no catastrophic-backtracking risk.

---

## Relationship to `docs/ALPHA_BACKLOG.md`
Every item above is **new** — none duplicates the 30-item whitepaper audit. The backlog is mostly *spec-vs-code drift and missing tests*; this sweep surfaces *runtime defects* (storms, races, data loss, crashes, silent feature breakage). Recommended Alpha-1 additions: **EM1, EM2, EM3, EM4, EM5, EM6, EM7** (all seven), plus NE4/NE5/NE7 (intent-inversion + safety-floor defense-in-depth) given their low fix cost and user-facing wrongness.

*End of review.*
