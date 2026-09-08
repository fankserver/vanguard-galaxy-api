using System;
using System.Collections.Generic;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class DungeonPodPersistenceTests
{
    internal static void TrackOperation(DungeonPodPersistence pods, Guid id)
    {
        Assert.True(pods.TrackOperation(new(id, Guid.NewGuid(), null, "ship-guid", "HostileShip", "Extraction", "Victory", "", DungeonTerminalProgress.NotStarted, false)));
    }
    internal sealed class Persistence : IPersistenceApi, IPersistenceRegistration, IPersistenceReadiness
    {
        internal PersistenceProvider Provider = null!;
        public bool MutationAllowed { get; set; } = true;
        public bool StateReady { get; set; } = true;
        public string Status => "test";
        public IPersistenceRegistration Register(PersistenceProvider provider) { Provider = provider; return this; }
        public void Dispose() { }
    }
    [Fact]
    public void DonorAbortRetiresOnlyMatchingReservationAndCannotCrossRestoreGenerations()
    {
        using var hub = new LifecycleHub((_, _) => { }); var persistence = new Persistence(); using var state = new DungeonPodPersistence(hub, persistence);
        var session = hub.Begin(SessionOrigin.SaveLoad, "save"); hub.PlayerReady(session); persistence.Provider.Restore(hub.CurrentSession!, null);
        var crew = new Dictionary<string, int> { ["Marine"] = 2 }; var id = Guid.NewGuid();
        Assert.True(state.TrackOperation(new(id, Guid.NewGuid(), null, "ship", "Ship", "Active", "", "", DungeonTerminalProgress.NotStarted, false,
            donors: new[] { new DungeonDonorApproachState("a", crew), new DungeonDonorApproachState("b", crew) })));
        var snapshot = persistence.Provider.Capture(); var token = state.RestoreToken;
        using (var scope = state.BeginTransfer())
        { Assert.NotNull(scope); Assert.False(state.CompleteDonorAbort(id, "a", token)); }
        Assert.True(state.CompleteDonorAbort(id, "a", token));
        Assert.Equal("b", Assert.Single(state.Operation(id)!.Donors).ShipId);
        persistence.Provider.Restore(hub.CurrentSession!, snapshot);
        Assert.False(state.CompleteDonorAbort(id, "a", token)); Assert.Equal(2, state.Operation(id)!.Donors.Count);
        Assert.True(state.CompleteDonorAbort(id, "a", state.RestoreToken));
        Assert.Equal(2, Assert.Single(state.Operation(id)!.Donors).Crew["Marine"]);
    }
    [Fact]
    public void SaveWindowCheckpointRefreshesKnownStateWithoutCreatingEffectsOrIdentities()
    {
        using var hub = new LifecycleHub((_, _) => { }); var persistence = new Persistence(); using var pods = new DungeonPodPersistence(hub, persistence);
        var session = hub.Begin(SessionOrigin.SaveLoad, "save"); hub.PlayerReady(session); persistence.Provider.Restore(hub.CurrentSession!, null);
        var id = Guid.NewGuid(); var location = Guid.NewGuid();
        DungeonOperationResumeState Snapshot(Guid key, DungeonTerminalProgress terminal, bool walking) => new(key, location, null, "ship", "Station", "Approach", "", "", terminal, false, walkDispatched: walking);
        Assert.True(pods.TrackOperation(Snapshot(id, DungeonTerminalProgress.NotStarted, false)));
        persistence.MutationAllowed = false;
        pods.Checkpoint(() =>
        {
            Assert.False(pods.CanMutate); Assert.Null(pods.BeginTransfer()); Assert.Null(pods.BeginTerminal(id));
            Assert.True(pods.TrackOperation(Snapshot(id, DungeonTerminalProgress.NotStarted, true)));
            Assert.False(pods.TrackOperation(Snapshot(Guid.NewGuid(), DungeonTerminalProgress.NotStarted, false)));
            Assert.False(pods.TrackOperation(Snapshot(id, DungeonTerminalProgress.Attempted, true)));
            Assert.Throws<InvalidOperationException>(() => persistence.Provider.Capture());
        });
        var payload = persistence.Provider.Capture(); persistence.Provider.Restore(hub.CurrentSession!, payload);
        Assert.True(pods.Operation(id)!.WalkDispatched);
        Assert.Equal(DungeonTerminalProgress.NotStarted, pods.Operation(id)!.TerminalProgress);
        Assert.Throws<InvalidOperationException>(() => pods.Checkpoint(() => throw new InvalidOperationException("read failure")));
        Assert.False(pods.IsCheckpointing);
    }
    [Fact]
    public void TransferFenceBlocksProviderCaptureAndMutationUntilReloadAfterFailure()
    {
        using var hub = new LifecycleHub((_, _) => { }); var persistence = new Persistence(); using var pods = new DungeonPodPersistence(hub, persistence);
        var session = hub.Begin(SessionOrigin.SaveLoad, "save"); hub.PlayerReady(session); persistence.Provider.Restore(hub.CurrentSession!, null);
        var snapshot = persistence.Provider.Capture();
        using (var outer = pods.BeginTransfer())
        {
            Assert.NotNull(outer); Assert.False(pods.CanMutate);
            using (var nested = pods.BeginTransfer())
            {
                Assert.NotNull(nested); Assert.Throws<InvalidOperationException>(() => persistence.Provider.Capture());
                nested!.Failed();
            }
        }
        Assert.False(pods.CanMutate); Assert.Null(pods.BeginTransfer());
        Assert.Throws<InvalidOperationException>(() => persistence.Provider.Capture());
        persistence.Provider.Restore(hub.CurrentSession!, snapshot);
        Assert.True(pods.CanMutate); Assert.NotNull(persistence.Provider.Capture());
        persistence.MutationAllowed = false; Assert.Null(pods.BeginTransfer());
    }
    [Fact]
    public void MissingOverflowReceiptDoesNotAttestDeliveryAndDispatchRefusesCapture()
    {
        using var hub = new LifecycleHub((_, _) => { }); var persistence = new Persistence(); using var pods = new DungeonPodPersistence(hub, persistence);
        var session = hub.Begin(SessionOrigin.SaveLoad, "save"); hub.PlayerReady(session); persistence.Provider.Restore(hub.CurrentSession!, null);
        var crew = new Dictionary<string, int> { ["Marine"] = 2 };
        var pod = new DungeonPodResumeState(Guid.NewGuid(), Guid.NewGuid(), DungeonPodPhase.Returning, true, true, false, crew, parentShipId: "ship");
        TrackOperation(pods, pod.OperationId); pods.Track(pod);
        Assert.False(pods.Return(pod.Id, "ship", _ =>
        {
            Assert.False(pods.CanMutate); Assert.Throws<InvalidOperationException>(() => persistence.Provider.Capture());
            return new DungeonPodDeliveryReceipt(new Dictionary<string, int> { ["Marine"] = 1 }, new Dictionary<string, int>());
        }));
        Assert.False(pods.Get(pod.Id)!.ReturnDelivered); Assert.False(pods.Get(pod.Id)!.CanRecover);
        var complete = new DungeonPodDeliveryReceipt(new Dictionary<string, int> { ["Marine"] = 1 }, new Dictionary<string, int> { ["Marine"] = 1 });
        Assert.True(complete.AccountsFor(crew));
    }
    [Fact]
    public void ReturnRequiresExactParentAndNeverRetriesAThrowingNativeDelivery()
    {
        using var hub = new LifecycleHub((_, _) => { }); var persistence = new Persistence(); using var pods = new DungeonPodPersistence(hub, persistence);
        var session = hub.Begin(SessionOrigin.SaveLoad, "save"); hub.PlayerReady(session); persistence.Provider.Restore(hub.CurrentSession!, null);
        var pod = new DungeonPodResumeState(Guid.NewGuid(), Guid.NewGuid(), DungeonPodPhase.Returning, true, true, false,
            new Dictionary<string, int> { ["Marine"] = 2 }, parentShipId: "ship-guid");
        TrackOperation(pods, pod.OperationId); pods.Track(pod); var calls = 0;
        Assert.False(pods.Return(pod.Id, "another-ship", _ => { calls++; return null; }));
        Assert.Throws<InvalidOperationException>(() => pods.Return(pod.Id, "ship-guid", manifest =>
        { calls++; Assert.Equal(2, manifest["Marine"]); throw new InvalidOperationException("native failure"); }));
        Assert.False(pods.Return(pod.Id, "ship-guid", _ => { calls++; return null; })); Assert.Equal(1, calls);
        Assert.False(pods.Get(pod.Id)!.ReturnDelivered);
        var payload = persistence.Provider.Capture(); persistence.Provider.Restore(hub.CurrentSession!, payload);
        Assert.False(pods.Return(pod.Id, "ship-guid", _ => { calls++; return null; })); Assert.Equal("ship-guid", pods.Get(pod.Id)!.ParentShipId);
    }
    [Fact]
    public void SlotChangesRollbackAndUnwritableSaveWindowsCannotBorrowPodObligations()
    {
        using var hub = new LifecycleHub((_, _) => { }); var persistence = new Persistence(); using var pods = new DungeonPodPersistence(hub, persistence);
        var session = hub.Begin(SessionOrigin.SaveLoad, "first"); hub.PlayerReady(session);
        var pod = new DungeonPodResumeState(Guid.NewGuid(), Guid.NewGuid(), DungeonPodPhase.Returning, true, true, false, new Dictionary<string, int> { ["Marine"] = 2 });
        Assert.False(pods.Track(pod)); persistence.Provider.Restore(hub.CurrentSession!, null); TrackOperation(pods, pod.OperationId); Assert.True(pods.Track(pod));
        var pending = persistence.Provider.Capture(); persistence.MutationAllowed = false;
        Assert.Null(pods.BeginReturn(pod.Id)); Assert.Single(pods.Snapshot);
        persistence.MutationAllowed = true; Assert.NotNull(pods.BeginReturn(pod.Id)); Assert.True(pods.Delivered(pod.Id));
        persistence.Provider.Restore(hub.CurrentSession!, pending); Assert.True(pods.Get(pod.Id)!.CanRecover);
        var next = hub.Begin(SessionOrigin.NewGame, "new"); hub.PlayerReady(next);
        Assert.Empty(pods.Snapshot); Assert.Null(pods.BeginReturn(pod.Id));
        persistence.Provider.Restore(hub.CurrentSession!, null); Assert.Empty(pods.Snapshot);
    }
}
