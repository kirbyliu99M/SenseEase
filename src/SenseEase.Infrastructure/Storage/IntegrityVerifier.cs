using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using SenseEase.Infrastructure.Real;

namespace SenseEase.Infrastructure.Storage;

/// <summary>
/// Result for a single manifest entry produced by
/// <see cref="IntegrityVerifier.VerifyManifestSweep"/>.
/// </summary>
public sealed class ManifestEntryResult
{
    /// <summary>Manifest key (dist-relative path).</summary>
    public string Key { get; }

    /// <summary>Outcome of the integrity check for this entry.</summary>
    public ManifestEntryStatus Status { get; }

    /// <summary>Optional human-readable detail (e.g. resolved absolute path,
    /// or a brief failure reason). Never null.</summary>
    public string Detail { get; }

    internal ManifestEntryResult(string key, ManifestEntryStatus status, string detail)
    {
        Key    = key;
        Status = status;
        Detail = detail ?? string.Empty;
    }
}

/// <summary>Outcome of a single manifest-entry integrity check.</summary>
public enum ManifestEntryStatus
{
    /// <summary>File present and SHA-256 matches the manifest value.</summary>
    Ok,

    /// <summary>File is listed in the manifest but not found on disk.</summary>
    Missing,

    /// <summary>File is present but its SHA-256 does not match the manifest.</summary>
    Tampered,

    /// <summary>The manifest entry could not be evaluated (parse error, hash
    /// value is not valid hex, or an unexpected I/O failure).</summary>
    Error,
}

/// <summary>
/// Alpha-1 engine-integrity guard: verifies a shipped file's SHA-256 against a
/// build-time <c>install.manifest.json</c> placed next to
/// <see cref="AppContext.BaseDirectory"/>.
///
/// <para><b>Absent-manifest = pass.</b> Alpha-1 builds ship without a manifest
/// yet (the build-time manifest generation + installer wiring is the PACKAGING
/// phase, out of scope here). When the manifest is missing this returns
/// <c>true</c> and logs "integrity check skipped: no manifest" so the gate is a
/// no-op on current dev/alpha builds rather than a hard block.</para>
///
/// <para>When the manifest IS present we compute the SHA-256 of
/// <paramref name="fileName"/> and compare (case-insensitive hex) to the
/// manifest entry. A missing entry, a hash mismatch, or any read/parse failure
/// yields <c>false</c>. Pure / static; never throws on the absent-manifest path.</para>
/// </summary>
public static class IntegrityVerifier
{
    /// <summary>Manifest file name, expected next to
    /// <see cref="AppContext.BaseDirectory"/>.</summary>
    private const string ManifestFileName = "install.manifest.json";

