using System.Text.RegularExpressions;
using SenseEase.Core.Configuration;

namespace SenseEase.Infrastructure.Nlp;

internal static class FeedbackLexicon
{
    private static readonly string[] DiscomfortKeywords =
    {
        // Dizziness / VIMS
        "暈", "暈眩", "頭暈", "天旋地轉", "暈起來", "快暈了", "開始暈", "有點暈", "超暈", "很暈", "暈死了",
        "旋轉感", "感覺在轉", "眼前轉", "暈暈的", "昏昏的", "昏頭", "昏眩",
        "晕", "头晕", "晕眩", "晕起来", "快晕了", "开始晕", "有点晕", "超晕", "很晕", "晕车感",
        "dizzy", "dizziness", "dizzy feeling", "getting dizzy", "feel dizzy", "feeling dizzy",
        "spinning", "spinning sensation", "vertigo", "lightheaded", "light-headed", "woozy", "swimmy", "giddy",
        "有點 dizzy", "好 dizzy", "超 dizzy", "感覺 dizzy", "dizzy 感",

        // Nausea
        "噁心", "想吐", "快吐了", "要吐了", "吐感", "胃不舒服", "反胃", "乾嘔", "嘔吐感", "翻胃", "想嘔", "乾噦",
        "恶心", "想吐", "快吐了", "要吐了", "反胃", "干呕", "呕吐感", "胃不舒服",
        "nausea", "nauseous", "nauseated", "feel like throwing up", "feel sick", "sick feeling", "queasy",
        "want to vomit", "about to vomit", "stomach turning", "stomach upset", "throwing up", "vomiting", "gagging",
        "gonna puke", "want to puke", "gonna throw up", "feel like puking",

        // Headache
        "頭痛", "頭很痛", "偏頭痛", "頭脹", "悶痛", "刺痛", "抽痛", "太陽穴痛", "後腦痛", "頭壓", "頭好重", "頭炸掉",
        "头痛", "头很痛", "偏头痛", "头胀", "闷痛", "刺痛", "太阳穴痛", "后脑痛", "头压",
        "headache", "head pain", "head hurts", "head ache", "migraine", "throbbing head", "pounding head",
        "tension headache", "pressure in head", "head pressure", "splitting headache",

        // Eye strain / visual
        "眼睛酸", "眼酸", "眼睛痛", "眼壓高", "眼睛乾", "乾澀", "眼睛澀", "眼睛脹", "眼睛累", "視覺疲勞", "眼疲勞",
        "眼花", "看不清", "視線模糊", "視力模糊", "看東西模糊", "重影", "眼皮重", "睜不開", "眼睛刺", "眼睛灼熱",
        "眼睛不舒服", "眼睛充血", "眼紅",
        "眼干", "干涩", "视觉疲劳", "眼疲劳", "视线模糊", "视力模糊", "重影",
        "eye strain", "eyestrain", "eye fatigue", "tired eyes", "dry eyes", "eye dryness", "eye irritation",
        "blurry vision", "blurred vision", "double vision", "eyes hurt", "eye pain", "eye pressure",
        "heavy eyelids", "can't focus", "difficulty focusing", "burning eyes", "watery eyes", "sore eyes",
        "眼睛 strain", "eye 很 tired", "看東西 blurry",

        // Balance / disorientation
        "失去平衡", "站不穩", "重心不穩", "身體搖晃", "走路不穩", "飄飄的", "感覺在搖", "空間感錯亂",
        "失去平衡", "站不稳", "身体摇晃", "空间感错乱",
        "unsteady", "off balance", "imbalance", "disoriented", "disorientation", "spatial disorientation",
        "wobbly", "rocking sensation", "swaying", "floating feeling",

        // Generic discomfort/fatigue
        "不舒服", "不適", "很不舒服", "怪怪的", "感覺怪", "不對勁", "疲勞", "疲憊", "好累", "無法集中", "無法專注", "恍神",
        "不舒服", "不适", "很不舒服", "怪怪的", "感觉怪", "疲劳", "疲惫", "无法集中",
        "uncomfortable", "discomfort", "unwell", "feel off", "feel weird", "not feeling well", "feel awful",
        "fatigue", "exhausted", "tired", "worn out", "loss of concentration", "brain fog", "drowsy", "sluggish",

        // Visual stimulation triggers as symptoms
        "畫面太動", "畫面在動", "看到在轉", "畫面轉", "太閃", "閃爍", "螢幕刺眼", "光線刺激", "畫面跳動",
        "视差感", "画面太动", "画面在转", "太闪", "闪烁", "屏幕刺眼",
        "screen flickering", "flicker", "flashing", "too much motion", "visual overload", "overwhelming visuals",
        "visual noise", "visual chaos", "screen is moving", "feels like moving",

        // Abbreviations
        "vims", "des", "vr sickness", "cybersickness", "cyber-sickness", "simulator sickness"
    };

