using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Storage;

namespace SenseEase.Infrastructure.Reporting;

/// <summary>
/// Health-report redesign §8 (P4): composes the de-identified HTML health report and writes it to disk —
/// the richer companion to <see cref="ClinicalExportService"/>'s JSON (both are kept; the HTML AUGMENTS,
/// does not replace, the JSON). This is the thin I/O + composition seam:
/// <list type="number">
///   <item>read the symptom log + the daily behaviour log;</item>
///   <item>build the pure numeric export (<see cref="ClinicalExportBuilder.Build"/>) + the per-day series
///   (<see cref="ClinicalExportBuilder.BuildDailySeries"/>);</item>
///   <item>read the de-identified protection posture (allowlisted numeric / coarse-enum fields ONLY — no
///   calibration responses, no longitudinal observations, build-gate 1);</item>
///   <item>narrate each section through the two-wall pipeline (<see cref="ClinicalReportNarrator"/>);</item>
///   <item>render to a self-contained HTML file (<see cref="ClinicalReportHtmlRenderer"/>) and write it
///   atomically (tmp + replace), mirroring <see cref="ClinicalExportService.WriteJson"/>.</item>
/// </list>
/// </summary>
public sealed class ClinicalReportService
{
    private readonly FileSymptomEventStore _symptoms;
    private readonly DailyBehaviorAggregateWriter _behavior;
    private readonly IHabituationProfileStore? _profile;
    private readonly IChatbotService? _chatbot;
    private readonly Func<IReadOnlyList<SessionOnsetRecord>>? _onsetReader;
    private readonly Func<SatisfactionSummary?>? _satisfactionReader;

    /// <param name="profile">Optional — read for the de-identified posture section; null ⇒ section omitted.</param>
    /// <param name="chatbot">Optional — null / not-loaded ⇒ deterministic narrative (report still full).</param>
    /// <param name="onsetReader">Optional — the App injects <c>SessionOnsetLog.ReadAll</c> (learning-loop
    /// idea #2). Keeps the App→Infrastructure boundary clean: Infrastructure can't see AppConfig. Null ⇒ no
    /// onset data ⇒ the section shows its placeholder.</param>
    /// <param name="satisfactionReader">Optional — the App injects a score-only CSAT summary
    /// (<c>NpsService.ComputeCsat</c>, indicator #7). Null ⇒ no satisfaction tile.</param>
    public ClinicalReportService(
        FileSymptomEventStore symptoms,
        DailyBehaviorAggregateWriter behavior,
        IHabituationProfileStore? profile = null,
        IChatbotService? chatbot = null,
        Func<IReadOnlyList<SessionOnsetRecord>>? onsetReader = null,
        Func<SatisfactionSummary?>? satisfactionReader = null)
    {
        _symptoms = symptoms ?? throw new ArgumentNullException(nameof(symptoms));
        _behavior = behavior ?? throw new ArgumentNullException(nameof(behavior));
        _profile  = profile;
        _chatbot  = chatbot;
        _onsetReader = onsetReader;
        _satisfactionReader = satisfactionReader;
    }

    /// <summary>Compose the de-identified dataset ONCE and write BOTH artifacts from it: the JSON data
    /// record (<paramref name="jsonPath"/>) and the human HTML export (<paramref name="htmlPath"/>) — Kirby
    /// 2026-06-21: "JSON is the data recording, HTML is the export", so they are derived from one composed
    /// dataset and can never drift. Both atomic (tmp + replace). Optionally scopes to a recent window
    /// (<paramref name="sinceUtc"/>; null ⇒ all history). Returns the HTML path. Throws on I/O failure so the
    /// user-initiated export surfaces a clear error.</summary>
    public async Task<string> WriteAsync(
        string htmlPath, string jsonPath, bool english, DateTime nowUtc,
        DateTime? sinceUtc = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(htmlPath)) throw new ArgumentException("Report path is required.", nameof(htmlPath));
        if (string.IsNullOrWhiteSpace(jsonPath)) throw new ArgumentException("Data-record path is required.", nameof(jsonPath));

        // One dataset, scoped to the chosen window, feeds BOTH artifacts.
        var events = _symptoms.ReadAll().Where(e => e is not null && OnOrAfter(e!.TsUtc, sinceUtc)).ToList();
        string? sinceDate = sinceUtc?.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var behaviorDays = _behavior.ReadAll()
            .Where(b => b is not null && (sinceDate is null || string.Compare(b!.Date, sinceDate, StringComparison.Ordinal) >= 0))
            .ToList();

        var export = ClinicalExportBuilder.Build(events);
        export.GeneratedUtc      = nowUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        export.DailySeries       = ClinicalExportBuilder.BuildDailySeries(events, behaviorDays).ToList();
        export.OnsetTiming       = SessionFirstSignalBuilder.Build(_onsetReader?.Invoke() ?? Array.Empty<SessionOnsetRecord>());
        export.ConsentEngagement = ConsentEngagementBuilder.Build(events);
        export.Satisfaction      = _satisfactionReader?.Invoke();

        var posture   = ReadPosture();
        var narrator  = new ClinicalReportNarrator(_chatbot);
        var narrative = await narrator.BuildAsync(export, posture, english, ct).ConfigureAwait(false);

        // JSON data record + HTML export, both from the SAME composed export.
        AtomicWriteAllText(jsonPath, ClinicalExportService.Serialize(export));
        AtomicWriteAllText(htmlPath, ClinicalReportHtmlRenderer.Render(export, narrative, posture, english));
        return htmlPath;
    }

    /// <summary>True when a row's ISO-8601 timestamp is on/after the window start (or there is no window).
    /// Unparseable timestamps are KEPT (fail-open — never silently drop data we cannot date).</summary>
    private static bool OnOrAfter(string? tsUtc, DateTime? sinceUtc)
    {
        if (sinceUtc is not { } since) return true;
        if (string.IsNullOrWhiteSpace(tsUtc)) return true;
        return !DateTime.TryParse(tsUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
               || t.ToUniversalTime() >= since.ToUniversalTime();
    }

    /// <summary>Read ONLY the allowlisted, de-identified posture fields from the habituation profile —
    /// the coarse profile strength (mapped away from the raw RESILIENT/TYPICAL/SENSITIVE enum) + numbers.
    /// NEVER reads calibration_responses or the longitudinal-observation lists (build-gate 1). Best-effort:
    /// any failure ⇒ null ⇒ the posture section is simply omitted.</summary>
    private ReportPosture? ReadPosture()
    {
        try
        {
            var snap = _profile?.Load();
            if (snap is null) return null;
            return new ReportPosture
            {
                Strength         = MapStrength(snap.Profile),
                HabituationLevel = snap.HabituationLevel,
                OffsetPp         = snap.HabituationOffsetPp,
                OffsetCapPp      = snap.HabituationOffsetCapPp,
                CurrentTier      = snap.CurrentVimsTier,
            };
        }
        catch { return null; }
    }

    private static ProtectionStrength MapStrength(string? profile) => (profile ?? "").ToUpperInvariant() switch
    {
        "RESILIENT" => ProtectionStrength.Light,
        "SENSITIVE" => ProtectionStrength.Protective,
        _           => ProtectionStrength.Balanced,
    };

    private static void AtomicWriteAllText(string path, string content)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string tmp = path + ".tmp";
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write,
                                       FileShare.None, bufferSize: 4096, FileOptions.WriteThrough))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }
        try
        {
            if (File.Exists(path))
                File.Replace(tmp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }
}
