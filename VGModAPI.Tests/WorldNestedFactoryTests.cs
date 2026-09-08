using System;
using System.Collections.Generic;
using System.IO;
using LightJson;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using Xunit;

namespace Source.Combat { public enum DamageType { Kinetic } }
namespace Source.Hazard
{
    public class HazardData { }
    public sealed class KnownHazardData : HazardData { public KnownHazardData() => throw new Exception("Must not construct hazards during inspection"); }
}
namespace Behaviour.Unit { public abstract class AbstractUnit { } }
namespace Source.SpaceShip { public abstract class AutoActions { } }
namespace Source.SpaceShip.Auto
{
    public sealed class KnownActions : Source.SpaceShip.AutoActions
    {
        public KnownActions(Behaviour.Unit.AbstractUnit parent) => throw new Exception("Must not construct actions during inspection");
    }
}
namespace Source.Data.Persistable
{
    public abstract class PersistableData { }
    public sealed class KnownData : PersistableData { public KnownData() => throw new Exception("Must not construct during inspection"); }
}
namespace Source.Galaxy
{
    public abstract class UnitGenerationDescriptor { }
    public sealed class UnitPayloadDescriptor : UnitGenerationDescriptor { public UnitPayloadDescriptor() => throw new Exception("Must not generate during inspection"); }
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
            var persistable = new JsonObject
            {
                ["type"] = new(selector),
                ["hazard"] = new(new JsonObject
                {
                    ["hazard"] = new("Known"), ["damageType"] = new("Kinetic"),
                    ["damageMultiplier"] = new(1), ["maxDamageFalloff"] = new(0.5), ["range"] = new(10)
                })
            };
            var payload = new JsonObject
            {
                ["persistables"] = new(new List<JsonValue> { new(persistable) }),
                ["descriptor"] = new(new JsonObject { ["type"] = new("FixedPayloadDescriptor"), ["fixedUnit"] = new("NativeShip"), ["unitCount"] = new(count), ["autoActions"] = new("Known") })
            };
            var poi = new JsonObject { ["guid"] = new(identity.NativeId), ["type"] = new("Combat"), ["systemName"] = new("system"), ["payloads"] = new(new List<JsonValue> { new(payload) }) };
            var system = new JsonObject { ["guid"] = new("system"), ["pointsOfInterest"] = new(new List<JsonValue> { new(poi) }) };
            var root = new JsonObject { ["Player"] = new(new JsonObject { ["map"] = new(new JsonObject { ["systems"] = new(new List<JsonValue> { new(system) }) }) }) };
            var inspection = new WorldJsonInspection(typeof(JsonObject).Assembly);
            if (allowed) Assert.Single(inspection.Read(root));
            else Assert.Throws<InvalidDataException>(() => inspection.Read(root));
        }
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void BudgetExpandedDescriptorsRefuseBeforeImmediateOrDeferredGeneration(bool deferred)
        {
            var descriptor = new JsonObject
            {
                ["type"] = new("UnitPayloadDescriptor"), ["pointsScale"] = new(100),
                ["minUnits"] = new(1), ["maxUnits"] = new(128),
                ["minPointsPerUnit"] = new(0), ["maxPointsPerUnit"] = new(0)
            };
            // Previously accepted inputs: native Combat expansion alone adds 50,000 at level 10,000.
            var identity = new WorldObjectIdentity(new ContentDeclaration("author.a", "PoiX", PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent), Guid.NewGuid());
            var poi = new JsonObject { ["guid"] = new(identity.NativeId), ["type"] = new("Combat"), ["systemName"] = new("system"), ["level"] = new(10000) };
            if (deferred) poi["guardDescriptors"] = new(new List<JsonValue> { new(descriptor) });
            else poi["payloads"] = new(new List<JsonValue> { new(new JsonObject { ["descriptor"] = new(descriptor) }) });
            var system = new JsonObject { ["guid"] = new("system"), ["pointsOfInterest"] = new(new List<JsonValue> { new(poi) }) };
            var root = new JsonObject { ["Player"] = new(new JsonObject { ["map"] = new(new JsonObject { ["systems"] = new(new List<JsonValue> { new(system) }) }) }) };
            var error = Assert.Throws<InvalidDataException>(() => new WorldJsonInspection(typeof(JsonObject).Assembly).Read(root));
            Assert.Contains("Budget-expanded", error.Message);
        }
        [Theory]
        [InlineData("Known", true)]
        [InlineData("Known, foreign", false)]
        [InlineData("Known+Nested", false)]
        [InlineData("Missing", false)]
        public void AutoActionSelectorsRequireNativeConstructorMetadata(string selector, bool valid)
        {
            var catalog = new WorldNestedTypeCatalog(typeof(JsonObject).Assembly);
            if (valid) catalog.AutoActions(selector);
            else Assert.Throws<InvalidDataException>(() => catalog.AutoActions(selector));
        }
        [Fact]
        public void HazardSelectorsAndEnumsDoNotResolveProviderTypesOrNumericAliases()
        {
            var catalog = new WorldNestedTypeCatalog(typeof(JsonObject).Assembly);
            catalog.Hazard("Known");
            catalog.EnumName("Source.Combat.DamageType", "Kinetic");
            Assert.Throws<InvalidDataException>(() => catalog.Hazard("Known, foreign"));
            Assert.Throws<InvalidDataException>(() => catalog.Hazard("Missing"));
            Assert.Throws<InvalidDataException>(() => catalog.EnumName("Source.Combat.DamageType", "0"));
            Assert.Throws<InvalidDataException>(() => catalog.EnumName("Source.Combat.DamageType", "Unknown"));
        }
        [Fact]
        public void UnitDispatchRemainsTheInspectedClosedSwitch()
        {
            WorldNestedTypeCatalog.Unit("SpaceShip"); WorldNestedTypeCatalog.Unit("Turret"); WorldNestedTypeCatalog.Unit("CombatStationPart");
            Assert.Throws<InvalidDataException>(() => WorldNestedTypeCatalog.Unit("CustomUnit"));
        }
    }
}
