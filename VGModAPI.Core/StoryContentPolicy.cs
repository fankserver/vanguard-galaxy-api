using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

/// <summary>
/// Pure identity and supported-subset policy for owned story content. It decides which vanilla types
/// a definition may reach and how an owner-scoped identity becomes the namespaced identifier the
/// game stores; it installs nothing.
///
/// Inspected ground truth (installed assembly, re-read for this delivery):
/// <list type="bullet">
/// <item><c>StoryMission.Add</c> assigns <c>allMissions[identifier]</c>, so vanilla silently REPLACES
/// an existing identifier. Collision detection therefore belongs to this API, and a taken identifier
/// is refused rather than stolen.</item>
/// <item><c>Mission.FromJson</c> routes a string payload to <c>StoryMission.Get(player, id)</c>, whose
/// dictionary index throws when the definition is absent. A missing provider is a protected refusal,
/// never a substituted mission.</item>
/// <item><c>MissionObjective.Create</c> and <c>MissionReward.Create</c> resolve unqualified type names
/// under <c>Source.MissionSystem.Objectives</c>/<c>Rewards</c>, so only vanilla types round-trip; an
/// unresolvable reward is SKIPPED with a log line, which is why unsupported rewards are refused here.</item>
/// </list>
/// </summary>
internal static class StoryContentPolicy
{
    /// <summary>Namespace prefix of every identifier this API installs. Vanilla identifiers never contain it.</summary>
    internal const string IdentifierPrefix = "vgmodapi.story.";
    /// <summary>Bounds the base identifier AND the per-occurrence identifier derived from it.</summary>
    internal const int MaxIdentifierLength = 192;
    /// <summary>Bounded registry capacity; overflow is refused with a diagnostic, never silently dropped.</summary>
    internal const int MaxDefinitions = 256;

    /// <summary>Vanilla objective type names, exactly as <c>MissionObjective.Create</c> resolves them.</summary>
    private static readonly Dictionary<StoryObjectiveKind, string> ObjectiveTypes = new()
    {
        [StoryObjectiveKind.TravelToPoi] = "TravelToPOI",
        [StoryObjectiveKind.KillEnemies] = "KillEnemies",
        [StoryObjectiveKind.CollectCredits] = "CollectCredits",
        [StoryObjectiveKind.Scripted] = "TriggerObjective",
        [StoryObjectiveKind.DeliverItems] = "TradeOffer",
        [StoryObjectiveKind.MineItems] = "Mining",
        [StoryObjectiveKind.SalvageItems] = "Salvage",
        [StoryObjectiveKind.ReturnToSource] = "TravelToPOI",
        [StoryObjectiveKind.TravelToPocketSystemEntrance] = "TravelToPOI",
        [StoryObjectiveKind.TravelToResourceSite] = "TravelToPOI"
    };

    /// <summary>Vanilla reward type names, exactly as <c>MissionReward.Create</c> resolves them.</summary>
    private static readonly Dictionary<StoryRewardKind, string> RewardTypes = new()
    {
        [StoryRewardKind.Credits] = "Credits",
        [StoryRewardKind.Experience] = "Experience",
        [StoryRewardKind.Reputation] = "Reputation"
    };

    /// <summary>
    /// Vanilla difficulty names, by ASCENDING native tier. The game's own scale is Easy, Normal, Hard,
    /// Skull, Insane plus the non-scalar Faction/Tutorial/Story tiers, so this API's fourth tier maps
    /// onto Skull rather than inventing a name the enum does not have. The mapping is pinned against
    /// the installed assembly; an unmapped or absent name refuses installation instead of guessing.
    /// </summary>
    private static readonly Dictionary<StoryDifficulty, string> DifficultyNames = new()
    {
        [StoryDifficulty.Easy] = "Easy",
        [StoryDifficulty.Normal] = "Normal",
        [StoryDifficulty.Hard] = "Hard",
        [StoryDifficulty.VeryHard] = "Skull"
    };

    internal static string DifficultyName(StoryDifficulty difficulty)
        => DifficultyNames.TryGetValue(difficulty, out var name) ? name : throw new ArgumentOutOfRangeException(nameof(difficulty), "Unsupported difficulty.");

    internal const string ObjectiveNamespace = "Source.MissionSystem.Objectives";
    internal const string RewardNamespace = "Source.MissionSystem.Rewards";

    /// <summary>
    /// Objective kinds this API refuses to install today, with the reason. <c>KillEnemies</c> carries a
    /// faction: the game serializes <c>enemyFaction.identifier</c>, counts kills against that faction
    /// and renders its name, so an objective without one produces a save-breaking, uncompletable
    /// mission. Owner-scoped faction identity for objectives is not part of this subset, so the kind
    /// stays in the vocabulary and is refused at registration rather than installed unsafely.
    /// </summary>
    private static readonly Dictionary<StoryObjectiveKind, string> UnsupportedObjectives = new();

