using System;
using SenseEase.Core.Models;

namespace SenseEase.Core.Inference;

// ─────────────────────────────────────────────────────────────────────────────
// Habituation profile system — Phase 1 (POCO only).
//
// See docs/HABITUATION-PROFILE.md for the full design rationale, evidence base,
// and ordering. This file defines the data model only; the engine integration
// lands in Phase 2 (engine accepts params) and Phase 3 (engine state machine).
//
// Evidence base for the threshold choices:
//   - Barány Society Consensus on MS / VIMS diagnostic criteria (2023)
//   - Chang & Hain (2020): progressive TDP framework, phased calibration
//   - Pavlou (2010): intensive regimen 55 % dropout, supervised graded exposure
//   - VIMSSQ-short norms (Lukacova et al. 2023, 2025)
//
// Default profile: RESILIENT.
//   Rationale: untested users inherit historical baseline behaviour (which
//   matches the RESILIENT numerics exactly), while calibration / chatbot
//   override stratifies users INTO tighter profiles based on measured signal.
//   Defaulting to the tightest profile (SENSITIVE) would suppress long-term
//   habituation progress, contradicting the Chang-Hain framework.
//
// Unit convention (locked):
//   Internal engine state remains in SECONDS (preserves existing test
//   invariants and SenseDefaults numerics). This POCO exposes *Ms field names
//   to match the design spec and external JSON schema. Conversion happens ONCE
//   at the boundary, via the *Seconds helper properties below.
//
//   Rule: any field crossing the engine boundary uses seconds.
//         Any field touching JSON / spec / chatbot uses ms.
//         Never mix within the same scope.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Three-tier user reactivity profile. Determines how aggressively
/// the habituation engine relaxes the FOV mask over time, and how harshly it
/// reacts to spikes.</summary>
public enum HabituationProfile
{
    /// <summary>Default. Low reactivity / fast habituation. Numerics match
    /// the current SenseDefaults values exactly so existing tests pin.</summary>
    Resilient,

    /// <summary>Moderate reactivity. Slower relaxation, tighter cap.</summary>
    Typical,

    /// <summary>High reactivity. Cautious relaxation, low cap, strict
    /// strike limit, mid-level spikes get longer pauses. Level ≥ 10 spikes
    /// require explicit user confirmation to resume.</summary>
    Sensitive,
}

/// <summary>Severe-event handling mode. Only "halve" is used today; the "zero"
/// path triggers via SevereStrikeLimit overflow, not via this enum. Enum kept
/// as a future hook for profiles that might want immediate-zero on first
/// strike without a counter.</summary>
public enum SevereClearMode
{
    Halve,
    Zero,
}

/// <summary>Per-profile habituation tuning. Immutable record-like POCO; one
/// static instance per <see cref="HabituationProfile"/> exposes the canonical
/// values for the spec table in docs/HABITUATION-PROFILE.md §1.</summary>
public sealed class HabituationProfileParams
{
    /// <summary>Contiguous calm time required before the engine starts
    /// stepping the offset upward. SENSITIVE 6 min is intentionally slow.</summary>
    public int StableWindowMs { get; }

    /// <summary>Interval between successive relaxation steps once
    /// StableWindowMs is reached.</summary>
    public int StepIntervalMs { get; }

    /// <summary>FOV percentage points added per relaxation step.</summary>
    public double StepMagnitudePp { get; }

    /// <summary>Spike-recovery pause length for low-level spikes
    /// (intensity level ≤ 6). Engine resumes automatically after this.</summary>
    public int PostSpikePauseLowMs { get; }

    /// <summary>Spike-recovery pause length for mid-level spikes
    /// (intensity level 7–9). Engine resumes automatically after this.</summary>
    public int PostSpikePauseMidMs { get; }

    /// <summary>Hard ceiling on the accumulated FOV offset, in percentage
    /// points. SENSITIVE caps at 15 to prevent over-relaxation in users with
    /// high vection sensitivity.</summary>
    public int HabOffsetCapPp { get; }

