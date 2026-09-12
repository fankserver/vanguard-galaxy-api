using System;
using System.Linq;
using VGModAPI;

namespace StationCommerce;

public static class InventoryMoves
{
    public static IInventoryTransfer Move(IInventory source, IInventory destination, Guid selectedStack, int count)
        => source.MoveTo(destination, selectedStack, count);

    public static IInventory? CurrentCargo(IGame game) => game.Inventories.Discover()
        .Inventories.FirstOrDefault(inventory => inventory.Reference.Kind == InventoryKind.ShipCargo);
}
