using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace VGModAPI.Core.Integration;

/// <summary>Spawn-time actor origins; later callbacks never infer ownership from the current manager.</summary>
internal sealed class WorldActorOrigins
{
    private sealed class Origin
    {
        internal readonly Func<bool> Valid;
        internal readonly object? Data;
        internal Origin(Func<bool> valid, object? data) { Valid = valid; Data = data; }
    }
    private sealed class Scope : IDisposable
    {
        private readonly WorldActorOrigins _owner;
        internal readonly Origin? Origin;
        private readonly Scope? _parent;
        private bool _disposed;
        internal Scope(WorldActorOrigins owner, Origin? origin, Scope? parent) { _owner = owner; Origin = origin; _parent = parent; }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            if (!ReferenceEquals(_owner._scope, this)) return;
            var parent = _parent; while (parent != null && parent._disposed) parent = parent._parent;
            _owner._scope = parent;
        }
    }
    private Scope? _scope;
    private sealed class Actor
    {
        internal readonly Origin Origin;
        internal bool Admitted, Rejected, Validating;
        internal Actor(Origin origin) => Origin = origin;
    }
    private readonly ConditionalWeakTable<object, Actor> _actors = new();
    private readonly List<WeakReference<object>> _tracked = new();
    internal object[] Snapshot()
    {
        var result = new List<object>();
        foreach (var weak in _tracked) if (weak.TryGetTarget(out var actor)) result.Add(actor);
        return result.ToArray();
    }
    internal IDisposable Enter(Func<bool>? valid, object? data = null)
    {
        var scope = new Scope(this, valid == null ? null : new Origin(valid, data), _scope); _scope = scope; return scope;
    }
    internal void Capture(object actor)
    {
        var scope = _scope; var origin = scope?.Origin;
        if (origin == null) return;
        if (_actors.TryGetValue(actor, out var entry))
        {
            if (!ReferenceEquals(entry.Origin, origin)) throw new InvalidDataException("Actor already belongs to a different spawn origin.");
        }
        else
        {
            entry = new Actor(origin); _actors.Add(actor, entry);
            if (_tracked.Count >= 10000) _tracked.RemoveAll(weak => !weak.TryGetTarget(out _));
            if (_tracked.Count >= 10000) { entry.Rejected = true; throw new InvalidDataException("Owned actor tracking limit reached."); }
            _tracked.Add(new WeakReference<object>(actor));
        }
        try
        {
            if (entry.Rejected || entry.Validating) throw new InvalidDataException("Actor capture is rejected or reentrant.");
            entry.Validating = true;
            if (!origin.Valid() || entry.Rejected || !ReferenceEquals(scope, _scope))
                throw new InvalidDataException("Actor spawn origin is no longer admitted.");
            entry.Admitted = true;
        }
        catch { entry.Admitted = false; entry.Rejected = true; throw; }
        finally { entry.Validating = false; }
    }
    internal bool Attach(object component, object source)
    {
        if (!_actors.TryGetValue(source, out var origin)) return !_actors.TryGetValue(component, out _);
        if (_actors.TryGetValue(component, out var prior)) return ReferenceEquals(prior, origin);
        _actors.Add(component, origin); return true;
    }
    internal bool MatchesData(object actor, object? data) => _actors.TryGetValue(actor, out var entry) &&
        entry.Origin.Data != null && ReferenceEquals(entry.Origin.Data, data);
    internal bool Known(object actor) => _actors.TryGetValue(actor, out _);
    internal bool Allow(object actor)
    {
        if (!_actors.TryGetValue(actor, out var entry)) return true;
        if (!entry.Admitted || entry.Rejected || entry.Validating) return false;
        entry.Validating = true;
        try
        {
            if (!entry.Origin.Valid() || !entry.Admitted || entry.Rejected) { entry.Rejected = true; return false; }
            return true;
        }
        catch { entry.Rejected = true; throw; }
        finally { entry.Validating = false; }
    }
}
