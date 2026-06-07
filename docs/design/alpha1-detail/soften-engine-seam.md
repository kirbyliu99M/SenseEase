# Resolution — "Soften" Engine Seam (BEHAVIOR CHANGE)

> Generated 2026-06-05 by the senseease-rebrand-refine workflow (8 agents, adversarially reviewed). Draft for review — no code changed, nothing committed.

I now have everything needed. Here is the decision-grade markdown section.

---

## RESOLVED: Deliberate "Soften / more protection" preference must NOT count as discomfort recurrence

**Status:** ENGINE BEHAVIOR CHANGE — requires product-owner sign-off before merge (touches `IInferenceEngine`, `MockInferenceEngine.ApplyUserFeedback`, and the habituation `_offset` path). Scoped per C5 discipline: enumerated below, nothing more.

### The problem, pinned to code

Today there is exactly one FOV-feedback entry point, `MockInferenceEngine.ApplyUserFeedback(int fovDelta)` (`src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs:292-311`). Every negative delta is unconditionally treated as a discomfort recurrence:

```csharp
// MockInferenceEngine.cs:299-302
if (fovDelta < 0)
    _habituation.NotifyDiscomfort(
        Math.Abs(fovDelta) >= SenseDefaults.HabituationSevereFovThreshold, // 18
        _vimsTier);
```

`NotifyDiscomfort` (`HabituationEngine.cs:342-351`) then erodes the very thing the product is building — the earned tolerance `_offset` (the Q4.5 invariant = "weeks of user progress"):
- **severe** (`|delta| >= 18`): `RegisterSevereStrike(weight: 0.5, …)` → halves `_offset` (or zeroes it + resets strike count at the limit), bumps the strike counter, and forces a pause (`HabituationEngine.cs:359-380`).
- **mild** (`|delta| < 18`): `_offset = max(0, _offset - 4)` and pauses (`HabituationEngine.cs:349-350`).

So a calm, deliberate user who types "a little more protection please" (lexicon maps to `ComplaintFovDelta = -15`, `SenseDefaults.cs:22`; or the chatbot clamps to `[-30,0]`, `MistralChatbotService.cs:606`) silently burns down `_offset` and trips a pause. The "you're improving" copy then contradicts the engine state on the same tick. **This is the contradiction to fix.**

Note both UI call sites already label this `Source = "user"` (`ChatbotViewModel.cs:809`, `MainViewModel.cs` ~1460) — the intent is *known at the call site*, it is just thrown away before it reaches the engine.

### The distinction the engine must encode

| Path | Source | Today | Correct policy |
|---|---|---|---|
| User explicitly asks for tighter FOV via chat/feedback | `ApplyUserFeedback(neg)` from `ChatbotViewModel`/`MainViewModel` | discomfort recurrence (erodes `_offset`, pauses) | **NEUTRAL** — a standing preference, not a recurrence |
| Sensor pressure crosses recurrence threshold | `HabituationEngine.Step` → `pressure >= trigger * RecurrencePressureFraction` → `RegisterSevereStrike(weight:1.0)` (`HabituationEngine.cs:273-277`) | real discomfort | **UNCHANGED — still real discomfort** |
| Chatbot detects a genuine *complaint* ("I feel sick / dizzy") | `NotifyDiscomfort` via the same `ApplyUserFeedback` path | discomfort | **STILL discomfort** (see "why a flag, not a route-removal") |

The sensor path never flows through `ApplyUserFeedback` — it lives entirely inside `Step`. **So the sensor-driven discomfort guarantee is preserved for free**: this change cannot touch it. That is the key scoping fact that keeps the blast radius tiny.

### Recommended design — MINIMAL seam: one optional flag, default preserves today's behavior

Do **not** add an overload-with-new-method or a second engine entry point (that duplicates the clamp/persist/refresh body at `MockInferenceEngine.cs:296-309` — a C5 divergence point). Add **one optional boolean parameter** with a default that is byte-for-byte the current behavior, so every existing caller and test is untouched.

**Change 1 — interface (`IInferenceEngine.cs:28`)** — additive, source-compatible:
```csharp
/// <param name="isDeliberatePreference">
/// True when the negative delta is a standing user *preference* ("give me more
/// protection") rather than a discomfort recurrence. When true the FOV offset is
/// applied but the habituation engine is NOT notified — the earned _offset (Q4.5)
/// is preserved. Default false keeps the legacy discomfort-recurrence behavior.
/// Sensor-driven recurrence does NOT use this path (it lives in HabituationEngine.Step).
/// </param>
void ApplyUserFeedback(int fovDelta, bool isDeliberatePreference = false);
```

**Change 2 — implementation (`MockInferenceEngine.cs:292-311`)** — guard the single `NotifyDiscomfort` call, nothing else:
```csharp
public void ApplyUserFeedback(int fovDelta, bool isDeliberatePreference = false)
{
    lock (_gate)
    {
        _userFovOffset = Math.Clamp(_userFovOffset + fovDelta, -60, 0);
        // A negative (tightening) delta is a discomfort recurrence ONLY when it is
        // NOT an explicit standing preference. A deliberate "more protection" request
        // is NEUTRAL: it moves the FOV but must not erode the earned habituation _offset
        // (Q4.5) or trip a pause. Sensor-driven recurrence is unaffected — it never
        // reaches this method (see HabituationEngine.Step).
        if (fovDelta < 0 && !isDeliberatePreference)
            _habituation.NotifyDiscomfort(
                Math.Abs(fovDelta) >= SenseDefaults.HabituationSevereFovThreshold,
                _vimsTier);
        // ... unchanged: ComposeFovRadius / HabituationLevel / HabituationPaused / persist
    }
}
```

