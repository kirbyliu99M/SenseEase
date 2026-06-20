using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Storage;

namespace SenseEase.Infrastructure.Real;

/// <summary>
/// UM4 (User Model sprint): batch retrospective analysis. Reads the un-analysed tail
/// of <c>chat_history.jsonl</c> (from <c>last_analyzed_chat_turn_index</c>), asks the
/// LLM to extract durable VIMS observations, persists them via
/// <see cref="LongitudinalObservationWriter"/> (same dedup + monthly-bucket path as
/// real-time extraction), and advances the index. Fully silent — no UI, no event.
///
/// <para><b>Wiring contract:</b> the supplied <paramref name="observationWriter"/> MUST
/// be backed by the SAME <see cref="FileHabituationProfileStore"/> instance the engine
/// uses, so the W2.0-C atomic Update serialises the analyser's writes against the
/// engine's persist (per-instance lock). A fresh store instance would reintroduce the
/// lost-update race.</para>
///
/// <para><b>Resource guard:</b> at most <see cref="MaxTurnsPerRun"/> turns per run; the
/// remainder is left for the next run (the index advances by exactly the slice size).
/// This bounds the first-run LLM cost after a long pre-index history.</para>
/// </summary>
public sealed class UserModelRetrospectiveAnalyzer
{
    /// <summary>Hard cap on chat turns analysed in a single run (resource guard).</summary>
    public const int MaxTurnsPerRun = 100;

    /// <summary>B6 Stage 1 (was B5 Stage 4): the sensitivity scale the LLM proposed via an
    /// <c>[ADAPT_DIRECTION]</c> block during this run (null if none). The App maps it to a gate DIRECTION
    /// only; the deterministic AdaptationGate validates it against MEASURED data and sizes the step, so a
    /// wrong / absent / expired value is harmless — it can never relax protection the data doesn't support.</summary>
    public double? LastProposedSensitivityScale { get; private set; }

    /// <summary>The rationale text accompanying <see cref="LastProposedSensitivityScale"/> (render-only,
    /// for the in-chat notice; already injection-defanged by the parser; never re-enters any computation).</summary>
    public string LastProposalRationale { get; private set; } = string.Empty;

    /// <summary>B6 Stage 1 (RT-2s): when the captured proposal goes stale. Parsed from the block's
    /// <c>expires_at_iso</c>, else the run time + 72h. The wiring treats the hint as Hold once past this.</summary>
    public DateTime? LastProposalExpiresUtc { get; private set; }

    /// <summary>Analysis-only system prompt (UM4 brief). Appended as the [SYSTEM]
    /// message for this task; the conversation slice is the chat history.</summary>
    public const string AnalysisSystemPrompt =
        "You are analysing a slice of past conversation to extract durable observations " +
        "about the user's VIMS experience. For each meaningful observation, emit one " +
        "[OBSERVATION_ADD] block (same format as real-time extraction). Only emit " +
        "observations about symptoms, triggers, improvement, task context, or tolerance " +
        "patterns. Do not emit observations about app preferences or UI questions. " +
        "Emit nothing if the slice contains no clinically relevant content.\n\n" +
        // B6 Stage 1: a SEPARATE, optional adaptation-direction hint on a DISTINCT block name
        // ([ADAPT_DIRECTION], never [PROFILE_OVERRIDE]) so it can't reach the live-chat Accept-card (RT-1b).
        // DIRECTION only — the system sizes the change and re-checks it against measured data.
        "SEPARATELY, ONLY if the slice shows a CLEAR, SUSTAINED pattern in the user's comfort that implies " +
        "their protection level should change, you MAY emit ONE [ADAPT_DIRECTION] block:\n" +
        "[ADAPT_DIRECTION]\n" +
        "{\"intent\": \"adjust_sensitivity\", \"proposed_value\": <number near 1.0>, " +
        "\"rationale\": \"<short reason>\", \"expires_at_iso\": \"<UTC ISO-8601>\"}\n" +
        "[/ADAPT_DIRECTION]\n" +
        "proposed_value is a sensitivity scale near 1.0: a LOWER value (e.g. 0.8) means EASE protection — " +
        "emit it ONLY when the user has clearly tolerated their current level comfortably over time; a HIGHER " +
        "value (e.g. 1.2) means STRENGTHEN it — when discomfort is rising. Emit at most one [ADAPT_DIRECTION], " +
        "and OMIT it entirely unless the pattern is clear: the system ignores an absent or unsupported proposal.";

    private readonly ICalibrationLlmClient _llm;
    private readonly FileChatHistoryStore _chatHistory;
    private readonly LongitudinalObservationWriter _observationWriter;
    private readonly IUserModelStore _userModel;

