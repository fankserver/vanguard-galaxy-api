using System;
using System.Linq;
using VGModAPI;

namespace VGModAPI.ServiceConsumers;

// Call from an explicit UI action outside API callbacks, never automatically on load.
public static class InventoryMoves
{
    public static InventoryTransferResult MoveSelected(Guid session, InventoryReference source,
        InventoryReference destination, Guid selectedStack, int count)
    {
        var service = ModApi.Services.Inventories;
        return service.Transfer(Guid.NewGuid(), new InventoryHandle(session, source),
            new InventoryHandle(session, destination), selectedStack, count, new InventoryTransferOptions());
    }
    // Stockpile-shaped immediate movement only: scheduling/fees are not implied by success.
    public static InventoryTransferResult MoveBetweenStations(Guid session, string from, string to, Guid selectedStack, int count) =>
        MoveSelected(session, new InventoryReference(InventoryKind.StationMaterials, from),
            new InventoryReference(InventoryKind.StationMaterials, to), selectedStack, count);
    public static InventoryReference? CurrentCargo(Guid session) => ModApi.Services.Inventories.Discover(session)
        .Inventories.FirstOrDefault(x => x.Handle.Reference.Kind == InventoryKind.ShipCargo)?.Handle.Reference;
}
