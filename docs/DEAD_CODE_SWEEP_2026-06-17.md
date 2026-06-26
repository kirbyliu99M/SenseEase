# Dead-code / unwired-intention sweep — 2026-06-17

Deep sweep (6 parallel auditors over Core / Infrastructure / App-logic / App-XAML / cross-wiring /
markers), every candidate adversarially re-verified against the whole repo (XAML bindings, DI,
reflection, JSON serialization, test usage).

**42 candidates → 33 confirmed · 1 uncertain · 8 false-positives filtered.** Almost all LOW severity
(leftover scaffolding, not active bugs). **Caveat:** verification is not perfect — one "confirmed-dead"
item was a false positive that survived (the `MockInferenceEngine` accessor bundle: `EffectiveTrigger` /
`AutoAdaptTriggerDeltaPp` / `LoadedCalibrationCount` are test-consumed). Treat as high-confidence
*candidates*, confirm before deleting.

## ⚠️ MED — wire-or-retire: free-text NLU feedback subsystem (unwired)
A complete pipeline exists in the VM but **no XAML binds it** — the flag-on-but-inert class.
- `MainViewModel`: `AnalyzeFeedbackCommand`, `FeedbackText`, `NluResponse`/`NluResponseVisible`,
  `SubmitFeedbackAsync`, `_nlu = new ChatbotNluService(...)` (`MainViewModel.cs:1102,1176,1595,1606,1888-1936`)
- `ChatbotNluService(string modelPath)` ctor (`ChatbotNluService.cs:16`)
- **Action:** decide — surface it in the UI, or retire the whole subsystem.

## Unwired intentions (built, never connected)
- `EventLogEntry.ForegroundKey` — **known-deferred (B3.0 foundation)**; consumer is the *paused* B3.2
  per-app selector. Dormant by design; leave until B3.2 resumes.
- `NormFactorRecommender.Recommend` + `MadCalibrationReducer.Reduce` — M1 OFI-calibration runbook
  machinery; never auto-run (matches the roadmap — M1 is manual). Leave.
- Events with no subscriber: `SidecarHeadPoseReader.HealthChanged`; `UserActivityModeDetector.IdleSampled`;
  `MistralChatbotService.LoadError`; `App.LoadError` (`App.xaml.cs:55,249`).
- `MainViewModel.DisplayStressPercent` (mirrored each tick, never bound); `ToggleOverlayCommand`
  (constructed, never bound); `HabituationProposalLexicon.ProfileDescriptions`; `SevereClearMode`
  (enum + `HabituationProfileParams.SevereClearMode`); public `CalibrationProcessor.DetectQ1SignalConflict`
  / `ComputeSymptomDominancePrimary` / `BlendScaleWithPrior`.

## Dead code — safe to delete (low risk; confirm first)
- `MockInferenceEngine.PersonalPressureScale` getter — ✅ **already removed** (my own orphan from the B5
  snapshot refactor).
- 5-star comfort path (superseded by FMS slider): `MainViewModel.RecordComfortRating` →
  `ComfortRatingScale.BuildMeasuredReport` → `StarsToFms`.
- Members: `LevelChangeCause.UserPlusMinus`;
  `SetGrid.Set0GridVisibility`; `PendingSetMoveVm.Band/Shape`; `SenseState.EyeTrackingEnabled`;
  `MainViewModel.Engine` getter;
  `UserActivityModeDetector.OverrideMode`; `SidecarHeadPoseReader.ConsumeDroppedMalformedCount`;
  `CalibrationSource.Llm*` / `CalibrationPath.Llm*`.
  - **REMOVED 2026-06-27** (redteam cleanup): `HabituationProfileParams.PostSpikePauseLow/MidSeconds`
    (engine uses the `*Ms` variants) + `FeedbackLexicon.HasContextTrigger` + `ContextTriggers` (test-only) —
    deleted along with their tests.
- Dead XAML (`NotificationCardWindow.xaml`): `x:Name` `ProposalHeaderText` (:226); unused brush
  `ComfortSteadyWeak` (`SenseEaseTheme.xaml:57`, `Palette.Dark.xaml:38`).
  - **CORRECTION 2026-06-27** (redteam re-verify): `SetMoveHeaderText` (:341) + `ChatStatusScroll` (:750)
    were OVERTURNED — they are **LIVE, not dead**. `SetMoveHeaderText` is a live localized `{loc:T}` header
    binding target; `ChatStatusScroll`'s `MaxHeight` enforces the reply scroll-cap UX. Do NOT delete; stop
    re-flagging them.

## ⛔ Governance — do NOT delete without sign-off
- `CalibrationProcessor` private chain (`DeriveBaseProfile`/`EscalateOneLevel`/`ClassifyCompleteness`/
  `LookupPersonalPressureScale`/`ComputeCalibrationConfidence`/`Q1AlignsWithSensorPeak`/
  `CountActiveRiskFactors`) + `VestibularPersonalPressureScaleCap`: dead since **D-13** deleted `Process`,
  but the tombstone (`CalibrationProcessor.cs:36-48`) retains them as the `git revert 3131387` restore
  surface. **Doc inaccuracy found:** the tombstone claims these are still `<see cref>`'d, but only the
  two *public* helpers are. Decide: keep-as-restore-surface (add a banner so it stops being re-flagged)
  or trim. Backlog C4 (`ALPHA_BACKLOG.md:148`) would wire `CalibrationConfidence` to a consumer.

## Uncertain — needs a human eye (reflection/diagnostic use plausible)
- `WgcSensingProvider.CurrentExcludeRect`/`CurrentExcludeRects` getters; `MistralChatbotService.History`;
  `WgcDiagLog.Append`/`WriteHeader` (SEC-01 redaction path).

## False positives filtered (8)
Caught + dropped by verification (XAML/DI/reflection/serialization/test usage existed). Plus the one
that *survived* verification incorrectly (the `MockInferenceEngine` accessor bundle — test-consumed).
