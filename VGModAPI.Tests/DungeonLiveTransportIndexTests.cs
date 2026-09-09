using System;
using System.Collections.Generic;
using VGModAPI.Core;
using Xunit;
namespace VGModAPI.Tests;
public sealed class DungeonLiveTransportIndexTests
{
    [Fact]
    public void FreshReturningPodCheckpointsAfterItsOperationLeavesTheManager()
    {
        using var hub = new LifecycleHub((_, _) => { }); var persistence = new DungeonPodPersistenceTests.Persistence(); using var state = new DungeonPodPersistence(hub, persistence);
        var session = hub.Begin(SessionOrigin.SaveLoad, "save"); hub.PlayerReady(session); persistence.Provider.Restore(hub.CurrentSession!, null);
        var operation = Guid.NewGuid(); DungeonPodPersistenceTests.TrackOperation(state, operation);
        var crew = new Dictionary<string, int> { ["Marine"] = 1 }; var id = Guid.NewGuid();
        state.Track(new(id, operation, DungeonPodPhase.Returning, true, true, false, crew, parentShipId: "ship", transport: new("pod", false, crew, new float[9], "ship")));
        var nativePod = new object(); var index = new DungeonLiveTransportIndex(_ => true); index.Track(id, nativePod);
        // No operation-manager membership or reconstructed-instance ownership participates in this refresh.
        var moved = new float[] { 75, 21, 90, 0, 0, 12, 13, 0, 0 };
        index.Checkpoint((key, pod) => { Assert.Same(nativePod, pod); Assert.True(state.RefreshTransportPose(key, moved)); });
        var saved = persistence.Provider.Capture(); persistence.Provider.Restore(hub.CurrentSession!, saved);
        Assert.Equal(moved, state.Get(id)!.Transport!.Pose); Assert.True(state.Get(id)!.CanRecover);
    }
    [Fact]
    public void DuplicateLiveIdentityIsRejectedAndDeadInstancesAreNotCheckpointed()
    {
        var live = new HashSet<object>(); var index = new DungeonLiveTransportIndex(live.Contains); var id = Guid.NewGuid(); var pod = new object(); live.Add(pod); index.Track(id, pod);
        Assert.Throws<InvalidOperationException>(() => index.Track(id, new object()));
        live.Clear(); index.Checkpoint((_, _) => throw new InvalidOperationException("dead transport"));
        var replacement = new object(); live.Add(replacement); index.Track(id, replacement);
        index.Clear(); index.Checkpoint((_, _) => throw new InvalidOperationException("stale transport"));
    }
}
