using System;
using VGModAPI.Core;
using VGModAPI.Runtime;
using Xunit;
using NativeObject = VGModAPI.Tests.DungeonLayoutBuilderTests.NativeObject;

namespace VGModAPI.Tests;

public sealed class DungeonOperationResumeAdapterTests
{
    [Fact]
    public void FreshGenerationOnUsedLocationRetainsPreviousReturnObligations()
    {
        using var hub = new LifecycleHub((_, _) => { }); var persistence = new DungeonPodPersistenceTests.Persistence(); using var state = new DungeonPodPersistence(hub, persistence);
        var session = hub.Begin(SessionOrigin.SaveLoad, "save"); hub.PlayerReady(session); persistence.Provider.Restore(hub.CurrentSession!, null);
        var adapter = new DungeonOperationResumeAdapter(state, new DungeonLayoutBuilderTests.Native());
        var location = new NativeObject(); location.Fields["dungeonType"] = "HostileShip"; var content = Guid.NewGuid();
        var old = adapter.Created(Operation(location, "ship"), content, "")!.Value;
        var pod = new DungeonPodResumeState(Guid.NewGuid(), old, DungeonPodPhase.Returning, true, true, false, new System.Collections.Generic.Dictionary<string, int> { ["Marine"] = 2 }, parentShipId: "ship"); Assert.True(state.Track(pod));
        using (var terminal = state.BeginTerminal(old)) terminal!.Completed();
        var fresh = adapter.Created(Operation(location, "ship"), content, "")!.Value;
        Assert.NotEqual(old, fresh); Assert.Equal(fresh, adapter.LocationMarker(location));
        Assert.Equal(state.Operation(old)!.LocationId, state.Operation(fresh)!.LocationId);
        Assert.Equal(content, state.Operation(fresh)!.ContentOccurrence); Assert.True(state.Get(pod.Id)!.CanRecover);
        Assert.Equal(old, state.Get(pod.Id)!.OperationId);
    }
    [Fact]
    public void RestoreRequiresExactRecipientAndDuplicateOperationsCannotBorrowSavedIdentity()
    {
        using var hub = new LifecycleHub((_, _) => { }); var persistence = new DungeonPodPersistenceTests.Persistence(); using var state = new DungeonPodPersistence(hub, persistence);
        var session = hub.Begin(SessionOrigin.SaveLoad, "save"); hub.PlayerReady(session); persistence.Provider.Restore(hub.CurrentSession!, null);
        var adapter = new DungeonOperationResumeAdapter(state, new DungeonLayoutBuilderTests.Native());
        var location = new NativeObject(); location.Fields["dungeonType"] = "HostileShip";
        var op = Operation(location, "original"); var id = adapter.Created(op, Guid.NewGuid(), "mission-token")!.Value;
        var payload = persistence.Provider.Capture(); adapter.Clear(); persistence.Provider.Restore(hub.CurrentSession!, payload);
        adapter.LoadedLocation(location, id);
        Assert.False(adapter.Resumed(Operation(location, "other")));
        var restored = Operation(location, "original"); Assert.True(adapter.Resumed(restored)); Assert.Equal("mission-token", state.Operation(id)!.MissionProtection);
        Assert.False(adapter.Resumed(Operation(location, "original"))); Assert.True(adapter.Conflicted(id));
        Assert.Null(adapter.Created(restored, null, ""));
        adapter.Clear(); persistence.Provider.Restore(hub.CurrentSession!, null); adapter.LoadedLocation(location, id);
        Assert.Null(adapter.Created(Operation(location, "original"), null, ""));
    }
    private static NativeObject Operation(object location, string shipId)
    {
        var data = new NativeObject(); data.Fields["resumeShipGuid"] = shipId;
        var ship = new NativeObject(); ship.Fields["resumeShipData"] = data;
        var op = new NativeObject(); op.Fields["location"] = location; op.Fields["operationShip"] = ship; op.Fields["phase"] = "Approach";
        op.Fields["simulation"] = null; op.Fields["isAutonomous"] = false; return op;
    }
}
