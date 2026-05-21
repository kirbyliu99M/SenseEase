using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;

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

    /// <summary>How many recent adjustment / event records to embed in each model prompt.</summary>
    private const int HistoryWindow = 6;

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
            ? "Loading Mistral 3B… (first load ~30s on CPU; chat is disabled meanwhile)"
            : "Model not found. Place a Q4_K_M .gguf at: models\\mistral-3b\\model.gguf";

        _ = InitModelAsync();

        Messages.Add(new ChatMessageViewModel(
            "您好，我是 SenseEase AI 助理。請描述您的視覺不適（暈眩、噁心、頭痛、眼睛疲勞），我會為您調整防護設定。\n" +
            "Hi — I'm the SenseEase AI assistant. Describe any visual discomfort and I'll adjust your overlay protection.",
            ChatRole.Assistant));
    }

    private async Task InitModelAsync()
    {
        try
        {
            await _chatbot.LoadAsync().ConfigureAwait(false);
            _dispatcher.Invoke(() =>
            {
                StatusText = "Mistral 3B ready";
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

        InputText = string.Empty;
        IsGenerating = true;

        ChatMessageViewModel? placeholder = null;
        _dispatcher.Invoke(() =>
        {
            Messages.Add(new ChatMessageViewModel(text, ChatRole.User));
            placeholder = new ChatMessageViewModel("SenseEase AI 正在思考… / thinking…", ChatRole.Assistant);
            Messages.Add(placeholder);
        });

        try
        {
            string contextualInput =
                BuildContextualInput(text);

            var result = await _chatbot.SendAsync(contextualInput, CancellationToken.None).ConfigureAwait(false);
            _dispatcher.Invoke(() =>
            {
                if (placeholder is not null) Messages.Remove(placeholder);
                string summary = ApplyNluResult(result, text);
                Messages.Add(new ChatMessageViewModel(result.Response, ChatRole.Assistant));
                if (summary.Length > 0)
                    Messages.Add(new ChatMessageViewModel(summary, ChatRole.Assistant));
            });
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

        var parts = new System.Collections.Generic.List<string>();

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
                parts.Add($"FOV {appliedFov}%");
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
                parts.Add($"遮罩 mask {(appliedMaskDepth > 0 ? "+" : "")}{appliedMaskDepth:0.00}");
            if (appliedGridVisibility != 0)
                parts.Add($"格線 grid {(appliedGridVisibility > 0 ? "+" : "")}{appliedGridVisibility:0.00}");
            if (appliedFeatherWidth != 0)
                parts.Add($"邊緣 feather {(appliedFeatherWidth > 0 ? "+" : "")}{appliedFeatherWidth:0.00}");
        }

        AdjustmentApplied?.Invoke(this, EventArgs.Empty);

        if (parts.Count == 0) return string.Empty;

        string summary = "⚙ 已調整 / adjusted: " + string.Join("、", parts);
        if (!_isOverlayVisible()) summary += "（開啟 Overlay 以查看 / open the overlay to see it）";
        return summary;
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

public sealed class ChatMessageViewModel
{
    public string Content { get; }
    public ChatRole Role { get; }
    public string Author => Role == ChatRole.User ? "You" : "SenseEase AI";
    public bool IsUser => Role == ChatRole.User;
    public string Timestamp { get; }
    public Visibility UserVisibility => IsUser ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AssistantVisibility => IsUser ? Visibility.Collapsed : Visibility.Visible;

    public ChatMessageViewModel(string content, ChatRole role)
    {
        Content = content;
        Role = role;
        Timestamp = DateTime.Now.ToString("HH:mm");
    }
}
