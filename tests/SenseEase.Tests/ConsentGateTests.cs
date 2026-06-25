using System;
using System.IO;
using SenseEase.App.Configuration;
using SenseEase.App.Services;
using SenseEase.App.ViewModels;
using Xunit;

namespace SenseEase.Tests;

/// <summary>R3 #5 — the consent cooldown governor: a short baseline from the ASK, an explicit "Not now"
/// → a LONG cooldown (30 min), an UNANSWERED dismiss → a SHORT cooldown (3 min, Kirby 2026-06-20); plus a
/// startup-quiet window.</summary>
public class OverlayConsentGovernorTests
{
    private static OverlayConsentGovernor New(Func<DateTime> clock) =>
        new(clock) { StartupQuiet = TimeSpan.Zero }; // default DeclineCooldown 30m / NoAnswerCooldown 3m

    [Fact]
    public void Ask_StartsShortBaselineCooldown_ThenElapses()
    {
        var now = new DateTime(2026, 6, 18, 12, 0, 0, DateTimeKind.Utc);
        var g = New(() => now);

        Assert.True(g.ShouldAsk());            // fresh
        g.RecordAsked();
        Assert.False(g.ShouldAsk());           // cooling down (the 3-min baseline from the ask)
        now = now.AddMinutes(4);
        Assert.True(g.ShouldAsk());            // elapsed
    }

    [Fact]
    public void Accept_KeepsTheFromAskBaseline()
    {
        var now = new DateTime(2026, 6, 18, 12, 0, 0, DateTimeKind.Utc);
        var g = New(() => now);
        g.RecordAsked();
        g.RecordAccepted();
        Assert.False(g.ShouldAsk());           // accept does NOT clear it (the session owns re-asking)
        now = now.AddMinutes(4);
        Assert.True(g.ShouldAsk());
    }

    [Fact]
    public void Decline_StartsTheLongCooldown()
    {
        var now = new DateTime(2026, 6, 18, 12, 0, 0, DateTimeKind.Utc);
        var g = New(() => now);
        g.RecordDeclined();
        Assert.False(g.ShouldAsk());           // cooling down
        now = now.AddMinutes(4);
        Assert.False(g.ShouldAsk());           // past the 3-min no-answer window, but a DECLINE holds longer
        now = now.AddMinutes(27);              // total +31 min
        Assert.True(g.ShouldAsk());            // 30-min decline cooldown elapsed
    }

    [Fact]
    public void DismissedUnanswered_StartsTheShortCooldown()
    {
        var now = new DateTime(2026, 6, 18, 12, 0, 0, DateTimeKind.Utc);
        var g = New(() => now);
        g.RecordDismissedUnanswered();
        Assert.False(g.ShouldAsk());           // cooling down (3 min)
        now = now.AddMinutes(4);
        Assert.True(g.ShouldAsk());            // short cooldown elapsed → re-asks sooner than a decline
    }

    [Fact]
    public void StartupQuiet_BlocksTheEarlyPrompt()
    {
        var now = new DateTime(2026, 6, 18, 12, 0, 0, DateTimeKind.Utc);
        var g = new OverlayConsentGovernor(() => now) { StartupQuiet = TimeSpan.FromSeconds(60) };
        Assert.False(g.ShouldAsk());           // within the startup-quiet window
        now = now.AddSeconds(61);
        Assert.True(g.ShouldAsk());            // past it
    }

    [Fact]
    public void RestoreDeclineCooldown_RecentDecline_SuppressesUntilTheWindowElapses()
    {
        // W-E: an explicit decline from a PRIOR session (re-seeded on launch) keeps the 30-min cooldown.
        var now = new DateTime(2026, 6, 18, 12, 0, 0, DateTimeKind.Utc);
        var g = New(() => now);
        g.RestoreDeclineCooldown(now.AddMinutes(-5)); // declined 5 min before this launch
        Assert.False(g.ShouldAsk());                  // still cooling down → no re-nag after restart
        now = now.AddMinutes(26);                     // 31 min after the decline
        Assert.True(g.ShouldAsk());                   // window elapsed → re-asks
    }

