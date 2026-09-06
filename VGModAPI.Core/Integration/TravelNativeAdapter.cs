using System;
using System.Collections;
using System.Collections.Generic;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

// Real native travel observer: reduces empirically observed facts into verifiable,
// consumer-facing transitions. Iterator creation or a coroutine factory returning is
// never completion; only observed readiness/departure after forwarding Unity yields is
// evidence. Faults fail closed and disable the travel group rather than breaking the game.
internal sealed class TravelNativeAdapter : IDisposable
{
    internal readonly TravelEvents Events;
    internal readonly StationEvents Station;
    private readonly TravelNativeBindings _bindings;
    private readonly Action<string, Exception> _report;
    private readonly TravelLegTracker _tracker = new();
    private readonly TravelArrivalScopes _scopes = new();
    private readonly Dictionary<Guid, TravelMode> _modeByLeg = new();
    private readonly Dictionary<(string System, string? Poi), TravelLocation> _locations = new();
    private Guid? _sessionValue;
    private Guid _session = Guid.Empty;
    private TravelLegTracker.Leg? _leg;
    private TravelMode _pendingMode = TravelMode.Unknown;
    private bool _dockingWatched;
    private int? _previousDocking;
    private volatile bool _faulted;
    private bool _disposed;

    internal TravelNativeAdapter(TravelNativeBindings bindings, Action<string, Exception> report)
    {
        _bindings = bindings; _report = report;
        Events = new TravelEvents(report);
        Station = new StationEvents(report);
    }

    internal Guid? CurrentSession => _sessionValue;
    internal TravelNativeBindings Bindings => _bindings;

    internal void Guard(Action action)
    {
        if (_faulted || _disposed) return;
        try { action(); }
        catch (Exception ex)
        {
            _faulted = true;
            try { _report("travel", ex); } catch { }
        }
    }

    internal void SetSession(Guid? session)
    {
        Guard(() =>
        {
            if (_sessionValue == session) return;
            _sessionValue = session;
            _session = session ?? Guid.Empty;
            _tracker.Reset(session);
            _scopes.Reset();
            _modeByLeg.Clear(); _locations.Clear();
            _leg = null; _pendingMode = TravelMode.Unknown; _dockingWatched = false; _previousDocking = null;
            Events.SetSession(session);
            Station.SetSession(session);
        });
    }

    internal void Invalidate(string reason) => SetSession(null);

    // --- placement / dwell ----------------------------------------------------

    internal void ObservePlacement(object player, object? localManager)
    {
        if (_session == Guid.Empty || !IsLive(player)) return;
        Guard(() =>
        {
            var actual = _bindings.CurrentLocation(player);
            if (actual == null || localManager == null) return;
            var poi = _bindings.Poi(localManager);
            var playerPoi = _bindings.CurrentPoi(player);
            if (poi == null || playerPoi == null || !ReferenceEquals(poi, playerPoi)) return;
            if (!_bindings.Ready(localManager, poi)) return;
            Cache(actual);
            var place = PlaceOf(actual);
            if (place == null) return;
            _tracker.ObservePlacement(_session, place, _bindings.Time(player), true);
            Drain(_bindings.Time(player));
        });
    }

    // --- requested / cancelled ------------------------------------------------

    internal void OnRouteRequested(object player, object poi)
    {
        Guard(() =>
        {
            if (_session == Guid.Empty || !IsLive(player) || poi == null) return;
            var location = _bindings.Destination(poi);
            if (location == null) return;
            Cache(location);
            var place = PlaceOf(location);
            if (place == null) return;
            _pendingMode = TravelMode.InSystem;
            _leg = _tracker.Request(_session, place);
            if (_leg != null) _modeByLeg[_leg.Id] = _pendingMode;
            Drain(_bindings.Time(player));
        });
    }

    internal void OnTravelCancelled()
    {
        Guard(() =>
        {
            if (_session == Guid.Empty || _leg == null) return;
            _tracker.Cancel(_leg);
            _leg = null; _pendingMode = TravelMode.Unknown;
            var player = _bindings.Player;
            if (player != null) Drain(_bindings.Time(player));
        });
    }

    internal void OnDeparture(object player)
    {
        Guard(() =>
        {
            if (_session == Guid.Empty || !IsLive(player) || _leg == null) return;
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
            var player = _bindings.Player;
            if (_session == Guid.Empty || player == null || !IsLive(player) || _leg == null) return;
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
            if (valid) { _leg = null; _pendingMode = TravelMode.Unknown; }
            Drain(_bindings.Time(player));
            CheckRouteCompleted(player, arrived, valid);
        });
    }

    // --- cross-system jumps ---------------------------------------------------

    internal IEnumerator WrapJump(IEnumerator? inner, TravelMode mode, object? travelManager, object? player)
    {
        if (inner == null) return null!; // factories always yield an iterator; defensive only
        if (player != null)
            Guard(() =>
            {
                if (_session == Guid.Empty || player == null || !IsLive(player)) return;
                var origin = _bindings.CurrentLocation(player);
                var place = PlaceOf(origin);
                if (place == null) return; // unknown origin; cannot attribute a cross hop
                _pendingMode = mode;
                _leg = _tracker.Request(_session, place);
                if (_leg != null) _modeByLeg[_leg.Id] = _pendingMode;
                Drain(_bindings.Time(player));
            });
        return new TravelJumpObserver(inner, () => ObserveJumpStep(travelManager, player));
    }

