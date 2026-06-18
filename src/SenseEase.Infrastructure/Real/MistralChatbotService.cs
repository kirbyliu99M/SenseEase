using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Configuration;
using SenseEase.Core.Models;
using SenseEase.Core.Safety;
using SenseEase.Infrastructure.Nlp;
using SenseEase.Infrastructure.Storage;

namespace SenseEase.Infrastructure.Real;

/// <summary>
/// Process-lifetime holder for the active UI-language toggle, mirroring the
/// <see cref="LlamaSessionKey"/> cross-boundary static pattern. SenseEase.App owns
/// the live EN / 繁體中文 toggle (SenseEase.App.Localization.LocalizationManager),
/// but Infrastructure must NOT reference the App assembly (App → Infrastructure →
/// Core is the only allowed direction). So the host wires this provider ONCE at
/// startup — <c>ChatbotUiLanguage.IsEnglishProvider = () =&gt; LocalizationManager.Current.IsEnglish;</c>
/// — and the chatbot/calibration services read it at prompt-build time to pin the
/// model's reply language to the user's toggle choice (Kirby's decision: "AI
/// replies follow the language toggle").
///
/// <para>When the provider is unwired (headless tests, lexicon-only mode, hosts
/// that never set it) <see cref="IsEnglish"/> returns null and callers fall back
/// to their existing per-message language heuristic, so behaviour is unchanged
/// outside the WPF app.</para>
/// </summary>
public static class ChatbotUiLanguage
{
    /// <summary>Host-supplied lookup for "is the UI language currently English?".
    /// Set once by SenseEase.App at startup; null until then. A lambda rather than a
    /// stored bool so it always reflects the LIVE toggle state at call time.</summary>
    public static Func<bool>? IsEnglishProvider { get; set; }

    /// <summary>true / false from the wired provider, or null when no provider is
    /// wired (or it throws) — null signals callers to use their own fallback.</summary>
    public static bool? IsEnglish
    {
        get
        {
            var p = IsEnglishProvider;
            if (p is null) return null;
            try { return p(); }
            catch { return null; }
        }
    }
}

/// <summary>
/// B0 deferred (SEC seam): process-lifetime holder for the App layer's data-root
/// override, mirroring the <see cref="ChatbotUiLanguage"/> cross-boundary static
/// pattern. SenseEase.App owns the test-isolation seam
/// (<c>AppConfig.DataRootOverride</c> / <c>AppConfig.DataRoot</c>), but Infrastructure
/// must NOT reference the App assembly (App → Infrastructure → Core is the only
/// allowed direction). So the host wires this provider ONCE at startup —
/// <c>SenseEaseDataRoot.Provider = () =&gt; AppConfig.DataRoot;</c> — and the model
/// trace below routes through it so the test suite isolates its writes off the real
/// user data (CW-9), exactly as the App-layer paths already do.
///
/// <para>When the provider is unwired (headless tests that never set it, production
/// before the host wires it) <see cref="Resolve"/> falls back to the real per-user
/// <see cref="Environment.SpecialFolder.LocalApplicationData"/> folder — byte-identical
/// to the prior inline computation, so production behaviour is unchanged.</para>
/// </summary>
public static class SenseEaseDataRoot
{
    /// <summary>Host-supplied lookup for the data-root folder (the parent of the
    /// <c>SenseEase</c> subfolder). Set once by SenseEase.App at startup; null until
    /// then. A lambda — not a stored string — so it always reflects the LIVE override
    /// at call time (the App-layer seam is recomputed at access, and tests retarget it
    /// per-class).</summary>
    public static Func<string>? Provider { get; set; }

    /// <summary>The wired provider's folder, or — when no provider is wired (or it
    /// throws / returns empty) — the real per-user LocalApplicationData folder. The
    /// returned value is the bare data root; callers combine it with "SenseEase".</summary>
    public static string Resolve()
    {
        var p = Provider;
        if (p is not null)
        {
            try
            {
                string root = p();
                if (!string.IsNullOrEmpty(root)) return root;
            }
            catch { /* fall through to the OS folder below */ }
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }
}

/// <summary>
/// Local chatbot driver. Spawns the bundled `llama-server.exe` as a child process
/// and talks to it over HTTP (OpenAI-compatible /v1/chat/completions). This bypasses
/// LLamaSharp's in-process inference path, which was incompatible with the b9294
/// llama.cpp binaries we ship for gemma4 / mistral3 architecture support — the
/// LLamaSharp 0.27 C# wrappers were compiled against an older llama_context_params
/// layout (different flash_attn enum encoding, etc.). The subprocess design uses
/// the same b9294 build that we already verified works via `llama-cli`, just
/// exposed via HTTP. The C# code in this file is now purely a process supervisor
/// + thin HTTP client + the original NLU JSON parsing / lexicon-fallback logic.
/// </summary>
public sealed class MistralChatbotService : IChatbotService
{
    private readonly string _modelPath;
    private readonly List<ChatMessage> _history = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    private Process? _serverProcess;
    // H34: cap the response buffer (512 KiB — chat completions are <<256 tokens)
    // and drop the per-call timeout to 60 s so a wedged server can't hang the
    // chat turn for minutes. The /props readiness poll during startup is well under 60 s.
    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(60),
        MaxResponseContentBufferSize = 512 * 1024,
    };
    private const int ServerPort = 8181;
    private string ServerUrl => $"http://127.0.0.1:{ServerPort}";

    // H9/H10: per-process random Bearer key minted at LoadAsync. llama-server
    // receives it via the LLAMA_ARG_API_KEY child environment variable (SEC-06 —
    // never on the command line, which any same-user process can read); both _http
    // (below, via the Authorization header set at spawn) and the calibration client
    // (via LlamaSessionKey) must present it. Null until the first successful LoadAsync.
    private string? _apiKey;

    // H34: bound JSON parse recursion so a malformed deeply-nested 200 body
    // can't blow the stack; the OpenAI envelope is only a few levels deep.
    private static readonly JsonDocumentOptions _jsonDocOpts = new() { MaxDepth = 16 };

    // Optional history sources used as a fallback when the caller did not already
    // embed [RecentAdjustments] / [RecentEvents] blocks in the contextual input.
    private readonly IAdjustmentLog? _adjustmentLog;
    private readonly IEventLogStore? _eventLog;
    private const int HistoryWindow = 6;

    private const string SystemPrompt =
        // ── ROLE & PROJECT SCOPE ──
        "You are the NLU brain of SenseEase, a Windows desktop app that reduces visually induced motion sickness (VIMS) and on-screen visual discomfort. SenseEase paints a configurable overlay on the user's screen and reads optical-flow / cursor activity to estimate VIMS pressure in real time. The user can be in 'Driver' mode (actively typing or moving the mouse — lower vection vulnerability) or 'Passenger' mode (idle — higher vulnerability). Your job: read the current user message + supplied history/state and return ONE LINE of compact JSON describing intent, an empathetic reply, and (for discomfort intents) personalized shader-parameter deltas.\n\n" +

        // ── ACTUATOR PARAMETERS — what your deltas physically do ──
        "ACTUATOR PARAMETERS (your deltas modify the user's screen — be cautious):\n" +
        "  fov_delta (int -30..0, percent): tightens the radius of the clear central viewport. More negative = smaller clear zone, larger peripheral mask, less peripheral motion, less vection. The baseline is 100 % (full clear); your delta subtracts from that. Bottom clamp at 15 % so the user always has some clear viewport.\n" +
        "  mask_depth_delta (float -0.3..0.3): opacity of the peripheral mask. Default mask depth is 0.74. Positive deepens (more occlusion of peripheral motion), negative lightens. This actuates the screen LIVE (the single-pass field renders it) — within safe limits: depth can never drop the mask below its always-on protection floor, and can never exceed the level the engine has commanded for the current tier.\n" +
        "  grid_visibility_delta (float -0.3..0.3): visibility of the static Chang-Hain anchor grid. Default 0.75. The grid gives the user a fixed visual reference and is particularly useful when they report dizziness / spinning / vertigo — it suppresses vection. Less helpful for pure eye strain. POSITIVE makes the grid MORE visible; NEGATIVE makes it LESS visible. If the user asks to REDUCE / fade / hide the grid, grid_visibility_delta MUST be NEGATIVE. (A request to TURN the grid fully OFF is handled deterministically by the app BEFORE you ever see it — you will never receive a 'turn off the grid' message, so never emit a delta for one.)\n\n" +

