using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace VGModAPI.Core.Integration;

/// <summary>
/// Reconciles declared installations against the current galaxy on a slow cadence: hardens the
/// native persisted station invincibility (which natively protects parts, docking and interior
/// structure), and performs the guarded repair of targets that were already unusable. A live
/// operation, the player's presence and a legitimately cleared interior are never overridden.
/// Disposal restores an invincibility flag this session observed off. All faults fail open.
/// </summary>
internal sealed class DungeonAegisRuntime
{
    private const double Cadence = 2;
    private readonly DungeonAegisService _service;
    private readonly Func<object[]> _liveParts;
    private readonly Action<Exception> _report;
    private readonly Action<string> _notice;
    private readonly LifecycleHub _hub;
    private readonly PropertyInfo _mapCurrent, _allPois, _guid, _poiCurrent, _stationParts, _partPrefab, _partType, _managerInstance;
    private readonly MethodInfo _persistables;
    private readonly FieldInfo _stationInvincible, _locationData, _stationData, _operationActive, _dockingDestroyed, _integrity, _simulation, _partLocation, _partInvincible;
    private readonly PropertyInfo _collapse, _retreating, _victory, _complete;
    private readonly MethodInfo _getOperation;
    private readonly Type _location;
    private readonly object[] _dockingTypes;
    private readonly Dictionary<string, TargetState> _states = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reportedFaults = new(StringComparer.Ordinal);
    private Guid? _session;
    private double _due;

    private sealed class TargetState
    {
        internal bool HardenedFromOff, Repaired, PartsPassDone;
    }

