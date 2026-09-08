using System;
using System.Collections;
using System.Collections.Generic;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed class DungeonDirectiveAdapter
{
    private readonly IBoardingTacticalNativeBindings _native;
    private readonly Func<object> _create;
    private readonly Func<string, int, object> _enum;
    internal DungeonDirectiveAdapter(IBoardingTacticalNativeBindings native, Func<object> create, Func<string, int, object> enumValue)
    { _native = native; _create = create; _enum = enumValue; }
    internal IReadOnlyList<DungeonDirectiveState> Capture(IEnumerable directives, IReadOnlyList<object> units)
    {
        var result = new List<DungeonDirectiveState>();
        foreach (var directive in directives)
        {
            if (result.Count >= 4096 || directive == null) throw new InvalidOperationException("Invalid native directive list.");
            var claimed = (bool)_native.Get(directive, "directiveClaimed")!;
            var unit = _native.Get(directive, "directiveUnit"); var slot = -1;
            if (claimed)
            {
                for (var i = 0; i < units.Count; i++) if (ReferenceEquals(unit, units[i])) { slot = i; break; }
                if (slot < 0) throw new InvalidOperationException("Directive claimant is outside the saved simulation.");
            }
            else if (unit != null) throw new InvalidOperationException("Unclaimed directive has a claimant.");
            result.Add(new((int)_native.Get(directive, "directiveTarget")!, Convert.ToInt32(_native.Get(directive, "directivePriority")),
                (string?)_native.Get(directive, "directiveCrew"), Convert.ToInt32(_native.Get(directive, "directiveFilter")), slot));
        }
        return result.AsReadOnly();
    }
    internal void Restore(IList destination, IReadOnlyList<DungeonDirectiveState> saved, IReadOnlyList<object> units, int compartments)
    {
        if (saved.Count > 4096 || units.Count > 4096) throw new InvalidOperationException("Directive restore exceeds bounds.");
        var created = new List<object>(); var claimed = new HashSet<int>();
        foreach (var entry in saved)
        {
            if (entry.Target >= compartments || entry.ClaimingSlot >= units.Count ||
                (entry.Claimed && (!claimed.Add(entry.ClaimingSlot) || (int)_native.Get(units[entry.ClaimingSlot], "resumeDirectiveTarget")! != entry.Target)))
                throw new InvalidOperationException("Directive claim does not match restored crew execution state.");
            var directive = _create();
            _native.Set(directive, "directiveTarget", entry.Target); _native.Set(directive, "directivePriority", _enum("priority", entry.Priority));
            _native.Set(directive, "directiveCrew", entry.RequiredCrew); _native.Set(directive, "directiveFilter", _enum("filter", entry.Filter));
            _native.Set(directive, "directiveClaimed", entry.Claimed); _native.Set(directive, "directiveUnit", entry.Claimed ? units[entry.ClaimingSlot] : null);
            created.Add(directive);
        }
        destination.Clear(); foreach (var directive in created) destination.Add(directive);
    }
}
