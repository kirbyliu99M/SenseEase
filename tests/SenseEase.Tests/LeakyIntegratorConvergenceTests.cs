using System;
using SenseEase.Core.Configuration;
using SenseEase.Core.Inference;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// D3 — convergence properties of the <see cref="LeakyIntegrator"/> recurrence
///   Pressure ← alpha(dt) · Pressure + stimulus,
/// where alpha(dt) = BaseDecayAlpha^(dt / 0.1) and
///       stimulus = ((gated·weight) + (QuadraticTerm·gated²))·dt.
///
/// Every expected value here is derived from the REAL <see cref="SenseDefaults"/>
/// constants (no transcribed magic numbers) so the suite tracks the constants if
/// they are re-tuned. Where a discrete-time quantity is an exact fixed point of the
/// recurrence we assert it exactly; where the discrete behaviour only approaches the
/// continuous-time analytic value as dt → 0 we assert a bound / ordering / exactness
/// invariant instead of a brittle magnitude.
/// </summary>
public class LeakyIntegratorConvergenceTests
{
    // Audit 2026-06-22: a non-finite OFI must not poison Pressure into a permanent NaN — `NaN >= trigger`
    // is false, so a poisoned Pressure would silently keep the mask from ever re-engaging. The entry
    // finite-guard + NaN-safe clamp keep Pressure finite and let a later normal sample re-accumulate.
    [Fact]
    public void NonFiniteOfi_DoesNotPoisonPressure()
    {
        var li = new LeakyIntegrator();
        li.Step(50.0, 1.0, 0.1);
        Assert.True(li.Pressure > 0);

        li.Step(double.NaN, 1.0, 0.1);
        Assert.True(double.IsFinite(li.Pressure));
        li.Step(double.PositiveInfinity, 1.0, 0.1);
        Assert.True(double.IsFinite(li.Pressure));

        double after = li.Step(50.0, 1.0, 0.1);   // a normal sample still accumulates — protection recovers
        Assert.True(double.IsFinite(after) && after > 0);
    }

    // ── Real constants under test (single source of truth = SenseDefaults) ──────
    private const double Alpha100  = SenseDefaults.BaseDecayAlpha;     // 100 ms reference decay
    private const double NoiseGate = SenseDefaults.NoiseGateThreshold; // sub-threshold OFI discarded
    private const double Quadratic = SenseDefaults.QuadraticTerm;      // nonlinear stimulus term
    private const double Trigger   = SenseDefaults.TriggerPressure;    // mask-activation pressure

    /// <summary>Decay multiplier the integrator applies for a given step size,
    /// mirroring <c>Math.Pow(_decayAlpha, dtSeconds / 0.1)</c> in production code.</summary>
    private static double AlphaForDt(double dt) => Math.Pow(Alpha100, dt / 0.1);

    /// <summary>Per-step stimulus the integrator adds, mirroring the gated, weighted,
    /// quadratic, dt-scaled term in <see cref="LeakyIntegrator.Step"/>.</summary>
    private static double StimulusPerStep(double ofi, double weight, double dt)
    {
        double gated = ofi < NoiseGate ? 0.0 : ofi;
        return ((gated * weight) + (Quadratic * gated * gated)) * dt;
    }

    /// <summary>Exact fixed point of the recurrence for a constant input:
    /// P* = stimulus / (1 − alpha(dt)). Solving P* = alpha·P* + stimulus.</summary>
    private static double DiscreteEquilibrium(double ofi, double weight, double dt)
        => StimulusPerStep(ofi, weight, dt) / (1.0 - AlphaForDt(dt));

    // ── 1. Sustained supra-threshold OFI reaches the trigger within bounded time ─
    [Fact]
    public void SustainedSupraThresholdFlow_ReachesTrigger_WithinBoundedTime()
    {
        // ofi=40, weight=1.0 (Driver-style), dt=0.1 s (≈10 Hz). Equilibrium for these
        // inputs sits well above Trigger, so sustained flow MUST cross it; analytically
        // the crossing is a few seconds in, so it is not instantaneous on step 1.
        var integrator = new LeakyIntegrator();

        double afterFirstStep = integrator.Step(40, 1.0, 0.1);
        Assert.True(afterFirstStep > 0, "a single supra-threshold step must charge some pressure");
        Assert.True(afterFirstStep < Trigger,
            "one step of moderate sustained flow must not already exceed the trigger");

        int stepsToTrigger = 1; // already took the first step above
        const int maxSteps = 400; // 40 s of headroom — generous upper bound
        while (integrator.Pressure < Trigger && stepsToTrigger < maxSteps)
        {
            integrator.Step(40, 1.0, 0.1);
            stepsToTrigger++;
        }

        Assert.True(integrator.Pressure >= Trigger,
            $"sustained flow must reach the trigger; got {integrator.Pressure} after {stepsToTrigger} steps");
        // Crossing should require sustained accumulation, not a single sample, but also
        // be comfortably within the headroom window.
        Assert.InRange(stepsToTrigger, 2, maxSteps - 1);
    }

