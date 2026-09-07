using System;

namespace VGModAPI;

/// <summary>Optional interface on an acquired story provider for owner-scoped scripted progress.</summary>
public interface IStoryObjectiveProvider
{
    /// <summary>Sets absolute progress on the current live objective. Replayed values never increment progress.</summary>
    StoryTransitionResult SetProgress(Guid expectedSessionId, StoryObjectiveId objective, int progress);
    /// <summary>Reads the retained scripted objective in the requested session; unavailable is never zero progress.</summary>
    StoryObjectiveQuery Query(Guid expectedSessionId, StoryObjectiveId objective);
}

public sealed class StoryObjectiveQuery
{
    public StoryKnowledge Knowledge { get; }
    public int? Progress { get; }
    public int? Required { get; }
    public int? ContentRevision { get; }
    public string Diagnostic { get; }
    /// <summary>Retained terminal outcome, or null while unresolved. Temporary outcomes expire with their tombstones.</summary>
    public StoryOutcome? Outcome { get; }
    public StoryObjectiveQuery(StoryKnowledge knowledge, int? progress, int? required, int? contentRevision, string diagnostic)
        : this(knowledge, progress, required, contentRevision, diagnostic, null) { }
    public StoryObjectiveQuery(StoryKnowledge knowledge, int? progress, int? required, int? contentRevision, string diagnostic, StoryOutcome? outcome)
    { Knowledge = knowledge; Progress = progress; Required = required; ContentRevision = contentRevision; Diagnostic = diagnostic; Outcome = outcome; }
}

/// <summary>Stable objective identity within one provider-owned mission occurrence.</summary>
public readonly struct StoryObjectiveId : IEquatable<StoryObjectiveId>
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
