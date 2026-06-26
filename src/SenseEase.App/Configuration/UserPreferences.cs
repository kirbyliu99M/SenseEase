using System;
using System.IO;
using System.Text.Json.Serialization;
using SenseEase.Infrastructure.Storage;

namespace SenseEase.App.Configuration;

/// <summary>
/// User-selectable theme mode (Appearance setting, 2026-06-07). Persisted in
/// <see cref="UserPreferences.ThemeMode"/> and consumed by
/// <see cref="Theme.ThemeManager"/>.
/// <list type="bullet">
///   <item><see cref="Auto"/> — follow the Windows light/dark system setting
///         (registry <c>AppsUseLightTheme</c>); live-updates on OS flips.</item>
///   <item><see cref="Light"/> — force light, never dark, regardless of OS.</item>
///   <item><see cref="Dark"/> — force dark, always, regardless of OS.</item>
/// </list>
/// Serialised as its string name (forward-compatible; unknown values fall back
/// to <see cref="Auto"/> on load).
/// </summary>
public enum AppThemeMode
{
    /// <summary>Follow the Windows system light/dark setting (default).</summary>
    Auto,
    /// <summary>Always light, never dark.</summary>
    Light,
    /// <summary>Always dark.</summary>
    Dark,
}

/// <summary>Proactive-check-in consent tri-state (Phase-1 event-driven feedback, 2026-06-16).
/// <list type="bullet">
///   <item><see cref="Unset"/> — never asked: the first eligible spike shows a one-time opt-in
///         toast, NOT a check-in, until the user chooses.</item>
///   <item><see cref="Enabled"/> — the user opted in; contextual check-ins may show (governed by
///         the cadence rules).</item>
///   <item><see cref="Declined"/> — the user said no; never prompt.</item>
/// </list>
/// String-serialised for forward-compat; unknown values fall back to <see cref="Unset"/>.</summary>
public enum ProactiveCheckInConsent { Unset, Enabled, Declined }

/// <summary>
/// Pop-up sprint Task 5: persisted user preferences. Lives at
/// %LocalAppData%/SenseEase/user-preferences.json. Atomic write via the
/// shared <see cref="AtomicJsonWriter"/> (tmp + File.Replace) so a
/// Process.Kill mid-write leaves the file intact at either the old or
/// the new version, never a half-written state.
///
/// <para>This is the SINGLE canonical preferences home for SenseEase.
/// Future settings (sensing-mode choice, accessibility tunables, etc.)
/// should consolidate here rather than spawning new JSON files. Per
/// user direction 2026-05-26, the previous "don't create new JSON
/// files" rule was relaxed once it was confirmed no preferences home
/// existed — preferences want their own file.</para>
///
/// <para>Chatbot-resettable invariant (user requirement 2026-05-26):
/// <see cref="ResetToDefaults"/> is intentionally public + static so
/// the chatbot can offer "reset my preferences" as an intent without
/// reaching past a service layer. When a future sprint wires the
/// chatbot's intent dispatch to this method, no architectural change
/// is needed here.</para>
///
/// <para>Defaults (per user decisions 2026-05-26 round 2):</para>
/// <list type="bullet">
///   <item><see cref="AutoExpandNotifications"/> = <c>false</c> — Toggle A.
///         Active intervention (auto-expand cards) requires explicit user
///         opt-in. Passive signal (bar/tray colour) is the default.</item>
///   <item>Toggle B (bar/tray tier colour) was RETIRED per D-10 (2026-06-11)
///         along with its <c>bar_tray_color_enabled</c> field — the bar/tray
///         band signal is now always the calm non-colour treatment.</item>
/// </list>
/// </summary>
public sealed class UserPreferences
{
    // ── Schema versioning ───────────────────────────────────────────────────
    // Following the same pattern as habituation_profile.json — bump on
    // breaking changes; the loader treats unknown versions as "fall back to
    // defaults + log".
    //
    // Schema history:
    //   1 — initial (Pop-up sprint Task 5).
    //   2 — head-tracking sprint M8 adds head_tracking_enabled,
    //       head_tracking_follow_mode, head_tracking_escape_hatch_enabled.
    //       Old v=1 files load cleanly (missing fields default to the
    //       Roll forward strategy below).
    //   3 — Appearance setting (2026-06-07) adds theme_mode. Old v=1/v=2 files
    //       load cleanly; the missing field defaults to "Auto" (follow Windows),
    //       which is the pre-setting behaviour, so upgrading installs keep their
    //       OS-following theme until the user picks Light/Dark explicitly.
    //       (Also at v=3, additive: language_mode (2026-06-07). The roll-forward
    //       defaults a missing language_mode to "ChineseTraditional" like any other
    //       new field, so no version bump was needed — old v=1..3 files load cleanly.)
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Current canonical schema version this build writes.</summary>
    internal const int CurrentSchemaVersion = 3;

