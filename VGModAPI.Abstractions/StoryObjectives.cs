using System;

namespace VGModAPI;

/// <summary>Read-only objective progress. Unavailable progress is distinct from zero.</summary>
public sealed class StoryObjectiveQuery
{
    public StoryKnowledge Knowledge { get; }
    public int? Progress { get; }
    public int? Required { get; }
    public int? ContentRevision { get; }
    public string Diagnostic { get; }
    /// <summary>Retained terminal outcome, or null while unresolved. Temporary outcomes expire with their tombstones.</summary>
    public StoryOutcome? Outcome { get; }
    /// <summary>
    /// True when the session is known but this objective's authored destination no longer exists in
    /// the world. The API reports the broken destination and DECIDES NOTHING: only the owner knows
    /// whether the arc fails, the world is repaired, or the player is told in the mod's own terms.
    /// </summary>
    public bool DestinationLost { get; }
    public StoryObjectiveQuery(StoryKnowledge knowledge, int? progress, int? required, int? contentRevision, string diagnostic)
        : this(knowledge, progress, required, contentRevision, diagnostic, null) { }
    public StoryObjectiveQuery(StoryKnowledge knowledge, int? progress, int? required, int? contentRevision, string diagnostic, StoryOutcome? outcome, bool destinationLost = false)
    { Knowledge = knowledge; Progress = progress; Required = required; ContentRevision = contentRevision; Diagnostic = diagnostic; Outcome = outcome; DestinationLost = destinationLost; }
}

/// <summary>Stable objective identity within one provider-owned mission occurrence.</summary>
internal readonly struct StoryObjectiveId : IEquatable<StoryObjectiveId>
{
    public StoryContentId Definition { get; }
    public Guid OccurrenceId { get; }
    public string LocalKey { get; }

    public StoryObjectiveId(StoryContentId definition, Guid occurrenceId, string localKey)
    {
        if (definition.Provider == null) throw new ArgumentException("A definition identity is required.", nameof(definition));
        if (occurrenceId == Guid.Empty) throw new ArgumentException("An occurrence identity is required.", nameof(occurrenceId));
        if (!StoryContentId.IsValidSegment(localKey)) throw new ArgumentException("An objective key uses the story identity segment format.", nameof(localKey));
        Definition = definition;
        OccurrenceId = occurrenceId;
        LocalKey = localKey;
    }

    public bool Equals(StoryObjectiveId other) => Definition.Equals(other.Definition)
        && OccurrenceId == other.OccurrenceId && string.Equals(LocalKey, other.LocalKey, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is StoryObjectiveId other && Equals(other);
    public override int GetHashCode() => (Definition, OccurrenceId, LocalKey).GetHashCode();
    public static bool operator ==(StoryObjectiveId left, StoryObjectiveId right) => left.Equals(right);
    public static bool operator !=(StoryObjectiveId left, StoryObjectiveId right) => !left.Equals(right);
}
