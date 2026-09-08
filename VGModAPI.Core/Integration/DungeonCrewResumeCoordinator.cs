using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace VGModAPI.Runtime;

/// <summary>Holds malformed or incomplete restores before the first simulation tick.</summary>
internal sealed class DungeonCrewResumeCoordinator
{
    private readonly DungeonCrewResumeAdapter _adapter;
    private readonly DungeonCrewResumeHydrator _hydrator;
    private readonly DungeonCrewResumeJson _json;
    private readonly IBoardingTacticalNativeBindings _native;
    private readonly Action<Exception> _report;
    private readonly DungeonDirectiveAdapter? _directives;
    private readonly bool _execution;
    private ConditionalWeakTable<object, Exception> _invalid = new();
    internal DungeonCrewResumeCoordinator(IBoardingTacticalNativeBindings native, DungeonCrewResumeJson json, Action<Exception> report, DungeonDirectiveAdapter? directives = null, bool execution = false)
    { _execution = execution; _directives = directives; _native = native; _json = json; _report = report; _adapter = new(native); _hydrator = new(_adapter); }
    internal void Clear() { _invalid = new(); _hydrator.Clear(); }
    internal void SaveCrew(object crew, object json) => _json.Write(json, _adapter.Capture(crew));
    internal void LoadCrew(object crew, object json)
    {
        try { var saved = _json.Read(json); if (saved != null) _hydrator.Stage(crew, saved); }
        catch (Exception error) { Reject(crew, error); }
    }
    internal void SaveSimulation(object simulation, object json)
    {
        if (!CanTick(simulation)) throw new InvalidOperationException("Cannot serialize invalid simulation.");
        if (_execution)
        {
            var saved = new VGModAPI.Core.DungeonSimulationExecutionState((float)_native.Get(simulation, "resumeExplosionTimer")!, (IEnumerable<int>)_native.Get(simulation, "resumeVentTargets")!);
            saved.ValidateRooms(((ICollection)_native.Get(simulation, "compartments")!).Count);
            _json.WriteExecution(json, saved);
        }
        if (_directives == null) return;
        var units = new List<object>();
        foreach (var key in new[] { "friendlyUnits", "hostileUnits" })
            foreach (var crew in (IEnumerable)_native.Get(simulation, key)!)
            { if (crew == null || units.Count >= 4096) throw new InvalidOperationException("Invalid crew list."); units.Add(crew); }
        _json.WriteDirectives(json, _directives.Capture((IEnumerable)_native.Get(simulation, "pendingDirectives")!, units));
    }
    internal bool LoadSimulation(object simulation, object? json = null)
    {
        try
        {
            if (!CanTick(simulation)) return false;
            var rooms = _native.Get(simulation, "compartments") as ICollection ?? throw new InvalidOperationException("Missing restored compartments.");
            var units = new List<object>();
            foreach (var key in new[] { "friendlyUnits", "hostileUnits" })
            {
                var list = _native.Get(simulation, key) as IEnumerable ?? throw new InvalidOperationException("Missing restored crew list.");
                foreach (var crew in list)
                {
                    if (crew == null || units.Count >= 4096) throw new InvalidOperationException("Invalid restored crew list.");
                    if (_invalid.TryGetValue(crew, out var error)) throw new InvalidOperationException("Crew supplement is invalid.", error);
                    units.Add(crew);
                }
            }
            if (_execution && _json.ReadExecution(json ?? throw new InvalidOperationException("Missing simulation snapshot.")) is { } execution)
            {
                execution.ValidateRooms(rooms.Count);
                _native.Set(simulation, "resumeExplosionTimer", execution.ExplosionTimer);
                _native.Set(simulation, "resumeVentTargets", new HashSet<int>(execution.VentTargets));
            }
            if (!_hydrator.Apply(units, rooms.Count)) throw new InvalidOperationException("Crew supplement refers to missing compartments.");
            if (_directives != null)
            {
                var directives = _json.ReadDirectives(json ?? throw new InvalidOperationException("Missing simulation snapshot."));
                if (directives != null) _directives.Restore((IList)_native.Get(simulation, "pendingDirectives")!, directives, units, rooms.Count);
            }
            return true;
        }
        catch (Exception error) { Reject(simulation, error); return false; }
    }
    internal bool CanTick(object simulation) => !_invalid.TryGetValue(simulation, out _);
    private void Reject(object target, Exception error)
    {
        if (_invalid.TryGetValue(target, out _)) return;
        _invalid.Add(target, error); try { _report(error); } catch { }
    }
}
