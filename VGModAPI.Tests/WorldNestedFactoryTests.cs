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
    public sealed class KnownDescriptor : UnitGenerationDescriptor { public KnownDescriptor() => throw new Exception("Must not construct during inspection"); }
}
namespace VGModAPI.Tests
{
    public sealed class WorldNestedFactoryTests
    {
        [Theory]
        [InlineData("KnownData", true)]
        [InlineData("KnownData, foreign", false)]
        [InlineData("KnownData+Nested", false)]
        [InlineData("Missing", false)]
        public void NestedSelectorsAreNativeMetadataOnly(string selector, bool allowed)
        {
            var identity = new WorldObjectIdentity(new ContentDeclaration("author.a", "PoiX", PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent), Guid.NewGuid());
            var persistable = new JsonObject { ["type"] = new(selector) };
            var payload = new JsonObject
            {
                ["persistables"] = new(new List<JsonValue> { new(persistable) }),
                ["descriptor"] = new(new JsonObject { ["type"] = new("KnownDescriptor") })
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
