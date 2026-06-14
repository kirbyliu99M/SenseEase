using System;
using SenseEase.Core.Configuration;
using SenseEase.Core.Models;

namespace SenseEase.Core.Inference;

// ─────────────────────────────────────────────────────────────────────────────
// G1 (Alpha-2) — strict-language + slash-command parser for shader parameters.
//
// PURE, static, testable. Parses a SINGLE user utterance into a ShaderCommand
// the ChatbotViewModel executes locally (no LLM round-trip), so a request like
// "turn the grid off" / "把格線完全關掉" / "/grid off" resolves INSTANTLY and
// absolute-set (bypassing the delta-only [-0.3,+0.3] LLM path).
//
// Two layers, in priority order (both resolve to a ShaderCommand):
//   1. SLASH GRAMMAR — explicit "/grid off", "/mask 30", "/fov 60", "/reset
//      shader", "/help", "/show". Leading-token, case-insensitive, SINGLE
//      command — extra args / chaining are REJECTED (fail closed, locked-spec #4/#5).
//   2. STRICT-NL WHITELIST — a small WHOLE-UTTERANCE (trim+lowercase, NOT
//      substring) en/zh table so "I don't want the grid off" does NOT match
//      (locked-spec #6). First match wins; no match → Kind=None (fall to LLM).
//
// Mirrors HabituationProposalLexicon's style (ordered tables, first-match-wins,
// zh+en). Locked safety constraints enforced HERE (the caller does no further
// validation per the IInferenceEngine.SetShaderParam contract):
//   • Mask floor: /mask 0..19 and "mask off"/拿掉遮罩/關掉遮罩 are REJECTED
//     (mask is the core VIMS protection; lowest safe is /mask 20).      (#1)
//   • /fov outside [15,100] is REJECTED.
//   • No profile changes (not in the grammar).                          (#3)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>What a parsed shader utterance resolved to.</summary>
public enum ShaderCommandKind
{
    /// <summary>No slash command and no strict-NL match — caller falls through to the LLM.</summary>
    None,

    /// <summary>Absolute-set <see cref="ShaderCommand.Param"/> to <see cref="ShaderCommand.Value"/>.</summary>
    SetParam,

    /// <summary>Reset shader knobs + FOV offset to profile defaults (Q4.5: NOT habituation offset).</summary>
    ResetShader,

    /// <summary>Print the inline command list (local, no LLM).</summary>
    Help,

    /// <summary>Print the current shader values inline (local, no LLM).</summary>
    Show,

    /// <summary>A recognised-but-refused command (e.g. mask floor / out-of-range). Reply explains why.</summary>
    Reject,
}

/// <summary>G1 parse result. <see cref="Reply"/> is the local, bilingual-aware
/// assistant text the caller pushes via <c>ChatbotViewModel.PushAssistantBubble</c>
/// (plain string, NOT loc:T). For <see cref="ShaderCommandKind.Show"/> the caller
/// composes the live-values line itself (the lexicon is pure and cannot read the
/// engine), so <see cref="Reply"/> is empty there.</summary>
public readonly record struct ShaderCommand(
    ShaderCommandKind Kind,
    ShaderParam? Param,
    double Value,
    string Reply,
    string PatternId);

public static class ShaderCommandLexicon
{
    /// <summary>"On" default for the grid — the profile-default grid visibility the
    /// lexicon resolves "/grid on" / "show grid" to. The lexicon is PURE so it can't
    /// read the engine's active profile; it uses the canonical baseline default
    /// (<see cref="ShaderParams.DefaultGridVisibility"/> ≈ 0.75). The engine still
    /// clamps on receipt. SENSITIVE's slightly-lower default (0.60) is a known minor
    /// deviation — a one-line "/grid on" is a hint to "bring the grid back", not a
    /// per-profile-exact restore (use "/reset shader" for the exact profile baseline).</summary>
    public const double GridOnDefault = ShaderParams.DefaultGridVisibility;

    /// <summary>Mask depth safety floor (locked-spec #1). /mask below this — and any
    /// "mask off" strict-NL phrase — is REJECTED. Mask is the core VIMS protection.</summary>
    public const double MaskDepthSafetyFloor = 0.20;

