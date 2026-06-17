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

/// <summary>NPS response bucket (the KPI's promoter/detractor split).</summary>
public enum NpsBucket { Detractor, Passive, Promoter }

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

    /// <summary>Record a 0–10 score (+ optional one-line comment). Appends a row to
    /// survey_log.jsonl and clears any dismiss-suppression (engagement is the opposite
    /// signal). Out-of-range scores are ignored.</summary>
    public void RecordResponse(int score, string? comment)
    {
        if (score < 0 || score > 10) return;
        _state.LastScore = score;
        _state.DismissedUntilUtc = null;
        Save();
        AppendSurveyRow(score, comment);
    }

    /// <summary>Record a dismissal ("Not now") — arms the longer quiet window.</summary>
    public void RecordDismissed()
    {
        _state.DismissedUntilUtc = _clock() + DismissSuppression;
        Save();
    }

    /// <summary>NPS bucket for a 0–10 score: 0–6 Detractor, 7–8 Passive, 9–10 Promoter.</summary>
    public static NpsBucket BucketFor(int score) => score switch
    {
        <= 6 => NpsBucket.Detractor,
        <= 8 => NpsBucket.Passive,
        _    => NpsBucket.Promoter,
    };

    /// <summary>The KPI: (% promoters − % detractors) × 100, in [-100, 100]. Null when
    /// no responses are logged yet. Reads survey_log.jsonl; malformed lines are skipped.</summary>
    public double? ComputeNps()
    {
        var scores = ReadLoggedScores();
        if (scores.Count == 0) return null;
        double promoters = scores.Count(s => BucketFor(s) == NpsBucket.Promoter);
        double detractors = scores.Count(s => BucketFor(s) == NpsBucket.Detractor);
        return (promoters - detractors) / scores.Count * 100.0;
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
                    if (doc.RootElement.TryGetProperty("score", out var s) && s.TryGetInt32(out var v))
                        scores.Add(v);
                }
                catch { /* skip malformed line */ }
            }
        }
        catch { /* fail-safe */ }
        return scores;
    }
}