    /// <summary>Adjustment-log sample count at which personalization weight
    /// reaches 1.0 (linear ramp from 0). Tighter profiles wait for more
    /// samples before trusting the user's correction history.</summary>
    public int MinSamplesForFullWeight { get; }

    /// <summary>How a severe event clears the offset. See <see cref="SevereClearMode"/>
    /// — only Halve is wired in Phase 3; the zero path is gated on strike-count
    /// overflow, not on this enum.</summary>
    public SevereClearMode SevereClearMode { get; }

    /// <summary>Severe-event count at which the next severe event zeros the
    /// offset (rather than halving) and resets the counter. SENSITIVE = 1
    /// means the first severe event after a profile reset zeros the offset.
    /// Counts are doubles so chatbot-derived severities can be weighted 0.5.</summary>
    public double SevereStrikeLimit { get; }

    // ── Boundary conversions for engine consumers ──
    // The engine uses seconds throughout (see file header). These helpers
    // perform the one-time conversion so engine code reads cleanly.

    public double StableWindowSeconds       => StableWindowMs       / 1000.0;
    public double StepIntervalSeconds       => StepIntervalMs       / 1000.0;
    public double PostSpikePauseLowSeconds  => PostSpikePauseLowMs  / 1000.0;
    public double PostSpikePauseMidSeconds  => PostSpikePauseMidMs  / 1000.0;

    private HabituationProfileParams(
        int stableWindowMs, int stepIntervalMs, double stepMagnitudePp,
        int postSpikePauseLowMs, int postSpikePauseMidMs,
        int habOffsetCapPp, int minSamplesForFullWeight,
        SevereClearMode severeClearMode, double severeStrikeLimit)
    {
        StableWindowMs           = stableWindowMs;
        StepIntervalMs           = stepIntervalMs;
        StepMagnitudePp          = stepMagnitudePp;
        PostSpikePauseLowMs      = postSpikePauseLowMs;
        PostSpikePauseMidMs      = postSpikePauseMidMs;
        HabOffsetCapPp           = habOffsetCapPp;
        MinSamplesForFullWeight  = minSamplesForFullWeight;
        SevereClearMode          = severeClearMode;
        SevereStrikeLimit        = severeStrikeLimit;
    }

    /// <summary>The RESILIENT profile — engine default. Numerics match current
    /// SenseDefaults values exactly so existing tests pass unchanged when the
    /// engine is rewired to read from _params in Phase 2.</summary>
    public static readonly HabituationProfileParams Resilient = new(
        stableWindowMs:           120_000,
        stepIntervalMs:            30_000,
        stepMagnitudePp:               2.0,
        postSpikePauseLowMs:       90_000,
        postSpikePauseMidMs:      120_000,
        habOffsetCapPp:                30,
        minSamplesForFullWeight:        3,
        severeClearMode:                SevereClearMode.Halve,
        severeStrikeLimit:              2);

    /// <summary>The TYPICAL profile — moderately cautious. Calibration-demo
    /// users with peak pressure &gt; 50 OR mask activations ≥ 1 land here.</summary>
    public static readonly HabituationProfileParams Typical = new(
        stableWindowMs:           180_000,
        stepIntervalMs:            45_000,
        stepMagnitudePp:               1.5,
        postSpikePauseLowMs:       90_000,
        postSpikePauseMidMs:      180_000,
        habOffsetCapPp:                25,
        minSamplesForFullWeight:        4,
        severeClearMode:                SevereClearMode.Halve,
        severeStrikeLimit:              2);

    /// <summary>The SENSITIVE profile — very cautious. Calibration-demo users
    /// with peak pressure &gt; 85 OR mask activations ≥ 3 land here. Strike
    /// limit 1 means the FIRST severe event after a profile reset zeros the
    /// offset (instead of halving twice before zeroing).</summary>
    public static readonly HabituationProfileParams Sensitive = new(
        stableWindowMs:           360_000,
        stepIntervalMs:            60_000,
        stepMagnitudePp:               1.0,
        postSpikePauseLowMs:      180_000,
        postSpikePauseMidMs:      300_000,
        habOffsetCapPp:                15,
        minSamplesForFullWeight:        6,
        severeClearMode:                SevereClearMode.Halve,
        severeStrikeLimit:              1);

