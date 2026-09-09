using System;

namespace VGModAPI.Core;

/// <summary>Immutable declarative Combat-site properties, separate from mutable serialized instance state.</summary>
internal sealed class WorldCombatDefinition
{
    internal string LocalId { get; }
    internal int Revision { get; }
    internal string Name { get; }
    internal string FactionId { get; }
    internal int Level { get; }
    internal WorldCombatDefinition(string localId, int revision, string name, string factionId, int level)
    {
        _ = new ContentDeclaration("vgmodapi.world", localId, PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent);
        _ = new ContentDeclaration("vgmodapi.world", factionId, PersistentContentKind.Faction, ContentPersistenceImpact.ApiDependent);
        if (revision < 1) throw new ArgumentOutOfRangeException(nameof(revision));
        if (level < 1 || level > 100000) throw new ArgumentOutOfRangeException(nameof(level));
        if (string.IsNullOrWhiteSpace(name) || name.IndexOf('\0') >= 0 || WorldStateCodec.TextByteCount(name) > 1024)
            throw new ArgumentException("A bounded world display name is required.", nameof(name));
        LocalId = localId; Revision = revision; Name = name; FactionId = factionId; Level = level;
    }
}
