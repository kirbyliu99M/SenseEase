using SenseEase.Core.Configuration;
using SenseEase.Core.Inference;
using SenseEase.Core.Models;
using Xunit;

namespace SenseEase.Tests;

/// <summary>G1 (Alpha-2) — strict-language + slash-command parser. Pins every slash
/// branch, the /mask&lt;20 + "mask off" safety rejections + the mask floor, the /fov
/// out-of-range rejection, the whole-utterance strict-NL whitelist (en+zh) AND a
/// negation NON-match, plus /help + /show kinds. The lexicon is PURE — these tests
/// touch no engine. Replies default to ZH (isEnglish:false) unless noted.</summary>
public class ShaderCommandLexiconTests
{
    // ── /grid branches ────────────────────────────────────────────────────────

    [Fact]
    public void Slash_GridOff_SetsGridVisibilityZero()
    {
        var cmd = ShaderCommandLexicon.Parse("/grid off", isEnglish: true);
        Assert.Equal(ShaderCommandKind.SetParam, cmd.Kind);
        Assert.Equal(ShaderParam.GridVisibility, cmd.Param);
        Assert.Equal(0.0, cmd.Value);
        Assert.Contains("hidden", cmd.Reply);
    }

    [Fact]
    public void Slash_GridOn_SetsGridVisibilityProfileDefault()
    {
        var cmd = ShaderCommandLexicon.Parse("/grid on", isEnglish: true);
        Assert.Equal(ShaderCommandKind.SetParam, cmd.Kind);
        Assert.Equal(ShaderParam.GridVisibility, cmd.Param);
        Assert.Equal(ShaderParams.DefaultGridVisibility, cmd.Value);
    }

    [Theory]
    [InlineData("/grid 0", 0.0)]
    [InlineData("/grid 50", 0.50)]
    [InlineData("/grid 100", 1.0)]
    public void Slash_GridPercent_SetsGridVisibility(string input, double expected)
    {
        var cmd = ShaderCommandLexicon.Parse(input, isEnglish: true);
        Assert.Equal(ShaderCommandKind.SetParam, cmd.Kind);
        Assert.Equal(ShaderParam.GridVisibility, cmd.Param);
        Assert.Equal(expected, cmd.Value, 3);
    }

    [Fact]
    public void Slash_GridPercent_Above100_Rejected()
    {
        var cmd = ShaderCommandLexicon.Parse("/grid 150", isEnglish: true);
        Assert.Equal(ShaderCommandKind.Reject, cmd.Kind);
    }

    // ── /mask branches + safety floor ─────────────────────────────────────────

    [Theory]
    [InlineData("/mask 20", 0.20)]
    [InlineData("/mask 74", 0.74)]
    [InlineData("/mask 100", 1.0)]
    public void Slash_Mask_AtOrAboveFloor_SetsMaskDepth(string input, double expected)
    {
        var cmd = ShaderCommandLexicon.Parse(input, isEnglish: true);
        Assert.Equal(ShaderCommandKind.SetParam, cmd.Kind);
        Assert.Equal(ShaderParam.MaskDepth, cmd.Param);
        Assert.Equal(expected, cmd.Value, 3);
    }

    [Theory]
    [InlineData("/mask 0")]
    [InlineData("/mask 15")]
    [InlineData("/mask 19")]
    public void Slash_Mask_BelowFloor_Rejected(string input)
    {
        var cmd = ShaderCommandLexicon.Parse(input, isEnglish: true);
        Assert.Equal(ShaderCommandKind.Reject, cmd.Kind);
        Assert.Equal("slash.mask_floor", cmd.PatternId);
        Assert.Contains("/mask 20", cmd.Reply);
    }

    [Fact]
    public void MaskDepthSafetyFloor_Is020()
    {
        Assert.Equal(0.20, ShaderCommandLexicon.MaskDepthSafetyFloor);
    }

    // ── /feather ──────────────────────────────────────────────────────────────

    [Fact]
    public void Slash_Feather_SetsFeatherWidth()
    {
        var cmd = ShaderCommandLexicon.Parse("/feather 30", isEnglish: true);
        Assert.Equal(ShaderCommandKind.SetParam, cmd.Kind);
        Assert.Equal(ShaderParam.FeatherWidth, cmd.Param);
        Assert.Equal(0.30, cmd.Value, 3);
    }

    // ── CW-2 CLOSED (D-2, Beta A2): /mask and /feather are now REAL renderer inputs
    // (the single-pass field renders them live), so their confirmations un-interim — they
    // claim the change applied ("set to" / "已設為"), like /grid and /fov, because it now
    // actually does. The old "recorded only / Beta renderer" caveat must be GONE.

