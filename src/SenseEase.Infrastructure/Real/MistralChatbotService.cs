using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Configuration;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Nlp;

namespace SenseEase.Infrastructure.Real;

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
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private const int ServerPort = 8181;
    private string ServerUrl => $"http://127.0.0.1:{ServerPort}";

    // Optional history sources used as a fallback when the caller did not already
    // embed [RecentAdjustments] / [RecentEvents] blocks in the contextual input.
    private readonly IAdjustmentLog? _adjustmentLog;
    private readonly IEventLogStore? _eventLog;
    private const int HistoryWindow = 6;

    private const string SystemPrompt =
        // ── LANGUAGE RULE (highest priority — placed first so the model can't miss it) ──
        "LANGUAGE RULE — read this FIRST and obey it absolutely:\n" +
        "  • The user's input is in EXACTLY ONE language: either Traditional Chinese (zh-TW) or US English (en-US).\n" +
        "  • DETECT: does the user's CURRENT text contain ANY Han characters (CJK Unified Ideographs)? If yes → use Traditional Chinese for everything you emit (reply, followup_question, followup_options). If no → use US English for everything.\n" +
        "  • Mirror the user. 'hello' → reply in English. '你好' → reply in Traditional Chinese. 'I have a headache' → English. '我頭痛' → Traditional Chinese.\n" +
        "  • NEVER mix the two languages in ONE field. NEVER use Simplified Chinese characters (头/没/时/这/个/觉/调/视/视觉/调整). NEVER include Japanese kana, Hangul, or any other script.\n" +
        "  • When in doubt about the user's language, default to en-US.\n\n" +

        // ── ROLE & I/O CONTRACT ──
        "You are SenseEase NLU for a Windows visual-comfort app. Inputs include optional [RecentAdjustments] / [RecentEvents] history blocks (oldest-first), one [SenseEaseState ...] line, and a final 'User: <text>' line. " +
        "Reply with ONLY one line of compact JSON — no markdown fences, no commentary. Every REQUIRED field below MUST be present and non-empty; omitting `reply` is INVALID output.\n\n" +

        // ── FIELDS ──
        "Fields:\n" +
        "  intent (REQUIRED, string): one of ReportDiscomfort, ReportComfort, ResetRequest, AnalyzeRequest, FollowupRequest, Unknown.\n" +
        "  severity (REQUIRED, int 1-5): 1=very mild, 5=severe; derive from CURRENT user text only, never from history; use 1 for non-discomfort intents.\n" +
        "  reply (REQUIRED, non-empty string): user-facing message in the language picked by the LANGUAGE RULE above. ONE sentence under 30 words for ReportDiscomfort/ReportComfort/ResetRequest (mention FOV / mask / grid / monitoring when relevant). Up to TWO sentences under 55 words for AnalyzeRequest / Unknown, citing concrete numbers / timestamps from [RecentAdjustments] / [RecentEvents].\n" +

        // ── PERSONALIZED DELTAS (new) ──
        "  fov_delta (OPTIONAL, int -30..0): your suggested FOV-radius adjustment in percent. Negative tightens (smaller clear zone, more peripheral mask). Use FINER granularity than rough buckets when the user's wording warrants it — e.g. -3 for very mild, -8 for mild, -14 for moderate, -22 for severe, -28 for crippling. 0 (or omit) for ReportComfort / ResetRequest / AnalyzeRequest / FollowupRequest / Unknown.\n" +
        "  mask_depth_delta (OPTIONAL, float -0.3..0.3): peripheral-mask opacity nudge. Positive deepens mask, negative lightens. Typical magnitudes: 0.03–0.18 for discomfort severity 1–5. 0 to keep unchanged.\n" +
        "  grid_visibility_delta (OPTIONAL, float -0.3..0.3): Chang-Hain anchor-grid visibility nudge. Positive shows more grid; useful for vection-prone users. 0.02–0.08 range typical.\n" +
        "  feather_width_delta (OPTIONAL, float -0.3..0.3): mask edge softness nudge. Positive softens. 0 typical for most cases.\n" +
        "  followup_question (REQUIRED IFF intent=FollowupRequest, string, language per LANGUAGE RULE): the clarification question.\n" +
        "  followup_options (REQUIRED IFF intent=FollowupRequest, array 2-4 strings ≤ 30 chars, language per LANGUAGE RULE): tap-able quick replies.\n\n" +

        // ── INTENT RULES ──
        "Intent rules:\n" +
        "- ReportDiscomfort: user is clearly experiencing dizziness, nausea, headache, eye strain, or visual discomfort RIGHT NOW.\n" +
        "- ReportComfort: user feels better or stable.\n" +
        "- ResetRequest: user asks to undo / reset / restore defaults.\n" +
        "- AnalyzeRequest: user ASKS about past sessions / causes / patterns. No adjustment.\n" +
        "- FollowupRequest: user's input is too vague to act on AND no specific symptom is named. Ask ONE clarifying question with 2–4 options. Use sparingly.\n" +
        "- Unknown: anything else.\n" +
        "Never apply adjustments from history alone — only CURRENT user text drives intent/severity. AnalyzeRequest, FollowupRequest, Unknown MUST NOT emit non-zero deltas.\n\n" +

        // ── EXAMPLES (now show explicit deltas + language-match) ──
        "Examples:\n" +
        "User: 我覺得有點頭暈\n" +
        "{\"intent\":\"ReportDiscomfort\",\"severity\":2,\"reply\":\"了解，我已收緊 FOV 並加深周邊遮罩以降低視覺刺激。\",\"fov_delta\":-8,\"mask_depth_delta\":0.06,\"grid_visibility_delta\":0.03,\"feather_width_delta\":0.0}\n" +
        "User: I have a mild headache\n" +
        "{\"intent\":\"ReportDiscomfort\",\"severity\":2,\"reply\":\"Got it — I've slightly tightened the FOV and softened the mask edges to ease visual load.\",\"fov_delta\":-7,\"mask_depth_delta\":0.05,\"grid_visibility_delta\":0.02,\"feather_width_delta\":0.03}\n" +
        "User: I have a REALLY bad migraine, the screen is killing me\n" +
        "{\"intent\":\"ReportDiscomfort\",\"severity\":5,\"reply\":\"Sorry to hear it's that bad. I've sharply reduced FOV and deepened the mask to minimise stimulation.\",\"fov_delta\":-26,\"mask_depth_delta\":0.20,\"grid_visibility_delta\":0.07,\"feather_width_delta\":0.04}\n" +
        "User: 好多了，謝謝\n" +
        "{\"intent\":\"ReportComfort\",\"severity\":1,\"reply\":\"很好，系統會持續監測並逐步放寬保護。\",\"fov_delta\":0,\"mask_depth_delta\":0.0,\"grid_visibility_delta\":0.0,\"feather_width_delta\":0.0}\n" +
        "User: reset everything to default\n" +
        "{\"intent\":\"ResetRequest\",\"severity\":1,\"reply\":\"Done — overlay settings restored to default.\",\"fov_delta\":0,\"mask_depth_delta\":0.0,\"grid_visibility_delta\":0.0,\"feather_width_delta\":0.0}\n" +
        "User: I felt dizzy during last session, do you know what causes it?\n" +
        "{\"intent\":\"AnalyzeRequest\",\"severity\":1,\"reply\":\"Looking at recent events, pressure spiked to 220 and the FOV mask activated, after which you reduced FovRadius by 15%. High optical flow most likely triggered the dizziness.\",\"fov_delta\":0,\"mask_depth_delta\":0.0,\"grid_visibility_delta\":0.0,\"feather_width_delta\":0.0}\n" +
        "User: 怪怪的\n" +
        "{\"intent\":\"FollowupRequest\",\"severity\":1,\"reply\":\"請問您現在感受到哪一種不適？\",\"followup_question\":\"請問您現在感受到哪一種不適？\",\"followup_options\":[\"暈眩\",\"噁心\",\"頭痛\",\"眼睛疲勞\"],\"fov_delta\":0,\"mask_depth_delta\":0.0,\"grid_visibility_delta\":0.0,\"feather_width_delta\":0.0}\n" +
        "User: I feel off\n" +
        "{\"intent\":\"FollowupRequest\",\"severity\":1,\"reply\":\"Which sensation matches what you're feeling?\",\"followup_question\":\"Which sensation matches what you're feeling?\",\"followup_options\":[\"Dizziness\",\"Nausea\",\"Headache\",\"Eye strain\"],\"fov_delta\":0,\"mask_depth_delta\":0.0,\"grid_visibility_delta\":0.0,\"feather_width_delta\":0.0}";

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

    /// <summary>Spawns llama-server.exe as a child process bound to localhost:8181
    /// with the model loaded into VRAM (--n-gpu-layers 99). Waits for /health to
    /// return 200 before marking IsModelLoaded=true. The server runs the same
    /// b9294 native binaries that already work for `llama-cli`, just exposed via
    /// the OpenAI-compatible HTTP API so we never call llama.dll directly from C#.
    /// </summary>
    public async Task LoadAsync()
    {
        if (!IsModelAvailable || IsModelLoaded) return;
        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory
                                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string serverDir = Path.Combine(baseDir, "runtimes", "win-x64", "native", "cuda12");
            string serverExe = Path.Combine(serverDir, "llama-server.exe");
            if (!File.Exists(serverExe))
                throw new FileNotFoundException($"llama-server.exe not found at: {serverExe}");

            // Tee llama-server's stdout+stderr to a log file via a cmd.exe wrapper.
            // Direct Process.Start of llama-server.exe with RedirectStandardOutput=true
            // from a WPF (no-console) parent caused the server stub to exit within a
            // second of startup (likely a console-attachment heuristic in the stub or
            // pipe-buffer back-pressure during the verbose load phase). The cmd wrapper
            // gives the child a proper console + file-backed stdio. Kill(entireProcessTree)
            // takes both cmd and the llama-server grandchild down together.
            string logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SenseEase", "llama-server.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            try { File.Delete(logPath); } catch { }

            string serverArgs =
                $"--model \"{_modelPath}\" " +
                $"--n-gpu-layers 99 " +
                $"--host 127.0.0.1 --port {ServerPort} " +
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
                FileName = "cmd.exe",
                // cmd /c "exe args > log 2>&1"
                Arguments = $"/c \"\"{serverExe}\" {serverArgs} > \"{logPath}\" 2>&1\"",
                WorkingDirectory       = serverDir,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = false,
                RedirectStandardError  = false,
            };

            _serverProcess = Process.Start(psi);
            if (_serverProcess is null)
                throw new InvalidOperationException("Process.Start returned null.");
            // Adopt into a Job Object configured with KILL_ON_JOB_CLOSE — Windows
            // reaps the llama-server child whenever we exit (Stop-Process, Task
            // Manager kill, crash, etc.), preventing orphan llama-server.exe
            // instances from accumulating across dev runs.
            ChildProcessReaper.Adopt(_serverProcess);

            await WaitForServerReadyAsync(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            IsModelLoaded = true;
            LoadError = null;
        }
        catch (Exception ex)
        {
            LoadError = $"{ex.GetType().Name}: {ex.Message}";
            try { _serverProcess?.Kill(entireProcessTree: true); } catch { }
            _serverProcess = null;
            throw;
        }
    }

    private async Task WaitForServerReadyAsync(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_serverProcess is { HasExited: true })
                throw new InvalidOperationException(
                    $"llama-server exited during startup (exit code {_serverProcess.ExitCode}).");
            try
            {
                var resp = await _http.GetAsync($"{ServerUrl}/health").ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) return;
            }
            catch { /* server not listening yet */ }
            await Task.Delay(500).ConfigureAwait(false);
        }
        throw new TimeoutException("llama-server did not become ready within timeout.");
    }

    public async Task<NluResult> SendAsync(string userMessage, CancellationToken ct = default)
    {
        string input = userMessage ?? string.Empty;
        string pureUserText = ExtractUserText(input);
        string stateSummary = ExtractStateSummary(input);
        string historyPrefix = ExtractOrBuildHistoryPrefix(input);
        _history.Add(new ChatMessage { Role = ChatRole.User, Content = pureUserText });

        if (!IsModelLoaded && IsModelAvailable)
        {
            try { await LoadAsync().ConfigureAwait(false); }
            catch { /* LoadError is set; fall through to lexicon, retry next message */ }
        }

        NluResult result;
        if (IsModelLoaded)
        {
            string? raw = await AskServerForDecisionAsync(pureUserText, stateSummary, historyPrefix, ct).ConfigureAwait(false);
            LogModelTrace(pureUserText, raw);
            if (!TryParseModelDecision(raw, pureUserText, out result))
                result = BuildFallbackResult(pureUserText);
        }
        else
        {
            result = BuildFallbackResult(pureUserText);
        }

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
    /// "model produced the right reply".</summary>
    private static readonly string _modelTracePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SenseEase", "model_trace.jsonl");
    private static void LogModelTrace(string userText, string? raw)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_modelTracePath)!);
            string line = System.Text.Json.JsonSerializer.Serialize(new
            {
                t = DateTime.Now.ToString("HH:mm:ss.fff"),
                user = userText,
                raw = raw ?? string.Empty,
            });
            File.AppendAllText(_modelTracePath, line + Environment.NewLine);
        }
        catch { /* best-effort */ }
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
                return false;

            int severity = Math.Clamp(d.Severity, 1, 5);

            // Belt-and-suspenders: if lexicon detects reset cue, upgrade intent.
            var (_, _, _, reset) = FeedbackLexicon.ClassifyShaderHints(userText);
            if (reset && intent != "ResetRequest") intent = "ResetRequest";

            // AnalyzeRequest / FollowupRequest are non-actuating — never derive deltas.
            int fov; double md, gv, fw;
            if (intent == "AnalyzeRequest" || intent == "FollowupRequest")
            {
                fov = 0; md = 0; gv = 0; fw = 0;
            }
            else
            {
                // Personalized path: when the model emitted explicit deltas, trust
                // them (clamped to safe ranges); otherwise fall back to the lexicon's
                // severity-bucket defaults. Any one field being non-null counts as
                // "model engaged", at which point we apply ALL four model values
                // (missing ones implicitly 0) — mixing model+lexicon per-field would
                // produce incoherent combinations.
                bool modelProvidedDeltas = d.FovDelta.HasValue
                    || d.MaskDepthDelta.HasValue
                    || d.GridVisibilityDelta.HasValue
                    || d.FeatherWidthDelta.HasValue;
                if (modelProvidedDeltas)
                {
                    fov = Math.Clamp(d.FovDelta ?? 0,            -30, 0);
                    md  = Math.Clamp(d.MaskDepthDelta ?? 0,      -0.3, 0.3);
                    gv  = Math.Clamp(d.GridVisibilityDelta ?? 0, -0.3, 0.3);
                    fw  = Math.Clamp(d.FeatherWidthDelta ?? 0,   -0.3, 0.3);
                }
                else
                {
                    (fov, md, gv, fw) = FeedbackLexicon.DeriveAdjustments(intent, severity, userText);
                }
            }

            string response = (d.Reply ?? string.Empty).Trim();
            if (response.Length == 0)
                response = FeedbackLexicon.BuildFallbackResponse(userText, intent);

            // Sanitize followup payload: trust the model only if it gave both a
            // question AND 2-4 short options when intent is FollowupRequest.
            // If the model claimed FollowupRequest but failed to populate options,
            // fall back to the lexicon-built picker so the UI never sees a stuck
            // "no options, no progress" state.
            string? followupQ = null;
            string[]? followupOpts = null;
            if (intent == "FollowupRequest")
            {
                if (!string.IsNullOrWhiteSpace(d.FollowupQuestion) &&
                    d.FollowupOptions is { Length: >= 2 and <= 4 })
                {
                    followupQ = d.FollowupQuestion!.Trim();
                    followupOpts = Array.ConvertAll(d.FollowupOptions,
                        o => (o ?? string.Empty).Trim());
                }
                else
                {
                    var (q, opts) = FeedbackLexicon.TryBuildFollowup(userText, intent, severity);
                    if (q is null)
                    {
                        // Model claimed FollowupRequest on input the lexicon can't
                        // justify clarifying — downgrade to Unknown rather than ship a
                        // broken card. Keeps the visible reply but drops the picker.
                        intent = "Unknown";
                    }
                    else
                    {
                        followupQ = q;
                        followupOpts = opts;
                        if (string.IsNullOrWhiteSpace(response)) response = q;
                    }
                }
            }

            result = new NluResult
            {
                Intent = intent,
                FovDelta = fov,
                MaskDepthDelta = md,
                GridVisibilityDelta = gv,
                FeatherWidthDelta = fw,
                Response = response,
                FollowupQuestion = followupQ,
                FollowupOptions = followupOpts,
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static NluResult BuildFallbackResult(string userText)
    {
        var (intent, _) = FeedbackLexicon.Classify(userText);
        var (_, _, _, reset) = FeedbackLexicon.ClassifyShaderHints(userText);
        if (reset) intent = "ResetRequest";
        int severity = FeedbackLexicon.SeverityBucket(userText);
        var (fov, md, gv, fw) = FeedbackLexicon.DeriveAdjustments(intent, severity, userText);

        // Belt-and-suspenders: if the input is too vague to apply confidently,
        // the lexicon can offer a follow-up picker instead of guessing. When a
        // followup is offered we ALSO suppress the deltas — confirming first is
        // safer than half-applying an adjustment for an unconfirmed symptom.
        var (followupQ, followupOpts) = FeedbackLexicon.TryBuildFollowup(userText, intent, severity);
        if (followupQ is not null)
        {
            return new NluResult
            {
                Intent = "FollowupRequest",
                FovDelta = 0, MaskDepthDelta = 0, GridVisibilityDelta = 0, FeatherWidthDelta = 0,
                Response = followupQ,
                FollowupQuestion = followupQ,
                FollowupOptions = followupOpts,
            };
        }

        return new NluResult
        {
            Intent = intent,
            FovDelta = fov,
            MaskDepthDelta = md,
            GridVisibilityDelta = gv,
            FeatherWidthDelta = fw,
            Response = FeedbackLexicon.BuildFallbackResponse(userText, intent),
        };
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

    private static string ExtractJson(string raw)
    {
        int start = raw.IndexOf('{');
        int end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return string.Empty;
        return raw[start..(end + 1)];
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

    public void Dispose()
    {
        try { _serverProcess?.Kill(entireProcessTree: true); } catch { }
        try { _serverProcess?.Dispose(); } catch { }
        _serverProcess = null;
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
