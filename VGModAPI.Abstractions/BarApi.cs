using System;

namespace VGModAPI;

public enum BarStatus
{
    Succeeded, Unavailable, UnknownPlugin, CallerMismatch, AlreadyAcquired, ProviderConflict,
    LimitExceeded, DuplicateLocalId, InvalidDefinition, StaleSession, NotRegistered, PermissionDenied
}

public sealed class BarResult
{
    public BarStatus Status { get; }
    public string Detail { get; }
    public bool Succeeded => Status == BarStatus.Succeeded;
    public BarResult(BarStatus status, string detail = "") { Status = status; Detail = detail; }
}

public sealed class BarProviderResult
{
    public BarStatus Status { get; }
    public IBarProvider? Provider { get; }
    public string Detail { get; }
    public BarProviderResult(BarStatus status, IBarProvider? provider, string detail = "")
    { Status = status; Provider = provider; Detail = detail; }
}

/// <summary>Main-thread-only ownership. Acquire directly from the loaded plugin's assembly.</summary>
public interface IBarService : IServiceStatus
{
    BarProviderResult AcquireProvider(object pluginInstance);
    /// <summary>Main-thread-only finalized-roster observation. No replay; remove the handler on consumer teardown.</summary>
    event Action<BarRosterFinalized>? RosterFinalized;
}

/// <summary>Definitions and policies are registrations; persistent placement is a session mutation.</summary>
public interface IBarProvider : IDisposable
{
    string ProviderId { get; }
    BarResult Register(BarPatronDefinition definition, Action<BarInteraction>? interact = null);
    /// <summary>Remove runtime definition/behavior; persistent patron state remains until explicitly removed.</summary>
    BarResult Unregister(string localId);
    BarResult ConfigureStation(string stationId, BarRosterOwnership ownership);
    BarResult Place(Guid expectedSessionId, string localId);
    BarResult Remove(Guid expectedSessionId, string localId);
}
