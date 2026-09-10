using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VGModAPI;

/// <summary>
/// Owner-scoped identity of one registered story definition: a stable provider (plugin) ID plus a
/// local ID the provider chooses. Two independently loaded mods may both use the local ID
/// <c>MissionX</c>; the pair is what identifies content, never the bare local ID.
/// </summary>
public readonly struct StoryContentId : IEquatable<StoryContentId>
{
    public string Provider { get; }
    public string LocalId { get; }

    public StoryContentId(string provider, string localId)
    {
        Provider = Validate(provider, nameof(provider));
        LocalId = Validate(localId, nameof(localId));
    }

    /// <summary>1–48 lowercase ASCII letters/digits/hyphens beginning with a letter. Never a path, alias or display name.</summary>
    public static bool IsValidSegment(string? value) => value != null && value.Length is > 0 and <= 48
        && value[0] is >= 'a' and <= 'z'
        && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static string Validate(string value, string name)
        => IsValidSegment(value) ? value : throw new ArgumentException("Story identity segments are 1-48 lowercase ASCII letters/digits/hyphens starting with a letter.", name);

    public bool Equals(StoryContentId other)
        => string.Equals(Provider, other.Provider, StringComparison.Ordinal) && string.Equals(LocalId, other.LocalId, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is StoryContentId other && Equals(other);
    public override int GetHashCode() => (Provider, LocalId).GetHashCode();
    public override string ToString() => Provider + "/" + LocalId;
    public static bool operator ==(StoryContentId left, StoryContentId right) => left.Equals(right);
    public static bool operator !=(StoryContentId left, StoryContentId right) => !left.Equals(right);
}

/// <summary>
/// The objective kinds this API supports today. Each maps to ONE inspected vanilla objective type,
/// because vanilla reconstructs objectives by unqualified type name from its own assembly: a
/// provider-defined objective type cannot round-trip through a vanilla save. Unsupported behaviour
/// stays provider logic; it is never smuggled in as an opaque payload.
/// </summary>
public enum StoryObjectiveKind { TravelToPoi, KillEnemies, CollectCredits, Scripted, DeliverItems }

/// <summary>
/// Identity of a faction this API may reference. It is the game's own faction identifier, passed as a
/// string so no vanilla type reaches this contract, and it is RESOLVED against the game's faction
/// registry when content is registered: an identifier the game does not know refuses registration
/// rather than producing a mission the game cannot serialize.
/// </summary>
public readonly struct StoryFactionId : IEquatable<StoryFactionId>
{
    public string Value { get; }
    public StoryFactionId(string value)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));
        if (value.Length is < 1 or > 64 || !value.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.'))
            throw new ArgumentException("A faction identity is 1-64 ASCII letters, digits, dots, hyphens or underscores.", nameof(value));
        Value = value;
    }
    public bool Equals(StoryFactionId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is StoryFactionId other && Equals(other);
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
    public override string ToString() => Value ?? "";
}

/// <summary>
/// Reward kinds this API supports today. Vanilla SKIPS an unresolvable reward with a log line, so
/// the API refuses unsupported rewards at registration instead of letting them disappear on load.
/// Item/reputation rewards need owner-scoped item/faction identities and are deliberately absent.
/// </summary>
public enum StoryRewardKind { Credits, Experience, Reputation }

/// <summary>Mirrors the inspected vanilla mission difficulty names; the API never invents its own scale.</summary>
public enum StoryDifficulty { Easy, Normal, Hard, VeryHard }

/// <summary>
/// How much the API must retain for a definition. <see cref="Temporary"/> keeps only what is needed
/// to reconstruct offered/active content plus a bounded idempotency tombstone; <see cref="Campaign"/>
/// additionally retains queryable authoritative outcomes and declared choices. Neither retains
/// narrative history: that stays optional provider data.
/// </summary>
public enum StoryRetention { Temporary, Campaign }

/// <summary>Terminal state of one occurrence. Removal without a terminal proof is not an outcome.</summary>
public enum StoryOutcome { Completed, Failed, Abandoned }

/// <summary>One supported objective with its bounded parameters. Immutable and validated at construction.</summary>
public sealed class StoryObjective
{
    public const int MaxAmount = 1_000_000;
    public const float MaxVisitSeconds = 3600;

