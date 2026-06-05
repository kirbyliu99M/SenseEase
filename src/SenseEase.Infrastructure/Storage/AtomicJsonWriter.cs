using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SenseEase.Infrastructure.Storage;

/// <summary>
/// Shared helper for crash-safe JSON persistence. Writes via the tmp +
/// File.Replace pattern so a Process.Kill mid-write either leaves the
/// destination untouched (old version intact) or leaves it with the new
/// version — never a half-written file. Falls back to <see cref="File.Move"/>
/// for the first-write case where the destination doesn't yet exist
/// (File.Replace requires an existing destination).
///
/// Best-effort: never throws to the caller. Persistence layer failures must
/// not crash the application. Internal errors are swallowed; the caller
/// observes "did not write" as the failure mode and the next attempt will
/// retry.
///
/// Extracted from the FileHabituationStore.Save pattern in Phase 4;
/// FileHabituationStore.Save now routes through this helper (Alpha-1 W2 / H16,
/// NE15) so the habituation-progress file gets the same crash-safe,
/// flushed-to-disk atomic replace.
/// </summary>
public static class AtomicJsonWriter
{
    /// <summary>Serialize <paramref name="value"/> to JSON and atomically
    /// replace the file at <paramref name="path"/>.
    /// <para>Atomic across Process.Kill mid-write thanks to:</para>
    /// <list type="number">
    ///   <item>Write to <c>{path}.tmp</c> first. If a kill happens here,
    ///         the destination file is untouched.</item>
    ///   <item>Once tmp is fully written + flushed, <see cref="File.Replace"/>
    ///         performs the atomic rename. NTFS guarantees the rename is
    ///         a single transaction: any reader observes either the old
    ///         file or the new one, never a partial state.</item>
    /// </list>
    /// First-write fallback: when <paramref name="path"/> doesn't exist,
    /// File.Replace fails (it requires an existing destination), so we
    /// use File.Move which is also atomic on the same volume.
    /// <para>Options use <see cref="JsonNamingPolicy.SnakeCaseLower"/> so the
    /// resulting file is human-readable and matches the spec's schema
    /// naming. Pass an override via <paramref name="options"/> if a caller
    /// needs different shaping (none currently do).</para></summary>
    public static bool TryWrite<T>(string path, T value, JsonSerializerOptions? options = null)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            JsonSerializerOptions opts = options ?? _defaultOpts;
            string json = JsonSerializer.Serialize(value, opts);

            string tmp = path + ".tmp";
            // NE16: durably flush the tmp to disk BEFORE the rename. File.WriteAllText
            // can return before the OS persists the bytes, so a crash just after
            // File.Replace could leave a renamed-but-not-yet-persisted file.
            // WriteThrough + Flush(flushToDisk:true) forces the data down first.
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write,
                                           FileShare.None, bufferSize: 4096, FileOptions.WriteThrough))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                // Atomic on NTFS: destination is replaced as a single
                // transaction. backupFile=null means no .bak retained.
                // The third arg ignoreMetadataErrors=true skips ACL copy
                // to avoid permission failures on locked-down profiles.
                File.Replace(tmp, path, destinationBackupFileName: null,
                             ignoreMetadataErrors: true);
            }
            else
            {
                // First-write path: File.Replace can't see a destination.
                // File.Move on the same volume is also atomic. overwrite:true
                // (NE23/H46) so a process that races to create the destination
                // doesn't turn a benign first-write into an IOException + lost write.
                File.Move(tmp, path, overwrite: true);
            }
            return true;
        }
        catch
        {
            // Best-effort: leave the previous file (if any) untouched. Clean up the
            // tmp so repeated failures don't leak a growing pile of *.tmp (NE23/H46).
            try { if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); } catch { }
            return false;
        }
    }

    private static readonly JsonSerializerOptions _defaultOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy  = JsonNamingPolicy.SnakeCaseLower,
        // Stage 6 (schema v2): enums on the snapshot DTO serialize as
        // snake_case strings (e.g. "complete", "buttons", "high_risk_combo"),
        // matching the property naming policy. Set globally here so every
        // current and future enum-typed property on a snapshot uses the same
        // wire shape without per-property [JsonConverter] attributes.
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>Read + deserialize a JSON file written by
    /// <see cref="TryWrite"/>. Returns <c>default</c> when the file is
    /// missing, empty, malformed, or the schema doesn't match. Caller
    /// detects the missing-file case (default value) and handles fall-back.
    /// </summary>
    public static T? TryRead<T>(string path, JsonSerializerOptions? options = null)
    {
        try
        {
            if (!File.Exists(path)) return default;
            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return default;
            return JsonSerializer.Deserialize<T>(json, options ?? _defaultOpts);
        }
        catch
        {
            return default;
        }
    }
}
