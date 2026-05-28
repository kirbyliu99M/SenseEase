using System;
using System.Collections.Generic;
using SenseEase.Core.Inference;

namespace SenseEase.Core.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// Phase 7g (Q4.7 = B locked): a separate log tracking chatbot-driven engine
// changes. The existing IAdjustmentLog records FOV adjustments from any
// source; IChatbotActionLog records the proposal lifecycle (proposed →
// accepted/declined/expired/intercepted) so:
//
//   • The Processor (Phase 7c) can answer "did the user decline this intent
//     in the last 7 days?" for rule 5.
//   • Audit trail: a chatbot misbehaviour can be traced separately from
//     normal user adjustments.
//   • The chatbot can be told via context "you proposed X yesterday and it
//     was declined" so it doesn't immediately re-propose.
//
// In-memory implementation lives alongside (InMemoryChatbotActionLog). A
// file-backed JSONL implementation can come later — the in-memory version
// suffices for tests and the initial production flow (action history
// resetting on app restart is acceptable for v0.9 per Q4.6 stateless
// chatbot decision).
// ─────────────────────────────────────────────────────────────────────────────

public interface IChatbotActionLog
{
    /// <summary>Append a new action entry. Timestamp = UTC now.</summary>
    void Record(ChatbotActionKind kind, ProfileOverrideProposal proposal, string? reason = null);

    /// <summary>Returns the timestamp of the most recent <c>Declined</c>
    /// entry whose intent matches. Null = never declined that intent. Used
    /// by Processor rule 5 (7-day cooldown).</summary>
    DateTime? MostRecentDeclineFor(OverrideIntent intent);

    /// <summary>Returns the most recent entries in reverse chronological
    /// order, capped at <paramref name="count"/>.</summary>
    IReadOnlyList<ChatbotActionEntry> Recent(int count = 20);
}

/// <summary>Kinds of chatbot-action lifecycle events. <c>Proposed</c> is
/// recorded when the parser extracts a proposal AND the processor accepts
/// it (i.e. the Card UI is about to show); the user's response then
/// produces an <c>Accepted</c> / <c>Declined</c> / <c>Expired</c> entry.
/// <c>Intercepted</c> is logged when the Applicator's Phase 7f defensive
/// check blocks an Apply that the Processor had accepted (the snapshot
/// changed in the window between propose and confirm).</summary>
public enum ChatbotActionKind
{
    Proposed,
    Accepted,
    Declined,
    Expired,
    Intercepted,
}

public sealed record ChatbotActionEntry(
    DateTime TimestampUtc,
    ChatbotActionKind Kind,
    OverrideIntent Intent,
    object ProposedValue,
    string Rationale,
    string? Reason);
