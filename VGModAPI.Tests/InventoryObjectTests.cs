using System;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class InventoryObjectTests
{
    private sealed class Backend : IInventoryBackend
    {
        internal object Source = new int[] { 10 }, Destination = new int[] { 0 };
        internal bool Fault, Recoverable;
        internal int Prepares;
        public InventorySnapshotSet Discover(Guid session) => new(InventoryTransferStatus.Succeeded, Array.Empty<InventorySnapshot>());
        public InventorySnapshot? Resolve(Guid session, InventoryReference reference) => null;
        public PreparedInventoryMove Prepare(InventoryHandle source, InventoryHandle destination, Guid stack, int quantity, InventoryTransferOptions options)
        {
            Prepares++;
            var beforeSource = Source; var beforeDestination = Destination;
            return new(InventoryTransferStatus.Succeeded, quantity, new InventoryPairCommit(() => Source, value =>
            {
                if (Fault && !Recoverable && ReferenceEquals(value, beforeSource)) throw new Exception("rollback blocked");
                Source = value;
            }, () => Destination, value =>
            {
                if (Fault && !ReferenceEquals(value, beforeDestination)) throw new Exception("destination write");
                Destination = value;
            }, beforeSource, beforeDestination, new[] { ((int[])Source)[0] - quantity }, new[] { ((int[])Destination)[0] + quantity }));
        }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly LifecycleHub Hub = new((_, _) => { });
        internal readonly Backend Backend = new();
        internal readonly InventoryService Engine;
        internal readonly GameService Games;
        internal Fixture()
        {
            foreach (var name in new[] { "inventories", "session-lifecycle", "save-outcomes" }) Hub.SetCapability(name, true, "Bound");
            Engine = new InventoryService(Hub, () => Backend);
            Games = new GameService(Hub, new NavigationService(Hub, _ => null, (_, _, _) => NavigationStatus.Unavailable, (_, _) => null), Engine);
            Start();
        }
        internal IGame Game => Games.Current!;
        internal IInventory Source => Game.Inventories.Get(new(InventoryKind.StationMaterials, "a"));
        internal IInventory Destination => Game.Inventories.Get(new(InventoryKind.StationMaterials, "b"));
        internal void Start() { var id = Hub.Begin(SessionOrigin.NewGame, null); Hub.PlayerReady(id); Hub.GameplayInitialized(id); }
        internal void Tick() { Engine.Tick(); Hub.Gameplay.Tick(); }
        public void Dispose() { Games.Dispose(); Engine.Dispose(); Hub.Dispose(); }
    }

    [Fact]
    public void MoveOwnsItsIdentityAndCompletionCanRequestAnotherMoveWithoutReentrancyPlumbing()
    {
        using var f = new Fixture();
        var move = f.Source.MoveTo(f.Destination, Guid.NewGuid(), 3);
        Assert.Equal(InventoryTransferStatus.Pending, move.Result.Status);
        IInventoryTransfer? next = null;
        move.Completed += completed =>
        {
            Assert.False(f.Hub.IsDispatchingCallbacks); Assert.Same(f.Game, completed.Game);
            Assert.Equal(3, completed.Result.Accepted);
            next = f.Source.MoveTo(f.Destination, Guid.NewGuid(), 2);
        };
        f.Tick(); Assert.NotNull(next); Assert.Equal(InventoryTransferStatus.Pending, next!.Result.Status);
        Assert.Equal(1, f.Backend.Prepares); f.Tick(); Assert.Equal(2, f.Backend.Prepares);
        Assert.Equal(InventoryTransferStatus.Succeeded, next.Result.Status);
        Assert.NotEqual(move.Result.OperationId, next.Result.OperationId);
        Assert.Equal(5, ((int[])f.Backend.Source)[0]); Assert.Equal(5, ((int[])f.Backend.Destination)[0]);
    }

    [Fact]
    public void SaveInFlightWaitsInternallyRatherThanRefusingWithBusyOrAskingForRetry()
    {
        using var f = new Fixture(); var operation = Guid.NewGuid();
        f.Hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveStarted, f.Hub.CurrentSession, operation, "slot"));
        var move = f.Source.MoveTo(f.Destination, Guid.NewGuid(), 2);
        f.Tick(); Assert.Equal(InventoryTransferStatus.Pending, move.Result.Status); Assert.Equal(0, f.Backend.Prepares);
        f.Hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveSkipped, f.Hub.CurrentSession, operation, "slot"));
        f.Tick(); Assert.Equal(InventoryTransferStatus.Succeeded, move.Result.Status); Assert.Equal(1, f.Backend.Prepares);
    }

    [Fact]
    public void AutomaticRecoveryRetainsTheSaveFenceAndDoesNotReplayTheTransfer()
    {
        using var f = new Fixture(); f.Backend.Fault = true;
        var move = f.Source.MoveTo(f.Destination, Guid.NewGuid(), 3); var completions = 0;
        move.Completed += _ => completions++;
        f.Tick();
        Assert.Equal(InventoryTransferStatus.Pending, move.Result.Status); Assert.Null(move.Result.Removed);
        Assert.Throws<InvalidOperationException>(f.Engine.AssertSafeToSave);
        Assert.Equal(7, ((int[])f.Backend.Source)[0]); Assert.Equal(0, ((int[])f.Backend.Destination)[0]);
        f.Tick(); Assert.Equal(0, completions); Assert.Equal(1, f.Backend.Prepares);
        f.Backend.Recoverable = true; f.Tick();
        Assert.Equal(InventoryTransferStatus.ItemChanged, move.Result.Status);
        Assert.Equal(0, move.Result.Removed); Assert.Equal(1, completions); Assert.Equal(1, f.Backend.Prepares);
        Assert.Equal(10, ((int[])f.Backend.Source)[0]); Assert.Equal(0, ((int[])f.Backend.Destination)[0]);
        f.Engine.AssertSafeToSave();
    }

    [Fact]
    public void CapturedInventoriesAndPendingMovesNeverRebindAcrossLoads()
    {
        using var f = new Fixture(); var source = f.Source; var destination = f.Destination;
        var pending = source.MoveTo(destination, Guid.NewGuid(), 2);
        pending.Completed += _ => Assert.Fail("Old game notification");
        f.Start(); Assert.Equal(InventoryTransferStatus.GameEnded, pending.Result.Status);
        f.Tick(); Assert.Equal(0, f.Backend.Prepares);
        var stale = source.MoveTo(destination, Guid.NewGuid(), 2);
        Assert.Equal(InventoryTransferStatus.GameEnded, stale.Result.Status);
        Assert.Null(source.Snapshot); Assert.False(source.Game.IsActive);
    }

    [Fact]
    public void RemovingCompletionHandlerBeforeDeliverySuppressesIt()
    {
        using var f = new Fixture(); var move = f.Source.MoveTo(f.Destination, Guid.NewGuid(), 1);
        Action<IInventoryTransfer> handler = _ => Assert.Fail("Removed"); move.Completed += handler;
        f.Engine.Tick(); move.Completed -= handler; f.Hub.Gameplay.Tick();
        Assert.Equal(InventoryTransferStatus.Succeeded, move.Result.Status);
    }
}
