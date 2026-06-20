using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SenseEase.App.Configuration;
using SenseEase.App.Localization;

namespace SenseEase.App.Services;

/// <summary>EXPERIMENTAL NPS-analog bucket (promoter/detractor split) — NOT the primary KPI. See
/// <see cref="NpsService.ComputeNps"/> for why a 5-point instrument's NPS-analog must never be sent
/// labeled "NPS"; the primary KPI is <see cref="CsatSummary"/> via <see cref="NpsService.ComputeCsat"/>.</summary>
public enum NpsBucket { Detractor, Passive, Promoter }

/// <summary>The PRIMARY satisfaction KPI for the 5-point (CSAT) instrument: <see cref="Mean"/> (average
/// 1–5), <see cref="TopTwoBoxPercent"/> (% of responses scoring 4 or 5 — "satisfied"), and the
/// <see cref="Count"/> of responses. This is the metric to surface or send — honest for 5-point CSAT data,
/// unlike the experimental NPS-analog.</summary>
public readonly record struct CsatSummary(double Mean, double TopTwoBoxPercent, int Count);

/// <summary>Persisted NPS scheduler state (serialized to nps_state.json).</summary>
public sealed class NpsState
{
    public DateTime? LastAskedUtc { get; set; }
    public int? LastScore { get; set; }
    public int AskCount { get; set; }
    public DateTime? DismissedUntilUtc { get; set; }
    public int SessionsSeen { get; set; }
}

/// <summary>
/// UI-RESHAPE prep — the NPS KPI subsystem (net-new; no prior infra). Pure scheduling
/// logic + fail-safe persistence, modelled on <see cref="CheckInGovernor"/>'s
/// clock-injection so the eligibility decision is unit-testable without a window.
///
/// <para>Persists <see cref="NpsState"/> to <c>nps_state.json</c> and appends each
/// response to <c>survey_log.jsonl</c> under <c>%LOCALAPPDATA%\SenseEase\</c> (via
/// <see cref="AppConfig.DataRoot"/> — this service does NOT modify AppConfig; the path
/// will be promoted to an AppConfig property when the host wiring lands post-B5). The
/// data directory and the first-run probe are injectable through an internal ctor so
/// tests isolate without touching global <c>AppConfig.DataRootOverride</c>. All IO is
/// best-effort and never throws into the caller.</para>
///
/// <para>Gating mirrors the existing engine cadence philosophy (long cooldown + a
/// stronger post-dismiss quiet window + a minimum-usage floor); no new user-facing
/// prompt cadence is invented.</para>
/// </summary>
public sealed class NpsService
{
    private readonly Func<DateTime> _clock;
    private readonly Func<bool> _isFirstRun;
    private readonly string _dir;
    private NpsState _state;

    /// <summary>Minimum gap between two NPS asks. Long by design — NPS is a rare touch.</summary>
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Quiet window after the user dismisses ("Not now") without scoring.</summary>
    public TimeSpan DismissSuppression { get; set; } = TimeSpan.FromDays(90);

    /// <summary>Minimum app sessions before the FIRST ask — don't survey a brand-new user.</summary>
    public int MinSessions { get; set; } = 3;

    /// <summary>The survey scale's max (2026-06-18: the UI moved 0–10 → a 1–5 "five-point QA"). Each
    /// logged row is stamped with this; <see cref="ReadLoggedScores"/> counts ONLY current-scale rows
    /// so a pre-rescale 0–10 row can't pollute the KPI when mixed with 1–5 rows. (The PRIMARY KPI is
    /// <see cref="ComputeCsat"/> — mean + top-2-box; <see cref="BucketFor"/>/<see cref="ComputeNps"/> is an
    /// EXPERIMENTAL NPS-analog with 0–10 semantics that must never be sent labeled "NPS". This guard keeps
    /// the DATA clean for both.)</summary>
    private const int ScaleMax = 5;

