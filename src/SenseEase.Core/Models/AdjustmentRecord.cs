namespace SenseEase.Core.Models;

/// <summary>
/// One personalization adjustment (FOV radius or a shader knob), persisted to the
/// adjustment log so a per-user weight can be computed from history.
/// </summary>
public sealed class AdjustmentRecord
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    /// <summary>"FovRadius" | "MaskDepth" | "GridVisibility" | "FeatherWidth" | "Reset".</summary>
    public string Parameter { get; set; } = string.Empty;

    public double Delta    { get; set; }
    public double OldValue { get; set; }
    public double NewValue { get; set; }

    /// <summary>"user" (chatbot feedback) | "system" (auto) | "reset".</summary>
    public string Source { get; set; } = "user";

    /// <summary>The user utterance that triggered this adjustment, if any.</summary>
    public string Utterance { get; set; } = string.Empty;
}
