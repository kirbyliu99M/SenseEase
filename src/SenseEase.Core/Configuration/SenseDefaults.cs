namespace SenseEase.Core.Configuration;

/// <summary>
/// Phase-1 default parameters from the design draft §5.
/// Centralised here so Inference / Rendering / mocks all read one source of truth.
/// </summary>
public static class SenseDefaults
{
    public const int DefaultFovRadiusPercent = 35;
    // Tightest FOV reachable from natural pressure alone (no chatbot/habituation).
    // Decoupled from ComplaintFovDelta, which is a chatbot-feedback nudge — different policy knob.
    public const int TightestNaturalFovRadiusPercent = 15;
    public const int OuterTransitionRadiusPercent = 45;
    public const int GridSizePx = 40;
    public const double GridOpacity = 0.75;
    public const double NoiseGateThreshold = 8;
    public const double TriggerPressure = 85;
    public const double PassiveFlowWeight = 2.0;
    public const double QuadraticTerm = 0.0008;
    // Base decay alpha for a 100 ms step (tau ~= 2.81s).
    public const double BaseDecayAlpha = 0.965;
    public const int ComplaintFovDelta = -15;
    public const int CooldownMs = 8000;
    public const int EventLogMaxCount = 20;

    // Phase 2.1 — cursor-velocity OFI normalization
    // 80 px/s-per-OFI-unit: full-screen sweep in 1s (~2000 px/s) ≈ OFI 25; fast gaming swipe ≈ OFI 80.
    // Higher factor => lower OFI per cursor speed (less sensitive).
    public const double CursorVelocityNormFactor = 120.0;
    public const int    CursorVelocityPollIntervalMs = 33; // ~30 Hz
    // EMA smoothing applied to both cursor speed (2.1) and WGC pixel-diff (2.2).
    // Higher alpha => heavier smoothing (less responsive to spikes).
    public const double CursorEmaAlpha = 0.55;

    // Phase 2.2 — WGC screen-capture pixel-diff OFI
    public const int    WgcCaptureIntervalMs = 66;  // ~15 Hz; 1 capture per 2 display frames
    public const int    WgcDownsampleWidth   = 160; // width of grayscale comparison frame
    public const int    WgcDownsampleHeight  = 90;  // height (preserves 16:9 aspect)
    // MAD (mean absolute difference per pixel, 0–255) × factor = raw OFI.
    // Tune after first real WGC data: idle desktop ≈ MAD 0-1, scrolling video ≈ MAD 15-40.
    // Lower factor => lower OFI from same MAD pixel-diff.
    public const double WgcMadNormFactor     = 4.0;

    // ── Phase 3 — Habituation Engine ──────────────────────────────────────────
    // Pressure bands are fractions of TriggerPressure: relax when calm, back off on recurrence.
    public const double HabituationStablePressureFraction     = 0.25; // calm  => pressure < 25% of TriggerPressure
    public const double HabituationRecurrencePressureFraction = 0.60; // spike => pressure >= 60% of TriggerPressure
    public const double HabituationStableWindowSeconds        = 120.0; // contiguous calm needed before relaxing
    public const double HabituationMaxWindowSeconds           = 240.0; // cautious-user window upper bound
    public const double HabituationStepIntervalSeconds        = 30.0;  // one relaxation step per this interval
    public const double HabituationMaxStepIntervalSeconds     = 75.0;  // cautious-user cadence upper bound
    public const double HabituationStepPercent                = 2.0;   // FOV widened per step (percent points)
    public const int    HabituationMaxOffset                  = 30;    // hard cap on total FOV relaxation
    public const double HabituationMidBandDecayRate            = 0.5;   // non-calm time bleeds calm credit at this rate
    public const double HabituationPauseCooldownSeconds        = 90.0;  // forced pause after a recurrence
    public const double HabituationSpikeBackoffMultiplier      = 0.5;   // pressure-spike recurrence halves the offset
    public const double HabituationMildBackoffPercent          = 4.0;   // mild discomfort report reduces offset by this
    public const int    HabituationSevereFovThreshold          = 18;    // |fovDelta| >= this => severe => zero the offset
    public const int    HabituationPersonalizationMinSamples   = 3;     // history count for full personalization weight
    public const double HabituationCautionMeanDeltaDivisor     = 15.0;  // mean FOV delta that saturates "caution" to 1.0
    public const double HabituationTimeScale = 1.0;  // demo accelerator for habituation dt; 1.0 = real time
    public const double HabituationPersonalizationRefreshSeconds = 30.0; // re-pull personalization this often