    public StoryObjectiveKind Kind { get; }
    /// <summary>Optional stable author key, independent of display text or step position.</summary>
    public string? LocalKey { get; }
    public string? Description { get; }
    /// <summary>Required for <see cref="StoryObjectiveKind.TravelToPoi"/>: an existing world POI identity, never a display name.</summary>
    public string? TargetPoiId { get; }
    /// <summary>Required for the counting kinds; ignored by <see cref="StoryObjectiveKind.TravelToPoi"/>.</summary>
    public int RequiredAmount { get; }
    /// <summary>Required for <see cref="StoryObjectiveKind.DeliverItems"/>: an existing item-type identity, never a display name.</summary>
    public string? ItemTypeId { get; }
    public float RequiredVisitSeconds { get; }

    private StoryObjective(StoryObjectiveKind kind, string? targetPoiId, int requiredAmount, float requiredVisitSeconds, string? localKey = null, string? description = null, string? itemTypeId = null)
    { Kind = kind; TargetPoiId = targetPoiId; RequiredAmount = requiredAmount; RequiredVisitSeconds = requiredVisitSeconds; LocalKey = localKey; Description = description; ItemTypeId = itemTypeId; }

    /// <summary>Returns an immutable keyed copy. Keys must be unique throughout one mission definition.</summary>
    public StoryObjective WithKey(string localKey)
    {
        if (!StoryContentId.IsValidSegment(localKey)) throw new ArgumentException("An objective key uses the story identity segment format.", nameof(localKey));
        return new StoryObjective(Kind, TargetPoiId, RequiredAmount, RequiredVisitSeconds, localKey, Description, ItemTypeId);
    }

    public static StoryObjective TravelTo(string targetPoiId, float requiredVisitSeconds = 0)
    {
        if (string.IsNullOrEmpty(targetPoiId) || targetPoiId.Length > 128) throw new ArgumentException("A travel objective requires a bounded target POI identity.", nameof(targetPoiId));
        if (!(requiredVisitSeconds >= 0) || requiredVisitSeconds > MaxVisitSeconds) throw new ArgumentOutOfRangeException(nameof(requiredVisitSeconds));
        return new StoryObjective(StoryObjectiveKind.TravelToPoi, targetPoiId, 0, requiredVisitSeconds);
    }

    /// <summary>An author-driven counting objective. Progress is absolute, not an incrementing narrative event.</summary>
    public static StoryObjective Scripted(string localKey, string description, int requiredAmount = 1)
    {
        if (!StoryContentId.IsValidSegment(localKey)) throw new ArgumentException("Invalid objective key.", nameof(localKey));
        if (string.IsNullOrWhiteSpace(description) || description.Length > 512) throw new ArgumentException("A bounded description is required.", nameof(description));
        if (requiredAmount is < 1 or > MaxAmount) throw new ArgumentOutOfRangeException(nameof(requiredAmount));
        return new StoryObjective(StoryObjectiveKind.Scripted, null, requiredAmount, 0, localKey, description);
    }

    /// <summary>
    /// A native item-delivery objective: the game itself tracks the count at the delivery POI and
    /// CONSUMES the delivered items on mission turn-in. Both identities are exact: an unknown item
    /// type or an unresolvable delivery POI refuses installation, never substitutes.
    /// </summary>
    public static StoryObjective DeliverItems(string itemTypeId, int requiredAmount, string deliverToPoiId)
    {
        if (string.IsNullOrWhiteSpace(itemTypeId) || itemTypeId.Length > 128) throw new ArgumentException("A bounded exact item-type identity is required.", nameof(itemTypeId));
        if (string.IsNullOrWhiteSpace(deliverToPoiId) || deliverToPoiId.Length > 128) throw new ArgumentException("A bounded delivery POI identity is required.", nameof(deliverToPoiId));
        if (requiredAmount is < 1 or > MaxAmount) throw new ArgumentOutOfRangeException(nameof(requiredAmount));
        return new StoryObjective(StoryObjectiveKind.DeliverItems, deliverToPoiId, requiredAmount, 0, itemTypeId: itemTypeId);
    }

    public static StoryObjective KillEnemies(int requiredAmount) => Counting(StoryObjectiveKind.KillEnemies, requiredAmount);
    public static StoryObjective CollectCredits(int requiredAmount) => Counting(StoryObjectiveKind.CollectCredits, requiredAmount);

    private static StoryObjective Counting(StoryObjectiveKind kind, int requiredAmount)
    {
        if (requiredAmount is < 1 or > MaxAmount) throw new ArgumentOutOfRangeException(nameof(requiredAmount));
        return new StoryObjective(kind, null, requiredAmount, 0);
    }
}

/// <summary>One ordered step. Steps and objectives are bounded so a definition cannot grow unbounded persisted state.</summary>
public sealed class StoryStep
{
    public const int MaxObjectives = 8;
    public string Description { get; }
    public IReadOnlyList<StoryObjective> Objectives { get; }
    public bool RequireAllObjectives { get; }

