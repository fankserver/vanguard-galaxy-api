using System.Collections.Generic;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class ModUpdateControlsTests
{
    [Fact]
    public void RenderDoesNotClearKeyboardFocusThroughTransientDisable()
    {
        var focused = true;
        var assignments = new List<bool>();
        ModUpdateControls.Apply(true, true, false, value => { assignments.Add(value); if (!value) focused = false; }, _ => { }, _ => { });
        Assert.True(focused);
        Assert.Equal(new[] { true }, assignments);
    }
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void EachControlReceivesOnlyItsFinalState(bool selected, bool canCheck, bool canRelease)
    {
        var check = new List<bool>(); var automatic = new List<bool>(); var release = new List<bool>();
        ModUpdateControls.Apply(selected, canCheck, canRelease, check.Add, automatic.Add, release.Add);
        Assert.Equal(new[] { selected && canCheck }, check);
        Assert.Equal(new[] { selected }, automatic);
        Assert.Equal(new[] { selected && canRelease }, release);
    }
}
