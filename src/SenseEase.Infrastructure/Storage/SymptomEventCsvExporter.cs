using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SenseEase.Core.Models;

namespace SenseEase.Infrastructure.Storage;

/// <summary>
/// Long-term-analysis CSV export of the RAW <see cref="SymptomEvent"/> rows (one line per
/// check-in / estimate), header included. RFC-4180-style quoting so the file opens cleanly in
/// Excel / pandas / R. This is the USER's OWN on-device data, for their own longitudinal analysis —
/// distinct from the de-identified clinician aggregate (<see cref="ClinicalExportService"/>): it
/// keeps the per-event UTC timestamp and the coarse foreground key (present only when per-app
/// consent is on). User-initiated; never auto-uploaded. The append-only <c>symptom_log.jsonl</c>
/// remains the system-of-record and retains full history, so the CSV reflects everything captured.
/// </summary>
public static class SymptomEventCsvExporter
{
    // snake_case columns, mirroring the JSONL field names so the two formats line up 1:1.
    private static readonly string[] Header =
    {
        "ts_utc", "schema_version", "kind", "measured", "scale", "value",
        "source", "context_tier", "intervention_state", "trigger", "foreground_key",
    };

    /// <summary>Render the events as a CSV document (header + one row each), in the order given
    /// (the store returns oldest-first). Null/empty input yields a header-only document.</summary>
    public static string ToCsv(IReadOnlyList<SymptomEvent>? events)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", Header)).Append("\r\n");
        if (events is null) return sb.ToString();

        foreach (var e in events)
        {
            if (e is null) continue;
            sb.Append(F(e.TsUtc)).Append(',')
              .Append(e.SchemaVersion.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(F(e.Kind)).Append(',')
              .Append(e.Measured ? "true" : "false").Append(',')
              .Append(F(e.Scale)).Append(',')
              .Append(e.Value.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(F(e.Source)).Append(',')
              .Append(e.ContextTier.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(F(e.InterventionState)).Append(',')
              .Append(F(e.Trigger)).Append(',')
              .Append(F(e.ForegroundKey)).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>RFC-4180 field: wrap in double-quotes (doubling internal quotes) when it contains a
    /// comma, double-quote, CR or LF; null/empty → empty string.</summary>
    private static string F(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        bool needsQuote = s.IndexOf(',') >= 0 || s.IndexOf('"') >= 0
            || s.IndexOf('\n') >= 0 || s.IndexOf('\r') >= 0;
        return needsQuote ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
