using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Inference;

namespace SenseEase.Infrastructure.Storage;

/// <summary>
/// H17: durable, append-only JSONL chatbot-action log. Drop-in replacement for
/// <see cref="InMemoryChatbotActionLog"/> that ALSO persists the proposal
/// lifecycle (proposed → accepted/declined/expired/intercepted) to a per-UTC-day
/// file (chatbot-actions-YYYY-MM-DD.jsonl). The durable trail means the Processor's
/// rule-5 cooldown survives a restart and a tester's "the chatbot stopped flagging
/// vestibular concerns" report has evidence.
///
/// <para>On construction the last <see cref="RetentionDays"/> days of entries are
/// loaded back into memory so MostRecentDeclineFor / Recent are durable across
/// restarts; older files are pruned; each day's file is capped at
/// <see cref="MaxDailyFileBytes"/>. <c>ProposedValue</c> is persisted as a string
/// (forensic record) and rehydrated as a string on load. All file I/O is
/// best-effort.</para>
/// </summary>
public sealed class FileChatbotActionLog : IChatbotActionLog
{
    private readonly string _dir;
    private readonly object _gate = new();
    private readonly List<ChatbotActionEntry> _entries = new();
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = false };

    public const long MaxDailyFileBytes = 1024 * 1024; // 1 MB
    public const int RetentionDays = 7;

    private sealed record PersistedAction(
        string ts, string kind, string intent, string proposed_value, string rationale, string? reason);

    public FileChatbotActionLog(string directory)
    {
        _dir = directory;
        try
        {
            Directory.CreateDirectory(_dir);
            AuditLogFiles.Prune(_dir, "chatbot-actions-*.jsonl", RetentionDays);
            LoadRecent();
        }
        catch { /* best-effort */ }
    }

    public void Record(ChatbotActionKind kind, ProfileOverrideProposal proposal, string? reason = null)
    {
        if (proposal is null) throw new ArgumentNullException(nameof(proposal));
        var entry = new ChatbotActionEntry(
            TimestampUtc: DateTime.UtcNow,
            Kind: kind,
            Intent: proposal.Intent,
            ProposedValue: proposal.ProposedValue,
            Rationale: proposal.Rationale,
            Reason: reason);
        lock (_gate)
        {
            _entries.Add(entry);
            TryAppendToDisk(entry);
        }
    }

    public DateTime? MostRecentDeclineFor(OverrideIntent intent)
    {
        lock (_gate)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var e = _entries[i];
                if (e.Kind == ChatbotActionKind.Declined && e.Intent == intent)
                    return e.TimestampUtc;
            }
            return null;
        }
    }

    public IReadOnlyList<ChatbotActionEntry> Recent(int count = 20)
    {
        if (count <= 0) return Array.Empty<ChatbotActionEntry>();
        lock (_gate)
        {
            int take = Math.Min(count, _entries.Count);
            var slice = new ChatbotActionEntry[take];
            for (int i = 0; i < take; i++)
                slice[i] = _entries[_entries.Count - 1 - i]; // newest first
            return slice;
        }
    }

    private void TryAppendToDisk(ChatbotActionEntry e)
    {
        var dto = new PersistedAction(
            e.TimestampUtc.ToString("o"),
            e.Kind.ToString(),
            e.Intent.ToString(),
            e.ProposedValue?.ToString() ?? string.Empty,
            e.Rationale,
            e.Reason);
        AuditLogFiles.Append(_dir, "chatbot-actions", JsonSerializer.Serialize(dto, _opts), MaxDailyFileBytes);
    }

    private void LoadRecent()
    {
        var files = Directory.GetFiles(_dir, "chatbot-actions-*.jsonl");
        Array.Sort(files, StringComparer.Ordinal); // filenames sort chronologically
        foreach (string file in files)
        {
            foreach (string line in File.ReadAllLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var dto = JsonSerializer.Deserialize<PersistedAction>(line, _opts);
                    if (dto is null) continue;
                    if (!DateTime.TryParse(dto.ts, CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out DateTime ts)) continue;
                    if (!Enum.TryParse<ChatbotActionKind>(dto.kind, out var kind)) continue;
                    if (!Enum.TryParse<OverrideIntent>(dto.intent, out var intent)) continue;
                    _entries.Add(new ChatbotActionEntry(
                        ts.ToUniversalTime(), kind, intent, dto.proposed_value, dto.rationale, dto.reason));
                }
                catch { /* skip malformed line */ }
            }
        }
    }
}
