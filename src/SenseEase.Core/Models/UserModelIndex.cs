using System.Collections.Generic;

namespace SenseEase.Core.Models;

/// <summary>
/// UM3 (User Model sprint): incremental analysis state + monthly-bucket manifest,
/// persisted to <c>%LOCALAPPDATA%\SenseEase\user_model_index.json</c> via the
/// atomic writer. Lets the UM4 retrospective pipeline resume from where it left
/// off without re-reading the whole chat history, and lets summaries know each
/// observation bucket's size without loading the profile.
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
    /// The next retrospective run reads <c>chat_history.jsonl</c> from here onward.</summary>
    public int LastAnalyzedChatTurnIndex { get; set; }

    /// <summary>UM2/UM3: per-month observation-bucket metadata, keyed by "YYYY-MM".</summary>
    public Dictionary<string, BucketMeta> BucketManifest { get; set; } = new();
}

/// <summary>UM3: lightweight per-month bucket metadata for the manifest.</summary>
public sealed class BucketMeta
{
    public int EntryCount { get; set; }

    /// <summary>ISO-8601 UTC timestamp of the last write into this bucket.</summary>
    public string? LastUpdated { get; set; }
}