    [Fact]
    public void RestoreDeclineCooldown_StaleOrNull_IsNoOp()
    {
        var now = new DateTime(2026, 6, 18, 12, 0, 0, DateTimeKind.Utc);
        var g = New(() => now);
        g.RestoreDeclineCooldown(now.AddMinutes(-31)); // older than the 30-min window
        Assert.True(g.ShouldAsk());                    // already elapsed → does NOT suppress
        g.RestoreDeclineCooldown(null);                // never declined
        Assert.True(g.ShouldAsk());
    }
}

/// <summary>R3 #5 — the MainViewModel PER-SESSION consent gate. While dormant the overlay is held off
/// entirely (clamped to L1) and the user is asked only once the tier reaches the impactful level (>=6);
/// granting activates the session (all levels run un-held); a user-chosen baseline is implicit consent.</summary>
/// <para>W-E (2026-06-20): the consent gate now re-seeds its decline cooldown from the persisted
/// <see cref="UserPreferences.LastOverlayDeclineUtc"/> on construction, so these tests must run against
/// CLEAN prefs — isolated in the <c>TestDataRoot</c> collection (per-class temp subdir + cache reset)
/// like <see cref="PersistentOverlayBaselinePersistenceTests"/>, so a sibling test's persisted decline
/// can't suppress the expected ask.</para>
[Collection("TestDataRoot")]
public class ConsentGateTests : IDisposable
{
    public ConsentGateTests()
    {
        TestDataRootFixture.UseClassSubdir(nameof(ConsentGateTests));
        if (File.Exists(UserPreferences.FilePath)) File.Delete(UserPreferences.FilePath);
        UserPreferences.ResetCacheForTesting();
    }

    public void Dispose() => UserPreferences.ResetCacheForTesting();

    private static MainViewModel NewVm()
    {
        var vm = new MainViewModel();
        vm.SetIntensityLevel(1); // no always-on baseline → PersistentOverlayEnabled = false (dormant)
        vm.OverlayConsentGovernorForTests.StartupQuiet = TimeSpan.Zero; // bypass launch quiet in tests
        return vm;
    }

    [Fact]
    public void Dormant_HoldsEverything_AndAsksOnlyAtTheImpactTier()
    {
        var vm = NewVm();
        int asks = 0;
        vm.OverlayConsentRequested += (_, _) => asks++;

        // Below the impact tier (6): still held (dormant overlay shows nothing) but NO prompt.
        Assert.True(vm.ShouldHoldMaskForConsent(5));
        Assert.Equal(0, asks);

        // At the impact tier: held + ask ONCE; repeat ticks don't re-ask while pending.
        Assert.True(vm.ShouldHoldMaskForConsent(6));
        Assert.Equal(1, asks);
        Assert.True(vm.ShouldHoldMaskForConsent(7));
        Assert.Equal(1, asks);
    }

    [Fact]
    public void Grant_ActivatesSession_AllLevelsRunUnheld()
    {
        var vm = NewVm();
        Assert.True(vm.ShouldHoldMaskForConsent(6)); // ask
        vm.GrantOverlayConsent();

        // Session live → nothing is held, and a brief dip to L1 does NOT end it (needs sustained calm).
        Assert.False(vm.ShouldHoldMaskForConsent(8));
        Assert.False(vm.ShouldHoldMaskForConsent(3));
        Assert.False(vm.ShouldHoldMaskForConsent(1));
    }

    [Fact]
    public void AlwaysOnBaseline_SetLiveThisSession_IsImplicitConsent_NeverGated()
    {
        var vm = new MainViewModel();
        vm.SetIntensityLevel(6); // user opted into an always-on baseline LIVE → PersistentOverlayEnabled = true
        int asks = 0;
        vm.OverlayConsentRequested += (_, _) => asks++;

        Assert.False(vm.ShouldHoldMaskForConsent(8));
        Assert.Equal(0, asks);
        Assert.True(vm.IsOverlayConsented); // a live-set baseline is in-session consent
    }

