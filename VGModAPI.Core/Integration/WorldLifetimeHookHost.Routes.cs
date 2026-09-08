using System;
using System.IO;

namespace VGModAPI.Core.Integration;

internal interface IWorldRouteCaptureHost
{
    object? BeginRoute(object manager, object target);
    void CompleteRoute(object token, bool succeeded);
}

internal sealed partial class WorldLifetimeHookHost : IWorldRouteCaptureHost
{
    private sealed class RouteRequest
    {
        internal readonly WorldLifetimeHookHost Owner;
        internal readonly WorldTravelScopes.Route Route;
        internal readonly IDisposable Scope;
        internal bool Completed;
        internal RouteRequest(WorldLifetimeHookHost owner, WorldTravelScopes.Route route, IDisposable scope)
        { Owner = owner; Route = route; Scope = scope; }
    }
    public object? BeginRoute(object manager, object target)
    {
        _hub.CheckThread();
        if (_disposed) return null;
        var session = _hub.CurrentSession;
        var player = _player.GetValue(null);
        if (session == null || player == null) return null;
        if (!ReferenceEquals(_travelInstance.GetValue(null), manager) || !AllowUse(target))
            throw new InvalidDataException("Travel request lacks current manager or world admission.");
        var route = Travel.Begin(session.Id, player, manager, target);
        return new RouteRequest(this, route, Travel.EnterRequest(route));
    }
    public void CompleteRoute(object token, bool succeeded)
    {
        _hub.CheckThread();
        if (token is not RouteRequest request || !ReferenceEquals(request.Owner, this) || request.Completed)
            throw new InvalidDataException("Unknown or replayed world route completion.");
        request.Completed = true;
        try
        {
            if (!Travel.IsCurrent(request.Route)) return;
            if (!succeeded) { Travel.Cancel(request.Route); return; }
            if (_disposed || _hub.CurrentSession?.Id != request.Route.Session || !ReferenceEquals(_player.GetValue(null), request.Route.Player) ||
                !ReferenceEquals(_travelInstance.GetValue(null), request.Route.Manager) || !AllowUse(request.Route.Destination))
            {
                Travel.Cancel(request.Route);
                throw new InvalidDataException("Travel request changed before completion.");
            }
        }
        finally { request.Scope.Dispose(); }
    }
}
