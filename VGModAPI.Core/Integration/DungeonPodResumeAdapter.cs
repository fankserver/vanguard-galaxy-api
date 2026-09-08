using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

/// <summary>Copies live manifests and retains an independent identity on native pod data.</summary>
internal sealed class DungeonPodResumeAdapter
{
    private readonly DungeonPodPersistence _persistence;
    private readonly IBoardingTacticalNativeBindings _native;
    private ConditionalWeakTable<object, Identity> _ids = new();
    internal DungeonPodResumeAdapter(DungeonPodPersistence persistence, IBoardingTacticalNativeBindings native)
    { _persistence = persistence; _native = native; }
    private readonly Dictionary<Guid, WeakReference<object>> _objects = new();
    private readonly HashSet<Guid> _conflicts = new();
    private ConditionalWeakTable<object, object> _sources = new();
    internal void Clear() { _ids = new(); _sources = new(); _objects.Clear(); _conflicts.Clear(); }
    internal object? DataFor(Guid id) => !_conflicts.Contains(id) && _objects.TryGetValue(id, out var reference) && reference.TryGetTarget(out var data) ? data : null;
    internal void TrackLocation(object location)
    {
        if (_native.Get(location, "resumeLocationPods") is not System.Collections.IEnumerable pods) throw new InvalidOperationException("Missing location pod list.");
        foreach (var data in pods)
        {
            if (data == null) continue;
            if (_sources.TryGetValue(data, out var previous) && !ReferenceEquals(previous, location))
            { if (IdentityFor(data) is { } id) _conflicts.Add(id); continue; }
            if (!_sources.TryGetValue(data, out _)) _sources.Add(data, location);
        }
    }
    internal void DetachSource(object data)
    {
        if (!_sources.TryGetValue(data, out var location)) return;
        var pods = _native.Get(location, "resumeLocationPods") as System.Collections.IList ?? throw new InvalidOperationException("Missing source pod list.");
        pods.Remove(data); _sources.Remove(data);
    }
    internal bool Conflicted(Guid id) => _conflicts.Contains(id);
    internal Guid? IdentityFor(object data) => _ids.TryGetValue(data, out var identity) ? identity.Id : null;
    internal void Loaded(object data, Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("Missing pod identity.");
        if (_objects.TryGetValue(id, out var reference) && reference.TryGetTarget(out var existing) && !ReferenceEquals(existing, data)) _conflicts.Add(id);
        if (_ids.TryGetValue(data, out var previous) && previous.Id != id) { _conflicts.Add(previous.Id); _conflicts.Add(id); }
        _ids.Remove(data); _ids.Add(data, new(id)); _objects[id] = new(data);
    }
    internal bool Observe(object pod, Guid occurrence, string parentShipId, bool returnInitialized = false, DungeonPodTransport? transport = null)
    {
        if (!_persistence.CanMutate) return false;
        var data = _native.Get(pod, "resumePodData") ?? throw new InvalidOperationException("Pod data unavailable.");
        var savedId = IdentityFor(data);
        var id = savedId ?? Guid.NewGuid(); var previous = _persistence.Get(id);
        if (_conflicts.Contains(id) || (savedId.HasValue && previous == null)) return false;
        if (!Enum.TryParse<DungeonPodPhase>(_native.Get(data, "resumePodPhase")?.ToString(), out var phase)) throw new InvalidOperationException("Unknown pod phase.");
        var known = phase is DungeonPodPhase.Returning or DungeonPodPhase.Arrived;
        var manifest = known ? (returnInitialized ? _native.Get(pod, "resumeReturnCrew") as IReadOnlyDictionary<string, int> : previous?.ReturnManifestKnown == true ? previous.ReturnCrew : null) : null;
        if (known && manifest == null) return false;
        var state = new DungeonPodResumeState(id, occurrence, phase, _native.Get(data, "resumePodPlayer") is true,
            known, previous?.ReturnDelivered ?? false, manifest ?? new Dictionary<string, int>(), previous?.ReturnAttempted ?? false, parentShipId, transport ?? previous?.Transport);
        if (!_persistence.Track(state)) return false;
        if (!IdentityFor(data).HasValue) Loaded(data, id);
        return true;
    }
    internal DungeonPodTransport CaptureTransport(object pod, bool reinforcement, Func<object, (float X, float Y)> vector, string donorShipId)
    {
        var data = _native.Get(pod, "resumePodData") ?? throw new InvalidOperationException("Missing pod data.");
        var position = vector(_native.Get(data, "resumePosition")!);
        var hull = vector(_native.Get(data, "resumeHullOffset")!);
        var target = vector(_native.Get(data, "resumeTargetPosition")!);
        var attachment = vector(_native.Get(data, "resumeAttachmentOffset")!);
        return new DungeonPodTransport((string)_native.Get(data, "resumePodId")!, reinforcement,
            (IReadOnlyDictionary<string, int>)_native.Get(data, "resumePodCrew")!,
            new[] { position.X, position.Y, (float)_native.Get(data, "resumeAngle")!, hull.X, hull.Y, target.X, target.Y, attachment.X, attachment.Y }, donorShipId);
    }
    internal bool RestoreReturnManifest(object pod, string parentShipId)
    {
        if (!_persistence.CanMutate) return false;
        var data = _native.Get(pod, "resumePodData"); if (data == null) return false;
        var id = IdentityFor(data); if (!id.HasValue || _conflicts.Contains(id.Value)) return false;
        var state = _persistence.Get(id.Value);
        if (state == null || !state.CanRecover || string.IsNullOrEmpty(state.ParentShipId) || state.ParentShipId != parentShipId) return false;
        if ((_native.Get(data, "resumePodPlayer") is true) != state.PlayerOwned ||
            _native.Get(data, "resumePodPhase")?.ToString() != state.Phase.ToString()) return false;
        _native.Set(pod, "resumeReturnCrew", new Dictionary<string, int>(state.ReturnCrew, StringComparer.Ordinal));
        return true;
    }
    private sealed class Identity
    {
        internal readonly Guid Id;
        internal Identity(Guid id) { Id = id; }
    }
}