    /// <summary>
    /// Verifies <paramref name="fileName"/> against the install manifest.
    /// Returns <c>true</c> when no manifest is present (Alpha-1 builds), or when
    /// the manifest entry matches the file's SHA-256 (case-insensitive hex).
    /// Returns <c>false</c> when the manifest is present but the entry is
    /// missing, the hash mismatches, or the file/manifest cannot be read.
    /// </summary>
    public static bool Verify(string fileName)
    {
        string baseDir = AppContext.BaseDirectory ?? string.Empty;
        string manifestPath = Path.Combine(baseDir, ManifestFileName);

        // Absent manifest → pass (no-op gate on Alpha-1 builds).
        bool manifestExists;
        try { manifestExists = File.Exists(manifestPath); }
        catch { manifestExists = false; }

        if (!manifestExists)
        {
            try { WgcDiagLog.Append("integrity check skipped: no manifest"); }
            catch { /* best-effort */ }
            return true;
        }

        if (string.IsNullOrEmpty(fileName))
            return false;

        try
        {
            // Look up the expected hash for this entry. The manifest is a flat
            // JSON object mapping file name → lowercase/uppercase hex SHA-256.
            string? expectedHex = LookupExpectedHash(manifestPath, fileName);
            if (string.IsNullOrEmpty(expectedHex))
                return false; // present manifest but no entry for this file

            // Resolve the file to hash: a bare name is taken relative to the
            // base dir (alongside the manifest); an absolute/relative path is
            // honored as given.
            string filePath = Path.IsPathRooted(fileName)
                ? fileName
                : Path.Combine(baseDir, fileName);

            if (!File.Exists(filePath))
                return false;

            string actualHex = ComputeSha256Hex(filePath);
            return string.Equals(actualHex, expectedHex.Trim(),
                                 StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Any read/parse/hash failure on a PRESENT manifest is a failed
            // verification, not a skip.
            return false;
        }
    }

    /// <summary>Reads the manifest JSON and returns the hex hash string mapped to
    /// <paramref name="fileName"/>, or null when there is no such entry. Tolerates either a flat
    /// <c>{ "name": "hash" }</c> object or a <c>{ "files": { ... } }</c> wrapper.
    ///
    /// <para>The bundle ships SAME-NAMED files in different packs (the vulkan vs openvino
    /// <c>llama-server.exe</c> / <c>llama.dll</c> / <c>ggml*.dll</c> — identical leaf names, DIFFERENT
    /// bytes). So we match the entry whose dist-relative KEY is the most-specific PATH SUFFIX of
    /// <paramref name="fileName"/>, and fall back to a bare-name match only when no path-suffix entry
    /// exists. (A pure <c>Path.GetFileName</c> match returned whichever same-named entry sorted first and
    /// handed back the WRONG hash — that failed the Vulkan startup gate closed when the openvino entry led
    /// the manifest, suppressing the LLM + head-tracking on every packaged install.)</para></summary>
    internal static string? LookupExpectedHash(string manifestPath, string fileName)
    {
        string json = File.ReadAllText(manifestPath);
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        // Allow an optional "files" container; otherwise read the root object.
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("files", out JsonElement filesEl) &&
            filesEl.ValueKind == JsonValueKind.Object)
        {
            root = filesEl;
        }

        if (root.ValueKind != JsonValueKind.Object)
            return null;

        string query = fileName.Replace('\\', '/');
        string bare  = Path.GetFileName(fileName);

        string? suffixHash = null;
        int     bestKeyLen = -1;
        string? bareHash   = null;

        foreach (JsonProperty prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.String)
                continue;

            string key = prop.Name.Replace('\\', '/');

            // Path-suffix match: the query equals the key, or ends with "/<key>" on a real path
            // boundary (so "…/vulkan/llama-server.exe" can't match an unrelated "…server.exe").
            bool isSuffix = string.Equals(query, key, StringComparison.OrdinalIgnoreCase)
                            || query.EndsWith("/" + key, StringComparison.OrdinalIgnoreCase);
            if (isSuffix && key.Length > bestKeyLen)
            {
                suffixHash = prop.Value.GetString();
                bestKeyLen = key.Length;
            }

            // First bare-name match wins — used only as a fallback when no path-suffix entry matched.
            if (bareHash is null &&
                string.Equals(Path.GetFileName(prop.Name), bare, StringComparison.OrdinalIgnoreCase))
            {
                bareHash = prop.Value.GetString();
            }
        }

