using System;
using System.IO;
using System.Linq;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class DungeonStateCodecTests
{
    private static DungeonDefinition Definition() => new(1, "Dungeon", new DungeonLayout(new[]
    {
        new DungeonCompartmentDefinition("entry", CompartmentType.Airlock, new[] { "room" }),
        new DungeonCompartmentDefinition("room", CompartmentType.Corridor, new[] { "entry" })
    }));
    [Fact]
    public void IndependentOccurrencesAndProviderNamespacesRoundTripWithoutDefinitionsRegistered()
    {
        var a = new DungeonOccurrence(Guid.NewGuid(), new("a", "shared"), Definition());
        var b = new DungeonOccurrence(Guid.NewGuid(), new("a", "shared"), Definition());
        var c = new DungeonOccurrence(Guid.NewGuid(), new("b", "shared"), Definition());
        var bytes = DungeonStateCodec.Encode(new[] { a, b, c }); var restored = DungeonStateCodec.Decode(bytes);
        Assert.Equal(3, restored.Count); Assert.Equal(3, restored.Select(e => e.Id).Distinct().Count());
        Assert.Equal(2, restored.Count(e => e.DefinitionId.ProviderId == "a"));
        Assert.Equal(bytes, DungeonStateCodec.Encode(restored));
    }
    [Fact]
    public void DuplicateIdentitiesAndCorruptPayloadsAreRejected()
    {
        var entry = new DungeonOccurrence(Guid.NewGuid(), new("a", "id"), Definition());
        Assert.Throws<InvalidDataException>(() => DungeonStateCodec.Encode(new[] { entry, entry }));
        var bytes = DungeonStateCodec.Encode(new[] { entry });
        Assert.False(DungeonStateCodec.Validate(bytes.Take(bytes.Length - 1).ToArray()));
        Assert.False(DungeonStateCodec.Validate(bytes.Concat(new byte[] { 0 }).ToArray()));
        bytes[0] = 99; Assert.False(DungeonStateCodec.Validate(bytes));
    }
}
