# LOGIC — confirmed-open (14)

Coverage: Read fully: MockInferenceEngine (1913 lines), LeakyIntegrator, HabituationEngine, PressureSpikeDetector, ShaderParams, SenseDefaults, SenseState, EventLogEntry, AdaptiveFit (StateMachine, IntensityStatsBuilder, DeterministicSetSelector, SetCurve), WgcSensingProvider, CursorVelocityProvider, WindowsActivityFilter, UserActivityModeDetector, SidecarHeadPoseReader, MockSensingProvider, AtomicJsonWriter, FileHabituationStore, FileHabituationProfileStore, FileEventLogStore, FileChatbotActionLog, FileAdjustmentLog, LongitudinalObservationWriter, DailyBehaviorAggregateWriter, AdjustmentRecord, OverlayWindow, MainViewModel (all 1682 lines), targeted MainWindow/MistralChatbotService/FeedbackLexicon greps. Verified fixed: EM1-EM7, NE4, NE10-NE16, NE20, NE25, NE30, H35/H36/H37/H38, W1W2 #2/#4/#5/#9. Not examined: ChatbotViewModel internals, calibration processor, WgcDiagLog, ChildProcessReaper, IntegrityVerifier/PathValidator, UserModelRetrospectiveAnalyzer, LlamaServerCalibrationClient, DailyBehaviorAccumulator, SetGrid deformation math, head_tracker.py.

#### LOGIC-01 [P1] AdaptiveFit set move / level snap silently un-pauses habituation and clears the top-band safety pause
- file: src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs:941 (ApplySetMove), 792 (SetLevel); src/SenseEase.Core/Inference/HabituationEngine.cs:463-477 | effort: S | carriedOver: False
- evidence: ApplySetMove and SetLevel(snap causes) call _habituation.ResetTransientState(), which sets _externallyPaused=false AND _awaitingUserConfirm=false. The selector (live, both flags ON since c1461a9) applies same-segment moves silently from Update(); nothing gates it on pause state. So a background set move resumes graded exposure without opt-in and can exit PausedAwaitingUserConfirm without AcknowledgeAndResume.
- current: Habituation defaults to paused (ctor SetPaused(true)); a silent selector move or confirmed set move flips IsPaused to false for the rest of the session and clears any tier>=8 awaiting-confirm pause.
- intended: Q4.5/locked constraints: transient reset must clear chatbot deltas only; top-band PausedAwaitingUserConfirm exits ONLY via AcknowledgeAndResume; habituation runs only after explicit user opt-in.
- recommendation: In ApplySetMove/SetLevel, capture _habituation.IsExternallyPaused and IsAwaitingUserConfirm before ResetTransientState and restore them after (or add a narrower reset that preserves pause flags). Add a test: selector move while externally paused / awaiting confirm leaves both flags intact.
- verdict: isReal=True fixed=False conf=high | Verified: ResetTransientState (HabituationEngine.cs:463-477) clears both pause flags; ApplySetMove:941 and SetLevel:792 call it unguarded; flag defaults true (AppConfig.cs:268); ctor opt-in pause at MockInferenceEngine.cs:327. Silent selector move bypasses AcknowledgeAndResume-only contract (line 44 doc). Not fixed by 3131387/0cf1bc0.

#### LOGIC-02 [P2] WgcSensingProvider.Start() not serialized — concurrent recovery paths can double-init and leak the capture session
- file: src/SenseEase.Infrastructure/Real/WgcSensingProvider.cs:435-610 (Start), 341-364, 300-334 | effort: S | carriedOver: False
- evidence: Stop() and OnTick take _lifecycleLock; Start() does not. ScheduleFaultRecovery (Task.Run), the Resume watchdog (timer thread), and UI Resume() can each run Stop();Start() concurrently. Two threads can both pass 'if (_running) return' before either sets it, double-running Steps 1-7: the second init overwrites _ctxPtr/_captureSession etc., leaking the first session's AddRef'd COM pointers and an orphaned WGC session.
- current: Rare race window leaves a leaked D3D device/capture session (stuck yellow border, native memory) and inconsistent pointer state.
- intended: All lifecycle transitions serialized, matching the EM3 design intent.
- recommendation: Wrap Start()'s body (or at least the check-and-init region) in lock(_lifecycleLock), and re-check _running inside. Optionally route the Resume watchdog and fault recovery through one coalesced re-init method.
- verdict: isReal=True fixed=False conf=high | Verified: Start() (435-610) takes no _lifecycleLock; _running set only at line 589. Fault recovery (Task.Run:346), resume watchdog (timer:307-319), and UI Resume():292 can race Stop();Start(), double-init overwriting _ctxPtr/_captureSession. Coalescing flags (_faulted/_watchdogScheduled) don't cover cross-path races.

