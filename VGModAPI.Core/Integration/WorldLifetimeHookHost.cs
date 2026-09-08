using System;
using System.IO;
using System.Reflection;

namespace VGModAPI.Core.Integration;

internal interface IWorldLifetimeHookHost
{
    bool AllowAmbient(object poi);
    bool AllowRemoval(object poi);
    bool AllowUse(object poi);
    bool AllowManager(object manager);
    Func<bool> CaptureManager(object manager);
    bool AllowManagerAwake(object manager);
}

/// <summary>Refuses reserved ambient/removal paths before their native bodies; no world readiness is inferred.</summary>
internal sealed partial class WorldLifetimeHookHost : IWorldLifetimeHookHost, IDisposable
{
    private readonly LifecycleHub _hub;
    internal WorldTravelScopes Travel { get; } = new();
    private readonly FieldInfo _guid;
    private readonly FieldInfo _managerPoi;
    private readonly FieldInfo _player, _playerPoi, _travelInstance, _localTarget;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> _blockedManagers = new();
    private readonly WorldLifetimeGuard _guard;
    private readonly IDisposable _subscription;
    private Guid _session;
    private bool _disposed;
    internal WorldLifetimeHookHost(Assembly assembly, LifecycleHub hub, WorldLifetimeGuard? guard = null)
    {
        _guard = guard ?? new WorldLifetimeGuard();
        _hub = hub; _hub.CheckThread();
        if (_hub.CurrentSession != null) throw new InvalidOperationException("World lifetime guards must attach before a session.");
        _guid = assembly.GetType("Source.Galaxy.MapElement", true)!.GetField("<guid>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException("MapElement.guid backing field");
        if (_guid.FieldType != typeof(string)) throw new MissingFieldException("MapElement.guid must be a string.");
        _managerPoi = assembly.GetType("Behaviour.Managers.BasePoiManager", true)!.GetField("<poi>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException("BasePoiManager.poi backing field");
        var player = assembly.GetType("Source.Player.GamePlayer", true)!;
        var travel = assembly.GetType("Behaviour.Managers.TravelManager", true)!;
        _player = player.GetField("current", BindingFlags.Public | BindingFlags.Static) ?? throw new MissingFieldException("GamePlayer.current");
        _playerPoi = player.GetField("currentPointOfInterest", BindingFlags.Public | BindingFlags.Instance) ?? throw new MissingFieldException("GamePlayer.currentPointOfInterest");
        _travelInstance = assembly.GetType("Behaviour.Util.Singleton`1", true)!.MakeGenericType(travel).GetField("instance", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingFieldException("Singleton<TravelManager>.instance");
        _localTarget = travel.GetField("<localTarget>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance) ?? throw new MissingFieldException("TravelManager.localTarget backing field");
        _subscription = hub.Subscribe("vgmodapi.world-lifetime", OnLifecycle);
    }
    private void OnLifecycle(LifecycleEvent e)
    {
        if (e.Kind == LifecycleEventKind.SessionStarting && e.Session?.Id == _hub.CurrentSession?.Id)
        { _session = e.Session!.Id; _guard.Start(_session); Travel.Reset(); }
        else if ((e.Kind == LifecycleEventKind.SessionInvalidated || e.Kind == LifecycleEventKind.SessionStartFailed) && e.Session?.Id == _session)
        { _guard.Invalidate(); Travel.InvalidateSession(_session); }
    }
    private string Identity(object poi) => _guid.GetValue(poi) as string ?? throw new InvalidDataException("Missing native POI identity.");
    public bool AllowAmbient(object poi)
    {
        _hub.CheckThread();
        return _guard.AllowAmbient(_hub.CurrentSession?.Id ?? Guid.Empty, poi, Identity(poi));
    }
    public bool AllowUse(object poi) => AllowAmbient(poi);
    public bool AllowManager(object manager)
    {
        _hub.CheckThread();
        var poi = _managerPoi.GetValue(manager);
        return !_blockedManagers.TryGetValue(manager, out _) && (poi == null || AllowUse(poi));
    }
    public bool AllowManagerAwake(object manager)
    {
        _hub.CheckThread();
        if (_blockedManagers.TryGetValue(manager, out _)) return false;
        var player = _player.GetValue(null);
        var travel = _travelInstance.GetValue(null);
        var current = player == null ? null : _playerPoi.GetValue(player);
        var target = travel == null ? null : _localTarget.GetValue(travel);
        // Before poi assignment, conservatively require both possible native resolution candidates.
        if ((current == null || AllowUse(current)) && (target == null || AllowUse(target))) return true;
        _blockedManagers.GetValue(manager, _ => new object());
        return false;
    }
    public Func<bool> CaptureManager(object manager)
    {
        _hub.CheckThread();
        var poi = _managerPoi.GetValue(manager);
        var session = _hub.CurrentSession?.Id ?? Guid.Empty;
        if (poi != null) _ = AllowUse(poi); // Retain reserved classification even before first advancement.
        return () =>
        {
            _hub.CheckThread();
            return !_blockedManagers.TryGetValue(manager, out _) && session == (_hub.CurrentSession?.Id ?? Guid.Empty) && ReferenceEquals(poi, _managerPoi.GetValue(manager)) &&
                (poi == null || AllowUse(poi));
        };
    }
    public bool AllowRemoval(object poi)
    {
        _hub.CheckThread(); return _guard.AllowNativeRemoval(poi, Identity(poi));
    }
    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return;
        _disposed = true; _guard.Stop(); Travel.Stop(); _subscription.Dispose();
    }
}
