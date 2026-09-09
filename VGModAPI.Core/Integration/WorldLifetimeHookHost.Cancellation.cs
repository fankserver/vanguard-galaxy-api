using System.IO;

namespace VGModAPI.Core.Integration;

internal interface IWorldTravelCancellationHost
{
    object BeginCancellation(object manager);
    void EndCancellation(object token, bool invalidated);
}

internal sealed partial class WorldLifetimeHookHost : IWorldTravelCancellationHost
{
    private sealed class Cancellation
    {
        internal readonly WorldTravelScopes.Leg? Leg;
        internal Cancellation(WorldTravelScopes.Leg? leg) => Leg = leg;
    }
    private Cancellation? _cancellation;
    private void RequireNoCancellation()
    {
        if (_cancellation != null) throw new InvalidDataException("Reentrant travel work during native cancellation is refused.");
    }
    public object BeginCancellation(object manager)
    {
        _hub.CheckThread(); RequireNoCancellation();
        WorldTravelScopes.Leg? leg = null;
        if (Travel.PendingRequest is { } incoming) VerifyRouteNative(incoming, manager);
        else
        {
            leg = Travel.CaptureExecuting() ?? Travel.CurrentLeg;
            if (leg != null) VerifyRouteNative(leg.Route, manager);
        }
        // An incoming request may already have revoked the old route before native CancelTravel runs.
        // Its not-yet-created leg must not be cancelled by that predecessor cleanup.
        return _cancellation = new Cancellation(leg);
    }
    public void EndCancellation(object token, bool invalidated)
    {
        _hub.CheckThread();
        if (token is not Cancellation cancellation || !ReferenceEquals(_cancellation, cancellation))
            throw new InvalidDataException("Unknown native cancellation completion.");
        try
        {
            if (invalidated && cancellation.Leg != null && Travel.IsCurrent(cancellation.Leg.Route) && ReferenceEquals(Travel.CurrentLeg, cancellation.Leg))
                Travel.Cancel(cancellation.Leg.Route);
        }
        finally { _cancellation = null; }
    }
}
