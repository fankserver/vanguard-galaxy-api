using System;
using System.IO;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldConstructionGateTests
{
    private static readonly string Hash = new('a', 64);
    private static SnapshotAssociation Association() => new("/save/a", Hash, Hash, Guid.NewGuid(), Guid.NewGuid());
    private static WorldConstructionNode Node(string owner = "author.one") => new(new object(), new WorldObjectIdentity(
        new ContentDeclaration(owner, "PoiX", PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent), Guid.NewGuid()), Hash);

    [Fact]
    public void RefusesReservedFactoriesBeforeMetadataAndAllowsUnrelatedVanilla()
    {
        var gate = new WorldConstructionGate(); var session = Guid.NewGuid(); gate.Start(session);
        var node = Node();
        Assert.Throws<InvalidDataException>(() => gate.RequireFactory(session, node.Json, node.Identity.NativeId, Hash, 1));
        gate.RequireFactory(session, new object(), "vanilla-poi", Hash, 1);
        Assert.Throws<InvalidDataException>(() => gate.Open(session, null, "/save/a", Hash, 1, new[] { "author.one" }, new[] { node }));
    }

    [Fact]
    public void RejectedNodesCannotBecomeVanillaByStrippingTheirMarkers()
    {
        var gate = new WorldConstructionGate(); var session = Guid.NewGuid(); gate.Start(session);
        var beforeOpen = Node();
        Assert.Throws<InvalidDataException>(() => gate.RequireFactory(session, beforeOpen.Json, beforeOpen.Identity.NativeId, Hash, 1));
        Assert.Throws<InvalidDataException>(() => gate.RequireFactory(session, beforeOpen.Json, "stripped", Hash, 1));
        var missingProvider = Node();
        Assert.Throws<InvalidDataException>(() => gate.Open(session, Association(), "/save/a", Hash, 1, Array.Empty<string>(), new[] { missingProvider }));
        Assert.Throws<InvalidDataException>(() => gate.RequireFactory(session, missingProvider.Json, "stripped", Hash, 1));
        var missingMetadata = Node();
        Assert.Throws<InvalidDataException>(() => gate.Open(session, null, "/save/a", Hash, 1, Array.Empty<string>(), new[] { missingMetadata }));
        Assert.Throws<InvalidDataException>(() => gate.RequireFactory(session, missingMetadata.Json, "stripped", Hash, 1));
    }

    [Fact]
    public void ExactNodeGenerationAndProviderRevisionAreRequired()
    {
        var gate = new WorldConstructionGate(); var session = Guid.NewGuid(); gate.Start(session);
        var node = Node(); gate.Open(session, Association(), "/save/a", Hash, 1, new[] { "author.one" }, new[] { node });
        gate.RequireFactory(session, node.Json, node.Identity.NativeId, Hash, 1);
        Assert.Throws<InvalidDataException>(() => gate.RequireFactory(session, new object(), node.Identity.NativeId, Hash, 1));
        Assert.Throws<InvalidDataException>(() => gate.RequireFactory(session, node.Json, "vanilla-disguise", Hash, 1));
        Assert.Throws<InvalidDataException>(() => gate.RequireFactory(session, node.Json, node.Identity.NativeId, new string('b', 64), 1));
        Assert.Throws<InvalidDataException>(() => gate.RequireFactory(session, node.Json, node.Identity.NativeId, Hash, 2));
        Assert.Throws<InvalidDataException>(() => gate.Open(session, Association(), "/save/b", Hash, 1, new[] { "author.one" }, new[] { node }));
        Assert.Throws<InvalidDataException>(() => gate.RequireFactory(session, node.Json, node.Identity.NativeId, Hash, 1));
    }

    [Fact]
    public void MissingProviderAndDuplicateIdentityAreLoadRefusals()
    {
        var gate = new WorldConstructionGate(); var session = Guid.NewGuid(); gate.Start(session);
        var node = Node();
        Assert.Throws<InvalidDataException>(() => gate.Open(session, Association(), "/save/a", Hash, 1, Array.Empty<string>(), new[] { node }));
        var duplicate = new WorldConstructionNode(new object(), node.Identity, Hash);
        Assert.Throws<InvalidDataException>(() => gate.Open(session, Association(), "/save/a", Hash, 1, new[] { "author.one" }, new[] { node, duplicate }));
    }

    [Fact]
    public void ReplacementRejectsStaleNodesEvenAfterTheirMarkerIsStripped()
    {
        var gate = new WorldConstructionGate(); var session = Guid.NewGuid(); gate.Start(session);
        var node = Node(); gate.Open(session, Association(), "/save/a", Hash, 1, new[] { "author.one" }, new[] { node });
        gate.Invalidate();
        Assert.Throws<InvalidDataException>(() => gate.RequireFactory(session, node.Json, "vanilla-disguise", Hash, 1));
        var next = Guid.NewGuid(); gate.Start(next);
        Assert.Throws<InvalidDataException>(() => gate.RequireFactory(next, node.Json, "vanilla-disguise", Hash, 1));
        Assert.Throws<InvalidDataException>(() => gate.Open(session, Association(), "/save/a", Hash, 1, new[] { "author.one" }, new[] { node }));
    }
}
