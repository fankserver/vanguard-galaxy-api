using System;
using System.IO;
using System.Linq;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldDefinitionCodecTests
{
    private static WorldSavedDefinition Definition(string owner = "author.a", string name = "世界-é", int revision = 1, string faction = "player", int level = 2) =>
        new(owner, new WorldCombatDefinition("PoiX", revision, name, faction, level));

    [Fact]
    public void RetainedDeclarationsRoundtripWithoutOwnerCollision()
    {
        var bytes = WorldDefinitionCodec.Encode(new[] { Definition(), Definition("author.b") });
        var rows = WorldDefinitionCodec.Decode(bytes);
        Assert.Equal(2, rows.Length); Assert.Equal("世界-é", rows[0].Definition.Name);
        Assert.Equal("author.b", rows[1].Owner); Assert.Equal(bytes, WorldDefinitionCodec.Encode(rows));
        Assert.Throws<InvalidDataException>(() => WorldDefinitionCodec.Encode(new[] { Definition(), Definition() }));
    }

    [Fact]
    public void InvalidDefinitionsNeverBecomeAnEmptyRegistry()
    {
        var bytes = WorldDefinitionCodec.Encode(new[] { Definition() });
        for (int i = 0; i < bytes.Length; i++) Assert.Throws<InvalidDataException>(() => WorldDefinitionCodec.Decode(bytes.Take(i).ToArray()));
        Assert.Throws<InvalidDataException>(() => WorldDefinitionCodec.Decode(bytes.Concat(new byte[] { 0 }).ToArray()));
        var future = (byte[])bytes.Clone(); future[4] = 2;
        Assert.Throws<InvalidDataException>(() => WorldDefinitionCodec.Decode(future));
        var invalid = (byte[])bytes.Clone(); invalid[16] = 0xff;
        Assert.Throws<InvalidDataException>(() => WorldDefinitionCodec.Decode(invalid));
    }

    [Fact]
    public void RevisionEqualityDoesNotHideChangedDefinitionFields()
    {
        var assembly = typeof(WorldDefinitionCodecTests).Assembly;
        using var registry = new WorldDefinitionRegistry((_, caller) => new StoryHostPlugin("author.a", caller), () => { });
        var provider = registry.Acquire(new object(), assembly)!;
        Assert.True(provider.Register(Definition().Definition));
        Assert.True(registry.MatchesRetained(Definition()));
        Assert.False(registry.MatchesRetained(Definition(name: "changed")));
        Assert.False(registry.MatchesRetained(Definition(revision: 2)));
        Assert.False(registry.MatchesRetained(Definition(faction: "other")));
        Assert.False(registry.MatchesRetained(Definition(level: 3)));
        Assert.False(registry.MatchesRetained(Definition("author.b")));
    }
}
