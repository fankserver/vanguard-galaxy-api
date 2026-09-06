using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

// Real native travel observer. Every observed fact must be attributed to an immutable
// session/player/leg token captured when the hop began; stale iterators, disposed
// coroutines, replaced sessions and nested base/override arrivals fail closed and cannot
// touch a replacement. Method/proc returns and nested-child completion are never readiness,
// departure, or completion; a verified destination-readiness / physical state boundary is.
internal sealed class TravelNativeAdapter : IDisposable
{
    // Source.SpaceShip.Auto.DockingState numeric values (matching the inspected enum order).
    private const int DockingStateDocked = 3;
    private const int DockingStateLeaving = 5;

    private sealed class JumpContext
    {
        internal readonly Guid Session;
        internal readonly object Player;
        internal readonly object? TravelManager;
        internal readonly TravelMode Mode;
        internal TravelLegTracker.Leg? Leg;
        internal JumpContext(Guid session, object player, object? travelManager, TravelMode mode)
        { Session = session; Player = player; TravelManager = travelManager; Mode = mode; }
    }
    private struct CompletedRoute
    {
        internal Guid Session;
        internal Guid LegId;
        internal TravelLegTracker.Place? Actual;
    }
    private readonly int _thread = Thread.CurrentThread.ManagedThreadId;
    internal readonly TravelEvents Events;
    internal readonly StationEvents Station;
    private readonly TravelNativeBindings _bindings;
    private readonly Action<string, Exception> _report;
    private readonly TravelLegTracker _tracker = new();
    private readonly TravelArrivalScopes _scopes = new();
    private readonly Dictionary<Guid, TravelMode> _modeByLeg = new();
    private readonly Dictionary<Guid, LegMeta> _legMeta = new();
    private readonly Dictionary<(string System, string? Poi), TravelLocation> _locations = new();
    private readonly HashSet<Guid> _routeCompleted = new();
    private Guid? _sessionValue;
    private Guid _session = Guid.Empty;
    private object? _boundPlayer;
    private TravelLegTracker.Leg? _leg;
    private TravelMode _pendingMode = TravelMode.Unknown;
    private CompletedRoute? _lastCompleted;
    private (object Instance, Guid Session, object Player, object? Station)? _interiorLease;
    private bool _stationTransitionSeen;
    private volatile bool _faulted;
    private Exception? _faultDetail;
    private bool _disposed;

    internal TravelNativeAdapter(TravelNativeBindings bindings, Action<string, Exception> report)
    {
        _bindings = bindings; _report = report;
        Events = new TravelEvents(report);
        Station = new StationEvents(report);
    }

    private struct LegMeta
    {
        internal TravelLegTracker.Place? Origin;
        internal TravelLegTracker.Place Requested;
        internal LegMeta(TravelLegTracker.Place? origin, TravelLegTracker.Place requested) { Origin = origin; Requested = requested; }
    }

    internal Guid? CurrentSession => _sessionValue;
    internal bool IsFaulted => _faulted;
    internal Exception? FaultDetail => _faultDetail;
    internal TravelNativeBindings Bindings => _bindings;

    internal void Guard(Action action)
    {
        if (_faulted || _disposed) return;
        try
        {
            if (Thread.CurrentThread.ManagedThreadId != _thread)
                throw new InvalidOperationException("Travel adapter requires the Unity main thread.");
        }
        catch (Exception ex) { Fault(ex); return; }
        try { action(); }
        catch (Exception ex)
        {
            try { _report("travel", ex); } catch { }
        }
    }

    internal void Fault(Exception ex)
    {
        if (_faulted) return;
        _faulted = true; _faultDetail = ex;
        try { _report("travel-fatal", ex); } catch { }
    }

    internal void SetSession(Guid? session)
    {
        Guard(() =>
        {
            if (_sessionValue == session)
            {
                // Same session id: bind the FIRST non-null player, but never adopt a replacement
                // after a binding is already held for this session id.
                if (session != null && _boundPlayer == null) _boundPlayer = _bindings.Player;
                return;
            }
            if (session == null) _boundPlayer = null;
            else _boundPlayer = _bindings.Player; // bind at session ready; may be null -> retried above
            _sessionValue = session;
            _session = session ?? Guid.Empty;
            _tracker.Reset(session);
            _scopes.Reset();
            _modeByLeg.Clear(); _legMeta.Clear(); _locations.Clear();
            _routeCompleted.Clear();
            _leg = null; _pendingMode = TravelMode.Unknown;
            _lastCompleted = null; _interiorLease = null; _stationTransitionSeen = false;
            Events.SetSession(session);
            Station.SetSession(session);
        });
    }