    public StoryStep(string description, IEnumerable<StoryObjective> objectives, bool requireAllObjectives = true)
    {
        Description = StoryText.Require(description, 512, nameof(description));
        var copy = (objectives ?? throw new ArgumentNullException(nameof(objectives)))
            .Select(objective => objective ?? throw new ArgumentException("Null objective.", nameof(objectives))).ToArray();
        if (copy.Length is < 1 or > MaxObjectives) throw new ArgumentException("A step needs 1-" + MaxObjectives + " objectives.", nameof(objectives));
        Objectives = Array.AsReadOnly(copy);
        RequireAllObjectives = requireAllObjectives;
    }
}

/// <summary>One supported reward. Reward formulas stay in providers; this is the payout the API can persist and reconstruct.</summary>
public sealed class StoryReward
{
    public StoryRewardKind Kind { get; }
    public int Amount { get; }
    /// <summary>Optional explicit faction for <see cref="StoryRewardKind.Reputation"/>; null grants to the mission's source faction.</summary>
    public StoryFactionId? Faction { get; }
    private StoryReward(StoryRewardKind kind, int amount, StoryFactionId? faction)
    {
        if (amount is < 1 or > StoryObjective.MaxAmount) throw new ArgumentOutOfRangeException(nameof(amount));
        Kind = kind; Amount = amount; Faction = faction;
    }
    public static StoryReward Credits(int amount) => new(StoryRewardKind.Credits, amount, null);
    public static StoryReward Experience(int amount) => new(StoryRewardKind.Experience, amount, null);
    /// <summary>Reputation with an existing faction; null grants to the mission's source faction (the native default).</summary>
    public static StoryReward Reputation(int amount, StoryFactionId? faction = null) => new(StoryRewardKind.Reputation, amount, faction);
}

internal static class StoryText
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Encoded size in the bytes the API actually persists, so a byte bound means the same thing everywhere.</summary>
    internal static int Utf8Bytes(string value, string name)
    {
        try { return StrictUtf8.GetByteCount(value); }
        catch (EncoderFallbackException) { throw new ArgumentException("Text must be valid Unicode.", name); }
    }

    internal static string Require(string value, int max, string name)
    {
        if (value == null) throw new ArgumentNullException(name);
        if (value.Length == 0 || value.Length > max) throw new ArgumentException("Text must be 1-" + max + " characters.", name);
        return value;
    }
    internal static string? Optional(string? value, int max, string name)
    {
        if (value == null) return null;
        if (value.Length == 0 || value.Length > max) throw new ArgumentException("Text must be 1-" + max + " characters.", name);
        return value;
    }
}

/// <summary>
/// An immutable, validated definition of one supported story mission. It carries no delegates, no
/// vanilla objects and no provider callbacks: the API owns registration, reconstruction and the
/// supported persisted state, so a provider writes no save/load hook for it.
/// </summary>
public sealed class StoryMissionDefinition
{
    private int _contentRevision = 1;
    private int? _migratesFromRevision;
    public int ContentRevision => _contentRevision;
    public int? MigratesFromRevision => _migratesFromRevision;

    /// <summary>Returns a revisioned immutable copy with explicit key-preserving migration permission.
    /// Supported migrations retain every old scripted key and required amount; new keys start empty.</summary>
    public StoryMissionDefinition WithRevision(int revision, int? migratesFromRevision = null)
    {
        if (revision < 1) throw new ArgumentOutOfRangeException(nameof(revision));
        if (migratesFromRevision.HasValue && (migratesFromRevision < 1 || migratesFromRevision >= revision))
            throw new ArgumentOutOfRangeException(nameof(migratesFromRevision));
        if (Steps.SelectMany(step => step.Objectives).Any(objective => objective.Kind != StoryObjectiveKind.Scripted))
            throw new InvalidOperationException("Revision migration requires fully keyed scripted objectives.");
        var copy = (StoryMissionDefinition)MemberwiseClone();
        copy._contentRevision = revision;
        copy._migratesFromRevision = migratesFromRevision;
        return copy;
    }

