using SenseEase.Core.Models;

namespace SenseEase.Core.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// Local-LLM dialog test seam (originally Stage-4 calibration; the calibration
// card was removed 2026-06-08 and this is now consumed by the User-Model
// retrospective analyzer).
//
// The implementation talks HTTP to the local llama-server subprocess (spawned by
// MistralChatbotService). For unit testing we don't want to start a real server,
// so consumers use this abstraction instead of calling HTTP directly; tests
// inject a stub that returns canned LLM replies.
//
// INTENTIONALLY separate from IChatbotService: that models the NLU-during-runtime
// path (NluResult with shader deltas), while this returns the assistant's raw
// text reply for a multi-turn dialog. Mixing the two would bleed concerns.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Thin contract over the local llama-server chat endpoint for a multi-turn
/// dialog. The caller supplies the (immutable) system prompt plus the running
/// list of user/assistant turns; implementations send all three to the LLM and
/// return the assistant's raw text reply (the caller parses any structured block).
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