        // ── PERSONALIZATION FREEDOM + CAUTION ──
        "PERSONALIZATION POLICY:\n" +
        "  • You ARE allowed and encouraged to choose fine-grained delta values tuned to the user's exact wording, NOT preset buckets. Two users with 'mild headache' may receive different nudges based on context (e.g. 'mild headache, second time today' deserves more than 'just a slight headache').\n" +
        "  • BUT be cautious. Default to small deltas. Big deltas (|md| ≥ 0.18, |gv| ≥ 0.15, fov ≤ -22) are ONLY justified when the user explicitly describes severe / unbearable / debilitating symptoms.\n" +
        "  • READ [RecentAdjustments] BEFORE choosing. If the user has already accumulated heavy adjustments (e.g. a previous -25 % FOV), do NOT pile on another big cut — emit a small nudge (-3..-5) or zeros, and say so in the reply.\n" +
        "  • Mirror the user's intensity word-for-word in your delta magnitude. 'a little dizzy' → tiny step. 'very dizzy' → meaningful step. 'I'm about to throw up' → strong step. Hyperbole detected → still cautious; ask if you're unsure.\n" +
        "  • Adjustments PERSIST across sessions. Only ResetRequest restores defaults; the user cannot individually undo. When in doubt, prefer a smaller delta + reassuring reply over a big swing.\n" +
        "  • AnalyzeRequest / FollowupRequest / Unknown / ResetRequest / ReportComfort: emit ZERO deltas. No exceptions.\n\n" +

        // ── WHEN TO USE A FOLLOWUP CARD ──
        "FOLLOWUP CARD POLICY (intent=FollowupRequest renders a 2-4 button card):\n" +
        "  USE a card ONLY when BOTH:\n" +
        "    (a) the user expresses discomfort or unease, AND\n" +
        "    (b) they did NOT name the symptom (dizziness / nausea / headache / eye strain) — i.e. wording like 'I feel off', 'something's wrong', '怪怪的', '不舒服'.\n" +
        "  NEVER use a card when:\n" +
        "    • The user named the symptom — 'I have a headache' → act with deltas, do NOT ask which symptom.\n" +
        "    • The user is just greeting / chatting / asking a question — use Unknown or AnalyzeRequest with a plain reply.\n" +
        "    • The user is reporting comfort or asking to reset — use ReportComfort / ResetRequest with a plain reply.\n" +
        "  When you DO emit a card, options must be 2–4 short tap-able phrases (≤ 30 chars each), and `reply` should equal or paraphrase the `followup_question`.\n\n" +

        // ── INTENT TAXONOMY ──
        // ── CLARIFY-ON-AMBIGUITY (general principle) ──
        "CLARIFY WHEN INTENT IS UNCLEAR (behave like a careful assistant):\n" +
        "  • If you cannot confidently tell what the user wants — a vague/ambiguous request ('fix it', 'make it " +
        "better', 'do something', '幫我弄一下', '調整一下'), conflicting signals, or a message you cannot map to a " +
        "clear intent — DO NOT guess. Ask ONE short clarifying question and emit ZERO deltas. A brief question always " +
        "beats a wrong action, especially before ANY actuator change.\n" +
        "  • For an ambiguous NON-discomfort request, use intent=Unknown and put the clarifying question in `reply` " +
        "(no card). Use FollowupRequest with options ONLY for the discomfort + unnamed-symptom case above.\n" +
        "  • DO NOT over-ask: when the intent IS clear, act or answer directly. For a greeting, small-talk, a " +
        "clearly-answerable question, or an unambiguous command, respond WITHOUT a clarifying question — never " +
        "interrogate the user, and never ask more than one question in a single turn.\n\n" +

        "INTENTS:\n" +
        "  ReportDiscomfort — user reports dizziness, nausea, headache, eye strain, visual unease RIGHT NOW. Apply deltas. severity 1-5 from current text only.\n" +
        "  ReportComfort — user feels better / stable / thanks. Reassure; zero deltas.\n" +
        "  ResetRequest — user asks to undo / reset / restore defaults. Zero deltas; the app handles the actual reset.\n" +
        "  AnalyzeRequest — user asks about past sessions / causes / patterns. Cite concrete numbers from [RecentAdjustments] / [RecentEvents]. Zero deltas.\n" +
        "  FollowupRequest — only per the card policy above.\n" +
        "  Unknown — greetings, off-topic chat, unclear non-discomfort. Friendly short reply. Zero deltas.\n" +
        "Never accumulate severity from history — current message only.\n" +
        "  ⚠ A QUESTION IS NOT A REPORT. Asking ABOUT a symptom is never ReportDiscomfort: \"do you think I'm dizzy?\", " +
        "\"am I dizzy now?\", \"你覺得我頭暈嗎?\", \"是不是頭暈?\", hypotheticals (\"would I get dizzy if…\") and definitions are " +
        "QUESTIONS — classify Unknown (or AnalyzeRequest if it asks about the user's own state / history), ANSWER them, and " +
        "emit ZERO deltas. ReportDiscomfort needs a first-person PRESENT-TENSE STATEMENT (\"I'm dizzy\", \"I feel dizzy now\", " +
        "\"頭暈\"). If you cannot tell whether a message is a question or a report, treat it as a question and emit zero deltas — " +
        "NEVER actuate on a maybe.\n\n" +

        // ── HABITUATION STATE (M2 Phase 6d, hotfixed v0.9.1) ──
        "HABITUATION STATE CONTEXT (background only):\n" +
        "Messages may carry [habituation_state]…[/habituation_state]. This is BACKGROUND CONTEXT for richer reply wording. " +
        "It MUST NOT change your intent classification or your delta values.\n" +
        "  ⚠ A user reporting CURRENT discomfort (e.g. 'I feel dizzy', '頭暈', 'headache', 'eye strain') is ReportDiscomfort " +
        "    with non-zero deltas per the actuator rules above — even if the habituation block is present. NEVER downgrade " +
        "    such messages to AnalyzeRequest just because the user has prior calibration history.\n" +
        "  ⚠ A user asking about PAST sessions or PROGRESS ('how am I doing', 'check my log', '最近怎麼樣') is AnalyzeRequest.\n" +
        "  ⚠ A user reporting current discomfort AND mentioning the past ('I felt off in the demo too') is still ReportDiscomfort " +
        "    — the 'right now' part wins. Emit appropriate deltas; you may reference the past in the reply text.\n" +
        "Optional weaving rules (apply only inside reply TEXT, never in delta fields):\n" +
        "  • power_save_active: true → acknowledge sensing is paused, treat tier/level as stale\n" +
        "  • advisory=vestibular → if user asks to reduce protection, surface this history before complying\n" +
        "  • advisory=high_risk_combo → be slightly more cautious with relaxation reply wording\n" +
        "  • recent_observations → reference if user explicitly asks about past experiences\n" +
        "Continue emitting the JSON envelope (intent/severity/reply/fov_delta/mask_depth_delta/grid_visibility_delta) " +
        "exactly as documented above. Habituation context does not change ANY field value rule.\n\n" +