    /// <summary>Parse a single user utterance. Slash grammar wins over strict-NL;
    /// strict-NL wins over the LLM. Returns <see cref="ShaderCommandKind.None"/> when
    /// nothing matches (the common case — most turns fall through to the LLM).</summary>
    /// <param name="userText">The raw user turn (already crisis-screened upstream).</param>
    /// <param name="isEnglish">Caller-passed UI-language flag (mirrors
    /// <c>LocalizationManager.IsEnglish</c>): selects the EN vs ZH reply half.</param>
    public static ShaderCommand Parse(string? userText, bool isEnglish)
    {
        if (string.IsNullOrWhiteSpace(userText)) return None();
        string trimmed = userText.Trim();

        // ── Layer 1: slash grammar (leading '/') ─────────────────────────────
        if (trimmed.StartsWith('/'))
            return ParseSlash(trimmed, isEnglish);

        // ── Layer 2: grid on/off INTENT (verb + noun, either order) ──────────
        // Round-3 (Kirby item 5): the live failure was 「關閉網格」 → the bot routed
        // to the LLM (which applied a wrong-target/wrong-sign DELTA). The strict-NL
        // whitelist below is whole-utterance exact-match, so it missed the 關閉 verb
        // and the 網格 noun. This layer catches an explicit grid show/hide REQUEST by
        // co-occurrence of a hide/show VERB with a grid NOUN in EITHER order — and
        // resolves to the SAME absolute-set ShaderCommand /grid off|on uses. It is
        // negation-guarded (see DetectGridOnOff) so "I don't want the grid off" /
        // "why is the grid off" still fall through to the LLM (locked-spec #6).
        var gridIntent = DetectGridOnOff(trimmed, isEnglish);
        if (gridIntent.Kind != ShaderCommandKind.None)
            return gridIntent;

        // ── Layer 3: strict-NL whole-utterance whitelist ─────────────────────
        return ParseStrictNl(trimmed, isEnglish);
    }

    // ── Layer 1 implementation ────────────────────────────────────────────────

    private static ShaderCommand ParseSlash(string trimmed, bool isEnglish)
    {
        // Split on ASCII whitespace; collapse runs. Leading token is the command.
        string[] tokens = trimmed.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return None();

        // Chaining guard (locked-spec #5): a ';' anywhere is a multi-command attempt.
        if (trimmed.Contains(';'))
            return Reject(Pick(isEnglish,
                "一次只能下一個指令,不支援串接。",
                "One command at a time — chaining isn't supported."), "slash.chaining");

        string cmd = tokens[0].ToLowerInvariant();

        switch (cmd)
        {
            case "/grid":
                return ParseGrid(tokens, isEnglish);
            case "/mask":
                return ParseScalar01(tokens, ShaderParam.MaskDepth, "mask", isEnglish);
            case "/feather":
                return ParseScalar01(tokens, ShaderParam.FeatherWidth, "feather", isEnglish);
            case "/fov":
                return ParseFov(tokens, isEnglish);
            case "/reset":
                // Only "/reset shader" is valid; "/reset" alone or with other args rejects.
                if (tokens.Length == 2 && tokens[1].Equals("shader", StringComparison.OrdinalIgnoreCase))
                    return new ShaderCommand(
                        ShaderCommandKind.ResetShader, null, 0,
                        Pick(isEnglish,
                            "已將遮罩設定還原為個人預設(習慣化進度保留)。",
                            "Shader reset to your profile defaults (habituation progress kept)."),
                        "slash.reset_shader");
                return RejectExtraArgs(isEnglish);
            case "/help":
                if (tokens.Length != 1) return RejectExtraArgs(isEnglish);
                return new ShaderCommand(ShaderCommandKind.Help, null, 0, HelpText(isEnglish), "slash.help");
            case "/show":
                if (tokens.Length != 1) return RejectExtraArgs(isEnglish);
                // Reply composed by the caller (needs live engine values).
                return new ShaderCommand(ShaderCommandKind.Show, null, 0, string.Empty, "slash.show");
            default:
                return Reject(Pick(isEnglish,
                    $"未知指令「{cmd}」。輸入 /help 查看可用指令。",
                    $"Unknown command \"{cmd}\". Type /help for the command list."), "slash.unknown");
        }
    }