    /// <summary>Production ctor: data under %LOCALAPPDATA%\SenseEase\, first-run via AppConfig.</summary>
    public NpsService(Func<DateTime>? utcClock = null) : this(null, utcClock) { }

    /// <summary>Test seam: isolate the data directory + first-run probe without mutating
    /// global AppConfig state. <paramref name="dataDirOverride"/> null → the production path.</summary>
    internal NpsService(string? dataDirOverride, Func<DateTime>? utcClock = null, Func<bool>? isFirstRun = null)
    {
        _clock = utcClock ?? (() => DateTime.UtcNow);
        _isFirstRun = isFirstRun ?? (() => AppConfig.IsFirstRun);
        _dir = dataDirOverride ?? Path.Combine(AppConfig.DataRoot, "SenseEase");
        _state = Load();
    }

    /// <summary>The current persisted state (defensive copy for callers/tests).</summary>
    public NpsState State => new()
    {
        LastAskedUtc = _state.LastAskedUtc,
        LastScore = _state.LastScore,
        AskCount = _state.AskCount,
        DismissedUntilUtc = _state.DismissedUntilUtc,
        SessionsSeen = _state.SessionsSeen,
    };

    /// <summary>Count a new app session (persisted) — feeds the minimum-usage gate.</summary>
    public void RecordSessionStart()
    {
        _state.SessionsSeen++;
        Save();
    }

    /// <summary>True iff an NPS prompt may be shown now. Pure read (does not mutate).</summary>
    public bool ShouldAsk()
    {
        var now = _clock();
        if (_state.DismissedUntilUtc is { } until && now < until) return false;          // post-dismiss quiet
        if (_state.LastAskedUtc is { } last && now - last < Cooldown) return false;      // cooldown
        if (_state.SessionsSeen < MinSessions) return false;                             // min-usage floor
        if (_isFirstRun()) return false;                                                 // not before first-run completes
        return true;
    }

    /// <summary>Record that the survey was shown — starts the cooldown.</summary>
    public void RecordShown()
    {
        _state.LastAskedUtc = _clock();
        _state.AskCount++;
        Save();
    }

    /// <summary>Record a 1–5 satisfaction score (+ optional one-line comment). Appends a
    /// row to survey_log.jsonl and clears any dismiss-suppression (engagement is the
    /// opposite signal). Out-of-range scores are ignored.</summary>
    public void RecordResponse(int score, string? comment)
    {
        if (score < 1 || score > 5) return;
        _state.LastScore = score;
        _state.DismissedUntilUtc = null;
        Save();
        AppendSurveyRow(score, comment);
    }

    /// <summary>Record a dismissal. An EXPLICIT "Not now" tap (<paramref name="explicitDecline"/>=true)
    /// arms the long 90-day quiet window; an IDLE auto-dismiss (the card timed out un-answered — a far
    /// weaker signal) arms only the normal 30-day cooldown, so ignoring the card once does not silence it
    /// for a quarter. Default true preserves the explicit-decline behaviour for existing callers.</summary>
    public void RecordDismissed(bool explicitDecline = true)
    {
        _state.DismissedUntilUtc = _clock() + (explicitDecline ? DismissSuppression : Cooldown);
        Save();
    }

    /// <summary>EXPERIMENTAL NPS-analog bucket for a 1–5 satisfaction score: 1–3 Detractor, 4 Passive,
    /// 5 Promoter. (Top-box = Promoter; the neutral 4 is Passive; everything at or below the midpoint is a
    /// Detractor.) Feeds <see cref="ComputeNps"/> only — see its remarks; the primary KPI is
    /// <see cref="ComputeCsat"/>.</summary>
    public static NpsBucket BucketFor(int score) => score switch
    {
        <= 3 => NpsBucket.Detractor,
        4    => NpsBucket.Passive,
        _    => NpsBucket.Promoter,
    };

