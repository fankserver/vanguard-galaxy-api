using System;
using System.IO;
using System.Linq;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class DungeonDefinitionCodecTests
{
    private static DungeonDefinition Definition() => new(2, "Cargo survey", new DungeonLayout(new[]
    {
        new DungeonCompartmentDefinition("entry", CompartmentType.Airlock, new[] { "cargo" }),
        new DungeonCompartmentDefinition("cargo", CompartmentType.CargoHold, new[] { "entry" }, true)
    }), events: new[] { new DungeonEventDefinition("cache", "cargo", "Examine cargo", new[] { new DungeonChoiceDefinition("inspect", "Inspect", "Engineer", new[] { new DungeonLootDefinition("Ore", 2) }) }) });
    [Fact]
    public void DefinitionRoundTripsWithoutProviderCallbacksOrMutableInput()
    {
        var bytes = DungeonDefinitionCodec.Encode(Definition()); var restored = DungeonDefinitionCodec.Decode(bytes);
        Assert.Equal(2, restored.Version); Assert.True(restored.Layout.Compartments[1].Locked);
        Assert.Equal("Engineer", restored.Events[0].Choices[0].RequiredCrewId);
        Assert.Equal("Ore", restored.Events[0].Choices[0].Loot[0].ItemId);
        Assert.Equal(bytes, DungeonDefinitionCodec.Encode(restored));
    }
    [Fact]
    public void TruncationTrailingBytesUnknownSchemaAndOversizeAreRejected()
    {
        var bytes = DungeonDefinitionCodec.Encode(Definition());
        Assert.ThrowsAny<Exception>(() => DungeonDefinitionCodec.Decode(bytes.Take(bytes.Length - 1).ToArray()));
        Assert.Throws<InvalidDataException>(() => DungeonDefinitionCodec.Decode(bytes.Concat(new byte[] { 0 }).ToArray()));
        bytes[0] = 99; Assert.Throws<InvalidDataException>(() => DungeonDefinitionCodec.Decode(bytes));
        Assert.Throws<InvalidDataException>(() => DungeonDefinitionCodec.Decode(new byte[DungeonDefinitionCodec.MaximumBytes + 1]));
    }
}
