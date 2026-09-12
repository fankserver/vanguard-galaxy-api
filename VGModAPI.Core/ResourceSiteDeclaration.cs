using System;

namespace VGModAPI.Core;

/// <summary>Immutable declarative authored-site properties, separate from mutable serialized native state.</summary>
internal sealed class ResourceSiteDeclaration
{
    internal string LocalId { get; }
    internal int Revision { get; }
    internal string Name { get; }
    internal ResourceSiteKind Kind { get; }
    internal int Level { get; }
    internal string? FactionId { get; }
    internal string? WreckShipId { get; }
    internal bool WithStation { get; }
    internal ResourceSiteHazard? Hazard { get; }
    internal bool ScatterAsteroids { get; }
    internal int AsteroidCount { get; }
    internal ResourceSiteDeclaration(ResourceSiteDefinition definition)
    {
        if (definition == null) throw new ArgumentNullException(nameof(definition));
        _ = new PersistentDeclaration("vgmodapi.world", definition.LocalId, PersistentKind.WorldObject, PersistenceImpact.ApiDependent);
        if (string.IsNullOrWhiteSpace(definition.Name) || definition.Name.IndexOf('\0') >= 0 || WorldStateCodec.TextByteCount(definition.Name) > 1024)
            throw new ArgumentException("A bounded world display name is required.");
        if (definition.Kind == ResourceSiteKind.SalvageSite)
        {
            if (string.IsNullOrWhiteSpace(definition.WreckShipId) || WorldStateCodec.TextByteCount(definition.WreckShipId!) > 128)
                throw new ArgumentException("A bounded exact wreck ship class is required.");
            _ = new PersistentDeclaration("vgmodapi.world", definition.FactionId!, PersistentKind.Faction, PersistenceImpact.ApiDependent);
        }
        LocalId = definition.LocalId; Revision = definition.Revision; Name = definition.Name; Kind = definition.Kind;
        Level = definition.Level; FactionId = definition.FactionId; WreckShipId = definition.WreckShipId;
        WithStation = definition.WithStation; Hazard = definition.Hazard;
        ScatterAsteroids = definition.ScatterAsteroids; AsteroidCount = definition.AsteroidCount;
    }
    internal ResourceSiteDefinition ToDefinition() => Kind == ResourceSiteKind.SalvageSite
        ? ResourceSiteDefinition.Salvage(LocalId, Revision, Name, Level, WreckShipId!, FactionId!, WithStation, Hazard, ScatterAsteroids)
        : ResourceSiteDefinition.MiningField(LocalId, Revision, Name, Level, AsteroidCount);
}

/// <summary>
/// One persisted authored-site poi row: retained declarative identity plus the fresh per-game
/// native references owned by the poi. Nothing executable or callback-shaped is stored.
/// </summary>
internal sealed class ResourceSitePoi
{
    internal string Owner { get; }
    internal string LocalId { get; }
    internal string PoiKey { get; }
    internal int Revision { get; private set; }
    internal ResourceSiteKind Kind { get; }
    internal string SystemId { get; }
    internal string PoiId { get; }
    internal ResourceSitePoi(string owner, string localId, string poiKey, int revision,
        ResourceSiteKind kind, string systemId, string poiId)
    {
        if (string.IsNullOrWhiteSpace(owner) || WorldStateCodec.TextByteCount(owner) > 128) throw new ArgumentException("A bounded owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(localId) || WorldStateCodec.TextByteCount(localId) > 128) throw new ArgumentException("A bounded local identity is required.", nameof(localId));
        if (string.IsNullOrWhiteSpace(poiKey) || WorldStateCodec.TextByteCount(poiKey) > 256) throw new ArgumentException("A bounded poi key is required.", nameof(poiKey));
        if (revision < 1) throw new ArgumentOutOfRangeException(nameof(revision));
        if (!Enum.IsDefined(typeof(ResourceSiteKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (string.IsNullOrWhiteSpace(systemId) || WorldStateCodec.TextByteCount(systemId) > 128) throw new ArgumentException("A bounded native system identity is required.", nameof(systemId));
        if (string.IsNullOrWhiteSpace(poiId) || WorldStateCodec.TextByteCount(poiId) > 128) throw new ArgumentException("A bounded native POI identity is required.", nameof(poiId));
        Owner = owner; LocalId = localId; PoiKey = poiKey; Revision = revision;
        Kind = kind; SystemId = systemId; PoiId = poiId;
    }
    internal void MigrateRevision(int revision)
    {
        if (revision < 1 || revision <= Revision) throw new ArgumentOutOfRangeException(nameof(revision));
        Revision = revision;
    }
}


/// <summary>
/// One persisted combat-site key row: the author-local poi key and the API-allocated
/// instance identity it derived. Pure association - creation/resolution stay on the combat-POI
/// pipeline; this row exists so keyed pois are enumerable and settle-reportable.
/// </summary>
internal sealed class CombatSiteKeyRow
{
    internal string Owner { get; }
    internal string LocalId { get; }
    internal string PoiKey { get; }
    internal Guid InstanceId { get; }
    internal CombatSiteKeyRow(string owner, string localId, string poiKey, Guid instanceId)
    {
        if (string.IsNullOrWhiteSpace(owner) || WorldStateCodec.TextByteCount(owner) > 128) throw new ArgumentException("A bounded owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(localId) || WorldStateCodec.TextByteCount(localId) > 128) throw new ArgumentException("A bounded local identity is required.", nameof(localId));
        if (string.IsNullOrWhiteSpace(poiKey) || WorldStateCodec.TextByteCount(poiKey) > 256) throw new ArgumentException("A bounded poi key is required.", nameof(poiKey));
        if (instanceId == Guid.Empty) throw new ArgumentException("An instance identity is required.", nameof(instanceId));
        Owner = owner; LocalId = localId; PoiKey = poiKey; InstanceId = instanceId;
    }
}