    internal DungeonAegisRuntime(Assembly assembly, LifecycleHub hub, DungeonAegisService service, Func<object[]> livePartScanner, Action<string> notice, Action<Exception> report)
    {
        _service = service; _hub = hub; _liveParts = livePartScanner; _notice = notice; _report = report;
        var map = assembly.GetType("Source.Galaxy.GalaxyMapData", true)!;
        _mapCurrent = Property(map, "current"); _allPois = Property(map, "allPointsOfInterest");
        var element = assembly.GetType("Source.Galaxy.MapElement", true)!;
        _guid = Property(element, "guid");
        var poi = assembly.GetType("Source.Galaxy.MapPointOfInterest", true)!;
        _persistables = Method(poi, "GetPersistables");
        _poiCurrent = Property(poi, "current");
        _location = assembly.GetType("Source.Data.Persistable.DungeonLocationData", true)!;
        _stationInvincible = Field(_location, "stationIsInvincible");
        _locationData = Field(_location, "dungeonData");
        _stationData = Field(_location, "stationData");
        var data = assembly.GetType("Source.Dungeon.DungeonData", true)!;
        _operationActive = Field(data, "isOperationActive");
        _dockingDestroyed = Field(data, "dockingDestroyed");
        _integrity = Field(data, "facilityIntegrity");
        _simulation = Field(data, "simulation");
        var simulation = assembly.GetType("Source.Dungeon.DungeonSimulation", true)!;
        _collapse = Property(simulation, "structuralCollapse"); _retreating = Property(simulation, "isRetreating");
        _victory = Property(simulation, "victoryAchieved"); _complete = Property(simulation, "isComplete");
        _stationParts = Property(assembly.GetType("Source.Data.Persistable.CombatStationData", true)!, "stationParts");
        _partPrefab = Property(assembly.GetType("Source.Data.CombatStationPartData", true)!, "partPrefab");
        var part = assembly.GetType("Behaviour.Unit.CombatStationPart", true)!;
        _partType = Property(part, "partType");
        _partLocation = Field(part, "dungeonLocationData");
        _partInvincible = Field(assembly.GetType("Behaviour.Weapons.TargetableUnit", true)!, "isInvincible");
        var partTypes = _partType.PropertyType;
        _dockingTypes = new[] { Enum.Parse(partTypes, "DockingPad"), Enum.Parse(partTypes, "DockingTunnel"), Enum.Parse(partTypes, "CargoDock") };
        var manager = assembly.GetType("Behaviour.Managers.DungeonManager", true)!;
        _managerInstance = Property(manager.BaseType!, "Instance");
        _getOperation = manager.GetMethod("GetOperation", new[] { _location })
            ?? throw new MissingMethodException(manager.FullName, "GetOperation");
    }
    private static PropertyInfo Property(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
        ?? throw new MissingMemberException(type.FullName, name);
    private static FieldInfo Field(Type type, string name) =>
        type.GetField(name, BindingFlags.Public | BindingFlags.Instance)
        ?? throw new MissingFieldException(type.FullName, name);
    private static MethodInfo Method(Type type, string name) =>
        type.GetMethod(name, Type.EmptyTypes) ?? throw new MissingMethodException(type.FullName, name);

    internal void Tick(double now)
    {
        if (now < _due) return;
        _due = now + Cadence;
        try
        {
            var session = _hub.CurrentSession is { } current &&
                current.Phase is SessionPhase.PlayerReady or SessionPhase.GameplayInitialized ? current.Id : (Guid?)null;
            if (session != _session) { _states.Clear(); _session = session; }
            if (session == null) return;
            var declared = _service.DeclaredTargets();
            foreach (var poiId in declared)
                try { Reconcile(poiId); }
                catch (Exception error) { ReportOnce("target " + poiId, error); }
            ReleaseUndeclared(declared);
        }
        catch (Exception error) { ReportOnce("tick", error); }
    }

    private void Reconcile(string poiId)
    {
        if (Resolve(poiId) is not { } location) return;
        if (!_states.TryGetValue(poiId, out var state)) _states.Add(poiId, state = new TargetState());
        // Harden through the game's own persisted mechanism, remembering what this session observed.
        if (!(bool)_stationInvincible.GetValue(location)!)
        {
            state.HardenedFromOff = true;
            _stationInvincible.SetValue(location, true);
        }
        if (!state.PartsPassDone)
        {
            state.PartsPassDone = true;
            foreach (var part in _liveParts())
            {
                if (part == null || !ReferenceEquals(_partLocation.GetValue(part), location)) continue;
                _partInvincible.SetValue(part, true);
            }
        }
        Repair(poiId, location, state);
    }

    private void Repair(string poiId, object location, TargetState state)
    {
        if (_locationData.GetValue(location) is not { } data) return; // Never entered: nothing to repair.
        if ((bool)_operationActive.GetValue(data)!) return;
        if (_poiCurrent.GetValue(null) is { } present && _guid.GetValue(present) as string == poiId) return;
        var manager = _managerInstance.GetValue(null);
        if (manager != null && _getOperation.Invoke(manager, new[] { location }) != null) return;
        var simulation = _simulation.GetValue(data);
        var integrity = (float)_integrity.GetValue(data)!;
        var broken = (bool)_dockingDestroyed.GetValue(data)! ||
            (integrity >= 0f && integrity <= 0.1f) ||
            (simulation != null && Collapsed(simulation));
        if (!broken) return;
        // A legitimate clear is a player outcome, never something the world degraded; leave it alone.
        if (simulation != null && (bool)_complete.GetValue(simulation)! && (bool)_victory.GetValue(simulation)!) return;
        var dockingLost = (bool)_dockingDestroyed.GetValue(data)!;
        _dockingDestroyed.SetValue(data, false);
        _integrity.SetValue(data, -1f); // The game's healthy sentinel.
        if (simulation != null) _simulation.SetValue(data, null); // Only the broken interior regenerates.
        if (dockingLost && !HasDockingPart(location))
            _stationData.SetValue(location, null); // Regenerates natively with docking on next arrival.
        if (!state.Repaired)
        {
            state.Repaired = true;
            // An intended repair is information, not an error.
            try { _notice("Restored enterability of declared installation '" + poiId + "' after ambient degradation."); } catch { }
        }
    }

    private bool Collapsed(object simulation) =>
        (bool)_collapse.GetValue(simulation)! ||
        ((bool)_retreating.GetValue(simulation)! && !(bool)_victory.GetValue(simulation)!);

    private bool HasDockingPart(object location)
    {
        if (_stationData.GetValue(location) is not { } station) return false;
        if (_stationParts.GetValue(station) is not IEnumerable parts) return false;
        foreach (var part in parts)
        {
            var prefab = part == null ? null : _partPrefab.GetValue(part);
            if (prefab == null) continue;
            var type = _partType.GetValue(prefab);
            foreach (var docking in _dockingTypes)
                if (Equals(type, docking)) return true;
        }
        return false;
    }

    private void ReleaseUndeclared(string[] declared)
    {
        foreach (var poiId in new List<string>(_states.Keys))
        {
            if (Array.IndexOf(declared, poiId) >= 0) continue;
            var state = _states[poiId];
            _states.Remove(poiId);
            // Only a flag this session turned on is turned back off; native or foreign hardening stays.
            if (!state.HardenedFromOff || Resolve(poiId) is not { } location) continue;
            if ((bool)_stationInvincible.GetValue(location)!) _stationInvincible.SetValue(location, false);
        }
    }

    /// <summary>The native DungeonLocationData for a persistent POI identity, or null when absent or ambiguous.</summary>
    internal object? ResolveLocation(string poiId) => Resolve(poiId);

    private object? Resolve(string poiId)
    {
        if (_mapCurrent.GetValue(null) is not { } map || _allPois.GetValue(map) is not IEnumerable points) return null;
        object? found = null;
        foreach (var poi in points)
        {
            if (poi == null || _guid.GetValue(poi) as string != poiId) continue;
            if (found != null) return null; // Ambiguous identity never hardens or repairs anything.
            found = poi;
        }
        if (found == null || _persistables.Invoke(found, null) is not IEnumerable persistables) return null;
        object? location = null;
        foreach (var persistable in persistables)
        {
            if (persistable == null || !_location.IsInstanceOfType(persistable)) continue;
            if (location != null) return null;
            location = persistable;
        }
        return location;
    }

    private void ReportOnce(string identity, Exception error)
    {
        if (!_reportedFaults.Add(identity)) return;
        try { _report(error); } catch { }
    }
}