    /// <summary>Toggle A — auto-expand notification card on tier-up / spike.
    /// Default <c>false</c> per spec §4: passive signal first, active
    /// intervention requires user opt-in.</summary>
    [JsonPropertyName("auto_expand_notifications")]
    public bool AutoExpandNotifications { get; set; } = false;

    // D-10 (2026-06-11): BarTrayColorEnabled (Toggle B) deleted — the flag's
    // consumers (TrayIconService accent path, PersistentBarWindow colour path)
    // were retired with it. Old prefs files carrying "bar_tray_color_enabled"
    // load cleanly (unmapped members are ignored) and the key drops on next Save.

    // D-14 (2026-06-11): VestibularWarningShown deleted — it was a dead persisted
    // field (the Toggle-B vestibular warning dialog it gated never existed in code;
    // the live vestibular flow is VestibularPromptAnswered below + the profile-store
    // VestibularProtectionEnabled / MedicalAdvisoryShown fields). Old prefs files that
    // still carry "vestibular_warning_shown" load cleanly — System.Text.Json ignores
    // unmapped members by default — and the key drops off on the next Save. Backlog
    // H45 closes as moot for this field.

    /// <summary>Alpha-2 addendum (cancel explicit calibration → auto-profiling):
    /// "asked once" flag for the one-time vestibular first-use prompt that REPLACES the
    /// cancelled calibration Q5. Set <c>true</c> only after the user gives ANY response
    /// (yes / no / close) — mere display must NOT consume the ask, so closing the app
    /// without interacting reshows it next launch ("asked once, not flashed-and-counted").
    /// Default <c>false</c>; rolls forward like every other additive field, so old
    /// preferences files load cleanly. Only consulted while
    /// <c>AppConfig.AutoProfilingEnabled</c> is ON.</summary>
    [JsonPropertyName("vestibular_prompt_answered")]
    public bool VestibularPromptAnswered { get; set; } = false;

    /// <summary>Pop-up sprint Task 6 feature-flag dispatcher (spec §6).
    /// Default <c>true</c> = new bar/tray/card path. Setting to
    /// <c>false</c> suppresses the new bar/tray spawn entirely (emergency
    /// rollback). The legacy floating spike-popup it once fell back to was
    /// retired in the 2026-06-21 dashboard dismantle (the SpikePopupWindow
    /// view was deleted in the 2026-06-22 audit), so this flag now only gates
    /// the bar/tray — see PopupSurfaceGate.ShouldSpawnProductionBarAndTray.
    /// <para>INTENTIONALLY not exposed in the settings UI — emergency
    /// rollback only. Edit the JSON directly if you need to fall back.
    /// Will be retired in a future sprint once the new mode has been
    /// stable for two release cycles.</para></summary>
    [JsonPropertyName("use_new_bar_overlay")]
    public bool UseNewBarOverlay { get; set; } = true;

    /// <summary>Path Z UX iter 5: when true, closing the MainWindow hides
    /// it to the system tray rather than exiting the process. The real
    /// quit path is the tray menu's "退出 SenseEase" item. Default true
    /// because the entire bar + tray + card architecture exists to support
    /// background tray-resident operation.</summary>
    [JsonPropertyName("minimize_to_tray_on_close")]
    public bool MinimizeToTrayOnClose { get; set; } = true;

    /// <summary>Path Z UX iter 5: when true, a HKCU\Software\Microsoft\
    /// Windows\CurrentVersion\Run registry value is written so SenseEase
    /// auto-launches at Windows login with the <c>--minimized</c> arg.
    /// Read on startup to surface the current registry state in the
    /// Settings UI; <see cref="Save"/> updates the registry value to
    /// match. Default TRUE (R3 additional #3 — seamless "always there" experience;
    /// applies to fresh installs, existing prefs keep their saved value).</summary>
    [JsonPropertyName("run_at_windows_startup")]
    public bool RunAtWindowsStartup { get; set; } = true;

