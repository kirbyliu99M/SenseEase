using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using SenseEase.App.Configuration;
using SenseEase.App.Localization;
using SenseEase.Core.Inference;

namespace SenseEase.App.Services;

/// <summary>
/// Learning-loop Part D, Idea #2 (2026-06-21): the durable per-LAUNCH record of the FIRST discomfort
/// signal of a session — the earliest of consent-accept / FMS check-in / chatbot ReportDiscomfort. One
/// self-describing row per app-launch (the first-of-session guard lives in <c>MainViewModel</c>), appended
/// to <c>session_onset_log.jsonl</c> under <c>%LOCALAPPDATA%\SenseEase\</c> — parallel to the FMS
/// <c>fms_log.jsonl</c> and the NPS <c>survey_log.jsonl</c>, and DELIBERATELY independent of the
/// collision-hot <c>behavior_log.jsonl</c> the health-report build reads.
///
/// <para>The row carries the elapsed OFFSET (a duration, written immediately when the first signal fires
/// so a crash can't lose the in-memory session anchor) plus light context (tier, protection state, lang)
/// for dev monitoring. The Core <see cref="SessionFirstSignalBuilder"/> aggregates these into a
/// report-only median-minutes distribution; nothing here touches the relax gate or the FMS partition (see
/// docs/LEARNING_LOOP_PLAN.md §D0/§D2). Best-effort IO — a failure must never break the UI. The data
/// directory is injectable for tests.</para>
/// </summary>
public sealed class SessionOnsetLog
{
    private readonly Func<DateTime> _clock;
    private readonly string _dir;

    /// <summary>Production ctor: data under %LOCALAPPDATA%\SenseEase\.</summary>
    public SessionOnsetLog(Func<DateTime>? utcClock = null) : this(null, utcClock) { }

    /// <summary>Test seam: isolate the data directory without mutating global AppConfig state.</summary>
    internal SessionOnsetLog(string? dataDirOverride, Func<DateTime>? utcClock = null)
    {
        _clock = utcClock ?? (() => DateTime.UtcNow);
        _dir = dataDirOverride ?? Path.Combine(AppConfig.DataRoot, "SenseEase");
    }

    private string LogPath => Path.Combine(_dir, "session_onset_log.jsonl");

    /// <summary>Append one first-signal row. <paramref name="offsetSeconds"/> is the elapsed time from
    /// session start (a duration, D-17-safe); negative/non-finite offsets are dropped. Best-effort — an IO
    /// failure must never disturb the calling channel (consent / FMS / chat).</summary>
    public void Record(string channel, double offsetSeconds, int tier, string protectionState)
    {
        if (string.IsNullOrWhiteSpace(channel)) return;
        if (!double.IsFinite(offsetSeconds) || offsetSeconds < 0) return;
        try
        {
            Directory.CreateDirectory(_dir);
            string row = JsonSerializer.Serialize(new
            {
                ts = _clock().ToString("O", CultureInfo.InvariantCulture),
                channel,
                offset_sec = Math.Round(offsetSeconds, 1),
                tier,
                protection_state = protectionState,
                lang = LocalizationManager.Current.IsEnglish ? "en" : "zh",
            });
            File.AppendAllText(LogPath, row + Environment.NewLine, Encoding.UTF8);
        }
        catch { /* best-effort sink — never throw into the UI */ }
    }

    /// <summary>Read every recorded session-onset row into Core records for aggregation. Malformed lines
    /// are skipped; an unreadable/absent log yields an empty list. Reads only the two report-relevant
    /// fields (channel + offset) — the contextual fields stay dev-only.</summary>
    public IReadOnlyList<SessionOnsetRecord> ReadAll()
    {
        var records = new List<SessionOnsetRecord>();
        try
        {
            if (!File.Exists(LogPath)) return records;
            foreach (var line in File.ReadLines(LogPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("channel", out var ch) || ch.ValueKind != JsonValueKind.String) continue;
                    if (!root.TryGetProperty("offset_sec", out var off) || off.ValueKind != JsonValueKind.Number) continue;
                    records.Add(new SessionOnsetRecord
                    {
                        Channel = ch.GetString() ?? string.Empty,
                        OffsetSeconds = off.GetDouble(),
                    });
                }
                catch { /* skip a malformed row, keep the rest */ }
            }
        }
        catch { /* unreadable log → empty */ }
        return records;
    }
}
