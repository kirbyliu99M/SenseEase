using SenseEase.Core.Configuration;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Unit tests for the Phase-1 inference logic (Leaky Integrator + mask thresholding).</summary>
public class InferenceTests
{
    [Fact]
    public void NoiseGate_DiscardsSubThresholdFlow()
    {
        var integrator = new LeakyIntegrator();

        double pressure = integrator.Step(SenseDefaults.NoiseGateThreshold - 1, 1.0);

        Assert.Equal(0.0, pressure);
    }

    [Fact]
    public void LeakyIntegrator_AccumulatesPressureAcrossSteps()
    {
        var integrator = new LeakyIntegrator();

        double first = integrator.Step(100, 1.0);
        double second = integrator.Step(100, 1.0);

        Assert.True(first > 0);
        Assert.True(second > first);
    }

    [Fact]
    public void LeakyIntegrator_DecaysWhenInputStops()
    {
        var integrator = new LeakyIntegrator();
        for (int i = 0; i < 10; i++)
        {
            integrator.Step(120, 1.5);
        }

        double peak = integrator.Pressure;
        double afterIdle = integrator.Step(0, 1.5); // gated to zero -> only decay

        Assert.True(afterIdle < peak);
    }

    [Fact]
    public void MockInferenceEngine_ActivatesMaskAboveTriggerPressure()
    {
        var engine = new MockInferenceEngine();

        SenseState state = engine.Current;
        for (int i = 0; i < 200 && !state.MaskActive; i++)
        {
            state = engine.Update(140, "Passenger");
        }

        Assert.True(state.MaskActive);
        Assert.True(state.Pressure >= SenseDefaults.TriggerPressure);
        // FOV radius shrinks continuously with pressure once mask is active (35 → ≥15).
        int tightest = SenseDefaults.TightestNaturalFovRadiusPercent;
        Assert.InRange(state.FovRadiusPercent, tightest, SenseDefaults.DefaultFovRadiusPercent);
    }

    [Fact]
    public void PassengerMode_BuildsPressureFasterThanDriverMode()
    {
        var passenger = new MockInferenceEngine();
        var driver = new MockInferenceEngine();

        SenseState passengerState = passenger.Current;
        SenseState driverState = driver.Current;
        for (int i = 0; i < 5; i++)
        {
            passengerState = passenger.Update(80, "Passenger");
            driverState = driver.Update(80, "Driver");
        }

        Assert.True(passengerState.Pressure > driverState.Pressure);
    }
}
