namespace SenseEase.App.ViewModels;

/// <summary>
/// D3 liveness / trust contract: the single 4-value liveness scalar, orthogonal
/// to the VIMS comfort tier. Derived in exactly one place
/// (<see cref="MainViewModel.RecomputeLiveness"/>) from existing signals
/// (sensing health, power-save, mask-active) and fanned out in lock-step to the
/// three ambient surfaces — bar, tray, card — exactly as <c>VimsTier</c> is.
/// Bar/tray/card only ever READ it; none re-derive it.
///
/// <para>The whole point of the contract: a dead sensing pipeline must STOP
/// looking alive. Healthy states reuse the existing tier colour (no new visual);
/// <see cref="PausedByUser"/> reuses the shipped dashed sleep treatment;
/// <see cref="Faulted"/> is the only genuinely new visual and must be
/// statically, visibly different from a frozen-but-healthy bar.</para>
///
/// <para><see cref="SteadyWatching"/> is value 0 so the enum default (and the
/// VM's initial state under the bare <c>new Application()</c> smoke path) is a
/// healthy, no-new-visual state — App.Resources stays untouched.</para>
/// </summary>
public enum LivenessState
{
    /// <summary>Sensing live, screen calm, mask at baseline floor (tier-1 / 0% alpha).
    /// Healthy. Reuses the existing faint grey tier-1 brush — no new visual.</summary>
    SteadyWatching = 0,

    /// <summary>Sensing live AND mask actively dimming (MaskActive true / tier ≥ 2 above
    /// floor). Healthy. Reuses the existing tier brush — no new visual.</summary>
    Protecting = 1,

    /// <summary>User chose Power Save; sensing intentionally off. Reuses the shipped
    /// dashed sleep outline. Calm-BY-CHOICE — must never be confusable with Faulted.</summary>
    PausedByUser = 2,

    /// <summary>Sensing / sidecar / engine died unexpectedly. Calm-BY-ACCIDENT. The
    /// only new visual: a static low-alpha clay fill + solid (non-dashed) fault outline,
    /// surfaced only after one silent self-heal fails. Reads as "needs a tap," never alarm.</summary>
    Faulted = 3,
}
