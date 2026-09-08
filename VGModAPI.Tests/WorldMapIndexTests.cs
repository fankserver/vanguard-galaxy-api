using System.Collections.Generic;
using System.IO;
using Source.Galaxy;
using VGModAPI.Core.Integration;
using Xunit;

namespace Source.Galaxy
{
    public sealed partial class GalaxyMapData
    {
        private readonly List<SectorMapData> sectors = new();
        public List<SectorMapData> TestSectors => sectors;
    }
    public sealed class SectorMapData : MapElement
    {
        private readonly List<SystemMapData> systems = new();
        public List<SystemMapData> TestSystems => systems;
    }
}
namespace VGModAPI.Tests
{
    public sealed class WorldMapIndexTests
    {
        [Fact]
        public void FindsAcrossSystemsAndRejectsAmbiguousOrDanglingMembership()
        {
            var map = new GalaxyMapData(); var sector = new SectorMapData { guid = "sector" };
            var first = new SystemMapData { guid = "first" }; var second = new SystemMapData { guid = "second" };
            var poi = new MapPointOfInterest { guid = "poi", system = second };
            map.TestSectors.Add(sector); sector.TestSystems.AddRange(new[] { first, second }); second.pointsOfInterest.Add(poi);
            var index = new WorldMapIndex(typeof(GalaxyMapData).Assembly); var snapshot = index.Read(map);
            Assert.Same(second, snapshot.FindSystem("second")); Assert.Same(poi, snapshot.FindPoint("poi"));
            Assert.Null(snapshot.FindPoint("missing")); Assert.True(snapshot.SameMembership(index.Read(map)));
            Assert.Equal(0, poi.NameReads);
            poi.guid = "changed"; Assert.False(snapshot.SameMembership(index.Read(map)));
            poi.system = first; Assert.Throws<InvalidDataException>(() => index.Read(map));
            poi.system = second;
            first.pointsOfInterest.Add(new MapPointOfInterest { guid = "changed", system = first });
            Assert.Throws<InvalidDataException>(() => index.Read(map));
        }

        [Fact]
        public void ReplacingTheMapOrReparentingMembersCannotReuseASnapshot()
        {
            var map = new GalaxyMapData(); var sector = new SectorMapData { guid = "sector" };
            var a = new SystemMapData { guid = "a" }; var b = new SystemMapData { guid = "b" };
            var poi = new MapPointOfInterest { guid = "poi", system = a };
            map.TestSectors.Add(sector); sector.TestSystems.AddRange(new[] { a, b }); a.pointsOfInterest.Add(poi);
            var index = new WorldMapIndex(typeof(GalaxyMapData).Assembly); var before = index.Read(map);
            a.pointsOfInterest.Clear(); b.pointsOfInterest.Add(poi); poi.system = b;
            Assert.False(before.SameMembership(index.Read(map)));
            var other = new GalaxyMapData(); other.TestSectors.Add(sector);
            Assert.False(index.Read(map).SameMembership(index.Read(other)));
            sector.TestSystems.Add(a); Assert.Throws<InvalidDataException>(() => index.Read(map));
        }
    }
}
