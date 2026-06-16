using System;

namespace SenseEase.Core.Adaptation;

/// <summary>
/// B5 — the deterministic safety gate for LLM-proposed protection adaptation ("the LLM proposes,
/// deterministic code disposes"). This is the TRUSTED authority: a pure function that takes an
/// UNTRUSTED direction hint from the chatbot's retrospective analysis plus a <see cref="MeasuredState"/>
/// assembled from data the system actually measured, and decides whether — and by how much — to move
/// the auto-adaptation channel on <c>EffectiveTrigger</c>.
///
/// <para><b>The whole point is that the LLM influences DIRECTION ONLY.</b> The gate sets the magnitude
/// (always one minimum step), recomputes every piece of evidence from measured data, and enforces the
/// asymmetric / floor / clamp / rate / cold-start policy. A hallucinated or injected "relax, the user
/// is fine" cannot relax protection unless the measured data independently supports it.</para>
///
/// <para><b>Sign convention (read this before touching the math).</b> <c>EffectiveTrigger</c> is the
/// pressure at which the mask trips (<c>pressure &gt;= effTrigger</c>): LOWER trigger = fires sooner =
/// MORE protection. So the auto-channel delta is signed: <b>negative = tighten (more protection),
/// positive = relax (less protection)</b>. This sign is the exact place a perverse-protection bug
/// hides (it bit B1 and B2); every relax path is therefore guarded and every direction is asserted in
/// the tests.</para>
///
/// <para>No I/O, no engine references, no clock — fully unit-testable. Scope (Stage 1): the pressure
/// axis (<c>EffectiveTrigger</c>) only; the bounded selector band-bias axis is a later stage within
/// the SAME budget framework. The §6 <b>composed slew limit</b> (cap the per-smoothing-window tier/FOV
/// change an auto-shift causes) is NOT enforced here — it needs live tier/FOV/window state the pure
/// gate has no access to, so it is the COMPOSE/wiring stage's responsibility; the gate's
/// <see cref="GateDecision.Apply.ResultingEffectiveTrigger"/> is the TARGET that layer applies subject
/// to the slew cap.</para>
/// </summary>
public static class AdaptationGate
{
    /// <summary>One conservative evaluation. Returns either an <see cref="GateDecision.Apply"/> (a new
    /// clamped auto-delta + the resulting EffectiveTrigger) or a <see cref="GateDecision.Hold"/> with a
    /// machine-readable reason. Never throws on ordinary inputs; the App composes the user-facing
    /// in-chat notice from the decision.</summary>
    public static GateDecision Evaluate(AdaptationHint hint, MeasuredState s, AdaptationGateConfig cfg)
    {
        if (s is null || cfg is null) return new GateDecision.Hold("no state");
        hint ??= new AdaptationHint(AdaptDirection.Hold, string.Empty);

        // Fail CLOSED on non-finite measured inputs. NaN makes every comparison below fall open toward
        // RELAX (NaN > x and NaN <= x are both false in IEEE/C#), which is the worst failure mode for an
        // under-protection-harm system, so a garbage upstream value (e.g. p75 of an empty window) must
        // never leak a relax. FMS / trigger / delta / net-relax must be finite; the two timing fields may
        // legitimately be +Infinity ("none" — which only makes relax HARDER), but never NaN or −Infinity.
        if (!double.IsFinite(s.WorstRecentFmsOn) || !double.IsFinite(s.CurrentAutoDeltaPp) ||
            !double.IsFinite(s.BaseEffectiveTrigger) || !double.IsFinite(s.NetRelaxTodayPp) ||
            double.IsNaN(s.HoursSinceLastDiscomfort) || s.HoursSinceLastDiscomfort == double.NegativeInfinity ||
            double.IsNaN(s.DaysSinceLastAutoChange) || s.DaysSinceLastAutoChange == double.NegativeInfinity)
            return new GateDecision.Hold("non-finite-state");

        // A discomfort/worsening signal forbids relaxation this evaluation — the monotonicity invariant.
        // (Worsening behaviour OR a fresh discomfort report within the suppression window.)
        bool discomfortSignal =
            s.BehaviorWorsening ||
            s.HoursSinceLastDiscomfort <= cfg.RelaxSuppressAfterDiscomfortHrs;

        // (A) INDEPENDENT, DATA-DRIVEN TIGHTEN — highest priority, NOT gated on the LLM.
        // Measured worsening tightens immediately (the safe direction); this also closes the
        // "inject a HOLD to suppress a needed tighten" hole. Bypasses the relax rate-limit (we must
        // respond to real worsening now) but never exceeds the cumulative budget.
        if (s.BehaviorWorsening)
            return Tighten(s, cfg, "measured-worsening");

        // (B) LLM-proposed TIGHTEN — low bar (tightening is safe + reversible). Still budget-capped,
        // and subject to the per-week change cap so it can't churn the overlay endlessly.
        if (hint.Direction == AdaptDirection.Tighten)
        {
            if (s.AutoChangesThisWeek >= cfg.MaxAutoChangesPerWeek)
                return new GateDecision.Hold("weekly-change-cap");
            return Tighten(s, cfg, "tighten-proposed");
        }

        // (C) LLM-proposed RELAX — the high bar. Every condition below must hold.
        if (hint.Direction == AdaptDirection.Relax)
        {
            if (discomfortSignal)
                return new GateDecision.Hold("discomfort-signal-relax-suppressed");

            string? reject = RelaxReject(s, cfg);
            if (reject is not null) return new GateDecision.Hold(reject);

            return Relax(s, cfg);
        }

        return new GateDecision.Hold("no-actionable-proposal");
    }

