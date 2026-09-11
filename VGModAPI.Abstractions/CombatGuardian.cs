using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace VGModAPI;

/// <summary>
/// Item-equipment rarity an authored guardian can be forced onto. Mirrors the vanilla rarity names
/// (Standard, Enhanced, HighGrade, Exotic, Legendary); the installed game build decides whether a
/// given name is supported. Only the units the author owns through this surface are affected.
/// </summary>
public enum AuthoredRarity { Standard, Enhanced, HighGrade, Exotic, Legendary }

/// <summary>
/// Optional authored guardian equipment tuning applied when an encounter spawns. Only supplied
/// aspects change; everything else stays vanilla. Applying a rarity forces the unit's loadout to
/// that rarity and re-rolls its equipment; an overclock raises the unit's outgoing damage so its
/// weapons hit at the requested damage tier despite the game's item-level cap (the equivalent of
/// the legacy per-unit damage stat boost). Runtime-only: nothing is written to saves.
/// </summary>
public sealed class AuthoredLoadout
{
    /// <summary>Forced equipment rarity, or null to keep the vanilla roll.</summary>
    public AuthoredRarity? Rarity { get; }
    /// <summary>
    /// Intended outgoing-damage tier (a level). The API raises the responsible unit's outgoing damage
    /// from its achieved (item-level-capped) effective level up to this tier, no-op when the achieved
    /// level already meets or exceeds it. Null keeps vanilla damage.
    /// </summary>
    public int? RequestedDamageLevel { get; }

    public AuthoredLoadout(AuthoredRarity? rarity = null, int? requestedDamageLevel = null)
    {
        if (rarity is { } r && !Enum.IsDefined(typeof(AuthoredRarity), r))
            throw new ArgumentOutOfRangeException(nameof(rarity));
        if (requestedDamageLevel is { } level && (level < 1 || level > 100000))
            throw new ArgumentOutOfRangeException(nameof(requestedDamageLevel));
        if (rarity == null && requestedDamageLevel == null)
            throw new ArgumentException("At least one loadout aspect is required.", nameof(rarity));
        Rarity = rarity; RequestedDamageLevel = requestedDamageLevel;
    }
}

/// <summary>
/// Optional authored encounter level policy. When either aspect is set the spawn is scaled
/// dynamically at materialisation time against the observed player level rather than using the
/// fixed <see cref="EncounterComposition.Level"/> directly: resolved = min(max(floor,
/// playerLevel + threatOver), the game's level ceiling). When neither aspect is set the fixed level
/// is used unchanged (existing behavior).
/// </summary>
public sealed class EncounterLevelPolicy
{
    /// <summary>Absolute minimum resolved level; defaults to the composition's fixed level.</summary>
    public int? LevelFloor { get; }
    /// <summary>Player-relative threat bonus added to the observed player level.</summary>
    public int? ThreatOver { get; }
    /// <summary>True when dynamic scaling is active (either aspect set).</summary>
    public bool Dynamic => LevelFloor.HasValue || ThreatOver.HasValue;

    public EncounterLevelPolicy(int? levelFloor = null, int? threatOver = null)
    {
        if (levelFloor is { } floor && floor < 1) throw new ArgumentOutOfRangeException(nameof(levelFloor));
        if (threatOver is { } over && over < 0) throw new ArgumentOutOfRangeException(nameof(threatOver));
        if (levelFloor == null && threatOver == null) throw new ArgumentException("At least one policy aspect is required.");
        LevelFloor = levelFloor; ThreatOver = threatOver;
    }
}

/// <summary>
/// A declared, owned guardian unit: a named boss (or guard) an author spawns at a combat site,
/// scaled by an optional level policy, optionally forced to a loadout rarity and outgoing-damage
/// tier, and kept alive through the encounter. The facade composes the underlying authored-content
/// primitives (encounter spawn, combat site, unit protection, drone bays); it does not add a
/// competing spawning mechanism. Occurrences are author-local and re-resolve on load like the other
/// authored kinds.
/// </summary>
public sealed class GuardianSpec
{
    /// <summary>Ship class identifier the guardian is spawned as.</summary>
    public string ShipClassId { get; }
    /// <summary>Faction identity the guardian belongs to.</summary>
    public string FactionId { get; }
    /// <summary>Base level used when no level policy applies; also the fallback floor.</summary>
    public int Level { get; }
    public EncounterRank Rank { get; }
    public bool HostileToPlayer { get; }
    public bool NoReputationLoss { get; }
    /// <summary>Optional dynamic level scaling floor + player threat.</summary>
    public EncounterLevelPolicy? LevelPolicy { get; }
    /// <summary>Optional forced rarity / damage-tier inner equip.</summary>
    public AuthoredLoadout? Loadout { get; }

