using System;
using System.Reflection;

namespace VGModAPI.Core.Integration;
internal sealed partial class InventoryNativeBackend
{
    public PreparedInventoryMove Prepare(InventoryHandle source, InventoryHandle destination, Guid stackId, int quantity, InventoryTransferOptions options)
    {
        PreparedInventoryMove Refuse(InventoryTransferStatus status) => new(status);
        var player = Player(source.SessionId);
        var from = Find(player, source.Reference); var to = Find(player, destination.Reference);
        if (from?.Inventory == null || to?.Inventory == null) return Refuse(InventoryTransferStatus.Missing);
        if (ReferenceEquals(from.Inventory, to.Inventory)) return Refuse(InventoryTransferStatus.InvalidRequest);
        if (from.Inventory.GetType() != _inventory || to.Inventory.GetType() != _inventory ||
            source.Reference.Kind == InventoryKind.PlayerData || destination.Reference.Kind == InventoryKind.PlayerData) return Refuse(InventoryTransferStatus.Unsupported);
        if (source.Reference.Kind != InventoryKind.StationMaterials || destination.Reference.Kind != InventoryKind.StationMaterials)
        {
            var ship = Get(player, "currentSpaceShip"); var station = Get(_station, "current");
            if (ship == null || station == null || Get(ship, "dockingState")?.ToString() != "Docked") return Refuse(InventoryTransferStatus.AccessDenied);
            if ((from.Reference.Kind == InventoryKind.StationMaterials && !ReferenceEquals(from.Location, station)) ||
                (to.Reference.Kind == InventoryKind.StationMaterials && !ReferenceEquals(to.Location, station))) return Refuse(InventoryTransferStatus.AccessDenied);
        }
        var beforeFrom = (Array)_all.GetValue(from.Inventory)!; var beforeTo = (Array)_all.GetValue(to.Inventory)!;
        object? selected = null; int index = -1;
        for (int i = 0; i < beforeFrom.Length; i++)
        {
            var row = beforeFrom.GetValue(i);
            if (row != null && _tokens.TryGetValue(row, out var token) && token.Id == stackId) { selected = row; index = i; break; }
        }
        if (selected == null) return Refuse(InventoryTransferStatus.Missing);
        if (!Supported(selected)) return Refuse(InventoryTransferStatus.Unsupported);
        var item = Get(selected, "item")!;
        if (Flag(selected, "favourite") && !options.IncludeFavourite) return Refuse(InventoryTransferStatus.Protected);
        if (Convert.ToInt32(_player.GetMethod("RequiredItemCountForMissions")!.Invoke(player, new[] { item })) > 0)
            return Refuse(InventoryTransferStatus.Protected);
        bool Can(string method) => (bool)item.GetType().GetMethod(method, Type.EmptyTypes)!.Invoke(item, null)!;
        if (Can("CanGoInDataInventory") || (to.Reference.Kind == InventoryKind.PlayerArmory && !Can("CanGoInArmory")) ||
            (to.Reference.Kind == InventoryKind.StationMaterials && !Can("CanGoInMaterials"))) return Refuse(InventoryTransferStatus.Unsupported);
        // Mission requirement evaluation can call extension objectives. Revalidate after all
        // callback-capable admission, before reading stock or preparing either replacement array.
        if (!_current(source.SessionId) || !ReferenceEquals(Get(_player, "current"), player) ||
            !ReferenceEquals(Find(player, source.Reference)?.Inventory, from.Inventory) || !ReferenceEquals(Find(player, destination.Reference)?.Inventory, to.Inventory))
            return Refuse(InventoryTransferStatus.GameEnded);
        if (!ReferenceEquals(_all.GetValue(from.Inventory), beforeFrom) || !ReferenceEquals(_all.GetValue(to.Inventory), beforeTo) ||
            !ReferenceEquals(beforeFrom.GetValue(index), selected)) return Refuse(InventoryTransferStatus.ItemChanged);
        if (!Supported(selected)) return Refuse(InventoryTransferStatus.Unsupported);
        if (Flag(selected, "favourite") && !options.IncludeFavourite) return Refuse(InventoryTransferStatus.Protected);
        if (source.Reference.Kind != InventoryKind.StationMaterials || destination.Reference.Kind != InventoryKind.StationMaterials)
        {
            var ship = Get(player, "currentSpaceShip"); var station = Get(_station, "current");
            if (ship == null || station == null || Get(ship, "dockingState")?.ToString() != "Docked" ||
                (from.Reference.Kind == InventoryKind.StationMaterials && !ReferenceEquals(from.Location, station)) ||
                (to.Reference.Kind == InventoryKind.StationMaterials && !ReferenceEquals(to.Location, station))) return Refuse(InventoryTransferStatus.AccessDenied);
        }
        int stock = (int)Get(selected, "count")!;
        double volume = Number(item, "m3"), capacity = Capacity(to), used = Number(to.Inventory, "spaceUsed");
        if (double.IsNaN(volume) || double.IsInfinity(volume) || volume < 0 || double.IsNaN(capacity) || double.IsInfinity(capacity) ||
            double.IsNaN(used) || double.IsInfinity(used)) return Refuse(InventoryTransferStatus.Unsupported);
        int amount = Math.Min(quantity, stock);
        if (amount < quantity && !options.AllowPartial) return Refuse(InventoryTransferStatus.InsufficientStock);
        int fitting = volume == 0 ? amount : (int)Math.Max(0, Math.Min(amount, Math.Floor((capacity - used) / volume)));
        if (fitting < amount && !options.AllowPartial) return Refuse(InventoryTransferStatus.CapacityExceeded);
        amount = Math.Min(amount, fitting);
        if (amount <= 0) return Refuse(stock <= 0 ? InventoryTransferStatus.InsufficientStock : InventoryTransferStatus.CapacityExceeded);
        int target = -1; for (int i = 0; i < beforeTo.Length; i++) if (beforeTo.GetValue(i) == null) { target = i; break; }
        if (target < 0) target = beforeTo.Length;
        if (target >= 16384) return Refuse(InventoryTransferStatus.LimitReached);
        var afterFrom = (Array)beforeFrom.Clone();
        var afterTo = Array.CreateInstance(_stack, Math.Max(beforeTo.Length, target + 1)); Array.Copy(beforeTo, afterTo, beforeTo.Length);
        afterFrom.SetValue(stock == amount ? null : Copy(selected, from.Inventory, index, stock - amount), index);
        afterTo.SetValue(Copy(selected, to.Inventory, target, amount), target);
        var commit = new InventoryPairCommit(() => _all.GetValue(from.Inventory)!, value => _all.SetValue(from.Inventory, value),
            () => _all.GetValue(to.Inventory)!, value => _all.SetValue(to.Inventory, value), beforeFrom, beforeTo, afterFrom, afterTo);
        return new(InventoryTransferStatus.Succeeded, amount, commit, () =>
        {
            _inventory.GetMethod("UpdateVisibleItems")!.Invoke(from.Inventory, null);
            _inventory.GetMethod("UpdateVisibleItems")!.Invoke(to.Inventory, null);
        });
    }
    private object Copy(object original, object inventory, int slot, int count)
    {
        var row = Activator.CreateInstance(_stack, Get(original, "item"), inventory, slot, count, Flag(original, "canBuyback"))!;
        foreach (string name in new[] { "favourite", "isSoldByPlayer", "costItem", "costCount" })
            _stack.GetField(name)!.SetValue(row, Get(original, name));
        return row;
    }
}
