using System;
using System.Collections.Generic;
using VGModAPI.Core;
using Xunit;
namespace VGModAPI.Tests;
public sealed class DungeonDockedRefundTests
{
    private sealed class Fixture : IDisposable
    {
        internal readonly LifecycleHub Hub = new((_, _) => { });
        internal readonly DungeonPodPersistenceTests.Persistence Persistence = new();
        internal readonly DungeonPodPersistence State;
        internal readonly Guid Operation = Guid.NewGuid(), Pod = Guid.NewGuid();
        internal Fixture()
        {
            State = new(Hub, Persistence); var session = Hub.Begin(SessionOrigin.SaveLoad, "save"); Hub.PlayerReady(session); Persistence.Provider.Restore(Hub.CurrentSession!, null);
            DungeonPodPersistenceTests.TrackOperation(State, Operation);
            State.Track(new(Pod, Operation, DungeonPodPhase.Docked, true, false, false, new Dictionary<string, int>(), parentShipId: "ship-guid", transport: new("pod", false, Crew(2), new float[9], "ship-guid")));
        }
        public void Dispose() { State.Dispose(); Hub.Dispose(); }
    }
    private static Dictionary<string, int> Crew(int count) => new() { ["Marine"] = count };
    private static DungeonPodDeliveryReceipt Receipt(int accepted, int overflow) => new(accepted == 0 ? new() : Crew(accepted), overflow == 0 ? new() : Crew(overflow));
    [Fact]
    public void CancellationFencesSavesUntilAggregateAcceptedAndOverflowReceiptCompletes()
    {
        using var f = new Fixture();
        using (var cancellation = f.State.BeginCancellation(f.Operation))
        {
            Assert.NotNull(cancellation);
            using (var attempt = f.State.BeginDockedRefunds(f.Operation, new[] { f.Pod }))
            {
                Assert.NotNull(attempt); Assert.True(f.State.Get(f.Pod)!.ReturnAttempted);
                Assert.Throws<InvalidOperationException>(() => f.Persistence.Provider.Capture());
                Assert.False(attempt!.Complete(Receipt(1, 0))); Assert.True(attempt.Complete(Receipt(1, 1)));
                Assert.Equal(DungeonPodPhase.Refunded, f.State.Get(f.Pod)!.Phase);
            }
            Assert.Throws<InvalidOperationException>(() => f.Persistence.Provider.Capture());
        }
        var saved = f.Persistence.Provider.Capture(); f.Persistence.Provider.Restore(f.Hub.CurrentSession!, saved);
        Assert.True(f.State.Get(f.Pod)!.ReturnDelivered); Assert.False(f.State.Get(f.Pod)!.RequiresRecovery);
        Assert.Null(f.State.BeginDockedRefunds(f.Operation, new[] { f.Pod }));
    }
    [Fact]
    public void UncertainAttemptSurvivesReloadWithoutAutomaticRefundReplay()
    {
        using var f = new Fixture();
        using (var attempt = f.State.BeginDockedRefunds(f.Operation, new[] { f.Pod }))
        { Assert.NotNull(attempt); Assert.False(attempt!.Complete(Receipt(1, 0))); }
        var saved = f.Persistence.Provider.Capture(); f.Persistence.Provider.Restore(f.Hub.CurrentSession!, saved);
        Assert.True(f.State.Get(f.Pod)!.ReturnAttempted); Assert.False(f.State.Get(f.Pod)!.ReturnDelivered);
        Assert.Null(f.State.BeginDockedRefunds(f.Operation, new[] { f.Pod }));
    }
    [Fact]
    public void BatchValidationDoesNotPartiallyMarkValidPods()
    {
        using var f = new Fixture();
        Assert.Null(f.State.BeginDockedRefunds(f.Operation, new[] { f.Pod, Guid.NewGuid() }));
        Assert.False(f.State.Get(f.Pod)!.ReturnAttempted);
    }
    [Fact]
    public void OnlyOwningTerminalCanRefundAndReloadInvalidatesItsPermission()
    {
        using var f = new Fixture(); var before = f.Persistence.Provider.Capture();
        using var terminal = f.State.BeginTerminal(f.Operation); Assert.NotNull(terminal);
        Assert.Null(f.State.BeginDockedRefunds(Guid.NewGuid(), new[] { f.Pod }));
        using (var refund = f.State.BeginDockedRefunds(f.Operation, new[] { f.Pod })) { Assert.NotNull(refund); }
        f.Persistence.Provider.Restore(f.Hub.CurrentSession!, before);
        Assert.Null(f.State.BeginDockedRefunds(f.Operation, new[] { f.Pod }));
    }
    [Fact]
    public void CancellationFailureBlocksSnapshotsUntilReload()
    {
        using var f = new Fixture(); var before = f.Persistence.Provider.Capture();
        using (var cancellation = f.State.BeginCancellation(f.Operation)) { Assert.NotNull(cancellation); cancellation!.Failed(); }
        Assert.Throws<InvalidOperationException>(() => f.Persistence.Provider.Capture());
        Assert.Null(f.State.BeginDockedRefunds(f.Operation, new[] { f.Pod }));
        f.Persistence.Provider.Restore(f.Hub.CurrentSession!, before); Assert.True(f.State.CanMutate);
    }
}
