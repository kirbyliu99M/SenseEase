using System.Collections.Generic;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// UM5: the [behavior_trend] block builder. Pins the &lt;7-day guard, the six emitted
/// fields, the ~80-token budget, and the early-vs-late trend directions.
/// </summary>
public class LongitudinalSummaryBuilderTests
{
    private static DailyBehaviorAggregate Row(
        string date, int ofiP90 = 50, int spikes = 0, double offsetDelta = 1.0, int tierModal = 4) => new()
    {
        Date = date,
        OfiP90 = ofiP90,
        SpikeCount = spikes,
        OffsetDeltaPp = offsetDelta,
        TierModal = tierModal,
    };

    private static List<DailyBehaviorAggregate> Days(int n, int ofiP90 = 50, int spikes = 0)
    {
        var list = new List<DailyBehaviorAggregate>(n);
        for (int i = 0; i < n; i++)
            list.Add(Row($"2026-06-{(i + 1):00}", ofiP90: ofiP90, spikes: spikes));
        return list;
    }

    [Fact]
    public void BuildBlock_Null_ReturnsEmpty()
        => Assert.Equal(string.Empty, LongitudinalSummaryBuilder.BuildBlock(null));

    [Fact]
    public void BuildBlock_FewerThan7Days_ReturnsEmpty()
        => Assert.Equal(string.Empty, LongitudinalSummaryBuilder.BuildBlock(Days(6)));

    [Fact]
    public void BuildBlock_AtLeast7Days_EmitsAllFields_WithinTokenBudget()
    {
        string block = LongitudinalSummaryBuilder.BuildBlock(Days(10));

        Assert.StartsWith("[behavior_trend]", block);
        Assert.EndsWith("[/behavior_trend]", block);
        Assert.Contains("window: 30d", block);
        Assert.Contains("exposure_days: 10/30", block);
        Assert.Contains("ofi_p90_trend:", block);
        Assert.Contains("adaptation_slope:", block);
        Assert.Contains("spike_rate:", block);
        Assert.Contains("tolerance_ceiling: tier 4", block);

        // ~80-token budget (≈4 chars/token). Comfortably under.
        Assert.True(block.Length <= 320, $"block too long ({block.Length} chars): {block}");
    }

    [Fact]
    public void BuildBlock_RisingOfi_ReportedRising()
    {
        var rows = new List<DailyBehaviorAggregate>();
        for (int i = 0; i < 4; i++) rows.Add(Row($"2026-06-0{i + 1}", ofiP90: 40));
        for (int i = 0; i < 4; i++) rows.Add(Row($"2026-06-0{i + 5}", ofiP90: 70));

        string block = LongitudinalSummaryBuilder.BuildBlock(rows);
        Assert.Contains("ofi_p90_trend: +30 (rising)", block);
    }

    [Fact]
    public void BuildBlock_DecreasingSpikes_ReportedImproving()
    {
        var rows = new List<DailyBehaviorAggregate>();
        for (int i = 0; i < 4; i++) rows.Add(Row($"2026-06-0{i + 1}", spikes: 2));
        for (int i = 0; i < 4; i++) rows.Add(Row($"2026-06-0{i + 5}", spikes: 0));

        string block = LongitudinalSummaryBuilder.BuildBlock(rows);
        Assert.Contains("improving", block);
    }

    [Fact]
    public void BuildBlock_ToleranceCeiling_UsesMaxTierModal()
    {
        var rows = Days(8);
        rows[3].TierModal = 7; // a peak mid-window
        string block = LongitudinalSummaryBuilder.BuildBlock(rows);
        Assert.Contains("tolerance_ceiling: tier 7", block);
    }
}
