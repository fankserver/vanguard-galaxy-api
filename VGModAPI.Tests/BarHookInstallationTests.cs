using System;
using System.Linq;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

public sealed class BarHookInstallationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EveryPartialFailureRollsBackBeforePublication(int failure)
    {
        int attempted = 0, installed = 0, rollbacks = 0;
        bool published = false;
        var error = new InvalidOperationException("patch failure");
        var actions = Enumerable.Range(0, 4).Select(index => (Action)(() =>
        {
            attempted++;
            if (index == failure) throw error;
            installed++;
        }));
        var caught = Assert.Throws<InvalidOperationException>(() =>
        {
            BarHookInstallation.Install(actions, () => { installed = 0; rollbacks++; });
            published = true;
        });
        Assert.Same(error, caught);
        Assert.Equal(failure + 1, attempted);
        Assert.Equal(0, installed);
        Assert.Equal(1, rollbacks);
        Assert.False(published);
    }
}