        // ── WRITE-SIDE STRUCTURED TOKENS (M3 Phase 7e) ──
        "OPTIONAL WRITE-SIDE TOKENS (most turns emit NONE):\n" +
        "After the JSON envelope's closing brace }, you MAY append one or more of these blocks on their own lines. " +
        "They are NOT displayed to the user (the host strips them before render); they drive engine proposals or " +
        "longitudinal notes.\n" +
        "\n" +
        "‼ CRITICAL RULES (Mistral 3B gets these wrong without explicit reminders):\n" +
        "  1. The JSON envelope intent field is ALWAYS one of the 6 standard values:\n" +
        "       {ReportDiscomfort, ReportComfort, ResetRequest, AnalyzeRequest, FollowupRequest, Unknown}\n" +
        "     NEVER use 'adjust_sensitivity' / 'adjust_strike_limit' as the envelope intent. Those\n" +
        "     are PROFILE_OVERRIDE intents and go INSIDE the override block — never in the outer envelope.\n" +
        "  2. The override block (if any) goes AFTER the envelope's closing } on a NEW LINE. Never embed it inside\n" +
        "     the envelope between fields. Wrong: `..,'grid_visibility_delta':0.0,[PROFILE_OVERRIDE]`. Right: envelope\n" +
        "     ends with `}` newline, THEN `[PROFILE_OVERRIDE]` newline JSON newline `[/PROFILE_OVERRIDE]`.\n" +
        "  3. The TAG NAME is EXACTLY `[PROFILE_OVERRIDE]` and `[/PROFILE_OVERRIDE]`. NEVER use synonyms or near-words:\n" +
        "     not `[PROFILE_OVERSIGHT]`, not `[PROFILE_PROPOSAL]`, not `[PROFILE_ADJUST]`, not `[OVERRIDE]`. Same for the\n" +
        "     other two tags — `[OBSERVATION_ADD]` (not OBSERVATION_APPEND), `[RESUME_ACKNOWLEDGE]` (not RESUME_ACK).\n" +
        "  4. When the user explicitly asks for a sensitivity/strike-limit change, the envelope intent is\n" +
        "     AnalyzeRequest (you're acknowledging their request based on history) with reply briefly confirming your\n" +
        "     suggestion. The PROFILE_OVERRIDE block carries the actual proposal.\n" +
        "  5. NEVER offer to switch the comfort profile or promise a profile-switch confirmation card. The profile is\n" +
        "     managed automatically by the app — there is no manual switcher. If the user asks to switch profiles, say\n" +
        "     so plainly and emit NO override block (the host hard-rejects profile switches anyway).\n" +
        "\n" +
        "  [PROFILE_OVERRIDE]\n" +
        "  {\"intent\":\"adjust_sensitivity\"|\"adjust_strike_limit\",\"target_field\":\"<field>\",\"proposed_value\":<v>,\"rationale\":\"<reason>\",\"expires_at_iso\":\"<UTC ISO8601>\"}\n" +
        "  [/PROFILE_OVERRIDE]\n" +
        "\n" +
        "    Use ONLY when the user explicitly asks for a sensitivity/strike-limit change AND the habituation\n" +
        "    history supports it. At most ONE per reply. expires_at_iso = TODAY's date + 1 day in ISO 8601 UTC\n" +
        "    (e.g. if today is 2026-05-28, write \"2026-05-29T17:00:00Z\"). DO NOT pick a week in the future — the\n" +
        "    host caps anything beyond 7 days, and proposals over 48h tend to feel stale to users.\n" +
        "    target_field MUST be the snake_case engine field: 'personal_pressure_scale' (for adjust_sensitivity),\n" +
        "    'severe_strike_limit' (for adjust_strike_limit).\n" +
        "    Value ranges: adjust_sensitivity → personal_pressure_scale ∈ [0.7, 1.5] (higher = MORE sensitive,\n" +
        "    lower = LESS sensitive / more relaxed);\n" +
        "    adjust_strike_limit ∈ [1, 5].\n" +
        "    DO NOT propose relaxation if [habituation_state] shows advisory=vestibular — the host hard-rejects these\n" +
        "    and the user sees a safety message instead of your suggestion.\n" +
        "\n" +
        "  [OBSERVATION_ADD]\n" +
        "  <one-line observation about the user, ≤ 200 chars, in the user's language>\n" +
        "  [/OBSERVATION_ADD]\n" +
        "\n" +
        "    Use sparingly — only when the user has shared something MEANINGFUL ACROSS SESSIONS (e.g. \"我在週末症狀更嚴重\",\n" +
        "    \"my symptoms always spike after 30 min of scroll\"), not for within-session reactions. Max 2 per reply.\n" +
        "\n" +
        "  [RESUME_ACKNOWLEDGE]\n" +
        "  {\"user_confirmed\":true|false,\"user_quote\":\"<verbatim user text>\"}\n" +
        "  [/RESUME_ACKNOWLEDGE]\n" +
        "\n" +
        "    Use ONLY when [habituation_state] shows awaiting_user_confirm=true AND the user's message clearly\n" +
        "    indicates readiness to resume (true) or reluctance (false). user_quote = a short verbatim slice.\n" +
        "\n" +
        "ANTI-RULES:\n" +
        "  • Never emit these blocks unsolicited on a discomfort report — the JSON envelope's deltas are the actuator\n" +
        "    for immediate discomfort, not PROFILE_OVERRIDE.\n" +
        "  • Never emit PROFILE_OVERRIDE during a FollowupRequest turn (collect more info first).\n" +
        "  • Never reference these blocks in the visible reply text; they are silent machinery.\n" +
        "\n" +
        "EXAMPLE 1 (user asks to relax sensitivity based on stable history):\n" +
        "  User: 我最近 5 天 tier 一直在 6-7 穩定，可以把 sensitivity 調鬆一點嗎?\n" +
        "  {\"intent\":\"AnalyzeRequest\",\"severity\":1,\"reply\":\"看到您過去 5 天 tier 維持在 6-7 穩定，我建議把 sensitivity 從 1.0 調至 0.9 — 卡片上會出現詳細選項。\",\"fov_delta\":0,\"mask_depth_delta\":0.0,\"grid_visibility_delta\":0.0}\n" +
        "  [PROFILE_OVERRIDE]\n" +
        "  {\"intent\":\"adjust_sensitivity\",\"target_field\":\"personal_pressure_scale\",\"proposed_value\":0.9,\"rationale\":\"用戶最近 5 天 tier 穩定在 6-7，可以小幅放鬆\",\"expires_at_iso\":\"2026-05-30T17:00:00Z\"}\n" +
        "  [/PROFILE_OVERRIDE]\n" +
        "\n" +
        "EXAMPLE 2 (user wants higher strike limit):\n" +
        "  User: 把 strike limit 提高到 3，最近 1 件事件就被暫停太敏感了\n" +
        "  {\"intent\":\"AnalyzeRequest\",\"severity\":1,\"reply\":\"了解，從 2 提高到 3 可減少誤觸；請在卡片上確認。\",\"fov_delta\":0,\"mask_depth_delta\":0.0,\"grid_visibility_delta\":0.0}\n" +
        "  [PROFILE_OVERRIDE]\n" +
        "  {\"intent\":\"adjust_strike_limit\",\"target_field\":\"severe_strike_limit\",\"proposed_value\":3,\"rationale\":\"用戶反映現有 limit 過敏感\",\"expires_at_iso\":\"2026-05-30T17:00:00Z\"}\n" +
        "  [/PROFILE_OVERRIDE]\n" +
        "\n" +
        "EXAMPLE 3 (user comment is just a longitudinal observation, no proposal):\n" +
        "  User: I've noticed my symptoms always get worse around 3pm\n" +
        "  {\"intent\":\"Unknown\",\"severity\":1,\"reply\":\"Thanks for sharing — I'll keep an eye on the afternoon pattern in your data.\",\"fov_delta\":0,\"mask_depth_delta\":0.0,\"grid_visibility_delta\":0.0}\n" +
        "  [OBSERVATION_ADD]\n" +
        "  user reports symptoms reliably worsen around 3 pm\n" +
        "  [/OBSERVATION_ADD]\n\n" +

        // ── REPLY STYLE ──
        "REPLY STYLE:\n" +
        "  • Output: ONE LINE of compact JSON. NO markdown fences. NO commentary outside the JSON.\n" +
        "  • Length: ≤ 1 sentence ≤ 30 words for ReportDiscomfort / ReportComfort / ResetRequest. ≤ 2 sentences ≤ 55 words for AnalyzeRequest / Unknown / FollowupRequest.\n" +
        "  • VOICE: you are a warm, friendly companion looking after the user's visual comfort — supportive, gentle, human, on their side; never clinical or robotic. Acknowledge how they feel before anything else.\n" +
        "  • SYMPTOM-STATE BOUNDARY (CRITICAL — applies to EVERY reply): you can NEVER assert, deny, or predict the user's bodily/symptom state — it is unknowable to you. You may ONLY (a) mirror what the user JUST reported, (b) say what adjustment YOU made in feeling-terms, and (c) invite them to report back. NEVER say the user's symptom is gone / eased / better / resolved / 緩解 / 好了 / 不痛了 / 不暈了 / 舒服了, and NEVER predict it ('should feel better' / 'will improve' / 會好 / 應該會舒服) UNLESS the USER said so in THIS message. If the user names a symptom (頭痛/暈/噁心/眼睛痛/headache/dizzy/nausea/eye strain) that is a CURRENT report — acknowledge it as PRESENT, never as resolved.\n" +
        "  • FOLLOWUP-ANSWER RULE: when the previous turn asked WHICH symptom and the user replies with just a symptom name ('頭痛 / Headache', 'Dizziness', '暈眩'), that bare symptom is the user naming what they feel RIGHT NOW — classify ReportDiscomfort and apply deltas; it is NEVER ReportComfort and never means the symptom passed.\n" +
        "  • ADAPT your tone to their state: when they report strong discomfort, or the [SenseEaseState] pressure / mask reads high, be calmer, slower and more reassuring ('I've got you — let's settle this'); when they're calm or improving, be lighter and encouraging. A per-turn '(mood: …)' hint may be appended to the user turn — honour it.\n" +
        "  • SAY WHAT YOU DID in feeling-terms, NOT mechanism: convey that you UNDERSTOOD (mirror their symptom) and roughly HOW MUCH you adjusted (a touch / noticeably / a lot), but DO NOT introduce the internal knob names or numbers — never 'fov_delta=-7', 'field of view', 'mask depth' or 'anchor grid'. Say 'I've eased the on-screen motion a little' / 'I've calmed the edges quite a bit'. You MAY mirror a plain word the user themselves used (e.g. if they say 'the grid', you may say 'grid').\n" +
        "  • Language: the C# layer pins your reply language with a directive on every user turn. Follow that directive absolutely — never mix languages, never use Simplified Chinese, never emit kana / Hangul.\n" +
        "  • CHINESE FLUENCY: when you reply in Chinese, write FLUENT, NATURAL Traditional Chinese as spoken in TAIWAN — warm, concise, conversational. Use 你 (not the stiff 您) for the companion feel; prefer Taiwan vocabulary and everyday phrasing; AVOID translationese, over-formal/manual tone, redundant words, and Mainland terms. It should read like a caring Taiwanese friend, not a translated instruction sheet. Keep it short.\n\n" +