    // ── Head-tracking sprint M8 (2026-05-30) ────────────────────────────────
    //
    // Three fields surface the head-tracking feature in Settings UI. All read
    // at app startup by App.OnStartup which mirrors them onto the static
    // AppConfig.HeadTracking* props (AppConfig is the per-process source of
    // truth read by MainViewModel / OverlayWindow at runtime). The
    // HeadTrackingEnabled flag is "startup-bound" because the sidecar reader
    // factory + LightSensorProvider bind once at MainViewModel construction;
    // the FollowMode flag is runtime-changeable because OverlayWindow reads
    // it per frame in ApplyHeadPoseToBrush.

    /// <summary>M8: master opt-in for head-tracking. Default <c>false</c>
    /// (privacy + stability — see docs/HEAD-TRACKING-PRIVACY.md §1). When
    /// false the Python sidecar is never spawned and the FOV mask centre
    /// stays at the v0.9 hardcoded (0.5, 0.5). Changes take effect at the
    /// next app launch — the Settings UI annotates the checkbox with
    /// "需重新啟動 / Requires restart" because the IHeadPoseReader and
    /// LightSensorProvider factories run once at MainViewModel construction
    /// and don't watch the flag at runtime.</summary>
    /// R3 additional #3: default flipped to TRUE for the seamless "it just works" experience
    /// (Kirby-approved). NOTE: this starts the camera sidecar at launch — a privacy-sensitive
    /// default (see docs/HEAD-TRACKING-PRIVACY.md §1). Applies to fresh installs; existing prefs
    /// keep their saved value.
    [JsonPropertyName("head_tracking_enabled")]
    public bool HeadTrackingEnabled { get; set; } = true;

    /// <summary>Chat model selection. Default = "qwen3-4b" (the sole chat model). <c>AppConfig.ResolveActiveModel</c>
    /// reads this (env var SENSEEASE_CHAT_MODEL overrides; an unknown key falls back to the primary). If the
    /// model's GGUF is absent or can't load, chat uses the deterministic lexicon path — no second model.</summary>
    public string ChatModelKey { get; set; } = AppConfig.PrimaryModelKey;

    /// <summary>M8: head-tracking follow-mode persistence. String-serialised
    /// for forward-compat ("Balanced" / "FullFollow"). Default "Balanced"
    /// matches AppConfig default (clinical VIMS-mitigation purpose — periphery
    /// always partially dimmed). Unknown values fall back to "Balanced" on
    /// load. Runtime-changeable: OverlayWindow.ApplyHeadPoseToBrush reads the
    /// mode per frame.</summary>
    [JsonPropertyName("head_tracking_follow_mode")]
    public string HeadTrackingFollowMode { get; set; } = "Balanced";

    // D-10 (2026-06-11): HeadTrackingEscapeHatchEnabled deleted — the double-tap-
    // Ctrl escape-hatch feature was removed entirely (OverlayWindow.TriggerEscapeHatch
    // and the MainWindow chord detection are gone; the mask is never weakened).
    // Old prefs files carrying "head_tracking_escape_hatch_enabled" load cleanly
    // (unmapped members are ignored) and the key drops on next Save.

    // ── Graded-exposure (habituation) opt-in (D-1, 2026-06-11) ──────────────

    /// <summary>D-1 (CW-1 step 3): master opt-in for the graded-exposure
    /// (habituation) program. Default <c>false</c> = PAUSED — graded exposure
    /// stays off until the user opts in via Settings → 漸進適應 / Graded exposure.
    /// This Settings toggle is the SOLE user-intent path that flips the engine's
    /// habituation pause (the old dashboard toggle was removed 2026-06-09); the
    /// only other legitimate resume is the explicit AcknowledgeAndResume chat
    /// flow. Selector set-moves preserve the pause and never auto-unpause.
    /// <para>Hydrated at startup in MainViewModel right after the engine is
    /// constructed: <c>SetHabituationPaused(!HabituationEnabled)</c>, so the
    /// engine's pause state matches this pref before the first Update tick.
    /// Rolls forward like every other additive field — old v=1..3 files load
    /// cleanly with the field absent (defaults to paused), so no version bump.</para></summary>
    [JsonPropertyName("habituation_enabled")]
    public bool HabituationEnabled { get; set; } = false;