    /// <summary>All RELAX preconditions, in order. Returns a machine reason on the FIRST failure, or
    /// null when relaxation is permitted. Order is cheapest/strongest-first so the reason is meaningful.</summary>
    private static string? RelaxReject(MeasuredState s, AdaptationGateConfig cfg)
    {
        // Cohort: vestibular never auto-relaxes (replicates the existing relaxation intercept the gate
        // would otherwise bypass). SENSITIVE may never NET-increase the trigger (handled in Relax()).
        if (s.Cohort == Cohort.Vestibular)
            return "vestibular-no-auto-relax";

        // Cold-start ramp (not a cliff): the FIRST ever auto-relax needs a margin above steady-state.
        if (!s.HasEverAutoRelaxed)
        {
            if (s.BehaviourDays < cfg.ColdStartMinBehaviourDays)
                return "cold-start-behaviour-days";
            if (s.MeasuredOnReportCount < cfg.ColdStartMinFmsReports)
                return "cold-start-fms-reports";
        }
        else if (s.MeasuredOnReportCount < cfg.MinFmsForRelaxSteadyState)
        {
            return "insufficient-measured-reports";
        }

        // Coverage: silence ≠ comfort. Reject clustered / sparse reporting.
        if (!s.FmsCoverageOk)
            return "fms-coverage-insufficient";

        // Worst-case ON-state FMS: one fresh bad episode blocks relax (no mean-dilution). INCLUSIVE
        // (>=): a worst-case sitting exactly at the "low" ceiling is still discomfort — fail toward
        // protection, never relax at the boundary.
        if (s.WorstRecentFmsOn >= cfg.RelaxFmsLowThreshold)
            return "recent-fms-too-high";

        // Behaviour trend must be stable/improving AND reflect tolerated exposure, not avoidance.
        if (!s.TrendStableOrImproving)
            return "trend-not-stable";
        if (s.ExposureDeclining || !s.ExposureAtOrAboveBaseline)
            return "insufficient-tolerated-exposure"; // the avoidance signature

        // Rate limits: spacing vs ANY prior auto-change (either direction), weekly cap, per-day net relax.
        if (s.DaysSinceLastAutoChange < cfg.RelaxMinIntervalDays)
            return "relax-min-interval";
        if (s.AutoChangesThisWeek >= cfg.MaxAutoChangesPerWeek)
            return "weekly-change-cap";
        if (s.NetRelaxTodayPp + cfg.AutoTriggerStepPp > cfg.MaxNetRelaxPerDayPp)
            return "daily-net-relax-cap";

        return null;
    }

