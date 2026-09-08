using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

namespace VGModAPI.Core;

/// <summary>A parsed node paired by the caller with decoded committed ownership metadata.</summary>
internal sealed class WorldConstructionNode
{
    internal object Json { get; }
    internal WorldObjectIdentity Identity { get; }
    internal string Digest { get; }
    internal WorldConstructionNode(object json, WorldObjectIdentity identity, string digest)
    { Json = json ?? throw new ArgumentNullException(nameof(json)); Identity = identity ?? throw new ArgumentNullException(nameof(identity)); Digest = digest; }
}

/// <summary>Pre-factory admission only. The adapter must establish decoded metadata, parsed-node provenance and provider revision before opening a load.</summary>
internal sealed class WorldConstructionGate
{
    private Guid _session;
    private long _providerRevision;
    private bool _ready;
    private ConditionalWeakTable<object, WorldConstructionNode> _nodes = new();
    private readonly ConditionalWeakTable<object, object> _ownedNodes = new();

    internal void Start(Guid session)
    {
        _ready = false;
        _nodes = new ConditionalWeakTable<object, WorldConstructionNode>();
        _session = session;
        if (session == Guid.Empty) throw new ArgumentException("An observed load session is required.", nameof(session));
    }

    internal void Open(Guid session, SnapshotAssociation? association, string canonicalPath, string nativeHash,
        long providerRevision, string[] availableProviders, WorldConstructionNode[] nodes)
    {
        if (session == Guid.Empty || session != _session) throw new InvalidDataException("Stale world load.");
        _ready = false;
        _nodes = new ConditionalWeakTable<object, WorldConstructionNode>();
        if (association == null || !association.Matches(canonicalPath, nativeHash))
            throw new InvalidDataException("World loading requires exact committed generation metadata.");
        if (availableProviders == null || nodes == null || nodes.Length > WorldSerializationAssociation.MaxObjects)
            throw new InvalidDataException("Invalid world load inventory.");
        var providers = new HashSet<string>(availableProviders, StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = new ConditionalWeakTable<object, WorldConstructionNode>();
        foreach (var node in nodes)
        {
            if (node == null || !providers.Contains(node.Identity.Owner) || !ids.Add(node.Identity.NativeId) || result.TryGetValue(node.Json, out _))
                throw new InvalidDataException("Unavailable provider or duplicate world identity/node.");
            // Reuse the generation hash validator without accepting arbitrary digest strings.
            _ = new SnapshotAssociation(canonicalPath, nativeHash, node.Digest, association.Campaign, association.Snapshot);
            result.Add(node.Json, node);
            _ownedNodes.GetValue(node.Json, _ => new object());
        }
        _nodes = result;
        _providerRevision = providerRevision;
        _ready = true;
    }

    internal void RequireFactory(Guid session, object json, string nativeId, string digest, long providerRevision)
    {
        if (json == null) throw new ArgumentNullException(nameof(json));
        bool known = _nodes.TryGetValue(json, out var node);
        if (!known && !_ownedNodes.TryGetValue(json, out _) && !WorldObjectIdentity.IsReserved(nativeId)) return;
        if (!_ready || session != _session || providerRevision != _providerRevision || !known ||
            !string.Equals(node!.Identity.NativeId, nativeId, StringComparison.Ordinal) || !string.Equals(node.Digest, digest, StringComparison.Ordinal))
            throw new InvalidDataException("Owned world construction lacks current verified generation admission.");
    }

    internal void Invalidate()
    {
        _ready = false;
        _session = Guid.Empty;
        // Retain weak node identities so stripping a reserved ID cannot turn a known owned node into vanilla.
    }
}
