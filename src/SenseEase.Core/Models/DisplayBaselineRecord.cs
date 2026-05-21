namespace SenseEase.Core.Models;

/// <summary>
/// Persisted form of <see cref="DisplayBaseline"/>. The <see cref="CleanShutdown"/>
/// flag lets the next launch detect a force-kill that skipped restore and recover.
/// </summary>
public sealed class DisplayBaselineRecord
{
    public DisplayBaseline Baseline { get; set; } = new();

    /// <summary>A signature of the monitor this baseline belongs to (capabilities-string based).</summary>
    public string MonitorSignature { get; set; } = string.Empty;

    public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>True once a clean shutdown restored the baseline; false while adjustments are live.</summary>
    public bool CleanShutdown { get; set; }
}
