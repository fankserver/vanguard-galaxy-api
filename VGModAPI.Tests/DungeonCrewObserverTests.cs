using System;
using System.Collections.Generic;
using VGModAPI.Core;
using VGModAPI.Runtime;
using Xunit;
using NativeObject = VGModAPI.Tests.DungeonLayoutBuilderTests.NativeObject;

namespace VGModAPI.Tests;

public sealed class DungeonCrewObserverTests
{
    [Fact]
    public void PrisonerOverflowAndForeignRecipientsAreNotReportedAsBrigDelivery()
    {
        using var hub = new LifecycleHub((_, _) => { }); using var boarding = new BoardingService(hub, (_, _) => { });
        using var settlement = new DungeonSettlementService(hub, boarding, (_, _) => { });
        var session = hub.Begin(SessionOrigin.SaveLoad, "save"); hub.PlayerReady(session);
        var target = new BoardingTargetSnapshot(new(session, Guid.NewGuid()), 1, BoardingEncounterKind.Ship, "Ship", "Gold", "Ship", BoardingAvailability.Available, null);
        var handle = new BoardingHandle(session, Guid.NewGuid());
        var snapshot = new BoardingOperationSnapshot(handle, target.Handle, 1, BoardingPhase.Active, false, false, 10, 10, "InProgress", new Dictionary<string, int>(), Array.Empty<BoardingCompartmentSnapshot>(), 1);
        boarding.Observe(BoardingEventKind.OperationStarted, target, snapshot);
        var recipient = new NativeObject(); var ship = new NativeObject(); ship.Fields["settlementShipData"] = recipient;
        var dead = new NativeObject(); dead.Fields["state"] = "Killed"; dead.Fields["settlementCrewType"] = "Marine";
        var simulation = new NativeObject(); simulation.Fields["friendlyUnits"] = new[] { dead };
        var operation = new NativeObject(); operation.Fields["operationShip"] = ship; operation.Fields["simulation"] = simulation;
        var faults = 0;
        var observer = new DungeonCrewObserver(native => ReferenceEquals(native, operation) ? handle : null, settlement, new DungeonLayoutBuilderTests.Native(), _ => faults++);
        using (observer.Begin(operation))
        {
            observer.PrisonersApplied(new NativeObject(), "Crew", 3, 0);
            observer.PrisonersApplied(recipient, "Crew", 3, 3);
            Assert.Empty(settlement.Get(handle)!.PrisonersDelivered);
            Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using var nested = observer.Begin(new NativeObject());
                observer.PrisonersApplied(recipient, "Crew", 3, 0);
                Assert.Empty(settlement.Get(handle)!.PrisonersDelivered);
                throw new InvalidOperationException();
            }));
            observer.PrisonersApplied(recipient, "Crew", 3, 1);
        }
        observer.PrisonersApplied(recipient, "Crew", 3, 0);
        Assert.Equal(2, settlement.Get(handle)!.PrisonersDelivered["Crew"]);
        Assert.Equal(1, settlement.Get(handle)!.Casualties["Marine"]);
        Assert.False(settlement.Get(handle)!.CrewReturnSettled); Assert.Equal(0, faults);
    }
}
