using System;
using System.Collections.Generic;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class ModMenuLifetimeTests
{
    private sealed class View : IModMenuView
    {
        internal int Disposals, Ticks;
        internal bool Open = true;
        public void Tick(bool modalOpen) { ++Ticks; if (modalOpen) Open = false; }
        public void Dispose() { ++Disposals; Open = false; }
    }

    [Fact]
    public void ActualMenuIdentityCreatesOnlyOneViewAndInactiveMenuDetachesBeforeReturn()
    {
        var views = new List<View>();
        using var lifetime = new ModMenuLifetime((_, _, _) => { var view = new View(); views.Add(view); return view; });
        var menu = new object(); var viewport = new object(); var canvas = new object();
        lifetime.Poll(null, null, null, false); Assert.Empty(views);
        for (var i = 0; i < 100; ++i) lifetime.Poll(menu, viewport, canvas, false);
        Assert.Single(views); Assert.Equal(100, views[0].Ticks);
        lifetime.Poll(null, viewport, canvas, false); Assert.Equal(1, views[0].Disposals);
        lifetime.Poll(menu, viewport, canvas, false); Assert.Equal(2, views.Count);
        Assert.Equal(0, views[1].Disposals);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void MenuViewportOrCanvasReplacementDisposesOldViewBeforeAttaching(int replacement)
    {
        var keys = new[] { new object(), new object(), new object() };
        var views = new List<View>();
        using var lifetime = new ModMenuLifetime((_, _, _) =>
        {
            if (views.Count > 0) Assert.Equal(1, views[^1].Disposals);
            var view = new View(); views.Add(view); return view;
        });
        lifetime.Poll(keys[0], keys[1], keys[2], false);
        keys[replacement] = new object();
        lifetime.Poll(keys[0], keys[1], keys[2], false);
        Assert.Equal(2, views.Count); Assert.False(views[0].Open);
    }

    [Fact]
    public void ModalDefersFirstAttachmentAndClosesWithoutReopeningOrRecreating()
    {
        var created = 0; var view = new View();
        using var lifetime = new ModMenuLifetime((_, _, _) => { ++created; return view; });
        var menu = new object(); var viewport = new object(); var canvas = new object();
        lifetime.Poll(menu, viewport, canvas, true); Assert.Equal(0, created);
        lifetime.Poll(menu, viewport, canvas, false); Assert.Equal(1, created);
        lifetime.Poll(menu, viewport, canvas, true); Assert.False(view.Open);
        lifetime.Poll(menu, viewport, canvas, false); Assert.False(view.Open); Assert.Equal(1, created);
    }

    [Fact]
    public void ShutdownIsIdempotentAndCannotAttachOrTickAgain()
    {
        var view = new View();
        var lifetime = new ModMenuLifetime((_, _, _) => view);
        var key = new object();
        lifetime.Poll(key, key, key, false);
        lifetime.Dispose(); lifetime.Dispose(); lifetime.Poll(key, key, key, false);
        Assert.Equal(1, view.Disposals); Assert.Equal(1, view.Ticks);
    }

    [Fact]
    public void MissingViewportOrCanvasNeverBuildsAndTearsDownExistingView()
    {
        var view = new View(); var created = 0;
        using var lifetime = new ModMenuLifetime((_, _, _) => { ++created; return view; });
        var key = new object();
        lifetime.Poll(key, null, key, false); lifetime.Poll(key, key, null, false);
        Assert.Equal(0, created);
        lifetime.Poll(key, key, key, false); lifetime.Poll(key, key, null, false);
        Assert.Equal(1, created); Assert.Equal(1, view.Disposals);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(63, 0)]
    [InlineData(64, 1)]
    [InlineData(127, 1)]
    [InlineData(128, 2)]
    public void CompactRowsScrollAtExactRowBoundaries(float offset, int expectedFirst)
    {
        Assert.Equal(64, ModMenuRows.Height);
        Assert.Equal(expectedFirst, ModMenuRows.First(100, offset, 192));
        Assert.Equal(4, ModMenuRows.VisibleCount(100, 192));
    }

    [Fact]
    public void LargeInventoryUsesOnlyViewportSizedRowsAndClampsOverscroll()
    {
        Assert.Equal(0, ModMenuRows.VisibleCount(0, 220));
        Assert.Equal(5, ModMenuRows.VisibleCount(4096, 220));
        Assert.Equal(0, ModMenuRows.First(4096, -100, 220));
        Assert.Equal(2048, ModMenuRows.First(4096, 2048 * ModMenuRows.Height, 220));
        Assert.Equal(4092, ModMenuRows.First(4096, float.MaxValue, 220));
        Assert.Equal(0, ModMenuRows.First(2, 300, 220));
        Assert.Equal(2, ModMenuRows.VisibleCount(2, 220));
        Assert.Equal(20, ModMenuRows.VisibleCount(4096, 1200));
    }
}
