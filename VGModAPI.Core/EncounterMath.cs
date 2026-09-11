using System;

namespace VGModAPI.Core;

/// <summary>
/// Pure resolver for an authored guardian's dynamic level and outgoing-damage overclock. These
/// mirror the vanilla game's own scaling helpers (GameMath.ApplyItemLevelCap / DamageMultiplier /
/// maxLevel); the runtime path calls those native methods, this mirror exists so the policy math is
/// unit-testable without the game. Never drift: if the game changes the formula the runtime binding
/// must be re-inspected, and this mirror updated to match.
/// </summary>
internal static class EncounterMath
{
    /// <summary>
    /// Resolves the level to spawn at. Dynamic scaling (an <see cref="EncounterLevelPolicy"/> with at
    /// least one aspect set) yields min(max(floor, playerLevel + threatOver), levelCeiling); the fixed
    /// path is returned unchanged, preserving existing behavior exactly (no added ceiling).
    /// </summary>
    internal static int ResolveLevel(int playerLevel, int fixedLevel, EncounterLevelPolicy? policy, int levelCeiling)
    {
        if (policy == null || !policy.Dynamic) return fixedLevel;
        int floor = policy.LevelFloor ?? fixedLevel;
        int threat = playerLevel + (policy.ThreatOver ?? 0);
        int raw = Math.Max(floor, threat);
        return Math.Min(raw, levelCeiling);
    }

    /// <summary>Mirror of GameMath.ApplyItemLevelCap: playerLevel + ceil((level-playerLevel)^0.7) when above the player.</summary>
    internal static int ApplyItemLevelCap(int level, int playerLevel)
    {
        if (level <= playerLevel) return level;
        double gap = level - playerLevel;
        return playerLevel + (int)Math.Ceiling(Math.Pow(gap, 0.7));
    }

    /// <summary>Mirror of GameMath.DamageMultiplier: 2^(level/10).</summary>
    internal static float DamageMultiplier(int level) => (float)Math.Pow(2.0, level / 10.0);

    /// <summary>
    /// Fractional outgoing-damage boost so the unit's weapons hit at <paramref name="requestedDamageLevel"/>
    /// rather than its item-level-capped effective level. No-op (returns 0) when the achieved level already
    /// meets or exceeds the requested tier.
    /// </summary>
    internal static float ComputeDamageBoost(int playerLevel, int spawnLevel, int requestedDamageLevel)
    {
        int achieved = ApplyItemLevelCap(spawnLevel, playerLevel);
        float ratio = DamageMultiplier(requestedDamageLevel) / DamageMultiplier(achieved);
        return Math.Max(0f, ratio - 1f);
    }
}
