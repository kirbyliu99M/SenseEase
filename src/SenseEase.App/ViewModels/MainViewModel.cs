using System;
using System.Collections.Generic;
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
using SenseEase.Core.Adaptation;
using System.Linq;
using System.IO;
using System.Globalization;

namespace SenseEase.App.ViewModels;

/// <summary>P5 (propose-confirm): a small VM-layer projection of the engine's pending
/// cross-segment set-move proposal. SEPARATE from the chatbot <c>ProfileOverrideProposal</c>
/// channel — this is the AdaptiveFit selector's own queued suggestion, surfaced ONLY behind
/// a user-initiated card-open. <c>TargetLevel</c> drives the canned non-medical card copy.</summary>
public readonly record struct PendingSetMoveVm(int Band, int Shape, int TargetLevel, DateTime ExpiresAtUtc);

/// <summary>D-redesign: a small read-only roll-up of the recent MEASURED FMS self-reports for the
/// in-app comfort-trend glance. <c>Count</c> = 0 means "no data in the window" (the <c>default</c>
/// sentinel). <c>OnVsOffDelta</c> is the within-subject protection ON-minus-OFF mean discomfort
/// (negative ⇒ lower discomfort with protection ON); null when one of the two states has no
/// samples yet. A PERSONAL trend, never a population promise (the honest D-18 replacement for "70%").</summary>
internal readonly record struct ComfortTrendSummary(int Count, double MeanFms, double? OnVsOffDelta);

/// <summary>D-redesign Phase-2: one recent MEASURED FMS check-in for the Eye-Care "my check-ins"
/// list. Raw fields only — the code-behind localizes the level label and formats the time.</summary>
internal readonly record struct CheckInListEntry(DateTime WhenUtc, int Fms, bool ProtectionOn, string? ForegroundKey);