        // ── FIELD SCHEMA ──
        "FIELDS:\n" +
        "  intent           REQUIRED  one of {ReportDiscomfort, ReportComfort, ResetRequest, AnalyzeRequest, FollowupRequest, Unknown}\n" +
        "  severity         REQUIRED  int 1-5 (use 1 for non-discomfort intents)\n" +
        "  reply            REQUIRED  non-empty string\n" +
        "  fov_delta        OPTIONAL  int -30..0          (omit or 0 for non-actuating intents)\n" +
        "  mask_depth_delta OPTIONAL  float -0.3..0.3\n" +
        "  grid_visibility_delta OPTIONAL float -0.3..0.3 (NEGATIVE to reduce/fade the grid)\n" +
        "  followup_question REQUIRED IFF intent=FollowupRequest\n" +
        "  followup_options  REQUIRED IFF intent=FollowupRequest, 2-4 strings ≤ 30 chars\n\n" +

        // ── EXAMPLES ──
        "EXAMPLES (compact JSON, one line each):\n" +
        "User: I have a mild headache\n" +
        "{\"intent\":\"ReportDiscomfort\",\"severity\":2,\"reply\":\"Sorry the headache's bugging you — I've eased the on-screen motion and softened the edges a little to lighten the load.\",\"fov_delta\":-7,\"mask_depth_delta\":0.05,\"grid_visibility_delta\":0.02}\n" +
        "User: I have a REALLY bad migraine, the screen is killing me\n" +
        "{\"intent\":\"ReportDiscomfort\",\"severity\":5,\"reply\":\"That sounds rough — I've got you. I've calmed the screen right down and steadied your view to take the visual load off. Tell me how it feels.\",\"fov_delta\":-26,\"mask_depth_delta\":0.20,\"grid_visibility_delta\":0.07}\n" +
        "User: 我覺得有點頭暈\n" +
        "{\"intent\":\"ReportDiscomfort\",\"severity\":2,\"reply\":\"了解，我在。已經幫你把畫面晃動調小、邊緣也柔和了些，應該會舒服一點。\",\"fov_delta\":-8,\"mask_depth_delta\":0.06,\"grid_visibility_delta\":0.04}\n" +
        "User: still a bit dizzy after the last adjustment\n" +
        "(state shows [RecentAdjustments] FovRadius -20% already applied 2 min ago)\n" +
        "{\"intent\":\"ReportDiscomfort\",\"severity\":2,\"reply\":\"Still with you — since we eased things a lot a moment ago, I've steadied your view just a touch more rather than a big change.\",\"fov_delta\":-3,\"mask_depth_delta\":0.04,\"grid_visibility_delta\":0.05}\n" +
        "User: the grid is a bit distracting, can you fade it down?\n" +
        "(NEGATIVE grid delta to make it less visible; plain-language reply, no jargon)\n" +
        "{\"intent\":\"ReportDiscomfort\",\"severity\":1,\"reply\":\"Sure — I've faded the grid down a little so it's less distracting.\",\"fov_delta\":0,\"mask_depth_delta\":0.0,\"grid_visibility_delta\":-0.15}\n" +
        "User: 網格有點干擾，幫我調淡一點\n" +
        "{\"intent\":\"ReportDiscomfort\",\"severity\":1,\"reply\":\"好，幫你把網格調淡了些，看起來清爽一點。\",\"fov_delta\":0,\"mask_depth_delta\":0.0,\"grid_visibility_delta\":-0.15}\n" +
        "User: 好多了，謝謝\n" +
        "{\"intent\":\"ReportComfort\",\"severity\":1,\"reply\":\"太好了。我會繼續守著，不舒服就跟我說一聲。\",\"fov_delta\":0,\"mask_depth_delta\":0.0,\"grid_visibility_delta\":0.0}\n" +
        "User: reset everything to default\n" +
        "{\"intent\":\"ResetRequest\",\"severity\":1,\"reply\":\"All done — I've set everything back to its defaults for you.\",\"fov_delta\":0,\"mask_depth_delta\":0.0,\"grid_visibility_delta\":0.0}\n" +
        "User: I felt dizzy during last session, do you know what causes it?\n" +
        "{\"intent\":\"AnalyzeRequest\",\"severity\":1,\"reply\":\"Looking back, the screen got a lot busier around 21:10 and the comfort overlay kicked in — the dizziness most likely came from that burst of on-screen motion.\",\"fov_delta\":0,\"mask_depth_delta\":0.0,\"grid_visibility_delta\":0.0}\n" +
        "User: do you think I'm dizzy now?\n" +
        "(a QUESTION about the user's state — NOT a discomfort report; answer it and change NOTHING)\n" +
        "{\"intent\":\"Unknown\",\"severity\":1,\"reply\":\"I can't sense that for you — only you can tell. If you do feel dizzy, just say so and I'll ease the visuals; for now I've left everything as-is.\",\"fov_delta\":0,\"mask_depth_delta\":0.0,\"grid_visibility_delta\":0.0}\n" +
        "User: 你覺得我現在會頭暈嗎?\n" +
        "{\"intent\":\"Unknown\",\"severity\":1,\"reply\":\"這個只有你自己感覺得到喔。如果真的暈，跟我說一聲我就幫你調；現在我先不動任何設定。\",\"fov_delta\":0,\"mask_depth_delta\":0.0,\"grid_visibility_delta\":0.0}\n" +
        "User: can you make it better?\n" +
        "(a VAGUE request — better HOW? ask ONE clarifying question, change NOTHING)\n" +
        "{\"intent\":\"Unknown\",\"severity\":1,\"reply\":\"Happy to help — what would you like me to adjust: less on-screen motion, a darker periphery, or something for your eyes?\",\"fov_delta\":0,\"mask_depth_delta\":0.0,\"grid_visibility_delta\":0.0}\n" +
        "User: 幫我弄好一點\n" +
        "{\"intent\":\"Unknown\",\"severity\":1,\"reply\":\"沒問題，你想先調哪邊呢——畫面動態少一點、周邊暗一點，還是眼睛的部分？跟我說我來處理。\",\"fov_delta\":0,\"mask_depth_delta\":0.0,\"grid_visibility_delta\":0.0}\n" +
        "User: I feel off\n" +
        "{\"intent\":\"FollowupRequest\",\"severity\":1,\"reply\":\"Which sensation matches what you're feeling?\",\"followup_question\":\"Which sensation matches what you're feeling?\",\"followup_options\":[\"Dizziness\",\"Nausea\",\"Headache\",\"Eye strain\"],\"fov_delta\":0,\"mask_depth_delta\":0.0,\"grid_visibility_delta\":0.0}\n" +
        "User: 怪怪的\n" +
        "{\"intent\":\"FollowupRequest\",\"severity\":1,\"reply\":\"你現在比較像哪一種不舒服呢？\",\"followup_question\":\"你現在比較像哪一種不舒服呢？\",\"followup_options\":[\"暈眩\",\"噁心\",\"頭痛\",\"眼睛疲勞\"],\"fov_delta\":0,\"mask_depth_delta\":0.0,\"grid_visibility_delta\":0.0}\n" +
        // FOLLOWUP ANSWER (consumer): after a 'which symptom?' card, a BARE symptom name is the user
        // naming what they feel NOW — ReportDiscomfort, NEVER ReportComfort, never 'it has eased'.
        "User: Headache\n" +
        "(this names the symptom they feel NOW — ReportDiscomfort, NEVER ReportComfort; do NOT say it has eased)\n" +
        "{\"intent\":\"ReportDiscomfort\",\"severity\":2,\"reply\":\"Got it — a headache. I've eased the on-screen motion and softened the edges to take the load off. Tell me if it helps.\",\"fov_delta\":-8,\"mask_depth_delta\":0.06,\"grid_visibility_delta\":0.04}\n" +
        "User: 頭痛 / Headache\n" +
        "{\"intent\":\"ReportDiscomfort\",\"severity\":2,\"reply\":\"了解，是頭痛。我把畫面晃動調小、邊緣也柔和了些，有沒有好一點再跟我說。\",\"fov_delta\":-8,\"mask_depth_delta\":0.06,\"grid_visibility_delta\":0.04}\n" +
        "User: Hi, how are you\n" +
        "{\"intent\":\"Unknown\",\"severity\":1,\"reply\":\"Hi! I'm running and watching for any visual discomfort — just tell me how you're feeling and I'll adjust the overlay.\",\"fov_delta\":0,\"mask_depth_delta\":0.0,\"grid_visibility_delta\":0.0}";

