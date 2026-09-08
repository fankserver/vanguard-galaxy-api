using System;
using System.Collections.Generic;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class DungeonDefinitionRegistryTests
{
    private static DungeonDefinition Definition(string crew = "Marine", string item = "ore", string faction = "native") => new(1, "Dungeon", new DungeonLayout(new[]
    {
        new DungeonCompartmentDefinition("entry", CompartmentType.Airlock, new[] { "room" }),
        new DungeonCompartmentDefinition("room", CompartmentType.CargoHold, new[] { "entry" }, defenders: new Dictionary<string, int> { [crew] = 2 })
    }), faction, new[] { new DungeonEventDefinition("cache", "room", "Cargo cache", new[] { new DungeonChoiceDefinition("take", "Take cargo", loot: new[] { new DungeonLootDefinition(item, 1) }) }) });
    private static DungeonDefinitionRegistry Registry() => new(id => id == "Marine", id => id == "ore", id => id == "native");
    [Fact]
    public void SameLocalIdIsIndependentAcrossProvidersAndOldLeaseCannotRemoveReplacement()
    {
        var registry = Registry(); var a = new DungeonDefinitionId("a", "shared"); var b = new DungeonDefinitionId("b", "shared");
        var old = registry.Register(a, Definition()); using var other = registry.Register(b, Definition());
        Assert.Equal(2, registry.Count); Assert.Throws<InvalidOperationException>(() => registry.Register(a, Definition()));
        old.Dispose(); using var replacement = registry.Register(a, Definition()); old.Dispose();
        Assert.True(registry.TryGet(a, out _)); Assert.True(registry.TryGet(b, out _)); Assert.Equal(2, registry.Count);
    }
    [Theory]
    [InlineData("invented", "ore", "native")]
    [InlineData("Marine", "invented", "native")]
    [InlineData("Marine", "ore", "invented")]
    public void UnknownNativeCatalogIdentifiersRefuseRegistrationWithoutMutation(string crew, string item, string faction)
    {
        var registry = Registry(); Assert.Throws<ArgumentException>(() => registry.Register(new("a", "dungeon"), Definition(crew, item, faction)));
        Assert.Equal(0, registry.Count);
    }
}
