using System;
using System.Collections.Generic;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class OwnerSchemaCodecTests
{
    private static OwnerSchemaCodec Codec(string owner = "test.owner", int version = 1)
        => new(owner, version, b => b.Length == 1);

    [Fact]
    public void MissingCorruptAndFutureRemainDistinct()
    {
        var codec = Codec();
        Assert.Equal(SchemaReadStatus.Missing, codec.Decode(null).Status);
        Assert.Equal(SchemaReadStatus.Corrupt, codec.Decode(Array.Empty<byte>()).Status);
        Assert.Equal(SchemaReadStatus.Unsupported, codec.Decode(Codec(version: 2).Encode(new byte[] { 1 })).Status);
        var future = codec.Encode(new byte[] { 1 }); future[4] = 2;
        Assert.Equal(SchemaReadStatus.Unsupported, codec.Decode(future).Status);
    }

    [Fact]
    public void ChecksumCoversOwnerVersionAndPayload()
    {
        var codec = Codec();
        var original = codec.Encode(new byte[] { 7 });
        foreach (int index in new[] { 6, 17, original.Length - 33, original.Length - 1 })
        {
            var damaged = (byte[])original.Clone(); damaged[index] ^= 1;
            Assert.Equal(SchemaReadStatus.Corrupt, codec.Decode(damaged).Status);
        }
        Assert.Equal(SchemaReadStatus.Corrupt, Codec("other.owner").Decode(original).Status);
    }

    [Fact]
    public void ExplicitMigrationsProduceCandidateWithoutChangingSource()
    {
        var original = Codec().Encode(new byte[] { 1 });
        var copy = (byte[])original.Clone();
        var codec = new OwnerSchemaCodec("test.owner", 3, b => b[0] == 3,
            new Dictionary<int, Func<byte[], byte[]>> { [1] = b => new byte[] { 2 }, [2] = b => new byte[] { 3 } });
        var result = codec.Decode(original);
        Assert.Equal(SchemaReadStatus.Ready, result.Status);
        Assert.True(result.Migrated);
        Assert.Equal(new byte[] { 3 }, result.Payload);
        Assert.Equal(copy, original);
        var exposed = result.Payload!; exposed[0] = 99;
        Assert.Equal(new byte[] { 3 }, result.Payload);
    }

    [Fact]
    public void FailedMutatingMigrationPreservesSourceAndDoesNotBlockAnotherOwner()
    {
        var original = Codec().Encode(new byte[] { 1 });
        var copy = (byte[])original.Clone();
        var bad = new OwnerSchemaCodec("test.owner", 2, _ => true,
            new Dictionary<int, Func<byte[], byte[]>> { [1] = b => { b[0] = 9; throw new Exception("provider failure"); } });
        Assert.Equal(SchemaReadStatus.MigrationFailed, bad.Decode(original).Status);
        Assert.Equal(copy, original);
        var other = Codec("other.owner");
        Assert.Equal(SchemaReadStatus.Ready, other.Decode(other.Encode(new byte[] { 2 })).Status);
    }

    [Fact]
    public void MissingMigrationAndInvalidMigratedPayloadAreNotEmptySuccess()
    {
        var source = Codec().Encode(new byte[] { 1 });
        Assert.Equal(SchemaReadStatus.MigrationFailed, Codec(version: 2).Decode(source).Status);
        var invalid = new OwnerSchemaCodec("test.owner", 2, _ => false,
            new Dictionary<int, Func<byte[], byte[]>> { [1] = b => b });
        Assert.Equal(SchemaReadStatus.MigrationFailed, invalid.Decode(source).Status);
    }

    [Fact]
    public void BoundsAndNamespaceAreEnforced()
    {
        Assert.Throws<ArgumentException>(() => Codec("../owner"));
        Assert.Throws<ArgumentException>(() => Codec().Encode(new byte[OwnerSchemaCodec.MaxPayload + 1]));
        Assert.Equal(SchemaReadStatus.Corrupt, Codec().Decode(new byte[OwnerSchemaCodec.MaxPayload + 129]).Status);
        var source = Codec().Encode(new byte[] { 1 });
        Array.Resize(ref source, source.Length - 1);
        Assert.Equal(SchemaReadStatus.Corrupt, Codec().Decode(source).Status);
    }
}
