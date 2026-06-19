# Popup animation contract (R3-7 / R3-7b) — DO NOT break without coordinating

The flash-free popup glass-bar animation (commit `e07e06d`) is hard-won. It rests on structural
invariants that **do not show up in a normal diff**, so they are easy to break by accident while fixing
something unrelated. This file is the canonical contract; the guard is
[`tests/SenseEase.Tests/AnimationContractTests.cs`](../tests/SenseEase.Tests/AnimationContractTests.cs)
(a static lint that fails CI if an invariant is violated). The full "why" is in the `motion-design-thesis`
memory note.

**Rule of thumb:** if you change the animation deliberately, update the guard test + this doc in the
**same commit** — never just delete a failing check.

## The invariants

1. **Fixed window — never `SizeToContent`.** `PopupBarWindow` is `SizeToContent="Manual"`, explicit
   `Width="360" Height="496"`, `ResizeMode="NoResize"`, `AllowsTransparency="True"`. Resizing a layered
   (AllowsTransparency) window **re-allocates its surface = a flash**. The content lays out *inside* the
   fixed window (`RootGrid` right-aligned + vertically centred); panels open into already-allocated
   transparent, click-through space. New failure mode: content that grows taller than **496px clips** —
   it cannot push the window bigger. *(Guard: `Inv1_*`.)*

2. **No `Effect` / `BitmapCache` / `CacheMode` on the popup.** Under software rendering an Effect
   (`DropShadowEffect`, `BlurEffect`) or a `BitmapCache` allocates an offscreen render target that
   disables ClearType and flashes on every interaction. R3-6 removed them deliberately. Use a static
   `Border` / gradient (e.g. a `RadialGradientBrush` halo whose Opacity toggles) instead of a shadow or
   blur. *(Guard: `Inv2_*`.)*

3. **Don't touch the positioning math.** `RootGrid` is `HorizontalAlignment=Right`
   `VerticalAlignment=Center`. `ShellController.PositionPopup` docks flush-right + sets the window so its
   centre = the saved bar centre, with the `barHalf` clamp; `OnAutoCloseTick` proximity and the WGC
   exclude rect measure the **RootGrid content rect**, not the window rect. Keep all three consistent.

4. **Reveal = hidden-not-opacity, deferred.** On show, `OnVisibleChanged` sets `BarRoot.Visibility=Hidden`
   then defers `AnimateReveal` to `DispatcherPriority.Loaded` so layout settles first.

5. **Expand = single-pass.** `MeterSection/ToolsSection/DragHandle` flip Visible in one measure pass, then
   the **glass Height** grows on a capped cadence (`ExpandFps=30`, `CubicEase`) while the **content**
   fades + slides (`MeterShift/ToolsShift/DragShift` TranslateTransform `SectionSlide=8→0`) on a smooth
   uncapped composited animation — the eye tracks the fluid slide and stops reading the capped height
   steps. A single hooked `Completed` restores `Height=NaN` and re-checks `vm.IsExpanded` (latest-call
   wins). Height animation is safe **only because the window is fixed**. *(So `MeterShift/ToolsShift/DragShift`
   are live, not dead.)*

6. **Clean, flash-free hide — route every dismiss through `DismissToHandle()`.** `ResetAndHide` resets the
   VM flags (`IsChatOpen/IsSettingsOpen/IsExpanded=false`) then `HideToCondensed` → `ForceCondensedBaseline`
   blanks `BarRoot` and lets it render ~2 frames (`CompositionTarget.Rendering`, guarded by `_pendingHide`)
   **before** `Hide()`, so the layered surface isn't left holding a stale expanded bitmap (the reopen flash).
   A raw `_popup.Hide()` reintroduces the flash **and** skips the VM reset. The hotkey path
   (`ShellController.ToggleGlassPopupBar`) and click-away both go through `DismissToHandle()`.
   *(Guard: `Inv6_*`, `Inv4_*`.)*

7. **One panel at a time.** Chat/settings open via `OpenPanelCleanly` (sequenced; never two animating at
   once); chat-open calls `Activate()` for caret focus.

8. **Drag is Y-only, right-locked**, persists `PopupBarCenterY` (resize-invariant), gated to the `BarRoot`
   subtree, yields to interactive controls.

9. **ReducedMotion snaps everything.** Every animated path (`AnimateExpandSections`, `AnimateCard`,
   `CollapseToHandle` fade, EdgeSidebar `AnimateReveal`, `HideToCondensed`) checks
   `AccessibilityHelper.ReducedMotion()` and snaps. Opacity-only neon/typing-dot glows are RM-exempt
   (WCAG 2.3.3) and stay. *(Guard: `Inv4_*` asserts the gate count.)*

## Named elements the animation binds (don't rename/remove)

`RootGrid, BarRoot, BarScale, ChatPanel, ChatScale, SettingsFlyout, SettingsScale, MeterSection,
ToolsSection, DragHandle, MeterShift, ToolsShift, DragShift, chatNeonRim, barNeonRim` (popup), and
`Handle, HandleScale` (edge sidebar). *(Guard: `Inv5_*`, `EdgeSidebar_*`.)*

## Applying the code-review bug fixes safely

The tri-lens audit fixes were checked against this contract (workflow `wl228m5ob`). Most are inert
(non-animated windows / token / copy). The ones that touch a load-bearing seam — and must be verified
against the 496px ceiling or the hide path — are flagged in the `ui-reshape-popup-prep` memory note.
After any fix that touches an animated surface's measured size or the hide path, run the **unsplit** test
suite and smoke-test reveal → expand → collapse → hide → reveal in normal **and** Reduced-Motion.
