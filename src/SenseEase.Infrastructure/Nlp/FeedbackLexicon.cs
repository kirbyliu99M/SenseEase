using System.Text.RegularExpressions;
using SenseEase.Core.Configuration;
using SenseEase.Infrastructure.Real;

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
    { "看不到格", "需要參考", "錨點", "more grid", "show grid", "need reference",
      // 2026-06-20 (what_to_adjust card "Steadier view" chip): explicit POSITIVE-grid cues — the user
      // deliberately picked a steadier view, so these strengthen the anchor grid (symmetric with the
      // explicit reduce/dislike cues in GridLessKeywords; bare grid nouns stay absent for safety).
      "穩定畫面", "steadier view", "steady the view" };

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

            // Directional overrides: explicit cues beat the severity-derived defaults. GATED to
            // ReportDiscomfort (audit 2026-06-22): the only actuating intent. Previously this override
            // ran for EVERY intent, so a non-actuating turn that named an edge/grid cue — e.g. a
            // brush-off "算了，邊緣再暗一點" the model demoted to Unknown — leaked a +0.15 mask delta.
            // A non-actuating intent must NEVER actuate via the override (I1/I6).
            var (hintMd, hintGv, _, _) = ClassifyShaderHints(text);
            if (hintMd != 0) md = hintMd;
            if (hintGv != 0) gv = hintGv;
        }
        // ReportComfort / ResetRequest / Unknown -> all zero (severity defaults AND the override skipped).

        // C7: clamps reference the single-source per-call bounds in SenseDefaults.
        return (Math.Clamp(fov, SenseDefaults.MaxFovDeltaPerCall, 0),
                Math.Clamp(md, -SenseDefaults.MaxShaderDeltaPerCall, SenseDefaults.MaxShaderDeltaPerCall),
                Math.Clamp(gv, -SenseDefaults.MaxShaderDeltaPerCall, SenseDefaults.MaxShaderDeltaPerCall),
                Math.Clamp(fw, -SenseDefaults.MaxShaderDeltaPerCall, SenseDefaults.MaxShaderDeltaPerCall));
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
    // Eye SYMPTOM descriptors only — NOT a bare body-location word ("眼" / "eye" / "vision"). The bare
    // location made a vague "I feel weird with my eyes" match as a specific eye symptom → anySpecific=true
    // → the clarify picker was skipped and the FOV silently cut (tester report 2026-06-17). Mirrors the
    // HeadacheMarkers approach (which already required "head pain"/"head hurts", not bare "head"). A bare
    // location + a vague sensation now falls through to the clarify picker (TryBuildFollowup Case 2).
    private static readonly string[] EyeMarkers     =
    {
        "眼睛痛", "眼痛", "眼睛酸", "眼酸", "眼睛乾", "眼乾", "眼睛澀", "乾澀", "眼睛脹", "眼睛胀",
        "眼睛疲勞", "眼疲勞", "眼疲劳", "視覺疲勞", "视觉疲劳", "眼睛刺", "眼睛灼熱",
        "eye strain", "eyestrain", "eye fatigue", "eye pain", "eyes hurt", "sore eyes", "tired eyes",
        "dry eyes", "eye dryness", "burning eyes", "blur", "double vision",
    };

    // Vague-discomfort hints that on their own are too ambiguous to drive an
    // adjustment but DO warrant a follow-up question. "feel off", "怪怪的", etc.
    private static readonly string[] VagueDiscomfortHints =
    {
        "feel", "off", "weird", "strange", "uncomfortable", "not great", "something",
        "怪", "怪怪的", "不舒服", "不適", "不对劲", "不對勁", "不适", "感覺", "感觉",
    };

    // 2026-06-20 clarification-card taxonomy. VagueChangeHints = an imperative to CHANGE something with NO
    // symptom and NO direction ("make it better", "幫我弄一下") → the what_to_adjust card (B). EdgeIssueHints
    // = an edge/brightness complaint with no darker/lighter direction → the darker-or-lighter card (D).
    private static readonly string[] VagueChangeHints =
    {
        "make it better", "fix it", "do something", "help", "adjust", "improve", "make it",
        "幫我弄", "弄一下", "弄好", "調整一下", "調一下", "改善一下", "處理一下", "弄好一點",
    };
    private static readonly string[] EdgeIssueHints =
    {
        "edges look", "edges are off", "hard to look", "too much going on", "too busy",
        "邊緣怪", "邊緣不對", "看起來不太對", "畫面太亂", "看了不舒服",
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
        string? text, string intent, int severity, bool? uiEnglishPin = null)
    {
        string t = Normalize(text);
        if (string.IsNullOrEmpty(t)) return (null, null);

        bool isZh = uiEnglishPin is bool en ? !en : PreferZh(text);
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

        // ── Clarification-card taxonomy (2026-06-20): pick a MORE RELEVANT card than "which symptom?"
        // when the situation calls for it. These sit ABOVE the symptom-picker cases and only fire on their
        // specific cues; each chip carries a keyword the classifier already routes (so taps degrade
        // gracefully on the pure keyword-fallback path). Deterministic mirror of the prompt's card policy.
        var (cardMd, cardGv, _, _) = ClassifyShaderHints(t);
        bool noDirection = cardMd == 0 && cardGv == 0;

        // Card E — TWO+ symptoms named whose actuators conflict (the grid that suppresses vection can
        // aggravate eye strain): ask which is worst. Reuses the symptom chips, different question.
        int namedCount = (mentionsDizzy ? 1 : 0) + (mentionsNausea ? 1 : 0)
                       + (mentionsHeadache ? 1 : 0) + (mentionsEye ? 1 : 0);
        if (intent == "ReportDiscomfort" && namedCount >= 2)
            return (isZh ? "哪一項最困擾您？" : "Which is bothering you most?", symptomOptions);

        // Card D — an edge/brightness complaint with NO darker/lighter direction: ask the direction first.
        if (noDirection && !anySpecific && ContainsAny(t, EdgeIssueHints))
            return (isZh ? "邊緣要更暗還是更亮？" : "Should the edges be darker or lighter?",
                    isZh ? new[] { "邊緣更暗", "邊緣更亮" } : new[] { "Darker edges", "Lighter edges" });

        // Card B — a vague CHANGE request with no symptom and no direction ("make it better", "幫我弄一下"):
        // ask WHAT to adjust, not which symptom. The flagship addition (used to dead-end as a plain reply).
        if (!anySpecific && noDirection && ContainsAny(t, VagueChangeHints))
            return (isZh ? "請問要調整哪一項？" : "Sure — what should I adjust?",
                    isZh ? new[] { "降低畫面動態", "邊緣更暗", "穩定畫面", "眼睛休息" }
                         : new[] { "Less screen motion", "Darker edges", "Steadier view", "Rest my eyes" });

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
    /// <summary>True when the text contains an explicit comfort/improvement cue
    /// (好多了 / 改善了 / better / improved …) OR negates a symptom (不暈 / no longer dizzy).
    /// Used by <c>NluComplianceGate</c> to tell a genuine "my headache is better" (real
    /// ReportComfort) from a BARE symptom name like "頭痛" the model mis-tagged ReportComfort:
    /// the bare name has a symptom keyword but NO comfort/negation cue, so this returns false
    /// and the gate corrects it to ReportDiscomfort. Single-sources the comfort predicate
    /// <see cref="Classify"/> already uses, so the two can't drift apart.</summary>
    public static bool ExpressesComfortOrNegation(string? text)
    {
        string t = Normalize(text);
        if (string.IsNullOrEmpty(t)) return false;
        return ContainsAny(t, ComfortKeywords) || HasNegatedSymptom(t, 3);
    }

    public static bool MentionsSpecificSymptom(string? text)
    {
        string t = Normalize(text);
        if (string.IsNullOrEmpty(t)) return false;
        return ContainsAny(t, DizzyMarkers)
            || ContainsAny(t, NauseaMarkers)
            || ContainsAny(t, HeadacheMarkers)
            || ContainsAny(t, EyeMarkers);
    }

    // Dismissive closers / deferrals the model tends to mis-tag ReportComfort but which express NO genuine
    // comfort — just a brush-off ('算了', '沒事啦', 'never mind'). The gate demotes ReportComfort→Unknown when
    // one matches, so a brush-off never signals comfort to the engine (which would relax protection).
    private static readonly string[] DismissiveClosures =
    {
        "算了", "再看看", "之後再說", "以後再說", "當我沒說", "沒差", "不重要", "沒什麼啦", "沒事啦",
        "沒事沒事", "不用了", "不說了", "看心情", "隨便啦", "就那樣", "懶得",
        "never mind", "nevermind", "forget it", "whatever", "no reason", "just because", "anyway", "nvm",
    };

    /// <summary>A bare dismissive closer / deferral ('算了', '沒事啦', 'never mind') the small model tends to
    /// tag ReportComfort — but it carries NO genuine comfort, so it must not relax protection. The gate
    /// demotes these to Unknown (Kirby 2026-06-22, from the ZH stress sweep). Kept NARROW: a short utterance
    /// that IS essentially the dismissal, not a real comfort report ('好多了/改善了') that trails off.</summary>
    public static bool IsDismissiveClosure(string? text)
    {
        string t = Normalize(text);
        if (string.IsNullOrEmpty(t) || t.Length > 40) return false; // a bare, short closer only
        return ContainsAny(t, DismissiveClosures);
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

    public static string BuildFallbackResponse(string? text, string intent, bool? uiEnglishPin = null)
    {
        bool zh = uiEnglishPin is bool en ? !en : PreferZh(text);
        return intent switch
        {
            "ReportDiscomfort" => zh
                ? "已降低畫面晃動並柔化邊緣，以減少視覺刺激。如仍不適請告知。"
                : "I hear you're uncomfortable. I've eased the on-screen motion and softened the edges to help — tell me if it eases up for you.",
            "ReportComfort" => zh
                ? "了解，已記錄。如再有不適請告知。"
                : "Understood. Your condition seems more stable. The system will keep monitoring and gradually relax FOV protection.",
            _ => zh
                ? "可協助處理視覺不適。請描述是否有暈眩、噁心、頭痛或眼睛疲勞。"
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

    // Lexicon backstop (Kirby 2026-06-22): a few short ASCII words substring-match FALSELY inside common
    // words ("ok" in "look", "fine" in "define", "tired" in "retired", "so"/"very" in "person"/"every",
    // "ar" in "car", "des" in "describe"). These must match as a WHOLE token; everything else — CJK,
    // multi-word phrases, and intentional stems like "dizz"/"blur" — keeps plain substring matching so
    // symptom/comfort detection is unchanged. Narrows the over-broad deterministic match, no stem risk.
    private static readonly HashSet<string> WordBoundaryAscii =
        new(StringComparer.Ordinal) { "ok", "okay", "fine", "tired", "des", "so", "very", "ar", "vr" };

    private static bool ContainsAny(string text, IEnumerable<string> keywords)
        => keywords.Any(k => ContainsKeyword(text, k));

    private static bool ContainsKeyword(string text, string keyword)
    {
        if (!WordBoundaryAscii.Contains(keyword))
            return text.Contains(keyword, StringComparison.Ordinal);
        // Whole-token match: the keyword must be a standalone ASCII token, not embedded in a larger word.
        foreach (var tok in Tokenize(text))
            if (string.Equals(tok, keyword, StringComparison.Ordinal)) return true;
        return false;
    }

    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        string t = text.Trim().ToLowerInvariant();
        t = t.Replace('，', ',').Replace('。', '.').Replace('！', '!').Replace('？', '?');
        t = Regex.Replace(t, @"\s+", " ");
        return t;
    }

    internal static bool LooksChinese(string? text)
        => !string.IsNullOrEmpty(text) && text.Any(c => c >= 0x4E00 && c <= 0x9FFF);

    /// <summary>Reply-language selector: the live UI-language toggle WINS (Kirby's decision —
    /// "AI replies follow the language toggle"), independent of the script the user happened to
    /// type in. Falls back to the input's own script (<see cref="LooksChinese"/>) only when the
    /// host hasn't wired the toggle — headless / lexicon-only / tests. Single-sources the EN/ZH
    /// precedence used by the canned fallback reply, the follow-up card, and the gate's
    /// question-trap line so they can't drift, and mirrors
    /// <c>LocalLlmChatbotService.BuildLanguagePin</c> / <c>ReplyComplianceScrubber.IsEnglishReply</c>.</summary>
    internal static bool PreferZh(string? text)
        => ChatbotUiLanguage.IsEnglish is bool en ? !en : LooksChinese(text);
}
