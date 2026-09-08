using System;
using System.Collections;
using System.Collections.Generic;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldMembershipTransactionTests
{
    [Fact]
    public void AppendsWithoutReplacingExistingReferences()
    {
        var first = new object(); var created = new object(); var members = new List<object> { first };
        Assert.True(WorldMembershipTransaction.TryAppend(members, WorldMembershipTransaction.Capture(members), created, () => true));
        Assert.Same(first, members[0]); Assert.Same(created, members[1]);
        Assert.False(WorldMembershipTransaction.TryAppend(members, WorldMembershipTransaction.Capture(members), created, () => true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallbackCannotHideAReplacementByRewritingTheExpectation(bool ready)
    {
        var first = new object(); var replacement = new object(); var members = new List<object> { first };
        var expected = WorldMembershipTransaction.Capture(members);
        Assert.False(WorldMembershipTransaction.TryAppend(members, expected, new object(), () =>
        {
            members[0] = replacement; expected[0] = replacement; return ready;
        }));
        Assert.Same(replacement, Assert.Single(members));
    }

    [Fact]
    public void RefusalAndFaultDoNotAppendOrRollbackAnIndependentMutation()
    {
        var first = new object(); var members = new List<object> { first };
        Assert.False(WorldMembershipTransaction.TryAppend(members, WorldMembershipTransaction.Capture(members), new object(), () => false));
        Assert.Throws<InvalidOperationException>(() => WorldMembershipTransaction.TryAppend(members, WorldMembershipTransaction.Capture(members), new object(), () =>
        {
            members.Clear(); throw new InvalidOperationException("Owner fence fault");
        }));
        Assert.Empty(members);
        Assert.Throws<ArgumentException>(() => WorldMembershipTransaction.Capture(new ArrayList()));
    }
}
