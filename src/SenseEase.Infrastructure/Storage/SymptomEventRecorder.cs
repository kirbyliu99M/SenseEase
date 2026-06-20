using System;
using System.Globalization;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;

namespace SenseEase.Infrastructure.Storage;

/// <summary>
/// Beta-1 Phase-1 (option C): the single seam every symptom/comfort data-point goes through.
/// It writes the structured <see cref="SymptomEvent"/> to <c>symptom_log.jsonl</c> (the
/// system-of-record for the ON-vs-OFF delta + Theme-2 join + de-identified export) AND emits a
/// human-readable MIRROR line into the main event log (<see cref="IEventLogStore"/>) under the
/// durable <c>Symptom.*</c> category, so each rating is visible in the dashboard and survives a
/// restart in the forensic log — without the typed schema being crammed into a message string.
///
/// <para>The 5★/FMS console widget (D-18/19) and the daily NL→FMS estimator (D-21) both call
/// <see cref="Record"/>; consumers (e.g. feeding the selector's FMS input, D-19) read the
/// structured store. Best-effort: a logging failure never propagates to the caller.</para>
/// </summary>
public sealed class SymptomEventRecorder
{
    private readonly FileSymptomEventStore _store;
    private readonly IEventLogStore _eventLog;

    public SymptomEventRecorder(FileSymptomEventStore store, IEventLogStore eventLog)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _eventLog = eventLog ?? throw new ArgumentNullException(nameof(eventLog));
    }

    /// <summary>Persist <paramref name="ev"/> to the structured store and mirror it to the main
    /// event log. No-op for null. Never throws.</summary>
    public void Record(SymptomEvent ev)
    {
        if (ev is null) return;
        try { _store.Append(ev); } catch { /* best-effort */ }
        try { _eventLog.Append(BuildMirror(ev)); } catch { /* best-effort */ }
    }

    /// <summary>Build the dashboard/forensic mirror entry. Category is durable
    /// (<c>Symptom.Report</c> for a measured PRO, <c>Symptom.Estimate</c> for a derived value),
    /// so it persists and is filterable; the message keeps measured-vs-estimated explicit so the
    /// two are never visually conflated. Internal for unit cover.</summary>
    internal static EventLogEntry BuildMirror(SymptomEvent ev)
    {
        // W-E (2026-06-20): an overlay-consent row is NOT an FMS value — mirror it under its own durable
        // category so it never reads as a misleading "FMS ~0/20 estimate" in the dashboard/forensic log.
        if (ev.Kind == SymptomEvent.Kinds.OverlayConsent)
        {
            string cstate = string.IsNullOrEmpty(ev.InterventionState) ? "" : $", protection {ev.InterventionState}";
            return new EventLogEntry
            {
                Timestamp = ParseTsOrNow(ev.TsUtc),
                Category  = "Consent.Overlay",
                Message   = string.Format(CultureInfo.InvariantCulture,
                    "Overlay consent: {0} (tier {1}{2})", ev.Decision ?? "", ev.ContextTier, cstate),
            };
        }

        string category = ev.Measured ? "Symptom.Report" : "Symptom.Estimate";
        string qualifier = ev.Measured ? "measured" : "estimated";
        string approx = ev.Measured ? "" : "~";
        string state = string.IsNullOrEmpty(ev.InterventionState) ? "" : $", protection {ev.InterventionState}";
        string via = string.IsNullOrEmpty(ev.Source) ? "" : $" via {ev.Source}";
        string msg = string.Format(CultureInfo.InvariantCulture,
            "FMS {0}{1}/20 ({2}{3}, tier {4}{5})",
            approx, ev.Value, qualifier, via, ev.ContextTier, state);
        return new EventLogEntry
        {
            // Use the event's own UTC timestamp so the mirror and the structured row agree.
            Timestamp = ParseTsOrNow(ev.TsUtc),
            Category  = category,
            Message   = msg,
        };
    }

    private static DateTime ParseTsOrNow(string? tsUtc)
    {
        if (!string.IsNullOrWhiteSpace(tsUtc)
            && DateTime.TryParse(tsUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime t))
            return t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t.ToUniversalTime();
        return DateTime.UtcNow;
    }
}
