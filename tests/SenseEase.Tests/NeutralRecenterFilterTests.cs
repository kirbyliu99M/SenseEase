using SenseEase.App;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Beta-1 theme-4: the velocity-gated neutral recenter (option B). Pins that a constant
/// posture/camera offset is cancelled (a centred head ⇒ a centred hole), while a sustained
/// off-centre GAZE — kept alive by continuous movement — is NOT cancelled, and that face-loss
/// passes through untouched. Fixes Kirby's "hole shifts left and locks when my head is detected."
/// </summary>
public sealed class NeutralRecenterFilterTests
{
    private const double Dt = 0.006; // ~165 Hz pump frame

    [Fact]
    public void ConstantOffCentrePose_RecentresToCentre()
    {
        // A user sitting still but off-axis: raw cx is a constant 0.35 (the "shifts left" bug).
        // The recenter must map that resting posture to ~screen-centre.
        var f = new NeutralRecenterFilter();
        (double cx, double cy) outp = (0, 0);
        for (int i = 0; i < 2000; i++) // ~12 s of stillness
            outp = f.Recenter(0.35, 0.62, Dt, valid: true);

        Assert.Equal(0.5, outp.cx, 2); // left offset cancelled
        Assert.Equal(0.5, outp.cy, 2); // vertical offset cancelled
    }

    [Fact]
    public void FirstFrame_IsAlreadyCentred_NoInitialOffsetJump()
    {
        // Seed-to-current: the very first detected frame must not show the raw offset.
        var f = new NeutralRecenterFilter();
        var outp = f.Recenter(0.30, 0.70, Dt, valid: true);
        Assert.Equal(0.5, outp.cx, 3);
        Assert.Equal(0.5, outp.cy, 3);
    }

    [Fact]
    public void HeadMovement_ProducesFollowingDeviation_NotCancelled()
    {
        // Settle the resting baseline at centre (0.5) first.
        var f = new NeutralRecenterFilter();
        for (int i = 0; i < 2000; i++) f.Recenter(0.5, 0.5, Dt, valid: true);

        // A head turn to the right: the baseline (learned slowly, ~4 s) can't follow a move that
        // completes in a fraction of a second, so the hole FOLLOWS the turn — the clear zone goes
        // where the user looks, it is not cancelled. (A head that then SETTLES there eventually
        // becomes the new rest — that is option B by design, and is its own UVHS feel item.)
        (double cx, double cy) outp = (0, 0);
        double x = 0.5;
        for (int i = 0; i < 10; i++) // ~60 ms turn to 0.80
        {
            x = System.Math.Min(0.80, x + 0.03);
            outp = f.Recenter(x, 0.5, Dt, valid: true);
        }

        Assert.True(outp.cx > 0.70,
            $"a head turn must move the hole with the gaze (deviation preserved): cx={outp.cx}");
    }

    [Fact]
    public void FaceLost_PassesThroughUnchanged_AndDoesNotLearn()
    {
        var f = new NeutralRecenterFilter();
        for (int i = 0; i < 500; i++) f.Recenter(0.5, 0.5, Dt, valid: true); // baseline at centre
        double neutralBefore = f.NeutralCx;

        var outp = f.Recenter(0.42, 0.58, Dt, valid: false); // face lost: pass through, freeze
        Assert.Equal(0.42, outp.cx, 6);
        Assert.Equal(0.58, outp.cy, 6);
        Assert.Equal(neutralBefore, f.NeutralCx, 9); // baseline frozen during loss
    }
}