    private static ShaderCommand ParseGrid(string[] tokens, bool isEnglish)
    {
        if (tokens.Length != 2) return RejectExtraArgs(isEnglish);
        string arg = tokens[1].ToLowerInvariant();

        if (arg == "off")
            return SetParam(ShaderParam.GridVisibility, 0.0,
                Pick(isEnglish,
                    "已隱藏格線。輸入 /grid on 可恢復。",
                    "Grid hidden. Use /grid on to bring it back."),
                "slash.grid_off");

        if (arg == "on")
            return SetParam(ShaderParam.GridVisibility, GridOnDefault,
                Pick(isEnglish,
                    "已顯示格線。",
                    "Grid shown."),
                "slash.grid_on");

        // /grid <0-100>
        if (TryParsePercent(arg, out int n))
            return SetParam(ShaderParam.GridVisibility, n / 100.0,
                Pick(isEnglish,
                    $"格線透明度已設為 {n}%。",
                    $"Grid visibility set to {n}%."),
                "slash.grid_pct");

        return Reject(Pick(isEnglish,
            "用法:/grid off、/grid on 或 /grid <0-100>。",
            "Usage: /grid off, /grid on, or /grid <0-100>."), "slash.grid_badarg");
    }

    private static ShaderCommand ParseScalar01(
        string[] tokens, ShaderParam param, string label, bool isEnglish)
    {
        if (tokens.Length != 2) return RejectExtraArgs(isEnglish);
        if (!TryParsePercent(tokens[1], out int n))
            return Reject(Pick(isEnglish,
                $"用法:/{label} <0-100>。",
                $"Usage: /{label} <0-100>."), $"slash.{label}_badarg");

        // Mask floor (locked-spec #1): refuse /mask 0..19.
        if (param == ShaderParam.MaskDepth && n < MaskDepthSafetyFloor * 100)
            return Reject(Pick(isEnglish,
                "遮罩低於 20% 會關閉核心保護,已拒絕。最低安全值為 /mask 20。",
                "Mask depth below 20% disables core protection — refusing. The lowest safe value is /mask 20."),
                "slash.mask_floor");

        // CW-2 CLOSED (D-2, Beta A2): MaskDepth and FeatherWidth are now REAL renderer
        // inputs — the single-pass field (A2) renders them live. The confirmation un-interim:
        // it claims the change applied again ("set to" / "已設為"), like /grid and /fov,
        // because it now actually does.
        return SetParam(param, n / 100.0,
            Pick(isEnglish,
                $"{ZhLabel(param)}已設為 {n}%。",
                $"{EnLabel(param)} set to {n}%."),
            $"slash.{label}_pct");
    }

    private static ShaderCommand ParseFov(string[] tokens, bool isEnglish)
    {
        if (tokens.Length != 2) return RejectExtraArgs(isEnglish);
        if (!int.TryParse(tokens[1], out int n))
            return Reject(Pick(isEnglish,
                "用法:/fov <15-100>。",
                "Usage: /fov <15-100>."), "slash.fov_badarg");

        if (n < SenseDefaults.MinComposedFovRadius || n > 100)
            return Reject(Pick(isEnglish,
                $"FOV 必須介於 {SenseDefaults.MinComposedFovRadius} 到 100。試試 /fov 60 取得較柔和的視野。",
                $"FOV must be between {SenseDefaults.MinComposedFovRadius} and 100. Try /fov 60 for a softer view."),
                "slash.fov_range");

        return SetParam(ShaderParam.FovRadius, n,
            Pick(isEnglish,
                $"視野半徑已設為 {n}%。",
                $"FOV radius set to {n}%."),
            "slash.fov_pct");
    }

    // ── Layer 2 implementation: grid on/off intent (verb + noun co-occurrence) ─