#### LOGIC-03 [P2] Synchronous file IO (with AV-retry sleeps) inside the engine lock on the UI thread
- file: src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs:1522 (_profileStore.Update), 844 (_eventLog.ReadDurable); Storage/AtomicJsonWriter.cs:128-147 | effort: M | carriedOver: True
- evidence: Update() runs on the UI dispatcher and holds _gate. Each dirty tick calls _profileStore.Update → Load via AtomicJsonWriter.TryRead, which on a transiently locked file Thread.Sleeps 40/80/120/160 ms (≈400 ms worst) then does a fsync'd write. The selector tick additionally parses the full 7-day event-log JSONL (≤7 MB) every ~60 s under the same lock.
- current: On Kirby's Trend-Micro machine (known transient file locks) a dirty persist can freeze the UI thread ~0.5 s; the 60 s durable-stats read causes periodic jank and blocks ApplyUserFeedback callers.
- intended: Engine tick stays cheap; persistence and stats reads should not block the dispatcher or the engine lock.
- recommendation: Snapshot state under _gate, then perform profile-store Update and ReadDurable on a background worker (single-writer queue); feed selector stats back via a cached field. Carried from CodeReview-W1W2 #15 (partially mitigated by W2.0-C but the sync-IO-under-lock pattern remains).
- verdict: isReal=True fixed=False conf=high | Verified: Update() under _gate on DispatcherTimer calls MaybePersistProfileSnapshot→_profileStore.Update (1522)→TryRead with Thread.Sleep 40/80/120/160ms (AtomicJsonWriter.cs:141) + fsync write; selector ReadDurable (844) every SettlingMinSeconds=60 under same lock. AV file-lock makes the worst case real on Kirby's machine; pattern unfixed.

#### LOGIC-04 [P2] App-layer durable event-log entries persist local-time Timestamps (DateTime.Now default), skewing ReadDurable and selector stats
- file: src/SenseEase.App/ViewModels/HabituationOverrideApplicator.cs:75-135; MainViewModel.cs:392,417,437,742; Core/Models/EventLogEntry.cs:6 | effort: S | carriedOver: True
- evidence: EventLogEntry.Timestamp defaults to DateTime.Now (local). Engine writers set UtcNow explicitly, but 5 Applicator sites and 4 MainViewModel sites (Habituation.SafetyIntercept, Habituation.Profile, State.PersistedStateLoaded) omit Timestamp. These are durable categories, so event-log-*.jsonl mixes UTC and local (+8h in Taiwan) clocks.
- current: ReadDurable's 'since' filter and IntensityStatsBuilder's WindowHours/spike-rate math operate on mixed clock domains; forensic ordering across writers is wrong by hours.
- intended: All persisted timestamps UTC (LOG-PATHS invariant).
- recommendation: Change EventLogEntry.Timestamp default to DateTime.UtcNow (Time display helper can convert ToLocalTime for the dashboard), or set Timestamp=UtcNow at the 9 call sites. Carried from W1W2 #12 / NE21 — engine sites fixed, App-layer sites still open.
- verdict: isReal=True fixed=False conf=high | Verified: EventLogEntry.Timestamp defaults DateTime.Now (line 6); Applicator sites 75/91/100/116/131 and MainViewModel 392/417/437/742 omit it; categories match durable prefixes (FileEventLogStore.cs:42-43); engine writers set UtcNow. ReadDurable since-filter and IntensityStatsBuilder use Timestamp — mixed clocks real, not an AV artifact. | corr: Generic AppendLog (MainViewModel.cs:1601) also omits Timestamp but its categories (no dot suffix) are non-durable, so the 9-site count stands.