    public GuardianSpec(string shipClassId, string factionId, int level,
        EncounterRank rank = EncounterRank.Elite, bool hostileToPlayer = true, bool noReputationLoss = true,
        EncounterLevelPolicy? levelPolicy = null, AuthoredLoadout? loadout = null)
    {
        if (string.IsNullOrWhiteSpace(shipClassId)) throw new ArgumentException("A ship class is required.", nameof(shipClassId));
        if (string.IsNullOrWhiteSpace(factionId)) throw new ArgumentException("A faction identity is required.", nameof(factionId));
        if (level < 1 || level > 100000) throw new ArgumentOutOfRangeException(nameof(level));
        if (!Enum.IsDefined(typeof(EncounterRank), rank)) throw new ArgumentOutOfRangeException(nameof(rank));
        ShipClassId = shipClassId; FactionId = factionId; Level = level; Rank = rank;
        HostileToPlayer = hostileToPlayer; NoReputationLoss = noReputationLoss;
        LevelPolicy = levelPolicy; Loadout = loadout;
    }
}

/// <summary></summary>
public sealed class GuardianState
{
    public AuthoredSystemReconstructionStatus Status { get; }
    public AuthoredSystemFailureReason? Reason { get; }
    /// <summary>Resolved spawn level, populated only while reconstructed or scheduled.</summary>
    public int? Level { get; }
    /// <summary>The one unit-data identity this guardian owns, when materialised.</summary>
    public string? UnitId { get; }
    public bool Reconstructed => Status == AuthoredSystemReconstructionStatus.Reconstructed;
    public GuardianState(AuthoredSystemReconstructionStatus status, AuthoredSystemFailureReason? reason = null, int? level = null, string? unitId = null)
    { Status = status; Reason = reason; Level = level; UnitId = unitId; }
}

/// <summary>
/// One owned guardian occurrence for the current game, following the uniform occurrence contract: the
/// author names it with an author-local key and the API allocates and owns the native identity.
/// Spawning a guardian is runtime session content; only the site/occurrence it lives on is persistent.
/// </summary>
public interface ICombatGuardian
{
    string OccurrenceKey { get; }
    GuardianSpec Spec { get; }
    GuardianState State { get; }
    AuthoredActionResult LastAction { get; }
    event Action<ICombatGuardian>? Changed;
}

/// <summary>
/// Facade over the authored-content primitives for spawning and sustaining named boss/guardian units.
/// Availability is typed and independent: registration or a healthy binding is not permission to
/// spawn in a live session.
/// </summary>
public interface ICombatGuardianService : IServiceStatus
{
    /// <summary>Acquire an authoring scope bound to one loaded plugin (main-thread-only).</summary>
    ICombatGuardianProvider? AcquireProvider(object pluginInstance);
}

/// <summary>Authoring scope for one provider; declarations are owned per provider and released on dispose.</summary>
public interface ICombatGuardianProvider : IDisposable
{
    string ProviderId { get; }
    /// <summary>
    /// Creates (or reconciles) a guardian occurrence in an existing combat-site occurrence for the
    /// current game, keyed by an author-local occurrence key. Returns null while the world cannot
    /// author. Re-declaring the same key returns the SAME instance for the life of the session.
    /// </summary>
    ICombatGuardian? Create(string localId, string occurrenceKey, string siteOccurrenceKey, GuardianSpec spec);
    /// <summary>Re-obtains the owned guardian occurrence for a key, or null if it does not exist.</summary>
    ICombatGuardian? Get(string localId, string occurrenceKey);
}