    // ── Gate-restored-baselines fix (Kirby 2026-06-25): a persistent_overlay baseline RESTORED from disk
    //    (e.g. the strength slider durably set it in a PRIOR run) must NOT silently bypass the consent
    //    bubble — the root cause of the QA "Auto Protect OFF but overlay engaged + permanent session" report.
    //    Only a LIVE in-session baseline (above) or an explicit grant counts as consent. ──

    [Fact]
    public void RestoredBaseline_FromDisk_StillHeldAndAsks_NotASilentBypass()
    {
        // Simulate the QA box: a prior run's slider drag persisted an always-on baseline (tier 9).
        UserPreferences.Current.PersistentOverlayEnabled = true;
        UserPreferences.Current.PersistentOverlayBaselineTier = 9;
        UserPreferences.Current.Save();
        UserPreferences.ResetCacheForTesting();       // restart: the fresh VM re-reads the baseline from disk

        var vm = new MainViewModel();                 // hydrates the baseline, but NOT in-session consent
        vm.OverlayConsentGovernorForTests.StartupQuiet = TimeSpan.Zero;
        Assert.True(vm.PersistentOverlayEnabled);     // precondition: the baseline actually hydrated
        Assert.Equal(9, vm.PersistentOverlayBaselineTier);
        int asks = 0;
        vm.OverlayConsentRequested += (_, _) => asks++;

        Assert.False(vm.IsOverlayConsented);          // a restored baseline is not yet consent
        // Even at REST (natural L1) the restored tier-9 baseline would be visible → prompt at launch, held.
        Assert.True(vm.ShouldHoldMaskForConsent(1));
        Assert.Equal(1, asks);
    }

    [Fact]
    public void RestoredBaseline_AfterGrant_IsHonoredForTheRun()
    {
        UserPreferences.Current.PersistentOverlayEnabled = true;
        UserPreferences.Current.PersistentOverlayBaselineTier = 9;
        UserPreferences.Current.Save();
        UserPreferences.ResetCacheForTesting();         // restart: re-read the baseline from disk

        var vm = new MainViewModel();
        vm.OverlayConsentGovernorForTests.StartupQuiet = TimeSpan.Zero;
        Assert.True(vm.PersistentOverlayEnabled);       // precondition: the baseline hydrated

        Assert.True(vm.ShouldHoldMaskForConsent(1));   // held + ask at launch
        vm.GrantOverlayConsent();                       // user taps Start

        Assert.True(vm.IsOverlayConsented);
        Assert.False(vm.ShouldHoldMaskForConsent(1));   // baseline now honored at rest — un-held, no re-hold
        Assert.False(vm.ShouldHoldMaskForConsent(8));
    }

    [Fact]
    public void GrantedSession_Ends_AfterSustainedSubImpactfulMotion_NotJustDeadCalm()
    {
        // Permanent-session fix: the end-test is now "natural < impactful" (not "natural <= 1"), so a
        // granted session ends after sustained GENTLE motion — reachable on a noisy/video box.
        var vm = NewVm();
        vm.OverlayConsentGovernorForTests.NoAnswerCooldown = TimeSpan.Zero; // bypass the real-time re-ask cooldown
        long tick = 1_000; vm.ConsentTickProvider = () => tick;             // non-zero: 0 is the "not calm" sentinel
        int asks = 0;
        vm.OverlayConsentRequested += (_, _) => asks++;

        Assert.True(vm.ShouldHoldMaskForConsent(6));  // ask #1
        vm.GrantOverlayConsent();                      // session live
        Assert.Equal(1, asks);

        // Sustained tier-3 motion (never dead-calm at L1, but below the impactful threshold).
        Assert.False(vm.ShouldHoldMaskForConsent(3));          // session live; calm window opens
        tick += MainViewModel.SessionEndCalmMs;
        Assert.False(vm.ShouldHoldMaskForConsent(3));          // calm window elapsed → the session ends here
        // Proof it truly ended: the next impactful rise re-asks (old <=1 behaviour would never end → no re-ask).
        Assert.True(vm.ShouldHoldMaskForConsent(6));
        Assert.Equal(2, asks);
    }