    // ── Always-on overlay baseline (CW-7 / UX-12 ≡ FEAT-15, 2026-06-11) ─────

    /// <summary>CW-7 (UX-12 ≡ FEAT-15): persists the card's always-on overlay
    /// opt-in so the one effortless comfort action no longer resets to OFF every
    /// restart. Default <c>false</c> — the 2026-06-06 Kirby override stands: a fresh
    /// install starts immersive / zero-intervention (see
    /// <see cref="ViewModels.MainViewModel.PersistentOverlayEnabled"/>). Hydrated in the
    /// VM ctor BEFORE the overlay first shows. Q4.5-safe: this is a DISPLAY baseline only
    /// (it floors the shown tier via ResolveDisplayedTier) and never touches the earned
    /// habituation _offset. Rolls forward like every other additive field — old v=1..3
    /// files load cleanly with the field absent (defaults to OFF), so no version bump.</summary>
    [JsonPropertyName("persistent_overlay_enabled")]
    public bool PersistentOverlayEnabled { get; set; } = false;

    /// <summary>"Auto Protect" / 自動防護 overlay-consent mode (UI-RESHAPE quick-settings, Kirby 2026-06-20;
    /// user-facing label is "Auto Protect", the backing flag stays <c>AlwaysAllowOverlay</c>).
    /// When <c>true</c> the comfort overlay auto-engages on motion with NO consent bubble (a standing,
    /// durable consent). <c>false</c> (default) keeps the per-session "ask each time" model. Distinct
    /// from <see cref="PersistentOverlayEnabled"/> (which also floors an always-on baseline) — this only
    /// pre-grants consent. Additive field; old files load with it absent (defaults to OFF), no version bump.</summary>
    [JsonPropertyName("always_allow_overlay")]
    public bool AlwaysAllowOverlay { get; set; } = false;

    /// <summary>"One-tap consent" mode (Kirby 2026-06-25): after the user ticks "don't show this card
    /// again" on the consent bubble, the bubble STILL appears on each impactful rise, but a SINGLE tap on
    /// it grants instantly — the card no longer expands. DISTINCT from <see cref="AlwaysAllowOverlay"/>
    /// (Auto Protect), which suppresses the bubble entirely and auto-engages silently: one-tap keeps the
    /// per-episode tap so the overlay never actuates without the user's deliberate tap. The two are
    /// orthogonal (Auto Protect ON wins → no bubble at all). Default <c>false</c>; additive — old files load
    /// with it absent. Cleared by Settings → Reset preferences (kept by "Clear all my data", like every pref).</summary>
    [JsonPropertyName("overlay_consent_one_tap")]
    public bool OverlayConsentOneTap { get; set; } = false;

    /// <summary>Overlay-consent durability (W-E, 2026-06-20): the UTC time of the last EXPLICIT "Not now"
    /// decline. The <c>OverlayConsentGovernor</c> is in-memory, so on launch it re-seeds its 30-min decline
    /// cooldown from this — an explicit decline survives a restart instead of re-nagging on the next
    /// impactful rise. Null = never declined. The full asked/accepted/declined/ignored TIMELINE lives in
    /// symptom_log.jsonl (<c>kind=overlay_consent</c>); this is only the cooldown seed. Additive field —
    /// old files load with it absent, no version bump. Cleared by "Clear all my data".</summary>
    [JsonPropertyName("last_overlay_decline_utc")]
    public DateTime? LastOverlayDeclineUtc { get; set; }

    /// <summary>When the weekly welcome recap was last shown (UTC). Null = never. Drives the once-per-ISO-week
    /// gate (<see cref="SenseEase.App.ViewModels.WeeklyRecap.IsDue"/>): the recap fires on the first chat-open
    /// of each new ISO week (Monday-anchored). Additive  old prefs files roll forward (null). Stamped on show.</summary>
    [JsonPropertyName("last_weekly_recap_shown_utc")]
    public DateTime? LastWeeklyRecapShownUtc { get; set; }

