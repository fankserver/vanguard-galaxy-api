using System;
using System.Collections.Generic;
using System.IO;
using LightJson;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldJsonInspectionTests
{
    private static WorldObjectIdentity Identity() => new(new ContentDeclaration("author.one", "PoiX", PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent), Guid.NewGuid());
    private static JsonObject Poi(string id, string type = WorldSaveFormat.OwnedCombatType, string parent = "system-a") => new()
    { Text = "serialized-poi", ["guid"] = new(id), ["type"] = new(type), ["systemName"] = new(parent) };
    private static JsonObject Root(JsonObject poi, bool legacy = false)
    {
        var system = new JsonObject { ["guid"] = new("system-a"), ["pointsOfInterest"] = new(new List<JsonValue> { new(poi) }) };
        var sector = new JsonObject { ["systems"] = new(new List<JsonValue> { new(system) }) };
        var map = legacy ? sector : new JsonObject { ["sectors"] = new(new List<JsonValue> { new(sector) }) };
        return new JsonObject { ["Player"] = new(new JsonObject { ["map"] = new(map) }) };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MatchesExactOwnedNodeToMetadataWithoutConstructingAnything(bool legacy)
    {
        var identity = Identity(); var poi = Poi(identity.NativeId);
        var scanner = new WorldJsonInspection(typeof(JsonObject).Assembly);
        var parsed = scanner.Read(Root(poi, legacy));
        Assert.Single(parsed); Assert.Same(poi, parsed[0].Json);
        var row = new WorldSavedObject(identity, "system-a", WorldJsonInspection.Digest(poi), 1);
        var bound = WorldJsonInspection.Bind(new[] { row }, parsed);
        Assert.Same(poi, Assert.Single(bound).Json);
        Assert.Empty(scanner.Read(Root(Poi("vanilla-id", "SpaceStation"))));
    }

    [Fact]
    public void RejectsUnsupportedOwnedTypesAndWrongParentBeforeFactory()
    {
        var scanner = new WorldJsonInspection(typeof(JsonObject).Assembly); var identity = Identity();
        Assert.Throws<InvalidDataException>(() => scanner.Read(Root(Poi(identity.NativeId, "CustomType"))));
        var oldShape = Poi(identity.NativeId, "Combat");
        Assert.Throws<InvalidDataException>(() => scanner.Read(Root(oldShape)));
        Assert.Equal("Combat", oldShape["type"].AsString);
        Assert.Single(scanner.Read(Root(oldShape), nativeSnapshot: true));
        Assert.Throws<InvalidDataException>(() => scanner.Read(Root(Poi("vanilla-id"))));
        Assert.Throws<InvalidDataException>(() => scanner.Read(Root(Poi(identity.NativeId, parent: "other-system"))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicateParentSystemsAreRejectedAcrossTheWholeMap(bool acrossSectors)
    {
        var root = Root(Poi(Identity().NativeId));
        var sectors = root["Player"].AsJsonObject["map"].AsJsonObject["sectors"].AsJsonArray;
        var systems = sectors[0].AsJsonObject["systems"].AsJsonArray;
        var earlier = new JsonObject { ["guid"] = new("system-a"), ["pointsOfInterest"] = new(new List<JsonValue>()) };
        if (acrossSectors)
            sectors.Insert(0, new(new JsonObject { ["systems"] = new(new List<JsonValue> { new(earlier) }) }));
        else systems.Insert(0, new(earlier));
        Assert.Throws<InvalidDataException>(() => new WorldJsonInspection(typeof(JsonObject).Assembly).Read(root));
    }

    [Fact]
    public void MetadataMustCoverExactlyTheParsedNodesAndMutableState()
    {
        var identity = Identity(); var poi = Poi(identity.NativeId);
        var scanner = new WorldJsonInspection(typeof(JsonObject).Assembly);
        var parsed = scanner.Read(Root(poi));
        Assert.Throws<InvalidDataException>(() => WorldJsonInspection.Bind(Array.Empty<WorldSavedObject>(), parsed));
        var wrong = new WorldSavedObject(identity, "system-a", new string('b', 64), 1);
        Assert.Throws<InvalidDataException>(() => WorldJsonInspection.Bind(new[] { wrong }, parsed));
        var row = new WorldSavedObject(identity, "system-a", parsed[0].Digest, 1);
        Assert.Throws<InvalidDataException>(() => WorldJsonInspection.Bind(new[] { row, row }, new[] { parsed[0], parsed[0] }));
    }
}
