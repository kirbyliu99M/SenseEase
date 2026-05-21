namespace SenseEase.Core.Models;

/// <summary>A single diagnostics / event-log record shown in the dashboard table.</summary>
public class EventLogEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>Source category, e.g. "System", "Inference", "Feedback".</summary>
    public string Category { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    // State snapshot at the moment of the event
    public double Ofi { get; set; }
    public double Pressure { get; set; }
    public int FovRadius { get; set; }
    public string MaskSnapshot { get; set; } = "—";

    /// <summary>Display helper for the dashboard ListView.</summary>
    public string Time => Timestamp.ToString("HH:mm:ss");
}
