using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

namespace VGModAPI.Core;

/// <summary>Exact-reference lifetime protection. Persistent owned sites are never removed by ambient native cleanup.</summary>
internal sealed class WorldLifetimeGuard
{
    private sealed class Entry
    {
        internal readonly Guid Session;
        internal readonly string NativeId;
        internal Entry(Guid session, string nativeId) { Session = session; NativeId = nativeId; }
    }
    private readonly ConditionalWeakTable<object, Entry> _known = new();
    private readonly ConditionalWeakTable<object, object> _reserved = new();
    private Dictionary<string, object> _current = new(StringComparer.Ordinal);
    private Guid _session;
    private bool _ready;
    private bool _stopped;

    internal void Start(Guid session)
    {
        if (_stopped) throw new InvalidOperationException("World lifetime guard requires process restart.");
        if (session == Guid.Empty) throw new ArgumentException("Observed session required.", nameof(session));
        _session = session; _ready = false; _current = new Dictionary<string, object>(StringComparer.Ordinal);
    }
    internal void Track(Guid session, object poi, WorldObjectIdentity identity)
    {
        if (poi == null || identity == null) throw new ArgumentNullException("World object and identity required.");
        _reserved.GetValue(poi, _ => new object());
        if (_stopped || session != _session || session == Guid.Empty) throw new InvalidDataException("Stale world object registration.");
        if (_known.TryGetValue(poi, out var known))
        {
            if (known.Session == session && known.NativeId == identity.NativeId && _current.TryGetValue(identity.NativeId, out var existing) && ReferenceEquals(existing, poi)) return;
            throw new InvalidDataException("World reference was already bound to another instance/session.");
        }
        if (_current.Count >= WorldSerializationAssociation.MaxObjects || _current.ContainsKey(identity.NativeId)) throw new InvalidDataException("Duplicate or excessive live world instances.");
        _known.Add(poi, new Entry(session, identity.NativeId)); _current.Add(identity.NativeId, poi);
    }
    internal void Ready(Guid session)
    {
        if (_stopped || session == Guid.Empty || session != _session) throw new InvalidDataException("Stale world readiness.");
        _ready = true;
    }
    private bool Owned(object poi, string nativeId)
    {
        if (WorldObjectIdentity.IsReserved(nativeId)) _reserved.GetValue(poi, _ => new object());
        return _known.TryGetValue(poi, out _) || _reserved.TryGetValue(poi, out _);
    }
    internal bool AllowAmbient(Guid session, object poi, string nativeId)
    {
        if (!Owned(poi, nativeId)) return true;
        return !_stopped && _ready && session == _session && _known.TryGetValue(poi, out var entry) &&
            entry.Session == session && entry.NativeId == nativeId && _current.TryGetValue(nativeId, out var current) && ReferenceEquals(current, poi);
    }
    internal bool AllowNativeRemoval(object poi, string nativeId) => !Owned(poi, nativeId);
    internal void Invalidate() { _ready = false; _session = Guid.Empty; _current.Clear(); }
    internal void Stop() { _stopped = true; Invalidate(); }
}
