namespace SenseEase.Core.Models;

/// <summary>
/// UM3 (User Model sprint): incremental analysis state, persisted to
/// <c>%LOCALAPPDATA%\SenseEase\user_model_index.json</c> via the atomic writer.
/// Lets the UM4 retrospective pipeline resume from where it left off without
/// re-reading the whole chat history.
///
/// <para>B6 Stage 0 (2026-06-21): the per-month <c>BucketManifest</c>/<c>BucketMeta</c> were
/// retired — they were never written or read by any production path (only the resume cursor +
/// content marker are consumed). Old on-disk indexes simply drop the unknown field on load.</para>
///
/// <para>Schema v1 carries the sprint-locked PII annotations
/// <see cref="Exportable"/>=false and <see cref="DeletableOnClear"/>=true from the
/// first commit (no "unlock later" deferral).</para>
/// </summary>
public sealed class UserModelIndex
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Sprint-locked: user-model files are never exported.</summary>
    public bool Exportable { get; set; } = false;

    /// <summary>Sprint-locked: wiped by the H24 "clear all my data" path.</summary>
    public bool DeletableOnClear { get; set; } = true;

    /// <summary>UM4: index (exclusive upper bound) of chat turns already analysed.
    /// The next retrospective run reads <c>chat_history.jsonl</c> from here onward.
    /// LEGACY positional cursor — retained for back-compat and as the fallback when no
    /// <see cref="LastAnalyzedMarker"/> is present (pre-Beta-1 indexes / first run).</summary>
    public int LastAnalyzedChatTurnIndex { get; set; }

    /// <summary>Beta-1 #16: a STABLE content marker (FNV-1a hash of role+content) of the
    /// last analysed chat message. chat_history.jsonl is a trimmed sliding window, so the
    /// positional <see cref="LastAnalyzedChatTurnIndex"/> drifts once the file trims: the
    /// cursor would clamp past the new tail and the analyzer froze forever. The resume point
    /// is now resolved by FINDING this marker in the current window; if it was trimmed away,
    /// the analyzer re-scans the whole current window instead of silently stopping. Null on a
    /// legacy index / before the first successful run (positional fallback applies).</summary>
    public string? LastAnalyzedMarker { get; set; }
}
