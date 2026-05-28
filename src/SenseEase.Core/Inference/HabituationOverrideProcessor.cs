using System;
using SenseEase.Core.Models;

namespace SenseEase.Core.Inference;

// ─────────────────────────────────────────────────────────────────────────────
// Phase 7c — pure-logic validation for a chatbot-proposed engine override.
//
// Takes the parsed ProfileOverrideProposal (from Phase 7b) plus the user's
// current persisted state and returns an OverrideValidationResult: either
// IsAccepted=true (caller surfaces the Card-embedded confirm UI per Phase 7e)
// or IsAccepted=false with a reason the caller logs.
//
// Mirrors CalibrationProcessor pattern: no I/O, no UI, no engine writes.
// The Applicator (Phase 7d) performs writes only AFTER user explicitly
// confirms in the Card UI; the Processor is the gate that decides whether
// to surface the Card at all.
//
// Validation rules per spec §4.3.3:
//   1. expires_at_iso is in the future AND not more than 7 days out
//      (longer is treated as LLM hallucination)
//   2. proposed_value is in the intent's valid range
//      (personal_pressure_scale ∈ [0.7, 1.5]; profile ∈ enum;
//       severe_strike_limit ∈ [1, 5])
//   3. If user has Vestibular medical advisory AND the proposal would relax
//      the engine (lower trigger / less-restrictive profile / higher strike
//      limit) → REJECT
//   4. If another proposal is currently pending (Card open with un-acted
//      proposal) → REJECT (spam guard)
//   5. If the user declined the same intent kind within the last 7 days
//      → REJECT (don't badger)
//
// Rules 4 and 5 require state outside HabituationProfileSnapshot:
//   • Rule 4: tracked by the UI / pending-proposal manager
//   • Rule 5: tracked by ChatbotActionLog (Phase 7g)
// Both surface as optional parameters with a null default — callers in
// tests can ignore them; production callers wire them at the seam.
//
// Returns OverrideValidationResult.SafetyNote when the rejection has a
// safety-aware nuance the chatbot should be told about (rule 3 = vestibular
// hard cap). The Card UI shows this note next to the rejection so the user
// understands the system isn't randomly stonewalling them.
// ─────────────────────────────────────────────────────────────────────────────

public static class HabituationOverrideProcessor
{
    /// <summary>Maximum future window for expires_at_iso. Anything past this
    /// is treated as LLM hallucination (spec §4.3.3 rule 1).</summary>
    public const int ExpiresAtMaxFutureDays = 7;

    /// <summary>Valid range for personal_pressure_scale when intent =
    /// AdjustSensitivity. Matches CalibrationProcessor.PersonalPressureScale[Min|Max].</summary>
    public const double PersonalPressureScaleMin = 0.7;
    public const double PersonalPressureScaleMax = 1.5;

    /// <summary>Valid range for severe_strike_limit when intent =
    /// AdjustStrikeLimit. Below 1 = engine pauses immediately on any spike
    /// (unhelpful); above 5 = effectively unlimited (defeats the limit).</summary>
    public const int SevereStrikeLimitMin = 1;
    public const int SevereStrikeLimitMax = 5;

    /// <summary>Cooldown window for rule 5: if the user declined the same
    /// intent within this window, the new proposal is auto-rejected.</summary>
    public static readonly TimeSpan RecentDeclineCooldown = TimeSpan.FromDays(7);

