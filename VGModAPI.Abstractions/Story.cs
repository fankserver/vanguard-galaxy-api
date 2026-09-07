using System;
using System.Collections.Generic;
using System.Linq;

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
public enum StoryObjectiveKind { TravelToPoi, KillEnemies, CollectCredits }

/// <summary>
/// Reward kinds this API supports today. Vanilla SKIPS an unresolvable reward with a log line, so
/// the API refuses unsupported rewards at registration instead of letting them disappear on load.
/// Item/reputation rewards need owner-scoped item/faction identities and are deliberately absent.
/// </summary>
public enum StoryRewardKind { Credits, Experience }

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
    /// <summary>Required for <see cref="StoryObjectiveKind.TravelToPoi"/>: an existing world POI identity, never a display name.</summary>
    public string? TargetPoiId { get; }
    /// <summary>Required for the counting kinds; ignored by <see cref="StoryObjectiveKind.TravelToPoi"/>.</summary>
    public int RequiredAmount { get; }
    public float RequiredVisitSeconds { get; }

    private StoryObjective(StoryObjectiveKind kind, string? targetPoiId, int requiredAmount, float requiredVisitSeconds)
    { Kind = kind; TargetPoiId = targetPoiId; RequiredAmount = requiredAmount; RequiredVisitSeconds = requiredVisitSeconds; }

    public static StoryObjective TravelTo(string targetPoiId, float requiredVisitSeconds = 0)
    {
        if (string.IsNullOrEmpty(targetPoiId) || targetPoiId.Length > 128) throw new ArgumentException("A travel objective requires a bounded target POI identity.", nameof(targetPoiId));
        if (!(requiredVisitSeconds >= 0) || requiredVisitSeconds > MaxVisitSeconds) throw new ArgumentOutOfRangeException(nameof(requiredVisitSeconds));
        return new StoryObjective(StoryObjectiveKind.TravelToPoi, targetPoiId, 0, requiredVisitSeconds);
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
    public StoryReward(StoryRewardKind kind, int amount)
    {
        if (!Enum.IsDefined(typeof(StoryRewardKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (amount is < 1 or > StoryObjective.MaxAmount) throw new ArgumentOutOfRangeException(nameof(amount));
        Kind = kind; Amount = amount;
    }
}

internal static class StoryText
{
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
    public const int MaxSteps = 8;
    public const int MaxRewards = 4;

    public StoryContentId Id { get; }
    public string Title { get; }
    public string Description { get; }
    public string? Category { get; }
    public string? CompletionText { get; }
    public StoryDifficulty Difficulty { get; }
    public bool CanAbandon { get; }
    public StoryRetention Retention { get; }
    public IReadOnlyList<StoryStep> Steps { get; }
    public IReadOnlyList<StoryReward> Rewards { get; }

    public StoryMissionDefinition(StoryContentId id, string title, string description, IEnumerable<StoryStep> steps,
        IEnumerable<StoryReward>? rewards = null, StoryDifficulty difficulty = StoryDifficulty.Normal,
        StoryRetention retention = StoryRetention.Temporary, bool canAbandon = true,
        string? category = null, string? completionText = null)
    {
        if (id.Provider == null) throw new ArgumentException("A default story identity is not a registered content identity.", nameof(id));
        if (!Enum.IsDefined(typeof(StoryDifficulty), difficulty)) throw new ArgumentOutOfRangeException(nameof(difficulty));
        if (!Enum.IsDefined(typeof(StoryRetention), retention)) throw new ArgumentOutOfRangeException(nameof(retention));
        Id = id;
        Title = StoryText.Require(title, 128, nameof(title));
        Description = StoryText.Require(description, 1024, nameof(description));
        Category = StoryText.Optional(category, 64, nameof(category));
        CompletionText = StoryText.Optional(completionText, 1024, nameof(completionText));
        Difficulty = difficulty; CanAbandon = canAbandon; Retention = retention;
        var stepCopy = (steps ?? throw new ArgumentNullException(nameof(steps)))
            .Select(step => step ?? throw new ArgumentException("Null step.", nameof(steps))).ToArray();
        if (stepCopy.Length is < 1 or > MaxSteps) throw new ArgumentException("A definition needs 1-" + MaxSteps + " steps.", nameof(steps));
        Steps = Array.AsReadOnly(stepCopy);
        var rewardCopy = (rewards ?? Array.Empty<StoryReward>())
            .Select(reward => reward ?? throw new ArgumentException("Null reward.", nameof(rewards))).ToArray();
        if (rewardCopy.Length > MaxRewards) throw new ArgumentException("At most " + MaxRewards + " rewards.", nameof(rewards));
        if (rewardCopy.Select(reward => reward.Kind).Distinct().Count() != rewardCopy.Length)
            throw new ArgumentException("Duplicate reward kind.", nameof(rewards));
        Rewards = Array.AsReadOnly(rewardCopy);
    }
}

/// <summary>Why a registration was refused. Refusal is fail-closed: nothing is overwritten or stolen.</summary>
public enum StoryRegistrationStatus
{
    Registered,
    /// <summary>The same provider already registered this local ID in this process.</summary>
    DuplicateLocalId,
    /// <summary>The derived native identifier is already taken by other content; the API never overwrites it.</summary>
    IdentifierInUse,
    /// <summary>The registry's bounded capacity would be exceeded; nothing is silently dropped.</summary>
    LimitExceeded,
    /// <summary>The story capability is unavailable or the module is disposed.</summary>
    Unavailable
}

/// <summary>Session-owned handle. Disposal removes the registration; it never rewrites saved state.</summary>
public interface IStoryRegistration : IDisposable
{
    StoryContentId Id { get; }
    /// <summary>The namespaced identifier the API uses for this definition inside the game. Opaque to consumers.</summary>
    string NativeIdentifier { get; }
    bool Active { get; }
}

/// <summary>Result of a registration attempt; a refusal carries the reason, never a partial registration.</summary>
public sealed class StoryRegistrationResult
{
    public StoryRegistrationStatus Status { get; }
    public IStoryRegistration? Registration { get; }
    public string Diagnostic { get; }
    public StoryRegistrationResult(StoryRegistrationStatus status, IStoryRegistration? registration, string diagnostic)
    {
        if (!Enum.IsDefined(typeof(StoryRegistrationStatus), status)) throw new ArgumentOutOfRangeException(nameof(status));
        if ((status == StoryRegistrationStatus.Registered) != (registration != null))
            throw new ArgumentException("Only a successful registration carries a handle.", nameof(registration));
        Status = status; Registration = registration;
        Diagnostic = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));
    }
    public bool Succeeded => Status == StoryRegistrationStatus.Registered;
}

/// <summary>
/// One occurrence of a definition. Repeated occurrences of the same definition are separate
/// records with separate identity and progress; another provider can neither update nor delete them.
/// </summary>
public sealed class StoryOccurrenceRecord
{
    public StoryContentId Id { get; }
    public Guid OccurrenceId { get; }
    public StoryOutcome? Outcome { get; }
    public IReadOnlyDictionary<string, string> Choices { get; }
    public StoryOccurrenceRecord(StoryContentId id, Guid occurrenceId, StoryOutcome? outcome, IReadOnlyDictionary<string, string>? choices = null)
    {
        if (occurrenceId == Guid.Empty) throw new ArgumentException("An occurrence requires its own identity.", nameof(occurrenceId));
        if (outcome.HasValue && !Enum.IsDefined(typeof(StoryOutcome), outcome.Value)) throw new ArgumentOutOfRangeException(nameof(outcome));
        Id = id; OccurrenceId = occurrenceId; Outcome = outcome;
        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        if (choices != null) foreach (var pair in choices) copy[pair.Key] = pair.Value;
        Choices = copy;
    }
}

/// <summary>
/// Optional owned-story surface. Registration installs supported definitions; the API itself
/// preserves the offered/active/outcome state a reload needs. Providers supply no codecs, save/load
/// callbacks or restoration scheduling for this state. Additional provider-owned information keeps
/// using the separate save-data API.
/// </summary>
public interface IStoryApi
{
    /// <summary>Registers a supported definition. Refusals are diagnosed and never overwrite other content.</summary>
    StoryRegistrationResult Register(StoryMissionDefinition definition);

    /// <summary>Authoritative outcomes the API retained for this definition, newest last. Empty when nothing is retained.</summary>
    IReadOnlyList<StoryOccurrenceRecord> Occurrences(StoryContentId id);

    /// <summary>True when a campaign-retained occurrence of this definition completed. Works without any journal mod.</summary>
    bool IsCompleted(StoryContentId id);
}