    public const int MaxSteps = 8;
    public const int MaxRewards = 4;
    /// <summary>Declared choice keys a campaign definition may record per occurrence.</summary>
    public const int MaxChoiceKeys = 8;
    /// <summary>Encoded size bound of one declared choice key.</summary>
    public const int MaxChoiceKeyBytes = 32;
    /// <summary>
    /// Encoded size bound of one declared choice VALUE. Declared choices are short decision tokens
    /// ("spared-captain"), not narrative text: the API reserves this much space for every declared
    /// key of every offered occurrence, so the bound is what makes an outcome guaranteed recordable.
    /// </summary>
    public const int MaxChoiceValueBytes = 64;
    /// <summary>
    /// Total encoded size the API reserves for one occurrence's declared choices. A definition whose
    /// declared keys would need more is refused at construction, so no definition can be registered
    /// that the API could not finish recording.
    /// </summary>
    public const int MaxChoiceBytesPerOccurrence = 1024;

    /// <summary>
    /// The provider's OWN local identifier. The provider segment is never supplied by the caller: it
    /// is derived from the authenticated host plugin when a lease is acquired, so one mod cannot
    /// register content under another mod's name.
    /// </summary>
    public string LocalId { get; }
    public string Title { get; }
    public string Description { get; }
    public string? Category { get; }
    public string? CompletionText { get; }
    public StoryDifficulty Difficulty { get; }
    public bool CanAbandon { get; }
    public StoryRetention Retention { get; }
    /// <summary>
    /// The faction this mission comes FROM. It is required because the game writes
    /// <c>sourceFaction.identifier</c> unconditionally when it saves a held mission: a mission without
    /// one makes the player's save throw. It is resolved against the game's own registry at
    /// registration, so an unknown identity refuses instead of producing that mission.
    /// </summary>
    public StoryFactionId SourceFaction { get; }
    public IReadOnlyList<StoryStep> Steps { get; }
    public IReadOnlyList<StoryReward> Rewards { get; }
    /// <summary>
    /// The declared choice keys this definition may record when an occurrence retires. Choices are
    /// declared up front, never invented at retirement, because the API reserves their worst-case
    /// persisted size when the occurrence is offered. A key that is not declared here is refused.
    /// Campaign retention only: a temporary definition retains no choices at all.
    /// </summary>
    public IReadOnlyList<string> ChoiceKeys { get; }
    /// <summary>
    /// Worst-case encoded bytes this definition's declared choices can occupy in one occurrence. It
    /// is reserved at offer time and released when the outcome is recorded, so recording an outcome
    /// can never be refused for space.
    /// </summary>
    public int ReservedChoiceBytes { get; }

