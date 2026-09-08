using System;
using System.Collections.Generic;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class DungeonSettlementServiceTests
{
    [Fact]
    public void VictoryCaptureAndDelayedReturnRemainSeparateObservedFacts()
    {
        using var hub = new LifecycleHub((_, _) => { }); using var boarding = new BoardingService(hub, (_, _) => { });
        using var settlement = new DungeonSettlementService(hub, boarding, (_, _) => { });
        var session = hub.Begin(SessionOrigin.SaveLoad, "slot"); hub.PlayerReady(session);
        var target = new BoardingTargetSnapshot(new(session, Guid.NewGuid()), 1, BoardingEncounterKind.Ship, "Ship", "Gold", "Ship", BoardingAvailability.Available, null);
        var operation = new BoardingOperationSnapshot(new(session, Guid.NewGuid()), target.Handle, 1, BoardingPhase.Resolved, false, false, 10, 10, "FriendlyVictory",
            new Dictionary<string, int>(), Array.Empty<BoardingCompartmentSnapshot>(), 1);
        boarding.Observe(BoardingEventKind.SimulationResolved, target, operation);
        var resolved = settlement.Get(operation.Handle)!;
        Assert.False(resolved.CaptureApplied); Assert.False(resolved.CrewReturnSettled); Assert.False(resolved.CrewCountsObserved);
        boarding.Observe(BoardingEventKind.CaptureApplied, target, operation);
        Assert.True(settlement.Get(operation.Handle)!.CaptureApplied); Assert.False(settlement.Get(operation.Handle)!.CrewReturnSettled);
        var casualties = new Dictionary<string, int> { ["Marine"] = 1 };
        settlement.ObserveCrew(operation.Handle, casualties, new Dictionary<string, int> { ["Crew"] = 2 }); casualties["Marine"] = 99;
        Assert.Equal(1, settlement.Get(operation.Handle)!.Casualties["Marine"]);
        boarding.Observe(BoardingEventKind.CrewReturnSettled, target, operation);
        Assert.True(settlement.Get(operation.Handle)!.CrewReturnSettled); Assert.True(settlement.Get(operation.Handle)!.CrewCountsObserved);
        Assert.False(resolved.CrewReturnSettled);
        hub.Invalidate("unload"); Assert.Null(settlement.Get(operation.Handle));
    }
}
