using System.Text.Json;

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
/// Extracted from the FileHabituationStore.Save pattern in Phase 4. The
/// existing FileHabituationStore still uses inline File.Move and is NOT
/// migrated to this helper — it predates the strict atomicity requirement
/// and changing it is out of scope for the habituation-profile work.
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
            File.WriteAllText(tmp, json);

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
                // File.Move on the same volume is also atomic.
                File.Move(tmp, path, overwrite: false);
            }
            return true;
        }
        catch
        {
            // Best-effort: leave the previous file (if any) untouched.
            // Tmp file may be left behind on disk; the next successful
            // write replaces it. Tmp leaks are acceptable failure mode.
            return false;
        }
    }

    private static readonly JsonSerializerOptions _defaultOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy  = JsonNamingPolicy.SnakeCaseLower,
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