    internal void Invalidate(string reason) => SetSession(null);

    // --- placement / dwell ----------------------------------------------------

    internal void ObservePlacement(object player, object? localManager)
    {
        if (!IsLive(player)) return;
        Guard(() =>
        {
            var actual = _bindings.CurrentLocation(player);
            if (actual == null || localManager == null) return;
            var poi = _bindings.Poi(localManager);
            var playerPoi = _bindings.CurrentPoi(player);
            // The manager must be the one for the player's current POI. Both-null is the
            // attributable initialized empty-space manager; in-transit is excluded by the
            // reducer's no-pending-leg rule and the readiness gate.
            if (!ReferenceEquals(poi, playerPoi)) return;
            if (!_bindings.Ready(localManager, poi)) return;
            Cache(actual);
            var place = PlaceOf(actual);
            if (place == null) return;
            _tracker.ObservePlacement(_session, place, _bindings.Time(player), true);
            Drain(_bindings.Time(player));
        });
    }

    // --- requested / cancelled ------------------------------------------------

    // A route is a sequence of per-hop legs. Each hop is requested with its REAL waypoint:
    // the first hop (SetRouteToPOI) and every subsequent in-system hop (TravelToNextWaypoint)
    // request waypoints[0] when it is an actual in-system target, never the final targetPoi.
    // A waypoint in another system is a gate/wormhole handoff; the jump iterator owns that leg.
    internal void RequestWaypointLeg()
    {
        Guard(() =>
        {
            var player = _boundPlayer;
            if (!IsLive(player) || _leg != null) return;
            var waypoint = _bindings.Waypoint0(player);
            if (waypoint == null) return;
            if (!_bindings.InCurrentSystem(waypoint, player)) return; // jump handoff -> WrapJump owns it
            var location = _bindings.Destination(waypoint);
            if (location == null) return;
            Cache(location);
            var place = PlaceOf(location);
            if (place == null) return;
            _pendingMode = TravelMode.InSystem;
            _leg = _tracker.Request(_session, place);
            if (_leg != null) { _modeByLeg[_leg.Id] = TravelMode.InSystem; _legMeta[_leg.Id] = new LegMeta(_leg.Origin, place); }
            Drain(_bindings.Time(player));
        });
    }

    internal void OnTravelCancelled()
    {
        Guard(() =>
        {
            if (_leg == null) return;
            _tracker.Cancel(_leg);
            _leg = null; _pendingMode = TravelMode.Unknown;
            var player = _boundPlayer;
            if (player != null && IsLive(player)) Drain(_bindings.Time(player));
        });
    }

    internal void OnDeparture(object player, bool verifiedOriginChanged)
    {
        Guard(() =>
        {
            if (!verifiedOriginChanged || !IsLive(player) || _leg == null) return;
            if (_tracker.DepartAllowed(_leg))
            {
                _tracker.Depart(_leg, _bindings.Time(player));
                Drain(_bindings.Time(player));
            }
        });
    }

    // --- in-system arrival ----------------------------------------------------

    internal object? OnArrivalEnter(object manager)
    {
        if (manager == null) return null;
        try { return _scopes.Begin(manager); }
        catch { return null; }
    }

    internal void OnArrivalExit(object? token, object manager, Exception? error)
    {
        Guard(() =>
        {
            if (token == null || manager == null) return;
            var scope = (TravelArrivalScopes.Scope)token;
            if (!_scopes.End(scope, error) || error != null) return;
            var player = _boundPlayer;
            if (!IsLive(player) || _leg == null) return;
            var poi = _bindings.Poi(manager);
            var playerPoi = _bindings.CurrentPoi(player);
            if (poi == null || playerPoi == null || !ReferenceEquals(poi, playerPoi)) return;
            if (!_bindings.Ready(manager, poi)) return;
            var location = _bindings.Destination(poi);
            if (location == null) return;
            Cache(location);
            var place = PlaceOf(location);
            if (place == null) return;
            if (!_tracker.OwnsPending(_leg)) return;
            var arrived = _leg;
            _pendingMode = TravelMode.InSystem;
            _modeByLeg[arrived.Id] = _pendingMode;
            var valid = _tracker.Arrive(arrived, place, _bindings.Time(player), true);
            if (valid)
            {
                _leg = null; _pendingMode = TravelMode.Unknown;
                _lastCompleted = new CompletedRoute { Session = _session, LegId = arrived.Id, Actual = place };
            }
            Drain(_bindings.Time(player));
        });
    }

    // --- cross-system jumps ---------------------------------------------------

