using System;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class RecipeWidgetLayoutTests
{
    [Fact]
    public void SmallRecipeUsesOnlyContentHeight()
    {
        var layout = new RecipeWidgetLayout(4, false, 360);
        Assert.Equal(136f, layout.Height);
        Assert.Equal(96f, layout.RowsHeight);
        Assert.False(layout.Scrolls);
    }

    [Fact]
    public void IntegratedActionReservesItsOwnSpace()
    {
        var layout = new RecipeWidgetLayout(2, true, 360);
        Assert.Equal(118f, layout.Height);
        Assert.Equal(48f, layout.RowsHeight);
        Assert.False(layout.Scrolls);
    }

    [Fact]
    public void LongRecipeScrollsWithinAvailableHeight()
    {
        var layout = new RecipeWidgetLayout(32, true, 200);
        Assert.Equal(200f, layout.Height);
        Assert.Equal(130f, layout.RowsHeight);
        Assert.True(layout.Scrolls);
    }

    [Fact]
    public void InvalidGeometryIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecipeWidgetLayout(33, false, 200));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecipeWidgetLayout(1, false, float.NaN));
    }
}
