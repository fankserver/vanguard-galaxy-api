using System;
using System.Collections.Generic;
using VGModAPI.Qualification;
using Xunit;

namespace VGModAPI.Tests;

public sealed class ProbeCleanupTests
{
    [Fact]
    public void FailureDoesNotSkipDeviceRemovalOrRestorationAndPreservesFirstError()
    {
        var seen = new List<string>();
        var first = new InvalidOperationException("metadata deletion");
        var error = Assert.Throws<InvalidOperationException>(() => ProbeCleanup.Run(
            () => throw first,
            () => { seen.Add("refresh"); throw new Exception("refresh"); },
            () => seen.Add("keyboard removal"),
            () => seen.Add("mouse removal"),
            () => seen.Add("keyboard restoration"),
            () => seen.Add("mouse restoration")));
        Assert.Same(first, error.InnerException);
        Assert.Equal(new[] { "refresh", "keyboard removal", "mouse removal", "keyboard restoration", "mouse restoration" }, seen);
    }

    [Fact]
    public void SuccessfulCleanupRunsEveryStepInOrder()
    {
        var seen = new List<int>();
        ProbeCleanup.Run(() => seen.Add(1), () => seen.Add(2));
        Assert.Equal(new[] { 1, 2 }, seen);
    }
}
