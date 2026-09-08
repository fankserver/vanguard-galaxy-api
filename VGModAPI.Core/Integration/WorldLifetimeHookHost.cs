using System;
using System.IO;
using System.Reflection;

namespace VGModAPI.Core.Integration;

internal interface IWorldLifetimeHookHost
{
    bool AllowAmbient(object poi);
    bool AllowRemoval(object poi);
    bool AllowUse(object poi);
}

/// <summary>Refuses reserved ambient/removal paths before their native bodies; no world readiness is inferred.</summary>
internal sealed class WorldLifetimeHookHost : IWorldLifetimeHookHost, IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly FieldInfo _guid;
    private readonly WorldLifetimeGuard _guard = new();
    private readonly IDisposable _subscription;
    private Guid _session;
    private bool _disposed;
    internal WorldLifetimeHookHost(Assembly assembly, LifecycleHub hub)
    {
        _hub = hub; _hub.CheckThread();
        if (_hub.CurrentSession != null) throw new InvalidOperationException("World lifetime guards must attach before a session.");
        _guid = assembly.GetType("Source.Galaxy.MapElement", true)!.GetField("<guid>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException("MapElement.guid backing field");
        if (_guid.FieldType != typeof(string)) throw new MissingFieldException("MapElement.guid must be a string.");
        _subscription = hub.Subscribe("vgmodapi.world-lifetime", OnLifecycle);
    }
    private void OnLifecycle(LifecycleEvent e)
    {
        if (e.Kind == LifecycleEventKind.SessionStarting && e.Session?.Id == _hub.CurrentSession?.Id)
        { _session = e.Session!.Id; _guard.Start(_session); }
        else if ((e.Kind == LifecycleEventKind.SessionInvalidated || e.Kind == LifecycleEventKind.SessionStartFailed) && e.Session?.Id == _session)
            _guard.Invalidate();
    }
    private string Identity(object poi) => _guid.GetValue(poi) as string ?? throw new InvalidDataException("Missing native POI identity.");
    public bool AllowAmbient(object poi)
    {
        _hub.CheckThread();
        return _guard.AllowAmbient(_hub.CurrentSession?.Id ?? Guid.Empty, poi, Identity(poi));
    }
    public bool AllowUse(object poi) => AllowAmbient(poi);
    public bool AllowRemoval(object poi)
    {
        _hub.CheckThread(); return _guard.AllowNativeRemoval(poi, Identity(poi));
    }
    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return;
        _disposed = true; _guard.Stop(); _subscription.Dispose();
    }
}
