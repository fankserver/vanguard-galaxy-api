using System;

namespace VGModAPI.Core.Integration;

/// <summary>Resolves declarations through exact authenticated leases before a scoped native creation.</summary>
internal sealed class WorldAuthoringGate
{
    private readonly WorldDefinitionRegistry _definitions;
    private readonly WorldCreationCoordinator _creation;
    private readonly Func<Guid, bool> _persistenceReady;
    internal WorldAuthoringGate(WorldDefinitionRegistry definitions, WorldCreationCoordinator creation, Func<Guid, bool> persistenceReady)
    { _definitions = definitions; _creation = creation; _persistenceReady = persistenceReady; }

    internal WorldSnapshotInstance? TryCreate(WorldDefinitionRegistry.Provider provider, Guid session, string localId,
        Guid instanceId, string systemId, float x, float y, Func<bool>? availability = null)
    {
        if (!_definitions.TryResolve(provider, localId, out var saved)) return null;
        long revision = _definitions.Revision;
        var identity = new WorldObjectIdentity(new ContentDeclaration(saved!.Owner, localId,
            PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent), instanceId);
        return _creation.TryCreate(session, saved, identity, systemId, x, y, () =>
            (availability?.Invoke() ?? true) && _persistenceReady(session) && _definitions.Revision == revision &&
            _definitions.TryResolve(provider, localId, out var current) &&
            ReferenceEquals(current!.Definition, saved.Definition));
    }
}
