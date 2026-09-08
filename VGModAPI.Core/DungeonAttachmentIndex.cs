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
    private readonly Dictionary<Guid, WeakReference<object>> _occurrences = new();
    private readonly HashSet<Guid> _conflicts = new();
    internal bool Bind(object location, Guid occurrence)
    {
        if (location == null) throw new ArgumentNullException(nameof(location));
        if (occurrence == Guid.Empty || _conflicts.Contains(occurrence)) return false;
        if (_locations.TryGetValue(location, out var existing)) return existing.Id == occurrence;
        if (_occurrences.TryGetValue(occurrence, out var reference) && reference.TryGetTarget(out var other) && !ReferenceEquals(location, other))
        {
            // Neither copy of a duplicated save marker may execute authored effects.
            _locations.Add(location, new(occurrence)); _conflicts.Add(occurrence); return false;
        }
        _locations.Add(location, new(occurrence)); _occurrences[occurrence] = new(location); return true;
    }
    internal Guid? SavedMarker(object location) => _locations.TryGetValue(location, out var marker) ? marker.Id : null;
    internal Guid? Find(object location) => _locations.TryGetValue(location, out var marker) && !_conflicts.Contains(marker.Id) ? marker.Id : null;
    internal object? Resolve(Guid occurrence)
    {
        if (_conflicts.Contains(occurrence)) return null;
        return _occurrences.TryGetValue(occurrence, out var reference) && reference.TryGetTarget(out var location) ? location : null;
    }
    internal void Clear()
    { _locations = new(); _occurrences.Clear(); _conflicts.Clear(); }
}
