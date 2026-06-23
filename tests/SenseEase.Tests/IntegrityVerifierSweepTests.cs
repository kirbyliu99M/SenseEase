using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using SenseEase.Infrastructure.Storage;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// PKG-06: unit tests for <see cref="IntegrityVerifier.VerifyManifestSweep"/>.
///
/// <para>Each test builds a self-contained temp directory so results are
/// deterministic and never touch <see cref="AppContext.BaseDirectory"/> or
/// the real install. The <paramref name="installRoot"/> overload is designed
/// exactly for this test-seam purpose.</para>
///
/// <para>Cases covered:
/// <list type="bullet">
///   <item>Absent manifest → empty result list (pass-with-note, Alpha-1 posture)</item>
///   <item>Good file → <see cref="ManifestEntryStatus.Ok"/></item>
///   <item>Tampered file (content changed after hashing) → <see cref="ManifestEntryStatus.Tampered"/></item>
///   <item>Listed file absent from disk → <see cref="ManifestEntryStatus.Missing"/></item>
///   <item>Mixed manifest (good + tampered + missing) → correct per-entry results</item>
///   <item>Manifest with <c>{ "files": { ... } }</c> wrapper → same results as flat form</item>
///   <item>Manifest containing a relative-path key with forward slashes → resolves correctly</item>
///   <item>Malformed JSON manifest → single Error entry, key "manifest"</item>
/// </list>
/// </para>
/// </summary>
public class IntegrityVerifierSweepTests : IDisposable
{
    private readonly string _root;

