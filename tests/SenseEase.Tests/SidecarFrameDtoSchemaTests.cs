using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using SenseEase.Infrastructure.Real;
using Xunit;

namespace SenseEase.Tests;

/// <summary>E3 (Alpha-1 engine-integrity): privacy-posture schema lock on the
/// IPC DTO that crosses the sidecar boundary
/// (<see cref="SidecarHeadPoseReader"/>'s internal <c>SidecarFrameDto</c>).
///
/// <para>The sidecar's <c>PRIVACY.md</c> invariant (see the class doc on
/// <see cref="SidecarHeadPoseReader"/>) is that ONLY derived fields cross the
/// process boundary — never raw landmarks, blendshapes, eye geometry, or frame
/// imagery. The C# parse DTO is the last enforcement seam on the host side: if
/// a future sidecar revision starts emitting a privacy-sensitive key and a
/// developer adds a matching property here to "accept" it, that key would begin
/// silently flowing into the host. These reflection tests turn that into a
/// compile-against-reality unit-test failure instead.</para>
///
/// <para>Two guarantees are pinned:</para>
/// <list type="number">
///   <item>An EXACT allowlist of <see cref="JsonPropertyName"/> values — adding
///         OR removing a mapped JSON key (or renaming one) fails the test, so
///         the schema can only change with a conscious test edit.</item>
///   <item>A FORBID rule: no property name (CLR name OR JSON name) may contain
///         <c>landmark</c>, <c>eye</c>, <c>blendshape</c>, or <c>image</c> —
///         the privacy-sensitive field families that must never cross IPC.</item>
/// </list>
///
/// <para>The DTO is an internal nested type; the test assembly reaches it via
/// <c>InternalsVisibleTo SenseEase.Tests</c> on SenseEase.Infrastructure. We
/// resolve it by reflection (rather than a direct type reference) so the test
/// also asserts the type still LIVES where the privacy story documents it.</para>
/// </summary>
public class SidecarFrameDtoSchemaTests
{
    // The privacy-sensitive field families that MUST NOT cross the IPC boundary.
    // Matched case-insensitively against both the CLR property name and the
    // JsonPropertyName value. Mirrors the PRIVACY.md posture: derived scalars
    // only — no raw landmarks, no blendshape vectors, no eye geometry, no frame
    // imagery.
    private static readonly string[] ForbiddenSubstrings =
        { "landmark", "eye", "blendshape", "image" };

    /// <summary>Locate the internal DTO type by reflection over the
    /// SenseEase.Infrastructure assembly. Asserts it still exists and is nested
    /// in <see cref="SidecarHeadPoseReader"/> within the documented namespace —
    /// resolving by name (not a direct reference) is deliberate so a move/rename
    /// surfaces here rather than silently relocating the privacy seam.</summary>
    private static Type ResolveSidecarFrameDtoType()
    {
        Type? dto = typeof(SidecarHeadPoseReader)
            .GetNestedType("SidecarFrameDto", BindingFlags.NonPublic | BindingFlags.Public);

        Assert.NotNull(dto);
        Assert.Equal("SenseEase.Infrastructure.Real", dto!.DeclaringType!.Namespace);
        return dto;
    }

    private static IReadOnlyList<PropertyInfo> DtoProperties()
        => ResolveSidecarFrameDtoType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToArray();

    private static string? JsonNameOf(PropertyInfo p)
        => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;

    // ── 1) Exact JsonPropertyName allowlist ─────────────────────────────────

    [Fact]
    public void SidecarFrameDto_JsonPropertyNames_MatchExactAllowlist()
    {
        // EXACT allowlist derived from the real DTO attributes. Adding,
        // removing, or renaming a mapped JSON key fails this — the schema is
        // locked and can only move with a conscious edit to this set.
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "t_ms",
            "cx",
            "cy",
            "luma",
            "face_detected",
            "face_present",
            "yaw_deg",
            "pitch_deg",
        };