    [Fact]
    public void AutoProtect_OnThenOff_DropsTheImplicitSession_SoTheBubbleReAsks()
    {
        // Deadswitch regression (Kirby 2026-06-23): turning Auto Protect ON activates the standing session
        // (_overlaySessionActive=true); turning it OFF must drop that session so we return to "ask each time".
        // Before the fix the flag lingered, so the consent bubble never re-asked after an on→off flip.
        var vm = NewVm();
        int asks = 0;
        vm.OverlayConsentRequested += (_, _) => asks++;

        vm.AlwaysAllowOverlay = true;                  // standing consent
        Assert.False(vm.ShouldHoldMaskForConsent(8));  // session live → un-held, no bubble
        Assert.Equal(0, asks);

        vm.AlwaysAllowOverlay = false;                 // the fix: drop the implicit session

        Assert.True(vm.ShouldHoldMaskForConsent(6));    // dormant again → held AND re-asks at the impact tier
        Assert.Equal(1, asks);
    }

    // ── One-tap consent mode (Model-2, Kirby 2026-06-25): ticking "don't show the card again" turns on
    //    ONE-TAP mode, NOT Auto Protect. The consent GATE is unchanged (the bubble still appears); only the
    //    view's tap-handler changes (a tap grants without the card). These pin the orthogonality the user
    //    flagged — one-tap must NOT touch Auto Protect, and must NOT suppress the bubble. ──

    [Fact]
    public void OneTapConsentMode_DoesNotTouchAutoProtect_NorSuppressTheBubble()
    {
        var vm = NewVm();
        int asks = 0;
        vm.OverlayConsentRequested += (_, _) => asks++;

        vm.OverlayConsentOneTap = true;                 // user ticked "don't show the card again"

        Assert.True(vm.OverlayConsentOneTap);
        Assert.False(vm.AlwaysAllowOverlay);            // Auto Protect untouched (the user's key concern)
        Assert.True(vm.ShouldHoldMaskForConsent(6));    // gate UNCHANGED → still held + the bubble still appears
        Assert.Equal(1, asks);
    }

    [Fact]
    public void AutoProtect_StillWins_OverOneTap_NoBubble()
    {
        // Orthogonality the other way: with BOTH set, Auto Protect's "no bubble, auto-engage" still wins.
        var vm = NewVm();
        int asks = 0;
        vm.OverlayConsentRequested += (_, _) => asks++;

        vm.OverlayConsentOneTap = true;
        vm.AlwaysAllowOverlay = true;

        Assert.False(vm.ShouldHoldMaskForConsent(8));   // Auto Protect branch → un-held, no bubble
        Assert.Equal(0, asks);
    }

    [Fact]
    public void OneTapConsent_Persists_AndResetPreferencesClearsIt()
    {
        UserPreferences.Current.OverlayConsentOneTap = true;
        UserPreferences.Current.Save();
        UserPreferences.ResetCacheForTesting();
        Assert.True(UserPreferences.Current.OverlayConsentOneTap);   // survives a reload

        UserPreferences.ResetToDefaults();                            // Settings → Reset preferences
        Assert.False(UserPreferences.Current.OverlayConsentOneTap);   // cleared, per the chosen reset scope
    }

    [Fact]
    public void Decline_StaysDormant_NoReAskWhileCoolingDown()
    {
        var vm = NewVm();
        int asks = 0;
        vm.OverlayConsentRequested += (_, _) => asks++;

        Assert.True(vm.ShouldHoldMaskForConsent(6)); // ask #1
        vm.DeclineOverlayConsent();                  // cooldown begins (from the ask)
        Assert.True(vm.ShouldHoldMaskForConsent(7)); // still dormant → held
        Assert.Equal(1, asks);                       // but no re-ask while cooling down
    }

    // ── R3 #5 (Kirby 2026-06-20): the bubble is a STABLE prompt — transient tier dips must NOT retract it
    //    (the bug that made "Start" un-clickable during a motion video); it tucks away only after the full
    //    linger, suspends the tuck while the card is expanded, re-appears after the cooldown, and is
    //    retracted on overlay-off. ──

