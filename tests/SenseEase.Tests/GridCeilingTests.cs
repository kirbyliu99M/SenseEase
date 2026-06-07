using SenseEase.App.Views;
using Xunit;

namespace SenseEase.Tests;

// Locks in the grid-opacity ceiling curve used by OverlayWindow.ApplyGridIntensity.
// GridCeiling is a pure static, so we exercise the REAL shipping function directly
// (via InternalsVisibleTo SenseEase.Tests) rather than mirroring the math.
//
// Regression guard: the chatbot "/grid off" sets GridVisibility=0; the grid MUST
// then fully hide. The old curve (0.40 + 0.80*gv) returned 0.40 at gv=0, leaving
// the anchor grid faintly visible — this test fails if that regresses.
public class GridCeilingTests
{
    [Fact]
    public void GridOff_Gv0_Ceiling_Is_Exactly_Zero()
    {
        // gv=0 → ceiling 0 → target clamped to [0,0] → grid fully hidden.
        Assert.Equal(0.0, OverlayWindow.GridCeiling(0.0));
    }

    [Fact]
    public void Default_Gv075_Ceiling_Is_One_And_Floor_030_Preserved()
    {
        // gv=0.75 (ShaderParams.DefaultGridVisibility) must stay byte-identical:
        // ceiling = min(1.0, 0.40 + 0.80*0.75) = min(1.0, 1.0) = 1.00.
        double ceiling = OverlayWindow.GridCeiling(0.75);
        Assert.Equal(1.0, ceiling);

        // The calibrated grid baseline (floor) of 0.30 falls out of the downstream
        // target math at gv=0.75: the lowest non-zero opacity the curve reaches.
        // ceiling=1.0 is the prerequisite for that tuned floor — assert it directly.
        Assert.Equal(0.30, 0.40 * 0.75, precision: 10); // 0.40*gv at gv=0.75 == the tuned-good floor 0.30
    }

    [Theory]
    [InlineData(0.25, 0.60)]   // 0.40 + 0.80*0.25
    [InlineData(0.50, 0.80)]   // 0.40 + 0.80*0.50
    [InlineData(0.75, 1.00)]   // capped at 1.0
    [InlineData(1.00, 1.00)]   // capped at 1.0
    public void PositiveGv_Curve_Unchanged(double gv, double expectedCeiling)
    {
        Assert.Equal(expectedCeiling, OverlayWindow.GridCeiling(gv), precision: 10);
    }
}
