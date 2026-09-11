using System;

namespace VGModAPI.Core;

/// <summary>Immutable declarative authored-pocket-system properties, separate from mutable serialized native state.</summary>
internal sealed class PocketSystemDeclaration
{
    internal string LocalId { get; }
    internal int Revision { get; }
    internal string Name { get; }
    internal PocketSystemPlacement Placement { get; }
    internal string? FactionId { get; }
    /// <summary>Name of the remote subsector this pocket allocates when OffMap; null uses the game's generated name.</summary>
    internal string? SectorName { get; }
    internal PocketSystemDeclaration(string localId, int revision, string name, PocketSystemPlacement placement = PocketSystemPlacement.OffMap, string? factionId = null, string? sectorName = null)
    {
        _ = new ContentDeclaration("vgmodapi.world", localId, PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent);
        if (revision < 1) throw new ArgumentOutOfRangeException(nameof(revision));
        if (string.IsNullOrWhiteSpace(name) || name.IndexOf('\0') >= 0 || WorldStateCodec.TextByteCount(name) > 1024)
            throw new ArgumentException("A bounded world display name is required.", nameof(name));
        if (sectorName != null && (string.IsNullOrWhiteSpace(sectorName) || sectorName.IndexOf('\0') >= 0 || WorldStateCodec.TextByteCount(sectorName) > 1024))
            throw new ArgumentException("A bounded subsector name is required when one is supplied.", nameof(sectorName));
        LocalId = localId; Revision = revision; Name = name; Placement = placement; FactionId = factionId; SectorName = sectorName;
    }
}
