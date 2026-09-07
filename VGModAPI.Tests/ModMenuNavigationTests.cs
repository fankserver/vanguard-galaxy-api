using System;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class ModMenuNavigationTests
{
    [Theory]
    [InlineData(0, 5, 4, 1)]
    [InlineData(3, 5, 2, 4)]
    [InlineData(4, 5, 3, 0)]
    public void VerticalScrollbarsKeepScrollingAxisFreeAndHorizontalEscapeInsidePanel(int index, int count, int previous, int next)
    {
        var links = ModMenuNavigation.Neighbors(index, count, true);
        Assert.Null(links.Up);
        Assert.Null(links.Down);
        Assert.Equal(previous, links.Left);
        Assert.Equal(next, links.Right);
    }

    [Theory]
    [InlineData(0, 5, 4, 1)]
    [InlineData(2, 5, 1, 3)]
    [InlineData(4, 5, 3, 0)]
    public void OrdinaryControlsRetainClosedNavigationOnBothAxes(int index, int count, int previous, int next)
    {
        var links = ModMenuNavigation.Neighbors(index, count, false);
        Assert.Equal(previous, links.Up);
        Assert.Equal(next, links.Down);
        Assert.Equal(previous, links.Left);
        Assert.Equal(next, links.Right);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 2)]
    [InlineData(2, 2)]
    public void InvalidRingIsRejected(int index, int count) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ModMenuNavigation.Neighbors(index, count, false));
}