    /// <summary>Running count of explicit overlay-consent declines (prompt-fatigue analytics). Additive;
    /// reset by "Clear all my data". RESERVED — written on each decline but READ by no runtime path as of
    /// B6 (2026-06-21); only <see cref="LastOverlayDeclineUtc"/> drives the re-prompt cooldown. A decline is
    /// an ambiguous UI-friction-vs-symptom signal, so any future wire-in must be tighten/promotion BIAS only
    /// (never a relax input) and gated behind a working downgrade path. See docs/B6 §3 (orphan ledger).</summary>
    [JsonPropertyName("overlay_decline_count")]
    public int OverlayDeclineCount { get; set; }

    /// <summary>UI-RESHAPE quick-settings — 錨點格線 (Chang-Hain anchor-grid lines) on/off. Read each
    /// frame by <see cref="Views.OverlayWindow"/>'s grid pass; off forces grid opacity to 0. Default
    /// on (the grid is part of the protection; the toggle lets sensitive users hide the lines).
    /// Additive — old files without the field load cleanly defaulting to on.</summary>
    [JsonPropertyName("anchor_grid_enabled")]
    public bool AnchorGridEnabled { get; set; } = true;

    /// <summary>TASK #7 (UI-RESHAPE): the user-customisable global hotkey that reveals / toggles
    /// the glass popup bar, as a "+"-joined gesture string (e.g. "Win+Alt+S"). Default "Win+Alt+S".
    /// Parsed by <see cref="Services.GlobalHotkeyService.TryParse"/> and registered in
    /// <see cref="App.ReRegisterRevealHotkey"/>; an unparseable / already-claimed value fails soft
    /// (no hotkey that session — the edge-sidebar click still reveals the bar). Additive — old
    /// files without the field load cleanly defaulting to "Win+Alt+S", so no schema bump.</summary>
    [JsonPropertyName("reveal_hotkey")]
    public string RevealHotkey { get; set; } = "Win+Alt+S";

    /// <summary>UI-RESHAPE: the popup bar's remembered vertical CENTRE (dip — the bar's mid-Y on its
    /// monitor's work area). Null = default (vertically centred). The bar is locked to the right edge;
    /// the user can only drag it up/down. Stored as the CENTRE (not the top) so the bar stays put when
    /// it grows/shrinks — expanding or opening the chat extends symmetrically instead of shifting the
    /// always-on anchor (involuntary motion is bad for a motion-sickness tool). Additive — old files
    /// default to null (centred).</summary>
    [JsonPropertyName("popup_bar_center_y")]
    public double? PopupBarCenterY { get; set; }

    /// <summary>CW-7 (UX-12 ≡ FEAT-15): persists the always-on overlay baseline
    /// intensity level (1–12, native scale) paired with
    /// <see cref="PersistentOverlayEnabled"/>. Default <c>1</c> = 0% alpha at rest
    /// (the field is "ready", not "always dark"), matching the VM's day-1 default and
    /// the pinned R-B safety pairing (a higher baseline would over-dim every new user).
    /// Natural pressure-driven tiers still override a calm baseline, so a real motion
    /// spike darkens the periphery further. Display baseline only — never touches the
    /// habituation offset (Q4.5). Rolls forward; old files default to 1.</summary>
    [JsonPropertyName("persistent_overlay_baseline_tier")]
    public int PersistentOverlayBaselineTier { get; set; } = 1;

    // ── Per-app attribution consent (Beta-1 D-16, 2026-06-14) ───────────────

    /// <summary>Beta-1 D-16: per-app attribution consent. Default <c>false</c> —
    /// STRICTLY opt-in. When true, <c>WindowsActivityFilter</c> resolves the
    /// foreground app to a coarse category + raw app-name key (ON-DEVICE ONLY;
    /// never off-device processed — D-16) that the symptom recorder stamps onto
    /// data-points so personalisation can learn per-app triggers (Theme-5 join).
    /// Mirrors the head-tracking privacy contract. Hydrated at startup in
    /// App.OnStartup onto <c>AppConfig.PerAppAttributionEnabled</c> (the per-process
    /// source of truth the MainViewModel ctor reads to seed the activity filter),
    /// and runtime-changeable: the Settings toggle writes the live
    /// <c>WindowsActivityFilter.Instance.PerAppAttributionEnabled</c> flag so it
    /// takes effect on the next foreground change without a restart. Rolls forward
    /// like every other additive field — old v=1..3 files load cleanly with the
    /// field absent (defaults to OFF), so no version bump.</summary>
    [JsonPropertyName("per_app_attribution_enabled")]
    public bool PerAppAttributionEnabled { get; set; } = false;