    /// <summary>Validate a chatbot proposal against the user's current state.
    /// Pure logic — no side effects.</summary>
    /// <param name="proposal">Parsed proposal from Phase 7b.</param>
    /// <param name="currentSnapshot">User's persisted habituation profile.
    /// Null = no snapshot loaded (pre-calibration); rule 3 (vestibular) is
    /// skipped in that case.</param>
    /// <param name="isAnotherProposalPending">True iff the UI already has an
    /// un-acted Card-embedded proposal visible. Wired by the host. Null/false
    /// = rule 4 skipped (test contexts).</param>
    /// <param name="mostRecentDeclineFor">Returns the timestamp of the most
    /// recent user decline for the proposal's intent, or null if never
    /// declined. Wired to ChatbotActionLog at the seam. Null = rule 5
    /// skipped.</param>
    /// <param name="now">Test seam for the current UTC time. Defaults to
    /// <see cref="DateTime.UtcNow"/>.</param>
    public static OverrideValidationResult Validate(
        ProfileOverrideProposal proposal,
        HabituationProfileSnapshot? currentSnapshot,
        bool isAnotherProposalPending = false,
        Func<OverrideIntent, DateTime?>? mostRecentDeclineFor = null,
        DateTime? now = null)
    {
        DateTime utcNow = now ?? DateTime.UtcNow;

        // ── Rule 1: expires_at_iso in [now, now + 7 days] ──
        if (proposal.ExpiresAtUtc <= utcNow)
        {
            return OverrideValidationResult.Reject(
                "proposal expiry is in the past");
        }
        if (proposal.ExpiresAtUtc > utcNow + TimeSpan.FromDays(ExpiresAtMaxFutureDays))
        {
            return OverrideValidationResult.Reject(
                $"proposal expiry > {ExpiresAtMaxFutureDays} days out (likely LLM hallucination)");
        }

        // ── Rule 2: proposed_value in valid range for the intent ──
        if (!IsValueInRange(proposal, out string? rangeReason))
        {
            return OverrideValidationResult.Reject(rangeReason!);
        }

        // ── Rule 3: vestibular advisory + relaxation = HARD REJECT ──
        if (currentSnapshot is not null
            && currentSnapshot.MedicalAdvisoryShown == MedicalAdvisory.Vestibular
            && IsRelaxationProposal(proposal, currentSnapshot))
        {
            return OverrideValidationResult.Reject(
                rejectionReason: "vestibular advisory active; relaxation proposals require manual Settings change",
                safetyNote: "Your calibration flagged a vestibular factor. Relaxing the engine should be a deliberate Settings change, not chatbot-routed.");
        }

        // ── Rule 4: one proposal at a time ──
        if (isAnotherProposalPending)
        {
            return OverrideValidationResult.Reject(
                "another chatbot proposal is already pending — please respond to it first");
        }

        // ── Rule 5: don't badger — 7-day cooldown after a decline ──
        if (mostRecentDeclineFor is not null)
        {
            DateTime? last = mostRecentDeclineFor(proposal.Intent);
            if (last.HasValue && (utcNow - last.Value) < RecentDeclineCooldown)
            {
                return OverrideValidationResult.Reject(
                    $"same intent declined within the last {ExpiresAtMaxFutureDays} days; cooldown in effect");
            }
        }

        return OverrideValidationResult.Accept();
    }

    // ── Value-range checks ───────────────────────────────────────────────────

    private static bool IsValueInRange(
        ProfileOverrideProposal proposal,
        out string? rejectionReason)
    {
        switch (proposal.Intent)
        {
            case OverrideIntent.AdjustSensitivity:
            {
                if (!TryUnwrapDouble(proposal.ProposedValue, out double scale))
                {
                    rejectionReason = "proposed_value for adjust_sensitivity must be numeric";
                    return false;
                }
                if (scale < PersonalPressureScaleMin || scale > PersonalPressureScaleMax)
                {
                    rejectionReason = $"personal_pressure_scale {scale:F2} out of range [{PersonalPressureScaleMin:F2}, {PersonalPressureScaleMax:F2}]";
                    return false;
                }
                rejectionReason = null;
                return true;
            }

            case OverrideIntent.SwitchProfile:
            {
                if (proposal.ProposedValue is not string s || string.IsNullOrWhiteSpace(s))
                {
                    rejectionReason = "proposed_value for switch_profile must be a profile name string";
                    return false;
                }
                if (!Enum.TryParse<HabituationProfile>(s, ignoreCase: true, out _))
                {
                    rejectionReason = $"unknown profile '{s}'";
                    return false;
                }
                rejectionReason = null;
                return true;
            }

            case OverrideIntent.AdjustStrikeLimit:
            {
                if (!TryUnwrapInt(proposal.ProposedValue, out int limit))
                {
                    rejectionReason = "proposed_value for adjust_strike_limit must be an integer";
                    return false;
                }
                if (limit < SevereStrikeLimitMin || limit > SevereStrikeLimitMax)
                {
                    rejectionReason = $"severe_strike_limit {limit} out of range [{SevereStrikeLimitMin}, {SevereStrikeLimitMax}]";
                    return false;
                }
                rejectionReason = null;
                return true;
            }

            default:
                // Unreachable in practice — parser already filters unknown intents.
                rejectionReason = $"unsupported intent {proposal.Intent}";
                return false;
        }
    }

