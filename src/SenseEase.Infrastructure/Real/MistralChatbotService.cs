using System.Text;
using System.Text.Json;
using LLama;
using LLama.Common;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Configuration;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Nlp;

namespace SenseEase.Infrastructure.Real;

/// <summary>
/// Local Mistral chatbot via LLamaSharp.
/// Model-first: asks LLM for structured JSON decision, then falls back to lexicon rules.
/// </summary>
public sealed class MistralChatbotService : IChatbotService
{
    private readonly string _modelPath;
    private readonly ModelParams _modelParams;
    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private ChatSession? _session;
    private readonly List<ChatMessage> _history = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Optional history sources used as a fallback when the caller did not already
    // embed [RecentAdjustments] / [RecentEvents] blocks in the contextual input.
    private readonly IAdjustmentLog? _adjustmentLog;
    private readonly IEventLogStore? _eventLog;
    private const int HistoryWindow = 6;

    private const string SystemPrompt =
        "You are SenseEase NLU for a Windows visual-comfort app. You receive an input that may contain " +
        "an optional [RecentAdjustments] block (recent user overlay tweaks, oldest-first), an optional " +
        "[RecentEvents] block (recent system inference / mask events, oldest-first), one [SenseEaseState ...] " +
        "context line, and a final 'User: <text>' line. Reply with ONLY one line of JSON — no markdown, " +
        "no extra text — with exactly three fields: intent, severity, reply.\n" +
        "intent: one of ReportDiscomfort, ReportComfort, ResetRequest, AnalyzeRequest, Unknown.\n" +
        "severity: integer 1-5 (1=very mild, 5=severe); derive ONLY from the CURRENT user text, never from history; use 1 for non-discomfort intents.\n" +
        "reply: a short message for the user in the SAME language as the user (do NOT repeat it in another language). " +
        "For ReportDiscomfort/ReportComfort/ResetRequest keep it to ONE sentence under 30 words, mentioning SenseEase protection (FOV / mask / grid / monitoring) when relevant. " +
        "For AnalyzeRequest or Unknown questions you MAY use up to TWO sentences (under 55 words total) and you SHOULD cite concrete numbers / timestamps from [RecentAdjustments] or [RecentEvents] to explain causes or patterns.\n" +
        "Intent rules:\n" +
        "- ReportDiscomfort: dizziness, nausea, headache, eye strain, visual discomfort right now.\n" +
        "- ReportComfort: the user feels better or stable.\n" +
        "- ResetRequest: undo / reset / restore defaults.\n" +
        "- AnalyzeRequest: the user ASKS a question about past sessions / causes / patterns / why something happened (e.g. 'what caused', 'why did I', 'is there a pattern', 'last session'). No new adjustment is requested.\n" +
        "- Unknown: anything else.\n" +
        "IMPORTANT: never apply or recommend new adjustments purely because the history shows past events — only the CURRENT user text drives intent and severity. AnalyzeRequest and Unknown MUST NOT trigger overlay changes.\n" +
        "Examples:\n" +
        "User: 我覺得有點頭暈\n" +
        "{\"intent\":\"ReportDiscomfort\",\"severity\":2,\"reply\":\"了解，我已收緊 FOV 並加深周邊遮罩以降低視覺刺激。\"}\n" +
        "User: 好多了，謝謝\n" +
        "{\"intent\":\"ReportComfort\",\"severity\":1,\"reply\":\"很好，系統會持續監測並逐步放寬保護。\"}\n" +
        "User: reset everything to default\n" +
        "{\"intent\":\"ResetRequest\",\"severity\":1,\"reply\":\"Done — overlay settings restored to default.\"}\n" +
        "User: I felt dizzy during last session, do you know what causes it?\n" +
        "{\"intent\":\"AnalyzeRequest\",\"severity\":1,\"reply\":\"Looking at recent events, pressure spiked to 220 and the FOV mask activated, after which you reduced FovRadius by 15%. High optical flow at that moment most likely triggered the dizziness.\"}";

