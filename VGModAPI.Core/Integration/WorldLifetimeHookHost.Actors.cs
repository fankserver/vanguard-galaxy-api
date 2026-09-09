using System;
using System.IO;
using System.Reflection;

namespace VGModAPI.Core.Integration;

internal interface IWorldActorLifetimeHost
{
    IDisposable BeginSpawn(object manager, object? data = null);
    bool AllowPersistable(object component);
    void CaptureActor(object actor);
    bool AllowActor(object actor);
    Func<bool>? CaptureActivity(object actor);
}

internal sealed partial class WorldLifetimeHookHost : IWorldActorLifetimeHost
{
    private readonly WorldActorOrigins _actors = new();
    private readonly Action<object>? _quarantine;
    internal void MaintainActors()
    {
        _hub.CheckThread();
        System.Collections.Generic.List<Exception>? failures = null;
        foreach (var actor in _actors.Snapshot())
        {
            try { AllowActor(actor); }
            catch (Exception error) { (failures ??= new System.Collections.Generic.List<Exception>()).Add(error); }
        }
        if (failures != null) throw new AggregateException("Owned actor maintenance failed for one or more roots.", failures);
    }
    private void StopPhysics(object actor)
    {
        if (_quarantine == null) return;
        try { WorldNativeAssetInspection.RequireAlive(actor); }
        catch (InvalidDataException) { return; }
        _quarantine(actor);
    }
    public IDisposable BeginSpawn(object manager, object? data = null)
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
        return _actors.Enter(Valid, data);
    }
    public bool AllowPersistable(object component)
    {
        _hub.CheckThread();
        var type = component.GetType();
        var gameObject = type.GetProperty("gameObject", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMemberException("PersistableUpdater.gameObject");
        var source = gameObject.GetValue(component);
        if (source == null) return !_actors.Known(component);
        if (!_actors.Attach(component, source)) return false;
        if (!_actors.Known(component)) return true;
        var data = type.GetField("data", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingFieldException("PersistableUpdater.data");
        if (!_actors.MatchesData(component, data.GetValue(component))) return false;
        if (!AllowActor(source) || !AllowActor(component)) return false;
        return ReferenceEquals(source, gameObject.GetValue(component)) && _actors.MatchesData(component, data.GetValue(component));
    }
    public void CaptureActor(object actor)
    {
        _hub.CheckThread();
        try { _actors.Capture(actor); }
        catch { if (_actors.Known(actor)) StopPhysics(actor); throw; }
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
        try { WorldNativeAssetInspection.RequireAlive(actor); }
        catch (InvalidDataException) { return false; }
        bool allowed;
        try { allowed = _actors.Allow(actor); }
        catch { StopPhysics(actor); throw; }
        if (!allowed) { StopPhysics(actor); return false; }
        try { WorldNativeAssetInspection.RequireAlive(actor); return true; }
        catch (InvalidDataException) { return false; }
    }
}
