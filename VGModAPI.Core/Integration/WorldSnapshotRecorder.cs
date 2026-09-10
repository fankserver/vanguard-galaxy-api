using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

namespace VGModAPI.Core.Integration;

internal sealed class WorldSnapshotInstance
{
    internal object Native { get; }
    internal WorldObjectIdentity Identity { get; }
    internal string SystemId { get; }
    internal WorldSavedDefinition Definition { get; }
    internal WorldSnapshotInstance(object native, WorldObjectIdentity identity, string systemId, WorldSavedDefinition definition)
    {
        Native = native ?? throw new ArgumentNullException(nameof(native));
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        if (identity == null || identity.Owner != definition.Owner || identity.LocalId != definition.Definition.LocalId)
            throw new InvalidDataException("World snapshot ownership mismatch.");
        _ = new WorldSavedObject(identity, systemId, new string('0', 64), definition.Definition.Revision);
        Identity = identity; SystemId = systemId;
    }
}

/// <summary>Pairs pre-serialization declarations with the actual serialized nodes, never later live state.</summary>
internal sealed class WorldSnapshotRecorder
{
    private sealed class Capture
    {
        internal readonly long Operation, Revision;
        internal readonly WorldSnapshotInstance[] Instances;
        internal readonly byte[] Definitions;
        internal Capture(long operation, long revision, WorldSnapshotInstance[] instances, byte[] definitions)
        { Operation = operation; Revision = revision; Instances = instances; Definitions = definitions; }
    }
    private readonly WorldJsonInspection _json;
    private readonly WorldSerializationAssociation _states = new(), _definitions = new(), _systems = new();
    private readonly Func<byte[]>? _authoredCapture;
    private ConditionalWeakTable<object, Capture> _captures = new();
    private long _operation;
    internal WorldSnapshotRecorder(WorldJsonInspection json, Func<byte[]>? authoredCapture = null)
    { _json = json; _authoredCapture = authoredCapture; }

    internal object Begin(long revision, IReadOnlyList<WorldSnapshotInstance> instances)
    {
        long operation = Next();
        var frozen = Copy(instances);
        var definitions = new Dictionary<(string, string), WorldSavedDefinition>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var references = new ConditionalWeakTable<object, object>();
        foreach (var instance in frozen)
        {
            if (!ids.Add(instance.Identity.NativeId) || references.TryGetValue(instance.Native, out _)) throw new InvalidDataException("Duplicate world snapshot instance.");
            references.Add(instance.Native, new object());
            var key = (instance.Definition.Owner, instance.Identity.LocalId);
            if (definitions.TryGetValue(key, out var prior))
            {
                var a = prior.Definition; var b = instance.Definition.Definition;
                if (a.Revision != b.Revision || a.Name != b.Name || a.FactionId != b.FactionId || a.Level != b.Level)
                    throw new InvalidDataException("Conflicting retained world declarations.");
            }
            else definitions.Add(key, instance.Definition);
        }
        var bytes = WorldDefinitionCodec.Encode(new List<WorldSavedDefinition>(definitions.Values).ToArray());
        if (operation != _operation) throw new InvalidDataException("Reentrant world snapshot capture.");
        var token = new object(); _captures.Add(token, new Capture(operation, revision, frozen, bytes)); return token;
    }

