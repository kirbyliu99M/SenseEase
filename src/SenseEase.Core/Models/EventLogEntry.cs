namespace SenseEase.Core.Models;

/// <summary>A single diagnostics / event-log record shown in the dashboard table.</summary>
public class EventLogEntry
{
    // CW-12: stamp UTC by default so the durable log and the engine writers (which
    // already stamp UtcNow explicitly) share one clock domain. The App-layer durable
    // appenders that rely on this default are auto-corrected; the human-visible Time
    // helper converts back to local for display.
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>Source category, e.g. "System", "Inference", "Feedback".</summary>
    public string Category { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    // State snapshot at the moment of the event
    public double Ofi { get; set; }
    public double Pressure { get; set; }
    public int FovRadius { get; set; }
    public string MaskSnapshot { get; set; } = "—";

    /// <summary>Display helper for the dashboard ListView. CW-12/UX-15: Timestamp is
    /// stored in UTC, so convert to local for the human-visible row. ToLocalTime()
    /// converts a UTC-kind value (and treats an Unspecified-kind value as UTC); a
    /// Local-kind value is returned unchanged.</summary>
    public string Time => Timestamp.ToLocalTime().ToString("HH:mm:ss");
}
