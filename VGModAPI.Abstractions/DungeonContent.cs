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
    public Guid? DungeonId { get; }
    public string Detail { get; }
    public DungeonContentResult(DungeonContentStatus status, string detail, Guid? dungeonId = null)
    {
        if (!Enum.IsDefined(typeof(DungeonContentStatus), status) || dungeonId == Guid.Empty) throw new ArgumentException("Invalid dungeon result.");
        Status = status; Detail = detail ?? throw new ArgumentNullException(nameof(detail)); DungeonId = dungeonId;
    }
}

/// <summary>Copied saved dungeon state. Identity survives reload; a runtime target handle does not.</summary>
public sealed class DungeonSnapshot
{
    public Guid Id { get; }
    public DungeonDefinitionId DefinitionId { get; }
    public int DefinitionVersion { get; }
    public IReadOnlyDictionary<string, string> Choices { get; }
    public DungeonSnapshot(Guid id, DungeonDefinitionId definitionId, int definitionVersion, IEnumerable<KeyValuePair<string, string>> choices)
    {
        if (id == Guid.Empty || definitionVersion < 1) throw new ArgumentException("Invalid dungeon.");
        Id = id; DefinitionId = definitionId ?? throw new ArgumentNullException(nameof(definitionId)); DefinitionVersion = definitionVersion;
        Choices = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(choices ?? throw new ArgumentNullException(nameof(choices)), StringComparer.Ordinal));
    }
}

/// <summary>Immutable choice request supplied to registered behavior; callbacks cannot replace its retained definition.</summary>
public sealed class DungeonChoiceContext
{
    public DungeonSnapshot Dungeon { get; }
    public string EventId { get; }
    public string ChoiceId { get; }
    public DungeonChoiceContext(DungeonSnapshot dungeon, string eventId, string choiceId)
    {
        Dungeon = dungeon ?? throw new ArgumentNullException(nameof(dungeon));
        DungeonLayoutIds.Check(eventId); DungeonLayoutIds.Check(choiceId); EventId = eventId; ChoiceId = choiceId;
    }
}

public interface IDungeonProvider : IDisposable
{
    /// <summary>Get a stable installation view, including before its native POI exists. Does not create or own the POI.</summary>
    IDungeonInstallation GetInstallation(string poiId);
    IDisposable Register(string localId, DungeonDefinition definition, Func<DungeonChoiceContext, bool>? allowChoice = null);
    DungeonContentResult Attach(string localId, BoardingHandle target);
    /// <summary>
    /// Attaches by supported persistent installation identity instead of a boarding target handle.
    /// Use the installation object obtained from this provider's <see cref="GetInstallation"/>; a
    /// foreign object is a programming error. The attachment resolves the one live boarding target
    /// currently belonging to that installation; while none (or more than one) is observed the
    /// result is <see cref="DungeonContentStatus.StaleTarget"/> - a temporary refusal, not a
    /// display-name match.
    /// </summary>
    DungeonContentResult Attach(string localId, IDungeonInstallation installation);
    DungeonContentResult Choose(Guid dungeonId, string eventId, string choiceId);
    IReadOnlyList<DungeonSnapshot> GetDungeons();
}

/// <summary>Authoring attaches persistent dungeon content to supported existing targets; world/POI creation is a separate service.</summary>
public interface IDungeonContentService : IServiceStatus
{
    /// <summary>Acquire once during plugin setup. Optional custom save data is a provider-lifetime
    /// dependency: actionable installation events wait until its mutation gate opens.</summary>
    IDungeonProvider AcquireProvider(string pluginId, ISaveDataRegistration? saveData = null);
}
