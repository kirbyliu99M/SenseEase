using System;
using System.Collections.Generic;
using System.Globalization;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Nlp;
using SenseEase.Infrastructure.Storage;

namespace SenseEase.Infrastructure.Real;

/// <summary>
/// Beta-1 D-21: the daily NL→FMS estimator — the first producer that fills the symptom log
/// from real usage. It derives a per-day FMS ESTIMATE from the chatbot's natural-language
/// feedback (FeedbackLexicon intent + severity), on-device, and records it through the
/// option-C <see cref="SymptomEventRecorder"/> (structured row + Symptom.Estimate mirror).
///
/// <para>This is a SECONDARY, corroborating signal — rigorously labelled
/// <see cref="SymptomEvent.Measured"/>=false / <c>source=nl_daily</c> so it is never conflated
/// with the measured 5★ self-report (D-18/19). It also carries a self-selection bias (people
/// chat more when unwell), so it supports but never substitutes the PRO. The estimate is the
/// day's PEAK reported discomfort (the worst they felt), mapped to FMS 0-20; a day with no
/// discomfort report yields NO estimate (no FMS=0 noise).</para>
/// </summary>
public sealed class DailyFmsEstimator
{
    private readonly FileChatHistoryStore _chat;
    private readonly FileSymptomEventStore _symptomStore;
    private readonly SymptomEventRecorder _recorder;

    public DailyFmsEstimator(FileChatHistoryStore chat, FileSymptomEventStore symptomStore, SymptomEventRecorder recorder)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _symptomStore = symptomStore ?? throw new ArgumentNullException(nameof(symptomStore));
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
    }

    /// <summary>Compute and record today's estimate if there is fresh discomfort and one hasn't
    /// already been recorded for the UTC day. Returns the recorded event, or null if nothing was
    /// recorded (no discomfort today, or already estimated). Best-effort; never throws.
    /// <paramref name="nowUtc"/> is injectable for tests.</summary>
    public SymptomEvent? RunOnce(DateTime nowUtc)
    {
        try
        {
            var todayUtc = DateOnly.FromDateTime(nowUtc.ToUniversalTime());

            // Idempotent per UTC day: skip if a daily estimate already exists for today.
            foreach (var e in _symptomStore.ReadRecent(64))
                if (e.Source == SymptomEvent.Sources.NlDaily
                    && TryUtcDay(e.TsUtc, out DateOnly d) && d == todayUtc)
                    return null;

            var ev = EstimateForDay(_chat.Load(), todayUtc, nowUtc);
            if (ev is null) return null;
            _recorder.Record(ev);
            return ev;
        }
        catch { return null; } // best-effort daily estimate must never crash the app
    }

    /// <summary>Pure: derive the FMS estimate for <paramref name="utcDay"/> from the chat. The
    /// day's PEAK ReportDiscomfort severity (0..1) maps to FMS 0-20; no discomfort ⇒ null.
    /// Internal for unit cover.</summary>
    internal static SymptomEvent? EstimateForDay(IReadOnlyList<ChatMessage> messages, DateOnly utcDay, DateTime nowUtc)
    {
        if (messages is null) return null;
        double peak = -1.0;
        foreach (var m in messages)
        {
            if (m is null || m.Role != ChatRole.User) continue;
            if (DateOnly.FromDateTime(m.Timestamp.ToUniversalTime()) != utcDay) continue;
            var (intent, _) = FeedbackLexicon.Classify(m.Content);
            if (intent != "ReportDiscomfort") continue;
            double sev = FeedbackLexicon.SeverityScore(m.Content); // 0..1
            if (sev > peak) peak = sev;
        }
        if (peak < 0.0) return null; // no discomfort reported on this day

        int fms = (int)Math.Round(Math.Clamp(peak, 0.0, 1.0) * 20.0);
        return new SymptomEvent
        {
            Kind = SymptomEvent.Kinds.FmsEstimate,
            Measured = false,                              // ESTIMATE — never a PRO
            Scale = SymptomEvent.Scales.Fms20,
            Value = fms,
            Source = SymptomEvent.Sources.NlDaily,
            TsUtc = nowUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            ContextTier = 0,                               // day-aggregate, not a single moment
            InterventionState = string.Empty,             // not applicable to a daily aggregate
            Trigger = string.Empty,
        };
    }

    private static bool TryUtcDay(string? tsUtc, out DateOnly day)
    {
        day = default;
        if (!string.IsNullOrWhiteSpace(tsUtc)
            && DateTime.TryParse(tsUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime t))
        {
            day = DateOnly.FromDateTime(t.ToUniversalTime());
            return true;
        }
        return false;
    }
}
