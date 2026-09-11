using System;
using Xunit;

namespace VGModAPI.Tests;

public sealed class EncounterEquipmentOverrideTests
{
    [Theory]
    [InlineData(ItemRarity.Standard)]
    [InlineData(ItemRarity.Enhanced)]
    [InlineData(ItemRarity.HighGrade)]
    [InlineData(ItemRarity.Exotic)]
    [InlineData(ItemRarity.Legendary)]
    public void All_rarities_accepted(ItemRarity rarity)
    {
        var equipment = new EncounterEquipmentOverride(overrideRarity: rarity);
        Assert.Equal(rarity, equipment.OverrideRarity);
        Assert.Null(equipment.OverrideDamageLevel);
    }

    [Fact]
    public void Damage_level_alone_accepted()
    {
        var equipment = new EncounterEquipmentOverride(overrideDamageLevel: 120);
        Assert.Null(equipment.OverrideRarity);
        Assert.Equal(120, equipment.OverrideDamageLevel);
    }

    [Fact]
    public void Combines_rarity_and_damage_level()
    {
        var equipment = new EncounterEquipmentOverride(ItemRarity.Legendary, 140);
        Assert.Equal(ItemRarity.Legendary, equipment.OverrideRarity);
        Assert.Equal(140, equipment.OverrideDamageLevel);
    }

    [Fact]
    public void Rejects_empty_override()
    {
        Assert.Throws<ArgumentException>(() => new EncounterEquipmentOverride());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(100001)]
    public void Rejects_invalid_damage_level(int level)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EncounterEquipmentOverride(overrideDamageLevel: level));
    }
}

public sealed class EncounterLevelPolicyTests
{
    [Fact]
    public void Dynamic_when_either_aspect_set()
    {
        Assert.True(new EncounterLevelPolicy(levelFloor: 1).Dynamic);
        Assert.True(new EncounterLevelPolicy(threatOver: 0).Dynamic);
    }

    [Fact]
    public void Rejects_empty_policy()
    {
        Assert.Throws<ArgumentException>(() => new EncounterLevelPolicy());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rejects_invalid_floor(int floor)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EncounterLevelPolicy(levelFloor: floor));
    }

    [Fact]
    public void Rejects_negative_threat()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EncounterLevelPolicy(threatOver: -1));
    }
}

public sealed class EncounterCompositionEquipmentOptionsTests
{
    [Fact]
    public void Composition_carries_level_policy_and_equipment_override()
    {
        var wave = new EncounterWave(1f, "Redemption", 1);
        var policy = new EncounterLevelPolicy(levelFloor: 92, threatOver: 20);
        var equipment = new EncounterEquipmentOverride(ItemRarity.Legendary, 130);
        var comp = new EncounterComposition(new[] { wave }, "rogue", 92,
            rank: EncounterRank.Elite, hostileToPlayer: true, noReputationLoss: true, policy, equipment);
        Assert.Same(policy, comp.LevelPolicy);
        Assert.Same(equipment, comp.EquipmentOverride);
        Assert.Single(comp.Waves);
    }

    [Fact]
    public void Composition_without_options_keeps_legacy_shape()
    {
        var comp = new EncounterComposition(new[] { new EncounterWave(1f, "Redemption", 1) }, "rogue", 92);
        Assert.Null(comp.LevelPolicy);
        Assert.Null(comp.EquipmentOverride);
    }

    [Fact]
    public void Spawn_result_surfaces_owned_unit_ids()
    {
        var result = new EncounterSpawnResult(AuthoredActionStatus.Succeeded, 2, unitIds: new[] { "unit-a", "unit-b" });
        Assert.True(result.Succeeded);
        Assert.Equal(2, result.ScheduledUnits);
        Assert.Equal(new[] { "unit-a", "unit-b" }, result.UnitIds);
        Assert.Empty(new EncounterSpawnResult(AuthoredActionStatus.Unavailable).UnitIds);
    }
}