    // jumpGatePoi (or fromWormhole) is injected via Harmony; the requested destination is
    // built from the gate's RAW targetSystemGuid/targetPoiGuid (no world/name lookup), so a
    // nominal target absent from the current (tutorial/sandbox) map is still a valid request.
    internal IEnumerator WrapJump(IEnumerator? inner, TravelMode mode, object? travelManager, object? player, object? jumpGatePoi)
    {
        if (inner == null) return null!; // factories always yield an iterator; defensive only
        if (player == null) return inner;
        JumpContext? context = null;
        Guard(() =>
        {
            if (!IsLive(player)) return;
            context = new JumpContext(_session, player, travelManager, mode);
            var requested = BuildJumpRequest(player, mode, jumpGatePoi);
            if (requested == null) return; // cannot identify a nominal target; never invent a leg
            if (_leg != null && _tracker.OwnsPending(_leg) && !_tracker.Departed(_leg)
                && SamePlace(_bindings.CurrentLocation(player), _tracker.Current))
            {
                // Direct gate/wormhole handoff: reuse the pending same-origin in-system leg as this
                // jump's leg instead of manufacturing a fictitious Cancelled/Requested pair.
                _tracker.Upgrade(_leg, requested);
                _modeByLeg[_leg.Id] = mode;
                _legMeta[_leg.Id] = new LegMeta(_leg.Origin, requested);
                context.Leg = _leg;
            }
            else if (_leg == null)
            {
                _pendingMode = mode;
                context.Leg = _tracker.Request(context.Session, requested);
                _leg = context.Leg;
                if (context.Leg != null) { _modeByLeg[context.Leg.Id] = mode; _legMeta[context.Leg.Id] = new LegMeta(context.Leg.Origin, requested); }
            }
        });
        if (context == null) return inner;
        return new TravelJumpObserver(inner, () => ObserveJumpStep(context), () => OnJumpTerminated(context));
    }

    private void ObserveJumpStep(JumpContext context)
    {
        Guard(() =>
        {
            // Stale iterator: a replacement session/player/leg must never be advanced by an
            // old iterator, even for the same player object after SetSession.
            if (_session != context.Session || !IsLive(context.Player)) return;
            var player = context.Player;
            if (context.Leg == null || !_tracker.OwnsPending(context.Leg)) return;
            var current = _bindings.CurrentLocation(player);
            if (!_tracker.Departed(context.Leg) && current != null && !SamePlace(current, _tracker.Current))
            {
                _tracker.Depart(context.Leg, _bindings.Time(player));
                Drain(_bindings.Time(player));
            }
            var manager = context.TravelManager == null ? null : _bindings.LocalManager(context.TravelManager);
            if (manager == null) return;
            var poi = _bindings.Poi(manager);
            var playerPoi = _bindings.CurrentPoi(player);
            if (poi == null || playerPoi == null || !ReferenceEquals(poi, playerPoi)) return;
            if (!_bindings.Ready(manager, poi)) return;
            if (current == null) return;
            Cache(current);
            var place2 = PlaceOf(current);
            if (place2 == null) return;
            if (!_tracker.OwnsPending(context.Leg)) return;
            var arrived = context.Leg;
            var valid = _tracker.Arrive(arrived, place2, _bindings.Time(player), true);
            if (valid)
            {
                if (ReferenceEquals(_leg, arrived)) _leg = null;
                _pendingMode = TravelMode.Unknown;
                _lastCompleted = new CompletedRoute { Session = context.Session, LegId = arrived.Id, Actual = place2 };
            }
            Drain(_bindings.Time(player));
        });
    }

    private void OnJumpTerminated(JumpContext context)
    {
        // Root jump iterator terminated (completed or replaced) without arrival: the hop's leg
        // must not leak. Nested-child completion never reaches here (children carry no callback).
        Guard(() =>
        {
            if (_session != context.Session || context.Leg == null) return;
            if (_tracker.OwnsPending(context.Leg))
            {
                _tracker.Cancel(context.Leg);
                if (ReferenceEquals(_leg, context.Leg)) _leg = null;
                Drain(_bindings.Time(context.Player));
            }
        });
    }

    private TravelLegTracker.Place? BuildJumpRequest(object player, TravelMode mode, object? jumpGatePoi)
    {
        if (mode == TravelMode.Wormhole)
        {
            var waypoint = _bindings.Waypoint0(player);
            if (waypoint == null) return null;
            var location = _bindings.Destination(waypoint);
            return location == null ? null : PlaceOf(location);
        }
        var systemGuid = _bindings.JumpSystemGuid(jumpGatePoi);
        if (string.IsNullOrEmpty(systemGuid)) return null;
        var poiGuid = _bindings.JumpPoiGuid(jumpGatePoi);
        try { return new TravelLegTracker.Place(systemGuid, string.IsNullOrEmpty(poiGuid) ? null : poiGuid); }
        catch { return null; }
    }

