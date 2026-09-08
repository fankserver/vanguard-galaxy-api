using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

/// <summary>Defers per-unit supplements until the enclosing simulation's compartment list exists.</summary>
internal sealed class DungeonCrewResumeHydrator
{
    private ConditionalWeakTable<object, DungeonCrewResumeState> _pending = new();
    private readonly DungeonCrewResumeAdapter _adapter;
    internal DungeonCrewResumeHydrator(DungeonCrewResumeAdapter adapter) { _adapter = adapter; }
    internal void Clear() => _pending = new();
    internal void Stage(object crew, DungeonCrewResumeState saved)
    { _pending.Add(crew, saved); }
    internal bool Apply(IEnumerable<object> units, int compartmentCount)
    {
        var pending = new List<(object Crew, DungeonCrewResumeState State)>();
        var count = 0;
        foreach (var crew in units)
        {
            if (++count > 4096) throw new InvalidOperationException("Crew restore exceeds simulation bound.");
            if (_pending.TryGetValue(crew, out var state)) pending.Add((crew, state));
        }
        if (pending.Any(item => !item.State.Fits(compartmentCount))) return false;
        foreach (var item in pending) _adapter.Restore(item.Crew, item.State, compartmentCount);
        foreach (var item in pending) _pending.Remove(item.Crew);
        return true;
    }
}