    [Theory]
    [InlineData("/mask 74")]
    [InlineData("/feather 30")]
    public void Slash_LiveKnobs_Confirmation_IsAppliedAndTrue_En(string input)
    {
        var cmd = ShaderCommandLexicon.Parse(input, isEnglish: true);
        Assert.Equal(ShaderCommandKind.SetParam, cmd.Kind);
        Assert.Contains("set to", cmd.Reply);
        Assert.DoesNotContain("recorded", cmd.Reply);
        Assert.DoesNotContain("Beta renderer", cmd.Reply);
    }

    [Theory]
    [InlineData("/mask 74")]
    [InlineData("/feather 30")]
    public void Slash_LiveKnobs_Confirmation_IsAppliedAndTrue_Zh(string input)
    {
        var cmd = ShaderCommandLexicon.Parse(input, isEnglish: false);
        Assert.Equal(ShaderCommandKind.SetParam, cmd.Kind);
        Assert.Contains("已設為", cmd.Reply);
        Assert.DoesNotContain("已記錄", cmd.Reply);
        Assert.DoesNotContain("Beta 版渲染引擎", cmd.Reply);
    }

    // ── /fov range ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/fov 15", 15)]
    [InlineData("/fov 60", 60)]
    [InlineData("/fov 100", 100)]
    public void Slash_Fov_InRange_SetsFovRadius(string input, double expected)
    {
        var cmd = ShaderCommandLexicon.Parse(input, isEnglish: true);
        Assert.Equal(ShaderCommandKind.SetParam, cmd.Kind);
        Assert.Equal(ShaderParam.FovRadius, cmd.Param);
        Assert.Equal(expected, cmd.Value);
    }

    [Theory]
    [InlineData("/fov 14")]
    [InlineData("/fov 0")]
    [InlineData("/fov 150")]
    [InlineData("/fov 101")]
    public void Slash_Fov_OutOfRange_Rejected(string input)
    {
        var cmd = ShaderCommandLexicon.Parse(input, isEnglish: true);
        Assert.Equal(ShaderCommandKind.Reject, cmd.Kind);
        Assert.Equal("slash.fov_range", cmd.PatternId);
    }

    // ── /reset shader ─────────────────────────────────────────────────────────

    [Fact]
    public void Slash_ResetShader_KindResetShader()
    {
        var cmd = ShaderCommandLexicon.Parse("/reset shader", isEnglish: true);
        Assert.Equal(ShaderCommandKind.ResetShader, cmd.Kind);
    }

    [Fact]
    public void Slash_ResetAlone_Rejected()
    {
        var cmd = ShaderCommandLexicon.Parse("/reset", isEnglish: true);
        Assert.Equal(ShaderCommandKind.Reject, cmd.Kind);
    }

    // ── /help + /show kinds ───────────────────────────────────────────────────

    [Fact]
    public void Slash_Help_KindHelp_WithText()
    {
        var cmd = ShaderCommandLexicon.Parse("/help", isEnglish: true);
        Assert.Equal(ShaderCommandKind.Help, cmd.Kind);
        Assert.False(string.IsNullOrWhiteSpace(cmd.Reply));
        Assert.Contains("/grid", cmd.Reply);
    }

    [Fact]
    public void Slash_Show_KindShow_ReplyComposedByCaller()
    {
        var cmd = ShaderCommandLexicon.Parse("/show", isEnglish: true);
        Assert.Equal(ShaderCommandKind.Show, cmd.Kind);
        // Show reply is composed by the caller from live engine values.
        Assert.Equal(string.Empty, cmd.Reply);
    }

    // ── Case-insensitivity + leading-token + extra-args (locked #4/#5) ─────────

    [Fact]
    public void Slash_CaseInsensitive()
    {
        var cmd = ShaderCommandLexicon.Parse("/GRID OFF", isEnglish: true);
        Assert.Equal(ShaderCommandKind.SetParam, cmd.Kind);
        Assert.Equal(0.0, cmd.Value);
    }

    [Fact]
    public void Slash_ExtraArgs_Rejected()
    {
        var cmd = ShaderCommandLexicon.Parse("/grid off please", isEnglish: true);
        Assert.Equal(ShaderCommandKind.Reject, cmd.Kind);
    }

    [Fact]
    public void Slash_Chaining_Rejected()
    {
        var cmd = ShaderCommandLexicon.Parse("/grid off; /mask 30", isEnglish: true);
        Assert.Equal(ShaderCommandKind.Reject, cmd.Kind);
        Assert.Equal("slash.chaining", cmd.PatternId);
    }

    [Fact]
    public void Slash_Unknown_Rejected()
    {
        var cmd = ShaderCommandLexicon.Parse("/profile sensitive", isEnglish: true);
        Assert.Equal(ShaderCommandKind.Reject, cmd.Kind);
    }

    // ── Strict-NL whole-utterance whitelist (en + zh) ─────────────────────────

