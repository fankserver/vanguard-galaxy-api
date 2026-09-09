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
    private readonly Action? _validateAssets;
    internal void ValidateAssets() => _validateAssets?.Invoke();
    internal WorldConstructionNode(object json, WorldObjectIdentity identity, string digest, Action? validateAssets = null)
    { Json = json ?? throw new ArgumentNullException(nameof(json)); Identity = identity ?? throw new ArgumentNullException(nameof(identity)); Digest = digest; _validateAssets = validateAssets; }
}

/// <summary>Pre-factory admission only. The adapter must establish decoded metadata, parsed-node provenance and provider revision before opening a load.</summary>
internal sealed class WorldConstructionGate
{
    private Guid _session;
    private long _providerRevision;
    private bool _ready;
    private bool _inventoryRejected;
    private ConditionalWeakTable<object, WorldConstructionNode> _nodes = new();
    private readonly ConditionalWeakTable<object, object> _ownedNodes = new();

    internal void Start(Guid session)
    {
        _ready = false;
        _inventoryRejected = false;
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
        if (_inventoryRejected || availableProviders == null || nodes == null ||
            nodes.Length > WorldSerializationAssociation.MaxObjects || availableProviders.Length > WorldSerializationAssociation.MaxObjects)
        {
            // Do not traverse invalid inventories. Refuse all construction for this failed load,
            // including nodes whose owned markers could otherwise be stripped before observation.
            _inventoryRejected = true;
            throw new InvalidDataException("Invalid world load inventory.");
        }
        // Classification survives refusal; it does not confer permission to construct.
        foreach (var observed in nodes)
            if (observed != null) _ownedNodes.GetValue(observed.Json, _ => new object());
        if (association == null || !association.Matches(canonicalPath, nativeHash))
            throw new InvalidDataException("World loading requires exact committed generation metadata.");
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

    internal WorldConstructionNode? RequireFactory(Guid session, object json, string nativeId, string digest, long providerRevision)
    {
        if (json == null) throw new ArgumentNullException(nameof(json));
        if (_inventoryRejected) throw new InvalidDataException("World inventory rejection requires a new load attempt.");
        if (WorldObjectIdentity.IsReserved(nativeId)) _ownedNodes.GetValue(json, _ => new object());
        bool known = _nodes.TryGetValue(json, out var node);
        if (!known && !_ownedNodes.TryGetValue(json, out _) && !WorldObjectIdentity.IsReserved(nativeId)) return null;
        if (!_ready || session != _session || providerRevision != _providerRevision || !known ||
            !string.Equals(node!.Identity.NativeId, nativeId, StringComparison.Ordinal) || !string.Equals(node.Digest, digest, StringComparison.Ordinal))
            throw new InvalidDataException("Owned world construction lacks current verified generation admission.");
        node!.ValidateAssets();
        if (!_ready || session != _session || providerRevision != _providerRevision || !_nodes.TryGetValue(json, out var current) || !ReferenceEquals(current, node))
            throw new InvalidDataException("World admission changed during asset validation.");
        return node;
    }

    internal void Invalidate()
    {
        _ready = false;
        _session = Guid.Empty;
        // Retain weak node identities so stripping a reserved ID cannot turn a known owned node into vanilla.
    }
}
