# R3-5 — Popup animation overhaul (execution brief, "focus fire")

**Status:** READY to implement (not started). Sibling commits R3-1…R3-4 landed; build green, suite 1681✓/3 skip on `alpha1-w2-engine-integrity`.
**Goal:** the popup reveal + condensed→full EXPAND read as smooth, NOT "flashy / jerky / jumpy".
**Constraint that frames everything:** the popup windows use `AllowsTransparency=True` (required for the frosted-glass acrylic) → WPF falls back to **software rendering** for the whole window. This is the hard ceiling: it cannot be "silky" like a GPU-composited app, BUT **RenderTransform / Opacity animations are still composited acceptably even in software mode**, whereas anything that triggers **layout or a native window resize per frame** janks. So the rule is: **animate only RenderTransform + Opacity; never animate size/layout mid-flight; never resize the native window per frame.**

## Verified root causes (from the 2026-06-18 investigation)
1. **Height animation = per-frame layout + native resize.** `AnimateSectionHeight` (PopupBarWindow.xaml.cs) animates `FrameworkElement.Height` 0↔natural on expand/collapse. Because the window is `SizeToContent="WidthAndHeight"`, every frame re-runs layout AND resizes the native HWND — the classic WPF window-resize jank → "chunky" growth.
2. **Re-anchor churn.** `ShellController` wires `_popup.SizeChanged += (_, _) => { PositionPopup(); _exclude.Update(); }` (~line 121). Each height-animation frame fires `SizeChanged` → `PositionPopup()` recomputes `Left`/`Top` to keep the bar centre-anchored → the bar visibly **jumps/re-anchors every frame**.
3. **Reveal flash.** `OnVisibleChanged` sets `BarRoot.Opacity = 0` then defers `AnimateReveal` to `DispatcherPriority.Loaded`. If a paint happens between `Show()` and the deferred start, the bar flashes at full opacity/size for one frame.
4. **(Ceiling) `AllowsTransparency` software render** — leave as-is (acrylic needs it); just stop feeding it layout/resize work.

## The refactor (files + exact changes)

### A. Replace the Height-based expand with a transform reveal — `Views/PopupBarWindow.xaml(.cs)`
- **Delete** the `Height` animation in `AnimateSectionHeight` / `AnimateExpandSections` (the `DoubleAnimation` on `HeightProperty` + the `DesiredSize` measure + the `s.Height = double.NaN` restore).
- **Keep** the section Visibility flip (Collapsed↔Visible) so the window does its size change in **ONE** layout pass (one native resize), not per-frame.
- **Animate the CONTENT instead:** give `MeterSection` / `ToolsSection` (and `DragHandle`) a `TranslateTransform` (+ reuse opacity) and animate `TranslateTransform.Y` from a small offset (e.g. +8) → 0 with `QuinticEase EaseOut` (~200 ms) on expand, reverse + then collapse on close. Transforms don't trigger layout → smooth in software mode. Wrap each section (or the StackPanel) in a `ClipToBounds` host so the translate doesn't overflow during the slide.
  - Sequence on expand: set sections `Visible` (1 layout pass → window grows once) → on the next `Loaded`-priority tick, run the translate+fade. On collapse: translate+fade out → `Completed` → set `Collapsed` (1 layout pass → window shrinks once).
- **Unify with the reveal** (`AnimateCard`): it already uses `ScaleTransform` + opacity (good — transform-based). Keep it; just make sure the reveal also doesn't fight the resize (see C).

### B. Suppress the re-anchor during animation — `Services/ShellController.cs`
- Add an `internal bool IsAnimating` (or an `AnimationStarted`/`AnimationCompleted` event) on `PopupBarWindow`; set it true while reveal/expand/collapse animations run.
- In the `_popup.SizeChanged` handler (~line 121): `if (!_popup.IsAnimating) PositionPopup();` and always call `PositionPopup()` once on `AnimationCompleted`. (Keep `_exclude.Update()` running — the WGC rect must stay current.)
- Alternative if the event is fiddly: compute the centre-anchored `Top` ONCE at animation start from the *final* (post-resize) `ActualHeight` (measure first), set it, and skip per-frame repositioning.

### C. Kill the reveal flash — `OnVisibleChanged`
- Use `BarRoot.Visibility = Hidden` (not just `Opacity = 0`) until the first animation frame, OR set the initial transform/opacity state synchronously **before** `Show()` returns so nothing paints at the target state. Then start `AnimateReveal` on the deferred tick.

### D. Cut per-frame raster cost — `PopupBarWindow.xaml`
- `CacheMode="BitmapCache"` on `BarRoot` (and/or the animated content host) so the software compositor reuses a cached bitmap during the opacity/transform animation instead of re-rasterizing the glass + text each frame. Verify it doesn't blur the text at the rest state (remove if it does).

## Risks / notes
- The expand still needs **one** native window resize (Collapsed→Visible). That single snap is fine; the jank was the *per-frame* resize — eliminated by animating transforms within the already-resized window.
- `CacheMode=BitmapCache` can soften text on a HiDPI/software-rendered surface — UVHS-check; drop if it degrades crispness.
- Re-anchor suppression must still reposition once at the end (or the bar drifts off-centre after expand).
- Do NOT remove `AllowsTransparency` (breaks the acrylic glass). The ceiling stays; the goal is "smooth enough", not GPU-silky.
- ReducedMotion: keep the existing snap branch (geometry/transform suppressed; the R3-4 neon opacity glows are RM-exempt and stay).

## Verification
- Build App + tests; split-run the suite (App build → tests `--no-dependencies` → filtered run + `MainWindowSmoke` alone). No new unit tests expected (animation is UVHS-only); ensure the smoke + popup-adjacent tests stay green.
- **UVHS (required — rendering not headless-verifiable):** reveal shows no first-frame flash; condensed→full expansion grows smoothly with no per-frame jitter and **no centre re-anchor jump**; collapse is smooth; the bar stays centre-anchored after expand; ReducedMotion ON → reveal/expand snap, neon glows still fade.

## After R3-5
`additional #4` — the **comprehensive animation audit** (every transition, with `/ui-ux-pro-max` + web research) is the user's explicit next focus once this targeted fix lands.
