using System;
using System.IO;
using System.Linq;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldStateCodecTests
{
    private static WorldSavedObject Row(string owner = "author.one", string system = "系统-é") => new(
        new WorldObjectIdentity(new ContentDeclaration(owner, "PoiX", PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent), Guid.NewGuid()),
        system, new string('a', 64), 2);

    [Fact]
    public void OwnershipInstancesAndUnicodeSurviveRoundtrip()
    {
        var rows = new[] { Row(), Row("author.two"), Row() };
        var bytes = WorldStateCodec.Encode(rows);
        var decoded = WorldStateCodec.Decode(bytes);
        Assert.Equal(rows.Select(row => row.Identity.NativeId), decoded.Select(row => row.Identity.NativeId));
        Assert.All(decoded, row => { Assert.Equal("系统-é", row.SystemId); Assert.Equal(2, row.DefinitionRevision); Assert.Equal(new string('a', 64), row.NativeDigest); });
        Assert.Equal(bytes, WorldStateCodec.Encode(decoded));
        Assert.Empty(WorldStateCodec.Decode(WorldStateCodec.Encode(Array.Empty<WorldSavedObject>())));
    }

    [Fact]
    public void MissingTruncatedTrailingAndFutureDataNeverBecomeEmptyState()
    {
        var bytes = WorldStateCodec.Encode(new[] { Row() });
        for (int length = 0; length < bytes.Length; length++)
            Assert.Throws<InvalidDataException>(() => WorldStateCodec.Decode(bytes.Take(length).ToArray()));
        Assert.Throws<InvalidDataException>(() => WorldStateCodec.Decode(bytes.Concat(new byte[] { 0 }).ToArray()));
        var future = (byte[])bytes.Clone(); future[4] = 2;
        Assert.Throws<InvalidDataException>(() => WorldStateCodec.Decode(future));
        var hugeText = (byte[])bytes.Clone();
        Array.Copy(BitConverter.GetBytes(int.MaxValue), 0, hugeText, 12, 4);
        Assert.Throws<InvalidDataException>(() => WorldStateCodec.Decode(hugeText));
    }

    [Fact]
    public void RejectsDuplicatesOnEncodeAndDecode()
    {
        var row = Row();
        Assert.Throws<InvalidDataException>(() => WorldStateCodec.Encode(new[] { row, row }));
        var encoded = WorldStateCodec.Encode(new[] { row });
        var duplicate = encoded.Concat(encoded.Skip(12)).ToArray();
        Array.Copy(BitConverter.GetBytes(2), 0, duplicate, 8, 4);
        Assert.Throws<InvalidDataException>(() => WorldStateCodec.Decode(duplicate));
    }

    [Fact]
    public void RejectsOversizedInventoryAndInvalidUtf8()
    {
        Assert.Throws<InvalidDataException>(() => WorldStateCodec.Encode(new WorldSavedObject[WorldSerializationAssociation.MaxObjects + 1]));
        var bytes = WorldStateCodec.Encode(new[] { Row() });
        bytes[16] = 0xff;
        Assert.Throws<InvalidDataException>(() => WorldStateCodec.Decode(bytes));
        Assert.Throws<ArgumentException>(() => Row(system: new string('界', 43)));
        Assert.Throws<System.Text.EncoderFallbackException>(() => Row(system: "\ud800"));
    }
}