    private static bool TryUnwrapDouble(object value, out double result)
    {
        switch (value)
        {
            case double d:  result = d;        return true;
            case float f:   result = f;        return true;
            case long l:    result = l;        return true;
            case int i:     result = i;        return true;
            case string s when double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed):
                            result = parsed;   return true;
            default:        result = 0;        return false;
        }
    }

    private static bool TryUnwrapInt(object value, out int result)
    {
        switch (value)
        {
            case int i:    result = i;          return true;
            case long l when l >= int.MinValue && l <= int.MaxValue:
                           result = (int)l;     return true;
            case double d when d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue:
                           result = (int)d;     return true;
            case string s when int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed):
                           result = parsed;     return true;
            default:       result = 0;          return false;
        }
    }

    // ── Relaxation detection (rule 3 + Phase 7f defensive intercept) ─────────

    /// <summary>Returns true iff the proposal would relax the engine relative
    /// to the current snapshot. "Relax" = move the comfort intervention in
    /// the direction of LESS protection (higher trigger threshold / less-
    /// restrictive profile / higher strike limit). Phase 7f intercept reuses
    /// this same predicate in the Applicator.</summary>
    public static bool IsRelaxationProposal(
        ProfileOverrideProposal proposal,
        HabituationProfileSnapshot snapshot)
    {
        switch (proposal.Intent)
        {
            case OverrideIntent.AdjustSensitivity:
                // Lower scale = higher effective trigger = less sensitive = relaxation.
                if (TryUnwrapDouble(proposal.ProposedValue, out double scale))
                    return scale < snapshot.PersonalPressureScale;
                return false;

            case OverrideIntent.SwitchProfile:
                if (proposal.ProposedValue is string s
                    && Enum.TryParse<HabituationProfile>(s, ignoreCase: true, out var proposedProfile)
                    && Enum.TryParse<HabituationProfile>(snapshot.Profile, ignoreCase: true, out var currentProfile))
                {
                    return IsLessRestrictive(proposedProfile, currentProfile);
                }
                return false;

            case OverrideIntent.AdjustStrikeLimit:
                // Higher strike limit = more strikes tolerated = relaxation.
                if (TryUnwrapInt(proposal.ProposedValue, out int limit))
                    return limit > (int)snapshot.SevereStrikeLimit;
                return false;

            default:
                return false;
        }
    }

    private static bool IsLessRestrictive(HabituationProfile proposed, HabituationProfile current)
    {
        // Profile order from least to most restrictive: Resilient → Typical → Sensitive.
        // "Less restrictive" = lower index in this order.
        static int Order(HabituationProfile p) => p switch
        {
            HabituationProfile.Resilient => 0,
            HabituationProfile.Typical   => 1,
            HabituationProfile.Sensitive => 2,
            _                            => 1,
        };
        return Order(proposed) < Order(current);
    }
}

/// <summary>Result of <see cref="HabituationOverrideProcessor.Validate"/>.
/// IsAccepted=true means the proposal is safe to surface in the Card UI;
/// false means the caller should log the rejection (and optionally inform
/// the chatbot via event log so it doesn't re-propose). SafetyNote is
/// populated only when the rejection has a safety-aware nuance the user
/// should see (e.g. vestibular hard cap).</summary>
public sealed record OverrideValidationResult(
    bool IsAccepted,
    string? RejectionReason,
    string? SafetyNote)
{
    public static OverrideValidationResult Accept() =>
        new(IsAccepted: true, RejectionReason: null, SafetyNote: null);

    public static OverrideValidationResult Reject(string rejectionReason, string? safetyNote = null) =>
        new(IsAccepted: false, RejectionReason: rejectionReason, SafetyNote: safetyNote);
}
