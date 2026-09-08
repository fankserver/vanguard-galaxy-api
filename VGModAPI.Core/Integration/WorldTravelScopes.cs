using System;
using System.IO;

namespace VGModAPI.Core.Integration;

/// <summary>Route/leg provenance protocol. Callers must independently validate native state and world readiness.</summary>
internal sealed class WorldTravelScopes
{
    internal sealed class Route
    {
        internal readonly Guid Session;
        internal readonly object Player, Manager, Destination;
        internal Leg? Current;
        internal Route(Guid session, object player, object manager, object destination)
        { Session = session; Player = player; Manager = manager; Destination = destination; }
    }
    internal sealed class Leg
    {
        internal readonly Route Route;
        internal readonly object Target;
        internal bool HandedOff, Completed;
        internal Leg(Route route, object target) { Route = route; Target = target; }
    }
    internal sealed class Handoff
    {
        internal readonly Leg Predecessor;
        internal readonly object Target;
        internal bool Consumed;
        internal Handoff(Leg predecessor, object target) { Predecessor = predecessor; Target = target; }
    }
    private Route? _current;
    internal Route Begin(Guid session, object player, object manager, object destination)
    {
        if (session == Guid.Empty || player == null || manager == null || destination == null)
            throw new ArgumentException("Observed route identity required.");
        return _current = new Route(session, player, manager, destination);
    }
    internal Leg First(Route route, object target)
    {
        RequireRoute(route);
        if (target == null || route.Current != null) throw new InvalidDataException("Initial leg already assigned or missing target.");
        return route.Current = new Leg(route, target);
    }
    internal void RequireActive(Leg leg, Guid session, object player, object manager)
    {
        RequireLeg(leg);
        if (leg.Route.Session != session || !ReferenceEquals(leg.Route.Player, player) || !ReferenceEquals(leg.Route.Manager, manager))
            throw new InvalidDataException("Travel continuation belongs to another session, player or manager.");
    }
    internal Handoff PrepareHandoff(Leg leg, object nextWaypoint)
    {
        RequireLeg(leg);
        if (nextWaypoint == null) throw new ArgumentNullException(nameof(nextWaypoint));
        return new Handoff(leg, nextWaypoint);
    }
    internal Leg AcceptHandoff(Handoff handoff, object observedNextWaypoint)
    {
        RequireLeg(handoff.Predecessor);
        if (handoff.Consumed || !ReferenceEquals(handoff.Target, observedNextWaypoint))
            throw new InvalidDataException("Waypoint handoff is stale or was replaced.");
        var successor = new Leg(handoff.Predecessor.Route, handoff.Target);
        handoff.Consumed = true; handoff.Predecessor.HandedOff = true;
        handoff.Predecessor.Route.Current = successor;
        return successor;
    }
    // This only retires bookkeeping. It does not authorize the predecessor's native UI/travel-field tail.
    internal void RetirePredecessor(Leg leg)
    {
        RequireRoute(leg.Route);
        if (!leg.HandedOff || leg.Completed) throw new InvalidDataException("No pending predecessor completion.");
        leg.Completed = true;
    }
    internal void Complete(Leg leg)
    {
        RequireLeg(leg); leg.Completed = true;
    }
    internal bool Cancel(Route route)
    {
        if (!ReferenceEquals(_current, route)) return false;
        _current = null; return true;
    }
    private void RequireRoute(Route route)
    {
        if (route == null || !ReferenceEquals(_current, route)) throw new InvalidDataException("Stale travel route.");
    }
    private void RequireLeg(Leg leg)
    {
        if (leg == null) throw new ArgumentNullException(nameof(leg));
        RequireRoute(leg.Route);
        if (leg.HandedOff || leg.Completed || !ReferenceEquals(leg.Route.Current, leg)) throw new InvalidDataException("Stale travel leg.");
    }
}