    /// <summary>Hide-the-grid verbs. zh covers the full vocabulary Kirby named in
    /// round-3 (關閉 / 關掉 / 不要 / 移除 / 拿掉 / 隱藏); en covers the explicit phrasal
    /// verbs (turn off / disable / remove / hide). A bare "off" is deliberately NOT an
    /// en hide-verb — "why is the grid off" must NOT match (locked-spec #6).</summary>
    private static readonly string[] GridHideVerbsZh =
        { "關閉", "關掉", "不要", "移除", "拿掉", "隱藏" };
    private static readonly string[] GridHideVerbsEn =
        { "turn off", "disable", "remove", "hide" };

    /// <summary>Show-the-grid verbs (the on equivalent). zh: 開啟 / 打開 / 顯示.</summary>
    private static readonly string[] GridShowVerbsZh =
        { "開啟", "打開", "顯示" };
    private static readonly string[] GridShowVerbsEn =
        { "turn on", "enable", "show", "bring back" };

    /// <summary>Grid NOUNS in either language (網格 / 格線 / grid).</summary>
    private static readonly string[] GridNounsZh = { "網格", "格線" };

    /// <summary>en negation tokens that veto a loose grid-intent match so a
    /// rhetorical / negated utterance ("I don't want the grid off") still falls
    /// through to the LLM. zh negation 不 is handled by NOT treating it as a hide-verb
    /// stem outside the explicit 不要 token (which IS a remove request).</summary>
    private static readonly string[] EnNegationGuards =
        { "don't", "do not", "doesn't", "didn't", "why is", "why's", "tell me about", "what is", "what's" };

    /// <summary>Beta-1 #14: Chinese question / rhetorical guards, symmetric to
    /// <see cref="EnNegationGuards"/>. The app's PRIMARY language is zh, but only English had a
    /// guard — so 「為什麼網格被關掉」 ("why was the grid turned off?") wrongly resolved to a HIDE
    /// command and turned the protective Chang-Hain anchor grid off. A question/rhetorical
    /// utterance about the grid is never a command; it falls through to the LLM.</summary>
    private static readonly string[] ZhQuestionGuards =
        { "為什麼", "為甚麼", "為何", "怎麼", "是不是", "難道", "嗎" };

    /// <summary>zh hide verbs OTHER than 不要 — used to detect when 不要 is NEGATING a hide verb
    /// (不要關閉網格 = "don't turn off the grid") rather than itself requesting removal
    /// (不要網格 = "(I) don't want the grid"). The former must NOT turn the grid off.</summary>
    private static readonly string[] GridHideVerbsZhExceptBuyao =
        { "關閉", "關掉", "移除", "拿掉", "隱藏" };

    /// <summary>Round-3: detect an explicit grid SHOW/HIDE request by co-occurrence of
    /// a verb with a grid noun, in EITHER order, in either language. Resolves to the
    /// SAME ShaderCommand the slash / strict-NL grid paths produce (gv=0 for hide,
    /// profile default for show) so the ChatbotViewModel reuses one absolute-set path.
    /// Returns <see cref="ShaderCommandKind.None"/> when no grid intent is present OR
    /// when an en negation/question guard vetoes a loose match.</summary>
    private static ShaderCommand DetectGridOnOff(string trimmed, bool isEnglish)
    {
        string lower = trimmed.ToLowerInvariant();

        // Must mention the grid at all (en noun "grid" lower-cased; zh nouns are
        // case-free). No grid noun → not a grid command; fall through.
        bool mentionsGrid = lower.Contains("grid", StringComparison.Ordinal)
            || ContainsAny(trimmed, GridNounsZh);
        if (!mentionsGrid) return None();

        // en negation / question guard: a negated or rhetorical grid sentence is NOT a
        // command (locked-spec #6).
        if (ContainsAny(lower, EnNegationGuards)) return None();

        // Beta-1 #14: zh question / rhetorical guard (symmetric to the en guard) — "why is the
        // grid off?" etc. is never a command.
        if (ContainsAny(trimmed, ZhQuestionGuards)) return None();

        // Beta-1 #14: 不要 is ambiguous — 不要網格 ("don't want the grid") is a remove request,
        // but 不要關閉網格 ("don't turn off the grid") NEGATES the following hide verb. When 不要
        // co-occurs with another hide/show verb it is negating it → fall through to the LLM
        // rather than (wrongly) turning the protective grid off/on.
        if (trimmed.Contains("不要", StringComparison.Ordinal)
            && (ContainsAny(trimmed, GridHideVerbsZhExceptBuyao)
                || ContainsAny(trimmed, GridShowVerbsZh)
                || ContainsAny(lower, GridHideVerbsEn)
                || ContainsAny(lower, GridShowVerbsEn)))
            return None();

        bool hide = ContainsAny(trimmed, GridHideVerbsZh) || ContainsAny(lower, GridHideVerbsEn);
        bool show = ContainsAny(trimmed, GridShowVerbsZh) || ContainsAny(lower, GridShowVerbsEn);

        // Need an unambiguous direction: a verb on exactly one side. If both a hide and
        // a show verb appear (rare / contradictory), fall through to the LLM rather than
        // guess. If neither appears, it's a grid mention without an action → LLM.
        if (hide && !show)
            return GridOffCmd(isEnglish);
        if (show && !hide)
            return GridOnCmd(isEnglish);

        return None();
    }

