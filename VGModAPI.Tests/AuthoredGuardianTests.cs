using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace VGModAPI.Tests;

public sealed class AuthoredLoadoutTests
{
    [Theory]
    [InlineData(AuthoredRarity.Standard)]
    [InlineData(AuthoredRarity.Enhanced)]
    [InlineData(AuthoredRarity.HighGrade)]
    [InlineData(AuthoredRarity.Exotic)]
    [InlineData(AuthoredRarity.Legendary)]
    public void All_rarities_accepted(AuthoredRarity rarity)
    {
        var loadout = new AuthoredLoadout(rarity: rarity);
        Assert.Equal(rarity, loadout.Rarity);
        Assert.Null(loadout.RequestedDamageLevel);
    }

    [Fact]
    public void Damage_level_alone_accepted()
    {
        var loadout = new AuthoredLoadout(requestedDamageLevel: 120);
        Assert.Null(loadout.Rarity);
        Assert.Equal(120, loadout.RequestedDamageLevel);
    }

    [Fact]
    public void Combines_rarity_and_damage_level()
    {
        var loadout = new AuthoredLoadout(AuthoredRarity.Legendary, 140);
        Assert.Equal(AuthoredRarity.Legendary, loadout.Rarity);
        Assert.Equal(140, loadout.RequestedDamageLevel);
    }

    [Fact]
    public void Rejects_empty_loadout()
    {
        Assert.Throws<ArgumentException>(() => new AuthoredLoadout());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(100001)]
    public void Rejects_invalid_damage_level(int level)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AuthoredLoadout(requestedDamageLevel: level));
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

public sealed class GuardianSpecTests
{
    [Fact]
    public void Valid_spec_accepted()
    {
        var spec = new GuardianSpec("Redemption", "rogue", 92,
            loadout: new AuthoredLoadout(AuthoredRarity.Legendary, 120),
            levelPolicy: new EncounterLevelPolicy(levelFloor: 92, threatOver: 20));
        Assert.Equal("Redemption", spec.ShipClassId);
        Assert.Equal(120, spec.Loadout!.RequestedDamageLevel);
        Assert.Equal(20, spec.LevelPolicy!.ThreatOver);
    }

    [Theory]
    [InlineData("", 92)]
    [InlineData(null, 92)]
    public void Rejects_blank_ship_class(string? id, int level)
    {
        Assert.Throws<ArgumentException>(() => new GuardianSpec(id!, "f", level));
    }

    [Fact]
    public void Rejects_blank_faction()
    {
        Assert.Throws<ArgumentException>(() => new GuardianSpec("Redemption", " ", 92));
    }
}

public sealed class EncounterCompositionGuardianOptionsTests
{
    [Fact]
    public void Composition_carries_level_policy_and_loadout()
    {
        var wave = new EncounterWave(1f, "Redemption", 1);
        var policy = new EncounterLevelPolicy(levelFloor: 92, threatOver: 20);
        var loadout = new AuthoredLoadout(AuthoredRarity.Legendary, 130);
        var comp = new EncounterComposition(new[] { wave }, "rogue", 92,
            rank: EncounterRank.Elite, hostileToPlayer: true, noReputationLoss: true, policy, loadout);
        Assert.Same(policy, comp.LevelPolicy);
        Assert.Same(loadout, comp.Loadout);
        Assert.Single(comp.Waves);
    }

    [Fact]
    public void Composition_without_options_keeps_legacy_shape()
    {
        var comp = new EncounterComposition(new[] { new EncounterWave(1f, "Redemption", 1) }, "rogue", 92);
        Assert.Null(comp.LevelPolicy);
        Assert.Null(comp.Loadout);
    }
}
