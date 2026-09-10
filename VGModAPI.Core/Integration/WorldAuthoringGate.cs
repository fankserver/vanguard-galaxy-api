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

    internal WorldSnapshotInstance? TryFind(WorldDefinitionRegistry.Provider provider, Guid session, string localId, Guid instanceId, Func<bool> availability)
    {
        if (!_definitions.TryResolve(provider, localId, out _)) return null;
        long revision = _definitions.Revision;
        if (!availability() || !_persistenceReady(session) || revision != _definitions.Revision || !_definitions.TryResolve(provider, localId, out var definition)) return null;
        var identity = new WorldObjectIdentity(new ContentDeclaration(definition!.Owner, localId, PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent), instanceId);
        var record = _creation.Find(session, identity);
        return record != null && _definitions.MatchesRetained(record.Definition) ? record : null;
    }
    /// <summary>Whether any retained combat-site record lives in the given system (dissolution guard). Null while the inventory is not ready to answer.</summary>
    internal bool? AnyInSystem(string systemId)
    {
        WorldSnapshotInstance[] records;
        try { records = _creation.Snapshot(); }
        catch { return null; }
        foreach (var record in records) if (record.SystemId == systemId) return true;
        return false;
    }

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