    // ── Proactive contextual check-ins (Phase 1 event-driven FMS, 2026-06-16) ──

    /// <summary>Consent for proactive, event-triggered discomfort check-ins — the contextual
    /// "lots of motion just now — how are you feeling?" prompt surfaced after a pressure spike.
    /// Tri-state, string-serialised like <see cref="ThemeModeRaw"/> for tolerant forward-compat.
    /// Default "Unset" = ALLOWED (opt-out, restoring pre-46bb74a8 semantics): a contextual check-in
    /// surfaces on a pressure spike for everyone EXCEPT users who explicitly turn it off in Settings
    /// (which writes "Declined"). Read <see cref="ProactiveCheckInsAllowed"/> at the gate — only
    /// "Declined" blocks. Rolls forward like every other additive field — old v=1..3 files load cleanly
    /// with the field absent (defaults to Unset), so no version bump.</summary>
    [JsonPropertyName("proactive_checkins_consent")]
    public string ProactiveCheckInsConsentRaw { get; set; } = nameof(ProactiveCheckInConsent.Unset);

    /// <summary>Tolerant typed view over <see cref="ProactiveCheckInsConsentRaw"/> (mirrors the
    /// <see cref="ThemeMode"/> pattern). Unknown/malformed strings degrade to
    /// <see cref="ProactiveCheckInConsent.Unset"/> rather than throwing inside deserialisation;
    /// the setter writes the canonical enum name back so the persisted file stays
    /// "Unset"/"Enabled"/"Declined".</summary>
    [JsonIgnore]
    public ProactiveCheckInConsent ProactiveCheckInsConsent
    {
        get => Enum.TryParse<ProactiveCheckInConsent>(ProactiveCheckInsConsentRaw, ignoreCase: true, out var m)
            ? m
            : ProactiveCheckInConsent.Unset;
        set => ProactiveCheckInsConsentRaw = value.ToString();
    }

    /// <summary>Opt-out gate for the spike-triggered contextual check-in (Kirby 2026-06-27, restoring
    /// the pre-46bb74a8 semantics): ALLOWED unless the user explicitly turned it off
    /// (<see cref="ProactiveCheckInConsent.Declined"/>). So the default
    /// <see cref="ProactiveCheckInConsent.Unset"/> and <see cref="ProactiveCheckInConsent.Enabled"/>
    /// both allow; only <c>Declined</c> blocks. Single source of truth shared by the spike gate
    /// (<c>MainWindow.MaybeShowSpikeCheckIn</c>) and the Settings toggle hydration, so they can't drift.</summary>
    [JsonIgnore]
    public bool ProactiveCheckInsAllowed =>
        ProactiveCheckInsConsent != ProactiveCheckInConsent.Declined;

    /// <summary>B5 (auto-adaptation): opt-in consent for the deterministic AdaptationGate to AUTO-APPLY
    /// small, bounded protection adjustments derived from the chatbot's retrospective analysis (validated
    /// against MEASURED data, reversible, clamped to ±12pp). Tri-state, string-serialised like
    /// <see cref="ProactiveCheckInsConsentRaw"/>. Default "Unset" = OFF: with consent off the gate never
    /// runs, so protection is exactly as today. Additive — old preference files load with it absent.</summary>
    [JsonPropertyName("adaptive_autotune_consent")]
    public string AdaptiveAutoTuneConsentRaw { get; set; } = nameof(ProactiveCheckInConsent.Unset);

    /// <summary>Tolerant typed view over <see cref="AdaptiveAutoTuneConsentRaw"/> (mirrors
    /// <see cref="ProactiveCheckInsConsent"/>). Reuses the tri-state opt-in enum; only
    /// <see cref="ProactiveCheckInConsent.Enabled"/> turns the auto-adaptation loop on.</summary>
    [JsonIgnore]
    public ProactiveCheckInConsent AdaptiveAutoTuneConsent
    {
        get => Enum.TryParse<ProactiveCheckInConsent>(AdaptiveAutoTuneConsentRaw, ignoreCase: true, out var m)
            ? m
            : ProactiveCheckInConsent.Unset;
        set => AdaptiveAutoTuneConsentRaw = value.ToString();
    }

    // ── Notification HUD card position (2026-06-07) ─────────────────────────

