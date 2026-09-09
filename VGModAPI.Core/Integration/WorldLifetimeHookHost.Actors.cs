using System;
using System.IO;
using System.Reflection;

namespace VGModAPI.Core.Integration;

internal interface IWorldActorLifetimeHost
{
    IDisposable BeginSpawn(object manager);
    void CaptureActor(object actor);
    bool AllowActor(object actor);
    Func<bool>? CaptureActivity(object actor);
}

internal sealed partial class WorldLifetimeHookHost : IWorldActorLifetimeHost
{
    private readonly WorldActorOrigins _actors = new();
    public IDisposable BeginSpawn(object manager)
    {
        _hub.CheckThread();
        if (!AllowManager(manager)) throw new InvalidDataException("World actor spawn is quarantined.");
        var poi = _managerPoi.GetValue(manager);
        if (poi == null || AllowRemoval(poi)) return _actors.Enter(null);
        var managerValid = CaptureManager(manager);
        var travel = _travelInstance.GetValue(null); var player = _player.GetValue(null);
        var local = _localTarget.DeclaringType!.GetField("<localPoiManager>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException("TravelManager.localPoiManager");
        bool Valid() => managerValid() && ReferenceEquals(poi, _managerPoi.GetValue(manager)) &&
            ReferenceEquals(player, _player.GetValue(null)) && ReferenceEquals(travel, _travelInstance.GetValue(null)) &&
            travel != null && ReferenceEquals(local.GetValue(travel), manager);
        if (!Valid()) throw new InvalidDataException("World actor spawn lacks its current native manager.");
        return _actors.Enter(Valid);
    }
    public void CaptureActor(object actor)
    {
        _hub.CheckThread(); _actors.Capture(actor);
        if (!AllowActor(actor)) throw new InvalidDataException("World actor awakening is quarantined.");
    }
    public Func<bool>? CaptureActivity(object actor)
    {
        _hub.CheckThread();
        return _actors.Known(actor) ? () => AllowActor(actor) : null;
    }
    public bool AllowActor(object actor)
    {
        _hub.CheckThread();
        if (!_actors.Known(actor)) return true;
        try
        {
            WorldNativeAssetInspection.RequireAlive(actor);
            if (!_actors.Allow(actor)) return false;
            WorldNativeAssetInspection.RequireAlive(actor); return true;
        }
        catch (InvalidDataException) { return false; }
    }
}
