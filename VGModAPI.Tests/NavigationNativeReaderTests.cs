using Source.Galaxy;
using Source.Galaxy.POI;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;
public sealed class NavigationNativeReaderTests
{
    [Fact]
    public void ReadsStationFactsWithoutGeneratingNamesAndRejectsChangedMembership()
    {
        var map = new GalaxyMapData(); var sector = new SectorMapData { guid = "sector" };
        var system = new SystemMapData { guid = "system" }; var next = new SystemMapData { guid = "next" };
        map.TestSectors.Add(sector); sector.TestSystems.Add(system); sector.TestSystems.Add(next);
        system.NavigationNeighbors.Add(next);
        var station = new SpaceStation { guid = "station", system = system, lastVisitedTime = 5 };
        var hidden = new SpaceStation { guid = "hidden", system = system, hidden = true };
        system.pointsOfInterest.Add(station); system.pointsOfInterest.Add(hidden);
        var snapshot = new NavigationNativeReader(typeof(GalaxyMapData).Assembly).Read(map, () => true);
        Assert.Equal(new[] { "next" }, snapshot.Edges["system"]);
        var row = Assert.Single(snapshot.Stations);
        Assert.Equal("station", row.Id); Assert.Equal("system", row.SystemId); Assert.True(row.Visited);
        Assert.Null(row.Name); Assert.Equal(0, station.NameReads); Assert.Equal(0, hidden.NameReads);
        Assert.True(snapshot.IsCurrent()); system.pointsOfInterest.Remove(station); Assert.False(snapshot.IsCurrent());
    }
}