    // ── 2. Equilibrium pressure matches the analytic fixed point exactly ─────────
    [Theory]
    [InlineData(100, 2.0, 0.1)] // Passenger-weight sustained flow at 10 Hz
    [InlineData(60, 1.0, 0.05)] // Driver-weight at 20 Hz
    [InlineData(140, 2.0, 0.2)] // stronger flow, coarser step
    public void SustainedInput_ConvergesToAnalyticDiscreteEquilibrium(double ofi, double weight, double dt)
    {
        double expectedEq = DiscreteEquilibrium(ofi, weight, dt);

        var integrator = new LeakyIntegrator();
        // Far more than the time-constant's worth of steps so the geometric series
        // has effectively settled onto the fixed point.
        for (int i = 0; i < 5000; i++)
        {
            integrator.Step(ofi, weight, dt);
        }

        // Monotone-from-below convergence (verified analytically): never overshoots,
        // and lands within a tight relative tolerance of the exact fixed point.
        Assert.True(integrator.Pressure <= expectedEq + 1e-6,
            "pressure converges to equilibrium strictly from below — it must not overshoot");
        Assert.Equal(expectedEq, integrator.Pressure, 3); // 3 decimal places
    }

    [Fact]
    public void EquilibriumValue_IsAnExactFixedPoint_OfTheRecurrence()
    {
        // Independent of any simulation: feeding the analytic equilibrium back through
        // one Step (after charging to it) reproduces the same value, confirming the
        // closed form stimulus/(1-alpha) is the true fixed point of the production code.
        const double ofi = 100, weight = 2.0, dt = 0.1;
        double eq = DiscreteEquilibrium(ofi, weight, dt);

        var integrator = new LeakyIntegrator();
        for (int i = 0; i < 5000; i++) integrator.Step(ofi, weight, dt);
        double before = integrator.Pressure;
        double after  = integrator.Step(ofi, weight, dt);

        // At the fixed point a further identical step does not move pressure.
        Assert.Equal(before, after, 6);
        Assert.Equal(eq, after, 3);
    }

    // ── 3. Time-constant is rate-invariant (same real-time result at different dt) ─
    [Fact]
    public void TimeConstant_IsRateInvariant_SameRealTimeWindowDifferentDt()
    {
        // The dt scaling (stimulus ∝ dt, alpha = alpha100^(dt/0.1)) is designed so a
        // 30 Hz and a 10 Hz provider accumulate at the same real-time rate. Over a fixed
        // wall-clock window the two pressures should agree closely. They are not bit-exact
        // (the quadratic term discretizes slightly differently per dt), so assert a small
        // relative band rather than equality — and assert it shrinks toward the continuous
        // limit as dt → 0.
        const double ofi = 50, weight = 2.0, realSeconds = 3.0;

        double Run(double dt)
        {
            var integ = new LeakyIntegrator();
            int n = (int)Math.Round(realSeconds / dt);
            for (int i = 0; i < n; i++) integ.Step(ofi, weight, dt);
            return integ.Pressure;
        }

        double coarse = Run(0.1);   // 10 Hz
        double fine   = Run(0.05);  // 20 Hz
        double finer  = Run(0.02);  // 50 Hz

        // All three describe the same 3 s of sustained flow: within a few percent.
        double relCoarseFine = Math.Abs(coarse - fine) / fine;
        double relFineFiner  = Math.Abs(fine - finer) / finer;
        Assert.True(relCoarseFine < 0.05,
            $"10 Hz vs 20 Hz over the same window must agree within 5%; rel={relCoarseFine}");
        // Finer sampling pair must agree at least as tightly (convergence toward the limit).
        Assert.True(relFineFiner <= relCoarseFine + 1e-9,
            $"finer dt pair must not diverge more than the coarser pair; {relFineFiner} vs {relCoarseFine}");
    }

