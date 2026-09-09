using System.Collections.Generic;
using System.IO;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;
public sealed class WorldEmptyCombatProfileTests
{
    [Theory]
    [InlineData("units")]
    [InlineData("payloads")]
    [InlineData("combatStations")]
    [InlineData("salvageOverlay")]
    [InlineData("customFieldData")]
    [InlineData("unrecognized")]
    public void JsonProfileRefusesExecutableOrUnknownFieldsEvenWhenEmpty(string key)
    {
        var profile = new WorldJsonInspection(typeof(LightJson.JsonObject).Assembly);
        var node = new LightJson.JsonObject(); node["hasAsteroids"] = new LightJson.JsonValue(false);
        profile.RequireEmptyCombatJson(node);
        var value = new LightJson.JsonValue(new List<LightJson.JsonValue>()); node[key] = value;
        Assert.Throws<InvalidDataException>(() => profile.RequireEmptyCombatJson(node)); Assert.Same(value, node[key]);
        node.Remove(key); node["hasAsteroids"] = new LightJson.JsonValue(true);
        Assert.Throws<InvalidDataException>(() => profile.RequireEmptyCombatJson(node));
    }
    private class Poi
    {
        public List<object> persistables = new(), units = new(), payloads = new(), guardDescriptors = new(), cargoDescriptors = new(), salvageDescriptors = new();
        public HashSet<string> deadUnitIdentities = new(), deadPersistableIdentities = new(), deadSalvageIdentities = new();
        public Dictionary<string, object> unitOverlay = new(), persistableOverlay = new(), salvageOverlay = new();
        public List<object>? _pendingStationBuildings = null;
        public object? hazardFieldData = null, oreOwnershipOverride = null, oreOwnershipOverrideItem = null, storyteller = null, linkedJumpgatePassGuid = null;
        public int nextPayloadSequenceId = 0, nextCargoSlotId = 0;
        public bool hasAsteroids { get; set; }
        public object? customFieldData { get; set; }
    }
    private sealed class Combat : Poi { }
    private sealed class SubstitutedList : List<object> { }
    [Fact]
    public void RejectsPendingConstructionOverlaysAndCollectionSubclasses()
    {
        var profile = new WorldEmptyCombatProfile(typeof(Combat), typeof(Poi)); var poi = new Combat();
        poi._pendingStationBuildings = new(); profile.Require(poi);
        poi._pendingStationBuildings.Add(new object()); Assert.Throws<InvalidDataException>(() => profile.Require(poi)); poi._pendingStationBuildings.Clear();
        poi.unitOverlay.Add("unit0", new object()); Assert.Throws<InvalidDataException>(() => profile.Require(poi)); poi.unitOverlay.Clear();
        poi.deadSalvageIdentities.Add("salvage0"); Assert.Throws<InvalidDataException>(() => profile.Require(poi)); poi.deadSalvageIdentities.Clear();
        poi.linkedJumpgatePassGuid = "gate"; Assert.Throws<InvalidDataException>(() => profile.Require(poi)); poi.linkedJumpgatePassGuid = null;
        poi.persistables = new SubstitutedList(); Assert.Throws<InvalidDataException>(() => profile.Require(poi));
    }
    [Fact]
    public void RequiresEmptyCollectionsAndNoGenerationAttachments()
    {
        var profile = new WorldEmptyCombatProfile(typeof(Combat), typeof(Poi)); var poi = new Combat();
        profile.Require(poi);
        poi.units.Add(new object()); Assert.Throws<InvalidDataException>(() => profile.Require(poi)); poi.units.Clear();
        poi.customFieldData = new object(); Assert.Throws<InvalidDataException>(() => profile.Require(poi)); poi.customFieldData = null;
        poi.hasAsteroids = true; Assert.Throws<InvalidDataException>(() => profile.Require(poi)); poi.hasAsteroids = false;
        poi.nextCargoSlotId = 1; Assert.Throws<InvalidDataException>(() => profile.Require(poi)); poi.nextCargoSlotId = 0;
        poi.persistables = null!; Assert.Throws<InvalidDataException>(() => profile.Require(poi));
        Assert.Throws<InvalidDataException>(() => profile.Require(new Poi()));
    }
}