    // --- verified final-route boundary ----------------------------------------

    internal void CheckRouteBoundary(object travelManager)
    {
        Guard(() =>
        {
            if (_lastCompleted == null) return;
            var completed = _lastCompleted.Value;
            // Abort a route recorded under a different session/player.
            if (completed.Session != _session || !IsLive(_boundPlayer)) { _lastCompleted = null; return; }
            if (_routeCompleted.Contains(completed.LegId)) { _lastCompleted = null; return; }
            var player = _boundPlayer;
            if (player == null || travelManager == null) return;
            if (_bindings.WaypointCount(player) == 0 && !_bindings.TravelActive(travelManager))
            {
                var actual = LocForPlace(completed.Actual);
                if (actual != null)
                {
                    var mode = _modeByLeg.TryGetValue(completed.LegId, out var m) ? m : TravelMode.Unknown;
                    Events.Emit(completed.Session, completed.LegId, TravelTransitionKind.RouteCompleted, mode,
                        null, null, actual, _bindings.Time(player));
                    _routeCompleted.Add(completed.LegId);
                }
            }
            // The just-arrived leg's route is not final (next leg running): drop the slot so a
            // later arrival becomes the boundary candidate.
            _lastCompleted = null;
        });
    }

    private static bool SamePlace(TravelLocation? a, TravelLegTracker.Place? b)
        => a != null && b != null && a.SystemId == b.SystemId && a.PoiId == b.PoiId;

    // --- station facts: native dock/undock boundaries -------------------------

    // The ship object is captured before any dock reset, so attribution survives
    // ResetDockingOption() nulling dockingOption.dockingSpaceship (finding 3/5).
    internal void OnDockedPhysical(object? ship)
    {
        Guard(() =>
        {
            var player = _boundPlayer;
            if (!IsLive(player) || ship == null || !_bindings.IsPlayerShip(ship, player)) return;
            if (_bindings.ShipDockingState(ship) != DockingStateDocked) return; // physical state required
            // Initial-load DockQuick (InitializePoi) emits Docked before the reducer has a
            // placement and with no prior station transition: that is initial state, not a
            // transition. Suppress it (finding 6). A genuine transition is preceded by a
            // placement or a prior dock/undock boundary.
            if (!_stationTransitionSeen && _tracker.Current == null) return;
            var station = _bindings.CurrentLocation(player);
            if (station == null) return;
            Cache(station);
            Station.Emit(_session, StationTransitionKind.DockedPhysical, station, _bindings.Time(player));
            _stationTransitionSeen = true;
        });
    }
    internal void OnUndocking(object? ship)
    {
        Guard(() =>
        {
            var player = _boundPlayer;
            if (!IsLive(player) || ship == null || !_bindings.IsPlayerShip(ship, player)) return;
            var station = _bindings.CurrentLocation(player) ?? LocForPlace(_tracker.Current);
            Station.Emit(_session, StationTransitionKind.Undocking, station, _bindings.Time(player));
            _stationTransitionSeen = true;
        });
    }
    internal void OnLeaving(object? ship)
    {
        Guard(() =>
        {
            var player = _boundPlayer;
            if (!IsLive(player) || ship == null || !_bindings.IsPlayerShip(ship, player)) return;
            if (_bindings.ShipDockingState(ship) != DockingStateLeaving) return; // verified leaving
            var station = _bindings.CurrentLocation(player) ?? LocForPlace(_tracker.Current);
            Station.Emit(_session, StationTransitionKind.Leaving, station, _bindings.Time(player));
            _stationTransitionSeen = true;
        });
    }

    // --- interior lifetime lease (attributed, session/player/instance scoped) --

