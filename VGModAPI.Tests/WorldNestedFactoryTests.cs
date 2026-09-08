using System;
using System.Collections.Generic;
using System.IO;
using LightJson;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using Xunit;

namespace Source.Data.Persistable
{
    public abstract class PersistableData { }
    public sealed class KnownData : PersistableData { public KnownData() => throw new Exception("Must not construct during inspection"); }
}
namespace Source.Galaxy
{
    public abstract class UnitGenerationDescriptor { }
    public sealed class FixedPayloadDescriptor : UnitGenerationDescriptor { public FixedPayloadDescriptor() => throw new Exception("Must not construct during inspection"); }
}
namespace VGModAPI.Tests
{
    public sealed class WorldNestedFactoryTests
    {
        [Theory]
        [InlineData("KnownData", true, 1)]
        [InlineData("KnownData, foreign", false, 1)]
        [InlineData("KnownData+Nested", false, 1)]
        [InlineData("Missing", false, 1)]
        [InlineData("KnownData", false, -1)]
        [InlineData("KnownData", false, 129)]
        [InlineData("KnownData", false, 1.5)]
        [InlineData("KnownData", false, double.NaN)]
        [InlineData("KnownData", false, double.PositiveInfinity)]
        public void NestedSelectorsAreNativeMetadataOnly(string selector, bool allowed, double count)
        {
            var identity = new WorldObjectIdentity(new ContentDeclaration("author.a", "PoiX", PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent), Guid.NewGuid());
            var persistable = new JsonObject { ["type"] = new(selector) };
            var payload = new JsonObject
            {
                ["persistables"] = new(new List<JsonValue> { new(persistable) }),
                ["descriptor"] = new(new JsonObject { ["type"] = new("FixedPayloadDescriptor"), ["fixedUnit"] = new("NativeShip"), ["unitCount"] = new(count) })
            };
            var poi = new JsonObject { ["guid"] = new(identity.NativeId), ["type"] = new("Combat"), ["systemName"] = new("system"), ["payloads"] = new(new List<JsonValue> { new(payload) }) };
            var system = new JsonObject { ["guid"] = new("system"), ["pointsOfInterest"] = new(new List<JsonValue> { new(poi) }) };
            var root = new JsonObject { ["Player"] = new(new JsonObject { ["map"] = new(new JsonObject { ["systems"] = new(new List<JsonValue> { new(system) }) }) }) };
            var inspection = new WorldJsonInspection(typeof(JsonObject).Assembly);
            if (allowed) Assert.Single(inspection.Read(root));
            else Assert.Throws<InvalidDataException>(() => inspection.Read(root));
        }
        [Fact]
        public void UnitDispatchRemainsTheInspectedClosedSwitch()
        {
            WorldNestedTypeCatalog.Unit("SpaceShip"); WorldNestedTypeCatalog.Unit("Turret"); WorldNestedTypeCatalog.Unit("CombatStationPart");
            Assert.Throws<InvalidDataException>(() => WorldNestedTypeCatalog.Unit("CustomUnit"));
        }
    }
}
