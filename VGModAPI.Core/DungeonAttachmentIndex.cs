using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace VGModAPI.Core;

/// <summary>Session-local native location identity. Saved markers can arrive before the API envelope is restored.</summary>
internal sealed class DungeonAttachmentIndex
{
    private sealed class Marker
    {
        internal readonly Guid Id;
        internal Marker(Guid id) { Id = id; }
    }
    private ConditionalWeakTable<object, Marker> _locations = new();
    private readonly Dictionary<Guid, WeakReference<object>> _dungeons = new();
    private readonly HashSet<Guid> _conflicts = new();
    internal bool Bind(object location, Guid dungeon)
    {
        if (location == null) throw new ArgumentNullException(nameof(location));
        if (dungeon == Guid.Empty || _conflicts.Contains(dungeon)) return false;
        if (_locations.TryGetValue(location, out var existing)) return existing.Id == dungeon;
        if (_dungeons.TryGetValue(dungeon, out var reference) && reference.TryGetTarget(out var other) && !ReferenceEquals(location, other))
        {
            // Neither copy of a duplicated save marker may execute authored effects.
            _locations.Add(location, new(dungeon)); _conflicts.Add(dungeon); return false;
        }
        _locations.Add(location, new(dungeon)); _dungeons[dungeon] = new(location); return true;
    }
    internal Guid? SavedMarker(object location) => _locations.TryGetValue(location, out var marker) ? marker.Id : null;
    internal Guid? Find(object location) => _locations.TryGetValue(location, out var marker) && !_conflicts.Contains(marker.Id) ? marker.Id : null;
    internal object? Resolve(Guid dungeon)
    {
        if (_conflicts.Contains(dungeon)) return null;
        return _dungeons.TryGetValue(dungeon, out var reference) && reference.TryGetTarget(out var location) ? location : null;
    }
    internal Guid? FindSavedMarker(Func<object, bool> matchesLocation)
    {
        foreach (var pair in _locations)
            if (matchesLocation(pair.Key)) return pair.Value.Id;
        return null;
    }
    /// <summary>Forgets an dungeon's session-local binding after its row is intentionally dropped.</summary>
    internal void Detach(Guid dungeon)
    {
        if (_dungeons.TryGetValue(dungeon, out var reference))
        {
            if (reference.TryGetTarget(out var location)) _locations.Remove(location);
            _dungeons.Remove(dungeon);
        }
        _conflicts.Remove(dungeon);
    }
    internal void Clear()
    { _locations = new(); _dungeons.Clear(); _conflicts.Clear(); }
}
