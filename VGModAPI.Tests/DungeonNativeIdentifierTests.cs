using System;
using System.Collections.Generic;
using VGModAPI.Core;
using Xunit;
namespace VGModAPI.Tests;

public sealed class DungeonNativeIdentifierTests
{
    private static DungeonDefinition Definition() => new(1, "Catalog identities", new DungeonLayout(new[] {
        new DungeonCompartmentDefinition("entry", CompartmentType.Airlock, new[] { "hold" }),
        new DungeonCompartmentDefinition("hold", CompartmentType.CargoHold, new[] { "entry" }, defenders: new Dictionary<string, int> { ["Combat Medic (II)"] = 1 })
    }), factionId: "Allied / Independent", events: new[] {
        new DungeonEventDefinition("cache", "hold", "Cargo", new[] {
            new DungeonChoiceDefinition("recover", "Recover", "Combat Medic (II)", new[] { new DungeonLootDefinition("Titanium Plate", 2) })
        })
    });
    [Fact]
    public void NativeCatalogKeysRoundTripVerbatimWithoutChangingLocalIds()
    {
        var definition = DungeonDefinitionCodec.Decode(DungeonDefinitionCodec.Encode(Definition()));
        Assert.Equal("Allied / Independent", definition.FactionId);
        Assert.Contains("Combat Medic (II)", definition.Layout.Compartments[1].Defenders.Keys);
        Assert.Equal("Combat Medic (II)", definition.Events[0].Choices[0].RequiredCrewId);
        Assert.Equal("Titanium Plate", definition.Events[0].Choices[0].Loot[0].ItemId);
        Assert.Throws<ArgumentException>(() => new DungeonDefinitionId("author with spaces", "local"));
        Assert.Throws<ArgumentException>(() => new DungeonChoiceDefinition("local with spaces", "Text"));
    }
    [Theory]
    [InlineData("item")] [InlineData("crew")] [InlineData("faction")]
    public void BroaderSyntaxStillRequiresNativeCatalogMembership(string missing)
    {
        var registry = new DungeonDefinitionRegistry(_ => missing != "crew", _ => missing != "item", _ => missing != "faction");
        Assert.Throws<ArgumentException>(() => registry.Register(new("author", "local"), Definition()));
        Assert.Equal(0, registry.Count);
    }
    [Fact]
    public void MatchingCatalogKeysRegister()
    {
        var registry = new DungeonDefinitionRegistry(id => id == "Combat Medic (II)", id => id == "Titanium Plate", id => id == "Allied / Independent");
        using var registration = registry.Register(new("author", "local"), Definition());
        Assert.Equal(1, registry.Count);
    }
    [Theory]
    [InlineData("")] [InlineData(" ")] [InlineData("bad\nkey")] [InlineData("bad\0key")]
    public void NativeKeysRejectBlankAndControlCharacters(string key)
    {
        Assert.Throws<ArgumentException>(() => new DungeonLootDefinition(key, 1));
        Assert.Throws<ArgumentException>(() => new DungeonChoiceDefinition("local", "Text", key));
    }
    [Fact]
    public void NativeKeyLengthRemainsCompatibleWithSavedCodecBounds()
    {
        Assert.Equal(128, new DungeonLootDefinition(new string('a', 128), 1).ItemId.Length);
        Assert.Throws<ArgumentException>(() => new DungeonLootDefinition(new string('a', 129), 1));
    }
}
