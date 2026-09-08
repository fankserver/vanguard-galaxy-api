using System;
using System.IO;

namespace VGModAPI.Core.Integration;

/// <summary>Preallocates instance bookkeeping before native append; commits without callbacks after append.</summary>
internal sealed class WorldCreationCoordinator
{
    private readonly WorldNativeAttachment _native;
    private readonly Action _checkThread;
    private WorldSnapshotInstance[] _instances = Array.Empty<WorldSnapshotInstance>();
    private Guid _session;
    private long _revision;
    private bool _creating;
    internal WorldCreationCoordinator(WorldNativeAttachment native, Action checkThread) { _native = native; _checkThread = checkThread; }
    internal long Revision { get { _checkThread(); return _revision; } }
    internal void Reset(Guid session)
    {
        _checkThread();
        _revision = checked(_revision + 1); _session = session; _instances = Array.Empty<WorldSnapshotInstance>();
    }
    internal WorldSnapshotInstance[] Snapshot()
    {
        _checkThread();
        if (_creating) throw new InvalidDataException("World creation is in progress; snapshot cannot capture partial state.");
        return (WorldSnapshotInstance[])_instances.Clone();
    }
    internal WorldSnapshotInstance? TryCreate(Guid session, WorldSavedDefinition definition, WorldObjectIdentity identity,
        string system, float x, float y, Func<bool> admission)
    {
        _checkThread();
        if (admission == null) throw new ArgumentNullException(nameof(admission));
        if (_creating || session == Guid.Empty || session != _session || _instances.Length >= WorldSerializationAssociation.MaxObjects) return null;
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
        _creating = true;
        try
        {
            var created = _native.TryAppend(session, definition, identity, system, x, y,
                () => admission() && session == _session && revision == _revision && ReferenceEquals(before, _instances),
                record =>
                {
                    prepared = new WorldSnapshotInstance[before.Length + 1];
                    Array.Copy(before, prepared, before.Length); prepared[before.Length] = record;
                });
            if (created == null) return null;
            // The concrete List<T> append invokes no callback after the final admission fence.
            // Prepared storage and the checked revision leave no fallible work in this commit.
            _instances = prepared!; _revision = committedRevision;
            return created;
        }
        finally { _creating = false; }
    }
}
