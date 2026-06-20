using SenseEase.Core.Configuration;

namespace SenseEase.Core.AdaptiveFit;

/// <summary>
/// The pure, time-advanced cooldown / lifecycle state machine that governs WHEN the
/// <see cref="DeterministicSetSelector"/> is allowed to act (Alpha-2 P3/P4 GROUP C).
///
/// <para>Lifecycle: <c>Default(set0,L5) → Settling → Active ⇄ Cooldown</c>.
/// <list type="bullet">
///   <item><b>Default → Settling</b>: the first <see cref="Advance"/> tick leaves the
///   fresh-start Default and begins accumulating settle time.</item>
///   <item><b>Settling → Active</b>: once at least <c>SettlingMinSeconds</c> have elapsed
///   AND there are at least <c>SelectorMinSamplesBeforeAnyMove</c> samples backing a
///   confident pick (passed into <see cref="Advance"/>).</item>
///   <item><b>Active ⇄ Cooldown</b>: a set change enters Cooldown for
///   <c>SetCooldownSeconds</c>; the timer elapsing returns to Active (soft-定型,
///   re-eligible).</item>
/// </list></para>
///
/// <para><b>Dual-trigger governance</b> (distinct, non-overlapping effects):
/// <list type="bullet">
///   <item><see cref="OnManualAdjust"/> — the user nudged the level: RESET the cooldown
///   timer to full but STAY in the current state (do not enter/leave Cooldown). The user's
///   action just defers the next auto-evaluation; it is not itself an auto set change.</item>
///   <item><see cref="OnSetChange"/> — an auto/applied set move: ENTER Cooldown with a fresh
///   <c>SetCooldownSeconds</c> timer.</item>
///   <item><see cref="OnProposalDeclined"/> — the user rejected a proposal: DOUBLE the
///   REMAINING cooldown (×<c>CooldownDeclineMultiplier</c>), backing the selector off harder
///   the more it is rejected. If not currently in Cooldown, this seeds a doubled cooldown
///   from the base so a decline always produces a real back-off.</item>
/// </list></para>
///
/// <para>This machine governs timing/eligibility ONLY. It NEVER touches the engine's
/// <c>_offset</c> (habituation credit) — that invariant is the whole reason it is a
/// dedicated type. Pure Core, deterministic, no I/O, no engine/WPF dependency; read only
/// behind the <c>AppConfig.AdaptiveFitSelectorEnabled</c> flag (default TRUE since c1461a9).</para>
/// </summary>
public sealed class AdaptiveFitStateMachine
{
    private readonly double _settlingMinSeconds;
    private readonly double _cooldownSeconds;
    private readonly int    _minSamples;
    private readonly double _declineMultiplier;

    // Seconds accumulated in Settling (gates Settling → Active alongside min-samples).
    private double _settleElapsed;

    // Seconds remaining on the Cooldown timer (>0 only while in Cooldown).
    private double _cooldownRemaining;

    /// <summary>The current lifecycle state. Starts at <see cref="CooldownState.Default"/>
    /// (fresh start, set0 / L5).</summary>
    public CooldownState CurrentState { get; private set; } = CooldownState.Default;

    /// <summary>True ONLY in <see cref="CooldownState.Active"/> — the single state in which
    /// the selector may evaluate a set move.</summary>
    public bool CanEvaluate => CurrentState == CooldownState.Active;

    /// <summary>Seconds left on the cooldown timer (0 outside Cooldown). Exposed for tests
    /// and diagnostics; not part of the lifecycle contract.</summary>
    public double CooldownRemaining => _cooldownRemaining;

    /// <summary>Construct with the provisional <see cref="SenseDefaults"/> timings.</summary>
    public AdaptiveFitStateMachine()
        : this(
            settlingMinSeconds: SenseDefaults.SettlingMinSeconds,
            cooldownSeconds:    SenseDefaults.SetCooldownSeconds,
            minSamples:         SenseDefaults.SelectorMinSamplesBeforeAnyMove,
            declineMultiplier:  SenseDefaults.CooldownDeclineMultiplier)
    {
    }