    public StoryMissionDefinition(string localId, string title, string description, StoryFactionId sourceFaction, IEnumerable<StoryStep> steps,
        IEnumerable<StoryReward>? rewards = null, StoryDifficulty difficulty = StoryDifficulty.Normal,
        StoryRetention retention = StoryRetention.Temporary, bool canAbandon = true,
        string? category = null, string? completionText = null, IEnumerable<string>? choiceKeys = null)
    {
        if (!StoryContentId.IsValidSegment(localId)) throw new ArgumentException("A local ID is 1-48 lowercase ASCII letters/digits/hyphens starting with a letter.", nameof(localId));
        if (sourceFaction.Value == null) throw new ArgumentException("A source faction identity is required.", nameof(sourceFaction));
        SourceFaction = sourceFaction;
        if (!Enum.IsDefined(typeof(StoryDifficulty), difficulty)) throw new ArgumentOutOfRangeException(nameof(difficulty));
        if (!Enum.IsDefined(typeof(StoryRetention), retention)) throw new ArgumentOutOfRangeException(nameof(retention));
        LocalId = localId;
        Title = StoryText.Require(title, 128, nameof(title));
        Description = StoryText.Require(description, 1024, nameof(description));
        Category = StoryText.Optional(category, 64, nameof(category));
        CompletionText = StoryText.Optional(completionText, 1024, nameof(completionText));
        Difficulty = difficulty; CanAbandon = canAbandon; Retention = retention;
        var stepCopy = (steps ?? throw new ArgumentNullException(nameof(steps)))
            .Select(step => step ?? throw new ArgumentException("Null step.", nameof(steps))).ToArray();
        if (stepCopy.Length is < 1 or > MaxSteps) throw new ArgumentException("A definition needs 1-" + MaxSteps + " steps.", nameof(steps));
        var objectiveKeys = stepCopy.SelectMany(step => step.Objectives).Select(objective => objective.LocalKey)
            .Where(key => key != null).ToArray();
        if (objectiveKeys.Distinct(StringComparer.Ordinal).Count() != objectiveKeys.Length)
            throw new ArgumentException("Objective keys must be unique throughout a mission definition.", nameof(steps));
        Steps = Array.AsReadOnly(stepCopy);
        var rewardCopy = (rewards ?? Array.Empty<StoryReward>())
            .Select(reward => reward ?? throw new ArgumentException("Null reward.", nameof(rewards))).ToArray();
        if (rewardCopy.Length > MaxRewards) throw new ArgumentException("At most " + MaxRewards + " rewards.", nameof(rewards));
        // Reputation may repeat per distinct faction; an explicit source-faction grant is the SAME
        // grant as the null (source) form, so the two shapes dedup together. Other kinds stay unique.
        if (rewardCopy.Select(reward => (reward.Kind,
                reward.Faction is { } explicitFaction && explicitFaction.Equals(sourceFaction) ? null : reward.Faction?.Value))
            .Distinct().Count() != rewardCopy.Length)
            throw new ArgumentException("Duplicate reward kind.", nameof(rewards));
        Rewards = Array.AsReadOnly(rewardCopy);
        var choiceCopy = (choiceKeys ?? Array.Empty<string>()).ToArray();
        if (choiceCopy.Length > 0 && retention != StoryRetention.Campaign)
            throw new ArgumentException("Only a campaign definition retains declared choices.", nameof(choiceKeys));
        if (choiceCopy.Length > MaxChoiceKeys) throw new ArgumentException("At most " + MaxChoiceKeys + " declared choice keys.", nameof(choiceKeys));
        if (choiceCopy.Distinct(StringComparer.Ordinal).Count() != choiceCopy.Length)
            throw new ArgumentException("Duplicate declared choice key.", nameof(choiceKeys));
        int reserved = 0;
        foreach (var key in choiceCopy)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("A declared choice key must not be empty.", nameof(choiceKeys));
            int keyBytes = StoryText.Utf8Bytes(key, nameof(choiceKeys));
            if (keyBytes > MaxChoiceKeyBytes) throw new ArgumentException("A declared choice key is at most " + MaxChoiceKeyBytes + " encoded bytes.", nameof(choiceKeys));
            // The reservation counts what the codec writes: two length prefixes, the key, and a
            // value of the maximum supported size.
            reserved += 2 + keyBytes + 2 + MaxChoiceValueBytes;
        }
        if (reserved > MaxChoiceBytesPerOccurrence)
            throw new ArgumentException("Declared choices would reserve " + reserved + " bytes, above the "
                + MaxChoiceBytesPerOccurrence + "-byte bound for one occurrence.", nameof(choiceKeys));
        ChoiceKeys = Array.AsReadOnly(choiceCopy);
        ReservedChoiceBytes = reserved;
    }
}

/// <summary>
/// Why a registration was refused. Refusal is fail-closed: nothing is overwritten or stolen. The
/// numeric values are explicit and keep the numbers this enum already had, so appending a member can
/// never renumber an existing one; the gap before <see cref="Unavailable"/> is preserved rather than
/// closed, because closing it would change a value that is now exposed.
/// </summary>
public enum StoryRegistrationStatus
{
    Registered = 0,
    /// <summary>The definition itself is not admissible (an unsupported kind or an over-long identity).</summary>
    InvalidDefinition = 1,
    /// <summary>The same provider already registered this local ID in this process.</summary>
    DuplicateLocalId = 2,
    /// <summary>The derived native identifier is already taken by other content; the API never overwrites it.</summary>
    IdentifierInUse = 3,
    /// <summary>The registry's bounded capacity would be exceeded; nothing is silently dropped.</summary>
    LimitExceeded = 4,
    /// <summary>The story module or this provider lease is no longer active, or the world refused the installation.</summary>
    Unavailable = 7
}

/// <summary>Result of a registration attempt; a refusal carries the reason, never a partial registration.</summary>
public sealed class StoryRegistrationResult
{
    public StoryRegistrationStatus Status { get; }
    public IStoryDefinition? Definition { get; }
    public string Diagnostic { get; }
    public StoryRegistrationResult(StoryRegistrationStatus status, IStoryDefinition? definition, string diagnostic)
    {
        if (!Enum.IsDefined(typeof(StoryRegistrationStatus), status)) throw new ArgumentOutOfRangeException(nameof(status));
        if ((status == StoryRegistrationStatus.Registered) != (definition != null))
            throw new ArgumentException("Only a successful registration carries a definition.", nameof(definition));
        Status = status; Definition = definition;
        Diagnostic = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));
    }
    public bool Succeeded => Status == StoryRegistrationStatus.Registered;
}

