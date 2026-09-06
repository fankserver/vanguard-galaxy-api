using System;
using System.Collections.Generic;
using System.Linq;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed partial class MissionAdapter
{
    internal sealed class Sweep
    {
        internal readonly object Player;
        internal readonly Guid Session;
        internal readonly MissionTransitions.Observation Observation;
        internal readonly (object Mission, string? Definition, string Name, string[] Tags)[] Before;
        internal bool Closed;
        internal Sweep(object player, Guid session, MissionTransitions.Observation observation, MissionBindings bindings)
        {
            Player = player; Session = session; Observation = observation;
            Before = bindings.Active(player).Select(m => (m, bindings.Definition(m), bindings.Name(m), bindings.Tags(m))).ToArray();
        }
    }
    private readonly List<Sweep> _sweeps = new();
    internal Sweep? BeginSweep(object? player = null)
    {
        if (!Current || (player != null && !ReferenceEquals(player, _player))) return null;
        var observation = Events.Begin();
        try
        {
            var sweep = new Sweep(_player!, _session!.Value, observation, _bindings);
            _sweeps.Add(sweep); return sweep;
        }
        catch { observation.Dispose(); throw; }
    }
    private void WitnessSweepInsertion(object mission)
    {
        if (_sweeps.Count == 0 || !_bindings.Contains(_player!, mission) || Events.HasActiveOccurrence(mission)) return;
        if (!_sweeps.Any(s => Events.WasRemovedSince(mission, s.Observation.Order) || !s.Before.Any(row => ReferenceEquals(row.Mission, mission)))) return;
        using var observation = Events.Begin();
        Events.Record(observation, mission, MissionTransitionKind.Accepted, new MissionFacts(false, true),
            _bindings.Definition(mission), _bindings.Name(mission), _bindings.Tags(mission));
    }
    internal void EndSweep(Sweep sweep)
    {
        if (sweep.Closed) return;
        try
        {
            if (!Current || sweep.Session != _session || !ReferenceEquals(sweep.Player, _player)) return;
            var after = _bindings.Active(sweep.Player);
            // End-time changes follow any nested, already-witnessed completion/failure.
            using var changes = Events.Begin();
            foreach (var old in sweep.Before)
                if (!after.Any(m => ReferenceEquals(m, old.Mission)) && !Events.WasRemoved(old.Mission))
                    Events.Record(changes, old.Mission, MissionTransitionKind.Removed, new MissionFacts(true, false), old.Definition, old.Name, old.Tags);
            foreach (var mission in after)
                if ((!sweep.Before.Any(row => ReferenceEquals(row.Mission, mission)) || Events.WasRemovedSince(mission, sweep.Observation.Order)) && !Events.HasActiveOccurrence(mission))
                    Events.Record(changes, mission, MissionTransitionKind.Accepted, new MissionFacts(false, true),
                        _bindings.Definition(mission), _bindings.Name(mission), _bindings.Tags(mission));
        }
        finally
        {
            int index = _sweeps.IndexOf(sweep);
            if (index >= 0)
            {
                foreach (var pending in _calls.Where(c => c.Observation.Order > sweep.Observation.Order)) pending.Closed = true;
                _calls.RemoveAll(c => c.Observation.Order > sweep.Observation.Order);
                foreach (var pending in _sweeps.Skip(index)) pending.Closed = true;
                _sweeps.RemoveRange(index, _sweeps.Count - index);
            }
            sweep.Closed = true; sweep.Observation.Dispose();
        }
    }
}
