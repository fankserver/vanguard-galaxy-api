using System;

namespace VGModAPI.Core;

internal sealed class WormholePairOccurrence
{
    internal string Owner { get; }
    internal string LocalId { get; }
    internal string OccurrenceKey { get; }
    internal int Revision { get; private set; }
    internal string FirstSystemId { get; }
    internal string SecondSystemId { get; }
    internal string FirstPoiId { get; }
    internal string SecondPoiId { get; }
    internal bool DeclaredOpen { get; set; }
    internal WormholePairOccurrence(string owner, string localId, string occurrenceKey, int revision,
        string firstSystemId, string secondSystemId, string firstPoiId, string secondPoiId, bool declaredOpen)
    {
        if (string.IsNullOrWhiteSpace(owner) || WorldStateCodec.TextByteCount(owner) > 128) throw new ArgumentException(nameof(owner));
        if (string.IsNullOrWhiteSpace(localId) || WorldStateCodec.TextByteCount(localId) > 128) throw new ArgumentException(nameof(localId));
        if (string.IsNullOrWhiteSpace(occurrenceKey) || WorldStateCodec.TextByteCount(occurrenceKey) > 256) throw new ArgumentException(nameof(occurrenceKey));
        if (revision < 1) throw new ArgumentOutOfRangeException(nameof(revision));
        foreach (var value in new[] { firstSystemId, secondSystemId, firstPoiId, secondPoiId })
            if (string.IsNullOrWhiteSpace(value) || WorldStateCodec.TextByteCount(value) > 128) throw new ArgumentException("A bounded native identity is required.");
        Owner = owner; LocalId = localId; OccurrenceKey = occurrenceKey; Revision = revision;
        FirstSystemId = firstSystemId; SecondSystemId = secondSystemId; FirstPoiId = firstPoiId; SecondPoiId = secondPoiId; DeclaredOpen = declaredOpen;
    }
    internal void MigrateRevision(int revision) { if (revision <= Revision) throw new ArgumentOutOfRangeException(nameof(revision)); Revision = revision; }
}
