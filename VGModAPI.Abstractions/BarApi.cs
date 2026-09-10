using System;

namespace VGModAPI;

public enum BarStatus
{
    Succeeded, Unavailable, UnknownPlugin, CallerMismatch, AlreadyAcquired, ProviderConflict,
    LimitExceeded, InvalidDefinition, GameEnded, NotRegistered, PermissionDenied, Queued, NotRequested
}

public sealed class BarResult
{
    private BarStatus _status;
    internal Func<BarStatus?>? Ended;
    public BarStatus Status => _status == BarStatus.Queued ? Ended?.Invoke() ?? _status : _status;
    public string Detail { get; private set; }
    public bool Succeeded => Status == BarStatus.Succeeded;
    public BarResult(BarStatus status, string detail = "") { _status = status; Detail = detail; }
    internal void Finish(BarStatus status, string detail) { _status = status; Detail = detail; }
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
    BarProviderResult AcquireProvider(object pluginInstance, ISaveDataRegistration? saveData = null);
    /// <summary>Main-thread-only finalized-roster observation. No replay; remove the handler on consumer teardown.</summary>
    event Action<BarRosterFinalized>? RosterFinalized;
}

/// <summary>Declare contacts once. Re-declaring a local ID replaces this provider's definition without stacking placements.</summary>
public interface IBarProvider : IDisposable
{
    string ProviderId { get; }
    BarRegistrationResult Register(BarPatronDefinition definition, Action<IBarPatron>? interact = null);
    BarResult ConfigureStation(string stationId, BarRosterOwnership ownership);
}