#### LOGIC-05 [P2] Any cursor movement zeroes WGC screen-motion OFI for 300 ms — continuous mouse motion disables sensing during real vection
- file: src/SenseEase.Infrastructure/Real/WindowsActivityFilter.cs:62, 280-291, 307-322; WgcSensingProvider.cs:748-749 | effort: M | carriedOver: True
- evidence: PollKeyboard stamps _lastCursorMoveTicks on ANY pointer delta (20 Hz), and ShouldSuppressOfi holds true for 300 ms after each move. WgcSensingProvider.OnTick zeroes rawOfi while suppressed, so a user who keeps the mouse moving while video/scroll content plays gets zero pressure accumulation and no mask.
- current: Screen-motion protection is silently absent whenever the cursor moves at least every 300 ms — a common pattern while browsing/scrolling, exactly when VIMS content scrolls.
- intended: Suppress only the cursor sprite's own pixel noise, not externally-imposed screen motion that coincides with mouse movement.
- recommendation: For WGC mode, exclude only the small downsample cells around the cursor (like the bar exclude-rect) or weight-attenuate rather than hard-zero during cursor-move windows; keep full suppression for the cursor-velocity provider only. This is CodeReview-Opus4.8 NE9, still unfixed.
- verdict: isReal=True fixed=False conf=medium | Mechanism verified exactly (filter lines 62/280-291/320; hard-zero at WgcSensingProvider.cs:748-749; IsCursorCaptureEnabled=false at 545 voids sprite-noise rationale). NE9 still open. Medium confidence on severity: ALPHA_HANDOVER Wave 12 triaged NE9 as comments/doc cleanup, so team may treat suppression-on-cursor-move as accepted design.

#### LOGIC-06 [P2] behavior_log.jsonl rewritten non-atomically every 60 s — crash mid-write destroys all longitudinal behavior data
- file: src/SenseEase.Infrastructure/Storage/DailyBehaviorAggregateWriter.cs:103-109 (RewriteNoLock), 52-59 | effort: S | carriedOver: False
- evidence: Once the day's row exists, the 60 s heartbeat (MainViewModel.FlushBehaviorAggregate) takes the rows[idx]=aggregate path → RewriteNoLock → File.WriteAllText (truncate-then-write) of the WHOLE file. A kill/power-loss/AV lock during the write loses every prior day's UM1/UM5 row. Also ChatbotVm reads via a fresh writer instance whose _gate is a different object, so reads aren't serialized against the rewrite.
- current: Full-history loss window every 60 seconds; violates the H16 'atomic writes for persisted state' locked constraint.
- intended: Crash-safe tmp+File.Replace rewrite; one shared instance (or per-path lock) for reader and writer.
- recommendation: Write the rebuilt JSONL to a .tmp with flush and File.Replace (reuse the AtomicJsonWriter pattern), and share the MainViewModel _behaviorWriter instance with ChatbotVm.LoadBehaviorAggregates (or key the gate by path like FileHabituationProfileStore).
- verdict: isReal=True fixed=False conf=high | Confirmed: RewriteNoLock uses File.WriteAllText (lines 103-109); 60s heartbeat (MainViewModel 667/870-873) hits rewrite path after first daily flush; ChatbotVm reads via fresh writer instance (MainViewModel 800-801), separate _gate. AtomicJsonWriter pattern exists in repo, unused here.

#### LOGIC-07 [P3] 1 MB/day audit-log cap silently drops the rest of the day (event log + chatbot actions)
- file: src/SenseEase.Infrastructure/Storage/FileEventLogStore.cs:129-140 (AuditLogFiles.Append) | effort: S | carriedOver: True
- evidence: Append returns silently once the day file reaches MaxDailyFileBytes. No truncation marker is written. FileChatbotActionLog shares the helper, so a capped day can also drop Declined records that drive the rule-5 cooldown across restarts; AdaptiveFit durable stats lose the day's tail.
- current: Forensic trail and selector stats silently end mid-day on heavy days.
- intended: Cap with an explicit marker, or rotate within the day.
- recommendation: On first cap hit per day, append one final '[truncated: daily cap reached]' line; consider a higher cap or intra-day rotation for event-log. Carried from CodeReview-W1W2 #11, still present.
- verdict: isReal=True fixed=False conf=high | Confirmed: AuditLogFiles.Append returns silently at cap (line 136), no marker. FileChatbotActionLog shares the helper and rehydrates rule-5 cooldown via LoadRecent from capped files. Cap-silence is comment-documented as best-effort, but missing marker contradicts the durable-cooldown promise. Low likelihood in practice; P3 right.