    [Theory]
    [InlineData("grid off")]
    [InlineData("Grid Off")]      // case-insensitive
    [InlineData("hide grid")]
    [InlineData("turn off the grid")]
    [InlineData("關掉格線")]
    [InlineData("隱藏格線")]
    [InlineData("格線關掉")]
    [InlineData("hide grid.")]    // trailing punctuation tolerated
    public void StrictNl_GridOff_Matches(string input)
    {
        var cmd = ShaderCommandLexicon.Parse(input, isEnglish: true);
        Assert.Equal(ShaderCommandKind.SetParam, cmd.Kind);
        Assert.Equal(ShaderParam.GridVisibility, cmd.Param);
        Assert.Equal(0.0, cmd.Value);
    }

    [Theory]
    [InlineData("grid on")]
    [InlineData("show grid")]
    [InlineData("打開格線")]
    [InlineData("顯示格線")]
    public void StrictNl_GridOn_Matches(string input)
    {
        var cmd = ShaderCommandLexicon.Parse(input, isEnglish: true);
        Assert.Equal(ShaderCommandKind.SetParam, cmd.Kind);
        Assert.Equal(ShaderParam.GridVisibility, cmd.Param);
        Assert.Equal(ShaderParams.DefaultGridVisibility, cmd.Value);
    }

    [Theory]
    [InlineData("mask off")]
    [InlineData("拿掉遮罩")]
    [InlineData("關掉遮罩")]
    public void StrictNl_MaskOff_Rejected_SuggestsMask20(string input)
    {
        var cmd = ShaderCommandLexicon.Parse(input, isEnglish: true);
        Assert.Equal(ShaderCommandKind.Reject, cmd.Kind);
        Assert.Equal("nl.mask_off", cmd.PatternId);
        Assert.Contains("/mask 20", cmd.Reply);
    }

    [Theory]
    [InlineData("reset shader")]
    [InlineData("reset overlay")]
    [InlineData("重設遮罩")]
    [InlineData("重置畫面")]
    public void StrictNl_Reset_Matches(string input)
    {
        var cmd = ShaderCommandLexicon.Parse(input, isEnglish: true);
        Assert.Equal(ShaderCommandKind.ResetShader, cmd.Kind);
    }

    [Theory]
    [InlineData("show settings")]
    [InlineData("current settings")]
    [InlineData("目前設定")]
    [InlineData("現在的設定")]
    public void StrictNl_Show_Matches(string input)
    {
        var cmd = ShaderCommandLexicon.Parse(input, isEnglish: true);
        Assert.Equal(ShaderCommandKind.Show, cmd.Kind);
    }

    // ── Negation / false-positive guard (locked #6: whole-utterance only) ─────

    [Theory]
    [InlineData("I don't want the grid off")]
    [InlineData("I don't want the grid off, why is it gone?")]
    [InlineData("why is the grid off")]
    [InlineData("can you tell me about grid off")]
    public void StrictNl_Negation_DoesNotMatch_FallsThroughToLlm(string input)
    {
        var cmd = ShaderCommandLexicon.Parse(input, isEnglish: true);
        Assert.Equal(ShaderCommandKind.None, cmd.Kind);
    }

    [Fact]
    public void NonShaderUtterance_IsNone()
    {
        var cmd = ShaderCommandLexicon.Parse("I feel a bit dizzy today", isEnglish: true);
        Assert.Equal(ShaderCommandKind.None, cmd.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyOrNull_IsNone(string? input)
    {
        var cmd = ShaderCommandLexicon.Parse(input, isEnglish: true);
        Assert.Equal(ShaderCommandKind.None, cmd.Kind);
    }

    // ── Bilingual reply selection ─────────────────────────────────────────────

    [Fact]
    public void Reply_RespectsIsEnglishFlag()
    {
        var en = ShaderCommandLexicon.Parse("/grid off", isEnglish: true);
        var zh = ShaderCommandLexicon.Parse("/grid off", isEnglish: false);
        Assert.NotEqual(en.Reply, zh.Reply);
        Assert.Contains("hidden", en.Reply);
        Assert.Contains("格線", zh.Reply);
    }

    // ── Show-reply composer (UX table format) ─────────────────────────────────

    [Fact]
    public void ComposeShowReply_MatchesUxTableFormat()
    {
        string reply = ShaderCommandLexicon.ComposeShowReply(60, 0.74, 0.75, 0.50);
        Assert.Equal("FOV: 60% · Mask: 74% · Grid: 75% · Feather: 50%", reply);
    }

    [Fact]
    public void FovRange_UsesMinComposedFovRadiusConstant()
    {
        // /fov 14 is rejected because MinComposedFovRadius == 15.
        Assert.Equal(15, SenseDefaults.MinComposedFovRadius);
    }
}