    private static readonly string[] ComfortKeywords =
    {
        "好了", "好多了", "改善了", "有改善", "穩定了", "比較穩", "舒服多了", "還好", "沒事", "沒問題", "恢復了", "沒症狀",
        "不暈了", "比較不暈", "不噁心了", "頭不痛了",
        "好了", "好多了", "改善了", "稳定了", "比较稳", "还好", "没事", "恢复了", "不晕了", "不恶心了",
        "better", "much better", "feel better", "improved", "improving", "fine", "ok", "okay", "all good",
        "comfortable", "more comfortable", "stabilized", "stable now", "not dizzy anymore", "no longer dizzy",
        "less dizzy", "not nauseous anymore", "headache is gone", "recovered", "no symptoms"
    };

    // negation markers for prefix-window logic
    private static readonly string[] NegationMarkers =
    {
        "not", "no", "without", "不", "沒", "没有", "無"
    };

    private static readonly string[] SymptomCoreForNegation =
    {
        "dizzy", "dizziness", "nausea", "nauseous", "sick", "headache", "migraine", "eye strain", "blurred",
        "暈", "頭暈", "噁心", "想吐", "頭痛", "眼酸", "眼睛痛", "視覺疲勞",
        "晕", "恶心", "头痛", "眼酸", "眼睛痛", "视觉疲劳"
    };

    private static readonly string[] MildAdverbs =
    {
        "有點", "一點點", "微微", "稍微", "還好", "有点", "一点点", "微微", "稍微",
        "a little", "slightly", "a bit", "mildly", "somewhat"
    };

    private static readonly string[] ModerateAdverbs =
    {
        "蠻", "還蠻", "相當", "有些", "挺", "蛮", "相当",
        "quite", "fairly", "pretty", "moderately", "noticeably"
    };

    private static readonly string[] SevereAdverbs =
    {
        "很", "非常", "超", "極度", "嚴重", "真的很", "爆", "炸",
        "很", "非常", "超", "极度", "严重",
        "very", "really", "extremely", "severely", "incredibly", "so"
    };

    private static readonly string[] ExtremeAdverbs =
    {
        "超級", "無法忍受", "受不了", "崩潰", "撐不住", "快死了",
        "超级", "无法忍受", "受不了", "快死了",
        "unbearable", "can't take it", "absolutely", "completely"
    };

    private static readonly string[] ContextTriggers =
    {
        "vr", "ar", "headset", "quest", "vision pro", "虛擬實境", "混合實境",
        "螢幕", "screen", "display", "用電腦", "用手機",
        "在車上", "坐車", "搭捷運", "on the bus", "in the car",
        "滾動", "捲動", "scroll", "scrolling", "快速切換", "switching",
        "一直看", "看很久", "long time", "hours", "staring"
    };

    private static readonly string[] DarkerKeywords =
    { "太亮", "刺眼", "再暗", "暗一點", "更暗", "太刺", "太亮了", "亮", "too bright", "darker", "dim it", "more dark" };

    private static readonly string[] LighterKeywords =
    { "太暗", "看不清", "看不見", "太黑", "亮一點", "暗了", "看不到", "too dark", "lighter", "brighter", "can't see", "hard to see" };

