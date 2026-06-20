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

    /// <summary>B3 (per-app learning): the coarse on-device foreground key
    /// (<c>"category/app"</c>) in effect when this durable entry was emitted, or
    /// <c>null</c> when per-app consent (D-16) is off / the app is unknown. Additive +
    /// nullable: historical entries (and every entry written while consent is off)
    /// deserialize to null and fall into the global bucket, so the per-app stats
    /// pipeline degrades to today's app-agnostic behaviour. Never a raw window title —
    /// it is the same coarse key already stamped on FMS <c>SymptomEvent</c>s.
    /// <para>RESERVED scaffolding as of B6 (2026-06-21): stamped on every durable entry but READ by no
    /// selector path — the active set is single-global; this is the foundation for the PAUSED B3.2 per-app
    /// bucketing (docs/B6 §3). Do NOT conflate with <c>SymptomEvent.ForegroundKey</c>, which IS consumed by
    /// the per-app clinical export (output ②).</para></summary>
    public string? ForegroundKey { get; set; }

    /// <summary>Display helper for the dashboard ListView. CW-12/UX-15: Timestamp is
    /// stored in UTC, so convert to local for the human-visible row. ToLocalTime()
    /// converts a UTC-kind value (and treats an Unspecified-kind value as UTC); a
    /// Local-kind value is returned unchanged.</summary>
    public string Time => Timestamp.ToLocalTime().ToString("HH:mm:ss");
}
