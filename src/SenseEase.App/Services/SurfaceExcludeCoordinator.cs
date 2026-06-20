using System;
using System.Collections.Generic;

namespace SenseEase.App.Services;

/// <summary>
/// UI-RESHAPE — coordinates the WGC mask-exclude rects for the reshape's simultaneously-visible
/// surfaces (edge sidebar always-on; popup bar / chat / NPS on demand). The single-rect
/// <c>WgcSensingProvider.SetMonitorExcludeRect</c> can't hold several at once, so each surface
/// registers here while visible and the coordinator pushes the FULL list (the union) to the
/// engine via the injected sink (<c>MainViewModel.SetWgcMonitorExcludeRects</c>), which the OFI
/// loop zeroes alongside the legacy bar/card rect. Satisfies the always-on mask invariant
/// (docs/POPUP-OVERLAY.md §3) for the new surfaces without disturbing MainWindow's bar/card path.
///
/// <para>Surfaces call <see cref="Register"/> on show / <c>ContentRendered</c>,
/// <see cref="Update"/> on move/resize, and <see cref="Unregister"/> on hide (Closed is handled
/// automatically). Thread-safe; the sink is invoked on the caller's thread.</para>
/// </summary>
public sealed class SurfaceExcludeCoordinator
{
    private readonly Action<IReadOnlyList<(int Left, int Top, int Width, int Height)>?> _sink;
    private readonly HashSet<IShellSurface> _surfaces = new();
    private readonly object _lock = new();

    public SurfaceExcludeCoordinator(Action<IReadOnlyList<(int Left, int Top, int Width, int Height)>?> sink)
        => _sink = sink ?? throw new ArgumentNullException(nameof(sink));

    /// <summary>Register a visible surface (idempotent). Auto-unregisters on its Closed.</summary>
    public void Register(IShellSurface surface)
    {
        if (surface is null) return;
        lock (_lock) { if (!_surfaces.Add(surface)) return; }
        surface.Closed += OnSurfaceClosed;
        Refresh();
    }

    /// <summary>Unregister a surface (e.g. when hidden). Idempotent.</summary>
    public void Unregister(IShellSurface surface)
    {
        if (surface is null) return;
        lock (_lock) { if (!_surfaces.Remove(surface)) return; }
        surface.Closed -= OnSurfaceClosed;
        Refresh();
    }

    /// <summary>Re-read every registered surface's rect and push the union (call on move/resize).</summary>
    public void Update() => Refresh();

    /// <summary>Number of currently-registered surfaces (diagnostics/tests).</summary>
    public int Count { get { lock (_lock) return _surfaces.Count; } }

    private void OnSurfaceClosed(object? sender, EventArgs e)
    {
        if (sender is IShellSurface s) Unregister(s);
    }

    private void Refresh()
    {
        List<(int, int, int, int)> rects;
        lock (_lock)
        {
            rects = new List<(int, int, int, int)>(_surfaces.Count);
            foreach (var s in _surfaces)
            {
                try
                {
                    var r = s.GetMonitorRectInPhysicalPixels();
                    if (r.Width > 0 && r.Height > 0) rects.Add((r.Left, r.Top, r.Width, r.Height));
                }
                catch { /* a surface mid-teardown can't report — skip it this pass */ }
            }
        }
        // Hardening (2026-06-21): a sink throw (a surface mid-teardown / a disposed WGC provider)
        // must never propagate out of Refresh — Refresh runs from window Closed / IsVisibleChanged
        // handlers (ShellController), and an unhandled throw there reaches
        // App.DispatcherUnhandledException, which does NOT set e.Handled → the process terminates.
        // Swallow so a register/unregister storm during the settings-open teardown can't hard-crash.
        try { _sink(rects.Count == 0 ? null : rects); }
        catch { /* fail-soft: the next Update() pushes a correct union */ }
    }
}
