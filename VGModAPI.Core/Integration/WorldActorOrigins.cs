using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace VGModAPI.Core.Integration;

/// <summary>Spawn-time actor origins; later callbacks never infer ownership from the current manager.</summary>
internal sealed class WorldActorOrigins
{
    private sealed class Origin
    {
        internal readonly Func<bool> Valid;
        internal Origin(Func<bool> valid) => Valid = valid;
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
    private readonly ConditionalWeakTable<object, Origin> _actors = new();
    internal IDisposable Enter(Func<bool>? valid)
    {
        var scope = new Scope(this, valid == null ? null : new Origin(valid), _scope); _scope = scope; return scope;
    }
    internal void Capture(object actor)
    {
        var scope = _scope; var origin = scope?.Origin;
        if (origin == null) return;
        if (!origin.Valid() || !ReferenceEquals(scope, _scope)) throw new InvalidDataException("Actor spawn origin is no longer admitted.");
        if (_actors.TryGetValue(actor, out var previous))
        {
            if (!ReferenceEquals(previous, origin)) throw new InvalidDataException("Actor already belongs to a different spawn origin.");
        }
        else _actors.Add(actor, origin);
    }
    internal bool Known(object actor) => _actors.TryGetValue(actor, out _);
    internal bool Allow(object actor) => !_actors.TryGetValue(actor, out var origin) || origin.Valid();
}
