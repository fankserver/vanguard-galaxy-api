using System;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;
public sealed class InventoryServiceTests
{
    private sealed class Backend : IInventoryBackend
    {
        internal object Source = new int[] { 10 }, Destination = new int[] { 0 };
        internal int Calls;
        public InventorySnapshotSet Discover(Guid session) => new(InventoryTransferStatus.Succeeded, Array.Empty<InventorySnapshot>());
        public InventorySnapshot? Resolve(Guid session, InventoryReference reference) => null;
        public PreparedInventoryMove Prepare(InventoryHandle source, InventoryHandle destination, Guid stack, int quantity, InventoryTransferOptions options)
        {
            Calls++;
            return new(InventoryTransferStatus.Succeeded, quantity, new InventoryPairCommit(() => Source, value => Source = value,
                () => Destination, value => Destination = value, Source, Destination,
                new int[] { ((int[])Source)[0] - quantity }, new int[] { ((int[])Destination)[0] + quantity }));
        }
    }
    [Fact]
    public void DeduplicatesAndRejectsChangedRequestsReentrancySerializationAndStaleSessions()
    {
        using var hub = new LifecycleHub((_, _) => { }); hub.SetCapability("inventories", true, "test");
        var session = hub.Begin(SessionOrigin.NewGame, null); hub.PlayerReady(session); hub.GameplayInitialized(session);
        var backend = new Backend(); using var service = new InventoryService(hub, () => backend);
        var source = new InventoryHandle(session, new(InventoryKind.StationMaterials, "a"));
        var destination = new InventoryHandle(session, new(InventoryKind.StationMaterials, "b"));
        var operation = Guid.NewGuid(); var stack = Guid.NewGuid(); int notifications = 0;
        using var first = service.Subscribe("throwing", _ => throw new InvalidOperationException());
        using var second = service.Subscribe("observer", _ =>
        {
            notifications++;
            Assert.Equal(InventoryTransferStatus.Pending, service.Transfer(Guid.NewGuid(), source, destination, stack, 1, new()).Status);
        });
        var result = service.Transfer(operation, source, destination, stack, 3, new());
        Assert.Equal(InventoryTransferStatus.Succeeded, result.Status); Assert.Equal(3, result.Accepted);
        Assert.Same(result, service.Transfer(operation, source, destination, stack, 3, new()));
        Assert.Equal(InventoryTransferStatus.InvalidRequest, service.Transfer(operation, source, destination, stack, 4, new()).Status);
        Assert.Equal(1, notifications); Assert.Equal(1, backend.Calls); Assert.Equal(7, ((int[])backend.Source)[0]);
        service.BeginSerialization();
        Assert.Equal(InventoryTransferStatus.Pending, service.Transfer(Guid.NewGuid(), source, destination, stack, 1, new()).Status);
        service.EndSerialization();
        hub.Begin(SessionOrigin.NewGame, null);
        Assert.Equal(InventoryTransferStatus.GameEnded, service.Transfer(Guid.NewGuid(), source, destination, stack, 1, new()).Status);
    }
}
