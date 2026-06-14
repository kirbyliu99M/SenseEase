using SenseEase.App;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Beta-1: the ONE-TIME neutral recenter. Pins that a constant posture/camera offset is cancelled
/// (a centred head ⇒ a centred hole), a head turn moves the hole 1:1, a HELD off-centre gaze stays
/// deviated (NEVER drifts back — the 2026-06-15 regression guard), and face-loss passes through
/// untouched. Fixes Kirby's "hole shifts left and locks when my head is detected" without the
/// continuous-learning drift that made the hole "follow then drift off, never reaching the point".
/// </summary>
public sealed class NeutralRecenterFilterTests
{
    private const double Dt = 0.006; // ~165 Hz pump frame

    [Fact]
    public void ConstantOffCentrePose_RecentresToCentre()
    {
        // A user sitting off-axis: raw cx is a constant 0.35 (the "shifts left" bug). The one-time
        // neutral is captured on the first frame, so every frame maps that resting posture to centre.
        var f = new NeutralRecenterFilter();
        (double cx, double cy) outp = (0, 0);
        for (int i = 0; i < 200; i++)
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
    public void HeadMovement_ProducesFollowingDeviation_OneToOne()
    {
        // Neutral captured at centre (0.5) on the first frame.
        var f = new NeutralRecenterFilter();
        f.Recenter(0.5, 0.5, Dt, valid: true);

        // A head turn to the right: the hole follows the gaze 1:1 (deviation = raw − neutral), the
        // clear zone goes where the user looks — never attenuated.
        var outp = f.Recenter(0.80, 0.5, Dt, valid: true);
        Assert.Equal(0.80, outp.cx, 6);
    }

    [Fact]
    public void HeldOffCentreGaze_StaysDeviated_NeverDriftsBackToCentre()
    {
        // THE regression guard. With the old velocity-gated continuous learning the baseline crept
        // toward a held pose (~4 s tau) and the hole drifted back to centre — "follows then drifts
        // off, never reaches the point". Seed-only must hold the full deviation indefinitely.
        var f = new NeutralRecenterFilter();
        f.Recenter(0.5, 0.5, Dt, valid: true);          // neutral = 0.5
        (double cx, double cy) outp = (0, 0);
        for (int i = 0; i < 3000; i++)                  // ~18 s held at 0.80
            outp = f.Recenter(0.80, 0.5, Dt, valid: true);

        Assert.Equal(0.80, outp.cx, 4);                 // still fully deviated — no drift back
    }

    [Fact]
    public void FaceLost_PassesThroughUnchanged_AndDoesNotMoveNeutral()
    {
        var f = new NeutralRecenterFilter();
        f.Recenter(0.5, 0.5, Dt, valid: true);          // neutral captured at centre
        double neutralBefore = f.NeutralCx;

        var outp = f.Recenter(0.42, 0.58, Dt, valid: false); // face lost: pass through, freeze
        Assert.Equal(0.42, outp.cx, 6);
        Assert.Equal(0.58, outp.cy, 6);
        Assert.Equal(neutralBefore, f.NeutralCx, 9);    // baseline frozen during loss
    }
}