    internal static string? RefuseObjective(StoryObjectiveKind kind)
        => UnsupportedObjectives.TryGetValue(kind, out var reason) ? reason : null;

    internal static string ObjectiveTypeName(StoryObjectiveKind kind)
        => ObjectiveTypes.TryGetValue(kind, out var name) ? name : throw new ArgumentOutOfRangeException(nameof(kind), "Unsupported objective kind.");

    internal static string RewardTypeName(StoryRewardKind kind)
        => RewardTypes.TryGetValue(kind, out var name) ? name : throw new ArgumentOutOfRangeException(nameof(kind), "Unsupported reward kind.");


    /// <summary>
    /// The namespaced identifier for an owner-scoped content ID. Two providers that both choose the
    /// local ID <c>mission-x</c> produce different identifiers, so neither can capture the other's
    /// saved content.
    /// </summary>
    internal static string Identifier(StoryContentId id)
    {
        if (id.Provider == null) throw new ArgumentException("A default identity has no identifier.", nameof(id));
        var identifier = IdentifierPrefix + id.Provider + "." + id.LocalId;
        if (identifier.Length > MaxIdentifierLength) throw new ArgumentException("Story identifier exceeds " + MaxIdentifierLength + " characters.", nameof(id));
        return identifier;
    }

    /// <summary>Reads back an identifier this API produced. Foreign identifiers are not ours to interpret.</summary>
    /// <summary>
    /// The identifier ONE occurrence is installed under. The game archives a completed story
    /// identifier and refuses a duplicate of it forever after, so every occurrence needs its own:
    /// a shared base identifier could be accepted exactly once per save. It is derived
    /// deterministically from the content identity and the occurrence, so a reload reinstalls exactly
    /// the same entries without storing the string itself.
    /// </summary>
    internal static string OccurrenceIdentifier(StoryContentId id, Guid occurrenceId)
    {
        if (occurrenceId == Guid.Empty) throw new ArgumentException("An occurrence requires its own identity.", nameof(occurrenceId));
        var identifier = Identifier(id) + "." + occurrenceId.ToString("N");
        if (identifier.Length > MaxIdentifierLength) throw new ArgumentException("Occurrence identifier exceeds its bound.", nameof(id));
        return identifier;
    }

    /// <summary>Parses an occurrence identifier back to its content identity and occurrence.</summary>
    internal static bool TryParseOccurrenceIdentifier(string? identifier, out StoryContentId id, out Guid occurrenceId)
    {
        id = default; occurrenceId = Guid.Empty;
        if (identifier == null || identifier.Length < 33) return false;
        var separator = identifier.Length - 33;
        if (identifier[separator] != '.') return false;
        if (!Guid.TryParseExact(identifier.Substring(separator + 1), "N", out occurrenceId) || occurrenceId == Guid.Empty) return false;
        return TryParseIdentifier(identifier.Substring(0, separator), out id);
    }

    internal static bool TryParseIdentifier(string? identifier, out StoryContentId id)
    {
        id = default;
        if (identifier == null || identifier.Length > MaxIdentifierLength || !identifier.StartsWith(IdentifierPrefix, StringComparison.Ordinal)) return false;
        var rest = identifier.Substring(IdentifierPrefix.Length);
        int separator = rest.IndexOf('.');
        if (separator <= 0 || separator == rest.Length - 1) return false;
        var provider = rest.Substring(0, separator);
        var local = rest.Substring(separator + 1);
        if (!StoryContentId.IsValidSegment(provider) || !StoryContentId.IsValidSegment(local)) return false;
        id = new StoryContentId(provider, local);
        return true;
    }

    /// <summary>Null when the definition only uses supported types, otherwise the exact refusal reason.</summary>
    internal static string? Refuse(StoryContentId id, StoryMissionDefinition definition)
    {
        if (definition == null) return "A definition is required.";
        if (id.Provider == null || id.LocalId != definition.LocalId) return "The definition's local ID does not match its resolved identity.";
        foreach (var step in definition.Steps)
            foreach (var objective in step.Objectives)
                if (!ObjectiveTypes.ContainsKey(objective.Kind))
                    return "Unsupported objective kind " + objective.Kind + "; vanilla resolves objective types from its own assembly only.";
        foreach (var reward in definition.Rewards)
            if (!RewardTypes.ContainsKey(reward.Kind))
                return "Unsupported reward kind " + reward.Kind + "; vanilla skips unresolvable rewards on load.";
        foreach (var kind in definition.Steps.SelectMany(step => step.Objectives).Select(objective => objective.Kind))
        {
            var refusal = RefuseObjective(kind);
            if (refusal != null) return refusal;
        }
        try { Identifier(id); }
        catch (ArgumentException error) { return error.Message; }
        return null;
    }
}