    internal bool Complete(object token, long revision, IReadOnlyList<WorldSnapshotInstance> current, object root, Action? validateBeforePublish = null)
    {
        if (token == null || !_captures.TryGetValue(token, out var capture)) return false;
        _captures.Remove(token);
        if (capture.Operation != _operation) return false;
        long operation = Next();
        // Only a current capture owns revocation; stale/foreign/replayed completions cannot
        // erase a newer association. A current recapture still revokes on validation failure.
        _states.Forget(root); _definitions.Forget(root);
        if (capture.Revision != revision) return false;
        var observed = Copy(current);
        if (observed.Length != capture.Instances.Length) return false;
        for (int i = 0; i < observed.Length; i++) if (!ReferenceEquals(observed[i], capture.Instances[i])) return false;
        var nodes = _json.Read(root, nativeSnapshot: true);
        if (nodes.Length != observed.Length) return false;
        var byId = new Dictionary<string, WorldParsedNode>(StringComparer.Ordinal);
        foreach (var node in nodes) byId.Add(node.NativeId, node);
        var rows = new WorldSavedObject[observed.Length]; var objects = new object[observed.Length];
        for (int i = 0; i < observed.Length; i++)
        {
            var instance = observed[i];
            if (!byId.TryGetValue(instance.Identity.NativeId, out var node) || node.SystemId != instance.SystemId) return false;
            rows[i] = new WorldSavedObject(instance.Identity, node.SystemId, node.Digest, instance.Definition.Definition.Revision);
            objects[i] = instance.Native;
        }
        var beforeValidation = WorldJsonInspection.DigestRoot(root);
        validateBeforePublish?.Invoke();
        if (operation != _operation || beforeValidation != WorldJsonInspection.DigestRoot(root)) return false;
        foreach (var node in nodes) node.ValidateAssets();
        foreach (var node in nodes) _json.StampOwnedPoi(node.Json, node.NativeId);
        for (int i = 0; i < observed.Length; i++)
        {
            var instance = observed[i]; var node = byId[instance.Identity.NativeId];
            rows[i] = new WorldSavedObject(instance.Identity, node.SystemId, WorldJsonInspection.Digest(node.Json), instance.Definition.Definition.Revision);
        }
        var state = WorldStateCodec.Encode(rows);
        _json.SealSnapshot(root, rows.Length != 0);
        var digest = WorldJsonInspection.DigestRoot(root);
        foreach (var node in nodes) node.ValidateAssets();
        if (operation != _operation) return false;
        var stateToken = _states.Begin(revision, state, objects);
        var definitionToken = _definitions.Begin(revision, capture.Definitions, objects);
        if (_authoredCapture != null)
        {
            // A new row KIND alongside the existing owned rows, inside the same sealed envelope:
            // authored occurrence rows never reach the Combat-POI read/write/reconstruct path.
            var authoredBytes = _authoredCapture() ?? throw new InvalidDataException("Missing authored-system capture.");
            var systemsToken = _systems.Begin(revision, authoredBytes, objects);
            return _states.Complete(stateToken, revision, objects, root, digest) &&
                _definitions.Complete(definitionToken, revision, objects, root, digest) &&
                _systems.Complete(systemsToken, revision, objects, root, digest);
        }
        return _states.Complete(stateToken, revision, objects, root, digest) &&
            _definitions.Complete(definitionToken, revision, objects, root, digest);
    }

    internal Dictionary<string, byte[]> ForStore(object root)
    {
        var digest = WorldJsonInspection.DigestRoot(root);
        var result = new Dictionary<string, byte[]>
        {
            [WorldStateCodec.Owner] = _states.ForStore(root, digest),
            [WorldDefinitionCodec.Owner] = _definitions.ForStore(root, digest)
        };
        if (_authoredCapture != null) result[AuthoredSystemStateCodec.Owner] = _systems.ForStore(root, digest);
        return result;
    }
    internal void Reset() { Next(); _captures = new(); _states.Reset(); _definitions.Reset(); if (_authoredCapture != null) _systems.Reset(); }
    private long Next() => _operation = checked(_operation + 1);
    private static WorldSnapshotInstance[] Copy(IReadOnlyList<WorldSnapshotInstance> instances)
    {
        if (instances == null) throw new ArgumentNullException(nameof(instances));
        int count = instances.Count;
        if (count < 0 || count > WorldSerializationAssociation.MaxObjects) throw new InvalidDataException("World snapshot count exceeds bound.");
        var result = new WorldSnapshotInstance[count];
        for (int i = 0; i < count; i++) result[i] = instances[i] ?? throw new InvalidDataException("Null world snapshot instance.");
        if (instances.Count != count) throw new InvalidDataException("World snapshot membership changed during capture.");
        return result;
    }
}
