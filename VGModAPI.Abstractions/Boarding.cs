using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace VGModAPI;

public enum BoardingEncounterKind { Ship, Installation }
public enum BoardingPhase { Available, Approaching, AwaitingLanding, Active, Extracting, Resolved, ReturningCrew, Settled, Retired }
public enum BoardingAvailability { Available, Travelling, OperationActive, NoCrew, TargetUnavailable, SessionUnavailable, IntegrationUnavailable, IntegrityTooLow, LevelTooHigh }
public enum BoardingEventKind { TargetAvailable, TargetChanged, OperationStarted, OperationResumed, PhaseChanged, TacticalChanged, VictorySecured, SimulationResolved, CaptureApplied, RewardsDelivered, CrewReturnSettled, Retired, OperationRetired }

/// <summary>Runtime identity only. A new session or native instance requires a new identity.</summary>
public sealed class BoardingHandle : IEquatable<BoardingHandle>
{
    public Guid SessionId { get; }
    public Guid Generation { get; }
    public BoardingHandle(Guid sessionId, Guid generation)
    {
        if (sessionId == Guid.Empty || generation == Guid.Empty) throw new ArgumentException("Nonempty boarding identities required.");
        SessionId = sessionId; Generation = generation;
    }
    public bool Equals(BoardingHandle? other) => other != null && SessionId == other.SessionId && Generation == other.Generation;
    public override bool Equals(object? obj) => Equals(obj as BoardingHandle);
    public override int GetHashCode() => HashCode.Combine(SessionId, Generation);
}

/// <summary>Only discovered compartment information. Index is scoped to its operation, not a persistent identity.</summary>
public sealed class BoardingCompartmentSnapshot
{
    public int Index { get; }
    public string Kind { get; }
    public string State { get; }
    public bool Locked { get; }
    public bool Destroyed { get; }
    public int FriendlyCrew { get; }
    public int HostileCrew { get; }
    public BoardingCompartmentSnapshot(int index, string kind, string state, bool locked, bool destroyed, int friendlyCrew, int hostileCrew)
    {
        if (index < 0 || friendlyCrew < 0 || hostileCrew < 0) throw new ArgumentOutOfRangeException(nameof(index));
        Kind = kind ?? throw new ArgumentNullException(nameof(kind)); State = state ?? throw new ArgumentNullException(nameof(state));
        Index = index; Locked = locked; Destroyed = destroyed; FriendlyCrew = friendlyCrew; HostileCrew = hostileCrew;
    }
}

/// <summary>Copied observed target state. Availability must be rechecked before a command.</summary>
public sealed class BoardingTargetSnapshot
{
    public BoardingHandle Handle { get; }
    public long Revision { get; }
    public BoardingEncounterKind Kind { get; }
    public string Name { get; }
    public string? FactionId { get; }
    public string? ShipId { get; }
    public BoardingAvailability Availability { get; }
    public BoardingHandle? Operation { get; }
    public BoardingTargetSnapshot(BoardingHandle handle, long revision, BoardingEncounterKind kind, string name,
        string? factionId, string? shipId, BoardingAvailability availability, BoardingHandle? operation)
    {
        Handle = handle ?? throw new ArgumentNullException(nameof(handle));
        if (revision < 1) throw new ArgumentOutOfRangeException(nameof(revision));
        if (!Enum.IsDefined(typeof(BoardingEncounterKind), kind) || !Enum.IsDefined(typeof(BoardingAvailability), availability)) throw new ArgumentException("Unknown boarding state.");
        if (operation != null && operation.SessionId != handle.SessionId) throw new ArgumentException("Operation belongs to another session.");
        Revision = revision; Kind = kind; Name = name ?? throw new ArgumentNullException(nameof(name));
        FactionId = factionId; ShipId = shipId; Availability = availability; Operation = operation;
    }
}