#### LOGIC-08 [P3] Resume watchdog keys off frame arrival, not loop liveness — spurious WGC re-init 2 s after every Resume on a static screen
- file: src/SenseEase.Infrastructure/Real/WgcSensingProvider.cs:300-334 (ScheduleResumeWatchdog) | effort: S | carriedOver: False
- evidence: The watchdog re-inits when _lastFrameProcessedAt < _resumedAt after 2 s. But warmup (2.0 s) suppresses frame processing, and WGC legitimately delivers no frames on an idle/static screen — the exact trap the newer D3 liveness watchdog documents and avoids by keying off _lastPollAt (lines 366-404).
- current: On a quiet desktop, every Power-Save Resume triggers an unnecessary Stop/Start at +2 s: yellow-border flicker, fresh 2 s warmup, 1-2 s sensing gap.
- intended: Re-init only when the capture loop is actually wedged.
- recommendation: Key the resume watchdog off _lastPollAt staleness (same as OnLivenessWatchdog) or simply delete it — the rearming D3 watchdog already covers post-resume wedges.
- verdict: isReal=True fixed=False conf=high | Confirmed at lines 300-334. Worse than stated: warmup (2.0s) blocks _lastFrameProcessedAt stamping (OnTick returns at 696 before 716) for exactly the watchdog's 2s window, and Start seeds the stamp before _resumedAt — so one spurious Stop/Start fires per Resume even on active screens. D3 watchdog (366-404) documents the trap.

#### LOGIC-09 [P3] Stale mask-hysteresis comment (trigger 100 / release 85) contradicts actual 85 / 72.25
- file: src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs:397 | effort: S | carriedOver: True
- evidence: Comment reads 'Hysteresis: trigger at 100, release at 85'; code uses SenseDefaults.TriggerPressure (85) and TriggerPressure*0.85 (72.25).
- current: Misleading comment on the load-bearing hysteresis block.
- intended: Comment matches the locked constants.
- recommendation: Update the comment to 'trigger at TriggerPressure (85), release at 85% of it (72.25)'. CodeReview-Opus4.8 NE1, still unfixed.
- verdict: isReal=True fixed=False conf=high | Confirmed at MockInferenceEngine.cs line 397: comment says 'trigger at 100, release at 85'; code uses SenseDefaults.TriggerPressure=85 and *0.85=72.25. Comment-only defect; still present today.

