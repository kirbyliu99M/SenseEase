using System;
using SenseEase.App.Services;
using SenseEase.App.ViewModels;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Item 9 — the intensity DRAG SLIDER. The bar's <see cref="PopupBarViewModel.IntensityLevel"/> is
/// TWO-WAY: a user drag routes to the controller's ABSOLUTE setter exactly once (no feedback echo),
/// an engine push via <c>ApplyState</c> updates the readout WITHOUT calling back, and an unchanged
/// set is a no-op (absorbs the slider's snap echo). Also pins
/// <see cref="MainViewModel.SetIntensityLevel"/> — the absolute always-on baseline setter.
/// </summary>
public class PopupBarIntensityTests
{
    private sealed class FakeShellController : IShellController
    {
        private int _tier;
        public FakeShellController(int tier) { _tier = tier; }

        public int SetIntensityLevelCalls { get; private set; }
        public int LastSetLevel { get; private set; }

        public int CurrentTier => _tier;
        public bool IsPaused => false;
        public event EventHandler? StateChanged;
        public event EventHandler? QuickSettingsChanged;

        public int SetIntensityLevel(int level)
        {
            SetIntensityLevelCalls++;
            LastSetLevel = level;
            _tier = Math.Clamp(level, 1, 12);
            StateChanged?.Invoke(this, EventArgs.Empty); // mirror the real engine push-back
            QuickSettingsChanged?.Invoke(this, EventArgs.Empty); // real controller fires both on a state tick
            return _tier;
        }

        /// <summary>Simulate an engine-driven tier change (no user action).</summary>
        public void PushTier(int tier)
        {
            _tier = Math.Clamp(tier, 1, 12);
            StateChanged?.Invoke(this, EventArgs.Empty);
            QuickSettingsChanged?.Invoke(this, EventArgs.Empty);
        }

        public void PlayPause() { }
        public void NudgeStronger() { }
        public void NudgeGentler() { }
        public void OpenChat() { }
        public void OpenSettings() { }
        public void OpenLogs() { }
        public void OpenConsole() { }
        public void RegisterSurface(IShellSurface surface) { }
        public void UnregisterSurface(IShellSurface surface) { }
        public bool AnchorGridEnabled { get; set; }
        public bool GradedExposureEnabled { get; set; }
        public bool PersistentMaskEnabled { get; set; }
        public bool AlwaysAllowOverlay { get; set; }
    }

    [Fact]
    public void IntensityLevel_Drag_RoutesOneAbsoluteSet_NoEcho()
    {
        var ctl = new FakeShellController(5);
        var vm = new PopupBarViewModel(ctl);
        Assert.Equal(5, vm.IntensityLevel);

        vm.IntensityLevel = 9; // user drag

        Assert.Equal(1, ctl.SetIntensityLevelCalls); // exactly one — the ApplyState push-back must NOT echo
        Assert.Equal(9, ctl.LastSetLevel);
        Assert.Equal(9, vm.IntensityLevel);          // engine push-back reflected
        Assert.Equal("Lv.09", vm.LevelText);
    }

    [Fact]
    public void IntensityLevel_SetEqualToCurrent_IsNoOp()
    {
        var ctl = new FakeShellController(7);
        var vm = new PopupBarViewModel(ctl);

        vm.IntensityLevel = 7; // unchanged (snap echo)

        Assert.Equal(0, ctl.SetIntensityLevelCalls);
    }

    [Fact]
    public void IntensityLevel_ClampsTo_1_12()
    {
        var ctl = new FakeShellController(5);
        var vm = new PopupBarViewModel(ctl);

        vm.IntensityLevel = 99;
        Assert.Equal(12, ctl.LastSetLevel);
    }

    [Fact]
    public void ApplyState_EnginePush_UpdatesReadout_WithoutCallingController()
    {
        var ctl = new FakeShellController(5);
        var vm = new PopupBarViewModel(ctl);

        ctl.PushTier(10); // engine-driven, not a user drag

        Assert.Equal(10, vm.IntensityLevel);
        Assert.Equal("Lv.10", vm.LevelText);
        Assert.Equal(0, ctl.SetIntensityLevelCalls); // the _applyingEngineLevel guard prevents a feedback call
    }

    [Fact]
    public void IntensityLevel_Standalone_NoController_SetsOptimistically()
    {
        var vm = new PopupBarViewModel(); // no controller (design-time / tests)

        vm.IntensityLevel = 9;
        Assert.Equal(9, vm.IntensityLevel);

        vm.IntensityLevel = 99;
        Assert.Equal(12, vm.IntensityLevel);
    }

    [Fact]
    public void MainViewModel_SetIntensityLevel_SetsBaseline_Clamps_AndFloors()
    {
        var vm = new MainViewModel();

        vm.SetIntensityLevel(1);  // normalize to the floor first (deterministic regardless of persisted state)
        vm.SetIntensityLevel(9);  // raise to an absolute mid level
        Assert.Equal(9, vm.PersistentOverlayBaselineTier);
        Assert.True(vm.PersistentOverlayEnabled);

        vm.SetIntensityLevel(50); // clamp above 12
        Assert.Equal(12, vm.PersistentOverlayBaselineTier);

        vm.SetIntensityLevel(1);  // floor → drop the baseline + disable the always-on overlay
        Assert.Equal(1, vm.PersistentOverlayBaselineTier);
        Assert.False(vm.PersistentOverlayEnabled);
    }
}