    // Beta-1 #15 (clinical-safety): the BARE grid nouns (格線 / 網格 / grid) were REMOVED from
    // this LESS list. They made any discomfort utterance that merely NAMES the grid (e.g.
    // 「網格讓我頭暈」 / "the grid makes me dizzy") FADE the protective Chang-Hain anchor grid:
    // DeriveAdjustments sets a protective +gv for ReportDiscomfort, then ClassifyShaderHints saw
    // the bare noun and forced gv negative, overriding it. A grid REDUCTION now requires an
    // explicit reduce/dislike cue (干擾 / 分散 / 太多 / distracting / busy / ...), so naming the
    // grid while distressed deepens protection instead of removing the anchor that suppresses
    // vection. Explicit "/grid off" + the on-purpose ShaderCommandLexicon path are unaffected.
    private static readonly string[] GridLessKeywords =
    { "干擾", "格線太多", "網格太明顯", "分散", "distracting", "busy", "too many lines" };

    private static readonly string[] GridMoreKeywords =
    { "看不到格", "需要參考", "錨點", "more grid", "show grid", "need reference" };

    // Round-3 (Kirby item 5): the chatbot's edge-feather vocabulary is REMOVED — edge
    // feathering is user-meaningless, so the chatbot neither proposes nor applies it.
    // The SofterEdgeKeywords table and its feather_width_delta mapping are gone; the
    // /feather power-user slash command, the engine knob and the A2 renderer wiring are
    // unaffected (those live in ShaderCommandLexicon / the engine, not here).

    private static readonly string[] ResetKeywords =
    { "重設", "重置", "恢復", "回到原本", "回復預設", "復原", "reset", "undo", "back to normal", "default", "original", "restore" };

    /// <summary>
    /// Detect overlay-shader adjustment hints from free text. Returns normalized
    /// deltas (each in [-0.3,0.3]) plus a reset flag. Used as the model-free fallback.
    /// </summary>
    public static (double maskDepthDelta, double gridVisibilityDelta, double featherWidthDelta, bool reset)
        ClassifyShaderHints(string? text)
    {
        string t = Normalize(text);
        if (string.IsNullOrWhiteSpace(t)) return (0, 0, 0, false);

        if (ContainsAny(t, ResetKeywords)) return (0, 0, 0, true);

        double md = 0, gv = 0;
        // NE4: "darker / too bright" must DEEPEN the mask (positive md); "lighter
        // / too dark" must LIGHTEN it (negative md). Both were -= 0.15, so "darker"
        // actually lightened the screen and the two opposite intents were
        // indistinguishable. Positive deepens, per the shader-delta convention.
        if (ContainsAny(t, DarkerKeywords))  md += 0.15;
        if (ContainsAny(t, LighterKeywords)) md -= 0.15;
        if (ContainsAny(t, GridLessKeywords)) gv -= 0.15;
        if (ContainsAny(t, GridMoreKeywords)) gv += 0.15;
        // Round-3: feather_width_delta is always 0 — the chatbot edge-feather
        // vocabulary was removed (edge feathering is user-meaningless per Kirby).
        return (Math.Clamp(md, -0.3, 0.3), Math.Clamp(gv, -0.3, 0.3), 0.0, false);
    }

    public static (string intent, int fovDelta) Classify(string? text)
    {
        string t = Normalize(text);
        if (string.IsNullOrWhiteSpace(t))
            return ("Unknown", 0);

        bool hasDiscomfort = ContainsAny(t, DiscomfortKeywords);
        bool hasComfort = ContainsAny(t, ComfortKeywords);
        bool negatesDiscomfort = HasNegatedSymptom(t, 3);

        if (hasDiscomfort && !negatesDiscomfort)
            return ("ReportDiscomfort", SenseDefaults.ComplaintFovDelta);
        if (hasComfort || negatesDiscomfort)
            return ("ReportComfort", 0);
        return ("Unknown", 0);
    }

    public static double SeverityScore(string? text)
    {
        string t = Normalize(text);
        if (string.IsNullOrWhiteSpace(t)) return 0.0;

        if (ContainsAny(t, ExtremeAdverbs)) return 1.0;
        if (ContainsAny(t, SevereAdverbs)) return 0.75;
        if (ContainsAny(t, ModerateAdverbs)) return 0.5;
        if (ContainsAny(t, MildAdverbs)) return 0.25;
        return 0.5;
    }

