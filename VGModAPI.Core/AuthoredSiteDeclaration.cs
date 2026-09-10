using System;

namespace VGModAPI.Core;

/// <summary>Immutable declarative authored-site properties, separate from mutable serialized native state.</summary>
internal sealed class AuthoredSiteDeclaration
{
    internal string LocalId { get; }
    internal int Revision { get; }
    internal string Name { get; }
    internal AuthoredSiteKind Kind { get; }
    internal int Level { get; }
    internal string? FactionId { get; }
    internal string? WreckShipId { get; }
    internal bool WithStation { get; }
    internal AuthoredSiteHazard? Hazard { get; }
    internal bool ScatterAsteroids { get; }
    internal int AsteroidCount { get; }
    internal AuthoredSiteDeclaration(AuthoredSiteDefinition definition)
    {
        if (definition == null) throw new ArgumentNullException(nameof(definition));
        _ = new ContentDeclaration("vgmodapi.world", definition.LocalId, PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent);
        if (string.IsNullOrWhiteSpace(definition.Name) || definition.Name.IndexOf('\0') >= 0 || WorldStateCodec.TextByteCount(definition.Name) > 1024)
            throw new ArgumentException("A bounded world display name is required.");
        if (definition.Kind == AuthoredSiteKind.SalvageSite)
        {
            if (string.IsNullOrWhiteSpace(definition.WreckShipId) || WorldStateCodec.TextByteCount(definition.WreckShipId!) > 128)
                throw new ArgumentException("A bounded exact wreck ship class is required.");
            _ = new ContentDeclaration("vgmodapi.world", definition.FactionId!, PersistentContentKind.Faction, ContentPersistenceImpact.ApiDependent);
        }
        LocalId = definition.LocalId; Revision = definition.Revision; Name = definition.Name; Kind = definition.Kind;
        Level = definition.Level; FactionId = definition.FactionId; WreckShipId = definition.WreckShipId;
        WithStation = definition.WithStation; Hazard = definition.Hazard;
        ScatterAsteroids = definition.ScatterAsteroids; AsteroidCount = definition.AsteroidCount;
    }
    internal AuthoredSiteDefinition ToDefinition() => Kind == AuthoredSiteKind.SalvageSite
        ? AuthoredSiteDefinition.Salvage(LocalId, Revision, Name, Level, WreckShipId!, FactionId!, WithStation, Hazard, ScatterAsteroids)
        : AuthoredSiteDefinition.MiningField(LocalId, Revision, Name, Level, AsteroidCount);
}

/// <summary>
/// One persisted authored-site occurrence row: retained declarative identity plus the fresh per-game
/// native references owned by the occurrence. Nothing executable or callback-shaped is stored.
/// </summary>
internal sealed class AuthoredSiteOccurrence
{
    internal string Owner { get; }
    internal string LocalId { get; }
    internal string OccurrenceKey { get; }
    internal int Revision { get; private set; }
    internal AuthoredSiteKind Kind { get; }
    internal string SystemId { get; }
    internal string PoiId { get; }
    internal AuthoredSiteOccurrence(string owner, string localId, string occurrenceKey, int revision,
        AuthoredSiteKind kind, string systemId, string poiId)
    {
        if (string.IsNullOrWhiteSpace(owner) || WorldStateCodec.TextByteCount(owner) > 128) throw new ArgumentException("A bounded owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(localId) || WorldStateCodec.TextByteCount(localId) > 128) throw new ArgumentException("A bounded local identity is required.", nameof(localId));
        if (string.IsNullOrWhiteSpace(occurrenceKey) || WorldStateCodec.TextByteCount(occurrenceKey) > 256) throw new ArgumentException("A bounded occurrence key is required.", nameof(occurrenceKey));
        if (revision < 1) throw new ArgumentOutOfRangeException(nameof(revision));
        if (!Enum.IsDefined(typeof(AuthoredSiteKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (string.IsNullOrWhiteSpace(systemId) || WorldStateCodec.TextByteCount(systemId) > 128) throw new ArgumentException("A bounded native system identity is required.", nameof(systemId));
        if (string.IsNullOrWhiteSpace(poiId) || WorldStateCodec.TextByteCount(poiId) > 128) throw new ArgumentException("A bounded native POI identity is required.", nameof(poiId));
        Owner = owner; LocalId = localId; OccurrenceKey = occurrenceKey; Revision = revision;
        Kind = kind; SystemId = systemId; PoiId = poiId;
    }
    internal void MigrateRevision(int revision)
    {
        if (revision < 1 || revision <= Revision) throw new ArgumentOutOfRangeException(nameof(revision));
        Revision = revision;
    }
}