    [Fact]
    public void PendingBubble_SurvivesTierDips_NotRetracted()
    {
        var vm = NewVm();
        long tick = 0; vm.ConsentTickProvider = () => tick;
        int asks = 0, dismisses = 0;
        vm.OverlayConsentRequested += (_, _) => asks++;
        vm.OverlayConsentDismissRequested += (_, _) => dismisses++;

        Assert.True(vm.ShouldHoldMaskForConsent(6)); // ask
        Assert.Equal(1, asks);
        // Motion-video flicker: the natural tier dips below the impactful line again and again.
        Assert.True(vm.ShouldHoldMaskForConsent(3));
        Assert.True(vm.ShouldHoldMaskForConsent(2));
        Assert.True(vm.ShouldHoldMaskForConsent(5));
        Assert.Equal(0, dismisses);                  // the card is NEVER torn away
        Assert.True(vm.ConsentPending);
    }

    [Fact]
    public void PendingBubble_TucksAway_AfterTheFullLinger()
    {
        var vm = NewVm();
        long tick = 0; vm.ConsentTickProvider = () => tick;
        int dismisses = 0;
        vm.OverlayConsentDismissRequested += (_, _) => dismisses++;

        Assert.True(vm.ShouldHoldMaskForConsent(6));                  // ask at tick 0
        tick = MainViewModel.ConsentLingerMs - 1;
        Assert.True(vm.ShouldHoldMaskForConsent(6));                  // just under the linger → still up
        Assert.Equal(0, dismisses);
        tick = MainViewModel.ConsentLingerMs;
        Assert.True(vm.ShouldHoldMaskForConsent(6));                  // linger elapsed → tuck away
        Assert.Equal(1, dismisses);
        Assert.False(vm.ConsentPending);
    }

    [Fact]
    public void ExpandedCard_SuspendsTheTuckAway_UntilCollapsed()
    {
        var vm = NewVm();
        long tick = 0; vm.ConsentTickProvider = () => tick;
        int dismisses = 0;
        vm.OverlayConsentDismissRequested += (_, _) => dismisses++;

        Assert.True(vm.ShouldHoldMaskForConsent(6)); // ask
        vm.SetConsentInteracting(true);              // user opened the card
        tick = MainViewModel.ConsentLingerMs * 3;    // long past the linger
        Assert.True(vm.ShouldHoldMaskForConsent(6));
        Assert.Equal(0, dismisses);                  // not pulled away mid-decision
        vm.SetConsentInteracting(false);             // user collapsed without answering
        Assert.True(vm.ShouldHoldMaskForConsent(6));
        Assert.Equal(1, dismisses);                  // now it tucks away
    }

    [Fact]
    public void TuckedBubble_ReAppears_OnceCooldownElapses()
    {
        var vm = NewVm();
        vm.OverlayConsentGovernorForTests.NoAnswerCooldown = TimeSpan.Zero; // bypass the real-time cooldown
        long tick = 0; vm.ConsentTickProvider = () => tick;
        int asks = 0;
        vm.OverlayConsentRequested += (_, _) => asks++;

        Assert.True(vm.ShouldHoldMaskForConsent(6)); // ask #1
        tick = MainViewModel.ConsentLingerMs;
        Assert.True(vm.ShouldHoldMaskForConsent(6)); // tuck away (cooldown re-armed from here)
        Assert.Equal(1, asks);
        Assert.True(vm.ShouldHoldMaskForConsent(6)); // cooldown == 0 → re-ask on the next impactful tier
        Assert.Equal(2, asks);
    }

    [Fact]
    public void CancelPendingConsent_RetractsTheBubble_AndIsNoOpWhenIdle()
    {
        var vm = NewVm();
        int dismisses = 0;
        vm.OverlayConsentDismissRequested += (_, _) => dismisses++;

        Assert.True(vm.ShouldHoldMaskForConsent(6)); // ask
        Assert.True(vm.ConsentPending);
        vm.CancelPendingConsent();                   // overlay turned off while pending
        Assert.Equal(1, dismisses);
        Assert.False(vm.ConsentPending);
        vm.CancelPendingConsent();                   // nothing pending → no-op
        Assert.Equal(1, dismisses);
    }
}