/// <summary>
/// One occurrence of a definition. Repeated occurrences of the same definition are separate
/// records with separate identity and progress; another provider can neither update nor delete them.
/// </summary>
internal sealed class StoryOccurrenceRecord
{
    public StoryContentId Id { get; }
    public Guid OccurrenceId { get; }
    public StoryOutcome? Outcome { get; }
    public IReadOnlyDictionary<string, string> Choices { get; }
    public StoryOccurrenceRecord(StoryContentId id, Guid occurrenceId, StoryOutcome? outcome, IReadOnlyDictionary<string, string>? choices = null)
    {
        if (id.Provider == null) throw new ArgumentException("A default identity is not a content identity.", nameof(id));
        if (occurrenceId == Guid.Empty) throw new ArgumentException("An occurrence requires its own identity.", nameof(occurrenceId));
        if (outcome.HasValue && !Enum.IsDefined(typeof(StoryOutcome), outcome.Value)) throw new ArgumentOutOfRangeException(nameof(outcome));
        Id = id; OccurrenceId = occurrenceId; Outcome = outcome;
        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        if (choices != null) foreach (var pair in choices) copy[pair.Key] = pair.Value;
        Choices = copy;
    }
}

/// <summary>How far one occurrence has progressed. A retired occurrence is terminal and never re-opens.</summary>
/// <summary>
/// How far an UNRESOLVED occurrence has progressed. A retired occurrence is not a stage here: its
/// terminal record, with the outcome and the recorded choices, is a <see cref="StoryOccurrenceRecord"/>
/// returned by the retained query.
/// </summary>
internal enum StoryOccurrenceStage { Offered, Active }

/// <summary>
/// An immutable read-only view of one occurrence the API is still holding UNRESOLVED for this save.
/// It exists so a provider never has to persist occurrence identities itself: the API owns the
/// state, so it also has to be able to hand it back after a reload. It carries no outcome and no
/// choices, because an unresolved occurrence has none; a recorded outcome is a
/// <see cref="StoryOccurrenceRecord"/> from the retained query.
/// </summary>
internal sealed class StoryOccurrenceSnapshot
{
    public StoryContentId Id { get; }
    public Guid OccurrenceId { get; }
    public StoryOccurrenceStage Stage { get; }
    public StoryRetention Retention { get; }

    public StoryOccurrenceSnapshot(StoryContentId id, Guid occurrenceId, StoryOccurrenceStage stage, StoryRetention retention)
    {
        if (id.Provider == null) throw new ArgumentException("A default identity is not a content identity.", nameof(id));
        if (occurrenceId == Guid.Empty) throw new ArgumentException("An occurrence requires its own identity.", nameof(occurrenceId));
        if (!Enum.IsDefined(typeof(StoryOccurrenceStage), stage)) throw new ArgumentOutOfRangeException(nameof(stage));
        if (!Enum.IsDefined(typeof(StoryRetention), retention)) throw new ArgumentOutOfRangeException(nameof(retention));
        Id = id; OccurrenceId = occurrenceId; Stage = stage; Retention = retention;
    }
}

/// <summary>
/// Whether an answer describes the CURRENT save at all. A query never returns a plain empty list or
/// a bare false when the module could not restore this session's state: "nothing recorded yet" and
/// "this save's history is unavailable" are different answers.
/// </summary>
public enum StoryKnowledge
{
    /// <summary>The module restored (or freshly started) THIS session; the answer describes it.</summary>
    Known,
    /// <summary>No restored state for the current session: no session, a failed/invalidated start, blocked or unreadable owner data, or an inactive lease. Nothing is asserted about any save.</summary>
    Unavailable
}

/// <summary>Occurrence answer scoped to the session it was read in. Records are empty when unavailable.</summary>
internal sealed class StoryOccurrenceQuery
{
    public StoryKnowledge Knowledge { get; }
    /// <summary>The session the answer belongs to, or null when unavailable.</summary>
    public Guid? SessionId { get; }
    public IReadOnlyList<StoryOccurrenceRecord> Records { get; }
    public string Detail { get; }
    public StoryOccurrenceQuery(StoryKnowledge knowledge, Guid? sessionId, IEnumerable<StoryOccurrenceRecord>? records, string detail)
    {
        if (!Enum.IsDefined(typeof(StoryKnowledge), knowledge)) throw new ArgumentOutOfRangeException(nameof(knowledge));
        if ((knowledge == StoryKnowledge.Known) != sessionId.HasValue) throw new ArgumentException("Only a known answer carries its session.", nameof(sessionId));
        Knowledge = knowledge; SessionId = sessionId;
        var copy = (records ?? Array.Empty<StoryOccurrenceRecord>())
            .Select(record => record ?? throw new ArgumentException("Null record.", nameof(records))).ToArray();
        if (knowledge != StoryKnowledge.Known && copy.Length > 0) throw new ArgumentException("An unavailable answer carries no records.", nameof(records));
        Records = Array.AsReadOnly(copy);
        Detail = detail ?? throw new ArgumentNullException(nameof(detail));
    }
}

