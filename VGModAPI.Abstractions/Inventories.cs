using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace VGModAPI;

public enum InventoryKind { ShipCargo, PlayerArmory, PlayerData, StationMaterials }
public enum InventoryOwner { LocalPlayer }
public enum InventoryAccess { Available, Unavailable, Unsupported, DockingRequired }
public enum InventoryTransferStatus
{
    Succeeded, Partial, InvalidRequest, NotReady, Stale, Missing, AccessDenied, Unsupported,
    Protected, InsufficientStock, CapacityExceeded, Changed, Failed, RecoveryRequired, Busy, LimitReached
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
public sealed class InventoryHandle
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
    public InventoryHandle Handle { get; }
    public InventoryAccess Access { get; }
    public double? Capacity { get; }
    public double? SpaceUsed { get; }
    public IReadOnlyList<InventoryStackSnapshot> Stacks { get; }
    public InventorySnapshot(InventoryHandle handle, InventoryAccess access, double? capacity, double? spaceUsed, IEnumerable<InventoryStackSnapshot> stacks)
    { Handle = handle; Access = access; Capacity = capacity; SpaceUsed = spaceUsed; Stacks = new ReadOnlyCollection<InventoryStackSnapshot>(new List<InventoryStackSnapshot>(stacks)); }
}
public sealed class InventoryDiscovery
{
    public InventoryTransferStatus Status { get; }
    public IReadOnlyList<InventorySnapshot> Inventories { get; }
    public InventoryDiscovery(InventoryTransferStatus status, IEnumerable<InventorySnapshot> inventories)
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
    public bool RequiresRecovery => Status == InventoryTransferStatus.RecoveryRequired;
    public InventoryTransferResult(Guid operationId, InventoryTransferStatus status, int requested, int? removed, int? accepted, int? returned)
    { OperationId = operationId; Status = status; Requested = requested; Removed = removed; Accepted = accepted; Returned = returned; }
}
public sealed class InventoryRecovery
{
    public Guid SessionId { get; }
    public InventoryTransferResult Result { get; }
    public InventoryRecovery(Guid sessionId, InventoryTransferResult result) { SessionId = sessionId; Result = result; }
}
public interface IInventoryService : IServiceStatus
{
    Guid? SessionId { get; }
    InventoryRecovery? PendingRecovery { get; }
    InventoryDiscovery Discover(Guid expectedSessionId);
    InventorySnapshot? Resolve(Guid expectedSessionId, InventoryReference reference);
    InventoryTransferResult Transfer(Guid operationId, InventoryHandle source, InventoryHandle destination, Guid stackId, int quantity, InventoryTransferOptions options);
    InventoryTransferResult Recover(Guid expectedSessionId, Guid operationId);
    IDisposable Subscribe(string pluginId, Action<InventoryTransferResult> callback);
}
