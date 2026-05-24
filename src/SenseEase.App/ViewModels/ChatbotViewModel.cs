using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SenseEase.App.Configuration;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Storage;

namespace SenseEase.App.ViewModels;

/// <summary>View-model for the AI Assistant chatbot tab.</summary>
public sealed class ChatbotViewModel : INotifyPropertyChanged
{
    private readonly IChatbotService _chatbot;
    private readonly IInferenceEngine _engine;
    private readonly IAdjustmentLog _adjustmentLog;
    private readonly IEventLogStore? _eventLog;
    private readonly Dispatcher _dispatcher;
    private readonly Func<bool> _isOverlayVisible;
    private readonly FileChatHistoryStore _historyStore;
    private CancellationTokenSource _streamCts = new();

    /// <summary>How many recent adjustment / event records to embed in each model prompt.</summary>
    private const int HistoryWindow = 6;

    /// <summary>How many most-recent transcript bubbles to keep on disk. Older lines
    /// are dropped on startup so the file can't grow without bound.</summary>
    private const int PersistedHistoryMax = 100;

    /// <summary>Total cap on fake-stream animation duration, regardless of reply length.</summary>
    private const int FakeStreamMaxMs = 1500;
    private const int FakeStreamStepMs = 30;

    private bool _isGenerating;
    private string _inputText = string.Empty;
    private string _statusText = string.Empty;

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new();

    public bool IsGenerating
    {
        get => _isGenerating;
        private set
        {
            if (Set(ref _isGenerating, value))
                ((RelayCommand)SendCommand).RaiseCanExecuteChanged();
        }
    }

    public string InputText
    {
        get => _inputText;
        set
        {
            if (Set(ref _inputText, value))
                ((RelayCommand)SendCommand).RaiseCanExecuteChanged();
        }
    }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    public ICommand SendCommand { get; }
    public ICommand ClearCommand { get; }

    public ChatbotViewModel(
        IChatbotService chatbot,
        IInferenceEngine engine,
        IAdjustmentLog adjustmentLog,
        Func<bool> isOverlayVisible,
        IEventLogStore? eventLog = null)
    {
        _chatbot = chatbot;
        _engine = engine;
        _adjustmentLog = adjustmentLog;
        _eventLog = eventLog;
        _dispatcher = Application.Current.Dispatcher;
        _isOverlayVisible = isOverlayVisible;

        SendCommand = new RelayCommand(async _ => await SendAsync(),
            _ => !IsGenerating && !string.IsNullOrWhiteSpace(InputText));
        ClearCommand = new RelayCommand(_ => Clear());

        StatusText = chatbot.IsModelAvailable
            ? "Loading Ministral 3B… (first load ~30s on CPU; chat is disabled meanwhile)"
            : $"Model not found at: {AppConfig.ModelPath}";

        _ = InitModelAsync();

        // Phase 4: persist transcript across restarts. Load existing history first;
        // if any prior bubbles exist they replace the welcome (returning user — the
        // welcome bubble would be noise above their previous conversation).
        _historyStore = new FileChatHistoryStore(AppConfig.ChatHistoryPath);
        var prior = _historyStore.Load();
        if (prior.Count > PersistedHistoryMax)
        {
            _historyStore.Trim(PersistedHistoryMax);
            prior = _historyStore.Load();
        }

        if (prior.Count > 0)
        {
            foreach (var m in prior)
                Messages.Add(new ChatMessageViewModel(m.Content, m.Role));
        }
        else
        {
            Messages.Add(new ChatMessageViewModel(
                "您好，我是 SenseEase AI 助理。請描述您的視覺不適（暈眩、噁心、頭痛、眼睛疲勞），我會為您調整防護設定。\n" +
                "Hi — I'm the SenseEase AI assistant. Describe any visual discomfort and I'll adjust your overlay protection.",
                ChatRole.Assistant));
        }
    }