    /// <summary>Apply one minimum RELAX step (delta moves toward +, trigger UP, LESS protection),
    /// clamped to the cumulative budget and the SENSITIVE no-net-increase floor.</summary>
    private static GateDecision Relax(MeasuredState s, AdaptationGateConfig cfg)
    {
        double newDelta = s.CurrentAutoDeltaPp + cfg.AutoTriggerStepPp;

        // Cumulative budget ceiling (vs the fixed opt-in baseline, which IS BaseEffectiveTrigger's
        // anchor — the budget is on the auto-delta itself, so it can't drift with a moving baseline).
        if (newDelta > cfg.AutoTriggerSpanPp)
            newDelta = cfg.AutoTriggerSpanPp;

        // SENSITIVE: the auto-channel may never NET-increase the trigger (delta must stay <= 0). A relax
        // that would push delta positive is blocked; unwinding a prior auto-tighten toward 0 is allowed.
        bool sensitiveClamped = false;
        if (s.Cohort == Cohort.Sensitive && newDelta > 0.0)
        {
            newDelta = Math.Min(0.0, s.CurrentAutoDeltaPp); // never above baseline for SENSITIVE
            sensitiveClamped = true;
        }

        if (newDelta <= s.CurrentAutoDeltaPp + Epsilon)
            return new GateDecision.Hold(sensitiveClamped ? "sensitive-no-net-relax" : "at-relax-budget");

        double resulting = s.BaseEffectiveTrigger + newDelta;
        return new GateDecision.Apply(newDelta, resulting, AdaptDirection.Relax, "relax-ok");
    }

    /// <summary>Apply one minimum TIGHTEN step (delta moves toward −, trigger DOWN, MORE protection),
    /// clamped to the cumulative budget. For vestibular the composed trigger is additionally capped at
    /// the vestibular floor (it must never end up weaker than the cohort override).</summary>
    private static GateDecision Tighten(MeasuredState s, AdaptationGateConfig cfg, string reason)
    {
        double newDelta = s.CurrentAutoDeltaPp - cfg.AutoTriggerStepPp;

        // Cumulative budget floor (symmetric with relax — bounds over-tightening / disorientation).
        if (newDelta < -cfg.AutoTriggerSpanPp)
            newDelta = -cfg.AutoTriggerSpanPp;

        if (newDelta >= s.CurrentAutoDeltaPp - Epsilon)
            return new GateDecision.Hold("at-tighten-budget");

        double resulting = s.BaseEffectiveTrigger + newDelta;

        // Vestibular defensive cap: the composed trigger must never exceed the vestibular floor (more
        // protection only). Tightening lowers the trigger, so this can only further protect — asserted
        // for completeness so a future baseline change can't sneak the composed value above the cap.
        if (s.Cohort == Cohort.Vestibular && resulting > cfg.VestibularTriggerCapPp)
            resulting = cfg.VestibularTriggerCapPp;

        return new GateDecision.Apply(newDelta, resulting, AdaptDirection.Tighten, reason);
    }

    /// <summary>B5 Stage 4 — map a chatbot-proposed sensitivity scale into a gate DIRECTION (the only
    /// thing the LLM is allowed to influence). The engine relation is
    /// <c>effectiveTrigger = TriggerPressure / scale</c>, so a HIGHER proposed scale = lower trigger =
    /// MORE protection = Tighten; a LOWER scale = Relax; equal = Hold. The magnitude of the proposed
    /// scale is intentionally discarded — the gate sizes the move, never the LLM.</summary>
    public static AdaptDirection DirectionFromSensitivityProposal(double proposedScale, double currentScale)
    {
        if (!double.IsFinite(proposedScale) || !double.IsFinite(currentScale)) return AdaptDirection.Hold;
        if (proposedScale > currentScale + Epsilon) return AdaptDirection.Tighten;
        if (proposedScale < currentScale - Epsilon) return AdaptDirection.Relax;
        return AdaptDirection.Hold;
    }

    private const double Epsilon = 1e-9;
}

/// <summary>Tighten = more protection; Relax = less protection; Hold = no change. The LLM may only
/// influence THIS (the direction), never the magnitude/axis/timing.</summary>
public enum AdaptDirection { Hold, Tighten, Relax }

/// <summary>The user's habituation cohort, as it bears on the protection floor.</summary>
public enum Cohort { Typical, Sensitive, Vestibular }

/// <summary>The UNTRUSTED proposal from the chatbot retrospective analysis: a direction only, plus a
/// render-only rationale string that NEVER re-enters any computation (it is sanitized display text for
/// the in-chat notice). Magnitude, axis, and timing are decided by the gate, not here.</summary>
public sealed record AdaptationHint(AdaptDirection Direction, string RationaleRenderOnly);

