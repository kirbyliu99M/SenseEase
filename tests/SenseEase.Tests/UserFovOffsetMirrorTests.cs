using SenseEase.Core.Abstractions;
using SenseEase.Core.Models;
using SenseEase.Infrastructure.Mock;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// Honest FOV-card hint (Kirby-approved, 2026-06-11): the engine's accumulated chatbot FOV
/// personalization (<c>_userFovOffset</c>, &lt;= 0) is mirrored onto
/// <see cref="SenseState.UserFovOffsetPp"/> beside every <c>FovRadiusPercent</c> compose, so
/// the dashboard can HONESTLY explain why the resting clear-zone reads below the natural
/// baseline. These tests pin the mirror across the three publication paths the hint depends on:
/// a live chatbot FOV delta, a personalization reset, and the ctor replay from a seeded log.
/// </summary>
public class UserFovOffsetMirrorTests
{
    /// <summary>Stub log that reports a fixed FovRadius net delta, so the ctor replay path
    /// (<c>_userFovOffset = Clamp((int)NetDelta, -60, 0)</c>) seeds a known value without
    /// touching the filesystem — mirrors the StubAdjustmentLog pattern used elsewhere.</summary>
    private sealed class SeededFovLog : IAdjustmentLog
    {
        private readonly double _fovNetDelta;
        public SeededFovLog(double fovNetDelta) => _fovNetDelta = fovNetDelta;
        public void Append(AdjustmentRecord record) { }
        public System.Collections.Generic.IReadOnlyList<AdjustmentRecord> All()
            => System.Array.Empty<AdjustmentRecord>();
        public PersonalWeight WeightFor(string parameter)
            => parameter == "FovRadius"
                ? new PersonalWeight(parameter, 1, _fovNetDelta, _fovNetDelta)
                : new PersonalWeight(parameter, 0, 0, 0);
    }

    [Fact]
    public void ApplyUserFeedback_NegativeFovDelta_DropsStateOffset()
    {
        var engine = new MockInferenceEngine();
        Assert.Equal(0, engine.Current.UserFovOffsetPp); // no personalization at rest

        engine.ApplyUserFeedback(-28); // the chatbot "tighten the clear zone" path

        // The state now carries the offset (single source = _userFovOffset) so the card hint
        // can read it. ApplyUserFeedback refreshes Current immediately (no Update tick needed).
        Assert.Equal(-28, engine.Current.UserFovOffsetPp);
        Assert.Equal(engine.UserFovOffset, engine.Current.UserFovOffsetPp);
        Assert.True(engine.Current.UserFovOffsetPp <= 0); // tighten-only invariant
    }

    [Fact]
    public void ApplyUserFeedback_AccumulatesAndMirrors()
    {
        var engine = new MockInferenceEngine();
        engine.ApplyUserFeedback(-10);
        engine.ApplyUserFeedback(-8);

        // Two tightening nudges accumulate (clamped -60..0) and the state tracks the running sum.
        Assert.Equal(-18, engine.Current.UserFovOffsetPp);
    }

    [Fact]
    public void ResetPersonalization_RestoresZero_AndStateReflectsIt()
    {
        var engine = new MockInferenceEngine();
        engine.ApplyUserFeedback(-28);
        Assert.Equal(-28, engine.Current.UserFovOffsetPp);

        engine.ResetPersonalization();

        // Back to plain: the offset is zeroed AND the state mirrors it immediately, so the
        // card caption drops the （含個人化 −N） parenthetical without waiting for an Update tick.
        Assert.Equal(0, engine.Current.UserFovOffsetPp);
        Assert.Equal(0, engine.UserFovOffset);
    }

    [Fact]
    public void UpdateTick_MirrorsCurrentOffset()
    {
        var engine = new MockInferenceEngine();
        engine.ApplyUserFeedback(-15);

        // A normal sensing tick re-composes FovRadiusPercent and must carry the offset along.
        SenseState state = engine.Update(0, "Passenger");

        Assert.Equal(-15, state.UserFovOffsetPp);
    }

    [Fact]
    public void Ctor_ReplaysOffsetFromSeededLog_IntoState()
    {
        // The user's persisted chatbot FOV personalization (here -28pp, matching Kirby's log)
        // is replayed at construction (_userFovOffset = Clamp((int)NetDelta, -60, 0)) and must
        // already be visible on Current before any Update tick — this is the boot path the
        // resting 72%-with-no-explanation card lives on.
        var engine = new MockInferenceEngine(new SeededFovLog(-28.0));

        Assert.Equal(-28, engine.UserFovOffset);
        Assert.Equal(-28, engine.Current.UserFovOffsetPp);
    }

    [Fact]
    public void Ctor_NoLog_StateOffsetIsZero()
    {
        // No adjustment log ⇒ no personalization ⇒ plain caption (byte-identical to today).
        var engine = new MockInferenceEngine();
        Assert.Equal(0, engine.Current.UserFovOffsetPp);
    }
}
