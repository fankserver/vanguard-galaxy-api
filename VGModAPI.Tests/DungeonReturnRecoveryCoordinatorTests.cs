using System;
using System.Collections.Generic;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class DungeonReturnRecoveryCoordinatorTests
{
    private sealed class Instance : IDungeonReturnInstance
    {
        internal bool Bound, Activated, Disposed;
        internal readonly float[] Pose = new float[9];
        public bool Alive => !Disposed;
        public void Activate() { Assert.True(Bound); Activated = true; }
        public void Dispose() { Disposed = true; }
    }
    [Fact]
    public void OnlyRestoredObligationsSpawnAfterRecipientResolutionAndRollbackDisposesOldInstances()
    {
        using var hub = new LifecycleHub((_, _) => { }); var persistence = new DungeonPodPersistenceTests.Persistence(); using var state = new DungeonPodPersistence(hub, persistence);
        var session = hub.Begin(SessionOrigin.SaveLoad, "save"); hub.PlayerReady(session); persistence.Provider.Restore(hub.CurrentSession!, null);
        var operation = Guid.NewGuid(); DungeonPodPersistenceTests.TrackOperation(state, operation);
        var crew = new Dictionary<string, int> { ["Marine"] = 2 };
        var pod = new DungeonPodResumeState(Guid.NewGuid(), operation, DungeonPodPhase.Returning, true, true, false, crew, parentShipId: "original", transport: new("native", false, crew, new float[9])); state.Track(pod);
        object? recipient = null; var made = new List<Instance>();
        using var coordinator = new DungeonReturnRecoveryCoordinator(state, id => { Assert.Equal("original", id); return recipient; },
            (_, _, resolved) => { Assert.Same(recipient, resolved); var instance = new Instance(); made.Add(instance); return instance; },
            (instance, id) => { Assert.Equal(pod.Id, id); ((Instance)instance).Bound = true; }, error => throw error);
        coordinator.Poll(); Assert.Empty(made);
        var payload = persistence.Provider.Capture(); persistence.Provider.Restore(hub.CurrentSession!, payload);
        coordinator.Poll(); Assert.Empty(made); recipient = new object(); coordinator.Poll(); coordinator.Poll();
        Assert.Single(made); Assert.True(made[0].Activated);
        made[0].Pose[0] = 37; persistence.MutationAllowed = false;
        coordinator.Checkpoint((id, instance) => Assert.True(state.RefreshTransportPose(id, ((Instance)instance).Pose)));
        var moved = persistence.Provider.Capture(); persistence.Provider.Restore(hub.CurrentSession!, moved);
        Assert.Equal(37, state.Get(pod.Id)!.Transport!.Pose[0]); Assert.False(state.Get(pod.Id)!.ReturnAttempted);
        persistence.MutationAllowed = true;
        persistence.Provider.Restore(hub.CurrentSession!, payload); coordinator.Poll();
        Assert.Equal(2, made.Count); Assert.True(made[0].Disposed); Assert.True(made[1].Activated);
    }
}
