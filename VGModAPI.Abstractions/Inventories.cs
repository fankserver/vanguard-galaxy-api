using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace VGModAPI;

public enum InventoryKind { ShipCargo, PlayerArmory, PlayerData, StationMaterials }
public enum InventoryOwner { LocalPlayer }
public enum InventoryAccess { Available, Unavailable, Unsupported, DockingRequired }
public enum InventoryTransferStatus
{
    Succeeded, Partial, Pending, InvalidRequest, NotReady, GameEnded, Missing, AccessDenied, Unsupported,
    Protected, InsufficientStock, CapacityExceeded, ItemChanged, Failed, LimitReached
}

/// <summary>Save-local identity. Ship cargo names an exact ship GUID; station storage names an exact POI ID.
/// Armory/data use an empty location ID and belong to the local player, not the current station.</summary>
public sealed class InventoryReference : IEquatable<InventoryReference>
{
    public InventoryKind Kind { get; }
    public string LocationId { get; }
    public InventoryOwner Owner => InventoryOwner.LocalPlayer;
    public InventoryReference(InventoryKind kind, string locationId = "")
    {
        if (!Enum.IsDefined(typeof(InventoryKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (locationId == null || locationId.Length > 512) throw new ArgumentException("Bounded location required.", nameof(locationId));
        if ((kind == InventoryKind.ShipCargo || kind == InventoryKind.StationMaterials) == string.IsNullOrWhiteSpace(locationId))
            throw new ArgumentException("Cargo/station references require a location; global inventories must not have one.", nameof(locationId));
        foreach (char c in locationId) if (char.IsControl(c)) throw new ArgumentException("Invalid location.", nameof(locationId));
        if ((kind == InventoryKind.PlayerArmory || kind == InventoryKind.PlayerData) && locationId.Length != 0)
            throw new ArgumentException("Global inventories have no location ID.", nameof(locationId));
        Kind = kind; LocationId = locationId;
    }
    public bool Equals(InventoryReference? other) => other != null && Kind == other.Kind && LocationId == other.LocationId;
    public override bool Equals(object? obj) => Equals(obj as InventoryReference);
    public override int GetHashCode() => ((int)Kind * 397) ^ StringComparer.Ordinal.GetHashCode(LocationId);
}
internal sealed class InventoryHandle
{
    public Guid SessionId { get; }
    public InventoryReference Reference { get; }
    public InventoryHandle(Guid sessionId, InventoryReference reference)
    { SessionId = sessionId; Reference = reference ?? throw new ArgumentNullException(nameof(reference)); }
}
public sealed class InventoryStackSnapshot
{
    public Guid StackId { get; }
    public string ItemId { get; }
    public string Name { get; }
    public int Count { get; }
    public bool Favourite { get; }
    public double UnitVolume { get; }
    public bool Transferable { get; }
    public InventoryStackSnapshot(Guid stackId, string itemId, string name, int count, bool favourite, double unitVolume, bool transferable)
    { StackId = stackId; ItemId = itemId; Name = name; Count = count; Favourite = favourite; UnitVolume = unitVolume; Transferable = transferable; }
}
public sealed class InventorySnapshot
{
    internal InventoryHandle Handle { get; }
    public InventoryReference Reference => Handle.Reference;
    public InventoryAccess Access { get; }
    public double? Capacity { get; }
    public double? SpaceUsed { get; }
    public IReadOnlyList<InventoryStackSnapshot> Stacks { get; }
    internal InventorySnapshot(InventoryHandle handle, InventoryAccess access, double? capacity, double? spaceUsed, IEnumerable<InventoryStackSnapshot> stacks)
    { Handle = handle; Access = access; Capacity = capacity; SpaceUsed = spaceUsed; Stacks = new ReadOnlyCollection<InventoryStackSnapshot>(new List<InventoryStackSnapshot>(stacks)); }
}
internal sealed class InventorySnapshotSet
{
    public InventoryTransferStatus Status { get; }
    public IReadOnlyList<InventorySnapshot> Inventories { get; }
    public InventorySnapshotSet(InventoryTransferStatus status, IEnumerable<InventorySnapshot> inventories)
    { Status = status; Inventories = new ReadOnlyCollection<InventorySnapshot>(new List<InventorySnapshot>(inventories)); }
}
public sealed class InventoryTransferOptions
{
    public bool AllowPartial { get; }
    public bool IncludeFavourite { get; }
    public InventoryTransferOptions(bool allowPartial = false, bool includeFavourite = false)
    { AllowPartial = allowPartial; IncludeFavourite = includeFavourite; }
}
public sealed class InventoryTransferResult
{
    public Guid OperationId { get; }
    public InventoryTransferStatus Status { get; }
    public int Requested { get; }
    public int? Removed { get; }
    public int? Accepted { get; }
    public int? Returned { get; }
    public InventoryTransferResult(Guid operationId, InventoryTransferStatus status, int requested, int? removed, int? accepted, int? returned)
    { OperationId = operationId; Status = status; Requested = requested; Removed = removed; Accepted = accepted; Returned = returned; }
}
public sealed class InventoryDiscovery
{
    public InventoryTransferStatus Status { get; }
    public IReadOnlyList<IInventory> Inventories { get; }
    internal InventoryDiscovery(InventoryTransferStatus status, IEnumerable<IInventory> inventories)
    { Status = status; Inventories = new ReadOnlyCollection<IInventory>(new List<IInventory>(inventories)); }
}

/// <summary>Inventories belonging to one game, never rebound to a replacement save.</summary>
public interface IInventories
{
    IGame Game { get; }
    InventoryDiscovery Discover();
    IInventory Get(InventoryReference reference);
}

public interface IInventory
{
    IGame Game { get; }
    InventoryReference Reference { get; }
    InventorySnapshot? Snapshot { get; }
    /// <summary>Request a move of the selected stack. The API owns transaction identity, safe execution
    /// and recovery. A missing or changed stack is refused rather than replaced with another stack.</summary>
    IInventoryTransfer MoveTo(IInventory destination, Guid stackId, int quantity, InventoryTransferOptions? options = null);
}

public interface IInventoryTransfer
{
    IGame Game { get; }
    InventoryTransferResult Result { get; }
    /// <summary>Actionable terminal result, never a request to run recovery. No replay; delivery ends with the game.</summary>
    event Action<IInventoryTransfer>? Completed;
}