    /// <summary>
    /// Bucket the continuous severity score into an integer 1-5 for the model-free fallback.
    /// </summary>
    public static int SeverityBucket(string? text)
        => Math.Clamp((int)Math.Round(SeverityScore(text) * 4) + 1, 1, 5);

    /// <summary>
    /// Map a classified intent + severity (1-5) to concrete overlay deltas, with directional
    /// overrides from explicit brightness/grid cues. Shared by the model path and the
    /// keyword fallback so both behave identically. The feather_width_delta component is
    /// always 0 — the chatbot's edge-feather vocabulary was removed in round-3 (edge
    /// feathering is user-meaningless per Kirby); the tuple shape is kept so the many
    /// call sites compile unchanged.
    /// </summary>
    public static (int fovDelta, double maskDepthDelta, double gridVisibilityDelta, double featherWidthDelta)
        DeriveAdjustments(string intent, int severity, string? text)
    {
        severity = Math.Clamp(severity, 1, 5);
        int fov = 0; double md = 0, gv = 0;
        const double fw = 0.0; // Round-3: chatbot never derives a feather delta.

        if (intent == "ReportDiscomfort")
        {
            // C7: single-source step-magnitude table (severity clamped to 1..5 above ⇒ index 0..4).
            fov = SenseDefaults.FovStepBySeverity[severity - 1];
            md  = SenseDefaults.MaskDepthStepBySeverity[severity - 1];
            gv  = severity >= 3 ? 0.06 : 0.03;
        }
        // ReportComfort / ResetRequest / Unknown -> all zero.

        // Directional overrides: explicit cues beat the severity-derived defaults.
        var (hintMd, hintGv, _, _) = ClassifyShaderHints(text);
        if (hintMd != 0) md = hintMd;
        if (hintGv != 0) gv = hintGv;

        // C7: clamps reference the single-source per-call bounds in SenseDefaults.
        return (Math.Clamp(fov, SenseDefaults.MaxFovDeltaPerCall, 0),
                Math.Clamp(md, -SenseDefaults.MaxShaderDeltaPerCall, SenseDefaults.MaxShaderDeltaPerCall),
                Math.Clamp(gv, -SenseDefaults.MaxShaderDeltaPerCall, SenseDefaults.MaxShaderDeltaPerCall),
                Math.Clamp(fw, -SenseDefaults.MaxShaderDeltaPerCall, SenseDefaults.MaxShaderDeltaPerCall));
    }

    public static bool HasContextTrigger(string? text)
    {
        string t = Normalize(text);
        return ContainsAny(t, ContextTriggers);
    }

    // ── Followup question builder ────────────────────────────────────────────────────
    // Markers used to detect *which* symptom bucket the user already named (so we
    // don't ask the obvious). Kept narrower than DiscomfortKeywords because false
    // positives here suppress the followup; better to err on the side of asking.
    // Use the 4-char "dizz" stem so it matches "dizzy", "dizziness", "dizzied",
    // and "dizzying". The full word "dizzy" doesn't contain "dizziness" and vice
    // versa (5th char differs: 'y' vs 'i'), so neither full form alone is sufficient.
    private static readonly string[] DizzyMarkers   = { "暈", "晕", "dizz", "spin", "vertigo", "lightheaded", "woozy" };
    private static readonly string[] NauseaMarkers  = { "噁心", "恶心", "想吐", "puke", "vomit", "nausea", "queasy" };
    private static readonly string[] HeadacheMarkers = { "頭痛", "头痛", "headache", "migraine", "head pain", "head hurts" };
    private static readonly string[] EyeMarkers     = { "眼", "eye", "vision", "blur", "視覺", "视觉" };

    // Vague-discomfort hints that on their own are too ambiguous to drive an
    // adjustment but DO warrant a follow-up question. "feel off", "怪怪的", etc.
    private static readonly string[] VagueDiscomfortHints =
    {
        "feel", "off", "weird", "strange", "uncomfortable", "not great", "something",
        "怪", "怪怪的", "不舒服", "不適", "不对劲", "不對勁", "不适", "感覺", "感觉",
    };

