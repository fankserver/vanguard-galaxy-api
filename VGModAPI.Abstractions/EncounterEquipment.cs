using System;

namespace VGModAPI;

/// <summary>
/// Item-equipment rarity an authored encounter can force onto its spawned units. Mirrors the vanilla
/// rarity names (<c>Source.Item.Rarity</c>: Standard, Enhanced, HighGrade, Exotic, Legendary); the
/// installed game build decides whether a given name is supported. Distinct from unit rank
/// (<see cref="EncounterRank"/>, which mirrors <c>UnitRank</c>).
/// </summary>
public enum ItemRarity { Standard, Enhanced, HighGrade, Exotic, Legendary }

/// <summary>
/// Optional authored equipment override applied when an encounter spawns. Only supplied aspects
/// change; everything else stays vanilla. Forcing <see cref="OverrideRarity"/> sets the spawned
/// unit's <c>shipRarity</c> and re-rolls its equipment; an <see cref="OverrideDamageLevel"/> raises
/// the unit's outgoing damage so its weapons hit at that damage tier despite the game's item-level
/// cap (an <c>EquipStat.Damage</c> stat boost). Runtime-only: nothing is written to saves.
/// </summary>
public sealed class EncounterEquipmentOverride
{
    /// <summary>Forced equipment rarity, or null to keep the vanilla roll.</summary>
    public ItemRarity? OverrideRarity { get; }
    /// <summary>
    /// Intended outgoing-damage tier (a level). The API raises the spawned unit's outgoing damage
    /// from its achieved (item-level-capped) effective level up to this tier, no-op when the achieved
    /// level already meets or exceeds it. Null keeps vanilla damage.
    /// </summary>
    public int? OverrideDamageLevel { get; }

    public EncounterEquipmentOverride(ItemRarity? overrideRarity = null, int? overrideDamageLevel = null)
    {
        if (overrideRarity is { } rarity && !Enum.IsDefined(typeof(ItemRarity), rarity))
            throw new ArgumentOutOfRangeException(nameof(overrideRarity));
        if (overrideDamageLevel is { } level && (level < 1 || level > 100000))
            throw new ArgumentOutOfRangeException(nameof(overrideDamageLevel));
        if (overrideRarity == null && overrideDamageLevel == null)
            throw new ArgumentException("At least one equipment aspect is required.", nameof(overrideRarity));
        OverrideRarity = overrideRarity; OverrideDamageLevel = overrideDamageLevel;
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