    internal void OnInteriorAwake(object instance, object player, Exception? error)
    {
        Guard(() =>
        {
            if (error != null) { if (_interiorLease?.Instance == instance) _interiorLease = null; return; }
            if (!IsLive(player) || instance == null) return;
            if (!ReferenceEquals(_bindings.InteriorInstance(), instance)) return;
            _interiorLease = (instance, _session, _boundPlayer!, _bindings.InteriorStation(instance));
        });
    }
    internal void OnInteriorStart(object instance, object player, Exception? error)
    {
        Guard(() =>
        {
            if (error != null) { if (_interiorLease?.Instance == instance) _interiorLease = null; return; }
            if (!_interiorLease.HasValue || !ReferenceEquals(_interiorLease.Value.Instance, instance)) return;
            var lease = _interiorLease.Value;
            if (lease.Session != _session || !ReferenceEquals(lease.Player, _boundPlayer)) { _interiorLease = null; return; }
            if (!ReferenceEquals(_bindings.InteriorInstance(), instance) || lease.Station == null) return;
            var location = _bindings.Destination(lease.Station);
            if (location == null) return;
            Cache(location);
            Station.Emit(lease.Session, StationTransitionKind.InteriorReady, location, _bindings.Time(lease.Player));
        });
    }
    internal void OnInteriorDestroyed(object instance, object player)
    {
        Guard(() =>
        {
            if (instance == null || !ReferenceEquals(_bindings.InteriorInstance(), instance)) return;
            if (_interiorLease.HasValue && ReferenceEquals(_interiorLease.Value.Instance, instance))
            {
                var lease = _interiorLease.Value;
                if (lease.Session == _session && ReferenceEquals(lease.Player, _boundPlayer) && lease.Station != null)
                {
                    var location = _bindings.Destination(lease.Station);
                    Station.Emit(lease.Session, StationTransitionKind.InteriorDestroyed, location, _bindings.Time(lease.Player));
                }
                _interiorLease = null;
            }
        });
    }

    internal void Tick(object? player, object? manager)
    {
        if (player != null && IsLive(player)) ObservePlacement(player, manager);
        // Dock/undock are observed only through native DockingOption boundaries (not polling),
        // so same-frame transitions cannot be missed and initial loaded-docked state is never
        // misreported as a transition.
    }

    private void Drain(double now)
    {
        var facts = _tracker.Drain();
        if (facts.Length == 0) return;
        var player = _boundPlayer;
        if (player == null) return;
        var batchSession = _session;
        foreach (var fact in facts)
        {
            if (batchSession != _session) break;
            if (batchSession == Guid.Empty) break;
            TravelMode mode = TravelMode.Unknown;
            if (fact.Operation.HasValue && _modeByLeg.TryGetValue(fact.Operation.Value, out var m)) mode = m;
            TravelLocation? origin = null, requested = null, actual = null;
            switch (fact.Transition)
            {
                case TravelLegTracker.Kind.InitialPlacement:
                case TravelLegTracker.Kind.RecoveredPlacement:
                    actual = LocForPlace(fact.Location); break;
                case TravelLegTracker.Kind.Requested:
                    requested = LocForPlace(fact.Location); break;
                case TravelLegTracker.Kind.Departed:
                    origin = LocForPlace(fact.Location); break;
                case TravelLegTracker.Kind.Arrived:
                    actual = LocForPlace(fact.Location);
                    if (fact.Operation.HasValue && _legMeta.TryGetValue(fact.Operation.Value, out var meta))
                    { origin = LocForPlace(meta.Origin); requested = LocForPlace(meta.Requested); }
                    break;
                case TravelLegTracker.Kind.Cancelled:
                    actual = LocForPlace(fact.Location); break;
            }
            Events.Emit(batchSession, fact.Operation, ToKind(fact.Transition), mode, origin, requested, actual, now, fact.DwellSeconds);
        }
    }

    private static TravelTransitionKind ToKind(TravelLegTracker.Kind kind) => kind switch
    {
        TravelLegTracker.Kind.InitialPlacement => TravelTransitionKind.InitialPlacement,
        TravelLegTracker.Kind.Requested => TravelTransitionKind.Requested,
        TravelLegTracker.Kind.Departed => TravelTransitionKind.Departed,
        TravelLegTracker.Kind.Arrived => TravelTransitionKind.Arrived,
        TravelLegTracker.Kind.Cancelled => TravelTransitionKind.Cancelled,
        TravelLegTracker.Kind.RecoveredPlacement => TravelTransitionKind.RecoveredPlacement,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private bool IsLive([NotNullWhen(true)] object? player)
        => _session != Guid.Empty && player != null && ReferenceEquals(player, _boundPlayer)
            && ReferenceEquals(player, _bindings.Player);

    private void Cache(TravelLocation location)
    {
        if (location == null) return;
        _locations[(location.SystemId, location.PoiId)] = location;
    }

    private TravelLocation? LocForPlace(TravelLegTracker.Place? place)
    {
        if (place == null) return null;
        if (_locations.TryGetValue((place.SystemId, place.PoiId), out var cached)) return cached;
        return new TravelLocation(place.SystemId, place.PoiId, null, null);
    }

    private static TravelLegTracker.Place? PlaceOf(TravelLocation? location)
    {
        if (location == null) return null;
        return new TravelLegTracker.Place(location.SystemId, location.PoiId);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Events.Dispose();
        Station.Dispose();
    }
}