#### LOGIC-10 [P2] Chatbot/G1 shader adjustments (MaskDepth/Grid/Feather) are never restored at boot — they silently reset every launch
- file: src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs:311-322 (ctor restore), 532-592 | effort: M | carriedOver: True
- evidence: The ctor re-seeds only _userFovOffset from adjustmentLog.WeightFor("FovRadius"). MaskDepth/GridVisibility/FeatherWidth deltas are dutifully appended to adjustments.jsonl (MainViewModel.AppendAdjustment) and G1 SetShaderParam sets absolute values, but no load path replays them; SeedShaderFromProfile resets knobs to profile defaults.
- current: A user's accepted 'darker mask' / '/mask 0.5' personalization vanishes on restart — a genuine code-level 'my setting reset itself' source, distinct from the known AV file-lock cause (rule 8).
- intended: User-confirmed shader personalization survives restarts like the FOV offset does.
- recommendation: Persist the three shader knobs (e.g. in user-preferences or the profile snapshot's non-safety section) and restore them after SeedShaderFromProfile in the ctor, gated on _shaderUserModified. CodeReview-Opus4.8 NE2, still open.
- verdict: isReal=True fixed=False conf=high | Confirmed: ctor restores only WeightFor("FovRadius") (lines 311-322); WeightFor never called for MaskDepth/Grid/Feather anywhere; deltas are appended (MainViewModel 1558-1560) but never replayed; no shader-knob persistence in prefs/snapshot. Recent commits 7593d87/bdf193b wire AdaptiveFit selector, not user restore. Distinct from AV file-lock symptom.

#### LOGIC-11 [P3] MockSensingProvider stamps SampledAt with local DateTime.Now — clock-domain mix can freeze mask cooldown/tier debounce for hours after a Demo tick
- file: src/SenseEase.Infrastructure/Mock/MockSensingProvider.cs:27 | effort: S | carriedOver: False
- evidence: All real providers stamp DateTime.UtcNow; MockSensingProvider uses DateTime.Now. Engine Update uses sampledAt for _lastMaskChange and tier-debounce stamps. In UTC+8, one Demo-mode mask change stamps _lastMaskChange 8 h in the future; after switching to WGC, cooldownPassed stays false so the mask cannot re-activate until UTC catches up.
- current: Latent (Demo mode currently has no UI entry point; only ProviderFor/tests reference it), but any re-exposure of Demo mode re-arms an hours-long protection outage.
- intended: Single UTC clock domain for all engine timestamps.
- recommendation: Change SampledAt to DateTime.UtcNow (one line); optionally clamp negative (now - _lastMaskChange) in Update as defense-in-depth.
- verdict: isReal=True fixed=False conf=high | Confirmed: line 27 still DateTime.Now; real providers stamp UtcNow; engine stamps _lastMaskChange/cooldown from sampledAt (MockInferenceEngine 401/406/411). Demo unreachable (default Wgc, no Demo entry), so latent P3 is the right severity. One-line fix valid.

#### LOGIC-12 [P3] H14 residual: deleting schema_floor_v2.flag re-enables the v1 schema-downgrade null-fill
- file: src/SenseEase.Infrastructure/Storage/FileHabituationProfileStore.cs:208-215 (IsSchemaDowngrade) | effort: M | carriedOver: True
- evidence: The date-based arm was removed (per review #4/#5); the only downgrade signal is the sibling flag file. Removing schema_floor_v2.flag plus hand-editing schema_version:1 null-fills MedicalAdvisoryShown/VestibularProtectionEnabled on load.
- current: Local-tamper path can strip the vestibular advisory; acknowledged-loose in HABITUATION_ADJUSTMENT_PLAN §4.D.
- intended: Downgrade refusal robust to deletion of one unprotected sibling file.
- recommendation: Fold the floor signal into the snapshot itself (e.g. carry a min-schema watermark in user-preferences or the H32 integrity manifest) when the integrity-manifest work lands; low standalone priority. Carried (backlog H14 residual, open).
- verdict: isReal=True fixed=False conf=high | Confirmed: IsSchemaDowngrade (208-215) checks only the flag file; v1 load null-fills v2 safety fields. Plan §4.D line 194 explicitly documents 'Defense is loose'. Acknowledged carried residual; marginal risk since a tamperer could edit v3 fields directly anyway.

#### LOGIC-13 [P3] Auto-profiling L5 start level is erased by the fast-release tier snap on the first calm Update tick
- file: src/SenseEase.Infrastructure/Mock/MockInferenceEngine.cs:681-697 (ApplyAutoProfilingStartDefaults), 1855-1861 (fast-release) | effort: S | carriedOver: False
- evidence: ApplyAutoProfilingStartDefaults seeds _vimsTier=5 for fresh users. On the first Update with a calm screen, levelF≈1.0 so -gap=4 ≥ VimsTierFastReleaseGap(1.5) and the tier snaps straight to 1 within one ~66 ms tick. The documented 'fresh users start at L5「中」' state is never user-visible.
- current: L5 seed survives only in the persisted snapshot's CurrentVimsTier; the live ladder shows L1 almost immediately.
- intended: Per the Alpha-2 addendum, a fresh auto-profiling user starts at the L5 median and the selector converges from there — verify whether the seed was meant to hold until pressure data justifies a change.
- recommendation: Confirm intent with the product owner: if L5 should persist at rest, exempt the seeded tier from fast-release for the first stable window or floor it via the baseline-tier mechanism; if pressure-driven collapse is intended, update the addendum docs so the seed is described as snapshot-only.
- verdict: isReal=True fixed=False conf=medium | Code evidence accurate: L5 seed (line 689) snaps to 1 via fast-release (1855-1862) on first calm tick; no seed exemption. AutoProfilingEnabled defaults TRUE so live, not latent. Possibly by-design (tier is pressure-driven vection risk), but docs say 'L5 is the neutral default resting level' — verify-intent framing fair. | corr: Not flag-gated-off: AppConfig.AutoProfilingEnabled = true (AppConfig.cs:294), so this affects every fresh install

#### LOGIC-14 [P3] CursorVelocityProvider timer is never disposed (NE11 residual)
- file: src/SenseEase.Infrastructure/Real/CursorVelocityProvider.cs:56-100 | effort: S | carriedOver: True
- evidence: WgcSensingProvider gained Dispose() for its timers (NE11 fix), but CursorVelocityProvider still has no IDisposable; its System.Threading.Timer lives until process exit even after Stop().
- current: Negligible leak in practice (single app-lifetime instance), but the provider pair is now inconsistent and a future multi-instance use would leak.
- intended: All timer-owning providers disposable, symmetric with WgcSensingProvider.
- recommendation: Implement IDisposable (Stop + _timer.Dispose) and call it from MainViewModel.DisposeServices alongside the other teardown.
- verdict: isReal=True fixed=False conf=high | Confirmed: CursorVelocityProvider has no IDisposable; _timer (line 56) never disposed; WgcSensingProvider is IDisposable (line 37). Negligible leak claim accurate (single app-lifetime instance). | corr: WgcSensingProvider.Dispose() is itself never called from MainViewModel.DisposeServices or App exit; the fix should wire both providers into teardown, not just mirror WGC