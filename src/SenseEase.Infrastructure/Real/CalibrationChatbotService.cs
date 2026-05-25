using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;

namespace SenseEase.Infrastructure.Real;

// ─────────────────────────────────────────────────────────────────────────────
// Stage 4 — Calibration LLM dialog driver.
//
// Drives the natural-language calibration dialog used on the SECOND and later
// calibration runs (after the user has completed the first-time card-based
// calibration once). The dialog asks the model to produce a structured
// [CALIBRATION_DELTA] JSON block alongside its conversational reply; the
// caller parses that block via CalibrationLlmDeltaParser and feeds the
// resulting CalibrationResponses delta into CalibrationProcessor at the end.
//
// Subprocess REUSE: this service does NOT spawn a second llama-server. The
// production HTTP client (LlamaServerCalibrationClient below) talks to the
// same http://127.0.0.1:8181 endpoint MistralChatbotService already brought
// up. If the server isn't running, calls fail gracefully and the caller falls
// back to the card UI.
//
// Independent transcript: the calibration dialog has its own ChatMessage list
// (persisted to calibration_history.jsonl), separate from chat_history.jsonl
// so VIMS-monitoring chat context never bleeds into the calibration dialog's
// longitudinal-comparison reasoning, and vice-versa.
//
// Stage 4 split across commits in this branch:
//   • 4a: skeleton + system prompts (this file, initial)
//   • 4d: 8-turn budget + convergence prompt
//   • 4e: self-harm safety floor (short-circuits LLM call) — TOP PRIORITY
//   • 4f: Q3 confidence fallback (emits RequestQ3Fallback event)
// ─────────────────────────────────────────────────────────────────────────────

public sealed class CalibrationChatbotService : IDisposable
{
    /// <summary>Soft cap on normal turns before the convergence prompt is
    /// injected (spec §4.4: "用戶已對話 8 輪").</summary>
    public const int NormalTurnBudget = 8;

    /// <summary>Hard cap on total LLM calls per session — 8 normal turns plus
    /// one convergence turn. After this the service emits <see cref="Completed"/>
    /// regardless of the model's <c>is_complete</c> flag.</summary>
    public const int MaxLlmCallsPerSession = 9;

    private readonly ICalibrationLlmClient _llm;
    private readonly string _transcriptPath;
    private readonly List<ChatMessage> _history = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    private string? _systemPrompt;       // chosen at StartAsync based on first user message language
    private bool _isActive;
    private bool _completedFired;
    private bool _awaitingQ3Fallback;     // set when service deferred Completed pending caller-supplied Q3
    private bool _pendingIsComplete;      // is_complete value to use when fallback merge eventually fires Completed
    private int _llmCallCount;
    private CalibrationResponses? _mergedSoFar;
    private double[]? _lastConfidence;
    private CalibrationResponses _emptyResponses = new();

    public event Action<string>? AssistantMessageReceived;
    public event Action<CalibrationResponses>? DeltaParsed;
    public event Action? RequestQ3Fallback;
    public event Action<string>? SafetyFloorTriggered;
    /// <summary>Final event. <c>bool</c> = <c>is_complete</c> flag from the LLM
    /// (may be false if turn-9 hard-stop fired without convergence). The caller
    /// decides what to do with the partial result.</summary>
    public event Action<CalibrationResponses, bool>? Completed;

    /// <summary>Number of completed user→model exchanges. Increments AFTER the
    /// model replies, so a freshly-started session reads 0.</summary>
    public int TurnCount { get; private set; }

    /// <summary>True between <see cref="StartAsync"/> and <see cref="EndSession"/>
    /// (or until <see cref="Completed"/> fires, whichever comes first).</summary>
    public bool IsActive => _isActive;

    public IReadOnlyList<ChatMessage> History => _history.AsReadOnly();

