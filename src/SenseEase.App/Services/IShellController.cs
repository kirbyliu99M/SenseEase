using System;

namespace SenseEase.App.Services;

/// <summary>
/// UI-RESHAPE prep — the decoupling seam between the new discreet surfaces and the
/// engine/host. The future <c>ShellController</c> (built after the B5 Adaptation-Gate
/// work lands) will implement this, owning the single <c>MainViewModel</c> and the
/// surface lifecycle, so the launch-time console (<c>MainWindow</c>) can be deleted
/// later with minimal churn. The new windows depend ONLY on this interface — never on
/// <c>MainWindow</c> or the view-models directly.
///
/// <para>Kept engine-type-free (primitives only) so the prep scaffold compiles with
/// zero coupling to the engine layer B5 is changing. Each command maps to an existing
/// <c>MainViewModel</c> member, wired post-B5:</para>
/// <list type="bullet">
///   <item><see cref="PlayPause"/> → <c>SetSensingModeToWgc()</c> / power-save toggle</item>
///   <item><see cref="NudgeStronger"/> → <c>EaseView()</c> (tighten, deliberate-preference)</item>
///   <item><see cref="NudgeGentler"/> → <c>WidenView()</c> (loosen, deliberate-preference)</item>
/// </list>
/// </summary>
public interface IShellController
{
    /// <summary>Current engine VIMS tier (1–12, read-only — the engine owns it).</summary>
    int CurrentTier { get; }

    /// <summary>True while protection is paused / power-saving.</summary>
    bool IsPaused { get; }

    /// <summary>Raised when <see cref="CurrentTier"/> or <see cref="IsPaused"/> change,
    /// so surfaces refresh their pulse / play-pause affordances.</summary>
    event EventHandler? StateChanged;

    /// <summary>Toggle protection on/off (start sensing or power-save).</summary>
    void PlayPause();

    /// <summary>Deliberate-preference nudge toward MORE protection (tighten the FOV).</summary>
    void NudgeStronger();

    /// <summary>Deliberate-preference nudge toward LESS protection (widen the FOV).</summary>
    void NudgeGentler();

    /// <summary>Reveal the AI chat panel.</summary>
    void OpenChat();

    /// <summary>Open the full settings surface.</summary>
    void OpenSettings();

    /// <summary>Open the event-log surface.</summary>
    void OpenLogs();

    /// <summary>Open the legacy full dashboard console (transitional; retires with MainWindow).</summary>
    void OpenConsole();

    /// <summary>Register a surface for the WGC exclude-rect union + topmost fan-out.</summary>
    void RegisterSurface(IShellSurface surface);

    /// <summary>Unregister a surface (called on its Closed).</summary>
    void UnregisterSurface(IShellSurface surface);

    // ── UI-RESHAPE iter — quick-settings (two-way, persisted) + LLM status ──
    /// <summary>錨點格線 — the Chang-Hain anchor-grid overlay lines on/off.</summary>
    bool AnchorGridEnabled { get; set; }
    /// <summary>漸進適應 — graded-exposure / habituation on/off.</summary>
    bool GradedExposureEnabled { get; set; }
    /// <summary>常駐遮罩 — the always-on resting overlay mask on/off.</summary>
    bool PersistentMaskEnabled { get; set; }
    /// <summary>True when the local chat LLM is loaded + available (drives the chat-header dot).</summary>
    bool IsLlmOnline { get; }
}
