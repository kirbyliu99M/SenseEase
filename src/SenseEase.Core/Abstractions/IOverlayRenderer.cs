using SenseEase.Core.Models;

namespace SenseEase.Core.Abstractions;

/// <summary>
/// Rendering layer contract for the FOV mask / anchor-grid overlay.
/// MVP: WPF transparent overlay window. Future: DirectX 12 renderer.
/// </summary>
public interface IOverlayRenderer
{
    bool IsVisible { get; }

    void Show();
    void Hide();

    /// <summary>Update the rendered overlay from the current state (FOV radius, grid, mask).</summary>
    void ApplyState(SenseState state);
}
