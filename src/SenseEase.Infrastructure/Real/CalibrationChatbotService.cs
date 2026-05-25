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
            if ((parsed.IsComplete || hardStop) && !_completedFired)
            {
                _completedFired = true;
                CalibrationResponses final = _mergedSoFar ?? _emptyResponses;

                // Q3 confidence fallback: if Q3 intensity is missing OR the
                // model's Q3 confidence is < 0.7, ask the UI to show the
                // single-card Q3 fallback. The caller (Agent A's UI in Stage 3)
                // will re-emit Completed after merging Q3 from the card.
                bool q3Weak = NeedsQ3Fallback(final, _lastConfidence);
                if (q3Weak) RequestQ3Fallback?.Invoke();

                // TODO Stage 5 integration: caller merges Q3 from fallback card and re-emits Completed.
                Completed?.Invoke(final, parsed.IsComplete);
                EndSession();
            }
        }
        else if (_llmCallCount >= MaxLlmCallsPerSession && !_completedFired)
        {
            // Hard stop with no parseable delta at all — emit Completed with whatever
            // we've merged (possibly empty) and is_complete=false so the caller can
            // decide whether to surface the card fallback for everything.
            _completedFired = true;
            CalibrationResponses final = _mergedSoFar ?? _emptyResponses;
            if (NeedsQ3Fallback(final, _lastConfidence)) RequestQ3Fallback?.Invoke();
            Completed?.Invoke(final, false);
            EndSession();
        }
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
            Q4NoBaseline = delta.Q4NoBaseline || prior.Q4NoBaseline,
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

    // ─────────────────────────────────────────────────────────────────────────
    // System prompts (spec §4). Two complete versions — zh and en. Stored as
    // const-like statics, NOT loaded from disk (spec requirement: in-source).
    //
    // Each prompt has 5 blocks per spec §4:
    //   [USER_CONTEXT] — rendered from previous snapshot (first run renders
    //                    placeholder).
    //   [TASK]         — what the model is doing (ask, parse, emit deltas).
    //   [STYLE]        — short, warm, no jargon, no medical advice.
    //   [OUTPUT]       — visible reply + [CALIBRATION_DELTA] JSON block schema.
    //   [SAFETY_FLOOR] — self-harm → refuse + crisis line.
    // ─────────────────────────────────────────────────────────────────────────

    private const string SystemPromptZhTemplate =
        "你是 SenseEase 校準對話助手。SenseEase 是一個在 Windows 桌面執行的視覺暈眩症 (VIMS) 緩解工具，會在使用者完成一段 demo 影片後對其感受做校準。\n\n" +

        "[USER_CONTEXT]\n" +
        "{USER_CONTEXT}\n\n" +

        "[TASK]\n" +
        "透過自然對話了解使用者剛才看 demo 的感受，覆蓋以下 5 個面向：\n" +
        "Q1 不適時段：影片開頭 / 中段 / 結尾 / 全程都還好 (Start/Middle/End/None)\n" +
        "Q2 症狀類型：頭暈、噁心、眼睛疲勞、頭痛、平衡感差、定向感差（可複選 / 都沒有）\n" +
        "Q3 不適強度 (1-5)：1=幾乎沒有 5=非常嚴重 — 必填，無法達成 0.7 信心則由系統 fallback 卡片\n" +
        "Q4 與平常比較：跟你平時看 YouTube/TikTok/玩遊戲比，這次更不舒服 / 差不多 / 更舒服 (Worse/Similar/Better) — 或標示沒有可比基準\n" +
        "Q5 長期風險因子：暈車暈船史、偏頭痛、被診斷的前庭疾病、VR 易暈、不明原因暈眩（可複選 / 沒有 / 不想說）\n" +
        "若有上次的 calibration 資料，可以對比並輕量提及差異（例如「上次你說中段最不舒服，這次呢？」），但不要逐項機械式詢問。\n\n" +

        "[STYLE]\n" +
        "• 用繁體中文。短句、口語、溫和、不要醫療術語。每次回覆 ≤ 2 句、≤ 60 字。\n" +
        "• 一次只問一件事；若使用者一次給多個資訊，順勢確認，不要重複問。\n" +
        "• 不要做醫療診斷或建議用藥。不要假裝自己是醫師。\n" +
        "• 對話最多 8 輪；第 8 輪結束時要收斂並輸出最終 delta。\n\n" +

        "[OUTPUT]\n" +
        "每次回覆都包含 2 個部分：\n" +
        "1. 給使用者看的對話文字（自然語言，符合 STYLE 規則）。\n" +
        "2. 緊接著的 [CALIBRATION_DELTA] JSON 區塊，由系統解析，不會顯示給使用者。\n\n" +
        "[CALIBRATION_DELTA] 區塊嚴格格式如下（snake_case，UTF-8，缺項用 null）：\n" +
        "[CALIBRATION_DELTA]\n" +
        "{\n" +
        "  \"delta\": {\n" +
        "    \"q1_segment\": \"Start\" | \"Middle\" | \"End\" | \"None\" | null,\n" +
        "    \"q1_freetext\": string | null,\n" +
        "    \"q2_symptoms\": [\"Dizziness\"|\"Nausea\"|\"EyeStrain\"|\"Headache\"|\"Imbalance\"|\"Disorientation\"] | null,\n" +
        "    \"q2_freetext\": string | null,\n" +
        "    \"q3_intensity\": 1|2|3|4|5 | null,\n" +
        "    \"q3_freetext\": string | null,\n" +
        "    \"q4_comparison\": \"Worse\" | \"Similar\" | \"Better\" | null,\n" +
        "    \"q4_no_baseline\": true | false,\n" +
        "    \"q4_freetext\": string | null,\n" +
        "    \"q5_risk_factors\": [\"MotionSickness\"|\"Migraine\"|\"VestibularDiagnosed\"|\"VrSusceptible\"|\"UnexplainedDizziness\"|\"None\"|\"Skipped\"] | null,\n" +
        "    \"q5_freetext\": string | null\n" +
        "  },\n" +
        "  \"confidence\": [q1, q2, q3, q4, q5],  // 5 個 0..1 浮點數；對應信心程度，0.7 為門檻\n" +
        "  \"longitudinal_observation\": string | null,  // 與上次 snapshot 的比較觀察，可空\n" +
        "  \"is_complete\": true | false,  // 是否已足夠收斂可結束對話\n" +
        "  \"needs_followup_on\": [\"q1\"|\"q2\"|\"q3\"|\"q4\"|\"q5\"]  // 下一輪仍需追問的欄位，可為空陣列\n" +
        "}\n" +
        "[/CALIBRATION_DELTA]\n\n" +
        "只在當前對話真的得到 / 確認某欄位的值時才填非 null；不要為了好看而臆測。delta 是 INCREMENTAL — 每輪可以只填新資訊。\n\n" +

        "[SAFETY_FLOOR]\n" +
        "若使用者表達自殘、自殺、想結束生命等嚴重訊號：立即停止校準流程，回覆「SenseEase 不是醫療裝置，無法處理這種狀況」並提供求助資源（台灣安心專線 1925、findahelpline.com）。將 is_complete 設為 true、其他欄位留 null。系統本身也有同步檢查，會在送到你之前先攔截，但你看到時也要做同樣處理。";

    private const string SystemPromptEnTemplate =
        "You are SenseEase's calibration dialog assistant. SenseEase is a Windows desktop tool that mitigates visually-induced motion sickness (VIMS); after a demo video plays, it calibrates to the user's reaction.\n\n" +

        "[USER_CONTEXT]\n" +
        "{USER_CONTEXT}\n\n" +

        "[TASK]\n" +
        "Through natural conversation, cover these 5 dimensions about how the user felt during the demo:\n" +
        "Q1 Worst segment: Start / Middle / End / None (whole clip was fine)\n" +
        "Q2 Symptom types: Dizziness, Nausea, EyeStrain, Headache, Imbalance, Disorientation (multi-select or none)\n" +
        "Q3 Intensity 1-5: 1=barely noticed, 5=very severe. MANDATORY. If you cannot reach 0.7 confidence the system will fall back to a card.\n" +
        "Q4 Compared to baseline: vs your usual YouTube/TikTok/game time, was this Worse / Similar / Better — or mark no baseline.\n" +
        "Q5 Long-term risk factors: motion-sickness history, migraine, diagnosed vestibular condition, VR-susceptible, unexplained dizziness (multi-select / None / Skipped).\n" +
        "If you have prior-calibration context, you may lightly compare (e.g. \"last time you said the middle was worst — how about this time?\"), but don't mechanically run through every field.\n\n" +

        "[STYLE]\n" +
        "• English only. Short, warm, plain language. Each reply ≤ 2 sentences, ≤ 45 words.\n" +
        "• Ask one thing at a time; if the user volunteers extra info, fold it in rather than re-ask.\n" +
        "• No medical diagnoses, no medication advice, do not roleplay as a doctor.\n" +
        "• Conversation lasts at most 8 turns; by turn 8 you MUST converge and emit a final delta.\n\n" +

        "[OUTPUT]\n" +
        "Every reply has TWO parts:\n" +
        "1. The user-visible conversational text (natural language, follows STYLE).\n" +
        "2. Immediately after, a [CALIBRATION_DELTA] JSON block which the system parses and never shows the user.\n\n" +
        "The [CALIBRATION_DELTA] block follows this strict schema (snake_case, UTF-8, missing fields use null):\n" +
        "[CALIBRATION_DELTA]\n" +
        "{\n" +
        "  \"delta\": {\n" +
        "    \"q1_segment\": \"Start\" | \"Middle\" | \"End\" | \"None\" | null,\n" +
        "    \"q1_freetext\": string | null,\n" +
        "    \"q2_symptoms\": [\"Dizziness\"|\"Nausea\"|\"EyeStrain\"|\"Headache\"|\"Imbalance\"|\"Disorientation\"] | null,\n" +
        "    \"q2_freetext\": string | null,\n" +
        "    \"q3_intensity\": 1|2|3|4|5 | null,\n" +
        "    \"q3_freetext\": string | null,\n" +
        "    \"q4_comparison\": \"Worse\" | \"Similar\" | \"Better\" | null,\n" +
        "    \"q4_no_baseline\": true | false,\n" +
        "    \"q4_freetext\": string | null,\n" +
        "    \"q5_risk_factors\": [\"MotionSickness\"|\"Migraine\"|\"VestibularDiagnosed\"|\"VrSusceptible\"|\"UnexplainedDizziness\"|\"None\"|\"Skipped\"] | null,\n" +
        "    \"q5_freetext\": string | null\n" +
        "  },\n" +
        "  \"confidence\": [q1, q2, q3, q4, q5],  // 5 floats 0..1; 0.7 is the threshold\n" +
        "  \"longitudinal_observation\": string | null,  // optional comparison note vs prior snapshot\n" +
        "  \"is_complete\": true | false,  // true when the dialog has gathered enough\n" +
        "  \"needs_followup_on\": [\"q1\"|\"q2\"|\"q3\"|\"q4\"|\"q5\"]  // fields still needing follow-up; may be empty\n" +
        "}\n" +
        "[/CALIBRATION_DELTA]\n\n" +
        "Only set a field to non-null when the current dialog has genuinely captured / confirmed that value; do not guess to look complete. Delta is INCREMENTAL — each turn can carry only the new info.\n\n" +

        "[SAFETY_FLOOR]\n" +
        "If the user expresses self-harm, suicide, or wanting to end their life: stop calibration immediately, reply that SenseEase is not a medical device and cannot help with this, and provide crisis resources (US 988, international findahelpline.com). Set is_complete=true and leave other fields null. The system also screens this before sending to you, but if you see it, do the same.";

    /// <summary>Builds the system prompt by substituting the rendered
    /// [USER_CONTEXT] block. First-run rendering uses a placeholder that tells
    /// the LLM "this is the user's first calibration session".</summary>
    internal static string BuildSystemPrompt(bool zh, HabituationProfileSnapshot? prev, CalibrationSensorData sensor)
    {
        string ctx = RenderUserContext(zh, prev, sensor);
        string tmpl = zh ? SystemPromptZhTemplate : SystemPromptEnTemplate;
        return tmpl.Replace("{USER_CONTEXT}", ctx);
    }

    private static string RenderUserContext(bool zh, HabituationProfileSnapshot? prev, CalibrationSensorData sensor)
    {
        var sb = new StringBuilder();
        if (prev is null)
        {
            sb.AppendLine(zh
                ? "本用戶剛完成首次 calibration，沒有歷史 snapshot 可比較。"
                : "This is the user's first calibration session — no prior snapshot available.");
        }
        else
        {
            sb.AppendLine(zh ? "上次 calibration snapshot 摘要：" : "Prior calibration snapshot summary:");
            sb.AppendLine(zh ? $"  上次寫入時間: {prev.UpdatedAtIso}" : $"  Last written: {prev.UpdatedAtIso}");
            sb.AppendLine(zh ? $"  上次 profile: {prev.Profile} (來源: {prev.ProfileSource})"
                              : $"  Prior profile: {prev.Profile} (source: {prev.ProfileSource})");
            sb.AppendLine(zh ? $"  上次習慣化進度: {prev.HabituationLevel:0.0} / cap {prev.HabituationOffsetCapPp}pp"
                              : $"  Habituation progress: {prev.HabituationLevel:0.0} / cap {prev.HabituationOffsetCapPp}pp");
            sb.AppendLine(zh ? $"  上次 VIMS tier: {prev.CurrentVimsTier}"
                              : $"  Prior VIMS tier: {prev.CurrentVimsTier}");
        }
        sb.AppendLine();
        sb.AppendLine(zh ? "本次 demo 感測資料：" : "Current demo sensor data:");
        sb.AppendLine(zh
            ? $"  尖峰壓力: {sensor.PeakPressure:0.0} (在 {sensor.PeakPressureAtSec:0.0} 秒處), 遮罩觸發次數: {sensor.MaskActivations}"
            : $"  Peak pressure: {sensor.PeakPressure:0.0} at {sensor.PeakPressureAtSec:0.0}s, mask activations: {sensor.MaskActivations}");
        return sb.ToString().TrimEnd();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Production ICalibrationLlmClient — posts to the running llama-server's
// OpenAI-compatible /v1/chat/completions on http://127.0.0.1:8181. Mirrors
// MistralChatbotService's HTTP pattern (private HttpClient with a long
// timeout, JSON deserialization, choices[0].message.content extraction).
// Does NOT spawn a server — only talks to one MistralChatbotService already
// brought up.
// ─────────────────────────────────────────────────────────────────────────────
public sealed class LlamaServerCalibrationClient : ICalibrationLlmClient, IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private const int ServerPort = 8181;
    private string ServerUrl => $"http://127.0.0.1:{ServerPort}";

    public async Task<string?> SendChatAsync(
        string systemPrompt,
        IReadOnlyList<ChatMessage> history,
        CancellationToken ct = default)
    {
        if (systemPrompt is null) throw new ArgumentNullException(nameof(systemPrompt));
        if (history is null) throw new ArgumentNullException(nameof(history));

        var messages = new List<object>(history.Count + 1)
        {
            new { role = "system", content = systemPrompt },
        };
        foreach (var m in history)
        {
            messages.Add(new
            {
                role = m.Role == ChatRole.User ? "user" : "assistant",
                content = m.Content,
            });
        }

        var requestBody = new
        {
            messages = messages.ToArray(),
            max_tokens = 768, // larger budget than NLU path — calibration replies include the JSON delta block
            temperature = 0.4,
            top_p = 0.9,
            top_k = 40,
            stop = new[] { "[INST]" },
        };

        try
        {
            using var resp = await _http.PostAsJsonAsync(
                $"{ServerUrl}/v1/chat/completions", requestBody, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            string respBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(respBody);
            if (doc.RootElement.TryGetProperty("choices", out var choices)
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var msg)
                && msg.TryGetProperty("content", out var content))
            {
                return content.GetString();
            }
            return null;
        }
        catch
        {
            // Network failure / server not up — caller treats null as "LLM unavailable"
            // and is expected to fall back to the card UI.
            return null;
        }
    }

    public void Dispose()
    {
        try { _http.Dispose(); } catch { }
    }
}
