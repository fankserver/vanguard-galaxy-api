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
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DockedRefundComposesNativeReceiptHooksWithSaveReload(bool persistedOverflow)
    {
        using var hub = new LifecycleHub((_, _) => { }); var persistence = new DungeonPodPersistenceTests.Persistence(); using var state = new DungeonPodPersistence(hub, persistence);
        var session = hub.Begin(SessionOrigin.SaveLoad, "save"); hub.PlayerReady(session); persistence.Provider.Restore(hub.CurrentSession!, null);
        var operationId = Guid.NewGuid(); DungeonPodPersistenceTests.TrackOperation(state, operationId);
        var native = new DungeonLayoutBuilderTests.Native(); var pods = new DungeonPodResumeAdapter(state, native); var active = new ArrayList();
        var ids = new List<Guid>();
        for (var i = 0; i < 2; i++)
        {
            var id = Guid.NewGuid(); ids.Add(id); var crew = new Dictionary<string, int> { ["Marine"] = 2 };
            state.Track(new(id, operationId, DungeonPodPhase.Docked, true, false, false, new Dictionary<string, int>(), parentShipId: "ship-guid", transport: new("pod" + i, false, crew, new float[9], "ship-guid")));
            var data = new NativeObject(); data.Fields["resumePodPhase"] = "Docked"; data.Fields["resumePodPlayer"] = true; data.Fields["resumePodCrew"] = crew; pods.Loaded(data, id);
            var pod = new NativeObject(); pod.Fields["resumePodData"] = data; active.Add(pod);
        }
        var recipient = new NativeObject(); recipient.Fields["resumeShipGuid"] = "ship-guid"; var ship = new NativeObject(); ship.Fields["resumeShipData"] = recipient;
        var operation = new NativeObject(); operation.Fields["operationShip"] = ship; operation.Fields["_activePods"] = active; var origin = new object();
        var observer = new DungeonPodReturnObserver(state, pods, native, _ => origin, _ => true, _ => operationId); DungeonPodReturnPatches.Observer = observer;
        try
        {
            using (var cancellation = state.BeginCancellation(operationId))
            {
                Assert.NotNull(cancellation); Assert.True(observer.BeginDockedRefunds(operation, false, out var scope));
                using (scope)
                {
                    Assert.Throws<InvalidOperationException>(() => persistence.Provider.Capture());
                    DungeonPodReturnPatches.Crew.Postfix(recipient, "Marine", 2, 1);
                    DungeonPodReturnPatches.Crew.Postfix(recipient, "Marine", 2, 1);
                    active.Clear(); // Native destruction/removal happens before batched overflow emission.
                    DungeonPodReturnPatches.Overflow.Prefix("Marine", 2, origin, out var overflow);
                    var poi = new NativeObject(); var list = new ArrayList(); poi.Fields["persistables"] = list; var data = new Source.Data.Persistable.CrewPodData();
                    DungeonPodReturnPatches.Persisted.Prefix(poi, data);
                    if (persistedOverflow) list.Add(data);
                    DungeonPodReturnPatches.Persisted.Postfix(poi, data);
                    DungeonPodReturnPatches.Overflow.Postfix(overflow); DungeonPodReturnPatches.Overflow.Finalizer(null, overflow);
                    Assert.Equal(persistedOverflow, scope!.Complete());
                }
            }
            var payload = persistence.Provider.Capture(); persistence.Provider.Restore(hub.CurrentSession!, payload);
            foreach (var id in ids)
            {
                Assert.True(state.Get(id)!.ReturnAttempted); Assert.Equal(persistedOverflow, state.Get(id)!.ReturnDelivered);
                Assert.Equal(persistedOverflow ? DungeonPodPhase.Refunded : DungeonPodPhase.Docked, state.Get(id)!.Phase);
            }
            Assert.Null(state.BeginDockedRefunds(operationId, ids));
        }
        finally { DungeonPodReturnPatches.Observer = null; }
    }
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