    /// <summary>
    /// Decide whether the bot should ask a clarifying follow-up question instead of
    /// (or in addition to) committing to an overlay adjustment. Returns a 2–4-option
    /// picker when the user's intent is detectable as discomfort-adjacent but the
    /// specific symptom is unclear; returns (null, null) otherwise — the caller
    /// should treat that as "no follow-up needed, act on the parsed intent."
    ///
    /// Used by both the lexicon fallback path AND as a belt-and-suspenders override
    /// over the LLM path (so a model that forgets to ask still gets a sensible
    /// clarification offered when the user input is unambiguously vague).
    /// </summary>
    public static (string? question, string[]? options) TryBuildFollowup(
        string? text, string intent, int severity)
    {
        string t = Normalize(text);
        if (string.IsNullOrEmpty(t)) return (null, null);

        bool isZh = LooksChinese(text);
        bool mentionsDizzy    = ContainsAny(t, DizzyMarkers);
        bool mentionsNausea   = ContainsAny(t, NauseaMarkers);
        bool mentionsHeadache = ContainsAny(t, HeadacheMarkers);
        bool mentionsEye      = ContainsAny(t, EyeMarkers);
        bool anySpecific      = mentionsDizzy || mentionsNausea || mentionsHeadache || mentionsEye;
        bool hasVagueHint     = ContainsAny(t, VagueDiscomfortHints);

        // Standard 4-bucket symptom picker; same options for both branches so the UI
        // only has one card layout to render.
        string[] symptomOptions = new[]
        {
            isZh ? "暈眩 / Dizziness" : "Dizziness",
            isZh ? "噁心 / Nausea"    : "Nausea",
            isZh ? "頭痛 / Headache"  : "Headache",
            isZh ? "眼睛疲勞 / Eye strain" : "Eye strain",
        };

        // Followup policy (broadened per user feedback): be proactive. Ask unless
        // the user has given us enough to act confidently. "Enough" means EITHER
        // a specific symptom was named, OR the intent is unambiguously non-symptom
        // (ReportComfort / ResetRequest / AnalyzeRequest).
        //
        // Case 1: intent unknown — we have no idea what they want. Ask what's up.
        if (intent == "Unknown" && t.Length < 200)
        {
            // Vague-discomfort phrasing → empathetic "what are you feeling" wording;
            // otherwise the message is genuinely off-topic, ask the open question with
            // symptom options anyway (user can free-type if none fit).
            string question = hasVagueHint
                ? (isZh ? "可以告訴我您正在感受到哪一種不適嗎？" : "Which sensation matches what you're feeling right now?")
                : (isZh ? "請問您現在的狀況比較接近哪一種？" : "Which of these is closest to what you're experiencing?");
            return (question, symptomOptions);
        }

        // Case 2: discomfort reported but no specific symptom named — at ANY severity.
        // Even mild "I'm uncomfortable" should be clarified before applying deltas;
        // the previous severity>=3 gate let mild unspecific discomfort silently apply
        // a default FOV cut, which is exactly the over-eager behavior the user wanted
        // tightened up.
        if (intent == "ReportDiscomfort" && !anySpecific)
        {
            return (
                isZh ? "哪一種感覺最強烈？" : "Which symptom feels strongest right now?",
                symptomOptions);
        }

        return (null, null);
    }

    /// <summary>True when <paramref name="text"/> contains an unambiguous symptom
    /// marker (dizziness / nausea / headache / eye strain in either English or
    /// Chinese). Used as a veto over the LLM's FollowupRequest: when the user has
    /// already named the symptom, asking "which symptom?" is the wrong move —
    /// promote straight to ReportDiscomfort and act on it instead.</summary>
    public static bool MentionsSpecificSymptom(string? text)
    {
        string t = Normalize(text);
        if (string.IsNullOrEmpty(t)) return false;
        return ContainsAny(t, DizzyMarkers)
            || ContainsAny(t, NauseaMarkers)
            || ContainsAny(t, HeadacheMarkers)
            || ContainsAny(t, EyeMarkers);
    }

