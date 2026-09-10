using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace VGModAPI.Core.Integration;

internal sealed partial class InventoryNativeBackend : IInventoryBackend
{
    private readonly Type _inventory, _stack, _player, _station;
    private readonly WorldMapIndex _index;
    private readonly Func<Guid, bool> _current;
    private readonly FieldInfo _all;
    private readonly ConditionalWeakTable<object, Token> _tokens = new();
    internal InventoryNativeBackend(Assembly assembly, Func<Guid, bool> current)
    {
        _inventory = assembly.GetType("Source.Item.Inventory", true)!;
        _stack = _inventory.GetNestedType("InventoryItem")!;
        _player = assembly.GetType("Source.Player.GamePlayer", true)!;
        _station = assembly.GetType("Source.Galaxy.POI.SpaceStation", true)!;
        _all = _inventory.GetField("allItems", BindingFlags.NonPublic | BindingFlags.Instance) ?? throw new MissingFieldException("Inventory.allItems");
        _index = new WorldMapIndex(assembly); _current = current;
        if (_all.FieldType != _stack.MakeArrayType()) throw new InvalidOperationException("Inventory layout unsupported.");
    }
    private static object? Get(object value, string name)
    {
        var type = value as Type ?? value.GetType(); var instance = value is Type ? null : value;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var property = type.GetProperty(name, flags);
        return property != null ? property.GetValue(instance) : (type.GetField(name, flags) ?? throw new MissingMemberException(type.FullName, name)).GetValue(instance);
    }
    private static bool Flag(object value, string name) => (bool)Get(value, name)!;
    private static double Number(object value, string name) => Convert.ToDouble(Get(value, name));
    private object Player(Guid session)
    {
        if (!_current(session)) throw new InvalidOperationException("Inventory session unavailable.");
        return Get(_player, "current") ?? throw new InvalidOperationException("Player unavailable.");
    }
    private Endpoint? Find(object player, InventoryReference reference)
    {
        object? inventory, location = null;
        switch (reference.Kind)
        {
            case InventoryKind.PlayerArmory: inventory = Get(player, "globalInventory"); break;
            case InventoryKind.PlayerData: inventory = Get(player, "dataInventory"); break;
            case InventoryKind.ShipCargo:
                location = Get(player, "currentSpaceShip");
                if (location == null || (string)Get(location, "guid")! != reference.LocationId) return null;
                inventory = Get(location, "cargo"); break;
            case InventoryKind.StationMaterials:
                var map = Get(player, "map"); if (map == null) return null;
                location = _index.Read(map).FindPoint(reference.LocationId);
                if (location == null || !_station.IsInstanceOfType(location) || Flag(location, "hidden")) return null;
                inventory = Get(location, "materialStorage"); break;
            default: return null;
        }
        return new Endpoint(reference, inventory, location);
    }
    private InventorySnapshot Snapshot(Guid session, Endpoint endpoint)
    {
        var handle = new InventoryHandle(session, endpoint.Reference); var inventory = endpoint.Inventory;
        if (inventory == null) return new(handle, InventoryAccess.Unavailable, null, null, Array.Empty<InventoryStackSnapshot>());
        if (inventory.GetType() != _inventory) return new(handle, InventoryAccess.Unsupported, null, null, Array.Empty<InventoryStackSnapshot>());
        var rows = new List<InventoryStackSnapshot>();
        foreach (var row in (Array)_all.GetValue(inventory)!)
        {
            if (row == null) continue;
            var item = Get(row, "item")!;
            rows.Add(new InventoryStackSnapshot(_tokens.GetValue(row, _ => new Token()).Id, (string)Get(item, "identifier")!,
                (string)Get(item, "displayName")!, (int)Get(row, "count")!, Flag(row, "favourite"), Number(item, "m3"),
                endpoint.Reference.Kind != InventoryKind.PlayerData && Supported(row)));
        }
        return new(handle, endpoint.Reference.Kind == InventoryKind.PlayerData ? InventoryAccess.Unsupported : InventoryAccess.Available,
            Capacity(endpoint), Number(inventory, "spaceUsed"), rows);
    }
    private static double Capacity(Endpoint endpoint) => endpoint.Reference.Kind == InventoryKind.ShipCargo
        ? Number(endpoint.Location!, "cargoCapacity") : Number(endpoint.Inventory!, "capacity");
    private static bool Supported(object row)
    {
        var item = Get(row, "item")!;
        // Trade/currency routing is never treated as an ordinary movement of player belongings.
        return (string)Get(item, "identifier")! != "VanguardMark" && !Flag(row, "canBuyback") && !Flag(row, "isSoldByPlayer") &&
            Get(row, "costItem") == null && (int)Get(row, "costCount")! == 0;
    }
    public InventorySnapshotSet Discover(Guid session)
    {
        var player = Player(session); var endpoints = new List<Endpoint>();
        void Add(InventoryReference reference) { var endpoint = Find(player, reference); if (endpoint != null) endpoints.Add(endpoint); }
        Add(new(InventoryKind.PlayerArmory)); Add(new(InventoryKind.PlayerData));
        var ship = Get(player, "currentSpaceShip"); if (ship != null) Add(new(InventoryKind.ShipCargo, (string)Get(ship, "guid")!));
        var map = Get(player, "map");
        if (map != null) foreach (var point in _index.Read(map).Points)
            if (_station.IsInstanceOfType(point.Value) && !Flag(point.Value, "hidden")) Add(new(InventoryKind.StationMaterials, point.Key));
        var result = new List<InventorySnapshot>(); foreach (var endpoint in endpoints) result.Add(Snapshot(session, endpoint));
        if (!_current(session) || !ReferenceEquals(Get(_player, "current"), player)) throw new InvalidOperationException("Inventory session changed.");
        return new(InventoryTransferStatus.Succeeded, result);
    }
    public InventorySnapshot? Resolve(Guid session, InventoryReference reference)
    {
        var player = Player(session); var endpoint = Find(player, reference);
        return endpoint == null ? null : Snapshot(session, endpoint);
    }
    private sealed class Token { internal readonly Guid Id = Guid.NewGuid(); }
    private sealed class Endpoint
    {
        internal readonly InventoryReference Reference; internal readonly object? Inventory, Location;
        internal Endpoint(InventoryReference reference, object? inventory, object? location)
        { Reference = reference; Inventory = inventory; Location = location; }
    }
}
