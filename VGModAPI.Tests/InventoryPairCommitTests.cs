using System;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;
public sealed class InventoryPairCommitTests
{
    [Fact]
    public void PublishAndRepeatedCommitDoNotApplyTwice()
    {
        object source = new object(), destination = new object(), nextSource = new object(), nextDestination = new object();
        int writes = 0;
        var commit = new InventoryPairCommit(() => source, value => { writes++; source = value; },
            () => destination, value => { writes++; destination = value; }, source, destination, nextSource, nextDestination);
        Assert.Equal(InventoryCommitStatus.Committed, commit.Commit());
        Assert.Same(nextSource, source); Assert.Same(nextDestination, destination);
        Assert.Equal(InventoryCommitStatus.Committed, commit.Commit()); Assert.Equal(2, writes);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DestinationFailureBeforeOrAfterAssignmentRestoresBoth(bool assignFirst)
    {
        object source = new object(), destination = new object(), beforeSource = source, beforeDestination = destination;
        var commit = new InventoryPairCommit(() => source, value => source = value,
            () => destination, value => { if (assignFirst || ReferenceEquals(value, beforeDestination)) destination = value; throw new InvalidOperationException(); },
            source, destination, new object(), new object());
        Assert.Equal(InventoryCommitStatus.Unchanged, commit.Commit());
        Assert.Same(beforeSource, source); Assert.Same(beforeDestination, destination);
    }
    [Fact]
    public void FailedCompensationRetainsRecoverableStateAndDoesNotOverwriteUnrelatedData()
    {
        object source = new object(), destination = new object(), beforeSource = source, beforeDestination = destination;
        bool failRestore = true;
        var commit = new InventoryPairCommit(() => source, value => { if (failRestore && ReferenceEquals(value, beforeSource)) throw new InvalidOperationException(); source = value; },
            () => destination, _ => throw new InvalidOperationException(), source, destination, new object(), new object());
        Assert.Equal(InventoryCommitStatus.RecoveryRequired, commit.Commit());
        object changed = source = new object();
        failRestore = false;
        Assert.Equal(InventoryCommitStatus.RecoveryRequired, commit.Recover()); Assert.Same(changed, source);
        source = beforeSource;
        Assert.Equal(InventoryCommitStatus.Unchanged, commit.Recover()); Assert.Same(beforeDestination, destination);
    }
}