    /// <summary>Resolve a profile enum value to its parameter set.</summary>
    public static HabituationProfileParams For(HabituationProfile profile) => profile switch
    {
        HabituationProfile.Resilient => Resilient,
        HabituationProfile.Typical   => Typical,
        HabituationProfile.Sensitive => Sensitive,
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    /// <summary>Resolve the per-profile starting <see cref="ShaderParams"/> the
    /// engine should re-seed when a user is (re)classified into a profile —
    /// backlog A7, the "what overlay do we open with?" question.
    /// <list type="bullet">
    ///   <item>RESILIENT / TYPICAL → the neutral calibrated baseline
    ///         (<see cref="ShaderParams.Default"/>): MaskDepth 0.74,
    ///         GridVisibility 0.75, FeatherWidth 0.50. These are read straight
    ///         from <see cref="ShaderParams"/> so the literals live in exactly
    ///         one place; if the baseline retunes, both profiles follow.</item>
    ///   <item>SENSITIVE → a tightened start: lower MaskDepth (0.80 mask
    ///         opacity… see note), reduced GridVisibility 0.60, and a softer
    ///         FeatherWidth 0.55. The intent is a calmer, less stimulating
    ///         opening overlay for high-reactivity users; long-term habituation
    ///         still relaxes from here per the engine state machine.</item>
    /// </list>
    /// All values are pushed through <see cref="ShaderParams.Clamp"/> so the
    /// result is guaranteed in-range [0,1] even if the spec literals drift.
    /// This method is PURE: it returns a fresh, mutable instance the caller owns
    /// and never mutates any shared static state.</summary>
    /// <param name="profile">The reactivity profile to seed defaults for.</param>
    /// <returns>A fresh, clamped <see cref="ShaderParams"/> the engine may seed
    /// directly. Never returns a shared singleton — safe for the caller to keep
    /// and mutate.</returns>
    public static ShaderParams ResolveDefaultsFor(HabituationProfile profile)
    {
        // RESILIENT / TYPICAL inherit the neutral baseline verbatim. Reading from
        // ShaderParams.Default (rather than re-stating 0.74 / 0.75 / 0.50) keeps a
        // single source of truth for the baseline numerics.
        if (profile != HabituationProfile.Sensitive)
        {
            return ShaderParams.Default;
        }

        // SENSITIVE — tightened opening overlay for high-reactivity users.
        var sensitive = new ShaderParams
        {
            MaskDepth      = 0.80,
            GridVisibility = 0.60,
            FeatherWidth   = 0.55,
        };
        sensitive.Clamp(); // defensive: keep in [0,1] even if literals later drift
        return sensitive;
    }

    /// <summary>Level-aware post-spike pause selector (native 12-level scale,
    /// rescaled from the prior 1..10 thresholds — same top-3 / mid-3 proportions).
    /// <list type="bullet">
    ///   <item>Level ≤ 6 → returns <see cref="PostSpikePauseLowMs"/>, requireUserConfirm = false</item>
    ///   <item>Level 7–9 → returns <see cref="PostSpikePauseMidMs"/>, requireUserConfirm = false</item>
    ///   <item>Level ≥ 10 → returns -1, requireUserConfirm = true (engine enters
    ///         PausedAwaitingUserConfirm and stays there until AcknowledgeAndResume)</item>
    /// </list>
    /// The top-level user-confirm gate is a safety floor: under severe symptoms
    /// the engine refuses to silently resume on a timer; the user (or the
    /// chatbot on their explicit behalf) must affirm they're ready.</summary>
    public int GetPostSpikePauseMs(int vimsTier, out bool requireUserConfirm)
    {
        if (vimsTier >= 10)
        {
            requireUserConfirm = true;
            return -1;
        }
        requireUserConfirm = false;
        return vimsTier >= 7 ? PostSpikePauseMidMs : PostSpikePauseLowMs;
    }
}
