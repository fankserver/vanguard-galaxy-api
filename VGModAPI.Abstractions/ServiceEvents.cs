using System;

namespace VGModAPI;

/// <summary>
/// Main-thread session/save observations with independently reported binding health.
/// Events do not replay. A null CurrentSession means no tracked attempt, not menu readiness.
/// </summary>
public interface ILifecycleService
{
    IServiceStatus SessionTracking { get; }
    IServiceStatus SaveOutcomes { get; }
    SessionSnapshot? CurrentSession { get; }
    event Action<LifecycleEvent>? Changed;
}

/// <summary>Main-thread witnessed transitions, not a replayable mission history or a mission factory.</summary>
public interface IMissionService : IServiceStatus, IVersionSensitiveMissionAccess
{
    /// <summary>Independent saved-identity support; an observed transition can have only session-local identity.</summary>
    IServiceStatus IdentityContinuity { get; }
    event Action<MissionTransition>? Transitioned;
}

/// <summary>Main-thread travel observations. Missing current placement is domain absence, not service failure.</summary>
public interface ITravelService : IServiceStatus
{
    Guid? SessionId { get; }
    TravelLocation? CurrentLocation { get; }
    event Action<TravelTransition>? Transitioned;
}

/// <summary>Main-thread station facts; physical docking, interior readiness and service health are distinct.</summary>
public interface IStationService : IServiceStatus
{
    Guid? SessionId { get; }
    event Action<StationTransition>? Transitioned;
}
