using System;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldObjectIdentityTests
{
    private static readonly Guid Instance = Guid.Parse("89b5d079-8ec2-42a3-aa65-cd183bf5eec1");
    private static WorldObjectIdentity Identity(string owner, string local, Guid? instance = null) =>
        new(new ContentDeclaration(owner, local, PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent), instance ?? Instance);

    [Fact]
    public void ReconstructedTupleHasStableBoundedNativeIdentity()
    {
        var first = Identity("author.one", "PoiX");
        Assert.Equal(first.NativeId, Identity("author.one", "PoiX").NativeId);
        Assert.True(WorldObjectIdentity.IsReserved(first.NativeId));
        Assert.True(first.NativeId.Length <= 128);
        Assert.Equal("author.one", first.Owner);
        Assert.Equal("PoiX", first.LocalId);
        Assert.Equal(Instance, first.InstanceId);
    }

    [Fact]
    public void OwnerLocalAndInstanceRemainIndependent()
    {
        var id = Identity("author.one", "PoiX").NativeId;
        Assert.NotEqual(id, Identity("author.two", "PoiX").NativeId);
        Assert.NotEqual(id, Identity("author.one", "poix").NativeId);
        Assert.NotEqual(id, Identity("author.one", "PoiX", Guid.Parse("89b5d079-8ec2-42a3-aa65-cd183bf5eec2")).NativeId);
        Assert.NotEqual(Identity("a", "b.c").NativeId, Identity("a.b", "c").NativeId);
    }

    [Fact]
    public void RejectsInvalidIdentityInsteadOfCreatingAnAnonymousObject()
    {
        Assert.Throws<ArgumentException>(() => Identity("author.one", "PoiX", Guid.Empty));
        Assert.Throws<ArgumentException>(() => Identity("", "PoiX"));
        Assert.Throws<ArgumentException>(() => new WorldObjectIdentity(
            new ContentDeclaration("author.one", "PoiX", PersistentContentKind.Patron, ContentPersistenceImpact.ApiDependent), Instance));
    }

    [Theory]
    [InlineData("vgmodapi.world.v1.invalid", true)]
    [InlineData("vgmodapi.world.v99.future", true)]
    [InlineData("vgmodapi.world.", true)]
    [InlineData("vanilla-guid", false)]
    [InlineData(null, false)]
    public void ReservedClassificationDoesNotAdmitUnknownOrMalformedContent(string? value, bool reserved) =>
        Assert.Equal(reserved, WorldObjectIdentity.IsReserved(value));
}
