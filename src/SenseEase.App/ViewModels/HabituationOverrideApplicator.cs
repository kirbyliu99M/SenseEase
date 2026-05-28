using System;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;

namespace SenseEase.App.ViewModels;

// ─────────────────────────────────────────────────────────────────────────────
// M3 Phase 7d + 7f — caller-side applicator for chatbot-proposed engine
// overrides. Composes:
//
//   • Phase 7f defensive intercept layer (CheckSafetyIntercepts): runs
//     INSIDE ApplyAccepted, BEFORE the engine write. Re-checks the
//     vestibular / high-risk-combo / hard-set-strike-limit conditions
//     even if the Processor (Phase 7c) cleared the proposal — because the
//     persisted snapshot may have changed between propose-time and accept-
//     time (user runs another calibration mid-Card etc.).
//
//   • Phase 7d engine write via MockInferenceEngine.ApplyChatbotOverride
//     (added alongside this file). Returns an ApplyResult the Card UI can
//     render: success → swap to confirmation, blocked → show SafetyNote.
//
//   • Phase 7g audit trail via IChatbotActionLog: every accept / decline /
//     expire / intercept is recorded for later Processor rule-5 cooldown
//     queries and for chatbot-context grounding.
//
//   • Phase 7g longitudinal-observation propagation: callers feed
//     [OBSERVATION_ADD] strings through LongitudinalObservationWriter
//     (constructed separately).
//
// Lives in the App layer (alongside CalibrationApplicator) because it
// orchestrates Infrastructure types — same architectural boundary as the
// existing calibration applicator.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class HabituationOverrideApplicator
{
    private readonly MockInferenceEngine _engine;
    private readonly IHabituationProfileStore _profileStore;
    private readonly IEventLogStore _eventLog;
    private readonly IChatbotActionLog _actionLog;

    public HabituationOverrideApplicator(
        MockInferenceEngine engine,
        IHabituationProfileStore profileStore,
        IEventLogStore eventLog,
        IChatbotActionLog actionLog)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _eventLog = eventLog ?? throw new ArgumentNullException(nameof(eventLog));
        _actionLog = actionLog ?? throw new ArgumentNullException(nameof(actionLog));
    }

    /// <summary>User clicked Accept on the Card-embedded proposal. Re-runs
    /// the defensive intercept (Phase 7f), performs the engine write
    /// (Phase 7d), and logs to the action log + event log.
    /// <para>Returns <see cref="ApplyResult.BlockedBySafety"/> with a reason
    /// if the intercept fires — the Card surfaces this so the user sees
    /// "your acceptance was overridden by safety policy". This is more
    /// transparent than silently no-op'ing on Accept.</para></summary>
    public ApplyResult ApplyAccepted(ProfileOverrideProposal proposal)
    {
        if (proposal is null) throw new ArgumentNullException(nameof(proposal));

        // Phase 7f: re-check safety BEFORE writing. The Processor's gate
        // ran at propose-time; the snapshot may have changed since then
        // (e.g. user ran a new calibration that flipped on a vestibular
        // advisory). Belt-and-suspenders cheap; engine writes are irreversible.
        var snapshot = SafeLoadSnapshot();
        var intercept = CheckSafetyIntercepts(proposal, snapshot);
        if (intercept.Blocked)
        {
            _eventLog.Append(new EventLogEntry
            {
                Category = "Habituation.SafetyIntercept",
                Message  = $"chatbot proposal blocked: {intercept.Reason}",
            });
            _actionLog.Record(ChatbotActionKind.Intercepted, proposal, intercept.Reason);
            return ApplyResult.Blocked(intercept.Reason!);
        }

        bool applied = _engine.ApplyChatbotOverride(proposal.Intent, proposal.ProposedValue);
        if (!applied)
        {
            // Defensive: engine couldn't apply (unparsable value etc.). The
            // processor should have caught this at propose-time; if we land
            // here something upstream is wrong. Log + report failure but
            // don't crash.
            _eventLog.Append(new EventLogEntry
            {
                Category = "Habituation.SafetyIntercept",
                Message  = $"chatbot proposal could not be applied: {proposal.Intent} = {proposal.ProposedValue}",
            });
            _actionLog.Record(ChatbotActionKind.Intercepted, proposal, "engine rejected value");
            return ApplyResult.Blocked("engine could not apply the proposed value");
        }

        _eventLog.Append(new EventLogEntry
        {
            Category = "Habituation.Profile",
            Message  = $"chatbot override applied: {proposal.Intent} = {proposal.ProposedValue} (rationale: {proposal.Rationale})",
        });
        _actionLog.Record(ChatbotActionKind.Accepted, proposal);
        return ApplyResult.Success();
    }

    /// <summary>User clicked Decline. Records the decline so the Processor's
    /// rule 5 (7-day cooldown for the same intent) sees it. No engine
    /// change.</summary>
    public void RecordDeclined(ProfileOverrideProposal proposal)
    {
        if (proposal is null) throw new ArgumentNullException(nameof(proposal));
        _actionLog.Record(ChatbotActionKind.Declined, proposal);
        _eventLog.Append(new EventLogEntry
        {
            Category = "Habituation.Profile",
            Message  = $"chatbot proposal declined: {proposal.Intent}",
        });
    }

    /// <summary>The proposal's expires_at_iso elapsed without user action.
    /// Records the expiry so the chatbot doesn't immediately re-propose
    /// the same thing (action-log read on next turn shows "expired
    /// recently"). No engine change.</summary>
    public void RecordExpired(ProfileOverrideProposal proposal)
    {
        if (proposal is null) throw new ArgumentNullException(nameof(proposal));
        _actionLog.Record(ChatbotActionKind.Expired, proposal);
        _eventLog.Append(new EventLogEntry
        {
            Category = "Habituation.Profile",
            Message  = $"chatbot proposal expired: {proposal.Intent}",
        });
    }

    // ── Phase 7f: defensive intercept layer ──────────────────────────────────

    /// <summary>Intercept hook visible for tests. Production code goes
    /// through ApplyAccepted; tests can exercise the intercept predicate
    /// directly.</summary>
    public static SafetyInterceptResult CheckSafetyIntercepts(
        ProfileOverrideProposal proposal,
        HabituationProfileSnapshot? snapshot)
    {
        if (snapshot is null) return SafetyInterceptResult.Passed;

        // Rule 1 (most specific first): strike limit was hard-set to 1 by
        // vestibular calibration → chatbot cannot override (vestibular hard
        // cap). Checked before the generic vestibular-relaxation rule so the
        // reason string is the specific one.
        if (proposal.Intent == OverrideIntent.AdjustStrikeLimit
            && (int)snapshot.SevereStrikeLimit == 1
            && snapshot.MedicalAdvisoryShown == MedicalAdvisory.Vestibular)
        {
            return SafetyInterceptResult.Block(
                "strike limit was hard-set by vestibular calibration; chatbot cannot override");
        }

        // Rule 2: Vestibular user + ANY relaxation proposal → block.
        if (snapshot.MedicalAdvisoryShown == MedicalAdvisory.Vestibular
            && HabituationOverrideProcessor.IsRelaxationProposal(proposal, snapshot))
        {
            return SafetyInterceptResult.Block(
                "vestibular advisory active; relaxation proposals require manual Settings change");
        }

        // Rule 3: High-risk-combo + SIGNIFICANT relaxation → block.
        // Threshold: 15% relative change in the relaxation direction.
        if (snapshot.MedicalAdvisoryShown == MedicalAdvisory.HighRiskCombo
            && IsSignificantRelaxation(proposal, snapshot, threshold: 0.15))
        {
            return SafetyInterceptResult.Block(
                "high-risk-combo advisory; small adjustments only via this channel");
        }

        return SafetyInterceptResult.Passed;
    }

    private static bool IsSignificantRelaxation(
        ProfileOverrideProposal proposal,
        HabituationProfileSnapshot snapshot,
        double threshold)
    {
        if (!HabituationOverrideProcessor.IsRelaxationProposal(proposal, snapshot))
            return false;

        switch (proposal.Intent)
        {
            case OverrideIntent.AdjustSensitivity:
            {
                // Magnitude of relative change in personal_pressure_scale.
                if (proposal.ProposedValue is not double newScale) return false;
                double oldScale = snapshot.PersonalPressureScale;
                if (oldScale <= 0) return true;
                return Math.Abs(oldScale - newScale) / oldScale >= threshold;
            }
            case OverrideIntent.AdjustStrikeLimit:
            {
                // Magnitude of strike-limit increase relative to current.
                int newLimit = proposal.ProposedValue switch
                {
                    int i => i, long l => (int)l, _ => 0,
                };
                int oldLimit = (int)snapshot.SevereStrikeLimit;
                if (oldLimit <= 0) return true;
                return ((double)(newLimit - oldLimit) / oldLimit) >= threshold;
            }
            case OverrideIntent.SwitchProfile:
                // A profile switch is by definition significant — there are
                // only three steps on the ladder.
                return true;
            default:
                return false;
        }
    }

    private HabituationProfileSnapshot? SafeLoadSnapshot()
    {
        try { return _profileStore.Load(); }
        catch { return null; } // corrupt file shouldn't crash the apply path
    }
}

/// <summary>Outcome of <see cref="HabituationOverrideApplicator.ApplyAccepted"/>.
/// <c>BlockedBySafety</c> is the Phase 7f intercept path; the Card UI
/// surfaces <c>Reason</c> to the user so they understand why their Accept
/// was overridden.</summary>
public sealed record ApplyResult(bool IsSuccess, bool BlockedBySafety, string? Reason)
{
    public static ApplyResult Success()
        => new(IsSuccess: true,  BlockedBySafety: false, Reason: null);
    public static ApplyResult Blocked(string reason)
        => new(IsSuccess: false, BlockedBySafety: true,  Reason: reason);
}

/// <summary>Result of the defensive intercept check. Exposed publicly so
/// the Card UI can call <see cref="HabituationOverrideApplicator.CheckSafetyIntercepts"/>
/// at propose-display-time too, to colour the Accept button differently
/// when the action would be intercepted (a courtesy preview, not a
/// gate).</summary>
public sealed record SafetyInterceptResult(bool Blocked, string? Reason)
{
    public static readonly SafetyInterceptResult Passed = new(Blocked: false, Reason: null);
    public static SafetyInterceptResult Block(string reason)
        => new(Blocked: true, Reason: reason);
}
