using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SenseEase.Core.Inference;

// ─────────────────────────────────────────────────────────────────────────────
// Phase 7b — parser for the three structured token block types the
// chatbot LLM emits inside its reply to drive engine writes:
//
//   [PROFILE_OVERRIDE]
//   { "intent": "adjust_sensitivity" | "switch_profile" | "adjust_strike_limit",
//     "target_field": <string>,
//     "proposed_value": <value>,
//     "rationale": "<natural language reason>",
//     "expires_at_iso": "<UTC timestamp>" }
//   [/PROFILE_OVERRIDE]
//
//   [OBSERVATION_ADD]
//   <one-line natural language observation about user>
//   [/OBSERVATION_ADD]
//
//   [RESUME_ACKNOWLEDGE]
//   { "user_confirmed": true | false,
//     "user_quote": "<verbatim user text>" }
//   [/RESUME_ACKNOWLEDGE]
//
// Pure-logic surface (no I/O, no LLM dependency) so it can be exhaustively
// unit-tested without spinning up llama-server. Mirrors the
// CalibrationLlmDeltaParser pattern at src/SenseEase.Core/Inference/
// CalibrationLlmDeltaParser.cs — same regex+JsonSerializer+DTO discipline,
// same tolerance rules (missing fields → null, malformed JSON → null,
// never throw).
//
// Tolerance rules:
//   • Missing/null fields default to null.
//   • Unknown JSON properties are ignored (additive schema evolution safe).
//   • Enum strings are matched case-insensitively.
//   • PROFILE_OVERRIDE: multiple blocks → first one wins (spec §4.3.1 says
//     "每個 LLM response 最多含一個 PROFILE_OVERRIDE"; tolerant of model
//     re-emit by taking the first).
//   • OBSERVATION_ADD: up to 2 collected per spec §4.3.1; extras dropped.
//   • RESUME_ACKNOWLEDGE: first block wins.
//   • Malformed JSON inside any block → that block becomes null; sibling
//     blocks still parse.
//
// Locked decisions carried in:
//   • Q4.5: ResetOffset is NOT in OverrideIntent. The grammar mentions
//     "reset_offset" historically but per sprint lock the parser silently
//     drops PROFILE_OVERRIDE blocks whose intent doesn't map to one of the
//     three accepted values.
//   • Q4.3: expires_at_iso default is 48h (handled by processor validation,
//     not the parser — parser just extracts the timestamp the model gave).
// ─────────────────────────────────────────────────────────────────────────────

