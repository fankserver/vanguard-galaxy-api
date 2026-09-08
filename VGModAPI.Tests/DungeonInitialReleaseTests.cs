using System;
using System.Collections.Generic;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class DungeonInitialReleaseTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ObservationPrecedesActivationAndLocationDockingOccursOnce(bool location)
    {
        var calls = new List<string>();
        DungeonInitialRelease.Run(location, () => calls.Add("dock"), () => calls.Add("register"), () => calls.Add("observe"), () => calls.Add("activate"));
        Assert.Equal(location ? new[] { "dock", "register", "observe", "activate" } : new[] { "register", "observe", "activate" }, calls);
    }
    [Theory]
    [InlineData("dock")]
    [InlineData("register")]
    [InlineData("observe")]
    public void ReleaseFailureNeverActivates(string failure)
    {
        var calls = new List<string>(); var error = new InvalidOperationException("native failure");
        void Step(string step) { calls.Add(step); if (step == failure) throw error; }
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => DungeonInitialRelease.Run(true, () => Step("dock"), () => Step("register"), () => Step("observe"), () => Step("activate"))));
        Assert.DoesNotContain("activate", calls);
    }
}
