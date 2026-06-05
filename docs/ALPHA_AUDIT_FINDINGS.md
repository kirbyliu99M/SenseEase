# Alpha-1 — Running Audit Findings

> Findings surfaced during build that the product owner deferred to the
> **total advising audit at the end of the Alpha plan**. Each entry: what it is,
> why it matters, the recommended fix, and status. Do not silently auto-fix
> behavior items here without sign-off.

---

## AF-1 — "緩和畫面" (notification-card primary button) function mismatch / no-op
- **Surfaced:** 2026-06-06, during D2 card UVHS.
- **Where:** `src/SenseEase.App/MainWindow.xaml.cs:925-934` (`EngageClicked` handler);
  button is `EngageBtn` in `Views/NotificationCardWindow.xaml`, raised by `OnEngageClicked`.
- **Current behavior:** on click → `SetSensingModeToWgc()` + `OverlayVisible = true`.
- **Problem:**
  1. **No-op in the normal running state** — when the overlay is already visible and
     WGC is already the sensing mode (the default while running), both lines do
     nothing, so the card's *primary* button does nothing visible.
  2. **Label overpromises** — "緩和畫面 / ease my view now" implies an immediate visible
     softening, but at a calm tier the overlay is near-transparent ("nothing dims at
     rest"), and switching the sensing *mode* is an invisible backend side-effect
     unrelated to the user's "ease" intent. (The old label 啟動保護 / "activate
     protection" matched the engage-sensing-and-overlay function; the D2 rename
     exposed the gap.)
- **Recommended fix:** wire the button to the **D5 deliberate-preference** path —
  `engine.ApplyUserFeedback(-delta, isDeliberatePreference: true)` — so each press
  visibly tightens the FOV a notch as a standing preference (the canonical D5 use
  case), without counting as discomfort or eroding habituation. Decide the exact
  delta and whether to keep the sensing-mode switch / overlay-ensure as a secondary
  effect during the audit.
- **Status:** RESOLVED 2026-06-06. `MainViewModel.EaseView()` routes the button
  through the D5 deliberate-preference path (`ApplyUserFeedback(-12,
  isDeliberatePreference: true)`) + refresh, so each press visibly tightens the FOV
  a notch without counting as discomfort / eroding the offset; the EngageClicked
  handler calls it after ensuring WGC + overlay. `EaseViewTests` locks the visible
  tightening. The exact delta (−12) + whether to keep the sensing-mode/overlay
  side-effects remain open for the end-of-alpha advising audit.
