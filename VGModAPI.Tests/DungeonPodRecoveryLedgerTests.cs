using System;
using System.Collections.Generic;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class DungeonPodRecoveryLedgerTests
{
    [Fact]
    public void AttemptedManifestAndOwnershipCannotBeRewritten()
    {
        var ledger = new DungeonPodRecoveryLedger(); var id = Guid.NewGuid(); var occurrence = Guid.NewGuid();
        var crew = new Dictionary<string, int> { ["Marine"] = 2 };
        ledger.Track(new(id, occurrence, DungeonPodPhase.Returning, true, true, false, crew));
        Assert.Throws<InvalidOperationException>(() => ledger.Track(new(id, occurrence, DungeonPodPhase.Returning, false, true, false, crew)));
        ledger.BeginReturn(id); crew["Marine"] = 3;
        Assert.Throws<InvalidOperationException>(() => ledger.Track(new(id, occurrence, DungeonPodPhase.Returning, true, true, false, crew, true)));
        Assert.Equal(2, ledger.Get(id)!.ReturnCrew["Marine"]);
    }
    [Fact]
    public void FailedReturnIsNotRetriedAndRollbackRestoresTheSavedObligation()
    {
        var ledger = new DungeonPodRecoveryLedger(); var pod = new DungeonPodResumeState(Guid.NewGuid(), Guid.NewGuid(), DungeonPodPhase.Returning, true, true, false, new Dictionary<string, int> { ["Marine"] = 2 });
        ledger.Track(pod); var before = ledger.Capture();
        Assert.NotNull(ledger.BeginReturn(pod.Id)); Assert.Null(ledger.BeginReturn(pod.Id));
        var interrupted = ledger.Capture(); ledger.Restore(interrupted);
        Assert.Null(ledger.BeginReturn(pod.Id)); Assert.False(ledger.Get(pod.Id)!.ReturnDelivered);
        ledger.Restore(before); Assert.NotNull(ledger.BeginReturn(pod.Id)); ledger.Delivered(pod.Id);
        var completed = ledger.Capture(); ledger.Restore(completed); Assert.Null(ledger.BeginReturn(pod.Id)); Assert.True(ledger.Get(pod.Id)!.ReturnDelivered);
        ledger.Restore(null); Assert.Null(ledger.Get(pod.Id));
    }
}
