namespace SenseEase.Core.Inference;

/// <summary>Lifecycle phase of the <see cref="HabituationEngine"/>.</summary>
public enum HabituationPhase
{
    /// <summary>Waiting for a sustained calm window before relaxation may begin.</summary>
    Observing,
    /// <summary>Calm window satisfied; the FOV is being gradually relaxed.</summary>
    Relaxing,
    /// <summary>Backed off after a discomfort recurrence, or manually paused.</summary>
    Paused,
}
