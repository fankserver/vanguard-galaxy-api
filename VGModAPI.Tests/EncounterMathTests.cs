using System;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class EncounterMathTests
{
    [Theory]
    [InlineData(50, 80, 92, 20, 120, 92)]   // 50+20=70 < floor 92 -> floor
    [InlineData(110, 80, 92, 20, 120, 120)] // 110+20=130 > ceiling 120 -> capped
    [InlineData(60, 80, 92, 20, 100000, 92)] // 60+20=80 < floor 92 -> floor
    public void ResolveLevel_honours_floor_threat_and_cap(int player, int fixedLevel, int floor, int over, int ceiling, int expected)
    {
        var policy = new EncounterLevelPolicy(levelFloor: floor, threatOver: over);
        Assert.Equal(expected, EncounterMath.ResolveLevel(player, fixedLevel, policy, ceiling));
    }

    [Fact]
    public void ResolveLevel_preserves_fixed_level_when_not_dynamic()
    {
        // No level policy -> existing behavior unchanged, no added ceiling.
        Assert.Equal(95, EncounterMath.ResolveLevel(60, 95, null, 100));
    }

    [Fact]
    public void ResolveLevel_threat_only_uses_fixed_as_floor()
    {
        var policy = new EncounterLevelPolicy(threatOver: 10);
        Assert.Equal(80, EncounterMath.ResolveLevel(60, 80, policy, 100)); // max(80, 60+10)
        Assert.Equal(90, EncounterMath.ResolveLevel(80, 80, policy, 100)); // max(80, 80+10)
    }

    [Fact]
    public void ResolveLevel_floor_only_scales_with_player()
    {
        var policy = new EncounterLevelPolicy(levelFloor: 50);
        Assert.Equal(50, EncounterMath.ResolveLevel(40, 90, policy, 100)); // max(50, 40)
        Assert.Equal(80, EncounterMath.ResolveLevel(80, 90, policy, 100)); // max(50, 80)
    }

    [Fact]
    public void ApplyItemLevelCap_returns_level_at_or_below_player()
    {
        Assert.Equal(10, EncounterMath.ApplyItemLevelCap(10, 30));
        Assert.Equal(30, EncounterMath.ApplyItemLevelCap(30, 30));
    }

    [Fact]
    public void ApplyItemLevelCap_above_player_uses_ceiling_power()
    {
        // gap 20 -> 20^0.7 = 8.14 -> ceil 9 -> 30 + 9 = 39
        Assert.Equal(39, EncounterMath.ApplyItemLevelCap(50, 30));
    }

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(10, 2.0)]
    [InlineData(50, 32.0)]
    public void DamageMultiplier_is_two_to_the_quarter(int level, double expected)
    {
        Assert.Equal((float)expected, EncounterMath.DamageMultiplier(level), 5);
    }

    [Fact]
    public void ComputeDamageBoost_raises_damage_toward_requested_tier()
    {
        // Hand-computed independently of the functions under test: player 40, spawn 40 -> item-level
        // cap no-ops (achieved 40); DamageMultiplier(60)=2^6=64, DamageMultiplier(40)=2^4=16,
        // ratio 4 -> boost 3.
        Assert.Equal(40, EncounterMath.ApplyItemLevelCap(40, 40));
        Assert.Equal(3f, EncounterMath.ComputeDamageBoost(40, 40, 60), 5);
    }

    [Fact]
    public void ComputeDamageBoost_is_zero_when_requested_below_achieved()
    {
        int player = 40, spawn = 70, requested = 50;
        int achieved = EncounterMath.ApplyItemLevelCap(spawn, player);
        Assert.True(achieved >= requested);
        Assert.Equal(0f, EncounterMath.ComputeDamageBoost(player, spawn, requested));
    }
}
