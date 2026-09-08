using System;
using System.Collections;
using System.Collections.Generic;
using VGModAPI.Core;
using VGModAPI.Runtime;
using Xunit;
using NativeObject = VGModAPI.Tests.DungeonLayoutBuilderTests.NativeObject;
namespace VGModAPI.Tests;
public sealed class DungeonReconstructedArrivalTests
{
    private sealed class Instance : IDungeonReturnInstance
    {
        internal readonly NativeObject Pod = new(), Data = new();
        internal bool Bound, Active, Disposed;
        public bool Alive => !Disposed;
        public void Activate() { Assert.True(Bound); Active = true; }
        public void Dispose() => Disposed = true;
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReconstructionArrivalReceiptsAndSecondReloadDoNotRepeatDelivery(bool overflowPersisted)
    {
        using var hub = new LifecycleHub((_, _) => { }); var persistence = new DungeonPodPersistenceTests.Persistence(); using var state = new DungeonPodPersistence(hub, persistence);
        var session = hub.Begin(SessionOrigin.SaveLoad, "save"); hub.PlayerReady(session); persistence.Provider.Restore(hub.CurrentSession!, null);
        var operationId = Guid.NewGuid(); var podId = Guid.NewGuid(); var crew = new Dictionary<string, int> { ["Marine"] = 2 };
        var locationId = Guid.NewGuid();
        state.TrackOperation(new(operationId, locationId, null, "ship", "HostileShip", "Extraction", "Victory", "", DungeonTerminalProgress.NotStarted, false));
        using (var terminal = state.BeginTerminal(operationId)) { Assert.NotNull(terminal); terminal!.Completed(); }
        state.TrackOperation(new(operationId, locationId, null, "ship", "HostileShip", "Extraction", "Victory", "", DungeonTerminalProgress.Completed, false, retired: true));
        state.Track(new(podId, operationId, DungeonPodPhase.Returning, true, true, false, crew, parentShipId: "ship", transport: new("pod", false, crew, new float[9], "ship")));
        var payload = persistence.Provider.Capture(); persistence.Provider.Restore(hub.CurrentSession!, payload);
        var native = new DungeonLayoutBuilderTests.Native(); var pods = new DungeonPodResumeAdapter(state, native);
        var recipient = new NativeObject(); recipient.Fields["resumeShipGuid"] = "ship"; var ship = new NativeObject(); ship.Fields["resumeShipData"] = recipient;
        var operation = new NativeObject(); operation.Fields["operationShip"] = ship; var origin = new object();
        var observer = new DungeonPodReturnObserver(state, pods, native, _ => origin, _ => true, _ => operationId);
        Instance? live = null; var builds = 0;
        using var queue = new DungeonReturnRecoveryCoordinator(state, id => id == "ship" ? ship : null,
            (saved, _, resolved) =>
            {
                Assert.Same(ship, resolved); builds++; live = new();
                live.Data.Fields["resumePodPhase"] = saved.Phase.ToString(); live.Data.Fields["resumePodPlayer"] = saved.PlayerOwned;
                live.Pod.Fields["resumePodData"] = live.Data; live.Pod.Fields["resumeReturnCrew"] = saved.ReturnCrew; return live;
            }, (instance, id) => { var pod = (Instance)instance; pods.Loaded(pod.Data, id); pod.Bound = true; }, error => throw error);
        queue.Poll(); Assert.NotNull(live); Assert.True(live!.Active); Assert.True(observer.CanArrive(operation, live.Pod));
        live.Data.Fields["resumePodPhase"] = "Arrived";
        Assert.True(observer.Begin(operation, live.Pod, out var scope));
        using (scope)
        {
            observer.CrewAdded(recipient, "Marine", 2, 1);
            using var overflow = observer.BeginOverflow(origin, "Marine", 1);
            var poi = new NativeObject(); var list = new ArrayList(); poi.Fields["persistables"] = list; var data = new Source.Data.Persistable.CrewPodData();
            observer.AddingPersistable(poi, data); if (overflowPersisted) list.Add(data); observer.AddedPersistable(poi, data); overflow.Complete(); scope!.Complete();
        }
        live.Dispose();
        Assert.Equal(overflowPersisted, state.Get(podId)!.ReturnDelivered); Assert.True(state.Get(podId)!.ReturnAttempted);
        var settled = persistence.Provider.Capture(); pods.Clear(); persistence.Provider.Restore(hub.CurrentSession!, settled); queue.Poll();
        Assert.Equal(1, builds); Assert.Null(state.BeginTerminal(operationId));
        Assert.Equal(2, state.Get(podId)!.ReturnCrew["Marine"]); Assert.Equal(overflowPersisted, state.Get(podId)!.ReturnDelivered);
    }
}
