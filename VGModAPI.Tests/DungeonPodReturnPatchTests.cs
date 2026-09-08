using System;
using System.Collections;
using System.Collections.Generic;
using VGModAPI.Core;
using VGModAPI.Patches;
using VGModAPI.Runtime;
using Xunit;
using NativeObject = VGModAPI.Tests.DungeonLayoutBuilderTests.NativeObject;

namespace VGModAPI.Tests;

[Collection("Dungeon crew resume")]
public sealed class DungeonPodReturnPatchTests
{
    [Fact]
    public void NativeShapedReturnAccountsForRosterAndPersistedOverflowThenRejectsReplay()
    {
        using var hub = new LifecycleHub((_, _) => { }); var persistence = new DungeonPodPersistenceTests.Persistence(); using var state = new DungeonPodPersistence(hub, persistence);
        var session = hub.Begin(SessionOrigin.SaveLoad, "save"); hub.PlayerReady(session); persistence.Provider.Restore(hub.CurrentSession!, null);
        var operationId = Guid.NewGuid(); DungeonPodPersistenceTests.TrackOperation(state, operationId);
        var manifest = new Dictionary<string, int> { ["Marine"] = 3 }; var saved = new DungeonPodResumeState(Guid.NewGuid(), operationId, DungeonPodPhase.Returning, true, true, false, manifest, parentShipId: "ship-guid"); state.Track(saved);
        var native = new DungeonLayoutBuilderTests.Native(); var pods = new DungeonPodResumeAdapter(state, native);
        var data = new NativeObject(); data.Fields["resumePodPlayer"] = true; data.Fields["resumePodPhase"] = "Returning"; pods.Loaded(data, saved.Id);
        var pod = new NativeObject(); pod.Fields["resumePodData"] = data; pod.Fields["resumeReturnCrew"] = manifest;
        var recipient = new NativeObject(); recipient.Fields["resumeShipGuid"] = "ship-guid"; var ship = new NativeObject(); ship.Fields["resumeShipData"] = recipient;
        var operation = new NativeObject(); operation.Fields["operationShip"] = ship; var origin = new object(); var faults = 0; var ready = false;
        DungeonPodReturnPatches.Observer = new(state, pods, native, _ => origin, _ => ready, _ => operationId); DungeonPodReturnPatches.Report = _ => faults++;
        try
        {
            var destroyed = false; var callbacks = 0;
            void Arrive(Action deliver)
            {
                if (!DungeonPodReturnPatches.Observer!.CanArrive(operation, pod)) return;
                data.Fields["resumePodPhase"] = "Arrived"; callbacks++; deliver(); destroyed = true;
            }
            Arrive(() => throw new InvalidOperationException("Refused arrival must not deliver."));
            Assert.Equal("Returning", data.Fields["resumePodPhase"]); Assert.Equal(0, callbacks); Assert.False(destroyed);
            ready = true; Arrive(() =>
            {
            Assert.True(DungeonPodReturnPatches.Return.Prefix(operation, pod, out var scope));
            DungeonPodReturnPatches.Crew.Postfix(recipient, "Marine", 3, 2);
            DungeonPodReturnPatches.Overflow.Prefix("Marine", 2, origin, out var overflow);
            var poi = new NativeObject(); var list = new ArrayList(); poi.Fields["persistables"] = list; var overflowData = new Source.Data.Persistable.CrewPodData();
            DungeonPodReturnPatches.Persisted.Prefix(poi, overflowData); list.Add(overflowData); DungeonPodReturnPatches.Persisted.Postfix(poi, overflowData);
            DungeonPodReturnPatches.Overflow.Postfix(overflow); Assert.Null(DungeonPodReturnPatches.Overflow.Finalizer(null, overflow));
            DungeonPodReturnPatches.Return.Postfix(scope); Assert.Null(DungeonPodReturnPatches.Return.Finalizer(null, scope));
            });
            Assert.True(destroyed); Assert.Equal(1, callbacks);
            Assert.True(state.Get(saved.Id)!.ReturnDelivered); Assert.False(DungeonPodReturnPatches.Return.Prefix(operation, pod, out _)); Assert.Equal(0, faults);
            var error = new InvalidOperationException("native"); Assert.Same(error, DungeonPodReturnPatches.Return.Finalizer(error, null));
        }
        finally { DungeonPodReturnPatches.Observer = null; DungeonPodReturnPatches.Report = null; }
    }
}
