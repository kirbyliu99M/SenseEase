using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SenseEase.Core.Inference;

namespace SenseEase.Infrastructure.Storage;

/// <summary>
/// Beta-1 Phase 3 / Theme 5 (D-17): builds the de-identified clinical export from the on-device
/// symptom log and serialises it to a human-readable JSON file a user can hand to their health
/// professional. The heavy lifting (the pure numeric aggregation, measured vs estimated kept
/// separate) lives in <see cref="ClinicalExportBuilder"/>; this service is the thin I/O seam — read
/// the store, stamp the generation time, write the file.
///
/// <para>Privacy (D-17): the written file contains ONLY numeric aggregates + de-identified category
/// labels — never a raw app name, utterance, or finer-than-date timestamp. The append-only raw rows
/// (<c>Exportable=false</c>) are never written out.</para>
/// </summary>
public sealed class ClinicalExportService
{
    private readonly FileSymptomEventStore _store;

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never, // keep null fields explicit for the HP
    };

    public ClinicalExportService(FileSymptomEventStore store)
        => _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>Build the aggregate from everything in the symptom log, stamped with
    /// <paramref name="nowUtc"/> as the generation time.</summary>
    public ClinicalExport Build(DateTime nowUtc)
    {
        var export = ClinicalExportBuilder.Build(_store.ReadAll());
        export.GeneratedUtc = nowUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        return export;
    }

    /// <summary>Serialise a (fully composed) export to the de-identified JSON data record (indented,
    /// snake_case). Static so <see cref="SenseEase.Infrastructure.Reporting.ClinicalReportService"/> can write
    /// the SAME composed export it renders to HTML — one dataset, two artifacts (JSON = the data recording,
    /// HTML = the export). Kirby 2026-06-21.</summary>
    public static string Serialize(ClinicalExport export)
        => JsonSerializer.Serialize(export ?? throw new ArgumentNullException(nameof(export)), _json);

    /// <summary>Serialise an export to the de-identified JSON string (indented, snake_case).</summary>
    public string ToJson(ClinicalExport export) => Serialize(export);

    /// <summary>Build + write the export to <paramref name="path"/> (UTF-8, atomic via tmp+replace).
    /// Creates the parent directory. Returns the written path. Throws on I/O failure so the caller
    /// (a user-initiated "export for my doctor" action) can surface a clear error — unlike the
    /// best-effort telemetry writers, a failed export the user asked for must not pass silently.</summary>
    public string WriteJson(string path, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Export path is required.", nameof(path));

        string json = ToJson(Build(nowUtc));
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string tmp = path + ".tmp";
        byte[] bytes = Encoding.UTF8.GetBytes(json);
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
        return path;
    }
}