        return suffixHash ?? bareHash;
    }

    /// <summary>
    /// PKG-06: full manifest sweep — iterates every entry in
    /// <c>install.manifest.json</c> under <paramref name="installRoot"/> (or
    /// <see cref="AppContext.BaseDirectory"/> when <paramref name="installRoot"/>
    /// is null/empty), hashes each listed file, and returns one
    /// <see cref="ManifestEntryResult"/> per entry.
    ///
    /// <para><b>Absent-manifest = pass-with-note.</b> When no manifest file is
    /// found the method returns an empty list and logs
    /// "integrity sweep skipped: no manifest" (SEC-03 / Alpha-1 posture). The
    /// call-site owner treats an empty result as non-blocking.</para>
    ///
    /// <para>Designed for ONE call at startup. Hashing ~350 MB once is acceptable;
    /// never call per-spawn or per-frame.</para>
    ///
    /// <para>Never throws. Any top-level failure (unreadable manifest, JSON parse
    /// error) produces a single <see cref="ManifestEntryStatus.Error"/> entry with
    /// key "manifest" and the failure detail in <see cref="ManifestEntryResult.Detail"/>.</para>
    /// </summary>
    /// <param name="installRoot">Override for the install root directory. When null
    /// or empty, <see cref="AppContext.BaseDirectory"/> is used (production path).
    /// Test code passes a temp directory to avoid touching the real install.</param>
    public static IReadOnlyList<ManifestEntryResult> VerifyManifestSweep(
        string? installRoot = null)
    {
        string baseDir = string.IsNullOrEmpty(installRoot)
            ? (AppContext.BaseDirectory ?? string.Empty)
            : installRoot;

        string manifestPath = Path.Combine(baseDir, ManifestFileName);

        // Absent-manifest → pass-with-note (SEC-03 / Alpha-1 posture).
        bool manifestExists;
        try { manifestExists = File.Exists(manifestPath); }
        catch { manifestExists = false; }

        if (!manifestExists)
        {
            try { WgcDiagLog.Append("integrity sweep skipped: no manifest"); }
            catch { /* best-effort */ }
            return Array.Empty<ManifestEntryResult>();
        }

        var results = new List<ManifestEntryResult>();

        try
        {
            string json = File.ReadAllText(manifestPath);
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            // Tolerate an optional "files" wrapper (same as Verify).
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("files", out JsonElement filesEl) &&
                filesEl.ValueKind == JsonValueKind.Object)
            {
                root = filesEl;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                results.Add(new ManifestEntryResult(
                    "manifest", ManifestEntryStatus.Error,
                    "Root element is not a JSON object"));
                return results;
            }

            foreach (JsonProperty prop in root.EnumerateObject())
            {
                string key = prop.Name;

                // The expected hash must be a non-empty string.
                if (prop.Value.ValueKind != JsonValueKind.String)
                {
                    results.Add(new ManifestEntryResult(
                        key, ManifestEntryStatus.Error,
                        "Manifest value is not a string"));
                    continue;
                }

                string? expectedHex = prop.Value.GetString();
                if (string.IsNullOrWhiteSpace(expectedHex))
                {
                    results.Add(new ManifestEntryResult(
                        key, ManifestEntryStatus.Error,
                        "Manifest value is empty"));
                    continue;
                }

                // Resolve the file path: a relative key is combined with
                // installRoot (forward-slash keys from the stage script work
                // on Windows because Path.Combine handles mixed separators).
                string filePath = Path.IsPathRooted(key)
                    ? key
                    : Path.Combine(baseDir, key.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(filePath))
                {
                    results.Add(new ManifestEntryResult(
                        key, ManifestEntryStatus.Missing,
                        $"File not found: {filePath}"));
                    continue;
                }

                try
                {
                    string actualHex = ComputeSha256Hex(filePath);
                    bool match = string.Equals(actualHex, expectedHex.Trim(),
                                               StringComparison.OrdinalIgnoreCase);
                    results.Add(new ManifestEntryResult(
                        key,
                        match ? ManifestEntryStatus.Ok : ManifestEntryStatus.Tampered,
                        match ? filePath : $"Hash mismatch: expected {expectedHex.Trim()}, got {actualHex}"));
                }
                catch (Exception ex)
                {
                    results.Add(new ManifestEntryResult(
                        key, ManifestEntryStatus.Error,
                        $"Hash computation failed: {ex.Message}"));
                }
            }
        }
        catch (Exception ex)
        {
            // Top-level failure: manifest unreadable or not valid JSON.
            results.Add(new ManifestEntryResult(
                "manifest", ManifestEntryStatus.Error,
                $"Manifest read/parse failed: {ex.Message}"));
        }

        return results;
    }

    /// <summary>Computes the lowercase hex SHA-256 of the file at
    /// <paramref name="filePath"/>, streaming so large files are not buffered.</summary>
    private static string ComputeSha256Hex(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                                          FileShare.Read);
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash); // uppercase hex; compared case-insensitively
    }
}