    /// <summary>HUD-card rework: persisted X (Left) of the draggable notification
    /// card, in device-independent pixels in virtual-screen coordinates. Null =
    /// never dragged; the card falls back to its top-center first-run anchor on
    /// Show. Saved on drag-end / LocationChanged (Game-Bar-style "remember where
    /// I put it"). Stored as a nullable so an absent field on older v=1/v=2/v=3
    /// files cleanly means "unset" without a sentinel value.</summary>
    [JsonPropertyName("hud_left")]
    public double? HudLeft { get; set; }

    /// <summary>HUD-card rework: persisted Y (Top) of the draggable notification
    /// card, paired with <see cref="HudLeft"/>. Null = unset → top-center anchor.</summary>
    [JsonPropertyName("hud_top")]
    public double? HudTop { get; set; }

    // ── Appearance setting (2026-06-07) ─────────────────────────────────────

    /// <summary>User-selected theme mode, string-serialised for forward-compat
    /// exactly like <see cref="HeadTrackingFollowMode"/> ("Auto" / "Light" /
    /// "Dark"). Default "Auto" (follow the Windows light/dark system setting) —
    /// this matches the pre-setting behaviour, so existing installs and v=1/v=2
    /// files (where the field is absent) keep following the OS until the user
    /// explicitly picks Light or Dark in Settings → 外觀 / Appearance.
    /// <para>A plain string (not an enum-typed property) is used deliberately:
    /// an unrecognised stored value must NOT throw inside deserialisation and
    /// wipe the whole prefs file — it should degrade to Auto. The typed
    /// accessor <see cref="ThemeMode"/> performs that tolerant parse, and
    /// <see cref="Theme.ThemeManager"/> consumes the typed value.</para></summary>
    [JsonPropertyName("theme_mode")]
    public string ThemeModeRaw { get; set; } = nameof(AppThemeMode.Auto);

    /// <summary>Tolerant typed view over <see cref="ThemeModeRaw"/>. Unknown /
    /// malformed strings fall back to <see cref="AppThemeMode.Auto"/> on get;
    /// the setter writes the canonical enum name back to the raw string so the
    /// persisted file stays "Auto"/"Light"/"Dark". Case-insensitive parse.</summary>
    [JsonIgnore]
    public AppThemeMode ThemeMode
    {
        get => Enum.TryParse<AppThemeMode>(ThemeModeRaw, ignoreCase: true, out var m)
            ? m
            : AppThemeMode.Auto;
        set => ThemeModeRaw = value.ToString();
    }

    /// <summary>User-selected UI language (Settings → 語言 / Language, 2026-06-07).
    /// A plain string (not enum-typed) so an unrecognised stored value degrades to the
    /// default rather than throwing inside deserialisation. Parsed tolerantly by
    /// <see cref="Localization.LocalizationManager.Parse"/>. Default
    /// "ChineseTraditional" — the app's primary language.</summary>
    [JsonPropertyName("language_mode")]
    public string LanguageModeRaw { get; set; } = "ChineseTraditional";

    // ── Static singleton + persistence ──────────────────────────────────────

    private static UserPreferences? _current;
    private static readonly object _gate = new();

    /// <summary>UI Consolidation Sprint M-Phase-1 (2026-05-30): raised after a
    /// successful <see cref="Save"/> (or <see cref="ResetToDefaults"/>, which
    /// calls Save). Lets multiple UI surfaces hosting the shared
    /// <c>SettingsContent</c> UserControl re-hydrate from
    /// <see cref="Current"/> when a peer surface saves a change, so the
    /// Settings tab and SettingsWindow stay visually in sync without
    /// per-instance plumbing.
    /// <para>Fires on whatever thread called Save. All current callers are
    /// UI event handlers (toggle Checked/Unchecked), so subscribers can
    /// safely touch WPF state directly. A future background-thread caller
    /// would need to marshal back to the dispatcher itself.</para></summary>
    public static event EventHandler? Changed;

    /// <summary>The active preferences instance. First access loads from
    /// disk (falling back to defaults if missing/malformed). Thread-safe.</summary>
    public static UserPreferences Current
    {
        get
        {
            if (_current is not null) return _current;
            lock (_gate)
            {
                _current ??= Load();
                return _current;
            }
        }
    }

