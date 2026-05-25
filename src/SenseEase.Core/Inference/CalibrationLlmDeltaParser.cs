using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SenseEase.Core.Inference;

// ─────────────────────────────────────────────────────────────────────────────
// Stage 4c — parser for the [CALIBRATION_DELTA] block emitted by the
// calibration LLM dialog. Pure-logic surface (no I/O, no LLM dependency) so
// it can be exhaustively unit-tested without spinning up llama-server.
//
// Format spec (mirrors the [OUTPUT] block of the system prompt in
// CalibrationChatbotService):
//
//   [CALIBRATION_DELTA]
//   { "delta": { q1_segment, q1_freetext, q2_symptoms, q2_freetext,
//                q3_intensity, q3_freetext, q4_comparison, q4_no_baseline,
//                q4_freetext, q5_risk_factors, q5_freetext },
//     "confidence": { "q1": 0..1, "q2": 0..1, "q3": 0..1, "q4": 0..1, "q5": 0..1 },
//     "longitudinal_observation": string | null,
//     "is_complete": bool,
//     "needs_followup_on": [string] }
//   [/CALIBRATION_DELTA]
//
// Tolerance rules:
//   • Missing/null fields default to null (or empty array for needs_followup_on).
//   • Unknown JSON properties are ignored — additive schema evolution doesn't break callers.
//   • Enum strings are matched case-insensitively (UncomfortableSegment, etc.).
//   • Multiple delta blocks → first one wins (model occasionally re-emits).
//   • Malformed JSON inside the block → null (caller falls back).
// ─────────────────────────────────────────────────────────────────────────────

