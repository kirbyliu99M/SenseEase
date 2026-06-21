using System;
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

    /// <param name="profile">Optional — read for the de-identified posture section; null ⇒ section omitted.</param>
    /// <param name="chatbot">Optional — null / not-loaded ⇒ deterministic narrative (report still full).</param>
    public ClinicalReportService(
        FileSymptomEventStore symptoms,
        DailyBehaviorAggregateWriter behavior,
        IHabituationProfileStore? profile = null,
        IChatbotService? chatbot = null)
    {
        _symptoms = symptoms ?? throw new ArgumentNullException(nameof(symptoms));
        _behavior = behavior ?? throw new ArgumentNullException(nameof(behavior));
        _profile  = profile;
        _chatbot  = chatbot;
    }

    /// <summary>Build the full report HTML (de-identified) and write it to <paramref name="path"/>.
    /// Atomic (tmp + replace); creates the parent directory; returns the written path. Throws on I/O
    /// failure so the user-initiated export surfaces a clear error.</summary>
    public async Task<string> WriteHtmlAsync(string path, bool english, DateTime nowUtc, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Report path is required.", nameof(path));

        var events       = _symptoms.ReadAll();
        var behaviorDays = _behavior.ReadAll();

        var export = ClinicalExportBuilder.Build(events);
        export.GeneratedUtc = nowUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        export.DailySeries  = ClinicalExportBuilder.BuildDailySeries(events, behaviorDays).ToList();

        var posture   = ReadPosture();
        var narrator  = new ClinicalReportNarrator(_chatbot);
        var narrative = await narrator.BuildAsync(export, posture, english, ct).ConfigureAwait(false);

        string html = ClinicalReportHtmlRenderer.Render(export, narrative, posture, english);
        AtomicWriteAllText(path, html);
        return path;
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
