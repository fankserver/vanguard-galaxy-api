using VGModAPI.Core;
using Xunit;
namespace VGModAPI.Tests;
public sealed class DungeonPanelPlacementTests
{
    [Theory]
    [InlineData(100, 500, 508)]
    [InlineData(650, 950, 362)]
    public void SelectsAvailableSideWithoutCoveringNativePanel(float panelLeft, float panelRight, float expectedX)
    {
        var result = DungeonPanelPlacement.Beside(0, 0, 1000, 600, panelLeft, panelRight, 580)!.Value;
        Assert.Equal(expectedX, result.X); Assert.Equal(280, result.Width);
        Assert.True(result.X + result.Width <= panelLeft || result.X >= panelRight);
        Assert.InRange(result.Top - result.Height, 0, 600);
    }
    [Fact]
    public void CompactDrawerReservesVisibleToggleSpaceInsideViewport()
    {
        var area = DungeonPanelPlacement.Compact(8, 8, 592, 392)!.Value;
        Assert.True(area.X >= 8); Assert.True(area.X + area.Width <= 592);
        Assert.Equal(348, area.Top); Assert.True(area.Top - area.Height >= 8);
        Assert.Null(DungeonPanelPlacement.Compact(0, 0, 100, 100));
    }
    [Fact]
    public void WideNativePanelUsesVerticalSpaceWithoutOcclusion()
    {
        var below = DungeonPanelPlacement.Around(0, 0, 600, 600, 20, 200, 580, 580)!.Value;
        Assert.True(below.Top <= 192); Assert.True(below.Top - below.Height >= 0);
        var above = DungeonPanelPlacement.Around(0, 0, 600, 600, 20, 20, 580, 300)!.Value;
        Assert.True(above.Top - above.Height >= 308); Assert.True(above.Top <= 600);
    }
    [Fact]
    public void NarrowOrInvalidViewportRefusesOverlappingPresentation()
    {
        Assert.Null(DungeonPanelPlacement.Beside(0, 0, 600, 400, 50, 550, 350));
        Assert.Null(DungeonPanelPlacement.Beside(0, 0, float.NaN, 400, 50, 550, 350));
    }
}
