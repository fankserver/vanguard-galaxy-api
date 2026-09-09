using System;
using System.IO;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldGenerationAttemptsTests
{
    [Fact]
    public void SiblingsShareBudgetAndCaughtExhaustionCannotPublishOrRetry()
    {
        var attempts = new WorldGenerationAttempts(2);
        var outer = attempts.Begin(() => true);
        var first = attempts.Begin(() => true); attempts.Consume(); Assert.Null(first.Finish(null));
        var second = attempts.Begin(() => true); attempts.Consume();
        Assert.Throws<InvalidDataException>(() => attempts.Consume());
        Assert.IsType<InvalidDataException>(second.Finish(null));
        Assert.IsType<InvalidDataException>(outer.Finish(null));
        Assert.True(attempts.Failed);
        Assert.Throws<InvalidDataException>(() => attempts.Begin(() => true));
    }
    [Fact]
    public void VanillaMasksAccountingWithoutReplenishingEnclosingBudget()
    {
        var attempts = new WorldGenerationAttempts(1); var outer = attempts.Begin(() => true);
        attempts.Consume(); var vanilla = attempts.Begin(null);
        attempts.Consume(); attempts.Consume(); Assert.Null(vanilla.Finish(null));
        Assert.Throws<InvalidDataException>(() => attempts.Consume());
        Assert.IsType<InvalidDataException>(outer.Finish(null));
    }
    [Fact]
    public void OldFinalizerPreservesReplacementAuthorityAndOriginalFailure()
    {
        var attempts = new WorldGenerationAttempts(1); var old = attempts.Begin(() => true);
        attempts.Reset(); var current = attempts.Begin(() => true);
        var failure = new InvalidOperationException("native"); Assert.Same(failure, old.Finish(failure));
        Assert.False(attempts.Failed); attempts.Consume(); Assert.Null(current.Finish(null));
    }
    [Fact]
    public void CallbackReplacementDoesNotPoisonNewEpoch()
    {
        var attempts = new WorldGenerationAttempts(1); WorldGenerationAttempts.Scope? next = null;
        Assert.Throws<InvalidDataException>(() => attempts.Begin(() => { attempts.Reset(); next = attempts.Begin(() => true); return true; }));
        Assert.False(attempts.Failed); attempts.Consume(); Assert.Null(next!.Finish(null));
    }
}
