using System;

namespace VGModAPI.Core.Integration;

/// <summary>Prepared records and reversible native writes; publication is owned by the coordinator.</summary>
internal sealed class WorldRestorationPlan
{
    internal WorldSnapshotInstance[] Occurrences { get; }
    private readonly Action? _apply, _rollback;
    internal WorldRestorationPlan(WorldSnapshotInstance[] occurrences, Action? apply = null, Action? rollback = null)
    { Occurrences = occurrences; _apply = apply; _rollback = rollback; }
    internal void Apply() => _apply?.Invoke();
    internal void Rollback() => _rollback?.Invoke();
}