public static class HabituationOverrideParser
{
    private static readonly Regex ProfileOverrideBlockRegex = new(
        @"\[PROFILE_OVERRIDE\](?<body>.*?)\[/PROFILE_OVERRIDE\]",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex ObservationAddBlockRegex = new(
        @"\[OBSERVATION_ADD\](?<body>.*?)\[/OBSERVATION_ADD\]",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex ResumeAcknowledgeBlockRegex = new(
        @"\[RESUME_ACKNOWLEDGE\](?<body>.*?)\[/RESUME_ACKNOWLEDGE\]",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Extracts and parses all three structured-token block types from
    /// the LLM's reply. Returns a fully-populated <see cref="ParsedHabituationOverride"/>
    /// with null/empty fields for absent block types. Never throws; never
    /// returns null (callers can branch on Proposal/ResumeAck/Observations
    /// individually).</summary>
    public static ParsedHabituationOverride TryParse(string? llmReply)
    {
        if (string.IsNullOrWhiteSpace(llmReply))
        {
            return new ParsedHabituationOverride(
                Proposal: null,
                Observations: Array.Empty<string>(),
                ResumeAck: null);
        }

        var proposal = ExtractFirstProposal(llmReply);
        var observations = ExtractObservations(llmReply);
        var resumeAck = ExtractFirstResumeAcknowledge(llmReply);

        return new ParsedHabituationOverride(
            Proposal: proposal,
            Observations: observations,
            ResumeAck: resumeAck);
    }

    /// <summary>Returns <paramref name="llmReply"/> with all three block types
    /// removed so the assistant's visible message reads naturally. Surrounding
    /// whitespace is collapsed conservatively (consecutive blank lines fold
    /// to one). Returns the input unchanged if no blocks are found.</summary>
    public static string StripAllOverrideBlocks(string? llmReply)
    {
        if (string.IsNullOrEmpty(llmReply)) return llmReply ?? string.Empty;
        string stripped = ProfileOverrideBlockRegex.Replace(llmReply, string.Empty);
        stripped = ObservationAddBlockRegex.Replace(stripped, string.Empty);
        stripped = ResumeAcknowledgeBlockRegex.Replace(stripped, string.Empty);
        // Collapse the blank-line tower the strip can leave behind.
        stripped = Regex.Replace(stripped, "\r?\n\\s*\r?\n\\s*\r?\n", "\n\n");
        return stripped.Trim();
    }

    // ── PROFILE_OVERRIDE extraction ──────────────────────────────────────────

    private static ProfileOverrideProposal? ExtractFirstProposal(string llmReply)
    {
        foreach (Match match in ProfileOverrideBlockRegex.Matches(llmReply))
        {
            string body = match.Groups["body"].Value.Trim();
            if (body.Length == 0) continue;

            ProfileOverrideDto? dto;
            try
            {
                dto = JsonSerializer.Deserialize<ProfileOverrideDto>(body, JsonOpts);
            }
            catch (JsonException)
            {
                continue; // malformed — try next block (rare; spec says first wins, this is belt-and-suspenders)
            }
            if (dto is null) continue;

            var intent = ParseIntent(dto.Intent);
            if (intent is null) continue; // unknown intent (e.g., dropped "reset_offset") — skip

            DateTime expires;
            if (!TryParseUtcIso(dto.ExpiresAtIso, out expires)) continue;

            return new ProfileOverrideProposal(
                Intent: intent.Value,
                TargetField: dto.TargetField ?? string.Empty,
                ProposedValue: NormaliseProposedValue(dto.ProposedValue),
                Rationale: dto.Rationale ?? string.Empty,
                ExpiresAtUtc: expires);
        }
        return null;
    }

    private static OverrideIntent? ParseIntent(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.Trim().ToLowerInvariant() switch
        {
            "adjust_sensitivity"  => OverrideIntent.AdjustSensitivity,
            "switch_profile"      => OverrideIntent.SwitchProfile,
            "adjust_strike_limit" => OverrideIntent.AdjustStrikeLimit,
            _                     => null,
        };
    }

    private static bool TryParseUtcIso(string? s, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        return DateTime.TryParse(
            s,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out utc);
    }

    /// <summary>Unwrap System.Text.Json's JsonElement to a plain CLR value the
    /// processor can switch on. Strings stay strings, integers come back as
    /// long, fractional numbers as double, bools as bool. Arrays/objects
    /// collapse to a string representation (the processor doesn't need them
    /// today).
    /// <para>Note the explicit <c>(object)</c> cast on the integer branch:
    /// without it C#'s ternary common-type inference widens <c>long → double</c>
    /// and integer JSON values silently become doubles — a bug that bit on
    /// first test run.</para></summary>
    private static object NormaliseProposedValue(JsonElement? maybeElem)
    {
        if (maybeElem is not { } elem) return string.Empty;
        return elem.ValueKind switch
        {
            JsonValueKind.String => elem.GetString() ?? string.Empty,
            JsonValueKind.Number => elem.TryGetInt64(out long l) ? (object)l : elem.GetDouble(),
            JsonValueKind.True   => true,
            JsonValueKind.False  => false,
            JsonValueKind.Null   => string.Empty,
            _                    => elem.GetRawText(),
        };
    }

    // ── OBSERVATION_ADD extraction ───────────────────────────────────────────

    private static IReadOnlyList<string> ExtractObservations(string llmReply)
    {
        var matches = ObservationAddBlockRegex.Matches(llmReply);
        if (matches.Count == 0) return Array.Empty<string>();

        var list = new List<string>(Math.Min(matches.Count, 2));
        foreach (Match m in matches)
        {
            string body = m.Groups["body"].Value.Trim();
            if (body.Length == 0) continue;
            list.Add(body);
            if (list.Count >= 2) break; // spec cap §4.3.1: 0-2 per response
        }
        return list;
    }

    // ── RESUME_ACKNOWLEDGE extraction ────────────────────────────────────────

    private static ResumeAcknowledge? ExtractFirstResumeAcknowledge(string llmReply)
    {
        foreach (Match match in ResumeAcknowledgeBlockRegex.Matches(llmReply))
        {
            string body = match.Groups["body"].Value.Trim();
            if (body.Length == 0) continue;

            ResumeAcknowledgeDto? dto;
            try
            {
                dto = JsonSerializer.Deserialize<ResumeAcknowledgeDto>(body, JsonOpts);
            }
            catch (JsonException)
            {
                continue;
            }
            if (dto is null) continue;
            if (!dto.UserConfirmed.HasValue) continue; // required field per spec

            return new ResumeAcknowledge(
                UserConfirmed: dto.UserConfirmed.Value,
                UserQuote: dto.UserQuote ?? string.Empty);
        }
        return null;
    }

    // ── DTOs (private) — snake_case keys via PropertyNamingPolicy ────────────

    private sealed class ProfileOverrideDto
    {
        [JsonPropertyName("intent")]
        public string? Intent { get; set; }

        [JsonPropertyName("target_field")]
        public string? TargetField { get; set; }

        // Type-erased value: numbers, strings, bools all arrive as JsonElement.
        // The parser normalises via NormaliseProposedValue; the processor's
        // validation rules switch on intent and unpack to the matching type.
        [JsonPropertyName("proposed_value")]
        public JsonElement? ProposedValue { get; set; }

        [JsonPropertyName("rationale")]
        public string? Rationale { get; set; }

        [JsonPropertyName("expires_at_iso")]
        public string? ExpiresAtIso { get; set; }
    }

    private sealed class ResumeAcknowledgeDto
    {
        [JsonPropertyName("user_confirmed")]
        public bool? UserConfirmed { get; set; }

        [JsonPropertyName("user_quote")]
        public string? UserQuote { get; set; }
    }
}

/// <summary>Result record returned by <see cref="HabituationOverrideParser.TryParse"/>.
/// Callers branch on the three sub-records independently — any combination
/// (or none) of the block types may be present in a single LLM reply.</summary>
public sealed record ParsedHabituationOverride(
    ProfileOverrideProposal? Proposal,
    IReadOnlyList<string> Observations,
    ResumeAcknowledge? ResumeAck);

/// <summary>A chatbot-proposed engine override, before validation. The
/// processor (Phase 7c) decides accept/reject; the applicator (Phase 7d)
/// performs the engine write only after the user explicitly confirms via
/// the Card UI (Phase 7e).</summary>
public sealed record ProfileOverrideProposal(
    OverrideIntent Intent,
    string TargetField,
    object ProposedValue,
    string Rationale,
    DateTime ExpiresAtUtc);

/// <summary>Three accepted PROFILE_OVERRIDE intents. ResetOffset is
/// deliberately absent per Q4.5 (sprint-state-locked decision): "chatbot
/// proposing a full habituation reset" is too high-impact for the propose-
/// confirm channel; users seeking that path go through Settings.</summary>
public enum OverrideIntent
{
    AdjustSensitivity,
    SwitchProfile,
    AdjustStrikeLimit,
}

/// <summary>Result of a [RESUME_ACKNOWLEDGE] block. Caller (Phase 7d
/// applicator) only applies <c>AcknowledgeAndResume</c> when
/// <see cref="UserConfirmed"/> is true; <see cref="UserQuote"/> goes into
/// the event log for traceability.</summary>
public sealed record ResumeAcknowledge(
    bool UserConfirmed,
    string UserQuote);
