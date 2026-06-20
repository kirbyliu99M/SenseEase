namespace SenseEase.Core.AdaptiveFit;

/// <summary>
/// The outcome of one <see cref="DeterministicSetSelector"/> evaluation (Alpha-2 P3/P4
/// GROUP C). A closed two-case sum type:
/// <list type="bullet">
///   <item><see cref="Hold"/> — stay on the current set (the anti-overfit default: thin
///   data, flat/neutral stats, a sub-margin argmax, or a manual-conflict suppression all
///   resolve to Hold).</item>
///   <item><see cref="MoveTo"/> — adopt the named adjacent <see cref="MoveTo.Target"/>
///   (always at Manhattan distance 1 from the current cell by construction).</item>
/// </list>
///
/// <para>Pure Core — no engine / WPF dependency, immutable, value-equatable. Read only
/// behind the <c>AppConfig.AdaptiveFitSelectorEnabled</c> flag (default TRUE since c1461a9).</para>
/// </summary>
public abstract record SelectorDecision
{
    // Private ctor: the only two concrete cases live here, so the hierarchy stays closed
    // (no out-of-tree subclass can introduce a third state the engine doesn't handle).
    private SelectorDecision() { }

    /// <summary>Stay on the current set — the conservative default.</summary>
    public sealed record Hold : SelectorDecision;

    /// <summary>Move to <paramref name="Target"/>, an adjacent (distance-1) set.</summary>
    public sealed record MoveTo(GridCoord Target) : SelectorDecision;

    /// <summary>The shared, allocation-free Hold singleton (Hold carries no state).</summary>
    public static readonly SelectorDecision HoldDecision = new Hold();
}
