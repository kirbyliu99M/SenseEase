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

    /// <summary>B5 Stage 4: the last AdjustSensitivity scale the LLM proposed during this run (null if it
    /// emitted none). Captured from the SAME parse that extracts observations — no extra LLM call, no
    /// prompt change. The App maps it to a gate DIRECTION only; the deterministic AdaptationGate validates
    /// it against MEASURED data before anything changes, so a wrong/absent value is harmless (no relax).</summary>
    public double? LastProposedSensitivityScale { get; private set; }

    /// <summary>The rationale text accompanying <see cref="LastProposedSensitivityScale"/> (render-only,
    /// for the in-chat notice; never re-enters any computation).</summary>
    public string LastProposalRationale { get; private set; } = string.Empty;

    /// <summary>Analysis-only system prompt (UM4 brief). Appended as the [SYSTEM]
    /// message for this task; the conversation slice is the chat history.</summary>
    public const string AnalysisSystemPrompt =
        "You are analysing a slice of past conversation to extract durable observations " +
        "about the user's VIMS experience. For each meaningful observation, emit one " +
        "[OBSERVATION_ADD] block (same format as real-time extraction). Only emit " +
        "observations about symptoms, triggers, improvement, task context, or tolerance " +
        "patterns. Do not emit observations about app preferences or UI questions. " +
        "Emit nothing if the slice contains no clinically relevant content.";

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

        // B5 Stage 4: capture an OPTIONAL sensitivity proposal the LLM may have emitted (it is NOT
        // prompted to — no prompt change here, to keep observation extraction intact — but if one
        // appears we surface its DIRECTION to the gate). The gate validates it against measured data;
        // an absent or wrong proposal is harmless. ProposedValue is normalised to a boxed double.
        if (parsed.Proposal is { Intent: OverrideIntent.AdjustSensitivity } prop &&
            TryUnwrapScale(prop.ProposedValue, out double scale) && double.IsFinite(scale))
        {
            LastProposedSensitivityScale = scale;
            LastProposalRationale = prop.Rationale ?? string.Empty;
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
    /// <summary>Unwrap the parser's normalised proposed value to a double. <c>NormaliseProposedValue</c>
    /// boxes a whole-number JSON value as <c>long</c> and a fractional one as <c>double</c>, so an
    /// <c>is double</c> test alone silently misses integer scales like 1/2/3 (a boxed long does not match
    /// a double pattern). Accept both — mirrors the engine's <c>TryUnwrapDouble</c>.</summary>
    private static bool TryUnwrapScale(object? value, out double result)
    {
        switch (value)
        {
            case double d: result = d;        return true;
            case long l:   result = l;        return true;
            case int i:    result = i;        return true;
            default:       result = 0.0;      return false;
        }
    }

    private static string MarkerOf(ChatMessage m)
    {
        string s = ((int)m.Role).ToString() + ":" + (m.Content ?? string.Empty);
        ulong hh = 1469598103934665603UL;          // FNV-1a 64-bit offset basis
        foreach (char c in s) { hh ^= c; hh *= 1099511628211UL; }
        return hh.ToString("x16");
    }

}
