using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace VGModAPI.Core.Integration;

/// <summary>
/// Applies drone-bay tuning at the native boundaries: launch-duration reads and replacement rolls
/// consult the exact bay's owning unit identity, and declared complements are rebuilt through the
/// game's own per-drone initialisation in staggered batches. Every fault fails open to vanilla.
/// </summary>
internal sealed class DroneBayRuntime
{
    private const double Cadence = 0.25;
    private const int Batch = 10;
    private readonly DroneBayService _service;
    private readonly LifecycleHub _hub;
    private readonly Func<object[]> _ships;
    private readonly Func<object, object?> _findBay;
    private readonly Action<object> _destroyDrone;
    private readonly Action<string> _notice;
    private readonly Action<Exception> _report;
    private readonly Type _unit;
    private readonly PropertyInfo _unitData, _guid, _bayParent;
    private readonly FieldInfo _droneAmount, _bonusAmount, _shouldDeploy, _drones;
    private readonly MethodInfo _addDrone, _droneGet;
    private readonly Dictionary<string, BuildState> _builds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private Guid? _session;
    private double _due;

    private sealed class BuildState { internal int Next = -1; internal bool Done; }

    internal DroneBayRuntime(Assembly assembly, LifecycleHub hub, DroneBayService service,
        Func<object[]> shipScanner, Func<object, object?> bayFinder, Action<object> droneDestroyer,
        Action<string> notice, Action<Exception> report)
    {
        _hub = hub; _service = service; _ships = shipScanner; _findBay = bayFinder; _destroyDrone = droneDestroyer;
        _notice = notice; _report = report;
        _unit = assembly.GetType("Behaviour.Unit.AbstractUnit", true)!;
        _unitData = Property(_unit, "unitData");
        _guid = Property(assembly.GetType("Source.Data.AbstractUnitData", true)!, "guid");
        var bay = assembly.GetType("Behaviour.Equipment.Module.DroneBayModule", true)!;
        _bayParent = Property(bay, "parent");
        _droneAmount = Field(bay, "_droneAmount");
        _bonusAmount = Field(bay, "droneBonusAmount");
        _shouldDeploy = Field(bay, "shouldDeploy");
        _drones = Field(bay, "drones");
        _addDrone = bay.GetMethod("AddNewDrone", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(int) }, null)
            ?? throw new MissingMethodException(bay.FullName, "AddNewDrone");
        var drone = assembly.GetType("Behaviour.Unit.Drone", true)!;
        _droneGet = drone.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null)
            ?? throw new MissingMethodException(drone.FullName, "Get");
        if (_droneGet.ReturnType != drone) throw new MissingMethodException(drone.FullName, "Get");
    }
    private static PropertyInfo Property(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingMemberException(type.FullName, name);
    private static FieldInfo Field(Type type, string name) =>
        type.GetField(name, BindingFlags.Public | BindingFlags.Instance)
        ?? throw new MissingFieldException(type.FullName, name);

    private string? OwnerId(object? bay)
    {
        if (bay == null) return null;
        var parent = _bayParent.GetValue(bay);
        if (parent == null || !_unit.IsInstanceOfType(parent)) return null;
        return _unitData.GetValue(parent) is { } data ? _guid.GetValue(data) as string : null;
    }

    /// <summary>Null keeps the vanilla launch duration.</summary>
    internal double? LaunchSeconds(object? bay)
    {
        try { return _service.EffectiveFor(OwnerId(bay))?.LaunchSeconds; }
        catch (Exception error) { ReportOnce("launch", error); return null; }
    }

    /// <summary>Null keeps the vanilla replacement roll.</summary>
    internal object? ReplacementPrefab(object? bay, int index)
    {
        try
        {
            if (_service.EffectiveFor(OwnerId(bay))?.ReplacementDrones is not { } names) return null;
            for (var offset = 0; offset < names.Count; offset++)
            {
                var name = names[(Math.Abs(index) + offset) % names.Count];
                if (_droneGet.Invoke(null, new object[] { name }) is { } prefab) return prefab;
                if (_reported.Add("drone '" + name + "'"))
                    try { _report(new InvalidOperationException("The game has no drone '" + name + "'; the authored replacement roll skips it.")); } catch { }
            }
            return null; // Every authored name unknown: the vanilla roll proceeds.
        }
        catch (Exception error) { ReportOnce("replacement", error); return null; }
    }

    internal void Tick(double now)
    {
        if (now < _due) return;
        _due = now + Cadence;
        try
        {
            var session = _hub.CurrentSession is { } current && current.Phase == SessionPhase.GameplayInitialized ? current.Id : (Guid?)null;
            if (session != _session) { _builds.Clear(); _session = session; }
            if (session == null) return;
            foreach (var (unitId, complement) in _service.ComplementTargets())
                try { Build(unitId, complement); }
                catch (Exception error) { ReportOnce("complement " + unitId, error); }
        }
        catch (Exception error) { ReportOnce("tick", error); }
    }

    private void Build(string unitId, int complement)
    {
        if (!_builds.TryGetValue(unitId, out var state)) _builds.Add(unitId, state = new BuildState());
        if (state.Done) return;
        if (Resolve(unitId) is not { } bay) return;
        if (state.Next < 0)
        {
            _droneAmount.SetValue(bay, complement);
            _bonusAmount.SetValue(bay, 0);
            var docked = (IList)_drones.GetValue(bay)!;
            var stale = new List<object?>();
            foreach (var drone in docked) stale.Add(drone);
            foreach (var drone in stale) if (drone != null) _destroyDrone(drone);
            docked.Clear();
            state.Next = 0;
        }
        var stop = Math.Min(complement, state.Next + Batch);
        for (; state.Next < stop; state.Next++)
            _addDrone.Invoke(bay, new object[] { state.Next });
        if (state.Next < complement) return; // Staggered: the next batch builds on a later tick.
        _shouldDeploy.SetValue(bay, true);
        state.Done = true;
        try { _notice("Built the authored complement of " + complement + " drones for unit '" + unitId + "'."); } catch { }
    }

    private object? Resolve(string unitId)
    {
        object? found = null;
        foreach (var ship in _ships())
        {
            if (ship == null || !_unit.IsInstanceOfType(ship)) continue;
            if (_unitData.GetValue(ship) is not { } data || _guid.GetValue(data) as string != unitId) continue;
            if (found != null) return null; // Ambiguous identity tunes nothing.
            found = ship;
        }
        return found == null ? null : _findBay(found);
    }

    private void ReportOnce(string identity, Exception error)
    {
        if (!_reported.Add(identity)) return;
        try { _report(error); } catch { }
    }
}
