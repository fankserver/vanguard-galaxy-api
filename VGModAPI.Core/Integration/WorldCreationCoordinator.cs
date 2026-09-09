using System;
using System.IO;

namespace VGModAPI.Core.Integration;

/// <summary>Preallocates instance bookkeeping before native append; commits without callbacks after append.</summary>
internal sealed class WorldCreationCoordinator
{
    private readonly WorldNativeAttachment _native;
    private readonly Action _checkThread;
    private readonly WorldLifetimeGuard? _lifetime;
    private WorldSnapshotInstance[] _instances = Array.Empty<WorldSnapshotInstance>();
    private Guid _session;
    private long _revision;
    private bool _creating;
    private bool _restored;
    internal WorldCreationCoordinator(WorldNativeAttachment native, Action checkThread, WorldLifetimeGuard? lifetime = null) { _native = native; _checkThread = checkThread; _lifetime = lifetime; }
    internal bool HasRestoredInventory(Guid session) { _checkThread(); return _restored && session != Guid.Empty && session == _session; }
    internal bool Restored(Guid session) { _checkThread(); return !_creating && HasRestoredInventory(session); }
    internal long Revision { get { _checkThread(); return _revision; } }
    internal void Reset(Guid session)
    {
        _checkThread();
        _revision = checked(_revision + 1); _session = session; _restored = false; _instances = Array.Empty<WorldSnapshotInstance>();
    }
    internal bool TryRestore(Guid session, Func<WorldSnapshotInstance[]> reconstruct)
    {
        _checkThread();
        if (reconstruct == null) throw new ArgumentNullException(nameof(reconstruct));
        if (_creating || _restored || session == Guid.Empty || session != _session) return false;
        long revision = _revision, nextRevision = checked(_revision + 1);
        _creating = true;
        try
        {
            var source = reconstruct() ?? throw new InvalidDataException("Missing reconstructed inventory.");
            if (source.Length > WorldSerializationAssociation.MaxObjects) throw new InvalidDataException("Reconstructed inventory exceeds bound.");
            var prepared = (WorldSnapshotInstance[])source.Clone();
            var ids = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            var references = new System.Runtime.CompilerServices.ConditionalWeakTable<object, object>();
            foreach (var instance in prepared)
            {
                if (instance == null || !ids.Add(instance.Identity.NativeId) || references.TryGetValue(instance.Native, out _))
                    throw new InvalidDataException("Invalid reconstructed instance inventory.");
                references.Add(instance.Native, new object());
            }
            var tracking = _lifetime?.PrepareTracking(session, System.Linq.Enumerable.Select(prepared, item => (item.Native, item.Identity)));
            if (session != _session || revision != _revision || (tracking != null && !tracking.Current)) return false;
            tracking?.Commit();
            _instances = prepared; _revision = nextRevision; _restored = true;
            return true;
        }
        finally { _creating = false; }
    }

    internal WorldSnapshotInstance[] Snapshot()
    {
        _checkThread();
        if (_creating || !_restored) throw new InvalidDataException("World instance state is not ready for snapshot capture.");
        return (WorldSnapshotInstance[])_instances.Clone();
    }
    internal WorldSnapshotInstance? TryCreate(Guid session, WorldSavedDefinition definition, WorldObjectIdentity identity,
        string system, float x, float y, Func<bool> admission)
    {
        _checkThread();
        if (admission == null) throw new ArgumentNullException(nameof(admission));
        if (_creating || !_restored || session == Guid.Empty || session != _session || _instances.Length >= WorldSerializationAssociation.MaxObjects) return null;
        foreach (var instance in _instances)
        {
            if (instance.Identity.NativeId == identity.NativeId) return null;
            if (instance.Definition.Owner == definition.Owner && instance.Identity.LocalId == definition.Definition.LocalId)
            {
                var prior = instance.Definition.Definition; var next = definition.Definition;
                if (prior.Revision != next.Revision || prior.Name != next.Name || prior.FactionId != next.FactionId || prior.Level != next.Level)
                    throw new InvalidDataException("Live instances require explicit definition migration.");
            }
        }
        long revision = _revision, committedRevision = checked(_revision + 1);
        var before = _instances;
        WorldSnapshotInstance[]? prepared = null;
        WorldLifetimeGuard.PreparedTracking? tracking = null;
        _creating = true;
        try
        {
            var created = _native.TryAppend(session, definition, identity, system, x, y,
                () => admission() && session == _session && revision == _revision && ReferenceEquals(before, _instances) && (tracking == null || tracking.Current),
                record =>
                {
                    prepared = new WorldSnapshotInstance[before.Length + 1];
                    Array.Copy(before, prepared, before.Length); prepared[before.Length] = record;
                    tracking = _lifetime?.PrepareTracking(session, new[] { (record.Native, record.Identity) });
                });
            if (created == null) return null;
            // The concrete List<T> append invokes no callback after the final admission fence.
            // Prepared storage and the checked revision leave no fallible work in this commit.
            tracking?.Commit();
            _instances = prepared!; _revision = committedRevision;
            return created;
        }
        finally { _creating = false; }
    }
}
