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
    internal sealed class PreparedTracking
    {
        private readonly WorldLifetimeGuard _owner;
        private readonly Guid _session;
        private readonly Dictionary<string, object> _before, _after;
        private bool _used;
        internal PreparedTracking(WorldLifetimeGuard owner, Guid session, Dictionary<string, object> before, Dictionary<string, object> after)
        { _owner = owner; _session = session; _before = before; _after = after; }
        internal bool Current => !_used && !_owner._stopped && _owner._session == _session && ReferenceEquals(_owner._current, _before);
        internal void Commit()
        {
            if (!Current) throw new InvalidDataException("Stale prepared world lifetime inventory.");
            _used = true; _owner._current = _after;
        }
    }
    internal PreparedTracking PrepareTracking(Guid session, IEnumerable<(object Native, WorldObjectIdentity Identity)> instances)
    {
        if (_stopped || session == Guid.Empty || session != _session) throw new InvalidDataException("Stale world lifetime preparation.");
        var before = _current;
        var after = new Dictionary<string, object>(before, StringComparer.Ordinal);
        int visited = 0;
        foreach (var item in instances)
        {
            if (++visited > WorldSerializationAssociation.MaxObjects) throw new InvalidDataException("Excessive world lifetime preparation.");
            var poi = item.Native; var identity = item.Identity;
            if (poi == null || identity == null) throw new InvalidDataException("Missing world lifetime identity.");
            _reserved.GetValue(poi, _ => new object());
            if (_known.TryGetValue(poi, out var known))
            {
                if (known.Session != session || known.NativeId != identity.NativeId)
                    throw new InvalidDataException("World reference was already bound to another instance/session.");
            }
            else _known.Add(poi, new Entry(session, identity.NativeId));
            if (after.TryGetValue(identity.NativeId, out var existing))
            {
                if (!ReferenceEquals(existing, poi)) throw new InvalidDataException("Duplicate live world identity.");
            }
            else
            {
                if (after.Count >= WorldSerializationAssociation.MaxObjects) throw new InvalidDataException("Excessive live world instances.");
                after.Add(identity.NativeId, poi);
            }
        }
        return new PreparedTracking(this, session, before, after);
    }
    internal void Track(Guid session, object poi, WorldObjectIdentity identity)
    {
        if (poi == null || identity == null) throw new ArgumentNullException("World object and identity required.");
        _reserved.GetValue(poi, _ => new object());
        PrepareTracking(session, new[] { (poi, identity) }).Commit();
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