    /// <summary>Path to the on-disk preferences file. Public so docs +
    /// support tools can reference it; not intended for direct read/write.</summary>
    public static string FilePath => Path.Combine(
        AppConfig.DataRoot, "SenseEase", "user-preferences.json");

    /// <summary>Loads preferences from <see cref="FilePath"/>. Returns a
    /// defaults-only instance if the file is missing OR if the loaded
    /// schema_version is unknown (forward-compat: an older app reading
    /// a newer file falls back rather than misinterpreting).
    ///
    /// <para>Roll-forward strategy (NE17): ANY stored version in the inclusive
    /// range [1, <see cref="CurrentSchemaVersion"/>] loads cleanly and is
    /// migrated forward — new fields from later schema revisions default via
    /// their property initialisers, so an old install upgrading does NOT lose
    /// existing toggle state. On next Save the file is rewritten at the current
    /// schema. Defaults are reserved ONLY for a version newer than this build
    /// can understand (> Current), a version below the first schema (&lt; 1),
    /// or a malformed/absent version (deserialises to 0, i.e. &lt; 1) — this
    /// preserves the safe-on-mismatch posture for genuinely unknown files
    /// without rejecting valid intermediate versions.</para></summary>
    public static UserPreferences Load()
    {
        var loaded = AtomicJsonWriter.TryRead<UserPreferences>(FilePath);
        if (loaded is null) return new UserPreferences();
        if (loaded.SchemaVersion < 1 || loaded.SchemaVersion > CurrentSchemaVersion)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[user-preferences] Unknown schema_version {loaded.SchemaVersion} at {FilePath}; using defaults.");
            return new UserPreferences();
        }
        // Roll any in-range older version → current. Fields introduced by later
        // schema revisions already have safe defaults from property
        // initialisers; just stamp the version so the next Save persists as the
        // current schema.
        if (loaded.SchemaVersion < CurrentSchemaVersion)
            loaded.SchemaVersion = CurrentSchemaVersion;
        return loaded;
    }

    /// <summary>Persists the current in-memory snapshot to disk. Best-effort
    /// (matches the AtomicJsonWriter contract); a write failure leaves the
    /// previous file untouched and is silently logged.</summary>
    public void Save()
    {
        AtomicJsonWriter.TryWrite(FilePath, this);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Resets all preferences back to defaults and persists. A fresh
    /// instance replaces the cached one, so every persisted preference returns
    /// to its property-initialiser default — theme (Auto), language
    /// (ChineseTraditional), the notification / background toggles, and the
    /// saved HUD-card position — leaving the user in the same state as a clean
    /// install. (D-14, 2026-06-11: the old contract clause about also clearing
    /// VestibularWarningShown is now vacuous — that field was deleted — so the
    /// D-15 confirm copy enumerates only the surviving preferences.)
    ///
    /// <para>Q4.5-safe by construction: this touches ONLY the preferences file.
    /// The earned habituation offset lives in habituation.json / the profile
    /// store, which this method never opens — a preferences reset is distinct
    /// from the "Clear all my data" path.</para>
    ///
    /// <para>Public + static so the chatbot can call this from intent
    /// handlers without reaching past a service layer (user requirement
    /// 2026-05-26). When the chatbot's intent dispatch wires up "reset my
    /// preferences" to this, no architectural change here is needed.</para></summary>
    public static void ResetToDefaults()
    {
        lock (_gate)
        {
            _current = new UserPreferences();
            _current.Save();
            // Beta-1 #18: the actual Windows login-autostart lives in the HKCU Run key (owned
            // by AutoStartRegistry), NOT in this JSON. Swapping in a fresh instance set
            // RunAtWindowsStartup=false but left a stale Run entry still launching the app at
            // login (and the toggle re-reads the registry, so the UI also desynced). Reconcile
            // the registry to the reset value. Best-effort: a registry failure must not abort
            // the preference reset.
            try { AutoStartRegistry.SetEnabled(_current.RunAtWindowsStartup); } catch { /* best-effort */ }
        }
    }

    /// <summary>Test-only escape hatch — drops the cached singleton so a
    /// subsequent <see cref="Current"/> access re-loads from disk. Used by
    /// the round-trip test so reads from FilePath produce a fresh instance
    /// instead of the lock-cached one.</summary>
    internal static void ResetCacheForTesting()
    {
        lock (_gate) { _current = null; }
    }
}
