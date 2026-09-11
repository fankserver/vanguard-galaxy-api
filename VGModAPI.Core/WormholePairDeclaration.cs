using System;

namespace VGModAPI.Core;

/// <summary>Immutable declarative authored-pocket-system properties, separate from mutable serialized native state.</summary>
internal sealed class WormholePairDeclaration
{
    internal string LocalId { get; }
    internal int Revision { get; }
    internal string Name { get; }
    internal WormholePairDeclaration(string localId, int revision, string name)
    {
        _ = new ContentDeclaration("vgmodapi.world", localId, PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent);
        if (revision < 1) throw new ArgumentOutOfRangeException(nameof(revision));
        if (string.IsNullOrWhiteSpace(name) || name.IndexOf('\0') >= 0 || WorldStateCodec.TextByteCount(name) > 1024)
            throw new ArgumentException("A bounded world display name is required.", nameof(name));
        LocalId = localId; Revision = revision; Name = name;
    }
}
