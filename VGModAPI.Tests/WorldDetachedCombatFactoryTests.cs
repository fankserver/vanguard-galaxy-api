using System;
using System.IO;
using Source.Galaxy;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using Xunit;

namespace Source.Galaxy.POI { public sealed class Combat : MapPointOfInterest { } }
namespace VGModAPI.Tests
{
    public sealed class WorldDetachedCombatFactoryTests
    {
        [Fact]
        public void CreationDoesNotAttachMoveNeighboursOrConstructFactions()
        {
            var identity = new WorldObjectIdentity(new ContentDeclaration("author.a", "PoiX", PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent), Guid.NewGuid());
            // A unique already-loaded catalog entry avoids changing shared faction fixtures.
            string factionId = "world.factory.test";
            var faction = new Faction(); Faction.allFactions.Add(factionId, faction);
            try
            {
                var definition = new WorldSavedDefinition("author.a", new WorldCombatDefinition("PoiX", 1, "世界", factionId, 3));
                var system = new SystemMapData();
                var neighbour = new MapPointOfInterest { position = new UnityEngine.Vector2 { x = 1, y = 2 } };
                system.pointsOfInterest.Add(neighbour);
                var factory = new WorldDetachedCombatFactory(typeof(MapElement).Assembly);
                Assert.Throws<InvalidDataException>(() => factory.Create(definition, identity, system, 1, 2));
                var created = Assert.IsType<Source.Galaxy.POI.Combat>(factory.Create(definition, identity, system, 10, 20));
                Assert.Same(neighbour, Assert.Single(system.pointsOfInterest));
                Assert.Equal(1, neighbour.position.x); Assert.Equal(2, neighbour.position.y); Assert.Equal(0, neighbour.NameReads);
                Assert.Equal(identity.NativeId, created.guid); Assert.Equal("世界", created.name); Assert.Equal(3, created.level);
                Assert.Same(faction, created.faction); Assert.Same(system, created.system);
                Assert.InRange(created.backgroundSeed, 0UL, uint.MaxValue); Assert.InRange(created.contentSeed, 0UL, uint.MaxValue);
                var repeated = (MapPointOfInterest)factory.Create(definition, identity, system, 10, 20);
                Assert.Equal(created.backgroundSeed, repeated.backgroundSeed); Assert.Equal(created.contentSeed, repeated.contentSeed);
                var absent = new WorldSavedDefinition("author.a", new WorldCombatDefinition("PoiX", 1, "Site", "world.absent", 1));
                Assert.Throws<InvalidDataException>(() => factory.Create(absent, identity, system, 10, 20));
                Assert.False(Faction.allFactions.ContainsKey("world.absent"));
            }
            finally { Faction.allFactions.Remove(factionId); }
        }
    }
}
