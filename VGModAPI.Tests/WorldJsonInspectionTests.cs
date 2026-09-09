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
    public void SalvageSelectorAndPrimaryGenerationInputsAreInspectedWithoutConstruction()
    {
        var poi = Poi(Identity().NativeId);
        var descriptor = new JsonObject { ["type"] = new("StandardSalvageDescriptor"), ["shipTemplate"] = new("NativeShip"),
            ["level"] = new(1), ["itemCount"] = new(-1), ["itemRarity"] = new(1), ["totalSalvageTypes"] = new(2),
            ["scrapValueMultiplier"] = new(1), ["structuralAmountMultiplier"] = new(1),
            ["positionOffset"] = new(new JsonObject { ["x"] = new(0), ["y"] = new(0) }),
            ["velocity"] = new(new JsonObject { ["x"] = new(0), ["y"] = new(0) }),
            ["angle"] = new(0), ["angularVelocity"] = new(0), ["initialBattleDamage"] = new(0), ["showOutline"] = new(true), ["hasHazard"] = new(false) };
        descriptor["initialBattleDamagePoints"] = new(new List<JsonValue> { new(new JsonObject { ["size"] = new(1), ["core"] = new(false),
            ["position"] = new(new JsonObject { ["x"] = new(0), ["y"] = new(0) }) }) });
        descriptor["literalLootItems"] = new(new List<JsonValue> { new("NativeItem") });
        poi["salvageDescriptors"] = new(new List<JsonValue> { new(descriptor) });
        var reader = new WorldJsonInspection(typeof(JsonObject).Assembly);
        Assert.Single(reader.Read(Root(poi)));
        var leveled = new JsonObject { ["itemTypeId"] = new("NativeItem"), ["level"] = new(0) };
        descriptor["literalLootItems"] = new(new List<JsonValue> { new(leveled) });
        Assert.Single(reader.Read(Root(poi)));
        leveled["loreKey"] = new("mutates shared template");
        Assert.Throws<InvalidDataException>(() => reader.Read(Root(poi))); leveled.Remove("loreKey");
        var singleton = typeof(Behaviour.Util.PersistentSingleton<Behaviour.GameManager>).GetField("instance", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var previous = singleton.GetValue(null);
        try
        {
            leveled["level"] = new(1); singleton.SetValue(null, null);
            Assert.Throws<InvalidDataException>(() => reader.Read(Root(poi)));
            var manager = new Behaviour.GameManager(); singleton.SetValue(null, manager);
            var parsed = Assert.Single(reader.Read(Root(poi)));
            manager.itemBuilderRoot = new UnityEngine.Transform();
            Assert.Throws<InvalidDataException>(() => parsed.ValidateAssets());
        }
        finally { singleton.SetValue(null, previous); }
        descriptor["literalLootItems"] = new(new List<JsonValue> { new("UnknownItem") });
        Assert.Throws<InvalidDataException>(() => reader.Read(Root(poi)));
        descriptor["literalLootItems"] = new(new List<JsonValue> { new(new JsonObject { ["equipmentType"] = new("Native") }) });
        Assert.Throws<InvalidDataException>(() => reader.Read(Root(poi)));
        descriptor["literalLootItems"] = new(new List<JsonValue> { new("NativeItem") });
        descriptor["hasHazard"] = new(true); descriptor["hazardName"] = new("Uninspected");
        Assert.Throws<InvalidDataException>(() => reader.Read(Root(poi)));
        descriptor["hasHazard"] = new(false);
        descriptor["initialBattleDamagePoints"] = new(new List<JsonValue> { new(new JsonObject { ["size"] = new(129) }) });
        Assert.Throws<InvalidDataException>(() => reader.Read(Root(poi)));
        descriptor.Remove("initialBattleDamagePoints");
        descriptor["velocity"].AsJsonObject["x"] = new(double.NaN);
        Assert.Throws<InvalidDataException>(() => reader.Read(Root(poi)));
        descriptor["velocity"].AsJsonObject["x"] = new(0);
        descriptor["itemCount"] = new(129);
        Assert.Throws<InvalidDataException>(() => reader.Read(Root(poi)));
        descriptor["itemCount"] = new(1); descriptor["type"] = new("UnknownDescriptor");
        Assert.Throws<InvalidDataException>(() => reader.Read(Root(poi)));
    }

    [Fact]
    public void DeferredCargoGenerationChecksCountsGeometryAndSlotOverflow()
    {
        var poi = Poi(Identity().NativeId);
        var descriptor = new JsonObject { ["count"] = new(128), ["spawnChance"] = new(0.5), ["startSlotId"] = new(0),
            ["poiSize"] = new(new JsonObject { ["x"] = new(100), ["y"] = new(50) }) };
        var descriptors = new List<JsonValue> { new(descriptor) }; poi["cargoDescriptors"] = new(descriptors);
        var reader = new WorldJsonInspection(typeof(JsonObject).Assembly);
        Assert.Single(reader.Read(Root(poi)));
        descriptor["startSlotId"] = new(int.MaxValue);
        Assert.Throws<InvalidDataException>(() => reader.Read(Root(poi)));
        descriptor["startSlotId"] = new(0); descriptor["spawnChance"] = new(1.1);
        Assert.Throws<InvalidDataException>(() => reader.Read(Root(poi)));
        descriptor["spawnChance"] = new(0.5);
        for (int i = 1; i < 9; i++) descriptors.Add(new(descriptor));
        Assert.Throws<InvalidDataException>(() => reader.Read(Root(poi)));
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