    private void ObserveJumpStep(object? travelManager, object? player)
    {
        Guard(() =>
        {
            if (_session == Guid.Empty || player == null || !IsLive(player) || _leg == null) return;
            var current = _bindings.CurrentLocation(player);
            if (!_tracker.Departed(_leg) && current != null && !SamePlace(current, _tracker.Current))
            {
                _tracker.Depart(_leg, _bindings.Time(player));
                Drain(_bindings.Time(player));
            }
            var manager = travelManager == null ? null : _bindings.LocalManager(travelManager);
            if (manager == null) return;
            var poi = _bindings.Poi(manager);
            var playerPoi = _bindings.CurrentPoi(player);
            if (poi == null || playerPoi == null || !ReferenceEquals(poi, playerPoi)) return;
            if (!_bindings.Ready(manager, poi)) return;
            if (current == null) return;
            Cache(current);
            var place = PlaceOf(current);
            if (place == null) return;
            if (!_tracker.OwnsPending(_leg)) return;
            var arrived = _leg;
            var valid = _tracker.Arrive(arrived, place, _bindings.Time(player), true);
            if (valid) { _leg = null; _pendingMode = TravelMode.Unknown; }
            Drain(_bindings.Time(player));
            CheckRouteCompleted(player, arrived, valid);
        });
    }

    private void CheckRouteCompleted(object player, TravelLegTracker.Leg arrived, bool valid)
    {
        if (!valid || _tracker.Current == null) return;
        var travel = _bindings.TravelManager();
        if (travel == null) return;
        if (_bindings.WaypointCount(player) == 0 && !_bindings.UsingJumpgate(travel))
        {
            var actual = LocForPlace(_tracker.Current);
            if (actual != null)
            {
                var mode = _modeByLeg.TryGetValue(arrived.Id, out var m) ? m : TravelMode.Unknown;
                Events.Emit(_session, arrived.Id, TravelTransitionKind.RouteCompleted, mode,
                    null, null, actual, _bindings.Time(player));
            }
        }
    }

    private static bool SamePlace(TravelLocation a, TravelLegTracker.Place? b)
        => b != null && a.SystemId == b.SystemId && a.PoiId == b.PoiId;

    // --- station facts --------------------------------------------------------

    internal void OnInteriorReady(object player, object station, Exception? error)
    {
        Guard(() =>
        {
            if (error != null || _session == Guid.Empty || !IsLive(player) || station == null) return;
            var location = _bindings.Destination(station);
            if (location == null) return;
            Cache(location);
            Station.Emit(_session, StationTransitionKind.InteriorReady, location, _bindings.Time(player));
        });
    }

    internal void OnInteriorDestroyed(object player, object station)
    {
        Guard(() =>
        {
            if (_session == Guid.Empty || station == null) return;
            var location = _bindings.Destination(station);
            if (location == null) return;
            Station.Emit(_session, StationTransitionKind.InteriorDestroyed, location, _bindings.Time(player));
        });
    }

    internal void ObserveDockingState(object player)
    {
        if (_session == Guid.Empty || !IsLive(player)) return;
        Guard(() =>
        {
            var raw = _bindings.DockingState(player);
            if (!_dockingWatched) { _dockingWatched = true; _previousDocking = raw; return; }
            var previous = _previousDocking;
            _previousDocking = raw;
            // Source.SpaceShip.Auto.DockingState: 3=Docked, 4=Undocking, 5=Leaving.
            if (raw == 3 && previous != 3) Station.Emit(_session, StationTransitionKind.DockedPhysical, LocForPlace(_tracker.Current), _bindings.Time(player));
            else if (raw == 4 && previous != 4) Station.Emit(_session, StationTransitionKind.Undocking, LocForPlace(_tracker.Current), _bindings.Time(player));
            else if (raw == 5 && previous != 5) Station.Emit(_session, StationTransitionKind.Leaving, LocForPlace(_tracker.Current), _bindings.Time(player));
        });
    }

    internal void Tick(object? player, object? manager)
    {
        if (player != null) { ObservePlacement(player, manager); ObserveDockingState(player); }
    }

    private void Drain(double now)
    {
        var facts = _tracker.Drain();
        if (facts.Length == 0) return;
        var player = _bindings.Player;
        if (player == null) return;
        foreach (var fact in facts)
        {
            var mode = fact.Operation.HasValue && _modeByLeg.TryGetValue(fact.Operation.Value, out var m) ? m : TravelMode.Unknown;
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
                    actual = LocForPlace(fact.Location); break;
                case TravelLegTracker.Kind.Cancelled:
                    actual = LocForPlace(fact.Location); break;
            }
            Events.Emit(_session, fact.Operation, ToKind(fact.Transition), mode, origin, requested, actual, now, fact.DwellSeconds);
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

    private bool IsLive(object player)
        => _session != Guid.Empty && player != null && ReferenceEquals(player, _bindings.Player);

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
