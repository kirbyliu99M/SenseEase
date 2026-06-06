using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SenseEase.App.ViewModels;
using SenseEase.Core.Abstractions;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// P2 (Pause/Resume mode-shift — Wallpaper-Engine style): the single user-pause
/// control becomes a full pause that also GATES the chatbot, and on RESUME any
/// queued proposal is re-screened (never auto-applied across the pause boundary).
///
/// <para>These tests pin the two halves of that contract that are unit-testable
/// WITHOUT <c>Application.Current</c>:</para>
/// <list type="number">
///   <item>(a) <see cref="ChatbotViewModel.IsPaused"/> gates <see cref="ChatbotViewModel.SendCommand"/>:
///   the command is executable with non-empty input while running, and becomes
///   non-executable the instant the VM is paused.</item>
///   <item>(b) the resume-safety INVARIANT at the layer that is testable —
///   <see cref="HabituationOverrideApplicator.CheckSafetyIntercepts"/> blocks a
///   vestibular + relaxation proposal. This is the EXACT gate that
///   <c>MainViewModel.RevalidateActiveProposalAfterResume()</c> re-fires on resume;
///   the VM wiring itself (ChatbotVm.IsPaused flip + the Revalidate call from
///   MainWindow.ApplyPowerSave) is covered by code review because MainViewModel
///   needs <c>Application.Current</c> + engine wiring and cannot be unit-constructed
///   in this project (see <see cref="D6DayOneProtectionTests"/>' note).</item>
/// </list>
/// </summary>
public class PauseResumeTests
{
    // ── Stubs (mirror ChatbotViewModelSafetyFloorTests.BuildVm) ──────────────

    private sealed class StubChatbotService : IChatbotService
    {
        public bool IsModelAvailable => false;
        public bool IsModelLoaded => false;
        public IReadOnlyList<ChatMessage> History { get; } = new List<ChatMessage>();
        public Task LoadAsync() => Task.CompletedTask;
        public Task<NluResult> SendAsync(string userMessage, CancellationToken ct = default)
            => Task.FromResult(new NluResult { Intent = "Unknown", Response = "LLM-SHOULD-NOT-RUN" });
        public void ClearHistory() { }
        public void Dispose() { }
    }

    private sealed class StubAdjustmentLog : IAdjustmentLog
    {
        public void Append(AdjustmentRecord record) { }
        public IReadOnlyList<AdjustmentRecord> All() => Array.Empty<AdjustmentRecord>();
        public PersonalWeight WeightFor(string parameter) => new(parameter, 0, 0, 0);
    }

    private static ChatbotViewModel BuildVm()
    {
        var vm = new ChatbotViewModel(
            chatbot: new StubChatbotService(),
            engine: new MockInferenceEngine(),
            adjustmentLog: new StubAdjustmentLog(),
            isOverlayVisible: () => false);
        vm.LoadHabituationSnapshot = () => null;
        return vm;
    }

    // ── (a) IsPaused gates SendCommand.CanExecute ────────────────────────────

    /// <summary>While paused, the chatbot send must be disabled even when the user
    /// has typed something; un-pausing re-enables it. This is the P2 chatbot gate
    /// (ChatbotViewModel.IsPaused → SendCommand.CanExecute false).</summary>
    [Fact]
    public void IsPaused_GatesSendCommand_CanExecute()
    {
        var vm = BuildVm();

        // Non-empty input so the only variable under test is IsPaused.
        vm.InputText = "please relax sensitivity a little";

        // Running (not paused): send is available.
        vm.IsPaused = false;
        Assert.True(vm.SendCommand.CanExecute(null),
            "with non-empty input and not paused, SendCommand must be executable");

        // Paused: send is gated off.
        vm.IsPaused = true;
        Assert.False(vm.SendCommand.CanExecute(null),
            "while paused, SendCommand must NOT be executable (P2 chatbot gate)");

        // Resume: send is available again.
        vm.IsPaused = false;
        Assert.True(vm.SendCommand.CanExecute(null),
            "after un-pausing, SendCommand must be executable again");
    }

    // ── (b) Resume re-screen invariant (the gate Revalidate re-fires) ────────

    /// <summary>The resume-safety invariant: a vestibular user's relaxation proposal
    /// is BLOCKED by <see cref="HabituationOverrideApplicator.CheckSafetyIntercepts"/>.
    /// On resume, <c>MainViewModel.RevalidateActiveProposalAfterResume()</c> loads a
    /// fresh snapshot and re-fires exactly this predicate; a Blocked result clears /
    /// expires the proposal (it must NOT call ApplyAccepted). Mirrors
    /// <c>HabituationOverrideApplicatorTests</c>' vestibular Rule-2 case.
    ///
    /// <para>The MainViewModel wiring (ChatbotVm.IsPaused flip on enable, and the
    /// Revalidate call on disable from MainWindow.ApplyPowerSave) is covered by code
    /// review since MainViewModel can't be unit-constructed without Application.Current.</para></summary>
    [Fact]
    public void ResumeRevalidation_VestibularRelaxation_IsBlocked()
    {
        // scale 0.8 < current 1.0 ⇒ relaxation; vestibular advisory ⇒ blocked.
        var snapshot = new HabituationProfileSnapshot
        {
            SchemaVersion = 2,
            Profile = "TYPICAL",
            ProfileSource = "calibration_demo",
            PersonalPressureScale = 1.0,
            SevereStrikeLimit = 2,
            MedicalAdvisoryShown = MedicalAdvisory.Vestibular,
        };
        var proposal = new ProfileOverrideProposal(
            Intent: OverrideIntent.AdjustSensitivity,
            TargetField: "adjustsensitivity",
            ProposedValue: 0.8,            // relaxation
            Rationale: "test rationale",
            ExpiresAtUtc: DateTime.UtcNow.AddDays(2));

        var result = HabituationOverrideApplicator.CheckSafetyIntercepts(proposal, snapshot);

        Assert.True(result.Blocked,
            "a vestibular user's relaxation proposal must be intercepted on resume re-screen");
        Assert.Contains("vestibular", result.Reason!);
    }
}
