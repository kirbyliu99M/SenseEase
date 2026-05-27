using SenseEase.App.Services;
using Xunit;

namespace SenseEase.Tests;

/// <summary>Pop-up sprint Task 7 — pins the validation-mutex truth table
/// so a future refactor can't accidentally let the BarStub and the
/// production bar fight over the same monitor coordinates, or let
/// legacy mode (UseNewBarOverlay=false) still spawn the bar.</summary>
public class PopupSurfaceGateTests
{
    // ── Validation stub gate ─────────────────────────────────────────────────

    [Theory]
    [InlineData("1",     true)]   // env var = "1" → stub spawns
    [InlineData("0",     false)]  // any other value → stub does NOT spawn
    [InlineData("true",  false)]  // string "true" is NOT "1" — Ordinal compare
    [InlineData("",      false)]
    [InlineData(null,    false)]
    public void ShouldSpawnValidationStub_OnlyTrueForLiteralOne(string? envVal, bool expected)
    {
        Assert.Equal(expected, PopupSurfaceGate.ShouldSpawnValidationStub(envVal));
    }

    // ── Production bar+tray gate (mutex with validation, + UseNewBarOverlay) ─

    [Fact]
    public void ProductionGate_DefaultEnvAndNewOverlayOn_SpawnsBarAndTray()
    {
        // The default user state: no validation env var, new overlay enabled.
        Assert.True(PopupSurfaceGate.ShouldSpawnProductionBarAndTray(
            envVarValue: null, useNewBarOverlay: true));
    }

    [Fact]
    public void ProductionGate_ValidationEnvSet_SuppressesProduction_RegardlessOfNewOverlay()
    {
        // When VALIDATION=1 is set, production must NOT spawn even if
        // UseNewBarOverlay is true — they'd fight over monitor coords.
        Assert.False(PopupSurfaceGate.ShouldSpawnProductionBarAndTray(
            envVarValue: "1", useNewBarOverlay: true));
        Assert.False(PopupSurfaceGate.ShouldSpawnProductionBarAndTray(
            envVarValue: "1", useNewBarOverlay: false));
    }

    [Fact]
    public void ProductionGate_LegacyMode_SuppressesBarAndTray()
    {
        // UseNewBarOverlay=false (emergency rollback) → no bar, no tray,
        // legacy SpikePopupWindow path is the only notification surface.
        Assert.False(PopupSurfaceGate.ShouldSpawnProductionBarAndTray(
            envVarValue: null, useNewBarOverlay: false));
    }

    [Fact]
    public void ProductionGate_MutexWithValidation_IsExhaustive()
    {
        // Truth-table sweep — duplicates the docstring table in code so
        // a future docstring edit that diverges from behaviour fails CI.
        // (env, newOverlay) → expected production spawn
        var cases = new (string? env, bool newOverlay, bool expected)[]
        {
            ("1",  true,  false),
            ("1",  false, false),
            (null, true,  true),
            (null, false, false),
        };
        foreach (var (env, newOverlay, expected) in cases)
        {
            Assert.Equal(expected,
                PopupSurfaceGate.ShouldSpawnProductionBarAndTray(env, newOverlay));
        }
    }

    [Fact]
    public void ValidationStub_AndProductionBar_AreMutuallyExclusive()
    {
        // Cross-check: under any (env, newOverlay) combination, BarStub
        // and production bar must never both want to spawn.
        var envVars = new[] { null, "0", "1", "true", "" };
        var newOverlays = new[] { true, false };
        foreach (var env in envVars)
        foreach (var newOverlay in newOverlays)
        {
            bool stub = PopupSurfaceGate.ShouldSpawnValidationStub(env);
            bool prod = PopupSurfaceGate.ShouldSpawnProductionBarAndTray(env, newOverlay);
            Assert.False(stub && prod,
                $"Mutex violation: env='{env}' newOverlay={newOverlay} → both stub and prod true");
        }
    }
}
