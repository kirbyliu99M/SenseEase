using System.Linq;
using SenseEase.Infrastructure.Real;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// UI-RESHAPE — the additive multi-rect exclude slot on WgcSensingProvider (the reshape's
/// simultaneously-visible surfaces). Round-trips the rect storage; OFI-flat zeroing behaviour is
/// validated separately via the live SENSEEASE_WGC_BAR_VALIDATION harness.
/// </summary>
public class WgcExcludeRectsTests
{
    [Fact]
    public void SetMonitorExcludeRects_RoundTrips()
    {
        var p = new WgcSensingProvider();
        p.SetMonitorExcludeRects(new (int, int, int, int)[] { (10, 20, 30, 40), (100, 200, 8, 120) });

        var rects = p.CurrentExcludeRects;
        Assert.Equal(2, rects.Count);
        Assert.Contains((10, 20, 30, 40), rects.Select(r => (r.Left, r.Top, r.Width, r.Height)));
        Assert.Contains((100, 200, 8, 120), rects.Select(r => (r.Left, r.Top, r.Width, r.Height)));
    }

    [Fact]
    public void SetMonitorExcludeRects_DropsInvalidAndClears()
    {
        var p = new WgcSensingProvider();
        p.SetMonitorExcludeRects(new (int, int, int, int)[] { (0, 0, 0, 50), (5, 5, 10, 10), (1, 1, 10, -1) });
        Assert.Single(p.CurrentExcludeRects); // only the valid (5,5,10,10) survives

        p.SetMonitorExcludeRects(null);
        Assert.Empty(p.CurrentExcludeRects);

        p.SetMonitorExcludeRects(System.Array.Empty<(int, int, int, int)>());
        Assert.Empty(p.CurrentExcludeRects);
    }

    [Fact]
    public void LegacySingleRect_IsIndependentOfMultiSlot()
    {
        var p = new WgcSensingProvider();
        p.SetMonitorExcludeRect(1, 2, 3, 4);
        p.SetMonitorExcludeRects(new (int, int, int, int)[] { (9, 9, 9, 9) });

        // The two slots don't clobber each other.
        Assert.Equal((1, 2, 3, 4), p.CurrentExcludeRect);
        Assert.Single(p.CurrentExcludeRects);
    }
}