    /// <summary>Persist a non-transient bubble to the chat-history file. Safe to call
    /// for any real message; typing-indicator placeholders must NOT pass through here.</summary>
    private void PersistMessage(ChatRole role, string content)
    {
        if (string.IsNullOrEmpty(content)) return;
        _historyStore.Append(new ChatMessage
        {
            Role = role,
            Content = content,
            Timestamp = DateTime.Now,
        });
    }

    private async Task InitModelAsync()
    {
        try
        {
            await _chatbot.LoadAsync().ConfigureAwait(false);
            _dispatcher.Invoke(() =>
            {
                StatusText = _chatbot.IsModelLoaded
                    ? "Ministral 3B ready"
                    : $"Lexicon fallback (no GGUF found at: {AppConfig.ModelPath})";
                ((RelayCommand)SendCommand).RaiseCanExecuteChanged();
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Invoke(() =>
            {
                StatusText = $"Load failed: {ex.GetType().Name}: {ex.Message}";
                ((RelayCommand)SendCommand).RaiseCanExecuteChanged();
            });
        }
    }

    private async Task SendAsync()
    {
        string text = InputText.Trim();
        if (string.IsNullOrEmpty(text)) return;

        // Cancel any in-flight fake-stream animation from the prior turn so its
        // dispatcher updates can't overwrite this turn's bubble content.
        try { _streamCts.Cancel(); } catch { /* ignore */ }
        _streamCts.Dispose();
        _streamCts = new CancellationTokenSource();
        var streamToken = _streamCts.Token;

        InputText = string.Empty;
        IsGenerating = true;

        ChatMessageViewModel? placeholder = null;
        _dispatcher.Invoke(() =>
        {
            Messages.Add(new ChatMessageViewModel(text, ChatRole.User));
            PersistMessage(ChatRole.User, text);
            // Typing-indicator bubble: rendered as animated dots by the DataTemplate,
            // not as literal text. Removed when the real reply arrives. NOT persisted.
            placeholder = new ChatMessageViewModel(string.Empty, ChatRole.Assistant, isTyping: true);
            Messages.Add(placeholder);
        });

        try
        {
            string contextualInput = BuildContextualInput(text);

            var result = await _chatbot.SendAsync(contextualInput, CancellationToken.None).ConfigureAwait(false);
            ChatMessageViewModel? assistantBubble = null;
            string assistantContentForPersist = string.Empty;
            string deferredSummary = string.Empty;
            bool fakeStream = false;

            _dispatcher.Invoke(() =>
            {
                if (placeholder is not null) Messages.Remove(placeholder);
                string summary = ApplyNluResult(result, text);

                // Followup-requesting replies get rendered with option cards. The
                // assistant text is the question itself (FollowupQuestion); if the
                // model only set Response, fall back to that.
                bool hasFollowup = result.FollowupOptions is { Length: > 0 }
                                && !string.IsNullOrWhiteSpace(result.FollowupQuestion ?? result.Response);
                if (hasFollowup)
                {
                    string q = !string.IsNullOrWhiteSpace(result.FollowupQuestion)
                        ? result.FollowupQuestion!
                        : result.Response;
                    // Followup questions are NOT fake-streamed (must show with their option cards).
                    Messages.Add(new ChatMessageViewModel(
                        q,
                        ChatRole.Assistant,
                        followupOptions: result.FollowupOptions,
                        onFollowupSelected: AnswerWithFollowup));
                    PersistMessage(ChatRole.Assistant, q);
                }
                else
                {
                    // Only fake-stream lexicon-mode replies. When the real model is
                    // generating, Phase 5 will deliver true incremental streaming.
                    fakeStream = !_chatbot.IsModelLoaded && !string.IsNullOrEmpty(result.Response);
                    if (fakeStream)
                    {
                        assistantBubble = new ChatMessageViewModel(string.Empty, ChatRole.Assistant);
                        Messages.Add(assistantBubble);
                        assistantContentForPersist = result.Response;
                        // Persist the reply BEFORE the summary so reload order matches
                        // the rendered order. (Also acts as crash safety for the
                        // fake-stream window — file has the full reply even if the
                        // animation aborts.)
                        PersistMessage(ChatRole.Assistant, result.Response);
                    }
                    else
                    {
                        Messages.Add(new ChatMessageViewModel(result.Response, ChatRole.Assistant));
                        PersistMessage(ChatRole.Assistant, result.Response);
                    }
                }

                if (summary.Length > 0)
                {
                    // Persist the summary now so reload order stays user→reply→summary
                    // and a mid-animation crash doesn't lose it. The VISIBLE bubble is
                    // deferred until the fake-stream finishes (see below) so it never
                    // races ahead of the reply that explains it.
                    PersistMessage(ChatRole.Assistant, summary);
                    if (fakeStream)
                        deferredSummary = summary;
                    else
                        Messages.Add(new ChatMessageViewModel(summary, ChatRole.Assistant));
                }
            });

            if (fakeStream && assistantBubble is not null)
            {
                // Run the animation, THEN flip the deferred summary into the visible
                // collection on the dispatcher. Order: reply animation completes →
                // summary appears, matching what the user expects to read top-down.
                string summaryToShow = deferredSummary;
                _ = Task.Run(async () =>
                {
                    await FakeStreamAsync(assistantBubble, assistantContentForPersist, streamToken)
                          .ConfigureAwait(false);
                    if (streamToken.IsCancellationRequested) return;
                    if (summaryToShow.Length > 0)
                        _dispatcher.Invoke(() =>
                            Messages.Add(new ChatMessageViewModel(summaryToShow, ChatRole.Assistant)));
                });
            }
        }
        catch (Exception ex)
        {
            _dispatcher.Invoke(() =>
            {
                if (placeholder is not null) Messages.Remove(placeholder);
                Messages.Add(new ChatMessageViewModel($"[Error] {ex.Message}", ChatRole.Assistant));
            });
        }
        finally
        {
            _dispatcher.Invoke(() =>
            {
                if (placeholder is not null && Messages.Contains(placeholder))
                    Messages.Remove(placeholder);
                IsGenerating = false;
            });
        }
    }

    /// <summary>
    /// Animate <paramref name="full"/> into <paramref name="bubble"/>.Content in
    /// chunks so the typing-indicator gets visible airtime in lexicon-only mode.
    /// Total wall-clock duration is capped at <see cref="FakeStreamMaxMs"/>; chunk
    /// size scales with response length to honour that cap for long replies.
    /// Cooperatively cancellable so a follow-up Send aborts the prior animation.
    /// </summary>
    private async Task FakeStreamAsync(ChatMessageViewModel bubble, string full, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(full)) return;
        int maxSteps = Math.Max(1, FakeStreamMaxMs / FakeStreamStepMs);
        int chunkSize = Math.Max(3, (int)Math.Ceiling(full.Length / (double)maxSteps));

        try
        {
            for (int i = 0; i < full.Length; i += chunkSize)
            {
                if (ct.IsCancellationRequested) return;
                int end = Math.Min(full.Length, i + chunkSize);
                string slice = full.Substring(0, end);
                _dispatcher.Invoke(() => { if (!ct.IsCancellationRequested) bubble.Content = slice; });
                if (end >= full.Length) break;
                await Task.Delay(FakeStreamStepMs, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        finally
        {
            // On cancellation, snap to the full string so the UI never gets stuck
            // mid-word if the user immediately sends a new message.
            if (!ct.IsCancellationRequested)
                _dispatcher.Invoke(() => bubble.Content = full);
        }
    }

    /// <summary>
    /// Invoked when the user taps one of the followup option cards. Treats the
    /// chosen option as the next user message and runs it through SendAsync, so
    /// the model sees the option verbatim (and the picked card persists in the
    /// transcript as a normal "You: <option>" bubble). Public so the spike popup
    /// window can route its option clicks through the same path.
    /// </summary>
    public void SubmitFollowupAnswer(string option)
    {
        if (string.IsNullOrWhiteSpace(option) || IsGenerating) return;
        InputText = option;
        // Fire-and-forget — SendCommand handles its own dispatcher work.
        if (SendCommand.CanExecute(null)) SendCommand.Execute(null);
    }

    // Keep the old private name as a thin shim so call sites in the same VM
    // (the option-card constructor) read naturally.
    private void AnswerWithFollowup(string option) => SubmitFollowupAnswer(option);

    /// <summary>
    /// Inject a system-initiated assistant message that opens a followup picker.
    /// Used by the VIMS-spike popup path: the spike detector decides the bot
    /// should proactively ask "what are you feeling right now?" rather than
    /// waiting for the user to type. Safe to call from any thread.
    /// </summary>
    public void RaiseFollowup(string question, string[] options)
    {
        if (string.IsNullOrWhiteSpace(question) || options is null || options.Length == 0) return;
        _dispatcher.Invoke(() =>
        {
            Messages.Add(new ChatMessageViewModel(
                question,
                ChatRole.Assistant,
                followupOptions: options,
                onFollowupSelected: AnswerWithFollowup));
            PersistMessage(ChatRole.Assistant, question);
        });
    }

    /// <summary>
    /// Build the state-annotated model input. Prepends terse [RecentAdjustments] and
    /// [RecentEvents] blocks (oldest-first, capped at <see cref="HistoryWindow"/>) so
    /// the chatbot NLU can reason about past sessions for analytical questions like
    /// "what caused the dizziness in the last session?". Empty sources are gracefully
    /// omitted (new install -> no prefix), keeping the previous behaviour as a baseline.
    /// </summary>
    private string BuildContextualInput(string text)
    {
        var sb = new StringBuilder();
        AppendHistoryPrefix(sb);

        var sh = _engine.Current.Shader;
        var bl = _engine.BaselineShader;
        sb.Append("[SenseEaseState mode=").Append(_engine.Current.Mode)
          .Append(" ofi=").Append(_engine.Current.OpticalFlowIndex.ToString("F1"))
          .Append(" pressure=").Append(_engine.Current.Pressure.ToString("F1"))
          .Append(" fov=").Append(_engine.Current.FovRadiusPercent)
          .Append(" mask=").Append(_engine.Current.MaskActive ? "on" : "off")
          .Append(" shader(maskDepth=").Append(sh.MaskDepth.ToString("F2"))
          .Append(" gridVis=").Append(sh.GridVisibility.ToString("F2"))
          .Append(" feather=").Append(sh.FeatherWidth.ToString("F2")).Append(')')
          .Append(" defaults(maskDepth=").Append(bl.MaskDepth.ToString("F2"))
          .Append(" gridVis=").Append(bl.GridVisibility.ToString("F2"))
          .Append(" feather=").Append(bl.FeatherWidth.ToString("F2")).Append(")]\n")
          .Append("User: ").Append(text);
        return sb.ToString();
    }

    /// <summary>
    /// Append [RecentAdjustments] and [RecentEvents] sections (oldest-first), if either has data.
    /// Each line is terse and human-readable so it doubles as a debug log in the transcript.
    /// </summary>
    private void AppendHistoryPrefix(StringBuilder sb)
    {
        var adjustments = _adjustmentLog?.All();
        if (adjustments is { Count: > 0 })
        {
            int skip = Math.Max(0, adjustments.Count - HistoryWindow);
            sb.AppendLine("[RecentAdjustments]");
            for (int i = skip; i < adjustments.Count; i++)
            {
                var r = adjustments[i];
                string sign = r.Delta >= 0 ? "+" : string.Empty;
                string utter = string.IsNullOrEmpty(r.Utterance) ? string.Empty : $" \"{Sanitize(r.Utterance)}\"";
                sb.Append(r.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))
                  .Append(' ').Append(r.Parameter)
                  .Append(' ').Append(sign).Append(r.Delta.ToString("0.##"))
                  .Append(' ').Append(r.Source)
                  .AppendLine(utter);
            }
        }

        var events = _eventLog?.Recent(); // most-recent-first
        if (events is { Count: > 0 })
        {
            int take = Math.Min(HistoryWindow, events.Count);
            sb.AppendLine("[RecentEvents]");
            for (int i = take - 1; i >= 0; i--) // render oldest-first
            {
                var e = events[i];
                sb.Append(e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"))
                  .Append(' ').Append(e.Category)
                  .Append(" \"").Append(Sanitize(e.Message)).Append('"')
                  .AppendLine();
            }
        }
    }

    private static string Sanitize(string s)
        => string.IsNullOrEmpty(s) ? string.Empty : s.Replace('"', '\'').Replace("\r", " ").Replace("\n", " ");

    /// <summary>
    /// Apply an NLU result to the engine, persist every adjustment for personalization,
    /// and return a one-line human summary of what was ACTUALLY applied (not merely
    /// requested), for an in-chat feedback bubble.
    /// </summary>
    private string ApplyNluResult(NluResult result, string utterance)
    {
        if (result.Intent == "ResetRequest")
        {
            _engine.ResetPersonalization();
            _adjustmentLog.Append(new AdjustmentRecord
            {
                Parameter = "Reset", Source = "reset", Utterance = utterance,
            });
            AdjustmentApplied?.Invoke(this, EventArgs.Empty);
            string resetMsg = "⚙ 已重設所有個人化設定 / Overlay settings reset to default";
            return _isOverlayVisible() ? resetMsg : resetMsg + "（開啟 Overlay 以查看 / open the overlay to see it）";
        }

        // Each tuple: (label, formatted-change-string). Only populated for non-zero
        // deltas so the resulting pseudo-table never contains "no change" rows.
        var rows = new System.Collections.Generic.List<(string Label, string Change)>();

        if (result.FovDelta != 0)
        {
            int oldOffset = _engine.UserFovOffset;
            _engine.ApplyUserFeedback(result.FovDelta);
            int newOffset = _engine.UserFovOffset;
            int appliedFov = newOffset - oldOffset;
            if (appliedFov != 0)
            {
                _adjustmentLog.Append(new AdjustmentRecord
                {
                    Parameter = "FovRadius", Delta = appliedFov,
                    OldValue = oldOffset, NewValue = newOffset,
                    Source = "user", Utterance = utterance,
                });
                rows.Add(("FOV", $"{(appliedFov > 0 ? "+" : "")}{appliedFov}%"));
            }
        }

        bool hasShaderDelta = result.MaskDepthDelta != 0
                           || result.GridVisibilityDelta != 0
                           || result.FeatherWidthDelta != 0;
        if (hasShaderDelta)
        {
            var before = _engine.Current.Shader.Clone();
            _engine.ApplyShaderAdjustment(result.MaskDepthDelta, result.GridVisibilityDelta, result.FeatherWidthDelta);
            var after = _engine.Current.Shader.Clone();
            LogShader("MaskDepth",      result.MaskDepthDelta,      before.MaskDepth,      after.MaskDepth,      utterance);
            LogShader("GridVisibility", result.GridVisibilityDelta, before.GridVisibility, after.GridVisibility, utterance);
            LogShader("FeatherWidth",   result.FeatherWidthDelta,   before.FeatherWidth,   after.FeatherWidth,   utterance);

            double appliedMaskDepth      = after.MaskDepth      - before.MaskDepth;
            double appliedGridVisibility = after.GridVisibility - before.GridVisibility;
            double appliedFeatherWidth   = after.FeatherWidth   - before.FeatherWidth;

            if (appliedMaskDepth != 0)
                rows.Add(("Mask depth", $"{(appliedMaskDepth > 0 ? "+" : "")}{appliedMaskDepth:0.00}"));
            if (appliedGridVisibility != 0)
                rows.Add(("Grid",       $"{(appliedGridVisibility > 0 ? "+" : "")}{appliedGridVisibility:0.00}"));
            if (appliedFeatherWidth != 0)
                rows.Add(("Feather",    $"{(appliedFeatherWidth > 0 ? "+" : "")}{appliedFeatherWidth:0.00}"));
        }

        AdjustmentApplied?.Invoke(this, EventArgs.Empty);

        if (rows.Count == 0) return string.Empty;

        // MarkdownInlines (Views/MarkdownInlines.cs) only supports bold / italic /
        // inline-code / bullets / linebreaks — it does NOT render markdown tables.
        // So we emit a backtick-wrapped monospace pseudo-table: the label column is
        // padded to a fixed width so values align vertically in the chat bubble.
        int labelWidth = 0;
        foreach (var r in rows) if (r.Label.Length > labelWidth) labelWidth = r.Label.Length;
        labelWidth += 2; // gutter between columns

        var sb = new StringBuilder();
        sb.Append("**系統調整 / System adjustment**\n");
        foreach (var (label, change) in rows)
            sb.Append('`').Append(label.PadRight(labelWidth)).Append(change).Append("`\n");
        if (!_isOverlayVisible())
            sb.Append("\n_開啟 Overlay 以查看 / Open the overlay to see it_");
        return sb.ToString();
    }

    private void LogShader(string parameter, double delta, double oldValue, double newValue, string utterance)
    {
        if (delta == 0) return;
        _adjustmentLog.Append(new AdjustmentRecord
        {
            Parameter = parameter, Delta = delta,
            OldValue = oldValue, NewValue = newValue,
            Source = "user", Utterance = utterance,
        });
    }

    private void Clear()
    {
        _chatbot.ClearHistory();
        Messages.Clear();
        try { _streamCts.Cancel(); } catch { /* ignore */ }
        _historyStore.DeleteFile();
    }

    /// <summary>Raised after an NLU result mutates the shared engine, so the host can refresh rendering.</summary>
    public event EventHandler? AdjustmentApplied;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
        return true;
    }
}

public sealed class ChatMessageViewModel : INotifyPropertyChanged
{
    private string _content;
    private bool _isTyping;

    public ChatRole Role { get; }
    public string Author => Role == ChatRole.User ? "You" : "SenseEase AI";
    public bool IsUser => Role == ChatRole.User;
    public string Timestamp { get; }
    public Visibility UserVisibility => IsUser ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AssistantVisibility => IsUser ? Visibility.Collapsed : Visibility.Visible;

    public string Content
    {
        get => _content;
        set
        {
            if (_content == value) return;
            _content = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Content)));
        }
    }

