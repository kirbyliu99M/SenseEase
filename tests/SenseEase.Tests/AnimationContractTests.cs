using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// R3-7 ANIMATION-CONTRACT GUARD (added 2026-06-19, after commit e07e06d "flash-free popup glass-bar
/// animation (fixed-window reshape)"). The flash-free popup motion is hard-won and rests on a handful of
/// STRUCTURAL invariants that are invisible in a normal diff — so an unrelated edit (e.g. the code-review
/// bug-fix wave) can silently regress it. These static lints read the committed XAML/C# and fail loudly
/// if an invariant is broken. The "why" behind each rule lives in docs/ANIMATION_CONTRACT.md and the
/// motion-design-thesis memory note.
///
/// SCOPE: structural animation invariants ONLY — NOT chat content/bindings (a parallel effort owns those).
/// If one of these fails after a DELIBERATE, coordinated animation change, update the assertion in the
/// SAME commit (and the contract doc) — never just delete the check.
/// </summary>
public class AnimationContractTests
{
    private static string Popup => ReadApp("Views/PopupBarWindow.xaml");
    private static string PopupCs => ReadApp("Views/PopupBarWindow.xaml.cs");
    private static string Shell => ReadApp("Services/ShellController.cs");
    private static string Edge => ReadApp("Views/EdgeSidebarWindow.xaml");

    // ── INV-1: FIXED WINDOW. The popup window must stay a fixed size and NEVER use SizeToContent.
    // Resizing a layered (AllowsTransparency) window re-allocates its surface = the flash R3-7 removed.
    [Fact]
    public void Inv1_PopupWindow_StaysFixedSize_NeverSizeToContent()
    {
        string head = WindowOpeningTag(Popup);
        Assert.True(head.Contains("SizeToContent=\"Manual\"", StringComparison.Ordinal),
            "INV-1 broken: PopupBarWindow must keep SizeToContent=\"Manual\". Reintroducing SizeToContent " +
            "(WidthAndHeight/Height/Width) resizes the layered window per interaction = the flash R3-7 fixed.");
        Assert.False(Regex.IsMatch(Popup, "SizeToContent=\"(WidthAndHeight|Height|Width)\""),
            "INV-1 broken: SizeToContent was set to a resizing mode on PopupBarWindow — must stay Manual.");
        Assert.True(head.Contains("ResizeMode=\"NoResize\"", StringComparison.Ordinal),
            "INV-1 broken: PopupBarWindow must keep ResizeMode=\"NoResize\".");
        Assert.True(head.Contains("AllowsTransparency=\"True\"", StringComparison.Ordinal),
            "INV-1 context changed: PopupBarWindow lost AllowsTransparency=\"True\" — the whole flash analysis assumes the layered/software-render window.");
        Assert.True(Regex.IsMatch(head, "Width=\"\\d") && Regex.IsMatch(head, "Height=\"\\d"),
            "INV-1 broken: PopupBarWindow must declare an explicit fixed Width AND Height on the Window element.");
    }

    // ── INV-2: SOFTWARE-RENDER CEILING. No Effect / BitmapCache / CacheMode on the popup. Under
    // AllowsTransparency (software render) an Effect or BitmapCache allocates an offscreen render target,
    // which disables ClearType and flashes on every interaction. R3-6 deliberately removed them.
    [Fact]
    public void Inv2_PopupBar_HasNoEffectOrBitmapCache_FlashGuard()
    {
        string xaml = StripXamlComments(Popup);
        var banned = new Regex("BitmapCache|CacheMode|DropShadowEffect|BlurEffect|<\\w+\\.Effect>|\\sEffect=\"");
        var m = banned.Match(xaml);
        Assert.False(m.Success,
            $"INV-2 broken: PopupBarWindow.xaml introduced a render target ('{(m.Success ? m.Value.Trim() : "")}'). " +
            "Effects/BitmapCache flash + kill ClearType on this layered software-rendered window. Use a static " +
            "Border/gradient (e.g. a RadialGradientBrush halo) instead of a DropShadow/Blur/cache.");
    }

    // ── INV-5/6: the animation code references these elements by name (reveal/expand/collapse/hide +
    // the slide-mask transforms). Renaming or removing one silently breaks the motion.
    [Fact]
    public void Inv5_PopupBar_KeepsAnimationReferencedNames()
    {
        string xaml = StripXamlComments(Popup);
        string[] names =
        {
            "RootGrid", "BarRoot", "BarScale",
            "ChatPanel", "ChatScale", "SettingsFlyout", "SettingsScale",
            "MeterSection", "ToolsSection", "DragHandle",
            "MeterShift", "ToolsShift", "DragShift",
            "chatNeonRim", "barNeonRim",
        };
        var missing = names.Where(n => !xaml.Contains($"x:Name=\"{n}\"", StringComparison.Ordinal)).ToArray();
        Assert.True(missing.Length == 0,
            "INV-5/6 broken: PopupBarWindow.xaml is missing animation-referenced x:Name(s): " +
            string.Join(", ", missing) + ". The reveal/expand/hide code binds these by name; renaming/removing " +
            "one breaks the motion. If a rename is intended, update PopupBarWindow.xaml.cs + this test together.");
    }