That is the **entire** engine change: one parameter, one `&& !isDeliberatePreference` guard. `_offset`, the strike counter, the pause cooldown, `RegisterSevereStrike`, `Step`, and the whole `HabituationEngine` are untouched. The FOV offset still moves (the user gets the protection they asked for, applied via `_userFovOffset` at `MockInferenceEngine.cs:296` and composed into the radius at `:830`), but the habituation *progress ledger* is not charged for it.

**Why a flag, not a route-removal (this is the load-bearing scoping decision):** The same `ApplyUserFeedback(neg)` channel still carries genuine chatbot complaints ("I feel dizzy") whose lexicon delta is also negative. We must **not** make all negative deltas neutral — that would silence the chatbot discomfort signal entirely. The deliberateness is intent that only the *caller* knows, so the caller must declare it. The engine stays a dumb, deterministic executor (consistent with `HabituationEngine`'s "engine is intentionally ignorant" design, `HabituationEngine.cs:160-166`).

### Call-site wiring (UI layer — no sign-off needed, but ships together)

The two NLU call sites must pass `isDeliberatePreference: true` *only* when the NLU intent is a preference, not a complaint. The seam already exists: the lexicon emits `"ReportDiscomfort"` for complaints (`FeedbackLexicon.cs:171`). So:
- A **preference** intent (e.g. an explicit "Soften / more protection" control button, or NLU intent ≠ `ReportDiscomfort`) → `ApplyUserFeedback(delta, isDeliberatePreference: true)`.
- A **`ReportDiscomfort`** intent → `ApplyUserFeedback(delta)` (unchanged, default false).

Concretely at `ChatbotViewModel.cs:800` and `MainViewModel.cs:1453`, pass `isDeliberatePreference: result.Intent != "ReportDiscomfort"` (or wire a dedicated control's handler to pass `true` literally). Keep the `AdjustmentRecord.Source = "user"` log line as-is — it already records the human origin and feeds `SetPersonalization`'s caution model (`HabituationEngine.cs:387-405`), which *should* still see that this user tends to tighten. That personalization (longer window / slower cadence for cautious users) is the correct, non-punitive place for a tightening preference to register; it does **not** touch `_offset`.

### Q4.5 invariant proof

- Deliberate path: `NotifyDiscomfort` is never called → `_offset` is never read or written by this path → no halve, no `-4`, no zero-at-limit, no pause. The earned weeks-of-progress offset is mathematically untouched.
- `ResetPersonalization`/`Reset` are not invoked here, honoring "never call `_habituation.Reset()` for transient changes."
- Sensor recurrence (`Step`, `HabituationEngine.cs:273-277`, weight 1.0) is in a different method and is provably unaffected.

### Regression test — pins BOTH paths (add to `HabituationTests.cs`)

```csharp
[Fact]
public void ApplyUserFeedback_DeliberatePreference_DoesNotErodeHabituationOrPause()
{
    var engine = new MockInferenceEngine();
    engine.SetHabituationPaused(false);
    for (int i = 0; i < 600; i++) engine.Update(0.0, "Passenger"); // earn progress
    int earned = engine.Current.HabituationLevel;
    Assert.True(earned > 0, "precondition: must have earned habituation progress");

    // Severe-magnitude tightening (|−22| ≥ 18), but flagged deliberate.
    engine.ApplyUserFeedback(-22, isDeliberatePreference: true);

    Assert.Equal(earned, engine.Current.HabituationLevel);     // Q4.5 offset preserved
    Assert.False(engine.Current.HabituationPaused);            // no discomfort pause
    Assert.True(engine.UserFovOffset < 0);                     // protection STILL applied
}

[Fact]
public void ApplyUserFeedback_DefaultPath_StillCountsAsDiscomfort()
{
    var engine = new MockInferenceEngine();
    engine.SetHabituationPaused(false);
    for (int i = 0; i < 600; i++) engine.Update(0.0, "Passenger");
    int earned = engine.Current.HabituationLevel;

    engine.ApplyUserFeedback(-22); // default false → severe discomfort recurrence

    Assert.True(engine.Current.HabituationLevel < earned, "default path must erode offset");
    Assert.True(engine.Current.HabituationPaused, "default path must pause");
}
```

The second test is effectively the existing `MockEngine_DiscomfortFeedbackPausesHabituation` (`HabituationTests.cs:354-369`) plus an offset-erosion assertion — confirming the default overload is byte-compatible. A complementary sensor-path pin already exists and must stay green: `PressureSpike_HalvesOffsetAndPauses` (`HabituationTests.cs:147-161`).

### Recommendation

**Adopt the optional-flag seam (Change 1 + Change 2), default `false`.** It is the minimal possible engine edit (one parameter, one boolean guard), preserves Q4.5 by construction, leaves the sensor-driven discomfort guarantee literally untouched (different method), keeps the chatbot complaint signal intact, and resolves the copy/engine contradiction. **Flagged as an engine behavior change requiring user sign-off** because it alters how a class of negative `fovDelta` inputs mutates `_offset`.

---

Key files: `src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs:292-311`, `src/SenseEase.Core/Inference/HabituationEngine.cs:273-277,342-351,359-380`, `src/SenseEase.Core/Abstractions/IInferenceEngine.cs:28`, call sites `src/SenseEase.App/ViewModels/ChatbotViewModel.cs:797-813` and `src/SenseEase.App/ViewModels/MainViewModel.cs:1450-1461`, lexicon `src/SenseEase.Infrastructure/Nlp/FeedbackLexicon.cs:171`, tests `tests/SenseEase.Tests/HabituationTests.cs:354-369,147-161`.