    public UserModelRetrospectiveAnalyzer(
        ICalibrationLlmClient llm,
        FileChatHistoryStore chatHistory,
        LongitudinalObservationWriter observationWriter,
        IUserModelStore userModel)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _chatHistory = chatHistory ?? throw new ArgumentNullException(nameof(chatHistory));
        _observationWriter = observationWriter ?? throw new ArgumentNullException(nameof(observationWriter));
        _userModel = userModel ?? throw new ArgumentNullException(nameof(userModel));
    }

    /// <summary>Analyse the un-analysed chat tail and persist any extracted
    /// observations. Returns the count written (0 = nothing new, LLM unavailable, or
    /// no clinically relevant content). Never throws except for cancellation.</summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var index = _userModel.LoadIndex();
        var all = _chatHistory.Load();

        // Beta-1 #16: resolve the resume point by a STABLE content marker, not a positional
        // cursor. chat_history.jsonl is a trimmed sliding window, so a positional index drifts
        // once the file trims — the old clamp parked at all.Count and the analyzer froze
        // forever (new turns scrolled in below the cursor and were never analysed). Find the
        // last-analysed message by marker: resume just after it; if it was trimmed away,
        // re-scan the whole current window (safe superset — the observation writer dedups).
        // Legacy indexes (no marker yet) fall back to the positional clamp so existing
        // behaviour + tests are unchanged until the first marker is written.
        int start;
        if (!string.IsNullOrEmpty(index.LastAnalyzedMarker))
        {
            int found = -1;
            for (int i = all.Count - 1; i >= 0; i--)
                if (MarkerOf(all[i]) == index.LastAnalyzedMarker) { found = i; break; }
            start = found >= 0 ? found + 1 : 0;
        }
        else
        {
            start = Math.Min(Math.Max(0, index.LastAnalyzedChatTurnIndex), all.Count);
        }
        var slice = all.Skip(start).Take(MaxTurnsPerRun).ToList();
        if (slice.Count == 0) return 0; // nothing new to analyse

        string? raw;
        try
        {
            raw = await _llm.SendChatAsync(AnalysisSystemPrompt, slice, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // app exiting / superseded — propagate, do NOT advance the index
        }
        catch
        {
            return 0; // transient LLM error → retry this slice next run (index unchanged)
        }

        // null = LLM unavailable (model not loaded yet): retry next run, don't advance.
        if (raw is null) return 0;

        int written = 0;
        var parsed = HabituationOverrideParser.TryParse(raw);

        // B6 Stage 1: capture the OPTIONAL adaptation-direction hint from a DISTINCT [ADAPT_DIRECTION] block
        // (NOT [PROFILE_OVERRIDE] — RT-1b keeps it off the live-chat Accept-card path). Direction-only: the
        // gate maps it to a sign vs the live scale, sizes the step, and re-validates against measured data,
        // so an absent / wrong / expired proposal can never relax protection the data doesn't support.
        var adapt = AdaptDirectionParser.TryParse(raw, DateTime.UtcNow);
        if (adapt is not null)
        {
            LastProposedSensitivityScale = adapt.ProposedScale;
            LastProposalRationale = adapt.Rationale;
            LastProposalExpiresUtc = adapt.ExpiresAtUtc;
        }

        foreach (var obs in parsed.Observations)
        {
            try { if (_observationWriter.Append(obs)) written++; }
            catch { /* per-observation failure swallowed */ }
        }

        // The slice WAS analysed (empty output just means no clinically relevant
        // content) — advance the index so we never re-analyse it. Capped at the
        // slice size so a >100-turn backlog is consumed across runs. Persist BOTH the
        // positional cursor (back-compat) AND the stable marker of the last analysed message
        // (Beta-1 #16: the marker is what makes the resume trim-proof).
        index.LastAnalyzedChatTurnIndex = start + slice.Count;
        index.LastAnalyzedMarker = MarkerOf(all[start + slice.Count - 1]);
        _userModel.SaveIndex(index);
        return written;
    }

    /// <summary>Beta-1 #16: a process-stable marker for a chat message (string.GetHashCode is
    /// randomised per process, so it cannot persist across runs). FNV-1a over role+content;
    /// stores a hash, never the content itself, so the index carries no transcript text.</summary>
    private static string MarkerOf(ChatMessage m)
    {
        string s = ((int)m.Role).ToString() + ":" + (m.Content ?? string.Empty);
        ulong hh = 1469598103934665603UL;          // FNV-1a 64-bit offset basis
        foreach (char c in s) { hh ^= c; hh *= 1099511628211UL; }
        return hh.ToString("x16");
    }

}
