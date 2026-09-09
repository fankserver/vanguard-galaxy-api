using System;
using System.Collections.Generic;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class DungeonReturnReceiptCollectorTests
{
    [Fact]
    public void NestedUnknownReturnsMaskOuterReceiptsAndOverflowIsCountedOnce()
    {
        var collector = new DungeonReturnReceiptCollector(); var recipient = new object(); var origin = new object(); var data = new object();
        using var scope = collector.Begin(recipient, origin);
        using (collector.Begin(null, null))
        { collector.CrewAdded(recipient, "Marine", 3, 0); collector.OverflowPersisted(origin, "Marine", 3, data); }
        collector.CrewAdded(new object(), "Marine", 3, 0);
        collector.CrewAdded(recipient, "Marine", 3, 2); collector.OverflowPersisted(origin, "Marine", 2, data);
        collector.OverflowPersisted(origin, "Marine", 2, data);
        Assert.True(scope.Receipt.AccountsFor(new Dictionary<string, int> { ["Marine"] = 3 }));
        Assert.Equal(1, scope.Receipt.Accepted["Marine"]); Assert.Equal(2, scope.Receipt.OverflowCreated["Marine"]);
    }
    [Fact]
    public void ObservedReturnRequiresCompleteReceiptAndBlocksMidEffectCapture()
    {
        using var hub = new LifecycleHub((_, _) => { }); var persistence = new DungeonPodPersistenceTests.Persistence(); using var state = new DungeonPodPersistence(hub, persistence);
        var session = hub.Begin(SessionOrigin.SaveLoad, "save"); hub.PlayerReady(session); persistence.Provider.Restore(hub.CurrentSession!, null);
        var operation = Guid.NewGuid(); DungeonPodPersistenceTests.TrackOperation(state, operation);
        var pod = new DungeonPodResumeState(Guid.NewGuid(), operation, DungeonPodPhase.Returning, true, true, false, new Dictionary<string, int> { ["Marine"] = 3 }, parentShipId: "ship-guid");
        Assert.True(state.Track(pod));
        using (var attempt = state.BeginObservedReturn(pod.Id, "ship-guid"))
        {
            Assert.NotNull(attempt); Assert.False(state.CanMutate); Assert.Throws<InvalidOperationException>(() => persistence.Provider.Capture());
            Assert.True(attempt!.Complete(new(new Dictionary<string, int> { ["Marine"] = 1 }, new Dictionary<string, int> { ["Marine"] = 2 })));
        }
        Assert.True(state.Get(pod.Id)!.ReturnDelivered); Assert.True(state.CanMutate);
        var saved = persistence.Provider.Capture(); persistence.Provider.Restore(hub.CurrentSession!, saved);
        Assert.Null(state.BeginObservedReturn(pod.Id, "ship-guid"));
    }
}