        var actual = new HashSet<string>(
            DtoProperties().Select(JsonNameOf).Where(n => n is not null)!,
            StringComparer.Ordinal);

        // SetEquals gives a single assertion that fails on any add/remove/rename.
        Assert.True(
            actual.SetEquals(expected),
            "SidecarFrameDto JsonPropertyName set drifted from the privacy allowlist. " +
            $"Expected [{string.Join(", ", expected.OrderBy(x => x))}], " +
            $"got [{string.Join(", ", actual.OrderBy(x => x))}]. " +
            "If this is intentional, update the allowlist AND re-confirm the " +
            "sidecar PRIVACY.md posture (derived scalars only).");
    }

    [Fact]
    public void SidecarFrameDto_EveryMappedProperty_HasExplicitJsonPropertyName()
    {
        // The snake_case wire contract relies on explicit [JsonPropertyName] on
        // EVERY property — no property may lean on default PascalCase->json
        // naming, which would silently diverge from the sidecar's emitted keys.
        var unmapped = DtoProperties()
            .Where(p => JsonNameOf(p) is null)
            .Select(p => p.Name)
            .ToArray();

        Assert.True(
            unmapped.Length == 0,
            $"SidecarFrameDto properties without an explicit [JsonPropertyName]: " +
            $"[{string.Join(", ", unmapped)}].");
    }

    [Fact]
    public void SidecarFrameDto_JsonPropertyNames_AreSnakeCaseLowerInvariant()
    {
        // Invariant (robust against future field additions): the wire schema is
        // lower snake_case — no uppercase letters in any mapped JSON key. This
        // holds for the whole current set and is the contract the sidecar emits.
        foreach (PropertyInfo p in DtoProperties())
        {
            string? json = JsonNameOf(p);
            if (json is null) continue;
            Assert.True(
                json == json.ToLowerInvariant(),
                $"JsonPropertyName '{json}' on '{p.Name}' is not lower-invariant snake_case.");
        }
    }

    // ── 2) Privacy forbid-list ──────────────────────────────────────────────

    [Theory]
    [InlineData("landmark")]
    [InlineData("eye")]
    [InlineData("blendshape")]
    [InlineData("image")]
    public void SidecarFrameDto_NoProperty_NameOrJsonName_ContainsForbiddenSubstring(string forbidden)
    {
        // Privacy hard-stop: no CLR property name and no JsonPropertyName may
        // contain a privacy-sensitive family token. A future dev who adds e.g.
        // an "eye_openness" or "landmark_x" property to accept a new sidecar
        // key would trip this and have to consciously confront the privacy
        // posture rather than silently widening the IPC surface.
        foreach (PropertyInfo p in DtoProperties())
        {
            Assert.DoesNotContain(forbidden, p.Name, StringComparison.OrdinalIgnoreCase);

            string? json = JsonNameOf(p);
            if (json is not null)
            {
                Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void SidecarFrameDto_NoProperty_TripsAnyForbiddenSubstring_Aggregate()
    {
        // Belt + suspenders over the [Theory]: a single aggregate sweep across
        // ALL forbidden families at once, so adding a new forbidden token to
        // ForbiddenSubstrings is covered even if a dev forgets a [InlineData].
        var violations = new List<string>();
        foreach (PropertyInfo p in DtoProperties())
        {
            string? json = JsonNameOf(p);
            foreach (string forbidden in ForbiddenSubstrings)
            {
                if (p.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                    violations.Add($"{p.Name} (CLR name contains '{forbidden}')");
                if (json is not null &&
                    json.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                    violations.Add($"{p.Name} -> '{json}' (JSON name contains '{forbidden}')");
            }
        }

        Assert.True(
            violations.Count == 0,
            "SidecarFrameDto exposes privacy-forbidden field(s): " +
            string.Join("; ", violations) +
            ". The IPC boundary must carry derived scalars only — no landmarks, " +
            "eye geometry, blendshapes, or frame imagery.");
    }
}
