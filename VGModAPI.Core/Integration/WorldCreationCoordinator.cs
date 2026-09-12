using System;
using System.IO;

namespace VGModAPI.Core.Integration;

/// <summary>Preallocates occurrence bookkeeping before native append; commits without callbacks after append.</summary>
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
    // Fixed callback-free inspection only, not a provider extension callback.
    private readonly Action<object>? _profile;
    internal WorldCreationCoordinator(WorldNativeAttachment native, Action checkThread, WorldLifetimeGuard? lifetime = null, Action<object>? profile = null) { _native = native; _checkThread = checkThread; _lifetime = lifetime; _profile = profile; }
    internal bool HasRestoredInventory(Guid session) { _checkThread(); return _restored && session != Guid.Empty && session == _session; }
    internal bool Restored(Guid session) { _checkThread(); return !_creating && HasRestoredInventory(session); }
    internal long Revision { get { _checkThread(); return _revision; } }
    internal void Refuse(Guid session)
    {
        _checkThread();
        if (_session != session) return;
        _restored = false; _revision = checked(_revision + 1);
    }
    internal void Reset(Guid session)
    {
        _checkThread();
        _revision = checked(_revision + 1); _session = session; _restored = false; _instances = Array.Empty<WorldSnapshotInstance>();
    }
    internal bool TryRestore(Guid session, Func<WorldSnapshotInstance[]> reconstruct)
    {
        _checkThread();
        if (reconstruct == null) throw new ArgumentNullException(nameof(reconstruct));
        return TryRestorePrepared(session, () => new WorldRestorationPlan(reconstruct()));
    }
    internal bool TryRestorePrepared(Guid session, Func<WorldRestorationPlan> reconstruct)
    {
        _checkThread();
        if (reconstruct == null) throw new ArgumentNullException(nameof(reconstruct));
        if (_creating || _restored || session == Guid.Empty || session != _session) return false;
        long revision = _revision, nextRevision = checked(_revision + 1);
        _creating = true;
        try
        {
            var plan = reconstruct() ?? throw new InvalidDataException("Missing reconstruction plan.");
            var source = plan.Occurrences ?? throw new InvalidDataException("Missing reconstructed inventory.");
            if (source.Length > WorldSerializationAssociation.MaxObjects) throw new InvalidDataException("Reconstructed inventory exceeds bound.");
            var prepared = (WorldSnapshotInstance[])source.Clone();
            var ids = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            var references = new System.Runtime.CompilerServices.ConditionalWeakTable<object, object>();
            foreach (var occurrence in prepared)
            {
                if (occurrence == null || !ids.Add(occurrence.Identity.NativeId) || references.TryGetValue(occurrence.Native, out _))
                    throw new InvalidDataException("Invalid reconstructed occurrence inventory.");
                references.Add(occurrence.Native, new object());
            }
            var tracking = _lifetime?.PrepareTracking(session, System.Linq.Enumerable.Select(prepared, item => (item.Native, item.Identity)));
            if (session != _session || revision != _revision || (tracking != null && !tracking.Current)) return false;
            bool rollback = true; Exception? failure = null;
            try
            {
                plan.Apply();
                foreach (var record in prepared) _profile?.Invoke(record.Native);
                if (session != _session || revision != _revision || (tracking != null && !tracking.Current)) return false;
                tracking?.Commit();
                _instances = prepared; _revision = nextRevision; _restored = true;
                rollback = false; return true;
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                if (rollback)
                {
                    try { plan.Rollback(); }
                    catch (Exception cleanup) when (failure != null) { throw new AggregateException("World restoration and rollback failed.", failure, cleanup); }
                }
            }
        }
        finally { _creating = false; }
    }

    internal WorldSnapshotInstance? Find(Guid session, WorldObjectIdentity identity, bool observed = false)
    {
        _checkThread();
        if (_creating || !HasRestoredInventory(session)) return null;
        foreach (var record in _instances)
            if (record.Identity.NativeId == identity.NativeId) return _native.Contains(session, record, observed) ? record : null;
        return null;
    }
    internal WorldSnapshotInstance[] Snapshot()
    {
        _checkThread();
        if (_creating || !_restored) throw new InvalidDataException("World occurrence state is not ready for snapshot capture.");
        foreach (var record in _instances) _profile?.Invoke(record.Native);
        return (WorldSnapshotInstance[])_instances.Clone();
    }
    internal WorldSnapshotInstance? TryCreate(Guid session, WorldSavedDefinition definition, WorldObjectIdentity identity,
        string system, float x, float y, Func<bool> admission)
    {
        _checkThread();
        if (admission == null) throw new ArgumentNullException(nameof(admission));
        if (_creating || !_restored || session == Guid.Empty || session != _session || _instances.Length >= WorldSerializationAssociation.MaxObjects) return null;
        foreach (var occurrence in _instances)
        {
            if (occurrence.Identity.NativeId == identity.NativeId) return null;
            if (occurrence.Definition.Owner == definition.Owner && occurrence.Identity.LocalId == definition.Definition.LocalId)
            {
                var prior = occurrence.Definition.Definition; var next = definition.Definition;
                if (prior.Revision != next.Revision || prior.Name != next.Name || prior.FactionId != next.FactionId || prior.Level != next.Level)
                    throw new InvalidDataException("Live occurrences require explicit definition migration.");
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

    /// <summary>
    /// Removes the retained occurrence after a verified native removal and drops it from the retained
    /// inventory so no save record reconstructs it. The caller owns the key registry and provider lease.
    /// </summary>
    internal WorldRemoveOutcome TryRemove(Guid session, WorldObjectIdentity identity)
    {
        _checkThread();
        if (identity == null) throw new ArgumentNullException(nameof(identity));
        if (_creating || !HasRestoredInventory(session) || session != _session) return WorldRemoveOutcome.Missing;
        WorldSnapshotInstance? found = null;
        foreach (var occurrence in _instances)
            if (occurrence.Identity.NativeId == identity.NativeId) { found = occurrence; break; }
        if (found == null) return WorldRemoveOutcome.Missing;
        var outcome = _native.TryRemove(session, found);
        if (outcome != WorldRemoveOutcome.Removed) return outcome;
        var next = new WorldSnapshotInstance[_instances.Length - 1];
        int index = 0;
        foreach (var occurrence in _instances) if (!ReferenceEquals(occurrence, found)) next[index++] = occurrence;
        _instances = next;
        _revision = checked(_revision + 1);
        return WorldRemoveOutcome.Removed;
    }
}