/// <summary>Immutable operation observation. Resolution does not imply rewards or crew were delivered.</summary>
public sealed class BoardingOperationSnapshot
{
    public BoardingHandle Handle { get; }
    public BoardingHandle Target { get; }
    public long Revision { get; }
    public BoardingPhase Phase { get; }
    public bool Autonomous { get; }
    public bool AutoMove { get; }
    public float? Integrity { get; }
    public float? MaximumIntegrity { get; }
    public string? Outcome { get; }
    public IReadOnlyDictionary<string, int> AssignedCrew { get; }
    public IReadOnlyList<BoardingCompartmentSnapshot> Compartments { get; }
    public int? ActivePods { get; }
    public BoardingOperationSnapshot(BoardingHandle handle, BoardingHandle target, long revision, BoardingPhase phase,
        bool autonomous, bool autoMove, float? integrity, float? maximumIntegrity, string? outcome,
        IEnumerable<KeyValuePair<string, int>> assignedCrew, IEnumerable<BoardingCompartmentSnapshot> compartments, int? activePods)
    {
        Handle = handle ?? throw new ArgumentNullException(nameof(handle)); Target = target ?? throw new ArgumentNullException(nameof(target));
        if (handle.SessionId != target.SessionId) throw new ArgumentException("Target belongs to another session.");
        if (revision < 1 || activePods < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        if (!Enum.IsDefined(typeof(BoardingPhase), phase)) throw new ArgumentOutOfRangeException(nameof(phase));
        CheckNumber(integrity); CheckNumber(maximumIntegrity);
        var crew = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in assignedCrew ?? throw new ArgumentNullException(nameof(assignedCrew)))
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value < 0) throw new ArgumentException("Invalid crew manifest.");
            crew.Add(pair.Key, pair.Value);
        }
        var rooms = (compartments ?? throw new ArgumentNullException(nameof(compartments))).ToArray();
        if (rooms.Any(room => room == null) || rooms.Select(room => room.Index).Distinct().Count() != rooms.Length) throw new ArgumentException("Invalid compartments.");
        AssignedCrew = new ReadOnlyDictionary<string, int>(crew); Compartments = Array.AsReadOnly(rooms);
        Revision = revision; Phase = phase; Autonomous = autonomous; AutoMove = autoMove;
        Integrity = integrity; MaximumIntegrity = maximumIntegrity; Outcome = outcome; ActivePods = activePods;
    }
    private static void CheckNumber(float? value)
    {
        if (value.HasValue && (float.IsNaN(value.Value) || float.IsInfinity(value.Value) || value.Value < 0)) throw new ArgumentOutOfRangeException(nameof(value));
    }
}

public enum BoardingDeliveryRoute { Inventory, Credits, WorldLoot, DataInventory }

/// <summary>One observed reward application, not proof the complete reward batch was delivered.</summary>
public sealed class BoardingDelivery
{
    public BoardingDeliveryRoute Route { get; }
    public int Quantity { get; }
    public BoardingDelivery(BoardingDeliveryRoute route, int quantity)
    {
        if (!Enum.IsDefined(typeof(BoardingDeliveryRoute), route) || quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        Route = route; Quantity = quantity;
    }
}

/// <summary>One observed fact with a monotonic session-local sequence, never an inferred successful command.</summary>
public sealed class BoardingEvent
{
    public long Sequence { get; }
    public BoardingEventKind Kind { get; }
    public BoardingTargetSnapshot Target { get; }
    public BoardingOperationSnapshot? Operation { get; }
    public BoardingDelivery? Delivery { get; }
    public BoardingEvent(long sequence, BoardingEventKind kind, BoardingTargetSnapshot target, BoardingOperationSnapshot? operation, BoardingDelivery? delivery = null)
    {
        if (sequence < 1) throw new ArgumentOutOfRangeException(nameof(sequence));
        if (!Enum.IsDefined(typeof(BoardingEventKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        if (operation != null && !operation.Target.Equals(target.Handle)) throw new ArgumentException("Mismatched operation target.");
        if ((kind == BoardingEventKind.RewardsDelivered) != (delivery != null)) throw new ArgumentException("Reward events require an observed delivery.");
        Sequence = sequence; Kind = kind; Operation = operation; Delivery = delivery;
    }
}

/// <summary>Main-thread-only observations. Changed does not replay. Snapshots remain immutable after invalidation.</summary>
public interface IDungeonOperationService : IServiceStatus
{
    Guid? SessionId { get; }
    bool IsDispatchingCallbacks { get; }
    IReadOnlyList<BoardingTargetSnapshot> GetTargets();
    IReadOnlyList<BoardingOperationSnapshot> GetOperations();
    BoardingTargetSnapshot? GetTarget(BoardingHandle handle);
    BoardingOperationSnapshot? GetOperation(BoardingHandle handle);
    event Action<BoardingEvent>? Changed;
}
