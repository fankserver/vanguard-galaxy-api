using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace VGModAPI;

public enum DungeonContentStatus
{
    Registered, Attached, ChoiceApplied, Unavailable, PersistenceUnavailable, StaleTarget,
    InvalidDefinition, DuplicateId, TargetInUse, MissingDefinition, MissingProvider,
    VersionMismatch, WrongPhase, InvalidChoice, AlreadyChosen, Vetoed, NativeFailure
}

public sealed class DungeonContentResult
{
    public DungeonContentStatus Status { get; }
    public Guid? OccurrenceId { get; }
    public string Detail { get; }
    public DungeonContentResult(DungeonContentStatus status, string detail, Guid? occurrenceId = null)
    {
        if (!Enum.IsDefined(typeof(DungeonContentStatus), status) || occurrenceId == Guid.Empty) throw new ArgumentException("Invalid dungeon result.");
        Status = status; Detail = detail ?? throw new ArgumentNullException(nameof(detail)); OccurrenceId = occurrenceId;
    }
}

/// <summary>Copied saved occurrence state. Identity survives reload; a runtime target handle does not.</summary>
public sealed class DungeonOccurrenceSnapshot
{
    public Guid Id { get; }
    public DungeonDefinitionId DefinitionId { get; }
    public int DefinitionVersion { get; }
    public IReadOnlyDictionary<string, string> Choices { get; }
    public DungeonOccurrenceSnapshot(Guid id, DungeonDefinitionId definitionId, int definitionVersion, IEnumerable<KeyValuePair<string, string>> choices)
    {
        if (id == Guid.Empty || definitionVersion < 1) throw new ArgumentException("Invalid occurrence.");
        Id = id; DefinitionId = definitionId ?? throw new ArgumentNullException(nameof(definitionId)); DefinitionVersion = definitionVersion;
        Choices = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(choices ?? throw new ArgumentNullException(nameof(choices)), StringComparer.Ordinal));
    }
}

/// <summary>Immutable choice request supplied to registered behavior; callbacks cannot replace its retained definition.</summary>
public sealed class DungeonChoiceContext
{
    public DungeonOccurrenceSnapshot Occurrence { get; }
    public string EventId { get; }
    public string ChoiceId { get; }
    public DungeonChoiceContext(DungeonOccurrenceSnapshot occurrence, string eventId, string choiceId)
    {
        Occurrence = occurrence ?? throw new ArgumentNullException(nameof(occurrence));
        DungeonLayoutIds.Check(eventId); DungeonLayoutIds.Check(choiceId); EventId = eventId; ChoiceId = choiceId;
    }
}

public interface IDungeonProvider : IDisposable
{
    IDisposable Register(string localId, DungeonDefinition definition, Func<DungeonChoiceContext, bool>? allowChoice = null);
    DungeonContentResult Attach(string localId, BoardingHandle target);
    DungeonContentResult Choose(Guid occurrenceId, string eventId, string choiceId);
    IReadOnlyList<DungeonOccurrenceSnapshot> GetOccurrences();
}

/// <summary>Authoring attaches persistent dungeon occurrences to supported existing targets; world/POI creation is a separate service.</summary>
public interface IDungeonContentService : IServiceStatus
{
    IDungeonProvider AcquireProvider(string pluginId);
}
