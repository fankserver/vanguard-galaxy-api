using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace VGModAPI.Core.Integration;

internal interface IWorldTravelCaptureHost
{
    IEnumerator WrapLeg(object manager, object target, IEnumerator inner);
    IEnumerator WrapChild(object manager, IEnumerator inner);
    void RequireSceneTransition(object manager);
    object? BeginWaypoint(object manager);
    void EndWaypoint(object token);
}

internal sealed partial class WorldLifetimeHookHost : IWorldTravelCaptureHost
{
    private sealed class WaypointRequest
    {
        internal readonly WorldLifetimeHookHost Owner;
        internal readonly WorldTravelScopes.Handoff Handoff;
        internal readonly WaypointRequest? Parent;
        internal bool Ended;
        internal WaypointRequest(WorldLifetimeHookHost owner, WorldTravelScopes.Handoff handoff, WaypointRequest? parent)
        { Owner = owner; Handoff = handoff; Parent = parent; }
    }
    private WaypointRequest? _waypoint;
    private object? NextWaypoint(WorldTravelScopes.Route route)
    {
        var field = _player.DeclaringType!.GetField("waypoints", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingFieldException("GamePlayer.waypoints");
        var expected = typeof(List<>).MakeGenericType(_localTarget.FieldType);
        if (field.FieldType != expected || field.GetValue(route.Player) is not IList points || points.GetType() != expected || points.Count > 10000)
            throw new InvalidDataException("Unsupported native waypoint inventory.");
        return points.Count == 0 ? null : points[0] ?? throw new InvalidDataException("Missing native waypoint.");
    }
    private void VerifyRouteNative(WorldTravelScopes.Route route, object manager)
    {
        _hub.CheckThread();
        if (_disposed || !Travel.IsCurrent(route) || _hub.CurrentSession?.Id != route.Session || !ReferenceEquals(_player.GetValue(null), route.Player) ||
            !ReferenceEquals(manager, route.Manager) || !ReferenceEquals(_travelInstance.GetValue(null), manager) || !AllowUse(route.Destination))
            throw new InvalidDataException("Travel route no longer has its originating native context.");
    }
    public object? BeginWaypoint(object manager)
    {
        _hub.CheckThread();
        var leg = Travel.CaptureExecuting() ?? Travel.CurrentLeg;
        if (leg == null) return null;
        VerifyRouteNative(leg.Route, manager);
        if (!leg.Completed && !ReferenceEquals(_playerPoi.GetValue(leg.Route.Player), leg.Target))
            throw new InvalidDataException("Active leg has not reached its waypoint.");
        var next = NextWaypoint(leg.Route);
        if (next == null) return null;
        if (!AllowUse(next)) throw new InvalidDataException("Next world waypoint is quarantined.");
        var handoff = leg.Completed ? Travel.PrepareContinuation(leg, next) : Travel.PrepareHandoff(leg, next);
        return _waypoint = new WaypointRequest(this, handoff, _waypoint);
    }
    public void EndWaypoint(object token)
    {
        _hub.CheckThread();
        if (token is not WaypointRequest request || !ReferenceEquals(request.Owner, this) || request.Ended)
            throw new InvalidDataException("Unknown waypoint scope completion.");
        request.Ended = true;
        if (!ReferenceEquals(_waypoint, request)) return;
        var parent = request.Parent;
        while (parent != null && parent.Ended) parent = parent.Parent;
        _waypoint = parent;
    }
    public void RequireSceneTransition(object manager)
    {
        _hub.CheckThread();
        var leg = Travel.CaptureExecuting();
        var target = _localTarget.GetValue(manager);
        if (leg == null)
        {
            if (target != null && !AllowRemoval(target)) throw new InvalidDataException("Owned scene transition lacks originating travel scope.");
            if (Travel.CurrentLeg is { Completed: false }) throw new InvalidDataException("Active travel scene transition lost its origin.");
            return;
        }
        VerifyRouteNative(leg.Route, manager);
        Travel.RequireActive(leg, _hub.CurrentSession!.Id, _player.GetValue(null)!, manager);
        if (!ReferenceEquals(target, leg.Target) || !AllowUse(leg.Target))
            throw new InvalidDataException("World scene transition target changed or became unavailable.");
    }
    public IEnumerator WrapChild(object manager, IEnumerator inner)
    {
        _hub.CheckThread();
        var leg = Travel.CaptureExecuting();
        if (leg == null)
        {
            var target = _localTarget.GetValue(manager);
            if ((Travel.CurrentLeg is { Completed: false }) || (target != null && !AllowRemoval(target)))
                throw new InvalidDataException("Travel preparation child lost its originating leg.");
            return inner;
        }
        return new WorldTravelChildEnumerator(inner, Travel, leg, () =>
        {
            VerifyRouteNative(leg.Route, manager);
            Travel.RequireActive(leg, _hub.CurrentSession!.Id, _player.GetValue(null)!, manager);
            if (!ReferenceEquals(_localTarget.GetValue(manager), leg.Target) || !AllowUse(leg.Target))
                throw new InvalidDataException("Travel preparation target changed or became unavailable.");
        });
    }
    public IEnumerator WrapLeg(object manager, object target, IEnumerator inner)
    {
        _hub.CheckThread();
        WorldTravelScopes.Leg leg;
        if (_waypoint != null && Travel.IsCurrent(_waypoint.Handoff.Predecessor.Route))
        {
            var route = _waypoint.Handoff.Predecessor.Route;
            VerifyRouteNative(route, manager);
            if (!ReferenceEquals(target, NextWaypoint(route))) throw new InvalidDataException("Native waypoint changed before leg creation.");
            leg = Travel.AcceptHandoff(_waypoint.Handoff, target);
        }
        else if (Travel.CaptureRequest() is { } route)
        {
            VerifyRouteNative(route, manager);
            if (!ReferenceEquals(target, NextWaypoint(route) ?? route.Destination)) throw new InvalidDataException("Initial travel target differs from requested waypoint.");
            leg = Travel.First(route, target);
        }
        else
        {
            if (!AllowRemoval(target)) throw new InvalidDataException("Owned travel leg lacks a captured request or handoff.");
            return inner;
        }
        bool firstCheck = true;
        return new WorldTravelLegEnumerator(inner, Travel, leg, () =>
        {
            VerifyRouteNative(leg.Route, manager);
            Travel.RequireActive(leg, _hub.CurrentSession!.Id, _player.GetValue(null)!, manager);
            if (!AllowUse(target) || (!firstCheck && !ReferenceEquals(_localTarget.GetValue(manager), target)))
                throw new InvalidDataException("Travel leg target changed or became unavailable.");
            firstCheck = false; // Native StartTravel assigns localTarget on its first advancement.
        });
    }
}
