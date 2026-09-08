using System.IO;
using LightJson;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldSaveFormatTests
{
    private static WorldSaveFormat Format() => new(typeof(JsonObject).Assembly);
    [Theory]
    [InlineData("0.8.2.3")]
    [InlineData("0.9.0.0")]
    public void SealingAndVerifiedUnsealingPreserveOtherFieldsAndOriginalVersion(string version)
    {
        var player = new JsonObject { Text = "native-player-state" };
        var root = new JsonObject { ["Version"] = new(version), ["Player"] = new(player), ["extra"] = new("世界") };
        var format = Format(); format.Seal(root, true);
        Assert.Equal(WorldSaveFormat.Marker, root["Version"].AsString);
        Assert.Equal(version, root[WorldSaveFormat.OriginalVersion].AsString);
        Assert.Same(player, root["Player"].AsJsonObject);
        Assert.Throws<InvalidDataException>(() => format.Seal(root, true));
        format.UnsealVerified(root, true);
        Assert.Equal(version, root["Version"].AsString);
        Assert.False(root.ContainsKey(WorldSaveFormat.OriginalVersion));
        Assert.Same(player, root["Player"].AsJsonObject); Assert.Equal("世界", root["extra"].AsString);
    }
    [Theory]
    [InlineData("-1.0")]
    [InlineData("100.0")]
    [InlineData("1")]
    [InlineData("1.2.3.4.5")]
    [InlineData("99.99.99.99")]
    [InlineData("1. 2")]
    public void InvalidOriginalVersionsCannotBeUnsealed(string original)
    {
        var root = new JsonObject { ["Version"] = new(WorldSaveFormat.Marker), [WorldSaveFormat.OriginalVersion] = new(original) };
        Assert.Throws<InvalidDataException>(() => Format().UnsealVerified(root, true));
        Assert.Equal(WorldSaveFormat.Marker, root["Version"].AsString);
    }
    [Fact]
    public void ConflictingOrEmptyInventoryMarkersRefuseRatherThanNormalize()
    {
        var format = Format();
        var plain = new JsonObject { ["Version"] = new("0.8.2.3") };
        format.Seal(plain, false); format.UnsealVerified(plain, false);
        Assert.Throws<InvalidDataException>(() => format.UnsealVerified(plain, true));
        plain[WorldSaveFormat.OriginalVersion] = new JsonValue(null);
        Assert.Throws<InvalidDataException>(() => format.Seal(plain, false));
        Assert.Throws<InvalidDataException>(() => format.UnsealVerified(plain, false));
        plain["Version"] = new(WorldSaveFormat.Marker);
        Assert.Throws<InvalidDataException>(() => format.UnsealVerified(plain, false));
    }
}