    /// <summary>Construct with explicit timings (used by tests to keep dt arithmetic small).</summary>
    public AdaptiveFitStateMachine(
        double settlingMinSeconds,
        double cooldownSeconds,
        int minSamples,
        double declineMultiplier)
    {
        _settlingMinSeconds = settlingMinSeconds;
        _cooldownSeconds    = cooldownSeconds;
        _minSamples         = minSamples;
        _declineMultiplier  = declineMultiplier;
    }

    /// <summary>
    /// Advance the machine by <paramref name="dt"/> seconds.
    /// </summary>
    /// <param name="dt">Elapsed seconds since the last advance (clamped at 0 — never rewinds).</param>
    /// <param name="sampleCount">Current window sample count (gates Settling → Active).</param>
    /// <param name="haveConfidentPick">Whether the selector currently has a confident, in-margin
    /// pick (the selector's confidence gate). Settling → Active needs both this AND min-samples
    /// AND the settle-time floor.</param>
    public void Advance(double dt, int sampleCount, bool haveConfidentPick)
    {
        if (dt < 0.0) dt = 0.0;

        switch (CurrentState)
        {
            case CooldownState.Default:
                // The fresh-start state leaves on the first tick and begins settling. This
                // tick's dt counts toward the settle floor so a single large dt can both
                // leave Default and (with samples + a confident pick) reach Active.
                CurrentState  = CooldownState.Settling;
                _settleElapsed = dt;
                TryPromoteFromSettling(sampleCount, haveConfidentPick);
                break;

            case CooldownState.Settling:
                _settleElapsed += dt;
                TryPromoteFromSettling(sampleCount, haveConfidentPick);
                break;

            case CooldownState.Active:
                // Nothing to time down in Active; evaluation eligibility is steady here.
                break;

            case CooldownState.Cooldown:
                _cooldownRemaining -= dt;
                if (_cooldownRemaining <= 0.0)
                {
                    // Soft-定型 elapsed — re-eligible.
                    _cooldownRemaining = 0.0;
                    CurrentState = CooldownState.Active;
                }
                break;
        }
    }

    /// <summary>Settling → Active iff the settle-time floor, the min-samples floor, and a
    /// confident pick are all satisfied.</summary>
    private void TryPromoteFromSettling(int sampleCount, bool haveConfidentPick)
    {
        if (_settleElapsed >= _settlingMinSeconds
            && sampleCount >= _minSamples
            && haveConfidentPick)
        {
            CurrentState = CooldownState.Active;
        }
    }

    /// <summary>
    /// The user manually adjusted the level. RESET the cooldown timer to full but STAY in the
    /// current state (do not enter or leave Cooldown). A manual adjust defers the next
    /// evaluation; it is not an auto set change.
    /// </summary>
    public void OnManualAdjust()
    {
        // Only meaningful while in Cooldown — there the user's nudge restarts the soft-定型
        // wait. In other states there is no timer to reset (Active has none; Settling is
        // gated by settle-time + samples, untouched here), so this is a no-op by design.
        if (CurrentState == CooldownState.Cooldown)
            _cooldownRemaining = _cooldownSeconds;
    }

    /// <summary>
    /// An auto/applied set move occurred. ENTER <see cref="CooldownState.Cooldown"/> with a
    /// fresh <c>SetCooldownSeconds</c> timer (soft-定型).
    /// </summary>
    public void OnSetChange()
    {
        CurrentState       = CooldownState.Cooldown;
        _cooldownRemaining = _cooldownSeconds;
    }

    /// <summary>
    /// The user declined a proposal. DOUBLE the remaining cooldown
    /// (×<c>CooldownDeclineMultiplier</c>) so repeated rejection backs the selector off fast.
    /// If not currently in Cooldown, this enters Cooldown seeded at the doubled base so a
    /// decline always yields a real, escalating back-off.
    /// </summary>
    public void OnProposalDeclined()
    {
        if (CurrentState == CooldownState.Cooldown && _cooldownRemaining > 0.0)
        {
            _cooldownRemaining *= _declineMultiplier;
        }
        else
        {
            // Decline outside an active cooldown still escalates: seed a doubled base wait.
            CurrentState       = CooldownState.Cooldown;
            _cooldownRemaining = _cooldownSeconds * _declineMultiplier;
        }
    }
}
