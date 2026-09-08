using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class PointerRevisionTests
{
    [Fact]
    public void RightClickCannotEraseHeldLeftRevision()
    {
        var input = new PointerRevision();
        input.Down(true, 10); // Left press on A.
        input.Down(false, 20); // Model changes to B, then right press/click.
        Assert.Null(input.Click(false));
        Assert.Equal(10L, input.Click(true));
        Assert.Null(input.Click(true));
    }
    [Fact]
    public void PointerActivationRequiresPressAndDisableClearsIt()
    {
        var input = new PointerRevision(); Assert.Null(input.Click(true));
        input.Down(true, 10); input.Clear(); Assert.Null(input.Click(true));
        input.Down(true, null); Assert.Null(input.Click(true));
    }
}
