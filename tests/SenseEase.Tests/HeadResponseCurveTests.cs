using SenseEase.App;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Beta-1 theme-4 fix (C): the head→screen deadzone + expo response curve. Pins that near-neutral
/// input produces no movement (kills jitter, #3), full deflection still reaches the edge (no lost
/// range), the curve is monotonic, and expo gives sub-linear (fine) control near the deadzone edge.
/// </summary>
public sealed class HeadResponseCurveTests
{
    private const double Dz = 0.08;
    private const double Expo = 1.4;

    [Fact]
    public void Centre_StaysCentre()
        => Assert.Equal(0.5, HeadResponseCurve.Shape(0.5, Dz, Expo), 9);

    [Theory]
    [InlineData(0.5)]    // exact centre
    [InlineData(0.52)]   // |dev|*2 = 0.04 < 0.08 deadzone
    [InlineData(0.48)]
    public void InsideDeadzone_MapsToCentre(double v)
        => Assert.Equal(0.5, HeadResponseCurve.Shape(v, Dz, Expo), 9);

    [Theory]
    [InlineData(1.0, 1.0)]   // full right deflection → full reach preserved
    [InlineData(0.0, 0.0)]   // full left deflection → full reach preserved
    public void FullDeflection_ReachesEdge(double v, double expected)
        => Assert.Equal(expected, HeadResponseCurve.Shape(v, Dz, Expo), 9);

    [Fact]
    public void IsMonotonic_AndSymmetric()
    {
        double prev = -1.0;
        for (double v = 0.5; v <= 1.0; v += 0.02)
        {
            double o = HeadResponseCurve.Shape(v, Dz, Expo);
            Assert.True(o >= prev - 1e-12, $"must be monotonic non-decreasing past centre at v={v}");
            prev = o;
            // symmetry about 0.5
            double mirror = HeadResponseCurve.Shape(1.0 - v, Dz, Expo);
            Assert.Equal(o - 0.5, 0.5 - mirror, 9);
        }
    }

    [Fact]
    public void Expo_GivesFineControl_NearDeadzoneEdge()
    {
        // Just past the deadzone, an expo>1 curve outputs LESS than the linear (expo=1) curve —
        // i.e. finer control near centre.
        double v = 0.62; // |dev|*2 = 0.24, comfortably past the 0.08 deadzone
        double expoOut = HeadResponseCurve.Shape(v, Dz, 1.4) - 0.5;
        double linOut  = HeadResponseCurve.Shape(v, Dz, 1.0) - 0.5;
        Assert.True(expoOut < linOut, $"expo curve should be sub-linear near centre: expo={expoOut}, lin={linOut}");
    }
}
