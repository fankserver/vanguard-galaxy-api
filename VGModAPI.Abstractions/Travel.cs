using System;

namespace VGModAPI;

public enum TravelTransitionKind { InitialPlacement, Requested, Departed, Arrived, Cancelled, RecoveredPlacement, RouteCompleted }
public enum TravelMode { Unknown, InSystem, JumpGate, Wormhole }

/// <summary>Opaque native location keys, not display names or owner-local content registration keys.</summary>
public sealed class TravelLocation
{
    public string SystemId { get; }
    public string? PoiId { get; }
    public string? SystemName { get; }
    public string? PoiName { get; }
    public TravelLocation(string systemId, string? poiId, string? systemName, string? poiName)
    {
        if (string.IsNullOrWhiteSpace(systemId)) throw new ArgumentException("System identity required.", nameof(systemId));
        if (poiId != null && string.IsNullOrWhiteSpace(poiId)) throw new ArgumentException("Use null for empty space.", nameof(poiId));
        SystemId = systemId; PoiId = poiId;
        SystemName = systemName; PoiName = poiName;
    }
}

/// <summary>Immutable observed fact. InitialPlacement is the first verified location, not necessarily session start.</summary>
public sealed class TravelTransition
{
    public Guid SessionId { get; }
    public Guid? OperationId { get; }
    public long Sequence { get; }
    public TravelTransitionKind Kind { get; }
    public TravelMode Mode { get; }
    public TravelLocation? Origin { get; }
    public TravelLocation? RequestedDestination { get; }
    public TravelLocation? ActualLocation { get; }
    public double GameSeconds { get; }
    public double? DwellSeconds { get; }
    public TravelTransition(Guid sessionId, Guid? operationId, long sequence, TravelTransitionKind kind, TravelMode mode,
        TravelLocation? origin, TravelLocation? requestedDestination, TravelLocation? actualLocation, double gameSeconds, double? dwellSeconds)
    {
        if (sessionId == Guid.Empty || operationId == Guid.Empty) throw new ArgumentException("Nonempty identities required.");
        if (sequence < 1) throw new ArgumentOutOfRangeException(nameof(sequence));
        if (!Enum.IsDefined(typeof(TravelTransitionKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!Enum.IsDefined(typeof(TravelMode), mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        var placement = kind is TravelTransitionKind.InitialPlacement or TravelTransitionKind.RecoveredPlacement;
        if (placement == operationId.HasValue) throw new ArgumentException("Placements have no operation; travel facts require one.", nameof(operationId));
        if ((placement || kind is TravelTransitionKind.Arrived or TravelTransitionKind.RouteCompleted) && actualLocation == null) throw new ArgumentNullException(nameof(actualLocation));
        if (kind == TravelTransitionKind.Requested && requestedDestination == null) throw new ArgumentNullException(nameof(requestedDestination));
        CheckTime(gameSeconds, nameof(gameSeconds));
        if (dwellSeconds.HasValue) CheckTime(dwellSeconds.Value, nameof(dwellSeconds));
        SessionId = sessionId; OperationId = operationId; Sequence = sequence; Kind = kind; Mode = mode;
        Origin = origin; RequestedDestination = requestedDestination; ActualLocation = actualLocation;
        GameSeconds = gameSeconds; DwellSeconds = dwellSeconds;
    }
    private static void CheckTime(double value, string name)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) throw new ArgumentOutOfRangeException(name);
    }
}

/// <summary>Main-thread-only observations. Registration does not replay; consumers own optional history.</summary>
public interface ITravelEvents
{
    Guid? SessionId { get; }
    TravelLocation? CurrentLocation { get; }
    bool IsDispatchingCallbacks { get; }
    IDisposable Subscribe(string owner, Action<TravelTransition> callback);
}

/// <summary>Station-lifetime facts are deliberately distinct from travel legs and from the
/// physical dock state: an async dock/interior request is not readiness, and interior
/// readiness (attributed nonthrowing Awake + Start) is not physical dock completion.</summary>
public enum StationTransitionKind { DockedPhysical, Undocking, Leaving, InteriorReady, InteriorDestroyed }

/// <summary>Immutable observed station-lifetime fact. The station location is opaque and may be
/// null while the ship state is transitional (for example early undocking). Initial load placement
/// is represented by travel placement facts; this surface records real dock/interior state changes.</summary>
public sealed class StationTransition
{
    public Guid SessionId { get; }
    public long Sequence { get; }
    public StationTransitionKind Kind { get; }
    public TravelLocation? Station { get; }
    public double GameSeconds { get; }
    public StationTransition(Guid sessionId, long sequence, StationTransitionKind kind, TravelLocation? station, double gameSeconds)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("Nonempty session identity required.", nameof(sessionId));
        if (sequence < 1) throw new ArgumentOutOfRangeException(nameof(sequence));
        if (!Enum.IsDefined(typeof(StationTransitionKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (double.IsNaN(gameSeconds) || double.IsInfinity(gameSeconds) || gameSeconds < 0) throw new ArgumentOutOfRangeException(nameof(gameSeconds));
        SessionId = sessionId; Sequence = sequence; Kind = kind; Station = station; GameSeconds = gameSeconds;
    }
}

/// <summary>Main-thread-only station-lifetime observations. DockedPhysical reflects an observed
/// physical dock state, not the onDocked request that fires before docking completes.</summary>
public interface IStationEvents
{
    Guid? SessionId { get; }
    bool IsDispatchingCallbacks { get; }
    IDisposable Subscribe(string owner, Action<StationTransition> callback);
}
