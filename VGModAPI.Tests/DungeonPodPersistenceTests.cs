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