    public MistralChatbotService(string modelPath, IAdjustmentLog? adjustmentLog = null, IEventLogStore? eventLog = null)
    {
        _modelPath = modelPath;
        _adjustmentLog = adjustmentLog;
        _eventLog = eventLog;
        _modelParams = new ModelParams(modelPath)
        {
            ContextSize = 2048,
            GpuLayerCount = 0,
        };
    }

    public bool IsModelAvailable => File.Exists(_modelPath);
    public bool IsModelLoaded { get; private set; }
    public IReadOnlyList<ChatMessage> History => _history.AsReadOnly();
    public string? LoadError { get; private set; }

    public async Task LoadAsync()
    {
        if (!IsModelAvailable || IsModelLoaded) return;
        try
        {
            await Task.Run(() =>
            {
                _weights = LLamaWeights.LoadFromFile(_modelParams);
                CreateNewSession();
            }).ConfigureAwait(false);
            IsModelLoaded = true;
            LoadError = null;
        }
        catch (Exception ex)
        {
            LoadError = $"{ex.GetType().Name}: {ex.Message}";
            throw;
        }
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
        if (IsModelLoaded && _session is not null)
        {
            string? raw = await AskModelForDecisionAsync(pureUserText, stateSummary, historyPrefix, ct).ConfigureAwait(false);
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
        if (IsModelLoaded) CreateNewSession();
    }

    private void CreateNewSession()
    {
        _context?.Dispose();
        _context = _weights!.CreateContext(_modelParams);
        var executor = new InteractiveExecutor(_context);
        var hist = new ChatHistory();
        hist.AddMessage(AuthorRole.System, SystemPrompt);
        _session = new ChatSession(executor, hist);
    }

    private async Task<string?> AskModelForDecisionAsync(string userText, string stateSummary, string historyPrefix, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CreateNewSession();
            var sb = new StringBuilder();
            var inferParams = new InferenceParams
            {
                MaxTokens = 192,
                AntiPrompts = new List<string> { "User:", "[INST]", "\n\nUser" },
            };

            var pb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(historyPrefix))
            {
                pb.Append(historyPrefix);
                if (!historyPrefix.EndsWith("\n", StringComparison.Ordinal)) pb.Append('\n');
            }
            pb.Append("State: ").Append(stateSummary).Append('\n')
              .Append("User: ").Append(userText).Append('\n')
              .Append("Return JSON only.");

            var msg = new ChatHistory.Message(AuthorRole.User, pb.ToString());

            await foreach (var token in _session!.ChatAsync(msg, true, inferParams, ct))
                sb.Append(token);
            return sb.ToString().Trim();
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
            if (intent is not ("ReportDiscomfort" or "ReportComfort" or "ResetRequest" or "AnalyzeRequest" or "Unknown"))
                return false;

            int severity = Math.Clamp(d.Severity, 1, 5);

            // Belt-and-suspenders: if lexicon detects reset cue, upgrade intent.
            var (_, _, _, reset) = FeedbackLexicon.ClassifyShaderHints(userText);
            if (reset && intent != "ResetRequest") intent = "ResetRequest";

            // AnalyzeRequest is an analytical question — never derive deltas from it.
            int fov; double md, gv, fw;
            if (intent == "AnalyzeRequest")
            {
                fov = 0; md = 0; gv = 0; fw = 0;
            }
            else
            {
                (fov, md, gv, fw) = FeedbackLexicon.DeriveAdjustments(intent, severity, userText);
            }

            string response = (d.Reply ?? string.Empty).Trim();
            if (response.Length == 0)
                response = FeedbackLexicon.BuildFallbackResponse(userText, intent);

            result = new NluResult
            {
                Intent = intent,
                FovDelta = fov,
                MaskDepthDelta = md,
                GridVisibilityDelta = gv,
                FeatherWidthDelta = fw,
                Response = response,
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
        _lock.Dispose();
        _context?.Dispose();
        _weights?.Dispose();
    }

    private sealed class ModelDecision
    {
        public string Intent { get; set; } = "Unknown";
        public int Severity { get; set; } = 1;
        public string Reply { get; set; } = string.Empty;
    }
}