    /// <summary>
    /// Build the canonical follow-up payload used when a VIMS-pressure spike
    /// auto-opens the chatbot. Severity reflects how big the spike was on a 0–1
    /// normalized scale (caller passes 0..1 from the detector).
    /// </summary>
    public static (string question, string[] options) BuildSpikeFollowup(bool isChineseUiHint, double spikeNormalized)
    {
        // Spike intensity wording: small / medium / large. Drives empathy in the
        // question text without committing to a specific severity bucket.
        string intensity = spikeNormalized switch
        {
            >= 0.6 => isChineseUiHint ? "急遽" : "sharp",
            >= 0.3 => isChineseUiHint ? "明顯" : "noticeable",
            _      => isChineseUiHint ? "輕微" : "small",
        };

        string question = isChineseUiHint
            ? $"系統偵測到 VIMS 壓力{intensity}上升。請問您現在感受到什麼？"
            : $"I noticed a {intensity} rise in VIMS pressure. What are you experiencing right now?";

        string[] options =
        {
            isChineseUiHint ? "暈眩 / Dizziness"        : "Dizziness",
            isChineseUiHint ? "噁心 / Nausea"           : "Nausea",
            isChineseUiHint ? "頭痛 / Headache"         : "Headache",
            isChineseUiHint ? "畫面太刺激 / Visual overload" : "Visual overload",
        };
        return (question, options);
    }

    public static string BuildFallbackResponse(string? text, string intent)
    {
        bool zh = LooksChinese(text);
        return intent switch
        {
            "ReportDiscomfort" => zh
                ? "已偵測到不適，我已收緊 FOV 保護與周邊遮罩，降低視覺刺激。系統會持續監測 OFI 與壓力變化。"
                : "Discomfort detected. I tightened FOV protection and peripheral masking to reduce visual load. I will keep monitoring OFI and pressure.",
            "ReportComfort" => zh
                ? "收到，你目前較穩定。系統會持續監測，並逐步放寬 FOV 保護。"
                : "Understood. Your condition seems more stable. The system will keep monitoring and gradually relax FOV protection.",
            _ => zh
                ? "我可以協助偵測暈眩與視覺不適。請描述是否有暈、噁心、頭痛或眼睛疲勞。"
                : "I can help detect motion discomfort. Please tell me if you feel dizziness, nausea, headache, or eye strain."
        };
    }

    private static bool HasNegatedSymptom(string text, int maxTokenDistance)
    {
        string[] tokens = Tokenize(text);
        if (tokens.Length == 0) return false;

        for (int i = 0; i < tokens.Length; i++)
        {
            if (!NegationMarkers.Any(n => tokens[i].Contains(n, StringComparison.Ordinal))) continue;

            int end = Math.Min(tokens.Length - 1, i + maxTokenDistance + 1);
            for (int j = i + 1; j <= end; j++)
            {
                string segment = string.Join(' ', tokens[i..(j + 1)]);
                if (SymptomCoreForNegation.Any(s => segment.Contains(s, StringComparison.Ordinal)))
                    return true;
            }
        }

        // Chinese compact forms without spaces.
        return text.Contains("不暈", StringComparison.Ordinal) ||
               text.Contains("不晕", StringComparison.Ordinal) ||
               text.Contains("不噁心", StringComparison.Ordinal) ||
               text.Contains("不恶心", StringComparison.Ordinal) ||
               text.Contains("沒有頭痛", StringComparison.Ordinal) ||
               text.Contains("没有头痛", StringComparison.Ordinal);
    }

    private static string[] Tokenize(string text)
    {
        // Keep ASCII words and CJK chunks.
        var matches = Regex.Matches(text, @"[a-z0-9\-\']+|[\u4e00-\u9fff]+");
        return matches.Select(m => m.Value).ToArray();
    }

    private static bool ContainsAny(string text, IEnumerable<string> keywords)
        => keywords.Any(k => text.Contains(k, StringComparison.Ordinal));

    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        string t = text.Trim().ToLowerInvariant();
        t = t.Replace('，', ',').Replace('。', '.').Replace('！', '!').Replace('？', '?');
        t = Regex.Replace(t, @"\s+", " ");
        return t;
    }

    private static bool LooksChinese(string? text)
        => !string.IsNullOrEmpty(text) && text.Any(c => c >= 0x4E00 && c <= 0x9FFF);
}
