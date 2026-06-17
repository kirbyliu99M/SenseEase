using System;
using System.Collections.Generic;
using SenseEase.App.Services;
using Xunit;

namespace SenseEase.Tests;

/// <summary>
/// UI-RESHAPE — the coordinator unions every registered surface's rect and pushes the list to the
/// engine sink (register / update / unregister / auto-unregister-on-Closed). Pure; no WPF needed.
/// </summary>
public class SurfaceExcludeCoordinatorTests
{
    private sealed class FakeSurface : IShellSurface
    {
        public (int Left, int Top, int Width, int Height) Rect;
        public FakeSurface(int l, int t, int w, int h) => Rect = (l, t, w, h);
        public (int Left, int Top, int Width, int Height) GetMonitorRectInPhysicalPixels() => Rect;
        public void ReassertTopmost() { }
        public event EventHandler? Closed;
        public void RaiseClosed() => Closed?.Invoke(this, EventArgs.Empty);
    }

    [Fact]
    public void Register_Unregister_PushesUnion()
    {
        IReadOnlyList<(int Left, int Top, int Width, int Height)>? last = null;
        int pushes = 0;
        var coord = new SurfaceExcludeCoordinator(r => { last = r; pushes++; });

        var a = new FakeSurface(0, 0, 10, 10);
        var b = new FakeSurface(50, 50, 8, 120);

        coord.Register(a);
        Assert.Equal(1, coord.Count);
        Assert.NotNull(last);
        Assert.Single(last!);

        coord.Register(b);
        Assert.Equal(2, coord.Count);
        Assert.Equal(2, last!.Count);

        coord.Register(a); // idempotent — no extra push
        int pushesAfterDup = pushes;
        coord.Register(a);
        Assert.Equal(pushesAfterDup, pushes);

        coord.Unregister(a);
        Assert.Equal(1, coord.Count);
        Assert.Single(last!);

        coord.Unregister(b);
        Assert.Equal(0, coord.Count);
        Assert.Null(last); // empty → null (clears the exclude set)
    }

    [Fact]
    public void Closed_AutoUnregisters()
    {
        IReadOnlyList<(int, int, int, int)>? last = null;
        var coord = new SurfaceExcludeCoordinator(r => last = r);
        var s = new FakeSurface(1, 1, 20, 20);

        coord.Register(s);
        Assert.Equal(1, coord.Count);

        s.RaiseClosed();
        Assert.Equal(0, coord.Count);
        Assert.Null(last);
    }

    [Fact]
    public void Update_RepushesCurrentRects()
    {
        IReadOnlyList<(int Left, int Top, int Width, int Height)>? last = null;
        var coord = new SurfaceExcludeCoordinator(r => last = r);
        var s = new FakeSurface(0, 0, 10, 10);
        coord.Register(s);

        s.Rect = (200, 0, 12, 200); // moved/resized
        coord.Update();

        Assert.NotNull(last);
        Assert.Equal((200, 0, 12, 200), (last![0].Left, last[0].Top, last[0].Width, last[0].Height));
    }
}