/// <summary>
/// Everything the gate TRUSTS — assembled by the App from measured data through a measured-only helper
/// (chat-derived <c>nl_daily</c> estimates are excluded by construction, so the FMS corroboration is
/// un-injectable). All fields are pre-aggregated so the gate stays pure; the gate applies the policy.
/// </summary>
public sealed record MeasuredState(
    Cohort Cohort,
    // ── Measured-only (fms_report), protection-ON FMS ────────────────────────────────────────────
    /// <summary>Count of ON-state MEASURED (fms_report) reports in the window. Excludes nl_daily.</summary>
    int MeasuredOnReportCount,
    /// <summary>Worst-case recent ON-state measured FMS (e.g. window p75/max), 0..20. One fresh bad
    /// episode keeps this high and blocks relax — no mean dilution.</summary>
    double WorstRecentFmsOn,
    /// <summary>Reports are spread (not clustered into one day) and silence does not dominate the window.</summary>
    bool FmsCoverageOk,
    // ── Behaviour trend (sensor-derived, un-injectable) ──────────────────────────────────────────
    /// <summary>ofi_p90 not rising AND spike_rate not worsening AND each trend half has enough days.</summary>
    bool TrendStableOrImproving,
    /// <summary>Late-window load (session minutes + OFI p90) is at or above the user's own baseline —
    /// positive evidence of TOLERATED exposure, not merely absence of distress.</summary>
    bool ExposureAtOrAboveBaseline,
    /// <summary>Exposure/OFI is declining — the avoidance signature. Blocks relax.</summary>
    bool ExposureDeclining,
    /// <summary>Measured worsening (ofi rising / spikes worsening) — drives the independent tighten.</summary>
    bool BehaviorWorsening,
    int BehaviourDays,
    // ── Protection / budget state ────────────────────────────────────────────────────────────────
    /// <summary>The pre-auto trigger (base/userScale). The auto-delta composes on top of this.</summary>
    double BaseEffectiveTrigger,
    /// <summary>The auto-channel's current signed contribution (negative = tighten, positive = relax).</summary>
    double CurrentAutoDeltaPp,
    // ── Timing (persisted across restarts by the caller) ─────────────────────────────────────────
    /// <summary>Hours since the last discomfort report (fms_report above threshold) OR manual tighten;
    /// +Infinity if none.</summary>
    double HoursSinceLastDiscomfort,
    /// <summary>Days since the last auto-change of EITHER direction; +Infinity if none.</summary>
    double DaysSinceLastAutoChange,
    int AutoChangesThisWeek,
    /// <summary>Net relax (pp) already applied today — for the per-day net-relax cap.</summary>
    double NetRelaxTodayPp,
    bool HasEverAutoRelaxed);

/// <summary>The §11 policy knobs (defaults mirror the sign-off table; the wiring stage may override
/// from SenseDefaults). Pure data so tests can dial each rule independently.</summary>
public sealed record AdaptationGateConfig(
    int MinFmsForRelaxSteadyState,
    int ColdStartMinBehaviourDays,
    int ColdStartMinFmsReports,
    double RelaxFmsLowThreshold,
    double RelaxMinIntervalDays,
    int MaxAutoChangesPerWeek,
    double RelaxSuppressAfterDiscomfortHrs,
    double AutoTriggerSpanPp,
    double AutoTriggerStepPp,
    double VestibularTriggerCapPp,
    double MaxNetRelaxPerDayPp)
{
    /// <summary>The signed-off defaults (B5 design §11). VestibularTriggerCapPp mirrors
    /// SenseDefaults.VestibularTriggerPressureOverride (50).</summary>
    public static AdaptationGateConfig Default => new(
        MinFmsForRelaxSteadyState: 5,
        ColdStartMinBehaviourDays: 14,
        ColdStartMinFmsReports: 8,
        RelaxFmsLowThreshold: 6.0,
        RelaxMinIntervalDays: 7.0,
        MaxAutoChangesPerWeek: 2,
        RelaxSuppressAfterDiscomfortHrs: 48.0,
        AutoTriggerSpanPp: 12.0,
        AutoTriggerStepPp: 3.0,
        VestibularTriggerCapPp: 50.0,
        MaxNetRelaxPerDayPp: 3.0);
}

/// <summary>The gate's verdict: either apply a new clamped auto-delta, or hold with a reason.</summary>
public abstract record GateDecision
{
    /// <summary>Apply the change. <paramref name="NewAutoDeltaPp"/> is the new signed auto-channel value
    /// (negative = more protection); <paramref name="ResultingEffectiveTrigger"/> is the composed trigger
    /// after the floor; <paramref name="Reason"/> is machine-readable (the App localizes the notice).</summary>
    public sealed record Apply(double NewAutoDeltaPp, double ResultingEffectiveTrigger,
                               AdaptDirection Direction, string Reason) : GateDecision;

    /// <summary>No change this evaluation; <paramref name="Reason"/> says which precondition held it.</summary>
    public sealed record Hold(string Reason) : GateDecision;
}