    // ── INV-1 (chat): the chat panel must stay explicitly sized (not Auto/SizeToContent), so typing never
    // reflows the fixed window. (Asserts the structural invariant, not the exact dimensions — so legitimate
    // chat refinement can retune size as long as it stays fixed.)
    [Fact]
    public void Inv1_ChatPanel_StaysExplicitlySized_NoReflow()
    {
        var m = Regex.Match(StripXamlComments(Popup), "<Border\\s+x:Name=\"ChatPanel\"[\\s\\S]*?>");
        Assert.True(m.Success, "ChatPanel border not found in PopupBarWindow.xaml.");
        Assert.True(Regex.IsMatch(m.Value, "Width=\"\\d") && Regex.IsMatch(m.Value, "Height=\"\\d"),
            "INV-1 broken: the ChatPanel must keep an explicit fixed Width AND Height (not Auto/SizeToContent) " +
            "so the message list + input growing never reflows the fixed popup window.");
    }

    // ── INV-6: every popup hide must route through DismissToHandle (clean reset + reopen-flash-free
    // condensed baseline). A raw _popup.Hide() leaves a stale expanded surface (reopen flash) AND skips
    // the VM-flag reset (the audit H6 gap).
    [Fact]
    public void Inv6_HotkeyHide_RoutesThroughDismissToHandle_NotRawPopupHide()
    {
        string shell = StripCsComments(Shell);
        Assert.False(Regex.IsMatch(shell, "_popup\\s*!?\\??\\s*\\.\\s*Hide\\s*\\("),
            "INV-6 broken: ShellController calls _popup.Hide() directly. The popup must be dismissed via " +
            "_popup.DismissToHandle() — a raw Hide() reintroduces the reopen flash (stale expanded surface) " +
            "and skips the IsChatOpen/IsSettingsOpen/IsExpanded reset.");
        Assert.True(shell.Contains("DismissToHandle", StringComparison.Ordinal),
            "INV-6 broken: ShellController no longer references DismissToHandle — the clean hide route is gone.");
    }

    // ── INV-4/6/9: the reopen-flash machinery + the clean-hide VM reset + ReducedMotion gating must survive.
    [Fact]
    public void Inv4_PopupBar_KeepsReopenFlashAndReducedMotionMachinery()
    {
        string cs = StripCsComments(PopupCs);
        foreach (var sym in new[] { "DismissToHandle", "ForceCondensedBaseline", "HideToCondensed" })
            Assert.True(cs.Contains(sym, StringComparison.Ordinal),
                $"INV-4 broken: PopupBarWindow lost {sym} — part of the R3-7 reopen-flash / clean-hide machinery.");
        Assert.True(Regex.Matches(cs, "ReducedMotion\\(\\)").Count >= 3,
            "INV-9 broken: ReducedMotion() gating dropped below the number of animated paths it must snap " +
            "(expand, card, collapse-fade, hide). A queasy user must be able to kill all motion (WCAG 2.3.3).");
        Assert.True(cs.Contains("IsExpanded = false", StringComparison.Ordinal) &&
                    cs.Contains("IsChatOpen = false", StringComparison.Ordinal),
            "INV-6 broken: the hide path no longer resets the VM open/expand flags (ResetAndHide) — the audit-H6 " +
            "gap would reopen: a hide could leave IsExpanded/IsChatOpen set and corrupt the next reveal.");
    }

    // ── EdgeSidebar reveal animation targets these by name.
    [Fact]
    public void EdgeSidebar_KeepsRevealAnimationNames()
    {
        string edge = StripXamlComments(Edge);
        Assert.True(edge.Contains("x:Name=\"Handle\"", StringComparison.Ordinal),
            "EdgeSidebarWindow lost x:Name=\"Handle\" — its AnimateReveal animates Handle.Opacity.");
        Assert.True(edge.Contains("x:Name=\"HandleScale\"", StringComparison.Ordinal),
            "EdgeSidebarWindow lost x:Name=\"HandleScale\" — its AnimateReveal animates HandleScale.ScaleX.");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────
    private static string WindowOpeningTag(string xaml)
    {
        var m = Regex.Match(xaml, "<Window\\b[\\s\\S]*?>");
        return m.Success ? m.Value : "";
    }

    private static string StripXamlComments(string xaml) => Regex.Replace(xaml, "<!--[\\s\\S]*?-->", "");

    private static string StripCsComments(string cs)
    {
        cs = Regex.Replace(cs, "/\\*[\\s\\S]*?\\*/", "");      // block comments
        cs = Regex.Replace(cs, "(?m)//.*$", "");               // line comments
        return cs;
    }

    private static string ReadApp(string relativePosixPath)
    {
        string appDir = FindAppDir();
        string rel = relativePosixPath.Replace('/', Path.DirectorySeparatorChar);
        return File.ReadAllText(Path.Combine(appDir, rel));
    }

    private static string FindAppDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "src", "SenseEase.App");
            if (Directory.Exists(Path.Combine(candidate, "Theme")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate src/SenseEase.App from " + AppContext.BaseDirectory);
    }
}
