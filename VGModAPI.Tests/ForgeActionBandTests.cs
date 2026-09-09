using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class ForgeActionBandTests
{
    [Fact]
    public void ActionsAndTooltipSitAboveTabsWithinCanvas()
    {
        Assert.True(ForgeActionBand.TryCreate(1920, 1080, 0, 1180, 324, out var band));
        Assert.Equal(8, band.Left); Assert.Equal(328, band.Bottom); Assert.Equal(1172, band.Width);
        Assert.True(band.TooltipBottom >= band.Bottom + ForgeActionBand.Height);
        Assert.True(band.TooltipBottom + ForgeActionBand.TooltipHeight <= 1080);
    }
    [Theory]
    [InlineData(100, 600, 0, 100, 300)]
    [InlineData(1000, 600, 0, 800, 580)]
    [InlineData(1000, 600, 800, 200, 300)]
    [InlineData(1000, 600, 0, 800, float.NaN)]
    public void UnusableBandsAreRefused(float width, float height, float left, float right, float top)
        => Assert.False(ForgeActionBand.TryCreate(width, height, left, right, top, out _));
}
