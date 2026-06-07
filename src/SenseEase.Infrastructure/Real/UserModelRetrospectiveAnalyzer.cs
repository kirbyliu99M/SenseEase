using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Inference;
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

        // The index is a cumulative count of analysed turns; the file is trimmed, so
        // clamp the start to the available count (a shrunk file => nothing new, safe).
        int start = Math.Min(Math.Max(0, index.LastAnalyzedChatTurnIndex), all.Count);
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
        foreach (var obs in parsed.Observations)
        {
            try { if (_observationWriter.Append(obs)) written++; }
            catch { /* per-observation failure swallowed */ }
        }

        // The slice WAS analysed (empty output just means no clinically relevant
        // content) — advance the index so we never re-analyse it. Capped at the
        // slice size so a >100-turn backlog is consumed across runs.
        index.LastAnalyzedChatTurnIndex = start + slice.Count;
        _userModel.SaveIndex(index);
        return written;
    }
}
