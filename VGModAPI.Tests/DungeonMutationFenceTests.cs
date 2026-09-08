using System;
using VGModAPI.Core;
using Xunit;
namespace VGModAPI.Tests;
public sealed class DungeonMutationFenceTests
{
    [Fact]
    public void NestedTransfersRefuseSavingUntilBothComplete()
    {
        var fence = new DungeonMutationFence(); using var outer = fence.Enter();
        using (fence.Enter()) Assert.Throws<InvalidOperationException>(fence.EnsureSettled);
        Assert.Throws<InvalidOperationException>(fence.EnsureSettled);
        outer.Dispose(); outer.Dispose(); fence.EnsureSettled();
    }
    [Fact]
    public void FailureRequiresReloadAndStaleLeaseCannotPoisonNewGeneration()
    {
        var fence = new DungeonMutationFence(); var old = fence.Enter(); old.Failed(); old.Dispose();
        Assert.Throws<InvalidOperationException>(fence.EnsureSettled);
        Assert.Throws<InvalidOperationException>(() => fence.Enter());
        fence.Reset(); var stale = fence.Enter(); fence.Reset();
        using var current = fence.Enter(); stale.Failed(); stale.Dispose();
        Assert.False(fence.Uncertain); Assert.True(fence.Busy);
        current.Dispose(); fence.EnsureSettled();
    }
}
