namespace SenseEase.Core.Models;

/// <summary>Result of interpreting a user feedback utterance (mock NLU for MVP).</summary>
public class NluResult
{
    /// <summary>Detected intent: ReportDiscomfort | ReportComfort | ResetRequest | AnalyzeRequest | FollowupRequest | Unknown.</summary>
    public string Intent { get; set; } = "Unknown";

    /// <summary>Suggested change to the FOV radius percent (negative = shrink).</summary>
    public int FovDelta { get; set; }

    /// <summary>Suggested delta to ShaderParams.MaskDepth (normalized, clamped [-0.3,0.3]).</summary>
    public double MaskDepthDelta { get; set; }

    /// <summary>Suggested delta to ShaderParams.GridVisibility (normalized, clamped [-0.3,0.3]).</summary>
    public double GridVisibilityDelta { get; set; }

    /// <summary>Suggested delta to ShaderParams.FeatherWidth (normalized, clamped [-0.3,0.3]).</summary>
    public double FeatherWidthDelta { get; set; }

    /// <summary>Human-readable response shown back to the user.</summary>
    public string Response { get; set; } = string.Empty;

    /// <summary>
    /// Optional followup question the assistant wants to ask the user before committing
    /// to an adjustment. When set, the UI renders <see cref="FollowupOptions"/> as
    /// clickable cards (Claude-style picker) alongside the always-available free-text
    /// input. Null when no followup is needed.
    /// </summary>
    public string? FollowupQuestion { get; set; }

    /// <summary>
    /// 2–4 predefined option strings shown as clickable cards under the bot's message.
    /// Each option, when tapped, becomes the user's next message. Empty/null when no
    /// followup is requested. Intent for the response should be <c>FollowupRequest</c>.
    /// </summary>
    public string[]? FollowupOptions { get; set; }

    /// <summary>M3 Phase 7e: the LLM's full raw output (post-markdown-strip,
    /// pre-JSON-parse) carried through alongside the deserialized envelope.
    /// ChatbotViewModel runs <c>HabituationOverrideParser</c> on this so the
    /// write-side structured tokens ([PROFILE_OVERRIDE] / [OBSERVATION_ADD] /
    /// [RESUME_ACKNOWLEDGE]) survive even when the model:
    ///   • emits the override block AFTER the envelope's closing brace
    ///     (correct per spec, but invisible in Response),
    ///   • embeds the block INSIDE the envelope (malformed; envelope
    ///     parse fails → result builds from lexicon fallback so Response
    ///     no longer carries the original LLM text).
    /// Null = lexicon fallback path (model not loaded / envelope parse failed
    /// AND the caller didn't carry raw through), or safety-floor short-circuit.</summary>
    public string? RawModelOutput { get; set; }
}