    public IntegrityVerifierSweepTests()
    {
        _root = Path.Combine(Path.GetTempPath(),
            "senseease-sweep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private string WriteFile(string relPath, byte[] content)
    {
        string full = Path.Combine(_root, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    private static string Sha256Hex(byte[] data)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(data));
    }

    private void WriteManifest(string json)
    {
        File.WriteAllText(Path.Combine(_root, "install.manifest.json"), json);
    }

    // ------------------------------------------------------------------
    // Tests
    // ------------------------------------------------------------------

    [Fact]
    public void Sweep_NoManifest_ReturnsEmptyList()
    {
        // No install.manifest.json in temp root → absent-manifest posture.
        IReadOnlyList<ManifestEntryResult> results =
            IntegrityVerifier.VerifyManifestSweep(_root);

        Assert.Empty(results);
    }

    [Fact]
    public void Sweep_GoodFile_ReturnsOk()
    {
        byte[] content = { 0x01, 0x02, 0x03 };
        WriteFile("llama-server.exe", content);
        string hex = Sha256Hex(content); // uppercase from Convert.ToHexString

        WriteManifest($"{{ \"llama-server.exe\": \"{hex}\" }}");

        IReadOnlyList<ManifestEntryResult> results =
            IntegrityVerifier.VerifyManifestSweep(_root);

        Assert.Single(results);
        Assert.Equal(ManifestEntryStatus.Ok, results[0].Status);
        Assert.Equal("llama-server.exe", results[0].Key);
    }

    // ------------------------------------------------------------------
    // LookupExpectedHash: same-leaf-name files in different packs must
    // disambiguate by PATH SUFFIX, not bare name. This is the vulkan-vs-
    // openvino llama-server.exe collision that bricked the packaged startup
    // gate (the openvino entry led the manifest, so a bare-name lookup of the
    // Vulkan exe returned the openvino hash → mismatch → IntegrityFailed →
    // LLM + head-tracking suppressed on every install).
    // ------------------------------------------------------------------

    [Fact]
    public void LookupExpectedHash_SameLeafNameDifferentPacks_MatchesByPathSuffix()
    {
        // Two llama-server.exe with DIFFERENT hashes, openvino listed FIRST (the exact shape the stage
        // script emits + what shipped in SenseEase-Setup-1.0.0).
        const string openvinoHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        const string vulkanHash   = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        string manifest = Path.Combine(_root, "lookup.manifest.json");
        File.WriteAllText(manifest,
            "{\n" +
            $"  \"runtimes/win-x64/native/openvino/llama-server.exe\": \"{openvinoHash}\",\n" +
            $"  \"runtimes/win-x64/native/vulkan/llama-server.exe\": \"{vulkanHash}\"\n" +
            "}");

        // Each rooted install path (backslashes) must map to its OWN pack's hash, regardless of
        // manifest order — this is what the startup gate passes via ResolveServerExePath().
        string vulkanQuery   = @"C:\app\runtimes\win-x64\native\vulkan\llama-server.exe";
        string openvinoQuery = @"C:\app\runtimes\win-x64\native\openvino\llama-server.exe";

        Assert.Equal(vulkanHash,   IntegrityVerifier.LookupExpectedHash(manifest, vulkanQuery));
        Assert.Equal(openvinoHash, IntegrityVerifier.LookupExpectedHash(manifest, openvinoQuery));
    }

    [Fact]
    public void LookupExpectedHash_BareNameCaller_StillResolvesViaFallback()
    {
        // A bare-name query (no path) still resolves via the bare-name fallback when there is no
        // path-suffix entry to disambiguate (back-compat).
        const string hash = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";
        string manifest = Path.Combine(_root, "bare.manifest.json");
        File.WriteAllText(manifest, $"{{ \"models/Qwen3-4B-Instruct-2507-GGUF/foo.gguf\": \"{hash}\" }}");

        Assert.Equal(hash, IntegrityVerifier.LookupExpectedHash(manifest, "foo.gguf"));
    }

    [Fact]
    public void Sweep_TamperedFile_ReturnsTampered()
    {
        byte[] original = { 0xAA, 0xBB, 0xCC };
        string path = WriteFile("ggml-vulkan.dll", original);
        string correctHex = Sha256Hex(original);

        // Write the manifest with the hash of the original content...
        WriteManifest($"{{ \"ggml-vulkan.dll\": \"{correctHex}\" }}");

        // ...then tamper the file.
        File.WriteAllBytes(path, new byte[] { 0x00, 0x00, 0x00 });

        IReadOnlyList<ManifestEntryResult> results =
            IntegrityVerifier.VerifyManifestSweep(_root);

        Assert.Single(results);
        Assert.Equal(ManifestEntryStatus.Tampered, results[0].Status);
        Assert.Equal("ggml-vulkan.dll", results[0].Key);
        Assert.Contains("mismatch", results[0].Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sweep_MissingFile_ReturnsMissing()
    {
        // File listed in manifest but never written to disk.
        string bogusHex = new string('f', 64);
        WriteManifest($"{{ \"head_tracker.exe\": \"{bogusHex}\" }}");

        IReadOnlyList<ManifestEntryResult> results =
            IntegrityVerifier.VerifyManifestSweep(_root);

        Assert.Single(results);
        Assert.Equal(ManifestEntryStatus.Missing, results[0].Status);
        Assert.Equal("head_tracker.exe", results[0].Key);
    }

    [Fact]
    public void Sweep_Mixed_GoodTamperedMissing_AllCorrectlyClassified()
    {
        // Good entry.
        byte[] goodBytes = { 1, 2 };
        WriteFile("good.bin", goodBytes);
        string goodHex = Sha256Hex(goodBytes);

        // Tampered entry.
        byte[] originalBytes = { 3, 4 };
        string tamperedPath = WriteFile("tampered.bin", originalBytes);
        string tamperedHex = Sha256Hex(originalBytes);

        // Missing entry (never written to disk).
        string missingHex = new string('e', 64);

        WriteManifest(
            $"{{ \"good.bin\": \"{goodHex}\"," +
            $" \"tampered.bin\": \"{tamperedHex}\"," +
            $" \"missing.bin\": \"{missingHex}\" }}");

        // Tamper the file AFTER writing the manifest.
        File.WriteAllBytes(tamperedPath, new byte[] { 0xFF });

        IReadOnlyList<ManifestEntryResult> results =
            IntegrityVerifier.VerifyManifestSweep(_root);

        Assert.Equal(3, results.Count);

        ManifestEntryResult good     = results.First(r => r.Key == "good.bin");
        ManifestEntryResult tampered = results.First(r => r.Key == "tampered.bin");
        ManifestEntryResult missing  = results.First(r => r.Key == "missing.bin");

        Assert.Equal(ManifestEntryStatus.Ok,       good.Status);
        Assert.Equal(ManifestEntryStatus.Tampered,  tampered.Status);
        Assert.Equal(ManifestEntryStatus.Missing,   missing.Status);
    }

    [Fact]
    public void Sweep_FilesWrapperForm_MatchesCorrectly()
    {
        // The verifier tolerates { "files": { ... } } in both Verify and VerifyManifestSweep.
        byte[] content = { 7, 8, 9 };
        WriteFile("model.gguf", content);
        string hex = Sha256Hex(content);

        WriteManifest($"{{ \"files\": {{ \"model.gguf\": \"{hex}\" }} }}");

        IReadOnlyList<ManifestEntryResult> results =
            IntegrityVerifier.VerifyManifestSweep(_root);

        Assert.Single(results);
        Assert.Equal(ManifestEntryStatus.Ok, results[0].Status);
    }

    [Fact]
    public void Sweep_RelativePathKeyWithForwardSlashes_ResolvesCorrectly()
    {
        // The stage script keys entries as dist-relative paths with forward slashes,
        // e.g. "tools/head-tracker-sidecar/dist/head_tracker/head_tracker.exe".
        // The sweep must resolve these to the correct absolute path under installRoot.
        byte[] content = { 0x10, 0x20 };
        WriteFile("tools/head-tracker-sidecar/dist/head_tracker/head_tracker.exe", content);
        string hex = Sha256Hex(content);

        WriteManifest(
            "{ \"tools/head-tracker-sidecar/dist/head_tracker/head_tracker.exe\":"
            + $" \"{hex}\" }}");

        IReadOnlyList<ManifestEntryResult> results =
            IntegrityVerifier.VerifyManifestSweep(_root);

        Assert.Single(results);
        Assert.Equal(ManifestEntryStatus.Ok, results[0].Status);
    }

    [Fact]
    public void Sweep_MalformedManifestJson_ReturnsSingleErrorEntry()
    {
        // An unreadable/invalid JSON manifest must not throw; it must surface as a
        // single Error entry with key "manifest" so the call-site can react.
        File.WriteAllText(Path.Combine(_root, "install.manifest.json"),
            "{ this is not valid json }}}");

        IReadOnlyList<ManifestEntryResult> results =
            IntegrityVerifier.VerifyManifestSweep(_root);

        Assert.Single(results);
        Assert.Equal("manifest", results[0].Key);
        Assert.Equal(ManifestEntryStatus.Error, results[0].Status);
    }

    [Fact]
    public void Sweep_HashCaseInsensitive_OkForUppercaseManifestValue()
    {
        // Convert.ToHexString produces uppercase; the stage script emits lowercase.
        // Both must be accepted (OrdinalIgnoreCase compare).
        byte[] content = { 0x55 };
        WriteFile("face_landmarker.task", content);
        string upperHex = Sha256Hex(content).ToUpperInvariant();

        WriteManifest($"{{ \"face_landmarker.task\": \"{upperHex}\" }}");

        IReadOnlyList<ManifestEntryResult> results =
            IntegrityVerifier.VerifyManifestSweep(_root);

        Assert.Single(results);
        Assert.Equal(ManifestEntryStatus.Ok, results[0].Status);
    }
}