/// <summary>
/// Dashboard view model. Wires the mock Sensing ??Inference ??Feedback loop and
/// exposes bindable state for the dashboard and overlay.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    // D-9 (2026-06-11): the Demo (MockSensingProvider) and Cursor (CursorVelocityProvider)
    // providers were retired (FEAT-11/DEAD-03). WGC is the sole sensing source.
    // _wgcSensing: Phase 2.2 Windows.Graphics.Capture pixel-diff provider.
    // _sensing:    the active provider — always _wgcSensing now (kept as ISensingProvider
    //              so the Start/Stop/OpticalFlowSampled wiring stays provider-agnostic).
    private readonly WgcSensingProvider _wgcSensing = new();
    private ISensingProvider _sensing;
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
        SetSensingMode(SensingMode.Wgc, Loc("啟動保護", "Protection started"));
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
        if (snap is null) return false;
        // Alpha-2 addendum: the auto-profiling world records vestibular via the persistent
        // VestibularProtectionEnabled field (the one-time first-use prompt / Settings toggle);
        // the legacy calibration world records it via Q5RiskFactors. Honour BOTH so every
        // vestibular UI guard keeps firing regardless of which world set it.
        if (snap.VestibularProtectionEnabled) return true;
        if (snap.CalibrationResponses?.Q5RiskFactors is { } rfs)
        {
            foreach (var rf in rfs)
                if (rf == SenseEase.Core.Inference.RiskFactor.VestibularDiagnosed) return true;
        }
        return false;
    }

    // ── Alpha-2 addendum: auto-profiling visibility + one-time vestibular prompt ──

    /// <summary>True iff the auto-profiling world is active (explicit calibration
    /// cancelled). Drives the console's calibration/reset entry visibility. While false
    /// (the default) the console keeps today's calibration + reset entries.</summary>
    public bool AutoProfilingActive => AppConfig.AutoProfilingEnabled;

    /// <summary>Console Reset button visibility — hidden when auto-profiling moves reset
    /// into Settings.</summary>
    public System.Windows.Visibility ConsoleResetVisibility =>
        AutoProfilingActive ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    // ── UX-13: one-time first-run welcome strip ──────────────────────────────
    // Keyed off the EXISTING AppConfig.IsFirstRun flag (its designed-but-dead use):
    // a fresh user — flag-file absent — sees a plain-language "Am I protected?" strip
    // BEFORE the medical-adjacent vestibular prompt. Dismiss calls MarkFirstRunComplete
    // (writes the flag file) so it never shows again. The flag is read once at
    // construction so the strip's visibility is stable across the session even though
    // MarkFirstRunComplete writes to disk synchronously on dismiss.
    private bool _firstRunStripVisible = AppConfig.IsFirstRun;

    /// <summary>True while the one-time first-run welcome strip should show (fresh install,
    /// not yet dismissed). Exposed for tests; the XAML binds <see cref="FirstRunStripVisibility"/>.</summary>
    public bool IsFirstRunStripVisible => _firstRunStripVisible;

    /// <summary>Visibility of the UX-13 first-run welcome strip.</summary>
    public System.Windows.Visibility FirstRunStripVisibility =>
        _firstRunStripVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public ICommand DismissFirstRunCommand { get; private set; } = null!;

    /// <summary>UX-13: dismiss the first-run strip. Persists the AppConfig.IsFirstRun flag
    /// (MarkFirstRunComplete) so it never reappears, hides the strip, and unblocks the
    /// vestibular prompt bar (its visibility ANDs in "first-run dismissed"), so the
    /// vestibular question only surfaces AFTER the framing strip.</summary>
    private void DismissFirstRun()
    {
        if (!_firstRunStripVisible) return; // idempotent
        AppConfig.MarkFirstRunComplete();
        _firstRunStripVisible = false;
        OnPropertyChanged(nameof(IsFirstRunStripVisible));
        OnPropertyChanged(nameof(FirstRunStripVisibility));
        // The vestibular bar was held back behind the strip — re-evaluate it now so it
        // appears (if still un-answered) the instant the strip is dismissed.
        OnPropertyChanged(nameof(VestibularPromptVisibility));
    }

    // One-time vestibular prompt (auto-profiling world). Non-blocking bottom bar shown on
    // first use until the user gives ANY response. "Asked once, not flashed-and-counted":
    // VestibularPromptAnswered is set ONLY by the Yes/No commands (an actual click), so
    // mere display never consumes the ask — closing the app without interacting reshows it.
    private bool _vestibularPromptVisible =
        AppConfig.AutoProfilingEnabled && !UserPreferences.Current.VestibularPromptAnswered;

    /// <summary>Visibility of the one-time vestibular first-use prompt bar. UX-13: held back
    /// (Collapsed) while the first-run welcome strip is still showing, so a fresh user sees
    /// the plain-language framing strip FIRST and only meets the medical-adjacent vestibular
    /// question after dismissing it.</summary>
    public System.Windows.Visibility VestibularPromptVisibility =>
        (_vestibularPromptVisible && !_firstRunStripVisible)
            ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public ICommand VestibularPromptYesCommand { get; private set; } = null!;
    public ICommand VestibularPromptNoCommand  { get; private set; } = null!;

    private void AnswerVestibularPrompt(bool hasDiagnosis)
    {
        UserPreferences.Current.VestibularPromptAnswered = true;
        UserPreferences.Current.Save();
        if (hasDiagnosis && _engine is MockInferenceEngine mock)
            mock.SetVestibularProtection(true, source: "onetime_prompt");
        _vestibularPromptVisible = false;
        OnPropertyChanged(nameof(VestibularPromptVisibility));
        OnPropertyChanged(nameof(VestibularProtectionEnabled));
        AppendLog("Vestibular", hasDiagnosis
            ? Loc("已啟用前庭保護。", "Vestibular protection enabled.")
            : Loc("前庭詢問已回覆（未啟用）。", "Vestibular prompt answered (not enabled)."),
            CurrentState);
    }

    /// <summary>Settings "regret" toggle source of truth — the PERSISTENT vestibular
    /// protection. Permanently changeable, so a user who skipped the one-time prompt can
    /// enable later (and a user who enabled can disable). Writes through the engine, which
    /// applies/reverts the protection + persists it. Q4.5-safe (never touches _offset).</summary>
    public bool VestibularProtectionEnabled
    {
        get => (_engine as MockInferenceEngine)?.IsVestibularProtectionEnabled ?? false;
        set
        {
            if (_engine is MockInferenceEngine mock && mock.IsVestibularProtectionEnabled != value)
            {
                mock.SetVestibularProtection(value, source: "settings");
                OnPropertyChanged(nameof(VestibularProtectionEnabled));
                AppendLog("Vestibular",
                    value ? Loc("已於設定啟用前庭保護。", "Vestibular protection enabled in Settings.")
                          : Loc("已於設定關閉前庭保護。", "Vestibular protection disabled in Settings."),
                    CurrentState);
            }
        }
    }

    /// <summary>D-1 (CW-1 step 3, 2026-06-11): Settings 漸進適應 / Graded exposure
    /// toggle source of truth. Graded exposure (habituation) defaults PAUSED; this
    /// is the SOLE user-intent path that flips the engine pause (the old dashboard
    /// toggle was removed 2026-06-09). ON = resume the program, OFF = pause it.
    /// Reads through the live engine state (paused → disabled); the setter calls the
    /// engine path immediately AND persists <see cref="UserPreferences.HabituationEnabled"/>
    /// so the choice survives restart (re-applied at construction before the first
    /// Update tick). Selector set-moves preserve the pause; nothing here auto-unpauses.
    /// Q4.5-safe — SetHabituationPaused never touches the earned _offset.</summary>
    public bool GradedExposureEnabled
    {
        get => !HabituationPaused;
        set
        {
            if (GradedExposureEnabled == value) return; // already at the requested state
            _engine.SetHabituationPaused(!value);
            UpdateFromState(_engine.Current);
            UserPreferences.Current.HabituationEnabled = value;
            UserPreferences.Current.Save();
            OnPropertyChanged(nameof(GradedExposureEnabled));
            AppendLog("Habituation",
                value ? Loc("已於設定啟用漸進適應。", "Graded exposure enabled in Settings.")
                      : Loc("已於設定暫停漸進適應。", "Graded exposure paused in Settings."),
                CurrentState);
        }
    }
    private readonly IAdjustmentLog _adjustmentLog = new FileAdjustmentLog(AppConfig.AdjustmentLogPath);
    // B2: nominal FOV-equivalent (pp) charged to the habituation caution per manual level nudge —
    // ~one tier's FOV step (15-100 range / 12 tiers). Logged under the "LevelNudge" parameter.
    private const int ManualNudgeFovEquivalentPp = 8;
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

    // ── P5 (propose-confirm): AdaptiveFit pending set-move channel ────────────
    // A read-through mirror of the engine's queued cross-segment proposal. Kept
    // entirely SEPARATE from _activeProposal (the chatbot channel) — distinct
    // lifecycle, distinct card panel. The host (MainWindow) reads this ONLY behind
    // a userInitiated card-open and routes Accept/Decline/Defer back through here.

    /// <summary>P5: the engine's pending cross-segment set-move proposal projected to a
    /// VM record, or null when none is queued. Pure read-through (no mutation); the
    /// host filters by wall-clock at display time. Null-safe over the engine cast —
    /// a non-Mock engine simply reports no pending move.</summary>
    public PendingSetMoveVm? PendingSetMove =>
        (_engine as MockInferenceEngine)?.PeekPendingSetMove() is { } p
            ? new PendingSetMoveVm(p.Band, p.Shape, p.TargetLevel, p.ExpiresAtUtc)
            : null;

    /// <summary>P5: host calls this when the user clicked Accept on the in-card
    /// set-move proposal. Confirms the move in the engine (offset-preserving) and
    /// refreshes VM state so the overlay/dashboard reflect the new set + level.</summary>
    public void AcceptPendingSetMove()
    {
        (_engine as MockInferenceEngine)?.ConfirmPendingSetMove();
        UpdateFromState(_engine.Current);
    }

    /// <summary>P5: host calls this when the user clicked Decline. Backs the selector
    /// off (cooldown ×2) and clears the pending suggestion in the engine.</summary>
    public void DeclinePendingSetMove()
    {
        (_engine as MockInferenceEngine)?.DeclinePendingSetMove();
    }

    /// <summary>P5: host calls this when the user clicked Later / the card's 15s in-card
    /// expiry fired. No-op against the engine state — the pending move is left queued
    /// (neutral) so the next user-open can re-surface it; it decays on its own 24h TTL
    /// if the user never engages.</summary>
    public void DeferPendingSetMove()
    {
        // Intentionally no engine call — Defer is a "leave it for next time".
    }

    /// <summary>P5: host calls this when the card's in-card expiry fires with no user
    /// action. Clears the pending suggestion in the engine WITHOUT a back-off (neutral
    /// expiry, not a decline).</summary>
    public void ExpirePendingSetMove()
    {
        (_engine as MockInferenceEngine)?.ClearPendingSetMove();
    }

    /// <summary>P2 (LOCKED, Wallpaper-Engine-style Pause/Resume mode shift):
    /// on RESUME, re-screen the proposal that was surfaced/pending across the
    /// pause boundary so nothing auto-applies as if the pause never happened.
    /// Called by MainWindow.ApplyPowerSave(false) AFTER the existing resume
    /// steps and after ChatbotVm.IsPaused is cleared.
    ///
    /// <para>Behaviour: if <see cref="ActiveProposal"/> is null or already
    /// expired → no-op. Otherwise load a FRESH snapshot and run the same
    /// defensive intercepts <see cref="HabituationOverrideApplicator.ApplyAccepted"/>
    /// uses (<see cref="HabituationOverrideApplicator.CheckSafetyIntercepts"/>).
    /// Blocked → record an Expired action + log a safety-intercept event +
    /// clear the proposal (the user must re-ask). Not blocked → leave it
    /// surfaced/pending for the user to re-Accept by hand. NEVER calls
    /// ApplyAccepted — Resume is a control toggle, not a user acceptance.</para>
    ///
    /// <para>Must be invoked on the UI dispatcher thread (mutates the
    /// proposal lifecycle, like every other Accept/Decline/Expire path).</para></summary>
    public void RevalidateActiveProposalAfterResume()
    {
        if (_activeProposal is not { } proposal) return;
        if (DateTime.UtcNow >= proposal.ExpiresAtUtc) return; // expired → no-op (watchdog clears it)

        var snapshot = SafeLoadSnapshot();
        var intercept = HabituationOverrideApplicator.CheckSafetyIntercepts(proposal, snapshot);
        if (!intercept.Blocked) return; // safe across the boundary — leave it surfaced for manual re-Accept

        // Blocked across the pause boundary: never carry a now-unsafe proposal
        // forward. Record the lifecycle outcome (mirrors ExpireActiveProposal so
        // the Processor's one-at-a-time / cooldown bookkeeping stays consistent),
        // log a forensic safety-intercept entry, then clear so the next chatbot
        // turn starts clean.
        _overrideApplicator.RecordExpired(proposal);
        _log.Append(new EventLogEntry
        {
            Category = "Habituation.SafetyIntercept",
            Message  = $"active proposal cleared on resume: {intercept.Reason ?? "blocked by safety re-screen"}",
        });
        ClearActiveProposal();
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
        // P2 (LOCKED, Pause/Resume mode shift): while paused the chatbot's
        // SendCommand is gated off (ChatbotVm.IsPaused), but a proposal already
        // in flight from a turn that started just before the pause could still
        // arrive here. Short-circuit it so nothing surfaces mid-pause. The
        // pause flag is MainWindow's session-scoped _powerSaveActive, read via
        // the same host-injected lookup the [habituation_state] block uses.
        if (_powerSaveActiveLookup?.Invoke() == true)
        {
            _log.Append(new EventLogEntry
            {
                Category = "Habituation.SafetyIntercept",
                Message  = "chatbot proposal dropped: arrived while paused",
            });
            return;
        }

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

    /// <summary>USER dashboard profile pills (RESILIENT / TYPICAL / SENSITIVE).
    /// The user's own pick drives the engine directly via the calibration-grade
    /// <see cref="MockInferenceEngine.SetHabituationProfile"/> setter. (The chatbot
    /// can no longer live-switch the profile — Reading A severed that path — so the
    /// dashboard pills are the only profile-change entry point. The old in-chat
    /// picker + its OnChatbotProfilePickerSelected handler were removed with the
    /// LLM-calibration-subsystem cleanup.)</summary>
    private void OnUserProfileSelected(string profileName)
    {
        string canon = (profileName ?? string.Empty).Trim().ToUpperInvariant();
        if (!Enum.TryParse<HabituationProfile>(canon, ignoreCase: true, out var profile))
            return; // unknown profile string — ignore the click

        if (_engine is MockInferenceEngine mock)
        {
            mock.SetHabituationProfile(profile, source: "user_dashboard");
            // CurrentProfile reads the live engine profile; raise it here so the
            // active pill repaints immediately even when sensing is stopped (it is
            // also notified from UpdateFromState during live sensing).
            OnPropertyChanged(nameof(CurrentProfile));
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

    // Beta-1 Phase-1 (option C): the symptom/comfort recorder — writes the structured
    // SymptomEvent to symptom_log.jsonl (system-of-record) AND mirrors a Symptom.* line into
    // _log (dashboard + durable forensic log). Lazy so it composes the already-initialised _log
    // without a constructor dependency. The 5★/FMS console widget (D-18/19) and the daily NL→FMS
    // estimator (D-21) record THROUGH this; the selector's FMS input (D-19) reads the structured
    // store. This is what turns the new log into richer data for sounder personalization.
    private SymptomEventRecorder? _symptomRecorderLazy;
    internal SymptomEventRecorder SymptomRecorder =>
        _symptomRecorderLazy ??= new SymptomEventRecorder(
            new FileSymptomEventStore(AppConfig.SymptomLogPath), _log);

    /// <summary>Beta-1 D-18/D-19: record a MEASURED comfort rating (the main-console 5★ widget)
    /// as a patient-reported FMS data-point. Stamps the LIVE capture context — the engine
    /// <see cref="VimsTier"/>, the protection ON/OFF state (<see cref="MaskActive"/>), and the
    /// coarse on-device foreground key (null unless per-app consent is on, D-16) — so the symptom
    /// log can later compute the within-subject ON-vs-OFF comfort delta (the honest replacement
    /// for the retired "70%" claim) and the Theme-2 per-app join. The event-assembly lives in the
    /// pure <see cref="ComfortRatingScale"/> so it is unit-tested without the VM. Best-effort:
    /// a rating must never crash the console.</summary>
    internal void RecordComfortRating(int stars, string trigger = "")
    {
        try
        {
            var ev = ComfortRatingScale.BuildMeasuredReport(
                stars, VimsTier, MaskActive,
                WindowsActivityFilter.Instance.CurrentForegroundKey, trigger);
            SymptomRecorder.Record(ev);
        }
        catch { /* a comfort rating must never propagate into the UI click handler */ }
    }

    /// <summary>D-redesign: record a MEASURED 0-20 FMS self-report from the console capture (the
    /// 0-20 slider / one-tap band) as a patient-reported data-point. Same live-context stamping
    /// (tier + protection ON/OFF + coarse foreground key) and same best-effort contract as
    /// <see cref="RecordComfortRating"/>; <paramref name="fms"/> is 0-20 (range-validated in
    /// <see cref="ComfortRatingScale.BuildMeasuredFmsReport"/>). A rating must never crash the
    /// console — a bad value/throw is swallowed here exactly like the 5★ path.</summary>
    internal void RecordFmsRating(int fms, string trigger = "")
    {
        try
        {
            var ev = ComfortRatingScale.BuildMeasuredFmsReport(
                fms, VimsTier, MaskActive,
                WindowsActivityFilter.Instance.CurrentForegroundKey, trigger);
            SymptomRecorder.Record(ev);
        }
        catch { /* a comfort rating must never propagate into the UI click handler */ }
        PushFmsContextToEngine();   // B1: reflect the just-logged report in the selector immediately
    }

    /// <summary>B1: push the recent measured-FMS aggregate (mean + count over the selector's 7-day
    /// durable window) into the engine so the Adaptive Fit selector can use it as the OUTCOME term.
    /// The symptom store lives in the App data root, so the App computes it and hands it to the engine
    /// (the engine cannot reach it). Best-effort; called on the 60 s heartbeat + after each rating.
    /// No data ⇒ (0,0) ⇒ no FMS bias.</summary>
    private void PushFmsContextToEngine()
    {
        try
        {
            var s = GetRecentComfortSummary();   // default 7-day window — matches the selector's durable window
            (_engine as MockInferenceEngine)?.UpdateRecentFmsContext(s.MeanFms, s.Count);
        }
        catch { /* advisory signal — never disturb the VM */ }
    }

    /// <summary>B3 (per-app learning): push the coarse, consent-gated foreground key into the engine
    /// so durable events get stamped with the app they occurred under (the bucket for per-app stats).
    /// <c>WindowsActivityFilter.CurrentForegroundKey</c> is already D-16-gated — it returns null unless
    /// per-app consent is on — so this carries NO new privacy surface (it is the same coarse
    /// "category/app" key already stamped on FMS reports). Called on the 1 Hz metrics tick so the key
    /// is fresh before any spike/state-change durable event fires. Best-effort; null ⇒ global bucket.</summary>
    private void PushForegroundContextToEngine()
    {
        try
        {
            string? key = WindowsActivityFilter.Instance.CurrentForegroundKey;
            (_engine as MockInferenceEngine)?.NotifyForegroundAppCategory(key);
        }
        catch { /* advisory signal — never disturb the VM */ }
    }

    /// <summary>D-redesign: a best-effort read-only roll-up of the recent MEASURED FMS self-reports
    /// (last <paramref name="days"/> days, default 7) for the in-app comfort-trend glance — the
    /// count, the mean discomfort, and the within-subject protection ON-vs-OFF mean delta. Reads
    /// symptom_log.jsonl off the same path the recorder writes; filters to measured FMS rows only,
    /// so a conversation-derived estimate (D-21) never contaminates the user-facing PERSONAL trend.
    /// Never throws — any read/parse failure yields the empty <see cref="ComfortTrendSummary"/>.</summary>
    internal ComfortTrendSummary GetRecentComfortSummary(int days = 7)
    {
        try
        {
            var recent = new FileSymptomEventStore(AppConfig.SymptomLogPath).ReadRecent(256);
            DateTime cutoff = DateTime.UtcNow.AddDays(-Math.Abs(days));

            int count = 0; long sum = 0;
            int onCount = 0; long onSum = 0;
            int offCount = 0; long offSum = 0;

            foreach (var e in recent)
            {
                if (!e.Measured || e.Scale != SymptomEvent.Scales.Fms20) continue;
                if (!DateTimeOffset.TryParse(e.TsUtc, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out DateTimeOffset dto)) continue;
                if (dto.UtcDateTime < cutoff) continue;

                count++; sum += e.Value;
                if (e.InterventionState == SymptomEvent.InterventionStates.On)       { onCount++;  onSum  += e.Value; }
                else if (e.InterventionState == SymptomEvent.InterventionStates.Off) { offCount++; offSum += e.Value; }
            }

            if (count == 0) return default;
            double mean = (double)sum / count;
            double? delta = (onCount > 0 && offCount > 0)
                ? (double)onSum / onCount - (double)offSum / offCount
                : (double?)null;
            return new ComfortTrendSummary(count, mean, delta);
        }
        catch { return default; }
    }

    /// <summary>D-redesign Phase-2: the most-recent MEASURED FMS check-ins (newest-first, up to
    /// <paramref name="count"/>) for the Eye-Care "my check-ins" list. Best-effort read of
    /// symptom_log.jsonl; never throws (returns empty on any failure).</summary>
    internal IReadOnlyList<CheckInListEntry> GetRecentCheckIns(int count = 8)
    {
        var list = new List<CheckInListEntry>();
        try
        {
            if (count <= 0) return list;
            var recent = new FileSymptomEventStore(AppConfig.SymptomLogPath).ReadRecent(256);
            // recent is oldest-first; walk backwards to take the newest `count` measured FMS rows.
            for (int i = recent.Count - 1; i >= 0 && list.Count < count; i--)
            {
                var e = recent[i];
                if (!e.Measured || e.Scale != SymptomEvent.Scales.Fms20) continue;
                DateTime when = DateTimeOffset.TryParse(e.TsUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out DateTimeOffset dto) ? dto.UtcDateTime : DateTime.UtcNow;
                list.Add(new CheckInListEntry(
                    when, e.Value,
                    e.InterventionState == SymptomEvent.InterventionStates.On,
                    e.ForegroundKey));
            }
        }
        catch { /* best-effort */ }
        return list;
    }

    // ── M3/M4 head-tracking sprint: IHeadPoseReader registration ─────────────
    // This codebase doesn't use a Microsoft.Extensions.DependencyInjection
    // container — services are composed as `readonly` fields on the view-
    // models that need them (see `_wgcSensing`, `_log` above for the same
    // pattern). M3 wired the DefaultHeadPoseReader stub
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
    // OFI/idle timers can read it without locking; mutated only on the UI
    // dispatcher thread (by the hotkey handler + timer Tick).
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

    // CW-11/EXIT-04: the single owned MistralChatbotService instance. ChatbotVm holds
    // it as IChatbotService and ChatbotNluService wraps it with _ownsChatbot=false, so
    // NEITHER disposes it — the only place that reaps the ~2 GB llama-server child
    // (beyond the JobObject backstop) is DisposeServices, which disposes THIS reference
    // exactly once on the app-exit path.
    private readonly MistralChatbotService _mistral;

    private readonly DispatcherTimer _idleTimer; // steps the engine with OFI=0 so pressure decays if WGC stalls
    private readonly DispatcherTimer _metricsTimer;
    private DateTime _lastRealSampleAt   = DateTime.UtcNow;
    private DateTime _lastMetricsTickUtc = DateTime.UtcNow;
    private bool _prevMaskActiveForIntercept;
    private readonly PressureSpikeDetector _spikeDetector = new();
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    // ── UM1: daily behaviour telemetry collector ──────────────────────────────
    // Folds each organic engine tick into an in-memory single-day aggregate
    // (numeric only — no PII) and persists it idempotently to behavior_log.jsonl,
    // which feeds UM5's [behavior_trend] block. Demo runs (video + synthetic ramp)
    // are deliberate vection stress tests and are excluded (see SampleBehavior's
    // IsRunning gate) so the longitudinal trend reflects only real use. Q4.5-safe:
    // reads the engine offset via HabituationOffsetPp, never writes engine state.
    private readonly DailyBehaviorAccumulator _behaviorAccumulator = new();
    private readonly DailyBehaviorAggregateWriter _behaviorWriter = new(AppConfig.BehaviorLogPath);

    // B5: the deterministic learning-loop coordinator (WPF-free; gated on consent inside RunOnce).
    private readonly AdaptationCoordinator _adaptationCoordinator = new();
    private const double AdaptiveAutoTuneWindowDays = 7.0;
    private const string AutoAdaptParameter = "AutoAdaptTrigger";
    private int _behaviorFlushTickCounter;
    private const int BehaviorFlushPeriodSeconds = 60; // crash-durability cadence

    // Beta-1 D-21: the daily NL→FMS estimator — derives a per-day FMS ESTIMATE from the
    // conversation (lexicon severity, on-device, labelled estimated) and records it through the
    // option-C recorder. Lazy so it composes the live _log via SymptomRecorder. Run once at
    // clean exit so it captures the whole day's PEAK reported discomfort.
    private DailyFmsEstimator? _dailyFmsEstimatorLazy;
    private DailyFmsEstimator DailyFmsEstimatorInstance =>
        _dailyFmsEstimatorLazy ??= new DailyFmsEstimator(
            new FileChatHistoryStore(AppConfig.ChatHistoryPath),
            new FileSymptomEventStore(AppConfig.SymptomLogPath),
            SymptomRecorder);

    private bool _lastMaskActive;

    // Display Comfort (LuxController) removed at v0.11 (UI Consolidation
    // Sprint Phase 4) — feature decommissioned. See CHANGELOG.md.

    // D-9 (2026-06-11): WGC is the sole sensing source. With Demo/Cursor retired the
    // mode-keyed dispatch collapses to the one provider; kept as a method (rather than
    // inlining _wgcSensing everywhere) so the SetSensingMode wiring reads unchanged.
    private ISensingProvider ProviderFor(SensingMode mode) => _wgcSensing;

    public MainViewModel()
    {
        // M1 (Phase 6-pre): pass _log so the engine wrapper emits habituation
        // lifecycle events (profile change, level transition, spike, engine-
        // state change) for the chatbot's Phase 6c trajectory builder.
        _engine = new MockInferenceEngine(_adjustmentLog, _habituationStore, _habituationProfileStore, _log);

        // Alpha-2 GROUP D flag-mirror: the single App↔engine seam that arms the
        // adaptive-fit set-selector post-M1. SenseEase.Infrastructure cannot read
        // SenseEase.App, so the App layer mirrors the AppConfig flag onto the engine's
        // instance property here at startup — exactly the HeadTrackingEnabled App-only-
        // flag rule (see CreateHeadPoseReader, which reads AppConfig.HeadTrackingEnabled).
        // AppConfig.AdaptiveFitSelectorEnabled defaults TRUE since c1461a9, so this arms
        // the selector at startup; set the AppConfig flag back to false to revert to the
        // static path (the flag-off path stays byte-identical).
        if (_engine is MockInferenceEngine mockEngineForSelector)
            mockEngineForSelector.AdaptiveFitSelectorEnabled = AppConfig.AdaptiveFitSelectorEnabled;

        // Alpha-2 addendum (cancel explicit calibration → auto-profiling): mirror the
        // AutoProfilingEnabled flag onto the engine the same way, THEN seed the fresh-user
        // TYPICAL / L5「中」 start. AutoProfilingEnabled defaults TRUE since c1461a9. Per
        // D-8 (2026-06-11) flag-off RETAINS the TYPICAL/L5 seed — only the vestibular
        // prompt / console visibility / selector-adjacent UI react to the flag; the
        // explicit-calibration flow was deleted (D-13; restore requires git revert 3131387).
        if (_engine is MockInferenceEngine mockEngineForAutoProfiling)
        {
            mockEngineForAutoProfiling.AutoProfilingEnabled = AppConfig.AutoProfilingEnabled;
            mockEngineForAutoProfiling.ApplyAutoProfilingStartDefaults();

            // LOGIC-03 (W1W2 #15): in production the per-tick profile persist runs on the
            // engine's single-writer background queue so AV file-lock retries (~400ms worst)
            // never block the dispatcher under _gate. Tests leave this OFF for synchronous
            // read-after-write determinism; DisposeServices drains the queue at shutdown.
            mockEngineForAutoProfiling.BackgroundPersistEnabled = true;
        }

        // D-1 (CW-1 step 3, 2026-06-11): hydrate the graded-exposure (habituation)
        // pause from the persisted opt-in BEFORE the first Update tick. Graded
        // exposure defaults PAUSED (HabituationEnabled = false); the Settings
        // 漸進適應 / Graded exposure toggle is the sole user-intent flip. Applied
        // LAST among the engine-state steps so the pause survives the
        // auto-profiling seed above (seeding sets profile/level, never unpauses).
        // Q4.5-safe — SetHabituationPaused never touches the earned _offset.
        _engine.SetHabituationPaused(!UserPreferences.Current.HabituationEnabled);

        // CW-7 (UX-12 ≡ FEAT-15): hydrate the always-on overlay baseline (enabled flag +
        // level) from the persisted prefs BEFORE the overlay first shows, so the card's
        // +/- comfort action survives restart instead of resetting to OFF / L1. Seeds the
        // backing fields directly (not the public setters) so the auto-open side-effect
        // can't drive OverlayVisible during construction, before the host has wired the
        // overlay window. Q4.5-safe: this is a DISPLAY baseline only — it floors the shown
        // tier via ResolveDisplayedTier and never touches the earned habituation _offset.
        HydratePersistentOverlayFromPrefs();

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

        // Beta-1 D-16 / theme-2: apply the opt-in per-app attribution flag to the activity
        // filter (default OFF — explicit consent gated). When on, the filter resolves the
        // foreground app to a coarse on-device key the symptom recorder stamps onto data-points.
        SenseEase.Infrastructure.Real.WindowsActivityFilter.Instance.PerAppAttributionEnabled
            = AppConfig.PerAppAttributionEnabled;

        // H17: log the session baseline at boot so a field report ("my profile
        // flipped overnight") has a durable anchor for what state the session
        // started in. State.PersistedStateLoaded is a forensic category, so
        // FileEventLogStore persists it; the subscription above mirrors it to the
        // dashboard too.
        var bootSnap = _habituationProfileStore.Load();
        if (bootSnap is not null)
        {
            // Friendly, localized snapshot (UI/UX fine-tune 2026-06-07). ALL forensic
            // fields are retained for the durable field-report anchor — only the labels
            // follow the active UI language; the profile name / source stay verbatim
            // (no-translate exception list).
            string advZh = bootSnap.MedicalAdvisoryShown?.ToString() ?? "無";
            string advEn = bootSnap.MedicalAdvisoryShown?.ToString() ?? "none";
            _log.Append(new EventLogEntry
            {
                Category = "State.PersistedStateLoaded",
                Message  = Loc(
                    $"已載入個人化狀態：設定檔 {bootSnap.Profile}（來源 {bootSnap.ProfileSource}）、習慣化偏移 {bootSnap.HabituationOffsetPp:F1}pp、校正 {bootSnap.CalibrationCount} 次、醫療提示 {advZh}。",
                    $"Loaded saved state: profile {bootSnap.Profile} (source {bootSnap.ProfileSource}), habituation offset {bootSnap.HabituationOffsetPp:F1}pp, {bootSnap.CalibrationCount} calibration(s), advisory {advEn}."),
            });
        }
        // Kirby's decision: "AI replies follow the language toggle." Wire the live
        // UI-language toggle into Infrastructure via the ChatbotUiLanguage static
        // holder (Infrastructure must NOT reference the App assembly, so the App
        // pushes the toggle down here — same cross-boundary pattern as
        // LlamaSessionKey). The lambda reads the singleton at call time, so the
        // chatbot + calibration prompts pin the reply language to whatever the
        // toggle is set to at the moment each message is sent.
        ChatbotUiLanguage.IsEnglishProvider =
            () => SenseEase.App.Localization.LocalizationManager.Current.IsEnglish;

        // CW-11/EXIT-04: keep the owned reference so DisposeServices can terminate the
        // llama-server child deterministically on quit. ChatbotVm + _nlu both share this
        // instance without owning its lifetime (see _mistral field doc).
        _mistral = new MistralChatbotService(AppConfig.ModelPath, _adjustmentLog, _log);
        ChatbotVm = new ChatbotViewModel(_mistral, _engine, _adjustmentLog, () => OverlayVisible, _log);

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
        // Phase 7 (RESUME_ACKNOWLEDGE): the chatbot relays the user's in-chat confirmation to
        // continue from a tier>=8 PausedAwaitingUserConfirm check-in. The engine method is a
        // guarded no-op unless actually awaiting confirm; resume continues the habituation
        // program (protection stays at the current tier). The quote is logged for audit.
        ChatbotVm.AcknowledgeResumeCallback = quote =>
        {
            if (_engine is MockInferenceEngine mockResume)
            {
                mockResume.AcknowledgeAndResumeHabituation();
                AppendLog("Habituation", Loc(
                    $"使用者於對話確認恢復習慣化（\"{quote}\"）。",
                    $"User confirmed habituation resume in chat (\"{quote}\")."), CurrentState);
                UpdateFromState(_engine.Current);
            }
        };

        // M2 (Phase 6c): wire host-side lookups so ChatbotViewModel can build
        // the [habituation_state] block per turn. Power Save state comes from
        // MainWindow's session-scoped _powerSaveActive (queried via the VM's
        // _powerSaveActiveLookup callback set by MainWindow). Snapshot load
        // routes through the existing _habituationProfileStore.
        ChatbotVm.LoadHabituationSnapshot = () => _habituationProfileStore.Load();
        // UM5 + LOGIC-06 rider (CW-7): read the daily behaviour aggregates for the
        // [behavior_trend] block through the SHARED _behaviorWriter instance rather than
        // a fresh DailyBehaviorAggregateWriter. The writer's read/write are guarded by a
        // per-instance lock (_gate); a second instance has its OWN lock, so a fresh reader
        // could observe a torn file mid-rewrite (File.WriteAllText is non-atomic). Sharing
        // the one instance makes reader and writer serialise on the same lock. (G4 is also
        // making the writer internally path-keyed — both changes are good.)
        ChatbotVm.LoadBehaviorAggregates  = () => _behaviorWriter.ReadAll();
        ChatbotVm.IsPowerSaveActive       = () => _powerSaveActiveLookup?.Invoke() ?? false;
        ChatbotVm.AdjustmentApplied += (_, _) => UpdateFromState(_engine.Current);
        // Calibration CARD removed (2026-06-08): the 5-card Q&A, confirm card, the
        // invitation/recalibrate banners and the LLM-calibration session are gone (dead,
        // unreachable UI). The engine's CalibrationProcessor / CalibrationResult plumbing
        // and the User-Model LLM client are kept.
        // ChatbotNluService(MistralChatbotService) sets _ownsChatbot=false, so disposing
        // _nlu does NOT dispose _mistral — MainViewModel owns that single dispose (EXIT-04).
        _nlu = new ChatbotNluService(_mistral);

        _sensing = ProviderFor(AppConfig.Mode);


        // Fires every 300 ms to keep the LeakyIntegrator decaying when no real samples arrive.
        // D-9: WGC is the sole source — if no sample has arrived in the last 600 ms the
        // provider has stalled, so step the engine with OFI=0 to decay pressure rather than
        // freezing.
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _idleTimer.Tick += (_, _) =>
        {
            if ((DateTime.UtcNow - _lastRealSampleAt).TotalMilliseconds > 600)
                UpdateFromState(_engine.Update(0, Mode, DateTime.UtcNow));
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

            // B3 (per-app learning): keep the engine's foreground key fresh (1 Hz) so a durable
            // event emitted right after an alt-tab is bucketed to the correct app. Consent-gated
            // upstream (null unless D-16 is on); null ⇒ global bucket = today's behaviour.
            PushForegroundContextToEngine();

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

            // UM1: periodic crash-durability flush of the day's partial aggregate.
            // The idempotent-by-date writer rewrites the single row, so repeated
            // flushes converge rather than duplicate. Date rollover itself is
            // handled at sample time (SampleBehavior); this is just the heartbeat
            // that gets a long-running session's data to disk without a restart.
            if (++_behaviorFlushTickCounter >= BehaviorFlushPeriodSeconds)
            {
                _behaviorFlushTickCounter = 0;
                FlushBehaviorAggregate();
                PushFmsContextToEngine();   // B1: refresh the selector's measured-FMS outcome term
            }
        };
        _metricsTimer.Start();

        ResetStateCommand = new RelayCommand(_ => ResetState());
        // Dashboard profile pills are the USER's own pick: drive the engine directly
        // via SetHabituationProfile (NOT the severed chatbot SwitchProfile path —
        // Reading A keeps the chatbot from live-switching the profile). CommandParameter
        // is the canonical profile string "RESILIENT"/"TYPICAL"/"SENSITIVE".
        SelectProfileCommand = new RelayCommand(o => OnUserProfileSelected(o as string ?? string.Empty));
        ToggleOverlayCommand = new RelayCommand(_ => OverlayVisible = !OverlayVisible);
        AnalyzeFeedbackCommand = new RelayCommand(async _ => await SubmitFeedbackAsync(),
                                                  _ => !string.IsNullOrWhiteSpace(FeedbackText));
        ToggleHabituationCommand = new RelayCommand(_ =>
        {
            _engine.SetHabituationPaused(!HabituationPaused);
            UpdateFromState(_engine.Current);
            AppendLog("Habituation",
                HabituationPaused ? Loc("再適應已暫停。", "Habituation paused.")
                                  : Loc("再適應已恢復。", "Habituation resumed."),
                CurrentState);
        });
        // Alpha-2 addendum: one-time vestibular prompt responses (auto-profiling world).
        VestibularPromptYesCommand = new RelayCommand(_ => AnswerVestibularPrompt(true));
        VestibularPromptNoCommand  = new RelayCommand(_ => AnswerVestibularPrompt(false));
        // UX-13: one-time first-run welcome strip dismissal.
        DismissFirstRunCommand = new RelayCommand(_ => DismissFirstRun());

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
            // CW-11/EXIT-03: InvokeAsync (not the synchronous Invoke) so the 1 Hz
            // detector's ThreadPool Poll thread never blocks waiting on the UI
            // dispatcher. The old sync Invoke was the stack_dump.txt thread-pileup
            // amplifier — if the UI thread stalled, successive Poll callbacks each
            // parked a ThreadPool thread on the blocking marshal. The handler only
            // sets the Mode property (cheap), so fire-and-forget is correct; the
            // Interlocked re-entrancy gate in Poll keeps overlapping ticks from
            // piling up independently of this change.
            _activityDetector.ModeChanged += (_, newMode) =>
                _dispatcher.InvokeAsync(() =>
                {
                    if (Mode != newMode) Mode = newMode;
                });
            _activityDetector.Start();
        }

        CurrentState = _engine.Current;
        UpdateFromState(CurrentState);
        AppendLog("System", Loc("SenseEase 已啟動，推論迴圈就緒。", "SenseEase started, inference loop ready."), CurrentState);
    }


    /// <summary>Switch to the given sensing provider, unsubscribing the previous
    /// one and subscribing the new one. Idempotent: setting the current mode is
    /// a no-op. On failure, the previous provider is restored. Centralised so the
    /// sensing-mode callers share the same sub/unsub logic. D-9: with Demo/Cursor
    /// retired the only target is Wgc, but the sub/unsub plumbing is kept intact so
    /// SetSensingModeToWgc's resume-protection path reads unchanged.</summary>
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
            AppendLog("Error", Loc($"感測模式切換失敗：{ex.GetType().Name}：{ex.Message}", $"Sensing mode switch failed: {ex.GetType().Name}: {ex.Message}"), CurrentState);
        }
    }

    // D-9 (2026-06-11): WGC is the sole sensing source — the DEMO/CURSOR labels were
    // retired with their providers. The badge that read this is already gone (removed
    // per the 2026-06-09 UI audit); the property is retained but constant.
    private static string ModeLabel(SensingMode mode) => "SCREEN CAPTURE";

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
        // D-9 (2026-06-11): the Demo slider that pushed into MockSensingProvider was retired.
        // With WGC the sole source this is now a pure display mirror of the live OFI sample
        // (written by OnOpticalFlowSampled); setting it no longer feeds the pipeline.
        get => _opticalFlowIndex;
        set => Set(ref _opticalFlowIndex, value);
    }

    private double _pressure;
    public double Pressure { get => _pressure; private set => Set(ref _pressure, value); }

    private string _mode = "Passenger";
    public string Mode
    {
        // D-9 (2026-06-11): the Demo-mode forced re-push (into MockSensingProvider) was
        // retired with that provider. Under WGC the next ~15 Hz capture tick carries the new
        // Driver/Passenger mode into the engine, so no synthetic push is needed here.
        get => _mode;
        set => Set(ref _mode, value);
    }

    private int _fovRadiusPercent = SenseDefaults.OuterTransitionRadiusPercent;
    public int FovRadiusPercent { get => _fovRadiusPercent; private set => Set(ref _fovRadiusPercent, value); }

    private bool _maskActive;
    public bool MaskActive { get => _maskActive; private set => Set(ref _maskActive, value); }

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

    // Honest FOV hint: the engine's accumulated chatbot FOV personalization (pp, <= 0),
    // mirrored from SenseState so the Advanced FOV card's 清晰區 caption can explain WHY the
    // resting clear-zone reads below the natural baseline. 0 ⇒ plain caption; < 0 ⇒ caption
    // gains "（含個人化 −N）" via a MultiBinding converter. Display-only; the engine never reads it.
    private int _userFovOffsetPp;
    public int UserFovOffsetPp { get => _userFovOffsetPp; private set => Set(ref _userFovOffsetPp, value); }

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
            healthy ? Loc("感測已恢復。", "Sensing restored.")
                    : Loc("感測中斷，重新連線中。", "Sensing fault — reconnecting."),
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

    public string HabituationButtonText => HabituationPaused
        ? SenseEase.App.Localization.LocalizationManager.Current.Pick("恢復再適應", "Resume")
        : SenseEase.App.Localization.LocalizationManager.Current.Pick("暫停再適應", "Pause");

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
    // D6 (master plan M7): day-1 protection originally defaulted the always-on calm
    // field ON (paired with baseline tier 1 = 0% alpha at rest). Kirby override
    // (2026-06-06): default the always-on overlay toggle OFF for a more immersive,
    // zero-intervention start (aligns with the Alpha-2 "auto-profiling replaces explicit
    // calibration" direction). The D6 "0.00 alpha at rest" floor (VimsLevels[0].Alpha)
    // stays pinned. If this is re-enabled, KEEP the baseline-tier-1 pairing below
    // (risk R-B: a tier-4 baseline would over-dim every new user ~43%).
    // CW-7 (UX-12 ≡ FEAT-15): hydrated from UserPreferences in the ctor BEFORE the
    // overlay first shows (see HydratePersistentOverlayFromPrefs). _hydratingOverlayBaseline
    // suppresses the save-on-change while the ctor is seeding from disk, so reading the
    // persisted value can't immediately re-write it (and can't trip OverlayVisible during
    // construction before the host has wired the overlay window).
    private bool _persistentOverlayEnabled = false;
    private bool _hydratingOverlayBaseline;
    public bool PersistentOverlayEnabled
    {
        get => _persistentOverlayEnabled;
        set
        {
            if (!Set(ref _persistentOverlayEnabled, value)) return;
            // Toggling on auto-opens the overlay so the feature is immediately visible.
            // Toggling off doesn't auto-hide — user keeps whatever overlay state they had.
            if (value && !OverlayVisible) OverlayVisible = true;
            SavePersistentOverlayPrefs();
        }
    }

    private int _persistentOverlayBaselineTier = 1;
    /// <summary>Baseline intensity level (1–12, native scale) when PersistentOverlayEnabled is true.
    /// D6 day-1 default is 1 = 0% alpha at rest (nothing visibly dims; the field is
    /// "ready", not "always dark"). Natural higher levels still override, so a real
    /// motion spike darkens the periphery. The user can raise this baseline.
    /// <para>CW-7: persisted to UserPreferences on change so the card's +/- baseline
    /// survives restart (it was a plain VM field before). Q4.5-safe — this is a DISPLAY
    /// floor only, never the habituation offset.</para></summary>
    public int PersistentOverlayBaselineTier
    {
        get => _persistentOverlayBaselineTier;
        set { if (Set(ref _persistentOverlayBaselineTier, Math.Clamp(value, 1, 12))) SavePersistentOverlayPrefs(); }
    }

    /// <summary>CW-7 (UX-12 ≡ FEAT-15): persist the always-on overlay baseline (enabled
    /// flag + level) to UserPreferences. No-op while the ctor is hydrating from disk so a
    /// seed-read can't immediately re-write the same value. Best-effort like every other
    /// prefs save; matches the GradedExposureEnabled persist pattern. Q4.5-safe.</summary>
    private void SavePersistentOverlayPrefs()
    {
        if (_hydratingOverlayBaseline) return;
        UserPreferences.Current.PersistentOverlayEnabled      = _persistentOverlayEnabled;
        UserPreferences.Current.PersistentOverlayBaselineTier = _persistentOverlayBaselineTier;
        UserPreferences.Current.Save();
    }

    /// <summary>CW-7 (UX-12 ≡ FEAT-15): seed the always-on overlay baseline from the
    /// persisted prefs at construction. Sets the backing fields directly under the
    /// hydrate guard so neither the auto-open side-effect nor a redundant Save fires
    /// while seeding. The baseline level is clamped to the 1..12 native scale (defensive
    /// against a hand-edited prefs file). Called from the ctor BEFORE the overlay shows.</summary>
    private void HydratePersistentOverlayFromPrefs()
    {
        _hydratingOverlayBaseline = true;
        try
        {
            var prefs = UserPreferences.Current;
            _persistentOverlayBaselineTier = Math.Clamp(prefs.PersistentOverlayBaselineTier, 1, 12);
            _persistentOverlayEnabled      = prefs.PersistentOverlayEnabled;
        }
        finally { _hydratingOverlayBaseline = false; }
    }

    /// <summary>Apply the always-on tier floor to a freshly-emitted SenseState. Returns
    /// the (possibly elevated) effective tier. Pure function; safe to call from any
    /// thread.</summary>
    public int ResolveDisplayedTier(int naturalTier) =>
        PersistentOverlayEnabled
            ? Math.Max(naturalTier, PersistentOverlayBaselineTier)
            : naturalTier;

    /// <summary>Companion to <see cref="ResolveDisplayedTier"/>. When the always-on floor
    /// elevates the displayed tier above the engine's RAW tier, the selector's
    /// <c>MaskPeakAlphaOverride</c> — resolved by the engine for the raw tier (≈0 / L1 when the
    /// screen is calm) — would blank the always-on mask, because it overrides the tier-keyed
    /// alpha in <c>OverlayWindow.ApplyFovMask</c>. Re-resolve the override for the DISPLAYED tier
    /// so the baseline dimming renders at the active set's depth for that tier. Pass-through when
    /// there is no override (-1 ⇒ selector flag OFF) so the flag-off path stays byte-identical.</summary>
    public double ResolveDisplayedMaskOverride(int displayedTier, double rawOverride)
        => rawOverride < 0.0
            ? rawOverride
            : ((_engine as MockInferenceEngine)?.ResolveLevel(displayedTier).PeakMaskAlpha ?? rawOverride);

    /// <summary>Pop-up card +/- pills: fine-tune the 12-level intensity scale (the level the
    /// card reads out) with an immediate VISIBLE change in BOTH directions — the root fix for
    /// the "+/- not functional" report. Previously +/- nudged the FOV offset, which clamps at
    /// 0, so "−" was a silent no-op at rest and the L-readout never moved. Now it rides the
    /// existing 1..12 always-on baseline floor: "+" raises the baseline (more peripheral
    /// dimming), "−" lowers it; nudging down to L1 drops the floor entirely so the field
    /// returns to the calm/immersive natural state. Forces the WGC provider + overlay visible
    /// so the change is perceptible. Q4.5-safe (never touches the habituation offset). Returns
    /// the new displayed level (1..12) so the caller can refresh the card's L-readout.</summary>
    public int NudgeIntensityLevel(int direction)
    {
        int current = ResolveDisplayedTier(CurrentState.VimsTier);
        int target  = Math.Clamp(current + (direction >= 0 ? 1 : -1), 1, 12);
        // P5: a card +/- nudge defers the AdaptiveFit selector AND retracts any stale
        // pending suggestion. This path never reaches ApplyUserFeedback (it rides the
        // always-on baseline floor), so the OnManualAdjust wiring lives here.
        (_engine as MockInferenceEngine)?.NotifyManualLevelNudge(direction);   // B2: register manual-authority sign
        // B2 caution-feed: persist the nudge as a "LevelNudge" adjustment so it feeds the habituation
        // relaxation cadence (CombinedFovCautionWeight) — a habitually-tightening user habituates more
        // gently. SEPARATE parameter (not "FovRadius") so it never shifts the resting FOV offset (the
        // nudge's protection already persists via the baseline tier). Up (more protection) ⇒ negative
        // Delta (matching the FOV "more protection = tighter" sign). Best-effort.
        try
        {
            _adjustmentLog.Append(new AdjustmentRecord
            {
                Parameter = "LevelNudge",
                Delta     = (direction >= 0 ? -1 : +1) * ManualNudgeFovEquivalentPp,
                Source    = "user",
            });
        }
        catch { /* persistence is best-effort; the nudge must still apply */ }
        SetSensingModeToWgc();
        if (target <= 1)
        {
            // "−" to the floor: drop the always-on baseline so the field goes back to calm.
            PersistentOverlayBaselineTier = 1;
            PersistentOverlayEnabled      = false;
        }
        else
        {
            PersistentOverlayBaselineTier = target;                          // setter clamps 1..12
            if (!PersistentOverlayEnabled) PersistentOverlayEnabled = true;  // auto-opens overlay
        }
        if (!OverlayVisible) OverlayVisible = true;
        UpdateFromState(_engine.Current);
        return ResolveDisplayedTier(CurrentState.VimsTier);
    }

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

    public ICommand ResetStateCommand { get; }
    public ICommand ToggleOverlayCommand { get; }
    public ICommand AnalyzeFeedbackCommand { get; }
    public ICommand ToggleHabituationCommand { get; }
    public ICommand SelectProfileCommand { get; }

    // Calibration video DEMO + calibration CARD fully removed (2026-06-08): the first-run
    // welcome banner, StartSetupDemo/StartDemo/StopDemo/synthetic-ramp/OnDemoTick, the
    // DemoVideoWindow (WPF MediaElement), the 5-card Q&A / confirm card / LLM session and
    // ShowFirstRunBanner are all gone — no codec dependency, no bundled video, no dead QA UI.
    // Engine-side CalibrationProcessor / CalibrationResult are kept (not part of the card).

    // ?? Demo loop ??????????????????????????????????????????????????????????




    private void ResetState()
    {
        OpticalFlowIndex = 0;
        _engine.ResetPersonalization();
        _spikeDetector.Reset();
        UpdateFromState(_engine.Current);
        _adjustmentLog.Append(new AdjustmentRecord { Parameter = "Reset", Source = "reset" });
        NluResponse = string.Empty;
        OnPropertyChanged(nameof(NluResponseVisible));
        AppendLog("System", Loc("狀態已重設。", "State reset."), CurrentState);
    }


    // ?? Inference loop ?????????????????????????????????????????????????????

    private void OnOpticalFlowSampled(object? sender, OpticalFlowSample sample)
    {
        // D-9: WGC is the sole source — mirror its OFI straight onto the display field
        // (assign the backing field directly, not the setter, so this never re-enters the
        // pipeline) and stamp the freshness clock the idle-timer stall check reads.
        _lastRealSampleAt = DateTime.UtcNow;
        _opticalFlowIndex = sample.OpticalFlowIndex;
        OnPropertyChanged(nameof(OpticalFlowIndex));

        SenseState state = _engine.Update(sample.OpticalFlowIndex, Mode, sample.SampledAt);
        UpdateFromState(state);

        if (state.MaskActive != _lastMaskActive)
        {
            _lastMaskActive = state.MaskActive;
            AppendLog("Inference", state.MaskActive
                ? Loc($"壓力 {state.Pressure:F0} 超過閾值，已啟動視野遮罩。", $"Pressure {state.Pressure:F0} exceeded threshold, FOV mask activated.")
                : Loc("壓力回到閾值以下，已解除視野遮罩。", "Pressure returned below threshold, FOV mask released."), state);
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
        HabituationLevel  = state.HabituationLevel;
        HabituationPaused = state.HabituationPaused;
        VimsTier          = state.VimsTier;
        DisplayStressPercent = state.DisplayStressPercent; // P1
        UserFovOffsetPp      = state.UserFovOffsetPp;       // honest FOV-card hint
        OnPropertyChanged(nameof(CurrentProfile)); // profile-pill active highlight

        // D3: MaskActive just updated above — refresh the liveness scalar so
        // Protecting/Steady-watching track the mask in lock-step with the tier.
        RecomputeLiveness();

        // ── Eye-Protection session metrics ───────────────────────────────────────
        if (state.RawOpticalFlowIndex > SessionPeakOfi) SessionPeakOfi = state.RawOpticalFlowIndex;
        if (state.Pressure > SessionPeakPressure) SessionPeakPressure = state.Pressure;

        // FOV intercept: count the rising edge of MaskActive. Captured into a local
        // so UM1's daily collector can count the same rising edge as a mask activation.
        bool maskRising = state.MaskActive && !_prevMaskActiveForIntercept;
        if (maskRising)
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
        // Beta-1 #4 (clinical-safety): treat the pose as ACTIVE only when a face is actually
        // detected. The sidecar keeps emitting the last-valid cx/cy with FaceDetected=false on
        // face loss (SIDE-04, so the stream stays fresh and the staleness fallback never fires),
        // so a non-null sample is necessary but NOT sufficient. Writing HeadTrackingActive=true
        // on a stale face_detected=false sample violated SenseState.HeadTrackingActive's contract
        // and would carry a stale gaze into any consumer of the published state (e.g. the
        // Theme-5 data layer / a non-pump renderer). When the face is lost, publish the centered
        // baseline (0.5/0.5, inactive) — the 165 Hz pump owns the SMOOTH ease-to-center for the
        // live overlay; this only fixes the published source-of-truth state.
        var poseReader = _overrideReader ?? _headPoseReader;
        HeadPoseSample? pose = poseReader.ReadLatest();
        if (pose is not null && pose.FaceDetected)
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

        // Spike detection drives the pop-up + chatbot follow-up. (The demo run
        // that used to suppress this was removed 2026-06-08.)
        bool spike = _spikeDetector.Push(state.Pressure, DateTime.UtcNow);
        if (spike)
        {
            PressureSpikeDetected?.Invoke(this, _spikeDetector.LastNormalizedSpike);
        }

        // UM1: fold this tick into the daily behaviour collector (after the spike
        // edge is known). No-op during demo runs — see SampleBehavior.
        SampleBehavior(state, spike, maskRising);
    }

    /// <summary>UM1: fold one engine tick into the daily behaviour collector. Handles
    /// local-date rollover (flush the completed day, re-open the new one) and same-day
    /// session seed-merge (a mid-day restart resumes the additive counters from the
    /// row already on disk rather than clobbering it). No-op during demo runs
    /// (<see cref="IsRunning"/>) so the intentional vection stress test stays out of
    /// the longitudinal trend. Fail-soft: any telemetry error is swallowed so it can
    /// never break the inference loop.</summary>
    private void SampleBehavior(SenseState state, bool spike, bool maskRising)
    {
        try
        {
            DateTime nowLocal = DateTime.Now;
            string today = nowLocal.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            int offsetPp = (_engine as MockInferenceEngine)?.HabituationOffsetPp ?? 0;

            // Local-date rollover: persist the completed day, then re-open fresh.
            if (_behaviorAccumulator.Date is { } open && open != today)
            {
                FlushBehaviorAggregate();
                _behaviorAccumulator.Reset();
            }

            // (Re-)open the day, seeding from any row already on disk for it so a
            // mid-day restart resumes the additive counters instead of clobbering them.
            if (_behaviorAccumulator.Date is null)
            {
                DailyBehaviorAggregate? seed = null;
                foreach (var row in _behaviorWriter.ReadAll())
                    if (row.Date == today) { seed = row; break; }
                _behaviorAccumulator.Begin(today, seed, offsetPp, state.HabituationLevel, nowLocal);
            }

            _behaviorAccumulator.Sample(
                ofi:        (int)Math.Round(state.RawOpticalFlowIndex),
                tier:       state.VimsTier,
                maskRising: maskRising,
                spike:      spike,
                offsetPp:   offsetPp,
                level:      state.HabituationLevel,
                nowLocal:   nowLocal);
        }
        catch { /* best-effort telemetry — never disturb the inference loop */ }
    }

    /// <summary>UM1: write the collector's current-day aggregate to behavior_log.jsonl
    /// (idempotent by date — replaces the day's row). Called on the 60 s durability
    /// cadence, on date rollover, and at shutdown. Fail-soft.</summary>
    private void FlushBehaviorAggregate()
    {
        try
        {
            var agg = _behaviorAccumulator.BuildAggregate();
            if (agg is not null) _behaviorWriter.Write(agg);
        }
        catch { /* best-effort */ }
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

    /// <summary>Localize an event-log / status message to the active UI language. New
    /// entries follow the current language; historical entries keep the language they
    /// were written in (Pick captures it at write time). Profile names / config values
    /// passed inside the strings stay as-is per the no-translate exception list.</summary>
    private static string Loc(string zh, string en)
        => SenseEase.App.Localization.LocalizationManager.Current.Pick(zh, en);

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
    private System.Threading.CancellationTokenSource? _userModelAnalysisCts;

    /// <summary>UM4 first-attempt delay (idle settle before the first analysis pass).</summary>
    private static readonly TimeSpan UserModelFirstAttemptDelay = TimeSpan.FromMinutes(2);

    /// <summary>UM4 retry cadence after a failed attempt (LLM not up yet / transient
    /// error). The loop runs for the whole app session — there is no attempt cap; it
    /// stops only when one attempt completes with the LLM available (or on cancel).</summary>
    private static readonly TimeSpan UserModelRetryInterval = TimeSpan.FromMinutes(10);

    /// <summary>UM4: start the silent background retrospective user-model analysis.
    /// First attempt after a 2-minute idle delay, then a bounded-to-the-session retry
    /// every ~10 minutes until one attempt finds the LLM available and completes (the
    /// llama-server lazy-loads on first chat, so the first pass usually misses it).
    /// Fire-and-forget + cancellable (DisposeServices cancels the loop). Called from
    /// <see cref="SenseEase.App.App"/>'s one-shot window-activation hook ONLY, so it
    /// never runs under unit tests (which construct this VM directly). Reuses the SHARED
    /// _observationWriter (backed by _habituationProfileStore) so the analyser's
    /// observation writes stay atomic against the engine's persist (W2.0-C per-instance
    /// lock); the index store shares the same profile-store instance for the same reason.</summary>
    public void StartBackgroundUserModelAnalysis()
    {
        if (_userModelAnalysisCts is not null) return; // idempotent — start once
        _userModelAnalysisCts = new System.Threading.CancellationTokenSource();
        var ct = _userModelAnalysisCts.Token;

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var delay = UserModelFirstAttemptDelay;
                while (true)
                {
                    await System.Threading.Tasks.Task.Delay(delay, ct).ConfigureAwait(false);

                    // The llama-server lazy-loads on first chat; LlamaSessionKey.Current is
                    // published at spawn and cleared at teardown, so a non-null key is the
                    // App-readable "LLM is up" signal. Only run a real pass when it's up.
                    bool llmAvailable = LlamaSessionKey.Current is not null;
                    bool ranToCompletion = false;
                    if (llmAvailable)
                    {
                        var analyzer = new UserModelRetrospectiveAnalyzer(
                            new LlamaServerCalibrationClient(),
                            new FileChatHistoryStore(AppConfig.ChatHistoryPath),
                            _observationWriter, // shared-store writer (atomic vs engine persist)
                            new UserModelIndexStore(
                                AppConfig.UserModelIndexPath, AppConfig.BehaviorLogPath, _habituationProfileStore));
                        // A faulted attempt (e.g. AV file-lock at index load/save) must NOT
                        // escape the while-loop into the outer catch — that would permanently
                        // end the session's retry task. Swallow non-cancellation faults here so
                        // ranToCompletion stays false and the loop retries via
                        // ShouldRetryUserModelAnalysis(true, false). Cancellation still propagates.
                        try
                        {
                            await analyzer.RunAsync(ct).ConfigureAwait(false);
                            ranToCompletion = true;
                            // B5: consent-gated learning-loop tuning pass, using any sensitivity proposal
                            // the analyzer captured this run. Best-effort; never blocks/breaks the loop.
                            MaybeRunAdaptationTuning(analyzer.LastProposedSensitivityScale, analyzer.LastProposalRationale);
                        }
                        catch (OperationCanceledException) { throw; } // app exiting / superseded
                        catch { /* transient fault → retry next interval (ranToCompletion stays false) */ }
                    }

                    // Stop once an attempt completed with the LLM up; otherwise wait the
                    // retry interval and try again (no attempt cap — the session is the cap).
                    if (!ShouldRetryUserModelAnalysis(llmAvailable, ranToCompletion)) return;
                    delay = UserModelRetryInterval;
                }
            }
            catch (OperationCanceledException) { /* app exiting / superseded */ }
            catch { /* silent — retrospective analysis must never disturb the user */ }
        }, ct);
    }

    /// <summary>UM4 retry decision, extracted pure + static so it unit-tests without a
    /// real timer (mirrors <see cref="DeriveLiveness"/>). Returns true ⇒ schedule another
    /// retry, false ⇒ the loop is done for this session. Retry while the LLM is not yet
    /// available (the common first-pass case before any chat spawns the server); stop only
    /// once an attempt ran to completion with the LLM up. An available-but-incomplete
    /// attempt (RunAsync faulted) is treated as a transient failure and retried.</summary>
    internal static bool ShouldRetryUserModelAnalysis(bool llmAvailable, bool ranToCompletion)
        => !(llmAvailable && ranToCompletion);

    /// <summary>B5 — the learning-loop tuning pass, run AFTER the retrospective analysis completes and
    /// ONLY when the user has opted in (<see cref="ProactiveCheckInConsent.Enabled"/>). Assembles the
    /// gate's <see cref="MeasuredState"/> from the durable logs + live engine state, maps the LLM's
    /// OPTIONAL sensitivity proposal to a direction (else Hold ⇒ data-driven tighten only), and runs the
    /// deterministic <see cref="AdaptationGate"/> via the coordinator. On an Apply it moves the engine's
    /// dedicated auto-channel and surfaces a subtle in-chat notice.
    ///
    /// <para>Fully bounded: consent default OFF; the gate is fail-closed + clamped (±12pp) + monotone;
    /// the change is reversible (a Settings reset zeros it). Best-effort — any fault is swallowed so the
    /// analysis loop and the engine are never disturbed. Runs on the background analysis thread: the file
    /// reads are off-UI, <c>ApplyAutoAdaptDelta</c> locks the engine gate, and the notice marshals to the
    /// dispatcher.</para></summary>
    private void MaybeRunAdaptationTuning(double? proposedScale, string proposalRationale)
    {
        try
        {
            if (UserPreferences.Current.AdaptiveAutoTuneConsent != ProactiveCheckInConsent.Enabled) return;
            if (_engine is not MockInferenceEngine mock) return;

            DateTime now = DateTime.UtcNow;
            var cfg = AdaptationGateConfig.Default;

            // Evidence — measured-only FMS + tolerated-exposure trend (Stage 3 pure helpers).
            var symptomEvents = new FileSymptomEventStore(AppConfig.SymptomLogPath).ReadAll();
            var fms = AdaptationEvidence.MeasuredOnFms(symptomEvents, now, AdaptiveAutoTuneWindowDays, cfg.RelaxFmsLowThreshold);
            var exposure = AdaptationEvidence.ExposureTrend(_behaviorWriter.ReadAll());

            // Timing — derived from the durable, TimestampUtc-stamped adjustment log (the auto-channel
            // writes a Source="system" Parameter="AutoAdaptTrigger" audit record on every apply).
            IReadOnlyList<AdjustmentRecord> records =
                (_adjustmentLog as FileAdjustmentLog)?.All() ?? System.Array.Empty<AdjustmentRecord>();
            var autoRecords = records.Where(r => r.Parameter == AutoAdaptParameter).ToList();
            double daysSinceLastAutoChange = autoRecords.Count > 0
                ? (now - autoRecords.Max(r => r.TimestampUtc)).TotalDays : double.PositiveInfinity;
            int autoChangesThisWeek = autoRecords.Count(r => (now - r.TimestampUtc).TotalDays <= 7.0);
            double netRelaxTodayPp = autoRecords
                .Where(r => r.TimestampUtc.Date == now.Date && r.Delta > 0).Sum(r => r.Delta);
            bool hasEverAutoRelaxed = autoRecords.Any(r => r.Delta > 0);
            var manualTightens = records.Where(r =>
                r.Source == "user" && (r.Parameter == "FovRadius" || r.Parameter == "LevelNudge") && r.Delta < 0).ToList();
            double hoursSinceManualTighten = manualTightens.Count > 0
                ? (now - manualTightens.Max(r => r.TimestampUtc)).TotalHours : double.PositiveInfinity;

            // Engine state — cohort, base trigger (= effective − auto), current auto delta.
            Cohort cohort = mock.IsVestibularProtectionEnabled ? Cohort.Vestibular
                          : (mock.HabituationProfile == HabituationProfile.Sensitive ? Cohort.Sensitive : Cohort.Typical);
            double currentDelta = mock.AutoAdaptTriggerDeltaPp;
            double baseTrigger  = mock.EffectiveTrigger - currentDelta;

            var state = AdaptationEvidence.BuildMeasuredState(
                cohort, fms, exposure, baseTrigger, currentDelta,
                hoursSinceManualTighten, daysSinceLastAutoChange, autoChangesThisWeek, netRelaxTodayPp, hasEverAutoRelaxed);

            // Direction — the LLM's only influence; absent proposal ⇒ Hold (the gate still tightens on
            // measured worsening via its independent authority).
            AdaptDirection dir = proposedScale is double ps
                ? AdaptationGate.DirectionFromSensitivityProposal(ps, mock.PersonalPressureScale)
                : AdaptDirection.Hold;
            var hint = new AdaptationHint(dir, proposalRationale ?? string.Empty);

            _adaptationCoordinator.RunOnce(
                consentEnabled: true,
                hint: hint,
                state: state,
                applyAutoDelta: d => mock.ApplyAutoAdaptDelta(d),
                onApplied: (apply, _) => ChatbotVm?.AddAdaptationNotice(AdaptationNoticeText(apply)));
        }
        catch { /* advisory tuning — never disturb the analysis loop or the engine */ }
    }

    /// <summary>B5 — the subtle bilingual in-chat notice for an applied auto-adaptation (Kirby's
    /// "tell the user in the chatbox" transparency policy). Direction-aware; points at the reversible
    /// path. Render-only text.</summary>
    private static string AdaptationNoticeText(GateDecision.Apply apply)
        => apply.Direction == AdaptDirection.Relax
            ? Loc("根據你最近的回報，我稍微放寬了畫面防護。若想調回，可隨時在設定中重置，或要求加強。",
                  "Based on your recent check-ins, I eased the screen protection a little. You can undo this anytime in Settings (reset), or ask for more protection.")
            : Loc("根據你最近的狀況，我略為加強了畫面防護。若想調整，可隨時在設定中重置。",
                  "Based on your recent patterns, I strengthened the screen protection a little. You can adjust this anytime in Settings (reset).");

    public void DisposeServices()
    {
        // UM1: persist the day's final behaviour aggregate before teardown so the
        // tail since the last 60 s heartbeat isn't lost on a clean exit.
        FlushBehaviorAggregate();
        // Beta-1 D-21: record today's NL→FMS estimate (the day's peak reported discomfort) at
        // clean exit, when the full day's conversation is available. Idempotent per UTC day;
        // best-effort (never throws). This is the first producer that fills symptom_log.jsonl.
        try { DailyFmsEstimatorInstance.RunOnce(DateTime.UtcNow); } catch { }
        try { _userModelAnalysisCts?.Cancel(); _userModelAnalysisCts?.Dispose(); } catch { }
        try { _activityDetector.Dispose(); } catch { }

        // CW-11/EXIT-04: stop the ACTIVE sensing provider on exit. Previously
        // _sensing.Stop() was never called on quit, widening the EM3 race (freeing
        // COM/D3D pointers under a live OnTick) into every shutdown. Stop() is
        // idempotent + lock-guarded on WgcSensingProvider; try-guarded so a provider
        // throw can't abort the rest of teardown.
        try { _sensing.Stop(); } catch { }

        // CW-11/EXIT-04 (folds LOGIC-14 residual): the WGC provider implements
        // IDisposable but Dispose() was NEVER called from teardown — the capture +
        // liveness-watchdog timers leaked across every quit. Dispose the stable
        // _wgcSensing instance directly (Dispose() re-runs Stop() harmlessly) rather
        // than the routed _sensing, so the WGC resources are reaped even when the
        // active provider is currently something else.
        try { _wgcSensing.Dispose(); } catch { }

        // M4 head-tracking sprint: gracefully stop the sidecar process if it
        // was spawned. The JobObject reaper would catch a hard exit anyway,
        // but explicit Dispose is faster + flushes the stderr tail log.
        if (_headPoseReader is IDisposable disposable)
        {
            try { disposable.Dispose(); } catch { }
        }

        // CW-11/EXIT-04: terminate the llama-server child deterministically on quit.
        // ChatbotVm + _nlu share this instance but neither owns its lifetime, so the
        // single dispose lives here on the app-exit path (dispose ONCE — never from a
        // ChatbotViewModel/NLU teardown). MistralChatbotService.Dispose() Kills the
        // child process tree + clears the shared LlamaSessionKey; the JobObject stays
        // the backstop, not the mechanism.
        try { _mistral.Dispose(); } catch { }

        // LOGIC-03: drain the engine's background persist queue so the last enqueued
        // profile snapshot reaches disk before process exit (production runs with
        // BackgroundPersistEnabled = true; see the ctor wiring).
        try { (_engine as MockInferenceEngine)?.FlushPendingPersists(); } catch { }
    }

    /// <summary>EXIT-05 (CW-11): cheap, idempotent, never-throwing best-effort flush of
    /// the in-memory daily behaviour aggregate to disk. Exposed for the crash / process-
    /// exit handlers (App.xaml.cs wires it later this wave) so a hard termination doesn't
    /// lose the ≤60 s behaviour tail accumulated since the last durability heartbeat. Safe
    /// to call from ANY thread and from a terminating handler: it delegates to the same
    /// fail-soft <see cref="FlushBehaviorAggregate"/> the 60 s timer + clean-exit path use
    /// (idempotent-by-date writer → repeated calls converge on one row). Q4.5-safe — writes
    /// telemetry only, never engine state.</summary>
    public void TryFlushBehaviorAggregateForCrash()
    {
        try { FlushBehaviorAggregate(); } catch { /* crash-path best-effort — never rethrow */ }
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
