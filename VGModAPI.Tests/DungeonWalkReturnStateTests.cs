using System;
using System.Collections.Generic;
using VGModAPI.Core;
using Xunit;
namespace VGModAPI.Tests;
public sealed class DungeonWalkReturnStateTests
{
    [Fact]
    public void AttemptCannotRetryAndDeliveryRequiresAcceptedCrewPlusPersistedOverflow()
    {
        var pending = new DungeonWalkReturnState(new Dictionary<string, int> { ["Marine"] = 3 });
        var attempt = pending.Begin(); Assert.Throws<InvalidOperationException>(() => attempt.Begin());
        Assert.Throws<InvalidOperationException>(() => attempt.Complete(new(new Dictionary<string, int> { ["Marine"] = 2 }, new Dictionary<string, int>())));
        var delivered = attempt.Complete(new(new Dictionary<string, int> { ["Marine"] = 2 }, new Dictionary<string, int> { ["Marine"] = 1 }));
        Assert.Equal(DungeonWalkReturnProgress.Delivered, delivered.Progress);
        Assert.Throws<InvalidOperationException>(() => delivered.Begin());
    }
    [Fact]
    public void SavedAttemptCannotBeErasedOrResetToPending()
    {
        var ledger = new DungeonOperationRecoveryLedger();
        var pending = new DungeonWalkReturnState(new Dictionary<string, int> { ["Marine"] = 1 });
        var operation = new DungeonOperationResumeState(Guid.NewGuid(), Guid.NewGuid(), null, "ship", "Station", "Extraction", "Victory", "", DungeonTerminalProgress.NotStarted, false, walkReturn: pending);
        ledger.Track(operation); ledger.Track(operation.WithWalkReturn(pending.Begin()));
        var bytes = ledger.Capture(); ledger.Restore(bytes);
        Assert.Equal(DungeonWalkReturnProgress.Attempted, ledger.Get(operation.Id)!.WalkReturn!.Progress);
        Assert.Throws<InvalidOperationException>(() => ledger.Track(operation));
        Assert.Throws<InvalidOperationException>(() => ledger.Track(operation.WithWalkReturn(new DungeonWalkReturnState(new Dictionary<string, int>()))));
    }
    [Fact]
    public void KnownEmptyManifestStillRequiresAnAttempt()
    {
        var empty = new Dictionary<string, int>(); var state = new DungeonWalkReturnState(empty);
        var receipt = new DungeonPodDeliveryReceipt(empty, empty);
        Assert.Throws<InvalidOperationException>(() => state.Complete(receipt));
        Assert.Equal(DungeonWalkReturnProgress.Delivered, state.Begin().Complete(receipt).Progress);
    }
}
