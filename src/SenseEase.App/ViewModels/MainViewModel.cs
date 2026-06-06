using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SenseEase.App.Configuration;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Configuration;
using SenseEase.Core.Inference;
using SenseEase.Core.Safety;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using SenseEase.Infrastructure.Real;
using SenseEase.Infrastructure.Storage;
using System.IO;

namespace SenseEase.App.ViewModels;

/// <summary>
/// Dashboard view model. Wires the mock Sensing ??Inference ??Feedback loop and
/// exposes bindable state for the dashboard and overlay.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    // _mockSensing:   always available for the demo slider override.
    // _cursorSensing: Phase 2.1 Win32 GetCursorPos provider.
    // _wgcSensing:    Phase 2.2 Windows.Graphics.Capture pixel-diff provider (stub until Gemini implements).
    // _sensing:       active provider, swapped at runtime by ToggleSensingMode.
    private readonly MockSensingProvider _mockSensing = new();
    private ISensingProvider _sensing;
    private readonly CursorVelocityProvider _cursorSensing = new(AppConfig.RejectCursorDragOfi);
    private readonly WgcSensingProvider _wgcSensing = new();
    /// <summary>Pop-up sprint Task 1: pass-through for the WGC bar exclude rect.
    /// MainWindow's validation-mode startup uses this to feed the BarStub's
    /// monitor-coords rectangle into the OFI pipeline. Null = no mask (default).</summary>
    public void SetWgcMonitorExcludeRect(int? left, int? top, int? width, int? height)
        => _wgcSensing.SetMonitorExcludeRect(left, top, width, height);

    /// <summary>overlay-bar-redesign D3: pause-or-resume the WGC capture
    /// pipeline. Called by MainWindow when the Power Save toggle flips so
    /// the OFI pipeline goes quiet (frame callbacks short-circuit while
    /// paused) without tearing down the GraphicsCaptureSession.</summary>
    public void SetWgcPaused(bool paused)
    {
        if (paused) _wgcSensing.Pause();
        else        _wgcSensing.Resume();
        // D3: power-save is the highest-priority liveness signal (an intentional
        // pause must never read as a fault). Recompute on every toggle.
        RecomputeLiveness();
    }

    /// <summary>M2 (Phase 6c): host-injected lookup for Power Save state.
    /// MainWindow owns the source of truth (its private _powerSaveActive
    /// field, session-scoped per Pop-up sprint iter 2). The chatbot's
    /// [habituation_state] block reads through this callback so the VM
    /// doesn't take a direct dependency on the View layer.</summary>
    private Func<bool>? _powerSaveActiveLookup;
    public void SetPowerSaveActiveLookup(Func<bool> lookup) => _powerSaveActiveLookup = lookup;

    /// <summary>Path Z follow-up (Review Agent C #2 + Agent A judgment call
    /// #40): clear the spike detector so the first post-Resume frame doesn't
    /// register as a false spike against the artificial zero baseline that
    /// accumulated while WGC was paused. Lives on the VM because the
    /// SpikeDetector is owned here, not on the provider.</summary>
    public void ResetSpikeDetector() => _spikeDetector.Reset();

    /// <summary>Path Z UX iter 2: ensure WGC is the active provider. Public
    /// entry point for the card's Engage button — "啟動保護" should always
    /// switch to the real-measurement provider before showing the comfort
    /// overlay, so the overlay's tier/pressure read reflects real screen
    /// motion rather than cursor velocity or mock data.</summary>
    public void SetSensingModeToWgc()
    {
        if (AppConfig.Mode == SensingMode.Wgc) return;
        SetSensingMode(SensingMode.Wgc, "啟動保護");
    }

    /// <summary>Per-press FOV tightening for the card's "緩和畫面" control (AF-1).
    /// Negative = shrink the clear zone (more dimming). Accumulates toward the
    /// engine's −60 clamp, so repeated presses ease further.</summary>
    private const int EaseViewFovStep = -12;

    /// <summary>AF-1: the card's "緩和畫面" action — a deliberate user PREFERENCE to
    /// ease the view a notch. Routes through the D5 deliberate-preference path so
    /// it tightens the FOV (visible immediately) WITHOUT counting as a discomfort
    /// recurrence or eroding the earned habituation offset (Q4.5). Refreshes state
    /// so the overlay re-renders the tighter field at once.</summary>
    public void EaseView()
    {
        _engine.ApplyUserFeedback(EaseViewFovStep, isDeliberatePreference: true);
        UpdateFromState(_engine.Current);
    }

    private const int WidenViewFovStep = 12;

    /// <summary>The compact pop-up bar's "−" (ease less / widen) intervention nudge
    /// — the deliberate-preference mirror of <see cref="EaseView"/>. Loosens the FOV
    /// a notch via the D5 path (no discomfort recurrence, no habituation-offset
    /// erosion, Q4.5), then refreshes so the overlay re-renders the looser field.</summary>
    public void WidenView()
    {
        _engine.ApplyUserFeedback(WidenViewFovStep, isDeliberatePreference: true);
        UpdateFromState(_engine.Current);
    }

    /// <summary>Pop-up sprint Task 5: true iff the user's most recent
    /// calibration recorded a vestibular diagnosis (Q5 risk factor).
    /// Drives the SettingsWindow safety warning when the user tries to
    /// silence the bar/tray colour signal. Reads from the persisted
    /// habituation profile snapshot — null-safe across all the "no
    /// calibration has run" cases.</summary>
    public bool HasVestibularDiagnosis()
    {
        var snap = _habituationProfileStore.Load();
        if (snap?.CalibrationResponses?.Q5RiskFactors is null) return false;
        foreach (var rf in snap.CalibrationResponses.Q5RiskFactors)
        {
            if (rf == SenseEase.Core.Inference.RiskFactor.VestibularDiagnosed) return true;
        }
        return false;
    }
    private readonly IAdjustmentLog _adjustmentLog = new FileAdjustmentLog(AppConfig.AdjustmentLogPath);
    private readonly IHabituationStore _habituationStore = new FileHabituationStore(AppConfig.HabituationStatePath);
    // Stage 7b: pass HabituationStatePath as the wipe-probe artifact so the
    // app-data-wipe detection path (Stage 6c) is actually live in production.
    // Without the second arg, the loader's wipe synthesis never fires —
    // unit tests pass against the seam but real users with a wiped profile
    // file silently get null'd back to RESILIENT defaults instead of being
    // re-routed through the first-time calibration card. The probe file is
    // the existing habituation.json (the offset-only legacy file from
    // Phase 3); its presence-without-profile-file is the wipe signature.
    private readonly IHabituationProfileStore _habituationProfileStore =
        new FileHabituationProfileStore(
            AppConfig.HabituationProfilePath,
            AppConfig.HabituationStatePath);
    // Auto Driver/Passenger mode detector — polls Win32 GetLastInputInfo at 1 Hz and
    // raises ModeChanged when OS-level input idleness crosses the hysteresis band.
    private readonly UserActivityModeDetector _activityDetector = new();
    private readonly IInferenceEngine _engine;
    public  IInferenceEngine Engine => _engine;
    public  ChatbotViewModel ChatbotVm { get; }

    // ── M3 Phase 7d/7e/7f/7g: chatbot write-side infrastructure ────────────
    // H17: durable JSONL action log (survives restarts so the rule-5 cooldown +
    // forensic trail persist). Replaces InMemoryChatbotActionLog.
    private readonly IChatbotActionLog _chatbotActionLog = new FileChatbotActionLog(AppConfig.AuditLogDir);
    private readonly LongitudinalObservationWriter _observationWriter;
    private readonly HabituationOverrideApplicator _overrideApplicator;

    // Currently-surfaced (or pending-after-Later) chatbot proposal. Used by
    // the Processor's rule 4 (one proposal at a time) and re-surfaced on the
    // next bar click while non-null + not expired. Cleared on Accept / Decline /
    // Expired. Read by the action-log lookup MostRecentDeclineFor (Processor
    // rule 5). Mutated only on the UI dispatcher thread.
    private ProfileOverrideProposal? _activeProposal;
    private DispatcherTimer? _proposalExpiryTimer;

    /// <summary>M3 7e: raised when MainViewModel has validated a chatbot
    /// proposal via <see cref="HabituationOverrideProcessor"/> and the host
    /// (MainWindow) should surface it in the Card UI. Carries the validated
    /// proposal; the Card opens via the host's existing notification-card
    /// path.</summary>
    public event EventHandler<ProfileOverrideProposal>? ProposalSurfaceRequested;

    /// <summary>M3 7e: snapshot of the currently-pending proposal (set on
    /// validate-accept, cleared on Apply/Decline/Expire). Exposed for the
    /// host's bar-click handler to re-show the Card with the deferred
    /// proposal after a Later click. Null when no proposal is pending.</summary>
    public ProfileOverrideProposal? ActiveProposal => _activeProposal;

    /// <summary>The engine's current habituation profile as the canonical upper-case
    /// string ("RESILIENT"/"TYPICAL"/"SENSITIVE"), for the Dashboard profile-pill
    /// active-state highlight. Reads the REAL engine profile (null-safe cast), so a
    /// safety-blocked switch correctly leaves the prior pill lit. Notified from
    /// UpdateFromState.</summary>
    public string CurrentProfile =>
        (_engine as MockInferenceEngine)?.HabituationProfile.ToString().ToUpperInvariant()
        ?? "RESILIENT";

    /// <summary>M3 7e: host calls this when the user clicked Accept on the
    /// in-Card proposal. Routes through the applicator (defensive-intercept
    /// re-check + engine write), records the outcome in the action log,
    /// and clears the pending state.</summary>
    public ApplyResult AcceptActiveProposal()
    {
        if (_activeProposal is not { } p) return ApplyResult.Blocked("no active proposal");
        var result = _overrideApplicator.ApplyAccepted(p);
        if (result.IsSuccess) ClearActiveProposal();
        // BlockedBySafety path keeps the proposal active so the user can
        // Decline / Later; the Card surfaces the safety note inline.
        return result;
    }

    /// <summary>Host calls this when the user clicked Decline. Records the
    /// decline (drives Processor rule-5 cooldown) and clears pending.</summary>
    public void DeclineActiveProposal()
    {
        if (_activeProposal is not { } p) return;
        _overrideApplicator.RecordDeclined(p);
        ClearActiveProposal();
    }

    /// <summary>Host calls this when the user clicked Later. Keeps the
    /// proposal pending so the next bar click can re-surface it. The expiry
    /// timer auto-records Expired + clears pending if the user never
    /// re-engages before <c>ExpiresAtUtc</c>.</summary>
    public void DeferActiveProposal()
    {
        // Intentionally no applicator call — Later is a "hide for now".
        // _activeProposal stays set; expiry timer keeps running.
    }

    /// <summary>Phase 5 Step 7: host calls this when the Card's 15s expiry
    /// countdown fires (<c>NotificationCardWindow.ProposalExpired</c>).
    /// Mirrors the 60s VM watchdog body so Card-driven and timer-driven
    /// expiry are a single clear path. The <c>is not { } p</c> guard makes
    /// this idempotent against the dual-fire race with
    /// <see cref="StartProposalExpiryWatchdog"/> — whichever path fires
    /// first wins, the second is a no-op. Must be invoked on the UI
    /// dispatcher thread (Card events already run on UI thread).</summary>
    public void ExpireActiveProposal()
    {
        if (_activeProposal is not { } p) return;
        _overrideApplicator.RecordExpired(p);
        ClearActiveProposal();
    }

    private void ClearActiveProposal()
    {
        _activeProposal = null;
        try { _proposalExpiryTimer?.Stop(); } catch { /* ignore */ }
        _proposalExpiryTimer = null;
    }

    /// <summary>M3 7e: ChatbotVm.ProposalReceived handler. Runs the
    /// Processor's 5 validation rules; on accept, stores the proposal as
    /// active, starts the expiry watchdog, and raises
    /// <see cref="ProposalSurfaceRequested"/> so the host (MainWindow) opens
    /// the Card with the proposal section visible. On reject, the rejection
    /// is logged for telemetry and the user sees nothing (the chatbot's
    /// reply may still mention the suggestion in prose).</summary>
    private void OnChatbotProposalReceived(object? sender, ProfileOverrideProposal proposal)
    {
        var snapshot = SafeLoadSnapshot();
        var validation = HabituationOverrideProcessor.Validate(
            proposal,
            snapshot,
            isAnotherProposalPending: _activeProposal is not null,
            mostRecentDeclineFor: _chatbotActionLog.MostRecentDeclineFor);

        if (!validation.IsAccepted)
        {
            // Log the rejection for telemetry; user-facing reply already
            // landed in Messages so the chatbot's prose still surfaces.
            // The Card is not opened.
            _log.Append(new EventLogEntry
            {
                Category = "Habituation.SafetyIntercept",
                Message  = $"chatbot proposal rejected by processor: {validation.RejectionReason}",
            });
            return;
        }

        // Clamp expiry to processor's max future window. Mistral 3B reliably
        // overshoots the 48h target by hours-to-days; rather than discarding
        // an otherwise-valid proposal we cap the timer at +7 days so the
        // Card's countdown stays meaningful. The model's reply text still
        // mentions whatever date it computed — that's a cosmetic mismatch
        // we accept (and the user has the Card's authoritative countdown).
        var maxExpiry = DateTime.UtcNow.AddDays(HabituationOverrideProcessor.ExpiresAtMaxFutureDays);
        var clamped = proposal.ExpiresAtUtc > maxExpiry
            ? proposal with { ExpiresAtUtc = maxExpiry }
            : proposal;

        _activeProposal = clamped;
        _chatbotActionLog.Record(ChatbotActionKind.Proposed, clamped);
        StartProposalExpiryWatchdog(clamped);
        ProposalSurfaceRequested?.Invoke(this, clamped);
    }

    private HabituationProfileSnapshot? SafeLoadSnapshot()
    {
        try { return _habituationProfileStore.Load(); }
        catch { return null; }
    }

    /// <summary>M3 7e+: user picked a profile from the in-chat picker. Builds
    /// a SwitchProfile proposal and runs it through the standard Applicator
    /// path (same safety intercept + action-log audit as a chatbot-driven
    /// proposal). Pushes a confirmation / safety-block message back into
    /// the chat thread so the user sees the outcome inline rather than
    /// hunting in the event log.</summary>
    private void OnChatbotProfilePickerSelected(object? sender, string profileName)
    {
        string canon = (profileName ?? string.Empty).Trim().ToUpperInvariant();
        if (!Enum.TryParse<HabituationProfile>(canon, ignoreCase: true, out _))
        {
            ChatbotVm.PushAssistantBubble($"未知 profile：{profileName}。");
            return;
        }

        var proposal = new ProfileOverrideProposal(
            Intent: OverrideIntent.SwitchProfile,
            TargetField: "profile",
            ProposedValue: canon,
            Rationale: "user picked from in-chat profile picker",
            ExpiresAtUtc: DateTime.UtcNow.AddHours(48));

        _chatbotActionLog.Record(ChatbotActionKind.Proposed, proposal);
        var result = _overrideApplicator.ApplyAccepted(proposal);

        if (result.IsSuccess)
        {
            ChatbotVm.PushAssistantBubble($"已切換到 {canon} profile。");
        }
        else if (result.BlockedBySafety)
        {
            ChatbotVm.PushAssistantBubble(
                $"⚠ 無法切換到 {canon}：{result.Reason}");
        }
        else
        {
            ChatbotVm.PushAssistantBubble(
                $"切換失敗：{result.Reason ?? "unknown"}");
        }
    }

    /// <summary>Phase 5 Step 7 (v0.11, 2026-05-31): backstop for the primary
    /// Card-driven expiry path (<c>NotificationCardWindow.ProposalExpired</c>
    /// → <see cref="ExpireActiveProposal"/>). Card's 15s timer only exists
    /// while the Card window is alive; this 1-minute VM-side timer guarantees
    /// expiry clears in headless / Card-closed-early scenarios. Both paths
    /// converge on <see cref="ClearActiveProposal"/>; ExpireActiveProposal's
    /// <c>is not { } p</c> guard makes the dual-fire race a safe no-op
    /// (whichever fires first wins).</summary>
    private void StartProposalExpiryWatchdog(ProfileOverrideProposal proposal)
    {
        try { _proposalExpiryTimer?.Stop(); } catch { /* ignore */ }
        _proposalExpiryTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _proposalExpiryTimer.Tick += (_, _) =>
        {
            if (_activeProposal is not { } p) { try { _proposalExpiryTimer?.Stop(); } catch { } return; }
            if (DateTime.UtcNow >= p.ExpiresAtUtc)
            {
                _overrideApplicator.RecordExpired(p);
                ClearActiveProposal();
            }
        };
        _proposalExpiryTimer.Start();
    }
    // H17: durable JSONL event log (forensic categories persisted to a daily file,
    // 7-day retention, 1 MB/day cap). The in-memory window + Appended event behave
    // identically to InMemoryEventLogStore, so the dashboard mirror is unchanged.
    private readonly IEventLogStore _log = new FileEventLogStore(AppConfig.AuditLogDir);

    // ── M3/M4 head-tracking sprint: IHeadPoseReader registration ─────────────
    // This codebase doesn't use a Microsoft.Extensions.DependencyInjection
    // container — services are composed as `readonly` fields on the view-
    // models that need them (see `_mockSensing`, `_cursorSensing`, `_log`
    // above for the same pattern). M3 wired the DefaultHeadPoseReader stub
    // unconditionally; M4 switches the field to a flag-aware factory call
    // so SidecarHeadPoseReader is instantiated when HeadTrackingEnabled is
    // true and DefaultHeadPoseReader otherwise. The M4 invariant: with the
    // flag false (default), the field still binds to DefaultHeadPoseReader,
    // no sidecar process is spawned, and the consumer (M5) sees null on
    // every ReadLatest call → falls back to the (0.5, 0.5) center default.
    // Exposed `internal` so the M4 integration test can reach in and assert
    // the binding without widening the public surface (mirrors the
    // `InternalsVisibleTo SenseEase.Tests` plumbing already in
    // SenseEase.Infrastructure).
    private readonly IHeadPoseReader _headPoseReader = CreateHeadPoseReader();
    internal IHeadPoseReader HeadPoseReader => _headPoseReader;

    /// <summary>M5 UX-iter 3 Fix C: the reader the high-frequency head-pose
    /// timer in MainWindow pulls from. Mirrors the override-wins logic in
    /// UpdateFromState so the timer pulls from the mock-sweep reader when
    /// active (Ctrl+Shift+H deterministic pattern) and from the real
    /// SidecarHeadPoseReader otherwise.</summary>
    internal IHeadPoseReader EffectiveHeadPoseReader => _overrideReader ?? _headPoseReader;

    // ── M5: developer/QA mock-sweep override ─────────────────────────────────
    // When non-null, takes precedence over _headPoseReader inside the
    // UpdateFromState ReadLatest pull. Set by StartMockHeadPoseSweep (bound
    // to Ctrl+Shift+H at the MainWindow level) and cleared automatically
    // when the sweep completes (its ReadLatest returns null past 20 s, but
    // we also schedule a hard-clear timer for the field swap so the next
    // sweep press can start fresh). Volatile via Interlocked so the
    // OFI/cursor/demo timers can read it without locking; mutated only on
    // the UI dispatcher thread (by the hotkey handler + timer Tick).
    private IHeadPoseReader? _overrideReader;
    internal IHeadPoseReader? OverrideHeadPoseReaderForTests
    {
        get => _overrideReader;
        set => _overrideReader = value;
    }
    private DispatcherTimer? _mockSweepClearTimer;

    /// <summary>M5 developer/QA hook: install a deterministic 20-second
    /// mock head-pose sweep and surface it through the same UpdateFromState
    /// path used by the real sidecar reader. Used by the G4 UVHS demo and
    /// by anyone visually verifying the wiring without a working webcam.
    ///
    /// <para>Independent of <see cref="AppConfig.HeadTrackingEnabled"/>:
    /// the sweep WILL drive the FOV-mask center even when head-tracking is
    /// disabled overall, because its purpose is to prove the wiring on
    /// machines without webcams or with broken MediaPipe detection.</para>
    ///
    /// <para>Idempotent: a second press while a sweep is in flight is
    /// ignored (so the user can't double-trigger).</para>
    /// </summary>
    public void StartMockHeadPoseSweep()
    {
        if (_overrideReader is not null) return; // sweep already running
        _overrideReader = new MockSweepHeadPoseReader();
        _mockSweepClearTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(MockSweepHeadPoseReader.SweepDurationSeconds + 0.25),
        };
        _mockSweepClearTimer.Tick += (_, _) =>
        {
            try { _mockSweepClearTimer?.Stop(); } catch { }
            _mockSweepClearTimer = null;
            _overrideReader = null;
        };
        _mockSweepClearTimer.Start();
    }

    /// <summary>True while a mock sweep is active. Exposed for tray/diag
    /// surfaces or future UI indicators.</summary>
    public bool IsMockHeadPoseSweepActive => _overrideReader is not null;

    /// <summary>M4 factory: chooses the reader implementation based on
    /// <see cref="AppConfig.HeadTrackingEnabled"/>. Q5-locked DI pattern:
    /// no container, no service collection — just a static factory that
    /// reads the static flag and returns the appropriate instance.
    ///
    /// <para>Phase 3 fix (2026-05-30, adversarial review blocker #3): the
    /// log delegate now routes SidecarHeadPoseReader lifecycle diagnostics
    /// (spawn failure, Python missing, stderr tail on exit, parse errors,
    /// Restart errors) to %TEMP%/wgc_diag.txt — same fire-and-forget pattern
    /// MainWindow.SafeLog uses for tray/WGC diagnostics. Without this,
    /// Phase 3's PowerSave Stop/Restart failure modes were completely
    /// invisible: a venv quarantined by AV, a deleted tools/ folder, or a
    /// Python-3.13 mismatch would leave the FOV mask stuck at center after
    /// a PowerSave round-trip with NO signal anywhere to diagnose it.</para></summary>
    private static IHeadPoseReader CreateHeadPoseReader()
    {
        if (!AppConfig.HeadTrackingEnabled)
            return new DefaultHeadPoseReader();
        return new SidecarHeadPoseReader(
            sidecarExePath: AppConfig.SidecarExePath,
            log:            SidecarLifecycleLog,
            mirror:         true,
            webcamIndex:    0);
    }

    /// <summary>Phase 3 fix (2026-05-30): SidecarHeadPoseReader diagnostic
    /// sink. Appends to <c>%TEMP%/wgc_diag.txt</c> (the same diagnostic file
    /// MainWindow + TrayIconService + WgcSensingProvider write to), tagged
    /// with the <c>[SidecarHeadPose]</c> prefix. Fire-and-forget: IO
    /// failures are swallowed so a logging error can never break the sidecar
    /// lifecycle (which itself runs from a Task.Run background thread).</summary>
    private static void SidecarLifecycleLog(string msg)
    {
        // Phase 4: routed through WgcDiagLog for retry + orthogonal probe.
        SenseEase.Infrastructure.Real.WgcDiagLog.Append(
            $"[{DateTime.UtcNow:O}] [SidecarHeadPose] {msg}");
    }

    /// <summary>M4: kick the sidecar process if head-tracking is enabled.
    /// Called from MainWindow.Loaded so the spawn happens after the visual
    /// tree is ready (parity with the WGC validation / persistent bar /
    /// tray icon Loaded handlers). Flag-off invariant: returns immediately
    /// without touching the reader at all.</summary>
    public void StartHeadTrackingIfEnabled()
    {
        if (!AppConfig.HeadTrackingEnabled) return;
        if (_headPoseReader is SidecarHeadPoseReader sidecar)
        {
            try { sidecar.Start(); } catch { /* sidecar is best-effort */ }
        }
    }

    private readonly INluService _nlu;
    private readonly DispatcherTimer _demoTimer;
    private readonly DispatcherTimer _idleTimer; // keeps decay visible in Demo Mode when slider is idle
    private readonly DispatcherTimer _metricsTimer;
    private DateTime _lastDemoInputAtUtc = DateTime.UtcNow;
    private DateTime _lastRealSampleAt   = DateTime.UtcNow;
    private DateTime _lastMetricsTickUtc = DateTime.UtcNow;
    private bool _prevMaskActiveForIntercept;
    private readonly PressureSpikeDetector _spikeDetector = new();
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    private bool _lastMaskActive;
    private double _demoOfi;

    // Display Comfort (LuxController) removed at v0.11 (UI Consolidation
    // Sprint Phase 4) — feature decommissioned. See CHANGELOG.md.

    private ISensingProvider ProviderFor(SensingMode mode) => mode switch
    {
        SensingMode.Demo => _mockSensing,
        SensingMode.Wgc  => _wgcSensing,
        _                => _cursorSensing,
    };

    public MainViewModel()
    {
        // M1 (Phase 6-pre): pass _log so the engine wrapper emits habituation
        // lifecycle events (profile change, level transition, spike, engine-
        // state change) for the chatbot's Phase 6c trajectory builder.
        _engine = new MockInferenceEngine(_adjustmentLog, _habituationStore, _habituationProfileStore, _log);

        // M3 7e+ fix: mirror every _log.Append into the dashboard's bound
        // EventLog ObservableCollection so entries originating outside
        // MainViewModel (M1 habituation events from MockInferenceEngine,
        // Phase 7d/7f Applicator events) appear in the UI table. Without
        // this subscription, the Applicator's "chatbot override applied"
        // entry only landed in _log's in-memory list and never reached the
        // dashboard. UI marshal via dispatcher because the source event
        // can fire from any thread.
        _log.Appended += (_, entry) =>
        {
            _dispatcher.Invoke(() =>
            {
                EventLog.Insert(0, entry);
                while (EventLog.Count > SenseDefaults.EventLogMaxCount)
                    EventLog.RemoveAt(EventLog.Count - 1);
            });
        };

        // H17: log the session baseline at boot so a field report ("my profile
        // flipped overnight") has a durable anchor for what state the session
        // started in. State.PersistedStateLoaded is a forensic category, so
        // FileEventLogStore persists it; the subscription above mirrors it to the
        // dashboard too.
        var bootSnap = _habituationProfileStore.Load();
        if (bootSnap is not null)
        {
            _log.Append(new EventLogEntry
            {
                Category = "State.PersistedStateLoaded",
                Message  = $"profile={bootSnap.Profile} source={bootSnap.ProfileSource} " +
                           $"offset_pp={bootSnap.HabituationOffsetPp:F1} " +
                           $"calibrations={bootSnap.CalibrationCount} " +
                           $"advisory={bootSnap.MedicalAdvisoryShown?.ToString() ?? "none"}",
            });
        }
        var mistral = new MistralChatbotService(AppConfig.ModelPath, _adjustmentLog, _log);
        ChatbotVm = new ChatbotViewModel(mistral, _engine, _adjustmentLog, () => OverlayVisible, _log);

        // M3 7d/7e/7f/7g: wire chatbot write-side. Casting to the concrete
        // engine type is OK here because MainViewModel constructed it above
        // (Decision 7: chatbot Applicator targets MockInferenceEngine
        // directly, not IInferenceEngine — the chatbot-override surface
        // doesn't belong on the abstract interface).
        _observationWriter = new LongitudinalObservationWriter(_habituationProfileStore);
        _overrideApplicator = new HabituationOverrideApplicator(
            engine:       (MockInferenceEngine)_engine,
            profileStore: _habituationProfileStore,
            eventLog:     _log,
            actionLog:    _chatbotActionLog);
        ChatbotVm.LongitudinalObservationCallback = obs => _observationWriter.Append(obs);
        ChatbotVm.ProposalReceived += OnChatbotProposalReceived;
        ChatbotVm.ProfilePickerSelected += OnChatbotProfilePickerSelected;

        // M2 (Phase 6c): wire host-side lookups so ChatbotViewModel can build
        // the [habituation_state] block per turn. Power Save state comes from
        // MainWindow's session-scoped _powerSaveActive (queried via the VM's
        // _powerSaveActiveLookup callback set by MainWindow). Snapshot load
        // routes through the existing _habituationProfileStore.
        ChatbotVm.LoadHabituationSnapshot = () => _habituationProfileStore.Load();
        ChatbotVm.IsPowerSaveActive       = () => _powerSaveActiveLookup?.Invoke() ?? false;
        ChatbotVm.AdjustmentApplied += (_, _) => UpdateFromState(_engine.Current);
        // Stage 5a/5b: wire the LLM calibration session factory. The factory
        // constructs a CalibrationChatbotService and kicks StartAsync — the VM
        // just surfaces the active session for the chat tab to render.
        ChatbotVm.LlmCalibrationFactory = BuildLlmCalibrationSession;
        // v0.9.1 hotfix: route the banner's Start button (and the Recalibrate
        // entry) through the working button-card UI rather than the LLM
        // session that has no UI binding. See ChatbotViewModel.StartInvitedCalibration.
        ChatbotVm.OpenCalibrationCardCallback = TriggerCalibrationCard;
        _nlu = new ChatbotNluService(mistral);

        _sensing = ProviderFor(AppConfig.Mode);

        _demoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _demoTimer.Tick += OnDemoTick;

        // Fires every 300 ms to keep the LeakyIntegrator decaying when no real samples arrive.
        // Demo mode: push 0 (idle) or current slider value (active).
        // Real modes (Cursor/WGC): push OFI=0 so pressure decays if the provider stops unexpectedly.
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _idleTimer.Tick += (_, _) =>
        {
            if (AppConfig.UseMockSensing && !IsRunning)
            {
                bool idle = (DateTime.UtcNow - _lastDemoInputAtUtc).TotalMilliseconds >= 300;
                _mockSensing.Push(idle ? 0.0 : _opticalFlowIndex);
            }
            else if (!AppConfig.UseMockSensing)
            {
                // If no sample has arrived in the last 600 ms the real provider has stalled;
                // step the engine with OFI=0 so pressure decays rather than freezing.
                if ((DateTime.UtcNow - _lastRealSampleAt).TotalMilliseconds > 600)
                    UpdateFromState(_engine.Update(0, Mode, DateTime.UtcNow));
            }
        };
        _idleTimer.Start();

        // ── Eye-Protection Dashboard: 1 Hz metrics ticker ──────────────────
        _metricsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _metricsTimer.Tick += (_, _) =>
        {
            DateTime now = DateTime.UtcNow;
            SessionUptime = now - _sessionStartUtc;
            if (MaskActive) SessionMaskActiveDuration += now - _lastMetricsTickUtc;
            _lastMetricsTickUtc = now;

            // Cross-component status: surface OS-chrome suppression reason from
            // WindowsActivityFilter so the Eye-Protection tab can show users when
            // OFI sensing is being intentionally paused (typing, window drag, etc.).
            try
            {
                string reason = SenseEase.Infrastructure.Real.WindowsActivityFilter.Instance.SuppressionReason;
                ActivityStatus = string.IsNullOrEmpty(reason) ? "Idle" : $"Suppressed: {reason}";
            }
            catch { /* filter unavailable — leave previous status */ }

            // Auto Driver/Passenger summary. Show live idle reading when auto-detection
            // is on, "Manual" when the user has overridden or disabled the feature.
            if (AppConfig.AutoDriverPassengerModeEnabled)
            {
                int idle = _activityDetector.LastIdleMs;
                AutoModeText = $"Auto · {Mode} · idle {idle / 1000.0:F1}s";
            }
            else
            {
                AutoModeText = "Manual";
            }
        };
        _metricsTimer.Start();

        StartDemoCommand  = new RelayCommand(_ => StartDemo(),  _ => !IsRunning);
        StartSetupDemoCommand = new RelayCommand(_ => StartSetupDemo(), _ => !IsRunning);
        SkipSetupCommand  = new RelayCommand(_ =>
        {
            AppConfig.MarkFirstRunComplete();
            ShowFirstRunBanner = false;
            AppendLog("Setup", "使用者略過首次校正 / User dismissed first-run setup.", CurrentState);
        });
        StopDemoCommand   = new RelayCommand(_ => StopDemo(),   _ => IsRunning);
        ResetStateCommand = new RelayCommand(_ => ResetState());
        // Dashboard profile quick-access pills route through the SAME safety-
        // checked path as the in-chat profile picker (CommandParameter is the
        // canonical profile string "RESILIENT"/"TYPICAL"/"SENSITIVE").
        SelectProfileCommand = new RelayCommand(o => OnChatbotProfilePickerSelected(this, o as string ?? string.Empty));
        ToggleOverlayCommand = new RelayCommand(_ => OverlayVisible = !OverlayVisible);
        ToggleDemoModeCommand = new RelayCommand(_ => ToggleDemoMode());
        AnalyzeFeedbackCommand = new RelayCommand(async _ => await SubmitFeedbackAsync(),
                                                  _ => !string.IsNullOrWhiteSpace(FeedbackText));
        ToggleHabituationCommand = new RelayCommand(_ =>
        {
            _engine.SetHabituationPaused(!HabituationPaused);
            UpdateFromState(_engine.Current);
            AppendLog("Habituation",
                HabituationPaused ? "再適應已暫停 (Habituation paused)" : "再適應已恢復 (Habituation resumed)",
                CurrentState);
        });

        _sensing.OpticalFlowSampled += OnOpticalFlowSampled;
        // D3 liveness contract: subscribe once to the concrete WGC provider's
        // health event. _wgcSensing is a stable instance independent of the routed
        // _sensing mode and only fires while WGC is actually running; the event is
        // already marshalled to this (UI) thread by the provider.
        _wgcSensing.SensingHealthChanged += OnSensingHealthChanged;
        _sensing.Start();

        // Auto Driver/Passenger mode wiring. The detector polls OS-level input idleness
        // (Win32 GetLastInputInfo) at 1 Hz and pushes mode transitions through the
        // existing Mode property setter so MockInferenceEngine.Update sees the new mode
        // on the next OFI sample. The == guard avoids redundant PropertyChanged spam.
        if (AppConfig.AutoDriverPassengerModeEnabled)
        {
            _activityDetector.ModeChanged += (_, newMode) =>
                _dispatcher.Invoke(() =>
                {
                    if (Mode != newMode) Mode = newMode;
                });
            _activityDetector.Start();
        }

        CurrentState = _engine.Current;
        UpdateFromState(CurrentState);
        AppendLog("System", "SenseEase started, inference loop ready.", CurrentState);
    }

    private void ToggleDemoMode()
    {
        // Cycle is now Cursor <-> WGC only. SensingMode.Demo is no longer
        // user-cycleable: the dedicated Calibration & Demo button owns it and
        // is the only legitimate way to enter that mode at runtime. The enum
        // value stays (tests + StartSyntheticOfiRamp still reference it).
        var next = AppConfig.Mode == SensingMode.Wgc
            ? SensingMode.Cursor
            : SensingMode.Wgc;
        SetSensingMode(next, "感測模式切換");
    }

    /// <summary>Switch to the given sensing provider, unsubscribing the previous
    /// one and subscribing the new one. Idempotent: setting the current mode is
    /// a no-op. On failure, the previous provider is restored. Centralised here
    /// so both <see cref="ToggleDemoMode"/> (user-driven cycle) and the Demo
    /// video flow (which needs to force WGC and restore on exit) share the same
    /// sub/unsub logic.</summary>
    private void SetSensingMode(SensingMode target, string logPrefix)
    {
        if (AppConfig.Mode == target) return;

        ISensingProvider previous = _sensing;
        SensingMode previousMode = AppConfig.Mode;

        try
        {
            _sensing.Stop();
            _sensing.OpticalFlowSampled -= OnOpticalFlowSampled;

            AppConfig.Mode = target;
            _sensing = ProviderFor(target);

            _sensing.OpticalFlowSampled += OnOpticalFlowSampled;
            _sensing.Start();

            SensingModeLabel = ModeLabel(target);
            AppendLog("System", $"{logPrefix}: {SensingModeLabel}", CurrentState);
        }
        catch (Exception ex)
        {
            AppConfig.Mode = previousMode;
            _sensing = previous;
            _sensing.OpticalFlowSampled -= OnOpticalFlowSampled;
            _sensing.OpticalFlowSampled += OnOpticalFlowSampled;
            _sensing.Start();
            AppendLog("Error", $"感測模式切換失敗: {ex.GetType().Name}: {ex.Message}", CurrentState);
        }
    }

    private static string ModeLabel(SensingMode mode) => mode switch
    {
        SensingMode.Demo => "DEMO MODE",
        SensingMode.Wgc  => "SCREEN CAPTURE",
        _                => "CURSOR VELOCITY",
    };

    // ?? Bindable state ??????????????????????????????????????????????????????

    private string _sensingModeLabel = ModeLabel(AppConfig.Mode);
    public string SensingModeLabel
    {
        get => _sensingModeLabel;
        private set => Set(ref _sensingModeLabel, value);
    }

    // Display Comfort bindable state (CurrentLux, TargetContrast, TargetBrightness,
    // TargetSharpness, LuxComfortEnabled, DisplaySupported, AlsAvailable, MockLux,
    // LuxStatusText) removed at v0.11 (UI Consolidation Sprint Phase 4).
    // See CHANGELOG.md v0.11 "Removed".

    private double _opticalFlowIndex;
    public double OpticalFlowIndex
    {
        get => _opticalFlowIndex;
        set
        {
            if (Set(ref _opticalFlowIndex, value) && AppConfig.UseMockSensing)
            {
                _lastDemoInputAtUtc = DateTime.UtcNow;
                _mockSensing.Push(value); // only push from slider when in mock/demo mode
            }
        }
    }

    private double _pressure;
    public double Pressure { get => _pressure; private set => Set(ref _pressure, value); }

    private string _mode = "Passenger";
    public string Mode
    {
        get => _mode;
        set
        {
            if (!Set(ref _mode, value)) return;
            // Force a fresh inference tick so mode impact is immediate in overlay/state.
            if (AppConfig.UseMockSensing)
                _mockSensing.Push(_opticalFlowIndex);
        }
    }

    private int _fovRadiusPercent = SenseDefaults.OuterTransitionRadiusPercent;
    public int FovRadiusPercent { get => _fovRadiusPercent; private set => Set(ref _fovRadiusPercent, value); }

    private bool _maskActive;
    public bool MaskActive { get => _maskActive; private set => Set(ref _maskActive, value); }

    private int _fmsScore;
    public int FmsScore { get => _fmsScore; private set => Set(ref _fmsScore, value); }

    // VIMS tier (0..10). Mirrored from SenseState as a proper notifying property
    // because CurrentState is replaced wholesale by UpdateFromState and does NOT
    // raise PropertyChanged itself, so bindings like {Binding CurrentState.VimsTier}
    // would only refresh when WPF coincidentally re-evaluated them (e.g. on tab
    // switch). Bind to this property instead.
    private int _vimsTier;
    public int VimsTier { get => _vimsTier; private set => Set(ref _vimsTier, value); }

    // P1 (personalization-visible): personalized display stress %, mirrored from
    // SenseState so the Home hero can bind it. Two users at equal raw pressure but
    // different effective triggers see different values — personalization made visible.
    private int _displayStressPercent;
    public int DisplayStressPercent { get => _displayStressPercent; private set => Set(ref _displayStressPercent, value); }

    // ── D3 liveness / trust contract ─────────────────────────────────────────
    // One liveness scalar, derived in ONE place from existing signals and fanned
    // out to bar/tray/card exactly as VimsTier is. Default Steady-watching keeps
    // the bare new Application() smoke path on a healthy, no-new-visual state.
    private LivenessState _livenessState = LivenessState.SteadyWatching;
    public LivenessState LivenessState
    {
        get => _livenessState;
        private set
        {
            if (_livenessState == value) return;
            _livenessState = value;
            OnPropertyChanged(nameof(LivenessState));
            LivenessChanged?.Invoke(this, value);
        }
    }

    /// <summary>D3: fires whenever the liveness scalar transitions. MainWindow
    /// fans this out to the bar (Enter/ExitFaultedMode), tray (SetFaultIndicator),
    /// and card self-check — the single fan-out point, mirroring StateChanged.</summary>
    public event EventHandler<LivenessState>? LivenessChanged;

    // Cached sensing-health flag, updated by the WGC provider's SensingHealthChanged.
    private bool _sensingHealthy = true;

    /// <summary>D3: the single liveness derivation. Priority: a user-chosen pause
    /// is intentional and outranks everything (so a paused-then-stale pipeline
    /// reads "paused," never "faulted"); an unexpected sensing death is next;
    /// otherwise the healthy split is Protecting (mask dimming) vs Steady-watching
    /// (calm baseline). Pure + static so it unit-tests in isolation.</summary>
    internal static LivenessState DeriveLiveness(bool sensingHealthy, bool pausedByUser, bool maskActive)
    {
        if (pausedByUser)    return LivenessState.PausedByUser;
        if (!sensingHealthy) return LivenessState.Faulted;
        return maskActive ? LivenessState.Protecting : LivenessState.SteadyWatching;
    }

    /// <summary>D3: recompute + publish the liveness scalar from the current
    /// signals. Called from the three places a signal can change: every engine
    /// tick (UpdateFromState → MaskActive), the Power-Save toggle (SetWgcPaused),
    /// and the provider health event (OnSensingHealthChanged).</summary>
    private void RecomputeLiveness()
    {
        bool paused = _powerSaveActiveLookup?.Invoke() ?? false;
        LivenessState = DeriveLiveness(_sensingHealthy, paused, MaskActive);
    }

    /// <summary>D3: WGC provider health transition (edge-triggered, already
    /// marshalled to this thread by the provider). Logs the transition, then
    /// updates the cached flag and recomputes.</summary>
    private void OnSensingHealthChanged(bool healthy)
    {
        AppendLog(healthy ? "System" : "Error",
            healthy ? "感測已恢復 / Sensing restored."
                    : "感測中斷 / Sensing fault — reconnecting.",
            CurrentState);
        SetSensingHealthy(healthy);
    }

    private void SetSensingHealthy(bool healthy)
    {
        _sensingHealthy = healthy;
        RecomputeLiveness();
    }

    /// <summary>D3 test seam: drive the health transition without a live WGC
    /// pipeline (skips the event-log marshal). Internal +
    /// InternalsVisibleTo("SenseEase.Tests").</summary>
    internal void TestHook_RaiseSensingHealth(bool healthy) => SetSensingHealthy(healthy);

    // LuxSourceLabel removed at v0.11 — Display Comfort decommissioned.

    // Live OS-chrome suppression status from WindowsActivityFilter. Refreshed at
    // 1 Hz from the metrics timer. Empty filter reason → "Idle"; otherwise
    // "Suppressed: <reason>". Drives the small status pill in the Eye-Protection
    // tab so users can see when sensing is intentionally paused.
    private string _activityStatus = "Idle";
    public string ActivityStatus { get => _activityStatus; private set => Set(ref _activityStatus, value); }

    // Auto Driver/Passenger summary string for the metrics strip. Shows the
    // current detected mode plus the live idle reading when auto-detection is on,
    // or "Manual" when the user has opted out / overridden via the toggle button.
    private string _autoModeText = "Auto";
    public string AutoModeText { get => _autoModeText; private set => Set(ref _autoModeText, value); }

    private int _habituationLevel;
    public int HabituationLevel
    {
        get => _habituationLevel;
        private set => Set(ref _habituationLevel, value);
    }

    private bool _habituationPaused;
    public bool HabituationPaused
    {
        get => _habituationPaused;
        private set
        {
            if (Set(ref _habituationPaused, value))
                OnPropertyChanged(nameof(HabituationButtonText));
        }
    }

    public string HabituationButtonText => HabituationPaused ? "恢復再適應 / Resume" : "暫停再適應 / Pause";

    private bool _overlayVisible;
    public bool OverlayVisible
    {
        get => _overlayVisible;
        set { if (Set(ref _overlayVisible, value)) OverlayVisibilityChanged?.Invoke(this, value); }
    }

    // ── Always-on Overlay (issue #4) ─────────────────────────────────────────────
    // When enabled, the overlay window is forced visible and the VIMS tier is
    // floored to PersistentOverlayBaselineTier. Natural pressure-driven tier still
    // wins when it's higher — so a calm screen shows the baseline dim, and a
    // motion-driven spike still darkens the periphery further.
    // D6 (master plan M7): day-1 protection — the calm field defaults ON. Paired
    // ATOMICALLY with baseline tier 1 below (0% alpha at rest) so nothing visibly
    // dims until real motion raises the tier. NEVER flip this true without the
    // tier=1 pairing (risk R-B: a tier-4 baseline would over-dim every new user
    // ~43%). The "0.00 alpha at rest" gate is D6DayOneProtectionTests.
    private bool _persistentOverlayEnabled = true;
    public bool PersistentOverlayEnabled
    {
        get => _persistentOverlayEnabled;
        set
        {
            if (!Set(ref _persistentOverlayEnabled, value)) return;
            // Toggling on auto-opens the overlay so the feature is immediately visible.
            // Toggling off doesn't auto-hide — user keeps whatever overlay state they had.
            if (value && !OverlayVisible) OverlayVisible = true;
        }
    }

    private int _persistentOverlayBaselineTier = 1;
    /// <summary>Baseline VIMS tier (1–10) when PersistentOverlayEnabled is true.
    /// D6 day-1 default is 1 = 0% alpha at rest (nothing visibly dims; the field is
    /// "ready", not "always dark"). Natural higher tiers still override, so a real
    /// motion spike darkens the periphery. The user can raise this baseline.</summary>
    public int PersistentOverlayBaselineTier
    {
        get => _persistentOverlayBaselineTier;
        set => Set(ref _persistentOverlayBaselineTier, Math.Clamp(value, 1, 10));
    }

    /// <summary>Apply the always-on tier floor to a freshly-emitted SenseState. Returns
    /// the (possibly elevated) effective tier. Pure function; safe to call from any
    /// thread.</summary>
    public int ResolveDisplayedTier(int naturalTier) =>
        PersistentOverlayEnabled
            ? Math.Max(naturalTier, PersistentOverlayBaselineTier)
            : naturalTier;

    private string _feedbackText = string.Empty;
    public string FeedbackText
    {
        get => _feedbackText;
        set
        {
            if (Set(ref _feedbackText, value))
                ((RelayCommand)AnalyzeFeedbackCommand).RaiseCanExecuteChanged();
        }
    }

    private string _nluResponse = string.Empty;
    public string NluResponse { get => _nluResponse; private set => Set(ref _nluResponse, value); }

    public Visibility NluResponseVisible =>
        string.IsNullOrEmpty(_nluResponse) ? Visibility.Collapsed : Visibility.Visible;

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!Set(ref _isRunning, value)) return;
            ((RelayCommand)StartDemoCommand).RaiseCanExecuteChanged();
            ((RelayCommand)StopDemoCommand).RaiseCanExecuteChanged();
        }
    }

    public ObservableCollection<EventLogEntry> EventLog { get; } = new();
    public SenseState CurrentState { get; private set; }

    // ── Eye-Protection Dashboard: session metrics ─────────────────────────────
    private int _sessionFovIntercepts;
    public  int SessionFovIntercepts { get => _sessionFovIntercepts; private set => Set(ref _sessionFovIntercepts, value); }

    private double _sessionPeakOfi;
    public  double SessionPeakOfi { get => _sessionPeakOfi; private set => Set(ref _sessionPeakOfi, value); }

    private double _sessionPeakPressure;
    public  double SessionPeakPressure { get => _sessionPeakPressure; private set => Set(ref _sessionPeakPressure, value); }

    private TimeSpan _sessionMaskActiveDuration;
    public  TimeSpan SessionMaskActiveDuration { get => _sessionMaskActiveDuration; private set { if (Set(ref _sessionMaskActiveDuration, value)) OnPropertyChanged(nameof(SessionMaskActiveText)); } }
    public  string SessionMaskActiveText => FormatDuration(SessionMaskActiveDuration);

    private DateTime _sessionStartUtc = DateTime.UtcNow;
    private TimeSpan _sessionUptime;
    public  TimeSpan SessionUptime { get => _sessionUptime; private set { if (Set(ref _sessionUptime, value)) OnPropertyChanged(nameof(SessionUptimeText)); } }
    public  string SessionUptimeText => FormatDuration(SessionUptime);

    private static string FormatDuration(TimeSpan ts) =>
        ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}h{ts.Minutes:00}m"
            : ts.TotalMinutes >= 1
                ? $"{(int)ts.TotalMinutes}m{ts.Seconds:00}s"
                : $"{(int)ts.TotalSeconds}s";

    // Cumulative pressure integral — proxy for "visual load over the session".
    private double _cumulativePressureLoad;
    public  double CumulativePressureLoad { get => _cumulativePressureLoad; private set => Set(ref _cumulativePressureLoad, value); }

    // Ring-buffer history for the live chart (OFI, Pressure samples, both ~last 120 ticks).
    public RingHistory OfiHistory { get; } = new(ChartHistoryMax);
    public RingHistory PressureHistory { get; } = new(ChartHistoryMax);
    private const int ChartHistoryMax = 120;

    // O(1)-append fixed-size ring history. Indexed access yields oldest→newest order.
    public sealed class RingHistory : System.Collections.Generic.IReadOnlyList<double>,
                                        System.Collections.Specialized.INotifyCollectionChanged
    {
        private readonly double[] _buf;
        private int _head; // next write slot
        private int _count;
        public RingHistory(int capacity) { _buf = new double[capacity]; }
        public int Count => _count;
        public double this[int i] => _buf[(_head - _count + i + _buf.Length) % _buf.Length];
        public void Append(double v)
        {
            _buf[_head] = v;
            _head = (_head + 1) % _buf.Length;
            if (_count < _buf.Length) _count++;
            CollectionChanged?.Invoke(this,
                new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
                    System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
        }
        public System.Collections.Generic.IEnumerator<double> GetEnumerator()
        {
            for (int i = 0; i < _count; i++) yield return this[i];
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public event System.Collections.Specialized.NotifyCollectionChangedEventHandler? CollectionChanged;
    }

    // ?? Events / commands ??????????????????????????????????????????????????

    public event EventHandler<bool>? OverlayVisibilityChanged;
    public event EventHandler<SenseState>? StateChanged;

    /// <summary>
    /// Raised when VIMS pressure shows a sharp relative rise (≥30% within 3s).
    /// Carries the spike magnitude 0..1. UI listens to open the chatbot popup.
    /// </summary>
    public event EventHandler<double>? PressureSpikeDetected;

    public ICommand StartDemoCommand { get; }
    public ICommand StopDemoCommand { get; }
    public ICommand ResetStateCommand { get; }
    public ICommand ToggleOverlayCommand { get; }
    public ICommand ToggleDemoModeCommand { get; }
    public ICommand AnalyzeFeedbackCommand { get; }
    public ICommand ToggleHabituationCommand { get; }
    public ICommand StartSetupDemoCommand { get; }
    public ICommand SkipSetupCommand { get; }
    public ICommand SelectProfileCommand { get; }

    // Welcome banner state. Initialised from AppConfig.IsFirstRun so a brand-new
    // install shows the calibration prompt automatically; toggled false the
    // moment the user either completes or dismisses the setup wizard.
    private bool _showFirstRunBanner = AppConfig.IsFirstRun;
    public bool ShowFirstRunBanner
    {
        get => _showFirstRunBanner;
        set => Set(ref _showFirstRunBanner, value);
    }

    // Stage 5d: StartSetupDemo previously toggled an _isSetupRun flag so the
    // post-demo flow could differentiate calibration vs plain runs. Now that
    // DemoSummaryWindow is gone and the post-demo branch is purely a function
    // of AppConfig.IsFirstRun (first run → card, subsequent → banner), the
    // setup-demo command is a thin alias around StartDemo. The button is kept
    // (welcome banner + dashboard "Calibration & Demo") so external bindings
    // continue to work, but the flag-and-fallback logic is gone.
    private void StartSetupDemo() => StartDemo();

    // ?? Demo loop ??????????????????????????????????????????????????????????

    // Demo now plays a real first-person-view vection clip in its own window;
    // the existing WgcSensingProvider captures the primary monitor, so the
    // video's optical flow flows naturally through inference -> mask -> chatbot
    // -- i.e. the demo exercises the full real pipeline, not a synthetic OFI
    // ramp. If the video file is missing OR WGC fails to start, we fall back
    // to the legacy OFI auto-increment so a demo of *something* still runs.

    private Views.DemoVideoWindow? _demoVideoWindow;
    private SensingMode? _modeBeforeDemo;
    private bool?    _overlayBeforeDemo;
    private DateTime _demoStartedAt;
    private int      _demoMaskActivations;
    private double   _demoPeakPressure;
    // Seconds-since-_demoStartedAt at which _demoPeakPressure was last updated.
    // Stage 5c: closes the Stage 3 Q4 carve-out by feeding Q1-vs-sensor
    // alignment a real timestamp instead of the hard-coded zero CalibrationProcessor
    // previously had to treat as "no claim".
    private double   _demoPeakPressureAtSec;

    private void StartDemo()
    {
        if (IsRunning) return;

        string videoPath = AppConfig.DemoVideoPath;
        if (string.IsNullOrEmpty(videoPath))
        {
            AppendLog("Demo", "Demo video not found, running synthetic OFI ramp instead.", CurrentState);
            StartSyntheticOfiRamp();
            return;
        }

        // Reset run-scoped stats. We snapshot peak pressure / mask count over
        // the demo window so the summary log line reflects only this run.
        _demoStartedAt          = DateTime.Now;
        _demoMaskActivations    = 0;
        _demoPeakPressure       = 0;
        _demoPeakPressureAtSec  = 0;

        // Force WGC so the video's pixel motion actually drives the pipeline.
        // In Demo mode the sensor is MockSensingProvider (slider-fed) and
        // would ignore the on-screen video entirely.
        _modeBeforeDemo = AppConfig.Mode;
        SetSensingMode(SensingMode.Wgc, "Demo 啟動，切換感測");

        // Auto-enable the overlay so the mask is visible the moment pressure
        // crosses threshold — the whole point of the demo is to SHOW the
        // intervention happening on screen. Restore the prior state on close
        // (a user who had the overlay off pre-demo gets it off again after).
        _overlayBeforeDemo = OverlayVisible;
        if (!OverlayVisible) OverlayVisible = true;

        try
        {
            _demoVideoWindow = new Views.DemoVideoWindow(videoPath);
            _demoVideoWindow.DemoFinished += OnDemoVideoFinished;
            _demoVideoWindow.Owner = Application.Current?.MainWindow;
            _demoVideoWindow.Show();

            IsRunning = true;
            AppendLog("Demo", $"Demo started — playing {Path.GetFileName(videoPath)} via WGC sensing.", CurrentState);
        }
        catch (Exception ex)
        {
            AppendLog("Error", $"Demo 影片啟動失敗: {ex.GetType().Name}: {ex.Message}", CurrentState);
            if (_modeBeforeDemo is { } prior)
            {
                SetSensingMode(prior, "Demo 還原感測");
                _modeBeforeDemo = null;
            }
            StartSyntheticOfiRamp();
        }
    }

    private void StartSyntheticOfiRamp()
    {
        _demoOfi = 0;
        OpticalFlowIndex = 0;
        IsRunning = true;
        _demoTimer.Start();
        AppendLog("Demo", "OFI auto-increment running (synthetic fallback).", CurrentState);
    }

    private void StopDemo()
    {
        // Synthetic-ramp branch -- original behaviour preserved.
        if (_demoTimer.IsEnabled)
        {
            _demoTimer.Stop();
            IsRunning = false;
            AppendLog("Demo", $"Demo stopped, final OFI = {OpticalFlowIndex:F1}", CurrentState);
            return;
        }

        // Video branch -- closing the window triggers OnDemoVideoFinished,
        // which is where the real teardown / summary lives.
        if (_demoVideoWindow is { } w)
        {
            try { w.Close(); } catch { /* already closing */ }
        }
        else
        {
            IsRunning = false;
        }
    }

    private void OnDemoVideoFinished(object? sender, EventArgs e)
    {
        if (_demoVideoWindow is { } w)
        {
            w.DemoFinished -= OnDemoVideoFinished;
            _demoVideoWindow = null;
        }

        if (_modeBeforeDemo is { } prior)
        {
            SetSensingMode(prior, "Demo 結束，還原感測");
            _modeBeforeDemo = null;
        }

        // Restore overlay visibility to whatever the user had before Demo.
        if (_overlayBeforeDemo is { } priorOverlay)
        {
            if (OverlayVisible != priorOverlay) OverlayVisible = priorOverlay;
            _overlayBeforeDemo = null;
        }

        // Clear any spike state that accumulated while detection was suppressed.
        // Without this the first post-demo sample sees a fresh baseline and the
        // detector could fire a stale "spike" from the demo's peak pressure
        // decaying back to idle.
        _spikeDetector.Reset();

        TimeSpan dur = DateTime.Now - _demoStartedAt;
        AppendLog("Demo",
            $"Demo 結束 — 時長 {dur.TotalSeconds:F0}s，遮罩啟動 {_demoMaskActivations} 次，峰值壓力 {_demoPeakPressure:F0}。",
            CurrentState);

        // v0.9.1 diagnostic: Bug 2 reported "no calibration card / banner after demo"
        // — wrap the branching so we surface the failure point in the event log if
        // it ever throws silently again. The try/catch is no-cost on the happy path.
        try
        {
        // ── Post-demo branching ─────────────────────────────────────────────
        // First-time CALIBRATION users get the 5-card buttons UI in the chat
        // tab; subsequent runs get a banner offering an LLM dialog.
        //
        // Stage 7a: the "first-time" signal is the PERSISTED
        // habituation_profile.json's calibration_count field (surfaced via
        // MockInferenceEngine.LoadedCalibrationCount at startup), NOT
        // AppConfig.IsFirstRun. The two concepts diverged once Schema v2
        // landed:
        //   • AppConfig.IsFirstRun = "user has never used SenseEase on this
        //     machine" (drives the dashboard welcome banner).
        //   • LoadedCalibrationCount = "user has never completed calibration
        //     on this machine" (drives this branch).
        // They share most of their truth table on first install, but a user
        // who launched SenseEase once, skipped the welcome banner, then ran
        // a demo months later is "not first-run for the app" yet "first-run
        // for calibration" — and SHOULD see the button card, not the LLM
        // banner.
        //
        // Wipe-recovery synthetics arrive with CalibrationCount=0 (the
        // FileHabituationProfileStore wipe path returns defaults-only), which
        // is the intended behaviour: a recovered user is effectively starting
        // calibration over and gets the buttons UI again.
        int calibrationCount = (_engine is MockInferenceEngine mockForCount)
            ? mockForCount.LoadedCalibrationCount
            : -1;
        AppendLog("Calibration",
            $"[diag] post-demo branching: loaded_calibration_count={calibrationCount}",
            CurrentState);
        bool isFirstRunCalibration = calibrationCount <= 0;
        if (isFirstRunCalibration)
        {
            TriggerCalibrationCard();
        }
        else
        {
            // Stage 5a: invitation banner — DO NOT auto-trigger the card.
            // Snapshot the demo sensor data so the LLM session has accurate
            // current-run context if the user accepts the invitation.
            ChatbotVm.LastDemoSensorData = new CalibrationSensorData(
                PeakPressure: _demoPeakPressure,
                MaskActivations: _demoMaskActivations,
                PeakPressureAtSec: _demoPeakPressureAtSec);
            ChatbotVm.ShowCalibrationInvitationBanner = true;
            AppendLog("Calibration",
                "後續校準邀請橫幅已顯示 / Subsequent-calibration invitation banner shown.",
                CurrentState);
        }
        }
        catch (Exception ex)
        {
            AppendLog("Error",
                $"Post-demo calibration branching failed: {ex.GetType().Name}: {ex.Message}",
                CurrentState);
        }

        IsRunning = false;
    }

    // ── Stage 3e/3f: calibration card trigger + completion handlers ─────────

    /// <summary>Stash of sensor observations the calibration processor will
    /// consume on Completed/Cancelled. Captured at demo-end so the processor
    /// sees the run that prompted the card, not whatever state has accrued
    /// while the user was answering questions.</summary>
    private CalibrationSensorData? _pendingCalibrationSensorData;

    private void TriggerCalibrationCard()
    {
        // Snapshot sensor stats now so they survive even if pressure drifts
        // during the user's answer time. Stage 5c: PeakPressureAtSec is now
        // tracked inline in UpdateFromState (stamped each time _demoPeakPressure
        // rises), so Q1-vs-sensor alignment can make a real start/middle/end
        // claim instead of the prior "no claim" carve-out.
        _pendingCalibrationSensorData = new CalibrationSensorData(
            PeakPressure: _demoPeakPressure,
            MaskActivations: _demoMaskActivations,
            PeakPressureAtSec: _demoPeakPressureAtSec);

        var card = new CalibrationCardViewModel();
        card.Completed += OnCalibrationCompleted;
        card.Cancelled += OnCalibrationCancelled;

        ChatbotVm.ActiveCalibrationCard = card;
        ChatbotVm.IsCalibrationCardActive = true;

        AppendLog("Calibration", "校正卡開啟（首次校正）/ Calibration card opened (first run).", CurrentState);
    }

    private void OnCalibrationCompleted(object? sender, CalibrationResponses responses)
    {
        ApplyAndAnnounceCalibration(responses);
    }

    private void OnCalibrationCancelled(object? sender, CalibrationResponses responses)
    {
        // Delegate to the shared applicator so the engine + Messages mutation
        // rules live in one place (CalibrationApplicator). Abandoned classifies
        // to a no-op engine result with no messages posted, exactly what we
        // want for "user closed before Q3".
        ApplyAndAnnounceCalibration(responses);
    }

    /// <summary>Shared completion path used by both Complete and Partial flows.
    /// Hands off to <see cref="CalibrationApplicator.Apply"/> for the engine
    /// mutation + Messages append, then performs the MainViewModel-side
    /// follow-ups (dismiss card, mark first-run-complete, log).</summary>
    private void ApplyAndAnnounceCalibration(CalibrationResponses responses)
    {
        var sensor = _pendingCalibrationSensorData
                     ?? new CalibrationSensorData(_demoPeakPressure, _demoMaskActivations, _demoPeakPressureAtSec);

        CalibrationResult result;
        if (_engine is MockInferenceEngine mock)
        {
            result = CalibrationApplicator.Apply(responses, sensor, mock, ChatbotVm.Messages);
        }
        else
        {
            // Defensive: not the production engine flavour. Still classify so
            // we log the right thing, but skip the engine mutation.
            result = CalibrationProcessor.Process(responses, sensor);
        }

        DismissCalibrationCard();

        // Abandoned → silent dismiss, no first-run bookkeeping, no log noise.
        if (result.Completeness == CalibrationCompleteness.Abandoned)
        {
            AppendLog("Calibration",
                "校正卡已關閉（未完成 Q3，無紀錄）/ Card dismissed before Q3 — no profile change.",
                CurrentState);
            return;
        }

        AppConfig.MarkFirstRunComplete();
        ShowFirstRunBanner = false;

        bool partial = result.Completeness == CalibrationCompleteness.Partial;
        AppendLog("Calibration",
            partial
                ? $"校正部分完成 / Calibration partial — profile={result.Profile}, scale={result.PersonalPressureScale:F2}."
                : $"校正完成 / Calibration complete — profile={result.Profile}, scale={result.PersonalPressureScale:F2}.",
            CurrentState);
    }

    private void DismissCalibrationCard()
    {
        if (ChatbotVm.ActiveCalibrationCard is { } prev)
        {
            prev.Completed -= OnCalibrationCompleted;
            prev.Cancelled -= OnCalibrationCancelled;
        }
        ChatbotVm.IsCalibrationCardActive = false;
        ChatbotVm.ActiveCalibrationCard = null;
        _pendingCalibrationSensorData = null;
    }

    // ── Stage 5a/5b — LLM calibration session factory ───────────────────────

    /// <summary>Construct and start a CalibrationChatbotService session.
    /// Invoked via the ChatbotViewModel's LlmCalibrationFactory delegate so
    /// the VM stays infrastructure-free. Reads the previous-snapshot from
    /// the profile store so the system prompt's [USER_CONTEXT] block can
    /// reference longitudinal facts.</summary>
    private CalibrationChatbotService? BuildLlmCalibrationSession(CalibrationSensorData sensor)
    {
        try
        {
            var client = new LlamaServerCalibrationClient();
            var session = new CalibrationChatbotService(client, AppConfig.CalibrationHistoryPath);
            HabituationProfileSnapshot? prev = null;
            try { prev = _habituationProfileStore.Load(); } catch { /* corrupt or absent */ }
            // Fire-and-forget StartAsync — the service exposes IsActive/Completed
            // events the chat tab subscribes to. Errors surface via those.
            _ = session.StartAsync(prev, sensor);
            return session;
        }
        catch
        {
            // Construction failed (likely LlamaServerCalibrationClient threw on
            // an unrelated init issue). Return null so the VM treats it as
            // "LLM unavailable" and doesn't surface a half-constructed session.
            return null;
        }
    }

    // Stage 5d: removed ShowLegacyDemoSummary / DerivePreset /
    // ApplySetupDemoPreset / LogShaderDelta. Post-demo flow now branches into
    // either the first-time calibration card or the Stage 5a invitation
    // banner — the modal DemoSummaryWindow is gone, and with it the
    // setup-demo preset wired through Views.CalibrationPreset (also deleted).
    // Personalisation now flows exclusively through CalibrationProcessor /
    // CalibrationApplicator, so we don't need the alternate "tier preset"
    // path the legacy window used to apply.

    private void ResetState()
    {
        if (IsRunning) StopDemo();
        OpticalFlowIndex = 0;
        _engine.ResetPersonalization();
        _spikeDetector.Reset();
        UpdateFromState(_engine.Current);
        _adjustmentLog.Append(new AdjustmentRecord { Parameter = "Reset", Source = "reset" });
        NluResponse = string.Empty;
        OnPropertyChanged(nameof(NluResponseVisible));
        AppendLog("System", "State reset.", CurrentState);
    }

    private void OnDemoTick(object? sender, EventArgs e)
    {
        _demoOfi = Math.Min(_demoOfi + 3.5, 150);
        OpticalFlowIndex = _demoOfi;

        if (_demoOfi >= 150)
        {
            StopDemo();
            AppendLog("Demo", "OFI reached max 150, demo ended.", CurrentState);
        }
    }

    // ?? Inference loop ?????????????????????????????????????????????????????

    private void OnOpticalFlowSampled(object? sender, OpticalFlowSample sample)
    {
        // Sync OFI display from real provider (bypasses Push to avoid loop).
        if (!AppConfig.UseMockSensing)
        {
            _lastRealSampleAt = DateTime.UtcNow;
            _opticalFlowIndex = sample.OpticalFlowIndex;
            OnPropertyChanged(nameof(OpticalFlowIndex));
        }

        SenseState state = _engine.Update(sample.OpticalFlowIndex, Mode, sample.SampledAt);
        UpdateFromState(state);

        if (state.MaskActive != _lastMaskActive)
        {
            _lastMaskActive = state.MaskActive;
            AppendLog("Inference", state.MaskActive
                ? $"Pressure {state.Pressure:F0} exceeded threshold, FOV mask activated."
                : "Pressure returned below threshold, FOV mask released.", state);
        }
    }
    /// <summary>M5 test seam: drive the engine→state publication path
    /// without needing a real <c>OnOpticalFlowSampled</c> tick. Internal +
    /// InternalsVisibleTo("SenseEase.Tests"). Tests inject a synthetic
    /// SenseState and observe the StateChanged callback for asserting
    /// HeadGaze + HeadTrackingActive plumbing.</summary>
    internal void UpdateFromStateForTests(SenseState state) => UpdateFromState(state);

    private void UpdateFromState(SenseState state)
    {
        CurrentState = state;
        Pressure = state.Pressure;
        FovRadiusPercent = state.FovRadiusPercent;
        MaskActive = state.MaskActive;
        FmsScore = state.FmsScore;
        HabituationLevel  = state.HabituationLevel;
        HabituationPaused = state.HabituationPaused;
        VimsTier          = state.VimsTier;
        DisplayStressPercent = state.DisplayStressPercent; // P1
        OnPropertyChanged(nameof(CurrentProfile)); // profile-pill active highlight

        // D3: MaskActive just updated above — refresh the liveness scalar so
        // Protecting/Steady-watching track the mask in lock-step with the tier.
        RecomputeLiveness();

        // ── Eye-Protection session metrics ───────────────────────────────────────
        if (state.RawOpticalFlowIndex > SessionPeakOfi) SessionPeakOfi = state.RawOpticalFlowIndex;
        if (state.Pressure > SessionPeakPressure) SessionPeakPressure = state.Pressure;

        // ── Demo-run stats (only meaningful while a video demo is active) ──
        // The fields are also accumulated when the synthetic ramp is running,
        // which is harmless: OnDemoVideoFinished is the only consumer, and the
        // ramp branch routes through StopDemo() instead.
        if (_demoVideoWindow is not null)
        {
            if (state.Pressure > _demoPeakPressure)
            {
                _demoPeakPressure      = state.Pressure;
                // Stage 5c: stamp the seconds-since-demo-start at which this
                // new peak occurred. CalibrationProcessor uses this for Q1-vs-
                // sensor alignment (start/middle/end window claim).
                _demoPeakPressureAtSec = (DateTime.Now - _demoStartedAt).TotalSeconds;
            }
            if (state.MaskActive && !_prevMaskActiveForIntercept) _demoMaskActivations++;
        }

        // FOV intercept: count the rising edge of MaskActive.
        if (state.MaskActive && !_prevMaskActiveForIntercept)
            SessionFovIntercepts++;
        _prevMaskActiveForIntercept = state.MaskActive;

        // Cumulative pressure load (rough integral, capped to avoid runaway).
        CumulativePressureLoad = Math.Min(CumulativePressureLoad + (state.Pressure / 60.0), 1_000_000.0);

        // History ring-buffer — O(1) append via RingHistory.
        OfiHistory.Append(state.RawOpticalFlowIndex);
        PressureHistory.Append(state.Pressure);

        // ── M5: head-pose-driven FOV-mask centering ────────────────────────
        // Pull the latest head-pose sample (sidecar reader OR the developer
        // mock-sweep override, override wins) and write it into SenseState
        // before publishing. Flag-off + no-sample path leaves the state at
        // the (0.5, 0.5) center defaults so OverlayWindow.ApplyFovMask keeps
        // its v0.9-baseline behavior. Q3-verified: sidecar cx/cy use the
        // same MappingMode.RelativeToBoundingBox convention as the WPF
        // RadialGradientBrush.Center — no conversion needed at any layer.
        var poseReader = _overrideReader ?? _headPoseReader;
        HeadPoseSample? pose = poseReader.ReadLatest();
        if (pose is not null)
        {
            state.HeadGazeX = pose.Cx;
            state.HeadGazeY = pose.Cy;
            state.HeadTrackingActive = true;
        }
        else
        {
            state.HeadGazeX = 0.5;
            state.HeadGazeY = 0.5;
            state.HeadTrackingActive = false;
        }

        StateChanged?.Invoke(this, state);

        // Suppress spike detection (and therefore the popup AND the chatbot
        // follow-up bubble) during a demo run. The demo is intentionally a
        // vection stress test — the whole point is for the user to *experience*
        // the discomfort end-to-end before the calibration summary uses that
        // reaction to seed a preset. Interrupting them mid-clip with "Did you
        // feel a spike?" defeats the experiment AND clutters the chat thread
        // with auto-generated questions that aren't part of the user's
        // organic conversation.
        if (_demoVideoWindow is null && _spikeDetector.Push(state.Pressure, DateTime.UtcNow))
        {
            PressureSpikeDetected?.Invoke(this, _spikeDetector.LastNormalizedSpike);
        }
    }

    // ?? Feedback ???????????????????????????????????????????????????????????

    private async Task SubmitFeedbackAsync()
    {
        if (string.IsNullOrWhiteSpace(FeedbackText)) return;
        string utterance = FeedbackText.Trim();
        NluResult result = await _nlu.InterpretAsync(utterance);
        if (result.Intent == "ResetRequest")
        {
            _engine.ResetPersonalization();
            _adjustmentLog.Append(new AdjustmentRecord { Parameter = "Reset", Source = "reset", Utterance = utterance });
        }
        else
        {
            if (result.FovDelta != 0)
            {
                int oldOffset = _engine.UserFovOffset;
                // D5: see ChatbotViewModel — a non-complaint (preference) tightening is neutral.
                _engine.ApplyUserFeedback(result.FovDelta, isDeliberatePreference: result.Intent != "ReportDiscomfort");
                int newOffset = _engine.UserFovOffset;
                if (newOffset != oldOffset)
                {
                    _adjustmentLog.Append(new AdjustmentRecord
                    {
                        Parameter = "FovRadius", Delta = newOffset - oldOffset,
                        OldValue = oldOffset, NewValue = newOffset,
                        Source = "user", Utterance = utterance,
                    });
                }
            }
            if (result.MaskDepthDelta != 0 || result.GridVisibilityDelta != 0 || result.FeatherWidthDelta != 0)
            {
                var before = _engine.Current.Shader.Clone();
                _engine.ApplyShaderAdjustment(result.MaskDepthDelta, result.GridVisibilityDelta, result.FeatherWidthDelta);
                var after = _engine.Current.Shader.Clone();
                AppendAdjustment("MaskDepth",      result.MaskDepthDelta,      before.MaskDepth,      after.MaskDepth,      utterance);
                AppendAdjustment("GridVisibility", result.GridVisibilityDelta, before.GridVisibility, after.GridVisibility, utterance);
                AppendAdjustment("FeatherWidth",   result.FeatherWidthDelta,   before.FeatherWidth,   after.FeatherWidth,   utterance);
            }
        }
        UpdateFromState(_engine.Current);
        NluResponse = result.Response;
        OnPropertyChanged(nameof(NluResponseVisible));
        // review #3: the Feedback box is a second self-harm entry point — when the
        // safety floor trips, persist the redacted placeholder, never the raw text
        // (the event log is now durable on disk; mirrors the chatbot path's H2).
        string loggedUtterance = SafetyFloor.Trips(utterance, out _)
            ? SafetyFloor.RedactedPlaceholder
            : utterance;
        AppendLog("Feedback", $"\"{loggedUtterance}\" -> {result.Intent}", CurrentState);
    }

    private void AppendAdjustment(string parameter, double delta, double oldValue, double newValue, string utterance)
    {
        if (delta == 0) return;
        _adjustmentLog.Append(new AdjustmentRecord
        {
            Parameter = parameter, Delta = delta,
            OldValue = oldValue, NewValue = newValue,
            Source = "user", Utterance = utterance,
        });
    }

    // ?? Log ????????????????????????????????????????????????????????????????

    // Display Comfort wiring (WireLuxComfort + OnLuxControllerStateChanged)
    // removed at v0.11 (UI Consolidation Sprint Phase 4) — feature
    // decommissioned. See CHANGELOG.md.

    private void AppendLog(string category, string message, SenseState? state = null)
    {
        var entry = new EventLogEntry
        {
            Category    = category,
            Message     = message,
            Ofi         = state?.RawOpticalFlowIndex ?? 0,
            Pressure    = state?.Pressure ?? 0,
            FovRadius   = state?.FovRadiusPercent ?? 0,
            MaskSnapshot = state?.MaskActive == true ? "ON" : "off",
        };

        // Single source of writes — _log.Append fires the Appended event
        // which the constructor wires to update EventLog on the dispatcher.
        // Removing the direct EventLog.Insert here avoids double-insertion.
        _log.Append(entry);
    }

    // ?? INotifyPropertyChanged ?????????????????????????????????????????????

    /// <summary>Stop auxiliary services (timers, hooks) on window close.</summary>
    public void DisposeServices()
    {
        try { _activityDetector.Dispose(); } catch { }
        // M4 head-tracking sprint: gracefully stop the sidecar process if it
        // was spawned. The JobObject reaper would catch a hard exit anyway,
        // but explicit Dispose is faster + flushes the stderr tail log.
        if (_headPoseReader is IDisposable disposable)
        {
            try { disposable.Dispose(); } catch { }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name!);
        return true;
    }
}