    // ── Phase 5 — Anti-flicker & tier hysteresis ──────────────────────────────
    // 3-tap median pre-filter on raw OFI rejects single-frame outliers (scene cuts, cursor
    // flicks) at the cost of ~133 ms onset latency. 5-tap was tested first but added 333 ms
    // of WGC-only delay, making screen content respond visibly slower than cursor input.
    public const int VimsMedianTaps = 3;

    // Visual VIMS tier (1..10) hysteresis. Within a tier the displayed mask does not move.
    // To transition UP requires levelF to exceed currentTier + 0.5 + deadband for `UpDebounceMs`.
    // To transition DOWN requires falling below currentTier - 0.5 - deadband for `DownDebounceMs`.
    // Asymmetric attack/release mirrors audio compressor design — fast tighten, slow release.
    public const double VimsTierDeadband     = 0.15;   // half-band width in level units
    public const int    VimsTierUpDebounceMs   = 200;  // confirm rise (~3 frames @ 15 Hz)
    public const int    VimsTierDownDebounceMs = 1200; // confirm fall — slow at the dangerous top
    // Fast-release knob for lower tiers (≤ VimsLowTierReleaseCutoff). At low tiers we are
    // mostly relaxed already, so the mask should clear briskly. Without this, full release
    // (tier 10 → 1) takes 9×1200ms = 10.8 s, far longer than the natural pressure decay
    // (τ≈2.81s, so 250→<85 in ≈3 s). The user perceives "stuck darkness."
    public const int    VimsTierDownDebounceLowMs = 600;  // tiers ≤ cutoff release at this rate
    public const int    VimsLowTierReleaseCutoff  = 7;    // tier transition target ≤ this uses low-ms

    // Fast-onset bypass: when levelF outruns the current tier by this many levels in a single
    // tick, snap straight to the new tier (no debounce). Justification: with WGC at ~15 Hz and
    // a strict +1-per-call limit, sudden onsets (scrolling video starts) take 9×200ms = 1.8 s
    // to climb 1→10 — by which time the pressure surge is over and the protection arrived late.
    // 1.5 levels of gap is well above per-tick noise (deadband 0.15) yet below the natural step
    // size for genuine onsets.
    public const double VimsTierFastOnsetGap = 1.5;

    // ── Phase 4 — Ambient Light Comfort (lux → display VCP) ───────────────────
    public const int    LuxPollIntervalMs   = 1000;  // ALS poll cadence
    public const double LuxLogMin           = 0.0;   // log10(1 lx)     — dark-room floor
    public const double LuxLogMax           = 4.0;   // log10(10000 lx) — bright-daylight ceiling
    public const double LuxEmaAlpha         = 0.85;  // lux smoothing (anti-flicker)
    public const int    LuxHysteresisDeadbandPercent = 3; // min target-% change before a DDC/CI write
    public const int    BaselineRecoveryMaxAgeHours  = 24; // ignore a stale force-kill baseline older than this
    // Comfort target ranges (normalized %, mapped onto each monitor's native VCP max).
    // Conservative defaults — never drive a feature to 0 or 100. Tune against real hardware.
    public const int ContrastMinPercent   = 45;
    public const int ContrastMaxPercent   = 80;
    public const int BrightnessMinPercent = 35;
    public const int BrightnessMaxPercent = 85;
    public const int SharpnessMinPercent  = 40;
    public const int SharpnessMaxPercent  = 75;
}
