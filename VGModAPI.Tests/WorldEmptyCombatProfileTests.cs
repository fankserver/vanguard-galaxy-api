using System.Collections.Generic;
using System.IO;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;
public sealed class WorldEmptyCombatProfileTests
{
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