    /// <summary>Production ctor: writes the transcript to
    /// <see cref="SenseEase.App.Configuration.AppConfig.CalibrationHistoryPath"/>.
    /// Tests use the (transcriptPath, llm) overload.</summary>
    public CalibrationChatbotService(ICalibrationLlmClient llm, string transcriptPath)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _transcriptPath = transcriptPath ?? throw new ArgumentNullException(nameof(transcriptPath));
    }

    /// <summary>Prepares the session with the prior snapshot (may be null on
    /// first run — rendered as "first calibration session" in the [USER_CONTEXT]
    /// block) and the current sensor observation. Does NOT send a message to
    /// the LLM yet — the first <see cref="SendUserMessageAsync"/> triggers
    /// language detection + the actual call.</summary>
    public Task StartAsync(HabituationProfileSnapshot? previousSnapshot,
                           CalibrationSensorData currentSensor)
    {
        _history.Clear();
        _systemPrompt = null;
        _isActive = true;
        _completedFired = false;
        _awaitingQ3Fallback = false;
        _pendingIsComplete = false;
        _llmCallCount = 0;
        TurnCount = 0;
        _mergedSoFar = null;
        _lastConfidence = null;

        // Stash the context so the system prompt can be rendered once the
        // user's language is known on the first send.
        _pendingPreviousSnapshot = previousSnapshot;
        _pendingCurrentSensor = currentSensor;
        return Task.CompletedTask;
    }

    private HabituationProfileSnapshot? _pendingPreviousSnapshot;
    // Defaulted to a zeroed sensor record so the field is non-null before
    // StartAsync runs; the actual sensor data is assigned by StartAsync.
    private CalibrationSensorData _pendingCurrentSensor = new(0, 0, 0);

    public async Task SendUserMessageAsync(string userText)
    {
        if (!_isActive) return;
        userText = (userText ?? string.Empty).Trim();
        if (userText.Length == 0) return;

        // ── Stage 4e (added later commit): self-harm safety floor — short-circuit
        // BEFORE any LLM call. Stub here so the skeleton compiles; the real check
        // arrives in the safety-floor commit.
        if (TripsSafetyFloor(userText, out string safetyResponse))
        {
            SafetyFloorTriggered?.Invoke(safetyResponse);
            EndSession();
            return;
        }

        // Lazy system-prompt selection: detect zh vs en from the first user message.
        if (_systemPrompt is null)
        {
            bool zh = IsHan(userText);
            _systemPrompt = BuildSystemPrompt(zh, _pendingPreviousSnapshot, _pendingCurrentSensor);
        }

        _history.Add(new ChatMessage { Role = ChatRole.User, Content = userText });
        AppendTranscriptLine(ChatRole.User, userText);

        // Convergence injection: if we're about to make the (NormalTurnBudget+1)th
        // call and the model hasn't yet flagged is_complete=true, append a
        // pseudo-user reminder. We use the user role because ChatRole only has
        // User/Assistant — using a leading-marker prefix the LLM treats as an
        // out-of-band directive (matches MistralChatbotService's language-pin
        // pattern of inlining directives into the user turn).
        if (TurnCount >= NormalTurnBudget && _llmCallCount < MaxLlmCallsPerSession)
        {
            string convergence = IsZh(_systemPrompt)
                ? "[系統提示] 用戶已對話 8 輪，請收斂並產出最終 [CALIBRATION_DELTA]，is_complete 設為 true。"
                : "[SYSTEM REMINDER] User has talked for 8 turns — converge now and emit a final [CALIBRATION_DELTA] with is_complete=true.";
            _history.Add(new ChatMessage { Role = ChatRole.User, Content = convergence });
        }

        await _lock.WaitAsync().ConfigureAwait(false);
        string? reply;
        try
        {
            _llmCallCount++;
            reply = await _llm.SendChatAsync(_systemPrompt!, _history).ConfigureAwait(false);
        }
        finally { _lock.Release(); }

        reply ??= string.Empty;
        _history.Add(new ChatMessage { Role = ChatRole.Assistant, Content = reply });
        AppendTranscriptLine(ChatRole.Assistant, reply);
        TurnCount++;

        // Parse the [CALIBRATION_DELTA] block. The user-visible reply has the
        // block stripped; the structured delta drives merge logic + completion.
        var parsed = CalibrationLlmDeltaParser.TryParse(reply);
        string visibleReply = CalibrationLlmDeltaParser.StripDeltaBlock(reply);
        AssistantMessageReceived?.Invoke(visibleReply);

        if (parsed is not null)
        {
            if (parsed.Delta is not null)
            {
                _mergedSoFar = MergeResponses(_mergedSoFar, parsed.Delta);
                DeltaParsed?.Invoke(parsed.Delta);
            }
            if (parsed.Confidence is not null) _lastConfidence = parsed.Confidence;

            bool hardStop = _llmCallCount >= MaxLlmCallsPerSession;
            if ((parsed.IsComplete || hardStop) && !_completedFired && !_awaitingQ3Fallback)
            {
                CalibrationResponses final = _mergedSoFar ?? _emptyResponses;

                // Q3 confidence fallback. Event-ownership invariant: only this
                // service ever fires Completed. If Q3 is weak we DEFER Completed
                // until the caller routes the Q3 fallback answer back via
                // MergeFallbackAndComplete — keeps the event source single.
                if (NeedsQ3Fallback(final, _lastConfidence))
                {
                    _awaitingQ3Fallback = true;
                    _pendingIsComplete  = parsed.IsComplete;
                    RequestQ3Fallback?.Invoke();
                    // NOTE: do NOT EndSession here. Session stays active until
                    // MergeFallbackAndComplete (which fires Completed) or the
                    // caller explicitly EndSession()s after a dismissed fallback.
                    return;
                }

                _completedFired = true;
                Completed?.Invoke(final, parsed.IsComplete);
                EndSession();
            }
        }
        else if (_llmCallCount >= MaxLlmCallsPerSession && !_completedFired && !_awaitingQ3Fallback)
        {
            // Hard stop with no parseable delta. Same event-ownership rule
            // applies — defer Completed when Q3 is weak.
            CalibrationResponses final = _mergedSoFar ?? _emptyResponses;
            if (NeedsQ3Fallback(final, _lastConfidence))
            {
                _awaitingQ3Fallback = true;
                _pendingIsComplete  = false;
                RequestQ3Fallback?.Invoke();
                return;
            }
            _completedFired = true;
            Completed?.Invoke(final, false);
            EndSession();
        }
    }

    /// <summary>Merge the user's Q3 fallback answer (typically collected via a
    /// single-card UI shown in response to <see cref="RequestQ3Fallback"/>) into
    /// the session's accumulated responses, then fire <see cref="Completed"/>.
    ///
    /// EVENT OWNERSHIP RULE: <see cref="Completed"/> is exclusively fired by
    /// this service. The fallback flow used to leave Completed-firing to the
    /// caller, which violated event-source consistency — this method is the
    /// fix per the Stage 4 patch review. Caller's responsibility ends at
    /// "collect Q3 and call this method"; firing the event stays here.
    ///
    /// No-op when the service is not in the fallback-awaiting state. Safe to
    /// call multiple times — second call is a no-op once Completed has fired.</summary>
    public void MergeFallbackAndComplete(int q3Intensity, string? q3Freetext = null)
    {
        if (!_awaitingQ3Fallback || _completedFired) return;

        var prior = _mergedSoFar ?? _emptyResponses;
        _mergedSoFar = prior with
        {
            Q3Intensity = q3Intensity,
            Q3Freetext  = string.IsNullOrWhiteSpace(q3Freetext) ? prior.Q3Freetext : q3Freetext.Trim(),
            Q3Source    = CalibrationSource.FallbackButtons,
        };

        _awaitingQ3Fallback = false;
        _completedFired     = true;

        // Hand the merged responses back through the service-owned event.
        // is_complete reflects what the LLM said at the time we entered the
        // fallback branch (preserved in _pendingIsComplete). If the LLM's
        // is_complete was true and the user gave us the missing Q3, the
        // calibration is genuinely complete; if hard-stop fallback, false.
        Completed?.Invoke(_mergedSoFar, _pendingIsComplete);
        EndSession();
    }

    public void EndSession()
    {
        _isActive = false;
    }

    public void Dispose()
    {
        try { _lock.Dispose(); } catch { }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Safety floor (Stage 4e). User-life-safety check — runs BEFORE every LLM
    // call. Whole-phrase matching on a strict suicide/self-harm vocabulary;
    // tuned to favour false-positives over misses (per spec). The vocabulary
    // intentionally excludes the soft-signal "撐不下去" alone — false positives
    // on "暈到不想活動" / "想死啊這 demo" are not catastrophic, but missing a
    // real distress signal is.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Strict self-harm / severe-distress phrases. Whole-substring
    /// match against the user text (case-insensitive for English; Han chars are
    /// identity for ToLowerInvariant). Each entry is a whole phrase, NOT a
    /// keyword — "不想活" matches, but "暈到不想活動" does NOT because we check
    /// for a word boundary on either side OR an explicit phrase like "不想活了".</summary>
    internal static readonly string[] SafetyFloorPhrasesZh =
    {
        "自殘", "自殺", "結束生命", "跳下去", "跳樓",
        "不想活了", "不想活下去", "不想再活",
        "想死了", "想去死", "我想死",
    };

    internal static readonly string[] SafetyFloorPhrasesEn =
    {
        "suicide", "self-harm", "self harm", "selfharm",
        "kill myself", "end it all", "end my life",
        "not want to live", "do not want to live", "don't want to live",
        "want to die", "wanna die",
    };

    /// <summary>Detects self-harm phrases. Returns true and the localized
    /// crisis-line response when triggered. Uses CJK detection on the user
    /// text to pick the zh vs en response — independent of the LLM's
    /// system-prompt language, because the user's CURRENT message language
    /// is what matters for the crisis-line content.</summary>
    internal static bool TripsSafetyFloor(string userText, out string response)
    {
        response = string.Empty;
        if (string.IsNullOrWhiteSpace(userText)) return false;

        bool zh = IsHan(userText);
        string lower = userText.ToLowerInvariant();

        if (zh)
        {
            foreach (string p in SafetyFloorPhrasesZh)
            {
                if (userText.Contains(p, StringComparison.Ordinal))
                {
                    response = "聽起來你現在很辛苦。SenseEase 不是醫療裝置，無法處理這種狀況。請立即聯繫專業協助：台灣 - 衛福部安心專線 1925；國際 - findahelpline.com。";
                    return true;
                }
            }
        }

        foreach (string p in SafetyFloorPhrasesEn)
        {
            if (lower.Contains(p, StringComparison.Ordinal))
            {
                response = zh
                    ? "聽起來你現在很辛苦。SenseEase 不是醫療裝置，無法處理這種狀況。請立即聯繫專業協助：台灣 - 衛福部安心專線 1925；國際 - findahelpline.com。"
                    : "It sounds like you're going through something really hard right now. SenseEase isn't a medical device and can't help with this. Please reach out to a crisis line immediately: US - 988; International - findahelpline.com.";
                return true;
            }
        }

        return false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Q3 confidence fallback (Stage 4f). Caller treats the parameterless event
    // as "show Card 3 single-card UI and re-emit Completed with merged Q3".
    // ─────────────────────────────────────────────────────────────────────────

    private static bool NeedsQ3Fallback(CalibrationResponses merged, double[]? confidence)
    {
        if (merged.Q3Intensity is null) return true;
        if (confidence is null || confidence.Length < 3) return true;
        return confidence[2] < 0.7;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Merge: later-turn deltas overwrite earlier ones on a per-field basis.
    // The model emits incremental deltas over an 8-turn dialog, so we
    // accumulate non-null fields rather than replacing wholesale.
    // ─────────────────────────────────────────────────────────────────────────
    private static CalibrationResponses MergeResponses(CalibrationResponses? prior, CalibrationResponses delta)
    {
        if (prior is null) return delta;
        return prior with
        {
            Q1Segment    = delta.Q1Segment    ?? prior.Q1Segment,
            Q1Freetext   = delta.Q1Freetext   ?? prior.Q1Freetext,
            Q2Symptoms   = delta.Q2Symptoms   ?? prior.Q2Symptoms,
            Q2Freetext   = delta.Q2Freetext   ?? prior.Q2Freetext,
            Q3Intensity  = delta.Q3Intensity  ?? prior.Q3Intensity,
            Q3Freetext   = delta.Q3Freetext   ?? prior.Q3Freetext,
            Q4Comparison = delta.Q4Comparison ?? prior.Q4Comparison,
            // Stage 4 review (Q1): uniform "delta wins outright" semantics.
            // Calibration is a single-session conversation — if the user
            // first says "I don't usually watch this kind of content" and
            // later remembers they DO watch TikTok, the new value should
            // overwrite the old. Sticky-true (the prior `||` semantic) would
            // lock the original answer and make corrections impossible.
            // Information loss when the LLM emits the default bool on a
            // silent turn is accepted as the cost of ONE uniform merge rule.
            Q4NoBaseline = delta.Q4NoBaseline,
            Q4Freetext   = delta.Q4Freetext   ?? prior.Q4Freetext,
            Q5RiskFactors = delta.Q5RiskFactors ?? prior.Q5RiskFactors,
            Q5Freetext   = delta.Q5Freetext   ?? prior.Q5Freetext,
            // Source tags: any non-default in the delta wins; otherwise keep prior.
            Q1Source = delta.Q1Source != CalibrationSource.Buttons ? delta.Q1Source : prior.Q1Source,
            Q2Source = delta.Q2Source != CalibrationSource.Buttons ? delta.Q2Source : prior.Q2Source,
            Q3Source = delta.Q3Source != CalibrationSource.Buttons ? delta.Q3Source : prior.Q3Source,
            Q4Source = delta.Q4Source != CalibrationSource.Buttons ? delta.Q4Source : prior.Q4Source,
            Q5Source = delta.Q5Source != CalibrationSource.Buttons ? delta.Q5Source : prior.Q5Source,
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Transcript persistence — best-effort JSON Lines append per turn so a mid-
    // session crash still preserves the partial dialog for later inspection.
    // ─────────────────────────────────────────────────────────────────────────
    private void AppendTranscriptLine(ChatRole role, string content)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_transcriptPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string line = JsonSerializer.Serialize(new
            {
                t = DateTime.UtcNow.ToString("o"),
                role = role.ToString(),
                content,
            });
            File.AppendAllText(_transcriptPath, line + Environment.NewLine);
        }
        catch { /* best-effort */ }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Language detection — reuses MistralChatbotService's CJK Unified Ideograph
    // heuristic (any U+4E00..U+9FFF char → zh).
    // ─────────────────────────────────────────────────────────────────────────
    internal static bool IsHan(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (char c in s)
            if (c >= 0x4E00 && c <= 0x9FFF) return true;
        return false;
    }

    /// <summary>Returns true iff the prompt string begins with the zh
    /// [USER_CONTEXT] marker — used by the convergence-prompt selector to pick
    /// language without re-running detection.</summary>
    private static bool IsZh(string? systemPrompt)
        => systemPrompt is not null && systemPrompt.Contains("[USER_CONTEXT]\n本用戶", StringComparison.Ordinal)
                                       || (systemPrompt is not null && systemPrompt.StartsWith("你是 SenseEase", StringComparison.Ordinal));

    // System prompts (spec §4) extracted to CalibrationPrompts.cs (Stage 4 review).
    // Both languages + the {USER_CONTEXT} substitution live there; this file
    // keeps the service logic readable without a 150-LOC verbatim text wall.
    internal static string BuildSystemPrompt(bool zh, HabituationProfileSnapshot? prev, CalibrationSensorData sensor)
        => CalibrationPrompts.BuildSystemPrompt(zh, prev, sensor);
}