public static class CalibrationLlmDeltaParser
{
    private static readonly Regex BlockRegex = new(
        @"\[CALIBRATION_DELTA\](?<body>.*?)\[/CALIBRATION_DELTA\]",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Extracts and parses the first [CALIBRATION_DELTA] block.
    /// Returns null if no block is present or the JSON body is malformed.
    /// Never throws.</summary>
    public static ParsedDelta? TryParse(string? llmReply)
    {
        if (string.IsNullOrWhiteSpace(llmReply)) return null;
        var match = BlockRegex.Match(llmReply);
        if (!match.Success) return null;

        string body = match.Groups["body"].Value.Trim();
        if (body.Length == 0) return null;

        DeltaDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<DeltaDto>(body, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
        if (dto is null) return null;

        var delta = MapInnerDelta(dto.Delta);
        double[]? confidence = NormalizeConfidence(dto.Confidence);
        string[] followups = dto.NeedsFollowupOn ?? Array.Empty<string>();

        return new ParsedDelta(
            Delta: delta,
            Confidence: confidence,
            LongitudinalObservation: string.IsNullOrWhiteSpace(dto.LongitudinalObservation) ? null : dto.LongitudinalObservation,
            IsComplete: dto.IsComplete ?? false,
            NeedsFollowupOn: followups);
    }

    /// <summary>Returns <paramref name="llmReply"/> with every
    /// [CALIBRATION_DELTA]...[/CALIBRATION_DELTA] block removed. Surrounding
    /// whitespace is collapsed conservatively so the user-visible text still
    /// reads naturally (two consecutive blank lines collapse to one). Returns
    /// the input unchanged if no block is found.</summary>
    public static string StripDeltaBlock(string? llmReply)
    {
        if (string.IsNullOrEmpty(llmReply)) return llmReply ?? string.Empty;
        string stripped = BlockRegex.Replace(llmReply, string.Empty);
        // Collapse the blank-line tower the strip can leave behind.
        stripped = Regex.Replace(stripped, "\r?\n\\s*\r?\n\\s*\r?\n", "\n\n");
        return stripped.Trim();
    }

    // ── DTOs (private) — snake_case keys via PropertyNamingPolicy ────────────

    private sealed class DeltaDto
    {
        [JsonPropertyName("delta")]
        public InnerDeltaDto? Delta { get; set; }

        // Wire format: confidence is a JSON OBJECT with q1..q5 keys (per Stage 4
        // prompt review — array form was error-prone for the LLM, which would
        // occasionally emit wrong-length arrays and silently shift Q3's value
        // into Q2's slot). Internal C# representation stays double[5] indexed
        // 0..4 (Q1..Q5) so consumers like CalibrationChatbotService.NeedsQ3Fallback
        // can keep their existing Confidence[2] access pattern.
        [JsonPropertyName("confidence")]
        public ConfidenceDto? Confidence { get; set; }

        [JsonPropertyName("longitudinal_observation")]
        public string? LongitudinalObservation { get; set; }

        [JsonPropertyName("is_complete")]
        public bool? IsComplete { get; set; }

        [JsonPropertyName("needs_followup_on")]
        public string[]? NeedsFollowupOn { get; set; }
    }

    /// <summary>JSON sub-record for the confidence object. Missing keys
    /// default to 0.0 (least confident) per Stage 4 spec — caller's downstream
    /// rules then treat low confidence as "no data" / "request fallback".</summary>
    private sealed class ConfidenceDto
    {
        [JsonPropertyName("q1")] public double Q1 { get; set; }
        [JsonPropertyName("q2")] public double Q2 { get; set; }
        [JsonPropertyName("q3")] public double Q3 { get; set; }
        [JsonPropertyName("q4")] public double Q4 { get; set; }
        [JsonPropertyName("q5")] public double Q5 { get; set; }
    }

    private sealed class InnerDeltaDto
    {
        [JsonPropertyName("q1_segment")]      public string? Q1Segment { get; set; }
        [JsonPropertyName("q1_freetext")]     public string? Q1Freetext { get; set; }
        [JsonPropertyName("q2_symptoms")]     public string[]? Q2Symptoms { get; set; }
        [JsonPropertyName("q2_freetext")]     public string? Q2Freetext { get; set; }
        [JsonPropertyName("q3_intensity")]    public int?     Q3Intensity { get; set; }
        [JsonPropertyName("q3_freetext")]     public string? Q3Freetext { get; set; }
        [JsonPropertyName("q4_comparison")]   public string? Q4Comparison { get; set; }
        [JsonPropertyName("q4_no_baseline")]  public bool?    Q4NoBaseline { get; set; }
        [JsonPropertyName("q4_freetext")]     public string? Q4Freetext { get; set; }
        [JsonPropertyName("q5_risk_factors")] public string[]? Q5RiskFactors { get; set; }
        [JsonPropertyName("q5_freetext")]     public string? Q5Freetext { get; set; }
    }

    // ── Enum + record mapping ─────────────────────────────────────────────────

    private static CalibrationResponses? MapInnerDelta(InnerDeltaDto? d)
    {
        if (d is null) return null;

        var seg = ParseEnum<UncomfortableSegment>(d.Q1Segment);
        var symptoms = MapEnumList<SymptomType>(d.Q2Symptoms);
        int? intensity = d.Q3Intensity.HasValue
            ? (d.Q3Intensity.Value is >= 1 and <= 5 ? d.Q3Intensity : null)
            : null;
        var cmp = ParseEnum<BaselineComparison>(d.Q4Comparison);
        var risks = MapEnumList<RiskFactor>(d.Q5RiskFactors);

        // All fields source-tagged Llm; the caller (CalibrationChatbotService)
        // overwrites individual fields with LlmLowConfidence when the
        // corresponding confidence[i] < 0.5 in the per-field rule the merger
        // applies. Default at the parser level is plain Llm.
        return new CalibrationResponses
        {
            Q1Segment    = seg,
            Q1Freetext   = NullIfBlank(d.Q1Freetext),
            Q2Symptoms   = symptoms,
            Q2Freetext   = NullIfBlank(d.Q2Freetext),
            Q3Intensity  = intensity,
            Q3Freetext   = NullIfBlank(d.Q3Freetext),
            Q4Comparison = cmp,
            Q4NoBaseline = d.Q4NoBaseline ?? false,
            Q4Freetext   = NullIfBlank(d.Q4Freetext),
            Q5RiskFactors = risks,
            Q5Freetext   = NullIfBlank(d.Q5Freetext),
            Q1Source = CalibrationSource.Llm,
            Q2Source = CalibrationSource.Llm,
            Q3Source = CalibrationSource.Llm,
            Q4Source = CalibrationSource.Llm,
            Q5Source = CalibrationSource.Llm,
        };
    }

    private static T? ParseEnum<T>(string? s) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return Enum.TryParse<T>(s, ignoreCase: true, out var v) ? v : null;
    }

    private static IReadOnlyList<T>? MapEnumList<T>(string[]? items) where T : struct, Enum
    {
        if (items is null) return null;
        var list = new List<T>(items.Length);
        foreach (var s in items)
        {
            if (Enum.TryParse<T>(s, ignoreCase: true, out var v))
                list.Add(v);
            // Silently drop unparseable entries — additive enum vocabulary on
            // the model side shouldn't crash the parser.
        }
        return list;
    }

    private static string? NullIfBlank(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static double[]? NormalizeConfidence(ConfidenceDto? dto)
    {
        // Stage 4 review: wire format is object, internal representation is
        // a 5-element array indexed Q1=0 .. Q5=4 so existing consumers
        // (CalibrationChatbotService.NeedsQ3Fallback reads confidence[2])
        // don't change. Missing JSON keys default to 0.0 per System.Text.Json
        // value-type default behaviour, which matches the spec ("缺欄位預設為 0.0").
        if (dto is null) return null;
        return new[] { dto.Q1, dto.Q2, dto.Q3, dto.Q4, dto.Q5 };
    }
}

public sealed record ParsedDelta(
    CalibrationResponses? Delta,
    double[]? Confidence,
    string? LongitudinalObservation,
    bool IsComplete,
    string[] NeedsFollowupOn);