/// <summary>
/// Occurrence snapshots scoped to the session they were read in, with the same availability rules as
/// every other answer. Snapshots are empty when unavailable.
/// </summary>
internal sealed class StoryOccurrenceSnapshotQuery
{
    public StoryKnowledge Knowledge { get; }
    public Guid? SessionId { get; }
    public IReadOnlyList<StoryOccurrenceSnapshot> Occurrences { get; }
    public string Detail { get; }
    public StoryOccurrenceSnapshotQuery(StoryKnowledge knowledge, Guid? sessionId,
        IEnumerable<StoryOccurrenceSnapshot>? occurrences, string detail)
    {
        if (!Enum.IsDefined(typeof(StoryKnowledge), knowledge)) throw new ArgumentOutOfRangeException(nameof(knowledge));
        if ((knowledge == StoryKnowledge.Known) != sessionId.HasValue) throw new ArgumentException("Only a known answer carries its session.", nameof(sessionId));
        var copy = (occurrences ?? Array.Empty<StoryOccurrenceSnapshot>())
            .Select(item => item ?? throw new ArgumentException("Null snapshot.", nameof(occurrences))).ToArray();
        if (knowledge != StoryKnowledge.Known && copy.Length > 0) throw new ArgumentException("An unavailable answer carries no snapshots.", nameof(occurrences));
        Knowledge = knowledge; SessionId = sessionId;
        Occurrences = Array.AsReadOnly(copy);
        Detail = detail ?? throw new ArgumentNullException(nameof(detail));
    }
}

/// <summary>
/// Campaign completion answer. <see cref="Completed"/> is null when unavailable, so a caller can
/// never read an unreadable history as "not completed".
/// </summary>
internal sealed class StoryCompletionQuery
{
    public StoryKnowledge Knowledge { get; }
    public Guid? SessionId { get; }
    public bool? Completed { get; }
    public string Detail { get; }
    public StoryCompletionQuery(StoryKnowledge knowledge, Guid? sessionId, bool? completed, string detail)
    {
        if (!Enum.IsDefined(typeof(StoryKnowledge), knowledge)) throw new ArgumentOutOfRangeException(nameof(knowledge));
        if ((knowledge == StoryKnowledge.Known) != sessionId.HasValue) throw new ArgumentException("Only a known answer carries its session.", nameof(sessionId));
        if ((knowledge == StoryKnowledge.Known) != completed.HasValue) throw new ArgumentException("Only a known answer carries a completion value.", nameof(completed));
        Knowledge = knowledge; SessionId = sessionId; Completed = completed;
        Detail = detail ?? throw new ArgumentNullException(nameof(detail));
    }
}

/// <summary>Why a provider lease was refused.</summary>
public enum StoryProviderStatus
{
    Acquired,
    /// <summary>The caller could not be resolved to a loaded plugin by the host, so no provider identity can be derived.</summary>
    UnknownPlugin,
    /// <summary>
    /// The supplied plugin instance belongs to a plugin the host loaded from a DIFFERENT assembly
    /// than the one that called this method. A plugin acquires its own lease, not another's.
    /// </summary>
    CallerMismatch,
    /// <summary>
    /// This plugin already holds a live lease. A lease is not shared or reference counted, so a
    /// caller keeps the one it acquired instead of receiving a second handle to it.
    /// </summary>
    AlreadyAcquired,
    /// <summary>The derived provider segment is already bound to a DIFFERENT host plugin; neither may take the other's content.</summary>
    ProviderConflict,
    /// <summary>The bounded number of story providers is already bound; an existing provider's reserved share is never taken away.</summary>
    LimitExceeded,
    /// <summary>The story module is unavailable or disposed.</summary>
    Unavailable
}

/// <summary>
/// A provider's own lease on owned story content. The provider segment is derived from the
/// authenticated host plugin, so every registration, transition and query is scoped to the plugin
/// that acquired it: a caller-supplied string can no longer claim another mod's content.
///
/// Disposing a lease releases only THIS provider's registrations. It never unregisters the module's
/// internal persistence owner and therefore never pauses another mod's saves.
/// </summary>
public interface IStoryProvider : IDisposable
{
    string ProviderId { get; }
    StoryRegistrationResult Register(StoryMissionDefinition definition);
}

