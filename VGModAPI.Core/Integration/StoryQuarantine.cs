using System;
using System.Collections.Generic;
using System.Linq;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

/// <summary>
/// What the native guards call. It answers one question per entry point — may this mission, or this
/// objective, advance? — and answers it fail-closed for anything this API could have written that
/// nobody currently vouches for.
///
/// Objectives are recognised by object IDENTITY, gathered from the quarantined missions themselves,
/// so no objective of a vanilla or another mod's mission is ever affected. The set is rebuilt when
/// admissions change, which is when an orphan can appear: at a session boundary, at restore, or when
/// the owning module suspends.
/// </summary>
internal sealed class StoryQuarantine
{
    private readonly StoryProtectionGuard _guard;
    private readonly StoryProtection _protection;
    private readonly Func<IEnumerable<object>> _heldMissions;
    private readonly Action<Exception>? _fault;
    private readonly HashSet<object> _objectives = new(ReferenceComparer.Instance);
    private long _built;

    internal StoryQuarantine(StoryProtectionGuard guard, StoryProtection protection,
        Func<IEnumerable<object>> heldMissions, Action<Exception>? fault = null)
    {
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _protection = protection ?? throw new ArgumentNullException(nameof(protection));
        _heldMissions = heldMissions ?? throw new ArgumentNullException(nameof(heldMissions));
        _fault = fault;
    }

    /// <summary>
    /// True when this mission must not progress. A guard that cannot decide treats the mission as
    /// quarantined ONLY if it is one of ours; anything else is left alone, because refusing another
    /// mod's or the game's own content would be its own kind of damage.
    /// </summary>
    internal bool Blocks(object? mission)
    {
        if (mission == null) return false;
        try { return _protection.IsQuarantined(_guard.StoryId(mission)); }
        catch (Exception error) { Report(error); return _guard.IsMission(mission); }
    }

    internal bool BlocksObjective(object? objective)
    {
        if (objective == null) return false;
        try
        {
            Refresh();
            return _objectives.Contains(objective);
        }
        catch (Exception error) { Report(error); return false; }
    }

    private void Refresh()
    {
        if (_built == _protection.Version) return;
        _built = _protection.Version;
        _objectives.Clear();
        foreach (var mission in _heldMissions() ?? Array.Empty<object>())
        {
            if (!Blocks(mission)) continue;
            foreach (var objective in _guard.Objectives(mission)) _objectives.Add(objective);
        }
    }

    private void Report(Exception error) => _fault?.Invoke(error);

    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        internal static readonly ReferenceComparer Instance = new();
        public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);
        public int GetHashCode(object value) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
    }
}