    /// <summary>
    /// When true, the bubble shows an animated "thinking" indicator instead of
    /// the Content text. Used by the placeholder bubble during model/lexicon work
    /// so the user has visible feedback that something is happening.
    /// </summary>
    public bool IsTyping
    {
        get => _isTyping;
        set
        {
            if (_isTyping == value) return;
            _isTyping = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTyping)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TypingVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ContentVisibility)));
        }
    }

    /// <summary>2–4 quick-reply option strings shown as clickable cards beneath the
    /// assistant message. Null/empty when no followup is offered.</summary>
    public string[]? FollowupOptions { get; }

    public Visibility FollowupVisibility =>
        FollowupOptions is { Length: > 0 } && !IsUser ? Visibility.Visible : Visibility.Collapsed;

    public Visibility TypingVisibility => IsTyping ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ContentVisibility => IsTyping ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Invoked when the user taps one of the followup option cards.
    /// Receives the chosen option's display string. Null on messages with no
    /// followup options (the visibility binding prevents that path).</summary>
    public Action<string>? OnFollowupSelected { get; }

    public ICommand SelectFollowupCommand { get; }

    public ChatMessageViewModel(
        string content,
        ChatRole role,
        string[]? followupOptions = null,
        Action<string>? onFollowupSelected = null,
        bool isTyping = false)
    {
        _content = content ?? string.Empty;
        Role = role;
        Timestamp = DateTime.Now.ToString("HH:mm");
        FollowupOptions = followupOptions;
        OnFollowupSelected = onFollowupSelected;
        _isTyping = isTyping;
        SelectFollowupCommand = new RelayCommand(o =>
        {
            if (o is string s && OnFollowupSelected is not null) OnFollowupSelected(s);
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