/// <summary>
/// Why an occurrence transition was refused. The numeric values are explicit and stable: a member is
/// only ever appended with a new value, so inserting one can never silently renumber the others for
/// code or persisted diagnostics compiled against an earlier build.
/// </summary>
internal enum StoryTransitionStatus
{
    Accepted = 0,
    /// <summary>The occurrence is unknown to the current ledger, including one pruned past the idempotency horizon.</summary>
    UnknownOccurrence = 1,
    /// <summary>The occurrence belongs to another provider or another definition; ownership is never crossed.</summary>
    ForeignOwner = 2,
    /// <summary>The transition does not follow the recorded state, for example a second terminal outcome.</summary>
    InvalidTransition = 3,
    /// <summary>A bounded limit would be exceeded. Nothing is truncated and no campaign progression is dropped.</summary>
    LimitExceeded = 4,
    /// <summary>
    /// The expected session is not the session that is loaded now. A reload restores the SAME
    /// occurrence identities, so a delayed callback from the pre-reload world would otherwise apply
    /// an outcome the loaded save never produced. Re-read the state and use the current session.
    /// </summary>
    StaleSession = 5,
    /// <summary>
    /// The state is readable but cannot be mutated at this instant, because lifecycle callbacks are
    /// dispatching or a save is already in flight. Accepting content now would leave it out of the
    /// save being written. This is temporary: the same call succeeds once the operation completes.
    /// </summary>
    Busy = 6,
    /// <summary>No restored state for the current session, or the lease/module is inactive; content is never accepted unsaved.</summary>
    Unavailable = 7
}

internal sealed class StoryTransitionResult
{
    public StoryTransitionStatus Status { get; }
    /// <summary>The occurrence this call created or addressed; empty when refused before one existed.</summary>
    public Guid OccurrenceId { get; }
    public string Detail { get; }
    public StoryTransitionResult(StoryTransitionStatus status, Guid occurrenceId, string detail)
    {
        if (!Enum.IsDefined(typeof(StoryTransitionStatus), status)) throw new ArgumentOutOfRangeException(nameof(status));
        Status = status; OccurrenceId = occurrenceId;
        Detail = detail ?? throw new ArgumentNullException(nameof(detail));
    }
    public bool Accepted => Status == StoryTransitionStatus.Accepted;
}

public sealed class StoryProviderResult
{
    public StoryProviderStatus Status { get; }
    public IStoryProvider? Provider { get; }
    public string Diagnostic { get; }
    public StoryProviderResult(StoryProviderStatus status, IStoryProvider? provider, string diagnostic)
    {
        if (!Enum.IsDefined(typeof(StoryProviderStatus), status)) throw new ArgumentOutOfRangeException(nameof(status));
        if ((status == StoryProviderStatus.Acquired) != (provider != null))
            throw new ArgumentException("Only an acquired lease carries a provider.", nameof(provider));
        Status = status; Provider = provider;
        Diagnostic = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));
    }
    public bool Succeeded => Status == StoryProviderStatus.Acquired;
}

/// <summary>
/// Optional owned-story surface. A consumer acquires a lease for ITSELF: it passes its own plugin
/// instance, the implementation captures the assembly that actually made the call, and the host
/// resolves both to one loaded plugin identity. Passing another plugin's instance from a different
/// assembly is refused (<see cref="StoryProviderStatus.CallerMismatch"/>), so an ordinary API call
/// cannot take another mod's provider identity. The API then owns registration, occurrence identity
/// and the supported persisted state, so a provider writes no codec, save/load callback or
/// restoration scheduling for it. Additional provider-owned information keeps using the separate
/// save-data API.
///
/// This is an ordinary-use boundary, NOT a sandbox: plugins share one process, and reflection,
/// injected code or an assembly that declares several plugins can still reach content the caller
/// association would otherwise separate. It prevents accidental and casual cross-mod ownership, not
/// a determined one.
///
/// All members, including queries and disposal, are Unity-main-thread-only.
/// </summary>
public interface IStoryService : IServiceStatus
{
    /// <summary>
    /// Acquires this plugin's provider lease. Pass the plugin instance itself (the object the host
    /// loaded) and call it DIRECTLY from that plugin's own assembly: the implementation reads the
    /// calling assembly at this boundary, and the host must resolve the instance to a plugin it
    /// loaded from that same assembly. There is no caller-supplied assembly or provider parameter,
    /// so the association cannot be spoofed by an argument. Callers cache the returned lease; a
    /// second acquisition while one is live reports <see cref="StoryProviderStatus.AlreadyAcquired"/>.
    /// </summary>
    StoryProviderResult AcquireProvider(object pluginInstance, ISaveDataRegistration? saveData = null);
}
