using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

/// <summary>Tracks native transports independently of manager operation lifetime without owning them.</summary>
internal sealed class DungeonLiveTransportIndex
{
    private readonly Func<object, bool> _alive;
    private readonly Dictionary<Guid, WeakReference<object>> _pods = new();
    internal DungeonLiveTransportIndex(Func<object, bool> alive) => _alive = alive;
    internal void Track(Guid id, object pod)
    {
        if (_pods.TryGetValue(id, out var reference) && reference.TryGetTarget(out var prior) && _alive(prior) && !ReferenceEquals(prior, pod))
            throw new InvalidOperationException("Duplicate live transport identity.");
        _pods[id] = new(pod);
    }
    internal void Checkpoint(Action<Guid, object> capture)
    {
        foreach (var pair in _pods.ToArray())
        {
            if (!pair.Value.TryGetTarget(out var pod) || !_alive(pod)) { _pods.Remove(pair.Key); continue; }
            capture(pair.Key, pod);
        }
    }
    internal void Clear() => _pods.Clear();
}