    [Fact]
    public void DecayFactor_PerStep_ExactlyEqualsAlphaRaisedToDtOverReference()
    {
        // Pure-decay exactness: with gated input forced to zero, each idle step multiplies
        // pressure by exactly alpha(dt). This pins the time-constant law independently of
        // the stimulus term. Use dt=0.1 so alpha(dt) == BaseDecayAlpha exactly.
        const double dt = 0.1;
        double expectedAlpha = AlphaForDt(dt); // == Alpha100 at dt = 0.1

        var integrator = new LeakyIntegrator();
        integrator.Step(120, 2.0, dt);          // charge above the noise gate
        double charged = integrator.Pressure;
        Assert.True(charged > 0);

        double idle1 = integrator.Step(0, 2.0, dt); // 0 < NoiseGate ⇒ gated to 0 ⇒ pure decay
        double idle2 = integrator.Step(0, 2.0, dt);

        Assert.Equal(expectedAlpha, idle1 / charged, 9);
        Assert.Equal(expectedAlpha, idle2 / idle1, 9);
        Assert.Equal(Alpha100, idle1 / charged, 9); // and it is precisely the 100 ms reference
    }

    [Fact]
    public void DecayFactor_IsRateInvariant_AcrossDtForEqualElapsedTime()
    {
        // One 0.2 s decay step must equal two consecutive 0.1 s decay steps, because
        // alpha(0.2) = alpha(0.1)². This is the exactness guarantee behind rate-invariance.
        var big = new LeakyIntegrator();
        var small = new LeakyIntegrator();

        big.Step(150, 2.0, 0.1);
        small.Step(150, 2.0, 0.1);
        Assert.Equal(big.Pressure, small.Pressure, 9); // identical charge

        big.Step(0, 2.0, 0.2);   // one 200 ms decay
        small.Step(0, 2.0, 0.1); // two 100 ms decays
        small.Step(0, 2.0, 0.1);

        Assert.Equal(big.Pressure, small.Pressure, 9);
    }

    // ── 4. A one-second spike does NOT trigger ──────────────────────────────────
    [Fact]
    public void OneSecondSpike_OfModerateFlow_DoesNotReachTrigger()
    {
        // ofi=20, weight=1.0, dt=0.1 over exactly 1 s (10 steps). Equilibrium for these
        // inputs is below Trigger and only ~1/3 of the time-constant elapses, so the peak
        // stays well under the mask threshold: a brief jolt must not fire the mask.
        var integrator = new LeakyIntegrator();

        double peak = 0;
        for (int i = 0; i < 10; i++) // 10 × 0.1 s = 1.0 s
        {
            double p = integrator.Step(20, 1.0, 0.1);
            peak = Math.Max(peak, p);
        }

        Assert.True(peak > 0, "the spike must register SOME pressure");
        Assert.True(peak < Trigger,
            $"a one-second moderate spike must stay below the trigger; peaked at {peak}");
    }

    [Fact]
    public void OneSecondSpike_AsSingleUnitStep_DoesNotReachTrigger()
    {
        // A single coarse interaction worth 1 s of real time (dt = 1.0). With ofi=30,
        // weight=1.0 the lone step adds stimulus = 30 + 0.0008·900 = 30.72 < Trigger(85),
        // so one isolated burst cannot trip the mask even at the unit-step rate.
        var integrator = new LeakyIntegrator();

        double pressure = integrator.Step(30, 1.0, 1.0);

        double expected = StimulusPerStep(30, 1.0, 1.0); // exact: first step starts from 0
        Assert.Equal(expected, pressure, 9);
        Assert.True(pressure < Trigger,
            $"one unit-step spike must not reach the trigger; got {pressure}");
    }

    [Fact]
    public void SustainedFlow_EventuallyTriggers_WhereOneSecondSpikeDidNot()
    {
        // Contrast partner to the spike tests: the SAME ofi/weight that fails to trigger
        // in one second DOES trigger when sustained, proving the gate is time-integrative
        // rather than instantaneous. ofi=40, weight=1.0, dt=0.1.
        var spike = new LeakyIntegrator();
        double spikePeak = 0;
        for (int i = 0; i < 10; i++) // 1 s
            spikePeak = Math.Max(spikePeak, spike.Step(40, 1.0, 0.1));
        Assert.True(spikePeak < Trigger, "1 s is too short for ofi=40 to trigger");

        var sustained = new LeakyIntegrator();
        bool triggered = false;
        for (int i = 0; i < 400 && !triggered; i++) // up to 40 s
            triggered = sustained.Step(40, 1.0, 0.1) >= Trigger;

        Assert.True(triggered, "the same flow sustained long enough must eventually trigger");
        Assert.True(sustained.Pressure > spikePeak,
            "sustained accumulation must exceed the one-second spike peak");
    }
}