    /// <summary>EXPERIMENTAL NPS-analog — NOT the primary KPI (that is <see cref="ComputeCsat"/>). Maps the
    /// 5-point CSAT data onto NPS buckets and returns (% promoters − % detractors) × 100 ∈ [-100, 100]. It is
    /// NOT comparable to industry 0–10 NPS (5-point granularity is far coarser — a single 3→4 move swings a
    /// whole bucket), so it must NEVER be surfaced to a user/stakeholder or sent over the wire labeled "NPS".
    /// Kept only for internal recalibration analysis. Null when no responses are logged.</summary>
    public double? ComputeNps()
    {
        var scores = ReadLoggedScores();
        if (scores.Count == 0) return null;
        double promoters = scores.Count(s => BucketFor(s) == NpsBucket.Promoter);
        double detractors = scores.Count(s => BucketFor(s) == NpsBucket.Detractor);
        return (promoters - detractors) / scores.Count * 100.0;
    }

    /// <summary>The PRIMARY satisfaction KPI for the 5-point instrument: the mean score (1–5) and the
    /// "top-2-box" satisfied rate (% of responses scoring 4 or 5). This is the metric to surface or send —
    /// honest for 5-point CSAT data, unlike <see cref="ComputeNps"/>. Null when no responses are logged.
    /// Reads survey_log.jsonl; malformed / legacy-scale rows are skipped (same guard as the analog).</summary>
    public CsatSummary? ComputeCsat()
    {
        var scores = ReadLoggedScores();
        if (scores.Count == 0) return null;
        double mean = scores.Average();
        double topTwoBox = scores.Count(s => s >= 4) / (double)scores.Count * 100.0;
        return new CsatSummary(mean, topTwoBox, scores.Count);
    }

    private string StatePath => Path.Combine(_dir, "nps_state.json");
    private string SurveyLogPath => Path.Combine(_dir, "survey_log.jsonl");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private NpsState Load()
    {
        try
        {
            if (File.Exists(StatePath))
                return JsonSerializer.Deserialize<NpsState>(File.ReadAllText(StatePath)) ?? new NpsState();
        }
        catch { /* fail-safe: a corrupt/absent file starts fresh */ }
        return new NpsState();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var tmp = StatePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state, JsonOpts), Encoding.UTF8);
            File.Move(tmp, StatePath, overwrite: true); // atomic-ish replace
        }
        catch { /* best-effort: never throw into the UI */ }
    }

    private void AppendSurveyRow(int score, string? comment)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var row = JsonSerializer.Serialize(new
            {
                ts = _clock().ToString("O", CultureInfo.InvariantCulture),
                score,
                scaleMax = ScaleMax, // self-describing scale so mixed-scale rows can't corrupt the KPI
                comment,
                appVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0",
                lang = LocalizationManager.Current.IsEnglish ? "en" : "zh",
            }, JsonOpts);
            File.AppendAllText(SurveyLogPath, row + Environment.NewLine, Encoding.UTF8);
        }
        catch { /* best-effort */ }
    }

    private System.Collections.Generic.List<int> ReadLoggedScores()
    {
        var scores = new System.Collections.Generic.List<int>();
        try
        {
            if (!File.Exists(SurveyLogPath)) return scores;
            foreach (var line in File.ReadAllLines(SurveyLogPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (!doc.RootElement.TryGetProperty("score", out var s) || !s.TryGetInt32(out var v))
                        continue;
                    // Count ONLY current-scale rows. A legacy 0–10 row has no scaleMax (→ treated as
                    // the old 10) and is skipped, so 0–10 and 1–5 rows never mix in the KPI.
                    int rowScale = doc.RootElement.TryGetProperty("scaleMax", out var sm) && sm.TryGetInt32(out var smv) ? smv : 10;
                    if (rowScale == ScaleMax) scores.Add(v);
                }
                catch { /* skip malformed line */ }
            }
        }
        catch { /* fail-safe */ }
        return scores;
    }
}