    private static bool ContainsAny(string text, string[] needles)
    {
        foreach (var n in needles)
            if (text.Contains(n, StringComparison.Ordinal)) return true;
        return false;
    }

    // ── Layer 3 implementation: strict-NL whole-utterance whitelist ───────────

    private static ShaderCommand ParseStrictNl(string trimmed, bool isEnglish)
    {
        // Whole-utterance normalisation: lowercase + strip trailing sentence
        // punctuation (so "hide grid." still matches) — but NOT substring.
        string norm = trimmed.ToLowerInvariant()
            .TrimEnd('.', '。', '!', '！', '?', '？', ' ', '\t');

        foreach (var (phrase, factory) in StrictPhrases)
        {
            if (string.Equals(norm, phrase, StringComparison.Ordinal))
                return factory(isEnglish);
        }
        return None();
    }

    /// <summary>Ordered whole-utterance whitelist. First match wins. Phrases are
    /// pre-lowercased (the matcher lowercases the input). zh phrases have no case so
    /// they pass through unchanged. Each maps to a ShaderCommand factory.</summary>
    private static readonly (string phrase, Func<bool, ShaderCommand> factory)[] StrictPhrases =
    {
        // grid off
        ("grid off",         GridOffCmd),
        ("hide grid",        GridOffCmd),
        ("turn off the grid", GridOffCmd),
        ("關掉格線",          GridOffCmd),
        ("隱藏格線",          GridOffCmd),
        ("格線關掉",          GridOffCmd),

        // grid on
        ("grid on",          GridOnCmd),
        ("show grid",        GridOnCmd),
        ("打開格線",          GridOnCmd),
        ("顯示格線",          GridOnCmd),

        // mask off → REJECT (suggest /mask 20)
        ("mask off",         MaskOffRejectCmd),
        ("拿掉遮罩",          MaskOffRejectCmd),
        ("關掉遮罩",          MaskOffRejectCmd),

        // reset shader / overlay
        ("reset shader",     ResetCmd),
        ("reset overlay",    ResetCmd),
        ("重設遮罩",          ResetCmd),
        ("重置畫面",          ResetCmd),

        // show settings
        ("show settings",        ShowCmd),
        ("current settings",     ShowCmd),
        ("目前設定",              ShowCmd),
        ("現在的設定",            ShowCmd),
    };

    private static ShaderCommand GridOffCmd(bool en) => SetParam(
        ShaderParam.GridVisibility, 0.0,
        Pick(en, "已隱藏格線。輸入 /grid on 可恢復。", "Grid hidden. Use /grid on to bring it back."),
        "nl.grid_off");

    private static ShaderCommand GridOnCmd(bool en) => SetParam(
        ShaderParam.GridVisibility, GridOnDefault,
        Pick(en, "已顯示格線。", "Grid shown."),
        "nl.grid_on");

