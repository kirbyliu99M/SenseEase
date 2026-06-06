using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using SenseEase.Infrastructure.Real;

namespace SenseEase.Infrastructure.Storage;

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
    /// <paramref name="fileName"/> (matched by the bare file name, case-insensitive),
    /// or null when there is no such entry. Tolerates either a flat
    /// <c>{ "name": "hash" }</c> object or a <c>{ "files": { ... } }</c> wrapper.</summary>
    private static string? LookupExpectedHash(string manifestPath, string fileName)
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

        string key = Path.GetFileName(fileName);
        foreach (JsonProperty prop in root.EnumerateObject())
        {
            string propKey = Path.GetFileName(prop.Name);
            if (string.Equals(propKey, key, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(prop.Name, fileName, StringComparison.OrdinalIgnoreCase))
            {
                return prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString()
                    : null;
            }
        }
        return null;
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
