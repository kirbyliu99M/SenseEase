using SenseEase.Core.Models;

namespace SenseEase.Core.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// Stage 4 — Calibration LLM dialog test seam.
//
// The production CalibrationChatbotService talks HTTP to the local llama-server
// subprocess (spawned by MistralChatbotService). For unit testing we don't want
// to start a real server, so the service consumes this abstraction instead of
// calling HTTP directly. Tests inject a stub that returns canned LLM replies.
//
// This is INTENTIONALLY a separate abstraction from IChatbotService:
//   • IChatbotService models the NLU-during-runtime path (returns NluResult
//     with shader deltas).
//   • ICalibrationLlmClient models the calibration-dialog path (returns the
//     raw text reply; the [CALIBRATION_DELTA] block is parsed by a separate
//     parser, not embedded in the abstraction).
// Mixing the two would create an awkward union type and bleed concerns.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Thin contract over the local llama-server chat endpoint for the
/// calibration-dialog path. The caller supplies the (immutable) system prompt
/// plus the running list of user/assistant turns. Implementations send all
/// three to the LLM and return the assistant's raw text reply. The reply may
/// contain a [CALIBRATION_DELTA] block — parsing that out is the caller's
/// responsibility (see <c>CalibrationLlmDeltaParser</c>).
/// </summary>
public interface ICalibrationLlmClient
{
    /// <summary>Sends the system prompt + chat history to the LLM and returns
    /// the raw assistant reply. Returns <c>null</c> if the LLM is unavailable
    /// or the call failed — the caller decides how to handle that (e.g. emit
    /// a graceful error and end the session). Never throws on network errors;
    /// only throws on truly programmer-error inputs (null arguments).
    ///
    /// <paramref name="systemPrompt"/> is the [SYSTEM] role message for the
    /// underlying chat endpoint.
    /// <paramref name="history"/> is the alternating user/assistant transcript
    /// (oldest first). May optionally include a final system-style
    /// "convergence" reminder appended by the caller at turn 8 — implementations
    /// should treat anything beyond the first system prompt as part of the
    /// running conversation.</summary>
    Task<string?> SendChatAsync(
        string systemPrompt,
        IReadOnlyList<ChatMessage> history,
        CancellationToken ct = default);
}