    private static ShaderCommand MaskOffRejectCmd(bool en) => Reject(
        Pick(en,
            "遮罩是核心保護,無法關閉。若只想隱藏格線請用 /grid off;最低安全遮罩為 /mask 20。",
            "Mask is the core protection and can't be turned off. Use /grid off to hide the grid only; the lowest safe mask is /mask 20."),
        "nl.mask_off");

    private static ShaderCommand ResetCmd(bool en) => new(
        ShaderCommandKind.ResetShader, null, 0,
        Pick(en,
            "已將遮罩設定還原為個人預設(習慣化進度保留)。",
            "Shader reset to your profile defaults (habituation progress kept)."),
        "nl.reset_shader");

    private static ShaderCommand ShowCmd(bool en) => new(
        ShaderCommandKind.Show, null, 0, string.Empty, "nl.show");

    // ── Reply / help text helpers ─────────────────────────────────────────────

    /// <summary>The inline /help command list. Kept short per the backlog UX table.</summary>
    public static string HelpText(bool isEnglish) => Pick(isEnglish,
        "可用指令:\n" +
        "/grid off｜on｜<0-100> — 格線\n" +
        "/mask <20-100> — 遮罩深度(最低 20%)\n" +
        "/feather <0-100> — 邊緣羽化\n" +
        "/fov <15-100> — 視野半徑\n" +
        "/reset shader — 還原遮罩預設\n" +
        "/show — 顯示目前數值",
        "Commands:\n" +
        "/grid off|on|<0-100> — grid\n" +
        "/mask <20-100> — mask depth (min 20%)\n" +
        "/feather <0-100> — edge feather\n" +
        "/fov <15-100> — FOV radius\n" +
        "/reset shader — restore shader defaults\n" +
        "/show — show current values");

    /// <summary>Compose the /show + "show settings" reply from live engine values.
    /// The lexicon is pure, so the CALLER reads the engine and passes the four
    /// resolved numbers in. Format matches the backlog UX table.</summary>
    public static string ComposeShowReply(
        int fovPercent, double maskDepth, double gridVisibility, double featherWidth)
        => $"FOV: {fovPercent}% · Mask: {Pct(maskDepth)}% · " +
           $"Grid: {Pct(gridVisibility)}% · Feather: {Pct(featherWidth)}%";

    // ── Small primitives ──────────────────────────────────────────────────────

    private static int Pct(double v01) => (int)Math.Round(Math.Clamp(v01, 0.0, 1.0) * 100);

    private static bool TryParsePercent(string s, out int n)
        => int.TryParse(s, out n) && n >= 0 && n <= 100;

    private static string Pick(bool isEnglish, string zh, string en) => isEnglish ? en : zh;

    private static ShaderCommand None()
        => new(ShaderCommandKind.None, null, 0, string.Empty, "none");

    private static ShaderCommand SetParam(ShaderParam param, double value, string reply, string patternId)
        => new(ShaderCommandKind.SetParam, param, value, reply, patternId);

    private static ShaderCommand Reject(string reply, string patternId)
        => new(ShaderCommandKind.Reject, null, 0, reply, patternId);

    private static ShaderCommand RejectExtraArgs(bool isEnglish)
        => Reject(Pick(isEnglish,
            "指令格式錯誤或含多餘參數,已拒絕。輸入 /help 查看用法。",
            "Malformed command or extra arguments — refusing. Type /help for usage."),
            "slash.extra_args");

    private static string EnLabel(ShaderParam p) => p switch
    {
        ShaderParam.MaskDepth      => "Mask depth",
        ShaderParam.GridVisibility => "Grid visibility",
        ShaderParam.FeatherWidth   => "Feather width",
        ShaderParam.FovRadius      => "FOV radius",
        _                          => "Shader",
    };

    private static string ZhLabel(ShaderParam p) => p switch
    {
        ShaderParam.MaskDepth      => "遮罩深度",
        ShaderParam.GridVisibility => "格線透明度",
        ShaderParam.FeatherWidth   => "邊緣羽化",
        ShaderParam.FovRadius      => "視野半徑",
        _                          => "遮罩",
    };
}