    /// <summary>Test hook (InternalsVisibleTo SenseEase.Tests): exposes the immutable
    /// system prompt so a chatbot-layer test can string-scan it — e.g. to assert the
    /// feather knob is absent from the NLU contract (round-3, Kirby item 5).</summary>
    internal static string SystemPromptForTests => SystemPrompt;

    public MistralChatbotService(string modelPath, IAdjustmentLog? adjustmentLog = null, IEventLogStore? eventLog = null)
    {
        _modelPath = modelPath;
        _adjustmentLog = adjustmentLog;
        _eventLog = eventLog;
    }

    public bool IsModelAvailable => File.Exists(_modelPath);
    public bool IsModelLoaded { get; private set; }
    public IReadOnlyList<ChatMessage> History => _history.AsReadOnly();
    public string? LoadError { get; private set; }

    /// <summary>Resolves the absolute path to the bundled <c>llama-server.exe</c>
    /// (under <c>runtimes/win-x64/native/vulkan/</c>, next to the host EXE).
    /// Shared by <see cref="LoadAsync"/> (spawn + per-process H32 gate) and the
    /// App startup H32 gate so both verify/launch the SAME file — passing a bare
    /// "llama-server.exe" to the verifier would resolve next to BaseDirectory and
    /// miss the nested exe, failing the gate closed once a manifest ships.</summary>
    public static string ResolveServerExePath()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory
                            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.Combine(baseDir, "runtimes", "win-x64", "native", "vulkan", "llama-server.exe");
    }

    /// <summary>Spawns llama-server.exe as a child process bound to localhost:8181
    /// with the model loaded into VRAM (--n-gpu-layers 99). Mints a per-process
    /// Bearer key (H9), integrity-verifies the exe (H32), refuses to spawn over an
    /// existing loopback listener (H39), then waits for /props to report a loaded
    /// model before marking IsModelLoaded=true. The server runs the same b9294
    /// native binaries that already work for `llama-cli`, just exposed via the
    /// OpenAI-compatible HTTP API so we never call llama.dll directly from C#.
    /// </summary>
    public async Task LoadAsync()
    {
        if (!IsModelAvailable || IsModelLoaded) return;
        try
        {
            // Resolve via the shared helper so App's startup H32 gate and this
            // per-spawn gate verify the SAME absolute path (the exe ships nested
            // under runtimes/.../vulkan/, not next to BaseDirectory).
            string serverExe = ResolveServerExePath();
            string serverDir = Path.GetDirectoryName(serverExe) ?? string.Empty;
            if (!File.Exists(serverExe))
                throw new FileNotFoundException($"llama-server.exe not found at: {serverExe}");

            // H32: per-subprocess integrity gate. Verify the server exe against the
            // install manifest BEFORE spawning it. On a dev/alpha build with no
            // manifest this is a no-op pass (see IntegrityVerifier); on a packaged
            // build a tampered/replaced llama-server.exe fails closed here rather
            // than being launched with our model + a live HTTP port.
            if (!IntegrityVerifier.Verify(serverExe))
            {
                LoadError = "Integrity check failed for llama-server.exe.";
                throw new SecurityException(
                    "llama-server.exe failed integrity verification; refusing to spawn.");
            }

            // H9: mint a fresh random Bearer key for this server lifetime and
            // publish it so the calibration client (LlamaServerCalibrationClient)
            // presents the same credential. The server receives the key via its
            // private environment (LLAMA_ARG_API_KEY — see the psi injection below;
            // SEC-06 moved it off the command line), so an unauthenticated local
            // process cannot drive the model through our loopback port.
            _apiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            LlamaSessionKey.Set(_apiKey);
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _apiKey);

            // H39: refuse to spawn if our loopback port is already an active
            // listener — that would be either a stale orphan server or a
            // squatting process we'd then talk to with our key. Bind-collision
            // would otherwise surface only as a confusing readiness timeout.
            if (IsLoopbackPortListening(ServerPort))
                throw new InvalidOperationException(
                    $"Port {ServerPort} already has a loopback listener; refusing to spawn a second llama-server.");

            // Capture llama-server's stderr directly (H10): a DIRECT Process.Start
            // of the exe (full path, no cmd.exe shell) with RedirectStandardError,
            // drained on a background thread. The earlier cmd.exe wrapper existed to
            // give the child a console + file-backed stdio; we no longer shell out,
            // so a malformed serverArgs string can't be re-parsed by cmd and the
            // child has exactly the argument vector we pass. stdout is left attached
            // (not redirected) to avoid the WPF-no-console fast-exit we hit when
            // redirecting stdout from a windowed parent; only stderr is piped.
            string logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SenseEase", "llama-server.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            try { File.Delete(logPath); } catch { }

            // SEC-06 (H9 threat model): the minted Bearer key is delivered to the
            // child via its ENVIRONMENT (LLAMA_ARG_API_KEY — llama.cpp maps every
            // --arg to an LLAMA_ARG_<UPPER_SNAKE> env var, see the env-var injection
            // on psi below), NOT on the command line. Same-user processes can read
            // another process's command line (WMI Win32_Process.CommandLine), so an
            // api-key argument on the CLI would leak the loopback credential to exactly
            // the attacker class H9's loopback auth is meant to exclude — defeating the
            // control for the server's whole lifetime. Environment blocks are not
            // readable across processes without elevated handles, so the key stays
            // private. The CLI carries only the non-secret flags below.
            string serverArgs =
                $"--model \"{_modelPath}\" " +
                $"--n-gpu-layers 99 " +
                $"--host 127.0.0.1 --port {ServerPort} " +
                // H9: disable the built-in web UI (no need for an attack surface) and
                // pin the context so a model swap can't silently shift behaviour.
                // (--api-key moved off the CLI to LLAMA_ARG_API_KEY — see SEC-06 above.)
                $"--no-webui --no-context-shift " +
                // Context budget. The expanded SystemPrompt (LANGUAGE RULE + 4
                // delta fields + 7 worked examples) plus the [RecentAdjustments]/
                // [RecentEvents] history blocks plus the user turn comes in at
                // ~2.1 KB tokens — 2048 caused every request to be rejected with
                // "exceeds the available context size". 8192 leaves comfortable
                // headroom (model trains to 262144 anyway) and costs ~70 MiB of
                // additional KV-cache VRAM on the RTX 5060 — negligible.
                $"--ctx-size 8192 " +
                $"--no-warmup " +
                // --jinja uses the GGUF's embedded Jinja chat template. Without
                // it the model produces garbage tokens (same bug we hit earlier
                // through LLamaSharp's path).
                $"--jinja";

            var psi = new ProcessStartInfo
            {
                FileName               = serverExe,   // H10: direct exe, no cmd.exe shell
                Arguments              = serverArgs,
                WorkingDirectory       = serverDir,
                UseShellExecute        = false,        // required so EnvironmentVariables is honored
                CreateNoWindow         = true,
                RedirectStandardOutput = false,        // leave stdout attached (WPF fast-exit guard)
                RedirectStandardError  = true,         // H10: capture stderr ourselves
                StandardErrorEncoding  = Encoding.UTF8,
            };

            // SEC-06 (H9 threat model): inject the Bearer key through the child's
            // private environment instead of the command line. llama.cpp's arg parser
            // reads LLAMA_ARG_API_KEY as the equivalent of --api-key (it derives an
            // LLAMA_ARG_<UPPER_SNAKE> env name for each long flag), so the server still
            // enforces the same key on every request — but the secret is no longer
            // visible in Win32_Process.CommandLine to same-user processes. Env blocks
            // require an elevated/own-process handle to read, which the H9 attacker
            // class (unprivileged same-user code) does not have.
            psi.EnvironmentVariables["LLAMA_ARG_API_KEY"] = _apiKey;

            _serverProcess = Process.Start(psi);
            if (_serverProcess is null)
                throw new InvalidOperationException("Process.Start returned null.");

            // H10: drain stderr on a background thread to the log file so the
            // pipe buffer can never fill and back-pressure / stall the server.
            // SEC-01: each line is run through PathRedactor before it is written so
            // a "...\Users\<name>\..." segment (e.g. the model path llama-server
            // echoes at load) never lands in the shipped llama-server.log.
            StartStderrPump(_serverProcess, logPath);

            // Adopt into a Job Object configured with KILL_ON_JOB_CLOSE — Windows
            // reaps the llama-server child whenever we exit (Stop-Process, Task
            // Manager kill, crash, etc.), preventing orphan llama-server.exe
            // instances from accumulating across dev runs.
            // SIDE-05: log any Win32/ctor failure from the reaper to the existing
            // llama-server.log so the non-fatal failure isn't completely silent.
            ChildProcessReaper.Adopt(_serverProcess);
            if (ChildProcessReaper.LastError is { } reaperErr)
                try { File.AppendAllText(logPath, reaperErr + Environment.NewLine); } catch { }

            await WaitForServerReadyAsync(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            IsModelLoaded = true;
            LoadError = null;
        }
        catch (Exception ex)
        {
            // For a SecurityException keep the descriptive LoadError set at the
            // integrity/spawn-refusal site (fall back to its message if unset);
            // for any other failure use the generic type:message form.
            if (ex is SecurityException)
                LoadError ??= $"{ex.GetType().Name}: {ex.Message}";
            else
                LoadError = $"{ex.GetType().Name}: {ex.Message}";
            try { _serverProcess?.Kill(entireProcessTree: true); } catch { }
            _serverProcess = null;
            // Spawn failed — drop the stale key so a later retry mints a fresh one
            // and no client keeps presenting a credential to a server that isn't up.
            _apiKey = null;
            LlamaSessionKey.Set(null);
            _http.DefaultRequestHeaders.Authorization = null;
            throw;
        }
    }

    /// <summary>H39: returns true if <paramref name="port"/> already has an active
    /// IPv4/IPv6 loopback TCP listener. Used to refuse spawning a second server on
    /// our port (stale orphan, or a squatter we'd then hand our key to).</summary>
    private static bool IsLoopbackPortListening(int port)
    {
        try
        {
            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            foreach (var ep in listeners)
            {
                if (ep.Port == port &&
                    System.Net.IPAddress.IsLoopback(ep.Address))
                    return true;
            }
        }
        catch
        {
            // If we can't enumerate listeners, don't block startup on a probe error.
            return false;
        }
        return false;
    }

    /// <summary>H10: drain the server's stderr on a background thread to the log
    /// file. Keeps the pipe buffer empty so a verbose load phase can't back-pressure
    /// and stall the child. Best-effort; failures are swallowed.</summary>
    private static void StartStderrPump(Process proc, string logPath)
    {
        var thread = new Thread(() =>
        {
            try
            {
                using var reader = proc.StandardError;
                using var writer = new StreamWriter(
                    new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    AutoFlush = true,
                };
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    // SEC-01: redact any "\Users\<name>\" segment before persisting so
                    // the OS account name never leaks into llama-server.log (which the
                    // handbook log-zip ships to the test team). PathRedactor never
                    // throws and returns the line unchanged when there is nothing to
                    // redact, so this is a no-op on the common (path-free) line.
                    try { writer.WriteLine(PathRedactor.Redact(line)); } catch { break; }
                }
            }
            catch { /* pipe closed / process gone — nothing to do */ }
        })
        {
            IsBackground = true,
            Name = "llama-server-stderr-pump",
        };
        thread.Start();
    }

    private async Task WaitForServerReadyAsync(TimeSpan timeout)
    {
        // H9: give the server ~800 ms to bind + begin loading before the first
        // probe, then verify readiness via /props (which only responds 200 with a
        // populated model name once the model is fully loaded) rather than the
        // bare /health endpoint, which can flip to 200 before the model is usable.
        await Task.Delay(800).ConfigureAwait(false);

        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_serverProcess is { HasExited: true })
                throw new InvalidOperationException(
                    $"llama-server exited during startup (exit code {_serverProcess.ExitCode}).");
            try
            {
                // /props requires the Bearer key (set on _http at spawn). A 200 with
                // a non-empty model identifier means the model is loaded and ready.
                using var resp = await _http.GetAsync($"{ServerUrl}/props").ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (PropsReportModelLoaded(body)) return;
                }
            }
            catch { /* server not listening / not ready yet */ }
            await Task.Delay(500).ConfigureAwait(false);
        }
        throw new TimeoutException("llama-server did not become ready within timeout.");
    }

    /// <summary>H9: parse the /props body and return true when it reports a loaded
    /// model — i.e. a non-empty model name/path string. llama-server exposes the
    /// loaded model under "model_path" (and/or "default_generation_settings.model").
    /// We accept any non-empty model identifier as the readiness signal. Internal
    /// for unit coverage.</summary>
    internal static bool PropsReportModelLoaded(string? propsBody)
    {
        if (string.IsNullOrWhiteSpace(propsBody)) return false;
        try
        {
            using var doc = JsonDocument.Parse(propsBody, _jsonDocOpts);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            if (HasNonEmptyString(root, "model_path")) return true;
            if (HasNonEmptyString(root, "model")) return true;

            if (root.TryGetProperty("default_generation_settings", out var gen)
                && gen.ValueKind == JsonValueKind.Object
                && (HasNonEmptyString(gen, "model") || HasNonEmptyString(gen, "model_path")))
                return true;

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasNonEmptyString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(v.GetString());

    public async Task<NluResult> SendAsync(string userMessage, CancellationToken ct = default)
    {
        string input = userMessage ?? string.Empty;
        string pureUserText = ExtractUserText(input);
        string stateSummary = ExtractStateSummary(input);
        string historyPrefix = ExtractOrBuildHistoryPrefix(input);

        // Clinical self-harm safety floor (Alpha-1 W1 / H1, H8). Short-circuits
        // the LLM call entirely when the user's message matches a strict self-
        // harm / severe-distress phrase. The LLM is NEVER given this text; a
        // fixed crisis-line response is returned and recorded in history. This is
        // defense-in-depth: ChatbotViewModel.SendAsync also runs the floor as its
        // line-1 intercept. Both call the single SenseEase.Core.Safety.SafetyFloor
        // so the vocabulary cannot drift between call sites (C3 / H3).
        if (SafetyFloor.Trips(pureUserText, out string crisisResponse))
        {
            // EM5-10: record the user turn only once the floor has decided. The
            // user text + fixed crisis reply are paired in history; the LLM is
            // never given the text (see class header).
            _history.Add(new ChatMessage { Role = ChatRole.User, Content = pureUserText });
            _history.Add(new ChatMessage { Role = ChatRole.Assistant, Content = crisisResponse });
            while (_history.Count > 20) _history.RemoveAt(0);
            return new NluResult
            {
                Intent              = "Unknown",
                FovDelta            = 0,
                MaskDepthDelta      = 0,
                GridVisibilityDelta = 0,
                FeatherWidthDelta   = 0,
                Response            = crisisResponse,
            };
        }

        // EM5-10: floor passed — now it is safe to add the user turn to history
        // ahead of the model/lexicon path below.
        _history.Add(new ChatMessage { Role = ChatRole.User, Content = pureUserText });

        if (!IsModelLoaded && IsModelAvailable)
        {
            try { await LoadAsync().ConfigureAwait(false); }
            catch { /* LoadError is set; fall through to lexicon, retry next message */ }
        }

        NluResult result;
        string? rawForCarry = null;
        if (IsModelLoaded)
        {
            string? raw = await AskServerForDecisionAsync(pureUserText, stateSummary, historyPrefix, ct).ConfigureAwait(false);
            LogModelTrace(pureUserText, raw);
            // M3 7e: stash the raw output so ChatbotViewModel can run
            // HabituationOverrideParser on it. This survives both:
            //   a) Correct LLM output where [PROFILE_OVERRIDE] sits AFTER
            //      the JSON envelope — invisible in result.Response.
            //   b) Malformed output where the block is embedded INSIDE the
            //      envelope, breaking envelope parse — the lexicon fallback
            //      builds result.Response from scratch, losing the original
            //      LLM text but raw still has it.
            rawForCarry = string.IsNullOrEmpty(raw) ? null : StripMarkdownFences(raw);
            if (!TryParseModelDecision(raw, pureUserText, out result))
                result = BuildFallbackResult(pureUserText);
        }
        else
        {
            result = BuildFallbackResult(pureUserText);
        }
        result.RawModelOutput = rawForCarry;

        _history.Add(new ChatMessage { Role = ChatRole.Assistant, Content = result.Response });
        while (_history.Count > 20) _history.RemoveAt(0);
        return result;
    }

    public void ClearHistory()
    {
        _history.Clear();
        // Subprocess design has no per-call session state to reset — each
        // SendAsync sends the full prompt (system + context + user) to the
        // server's stateless /v1/chat/completions endpoint.
    }

    /// <summary>Append one user-input + raw-model-output pair to
    /// %LocalAppData%\SenseEase\model_trace.jsonl. Tail this file to verify what
    /// the model actually emitted vs what the UI rendered — critical for
    /// distinguishing "model produced empty reply, lexicon backfilled" from
    /// "model produced the right reply".
    /// <para>B0 deferred: computed at ACCESS time (expression-bodied, not a cached
    /// <c>static readonly</c> field) so it routes through the <see cref="SenseEaseDataRoot"/>
    /// seam the host wires to <c>AppConfig.DataRoot</c> — the test suite retargets that
    /// override per-class, so the trace can never land in (or be deleted from) the
    /// developer's real SenseEase folder. Mirrors the AppConfig.ModelTracePath the App
    /// layer computes for the "clear all my data" button (both sides resolve the SAME
    /// file independently because Infrastructure can't see App config).</para></summary>
    private static string _modelTracePath => Path.Combine(
        SenseEaseDataRoot.Resolve(),
        "SenseEase", "model_trace.jsonl");

    /// <summary>H41 (PII): the model trace logs the user's chat text verbatim to
    /// disk — a privacy sink that must be OFF in shipped builds. Gate it behind an
    /// explicit developer opt-in. (The handover named <c>AppConfig.ModelTraceEnabled</c>,
    /// but Infrastructure cannot reference the App layer, so the opt-in is read from
    /// the environment: <c>SENSEEASE_DEV_MODE=1</c>.) Default — no env var — writes
    /// nothing.</summary>
    private static readonly bool _modelTraceEnabled =
        string.Equals(Environment.GetEnvironmentVariable("SENSEEASE_DEV_MODE"), "1", StringComparison.Ordinal);

    private static void LogModelTrace(string userText, string? raw)
    {
        if (!_modelTraceEnabled) return; // H41: no chat text to disk in shipped builds
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_modelTracePath)!);
            string line = System.Text.Json.JsonSerializer.Serialize(new
            {
                t = DateTime.UtcNow.ToString("o"), // H41: UTC ISO-8601, not local time
                user = userText,
                raw = raw ?? string.Empty,
            });
            File.AppendAllText(_modelTracePath, line + Environment.NewLine);
        }
        catch { /* best-effort */ }
    }

    /// <summary>H24 (PII): delete the on-disk model trace (which can carry the
    /// user's chat text). Wired into ChatbotViewModel.Clear so "clear chat" wipes
    /// the trace too. Best-effort; never throws.</summary>
    public void ClearOnDiskTrace()
    {
        try { if (File.Exists(_modelTracePath)) File.Delete(_modelTracePath); }
        catch { /* best-effort PII cleanup */ }
    }

    /// <summary>Posts the contextual user prompt to the llama-server's
    /// OpenAI-compatible /v1/chat/completions endpoint and returns the
    /// raw `choices[0].message.content` string. The server applies the
    /// GGUF's chat template internally (via --jinja), so we only send
    /// well-formed `role`/`content` pairs.</summary>
    private async Task<string?> AskServerForDecisionAsync(string userText, string stateSummary, string historyPrefix, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var body = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(historyPrefix))
            {
                body.Append(historyPrefix);
                if (!historyPrefix.EndsWith("\n", StringComparison.Ordinal)) body.Append('\n');
            }
            body.Append("State: ").Append(stateSummary).Append('\n')
                .Append("User: ").Append(userText).Append('\n')
                // Deterministic language pin. The SystemPrompt has a LANGUAGE RULE
                // too, but a 3 B model at temperature > 0 will sample around a rule
                // on ambiguous greetings (we saw "Hi, how are you" reliably return
                // Traditional Chinese). Pinning the language as an explicit
                // user-turn directive — not a system rule — makes the model
                // follow it with near-100 % reliability because instruction
                // adherence is strongest when the directive sits next to the input.
                .Append(BuildLanguagePin(userText)).Append('\n')
                .Append("Return JSON only.");

            var requestBody = new
            {
                messages = new[]
                {
                    new { role = "system", content = SystemPrompt },
                    new { role = "user",   content = body.ToString() },
                },
                max_tokens  = 256,
                temperature = 0.4,
                top_p       = 0.9,
                top_k       = 40,
                stop        = new[] { "[INST]", "User:" },
            };

            // NE3: never let a network error or a malformed-200 body throw past
            // here — any failure returns null so SendAsync routes to the existing
            // lexicon fallback. Cancellation is re-thrown so the caller's ct still
            // works. The lock is released in the outer finally regardless.
            try
            {
                using var resp = await _http.PostAsJsonAsync(
                    $"{ServerUrl}/v1/chat/completions", requestBody, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;

                string respBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(respBody, _jsonDocOpts);
                if (doc.RootElement.TryGetProperty("choices", out var choices)
                    && choices.GetArrayLength() > 0
                    && choices[0].TryGetProperty("message", out var msg)
                    && msg.TryGetProperty("content", out var content))
                {
                    return content.GetString();
                }
                return null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Network failure, timeout, or malformed JSON — fall back to lexicon.
                return null;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
    };

    private static string StripMarkdownFences(string raw)
    {
        // Remove ```json ... ``` or ``` ... ``` wrappers so ExtractJson can find the braces.
        string s = raw.Trim();
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            int firstNewline = s.IndexOf('\n');
            if (firstNewline >= 0) s = s[(firstNewline + 1)..];
        }
        if (s.EndsWith("```", StringComparison.Ordinal))
            s = s[..^3];
        return s.Trim();
    }

    private static bool TryParseModelDecision(string? raw, string userText, out NluResult result)
    {
        result = default!;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        string stripped = StripMarkdownFences(raw);
        string json = ExtractJson(stripped);
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            var d = JsonSerializer.Deserialize<ModelDecision>(json, _jsonOpts);
            if (d is null) return false;

            string intent = NormalizeIntent(d.Intent);
            if (intent is not ("ReportDiscomfort" or "ReportComfort" or "ResetRequest"
                or "AnalyzeRequest" or "FollowupRequest" or "Unknown"))
                return false; // parse-failure signal → caller routes to BuildFallbackResult

            // The model proposed; the single NluComplianceGate disposes. Every
            // deterministic compliance rule (reset-upgrade, promote/demote, non-actuating
            // zeroing, the model-engaged + all-zeros delta policy, the clamps, the reply
            // fallback, the followup sanitize/downgrade) now lives in ONE pure place the
            // lexicon fallback also passes through. The actuating deltas are carried as
            // NULLABLE so the gate keeps the "model omitted vs model set 0" distinction.
            result = NluComplianceGate.Check(
                new NluComplianceGate.RawNluDecision(
                    Intent: intent,
                    Severity: d.Severity,
                    Reply: d.Reply,
                    FovDelta: d.FovDelta,
                    MaskDepthDelta: d.MaskDepthDelta,
                    GridVisibilityDelta: d.GridVisibilityDelta,
                    FollowupQuestion: d.FollowupQuestion,
                    FollowupOptions: d.FollowupOptions,
                    Source: NluComplianceGate.DecisionSource.Model),
                userText);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static NluResult BuildFallbackResult(string userText)
    {
        // The lexicon DECIDES (intent + severity) when the LLM is down/loading; the SAME
        // NluComplianceGate then disposes — so the fallback inherits every compliance rule
        // (reset-upgrade, demote, the Case-1 "Unknown → ask" clarify, the severity-bucket
        // DeriveAdjustments defaults, the empty-reply BuildFallbackResponse, the followup
        // sanitize). Deltas cross as NULL (the lexicon is never "engaged" with explicit
        // deltas), so the gate takes its DeriveAdjustments branch exactly as before. The
        // Lexicon source unlocks the gate's Case-1 picker (the model handles Unknown itself).
        var (intent, _) = FeedbackLexicon.Classify(userText);
        int severity = FeedbackLexicon.SeverityBucket(userText);
        return NluComplianceGate.Check(
            new NluComplianceGate.RawNluDecision(
                Intent: intent,
                Severity: severity,
                Reply: null, // gate fills via BuildFallbackResponse when empty
                FovDelta: null,
                MaskDepthDelta: null,
                GridVisibilityDelta: null,
                FollowupQuestion: null,
                FollowupOptions: null,
                Source: NluComplianceGate.DecisionSource.Lexicon),
            userText);
    }

    private static string NormalizeIntent(string? intent)
    {
        string t = (intent ?? string.Empty).Trim();
        return t switch
        {
            "reportdiscomfort" => "ReportDiscomfort",
            "reportcomfort" => "ReportComfort",
            "resetrequest" => "ResetRequest",
            "analyzerequest" => "AnalyzeRequest",
            "unknown" => "Unknown",
            _ when t.Equals("ReportDiscomfort", StringComparison.OrdinalIgnoreCase) => "ReportDiscomfort",
            _ when t.Equals("ReportComfort", StringComparison.OrdinalIgnoreCase) => "ReportComfort",
            _ when t.Equals("ResetRequest", StringComparison.OrdinalIgnoreCase) => "ResetRequest",
            _ when t.Equals("AnalyzeRequest", StringComparison.OrdinalIgnoreCase) => "AnalyzeRequest",
            _ when t.Equals("Unknown", StringComparison.OrdinalIgnoreCase) => "Unknown",
            _ => t,
        };
    }

    internal static string ExtractJson(string raw) // internal for EM6 unit coverage (InternalsVisibleTo SenseEase.Tests)
    {
        int start = raw.IndexOf('{');
        if (start < 0) return string.Empty;
        // EM6: scan brace depth from the first '{' and stop at its MATCHING close,
        // so a trailing [PROFILE_OVERRIDE]{…} block (or any content after the
        // envelope object) is not swallowed. The old LastIndexOf('}') spanned BOTH
        // objects plus the marker text between them, so Deserialize<ModelDecision>
        // threw on the trailing content and every proposal turn silently fell back
        // to a generic lexicon reply. String-aware so a brace inside a JSON string
        // value (e.g. a rationale containing '}') doesn't break the depth count.
        int depth = 0;
        bool inString = false, escaped = false;
        for (int i = start; i < raw.Length; i++)
        {
            char c = raw[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    if (--depth == 0) return raw[start..(i + 1)];
                    break;
            }
        }
        return string.Empty; // unbalanced — no complete object
    }

    private static string ExtractUserText(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        const string marker = "User:";
        int idx = input.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return input.Trim();
        return input[(idx + marker.Length)..].Trim();
    }

    private static string ExtractStateSummary(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "state unavailable";
        int start = input.IndexOf("[SenseEaseState", StringComparison.OrdinalIgnoreCase);
        int end = input.IndexOf(']', start >= 0 ? start : 0);
        if (start >= 0 && end > start) return input[start..(end + 1)];
        return "state unavailable";
    }

    /// <summary>
    /// Returns the recent-history prefix already embedded in <paramref name="input"/> (everything
    /// before the [SenseEaseState ...] line). If no prefix is present but the service was
    /// constructed with adjustment/event log dependencies, build one from those sources as a
    /// graceful fallback. Empty sections are omitted; returns empty string when nothing is available.
    /// </summary>
    private string ExtractOrBuildHistoryPrefix(string input)
    {
        if (!string.IsNullOrEmpty(input))
        {
            int stateIdx = input.IndexOf("[SenseEaseState", StringComparison.OrdinalIgnoreCase);
            if (stateIdx > 0)
            {
                string prefix = input[..stateIdx].TrimEnd('\r', '\n', ' ', '\t');
                if (prefix.Contains("[RecentAdjustments]", StringComparison.OrdinalIgnoreCase)
                    || prefix.Contains("[RecentEvents]", StringComparison.OrdinalIgnoreCase))
                {
                    return prefix;
                }
            }
        }
        return BuildHistoryPrefixFromDeps();
    }

    private string BuildHistoryPrefixFromDeps()
    {
        var sb = new StringBuilder();
        if (_adjustmentLog is not null)
        {
            var all = _adjustmentLog.All();
            if (all.Count > 0)
            {
                int skip = Math.Max(0, all.Count - HistoryWindow);
                sb.AppendLine("[RecentAdjustments]");
                for (int i = skip; i < all.Count; i++)
                {
                    var r = all[i];
                    string sign = r.Delta >= 0 ? "+" : string.Empty;
                    string utter = string.IsNullOrEmpty(r.Utterance) ? string.Empty : $" \"{Sanitize(r.Utterance)}\"";
                    sb.Append(r.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))
                      .Append(' ').Append(r.Parameter)
                      .Append(' ').Append(sign).Append(r.Delta.ToString("0.##"))
                      .Append(' ').Append(r.Source)
                      .AppendLine(utter);
                }
            }
        }
        if (_eventLog is not null)
        {
            var recent = _eventLog.Recent(); // most-recent-first
            if (recent.Count > 0)
            {
                int take = Math.Min(HistoryWindow, recent.Count);
                sb.AppendLine("[RecentEvents]");
                // Render oldest-first for readability.
                for (int i = take - 1; i >= 0; i--)
                {
                    var e = recent[i];
                    sb.Append(e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"))
                      .Append(' ').Append(e.Category)
                      .Append(" \"").Append(Sanitize(e.Message)).Append('"')
                      .AppendLine();
                }
            }
        }
        return sb.ToString().TrimEnd('\r', '\n');
    }

    private static string Sanitize(string s)
        => string.IsNullOrEmpty(s) ? string.Empty : s.Replace('"', '\'').Replace("\r", " ").Replace("\n", " ");

    // Item 4 fix: the pin now explicitly OVERRIDES the language of anything earlier in the prompt.
    // Each call is system + one user turn (no prior replies are replayed), but the [RecentAdjustments]
    // block echoes the user's PRIOR utterances verbatim — so after a mid-session language toggle the
    // context can be full of the old language and bias the 3 B model. The override clause pins the
    // reply to the toggle regardless of that context.
    private const string ZhLanguagePin =
        "(REPLY ONLY in Traditional Chinese / 繁體中文 — regardless of the language used in the recent-adjustments list, the state context, or anything else above. Do NOT use Simplified Chinese, English, or any other script.)";
    private const string EnLanguagePin =
        "(REPLY ONLY in US English — regardless of the language used in the recent-adjustments list, the state context, or anything else above. Do NOT use Chinese or any other script.)";

    /// <summary>Returns a single-line language directive to append to the user
    /// turn, pinning the model's reply language.
    ///
    /// <para>Primary source (Kirby's decision: "AI replies follow the language
    /// toggle"): the live UI-language toggle, read via
    /// <see cref="ChatbotUiLanguage.IsEnglish"/>. English toggle → English pin;
    /// Traditional-Chinese toggle → 繁體中文 pin — regardless of what script the
    /// user happened to type.</para>
    ///
    /// <para>Fallback (provider unwired — headless tests / lexicon-only hosts):
    /// the original Han-character heuristic. If the user's text contains ANY CJK
    /// Unified Ideograph (U+4E00..U+9FFF) pin Traditional Chinese, else US English.
    /// Either way the pin is deterministic, removing the language choice from the
    /// model — which a 3 B model otherwise gets wrong on short / ambiguous inputs.</para>
    /// </summary>
    internal static string BuildLanguagePin(string? userText)
    {
        // UI toggle is authoritative when the host has wired it.
        bool? uiEnglish = ChatbotUiLanguage.IsEnglish;
        if (uiEnglish is bool en)
            return en ? EnLanguagePin : ZhLanguagePin;

        // No toggle wired — fall back to per-message Han detection.
        if (!string.IsNullOrEmpty(userText))
        {
            foreach (char c in userText)
            {
                if (c >= 0x4E00 && c <= 0x9FFF)
                    return ZhLanguagePin;
            }
        }
        return EnLanguagePin;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // M0 (v0.8.1 follow-up): self-harm safety floor. Copied from
    // CalibrationChatbotService.cs:296-348 — see SendAsync above for the
    // short-circuit point and the rationale for duplicating rather than
    // extracting to a shared utility.
    //
    // Vocabulary tuned to favour false-positives over misses (per
    // safety-first spec). Whole-phrase substring match, not keyword.
    // ─────────────────────────────────────────────────────────────────────────

    // Safety-floor vocabulary + Trips() + IsHan() extracted to
    // SenseEase.Core.Safety.SafetyFloor (Alpha-1 W1 / H1, H3, H8, C3) — single
    // source of truth shared with CalibrationChatbotService and ChatbotViewModel.
    // Called as SafetyFloor.Trips(...) from SendAsync above.

    public void Dispose()
    {
        try { _serverProcess?.Kill(entireProcessTree: true); } catch { }
        try { _serverProcess?.Dispose(); } catch { }
        _serverProcess = null;
        // H9: clear the shared session key so a torn-down server doesn't leave a
        // stale credential that a calibration client would keep presenting.
        if (_apiKey is not null && string.Equals(LlamaSessionKey.Current, _apiKey, StringComparison.Ordinal))
            LlamaSessionKey.Set(null);
        _apiKey = null;
        try { _http.Dispose(); } catch { }
        try { _lock.Dispose(); } catch { }
    }

    private sealed class ModelDecision
    {
        public string Intent { get; set; } = "Unknown";
        public int Severity { get; set; } = 1;
        public string Reply { get; set; } = string.Empty;

        // Optional, populated only when Intent == "FollowupRequest". Matched against
        // snake_case JSON via JsonNamingPolicy.SnakeCaseLower in _jsonOpts (already
        // configured for the existing fields). 2-4 short option strings.
        [System.Text.Json.Serialization.JsonPropertyName("followup_question")]
        public string? FollowupQuestion { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("followup_options")]
        public string[]? FollowupOptions { get; set; }

        // Personalized minor-adjustment deltas. Nullable so we can distinguish
        // "model explicitly chose 0" from "model omitted the field"; when null
        // we fall back to FeedbackLexicon.DeriveAdjustments.
        [System.Text.Json.Serialization.JsonPropertyName("fov_delta")]
        public int? FovDelta { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("mask_depth_delta")]
        public double? MaskDepthDelta { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("grid_visibility_delta")]
        public double? GridVisibilityDelta { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("feather_width_delta")]
        public double? FeatherWidthDelta { get; set; }
    }
}
