using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SenseEase.Core.Inference;

/// <summary>
/// B6 Stage 1 — parser for the UM4 retrospective analyzer's OPTIONAL <c>[ADAPT_DIRECTION]</c> block: the
/// chatbot's ONLY channel to PROPOSE an adaptation direction to the deterministic AdaptationGate.
/// Deliberately a SEPARATE grammar from <c>[PROFILE_OVERRIDE]</c> (<see cref="HabituationOverrideParser"/>) —
/// that block drives the live-chat, user-facing Accept-card; keeping the retrospective relax elicitation on a
/// DISTINCT tag (B6 red-team RT-1b) stops prompt-generalisation from spamming that card. The gate validates
/// the DIRECTION against measured-only evidence and sizes the step itself, so a wrong / absent / expired
/// proposal is harmless — it can never relax protection the measured data doesn't independently support.
///
/// <code>
///   [ADAPT_DIRECTION]
///   { "intent": "adjust_sensitivity",
///     "proposed_value": &lt;number&gt;,        // a target sensitivity scale; the gate maps it to a DIRECTION only
///     "rationale": "&lt;short reason&gt;",
///     "expires_at_iso": "&lt;UTC timestamp&gt;" }
///   [/ADAPT_DIRECTION]
/// </code>
///
/// Pure; never throws; missing / malformed / non-finite / wrong-intent → null. Mirrors the
/// <see cref="HabituationOverrideParser"/> regex + JsonSerializer + tolerance discipline (and reuses its
/// <c>SanitizeRationale</c> injection-defang so the render-only rationale is hardened identically).
/// </summary>
public static class AdaptDirectionParser
{
    /// <summary>Freshness default when the model omits <c>expires_at_iso</c>: the proposal is treated as
    /// stale (⇒ Hold) once older than this (B6 RT-2s), so one session's analysis can't stand for weeks.</summary>
    public const double DefaultFreshnessHours = 72.0;

    private static readonly Regex BlockRegex = new(
        @"\[ADAPT_DIRECTION\](?<body>.*?)\[/ADAPT_DIRECTION\]",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Extract the FIRST well-formed <c>[ADAPT_DIRECTION]</c> proposal, or null when the slice has
    /// none / it is malformed / the intent isn't <c>adjust_sensitivity</c> / the value isn't finite.
    /// <paramref name="nowUtc"/> seeds the freshness default when the model omits an expiry (injected so the
    /// pure parser stays deterministically unit-testable).</summary>
    public static AdaptDirectionProposal? TryParse(string? llmReply, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(llmReply)) return null;

        foreach (Match m in BlockRegex.Matches(llmReply))
        {
            string body = m.Groups["body"].Value.Trim();
            if (body.Length == 0) continue;

            AdaptDirectionDto? dto;
            try { dto = JsonSerializer.Deserialize<AdaptDirectionDto>(body, JsonOpts); }
            catch (JsonException) { continue; } // malformed body → try the next block (belt-and-suspenders)
            if (dto is null) continue;

            // Only adjust_sensitivity is a direction channel today; anything else is ignored (fail-safe).
            if (!string.Equals(dto.Intent?.Trim(), "adjust_sensitivity", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!TryGetFiniteDouble(dto.ProposedValue, out double scale)) continue;

            DateTime expires = TryParseUtcIso(dto.ExpiresAtIso, out DateTime parsed)
                ? parsed
                : nowUtc.AddHours(DefaultFreshnessHours);

            return new AdaptDirectionProposal(
                ProposedScale: scale,
                Rationale: HabituationOverrideParser.SanitizeRationale(dto.Rationale),
                ExpiresAtUtc: expires);
        }
        return null;
    }

    private static bool TryGetFiniteDouble(JsonElement? maybeElem, out double result)
    {
        result = 0.0;
        if (maybeElem is not { ValueKind: JsonValueKind.Number } elem) return false;
        if (!elem.TryGetDouble(out double d) || !double.IsFinite(d)) return false;
        result = d;
        return true;
    }

    private static bool TryParseUtcIso(string? s, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        return DateTime.TryParse(
            s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);
    }

    private sealed class AdaptDirectionDto
    {
        [JsonPropertyName("intent")] public string? Intent { get; set; }
        [JsonPropertyName("proposed_value")] public JsonElement? ProposedValue { get; set; }
        [JsonPropertyName("rationale")] public string? Rationale { get; set; }
        [JsonPropertyName("expires_at_iso")] public string? ExpiresAtIso { get; set; }
    }
}

/// <summary>A chatbot-proposed adaptation DIRECTION hint from the UM4 retrospective analysis (B6 Stage 1).
/// The gate maps <see cref="ProposedScale"/> to a sign only (vs the live personal pressure scale), sizes the
/// step itself, and re-validates against measured data; <see cref="Rationale"/> is render-only (already
/// injection-defanged); <see cref="ExpiresAtUtc"/> bounds staleness.</summary>
public sealed record AdaptDirectionProposal(double ProposedScale, string Rationale, DateTime ExpiresAtUtc);
