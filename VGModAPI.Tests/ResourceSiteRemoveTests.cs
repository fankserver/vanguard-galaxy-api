using Source.Galaxy;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>
/// Native authored-site dissolution is a structural membership delta: exactly the owned site POI
/// leaves its host system and nothing else changes. A leftover survivor, an accidental extra
/// removal, a new POI, or a claimed identity that was never present are all refused so the caller
/// rolls back rather than leaving the map half-edited.
/// </summary>
public sealed class ResourceSiteRemoveTests
{
    private sealed class MapBook
    {
        internal readonly WorldMapIndex Index;
        internal readonly GalaxyMapData Map = new();
        internal readonly SystemMapData Host = new() { guid = "host" };
        internal readonly SystemMapData Other = new() { guid = "other" };
        internal MapBook()
        {
            Index = new WorldMapIndex(typeof(GalaxyMapData).Assembly);
            var sector = new SectorMapData { guid = "sector" };
            Map.TestSectors.Add(sector);
            sector.TestSystems.Add(Host);
            sector.TestSystems.Add(Other);
        }
        internal WorldMapIndex.Snapshot Read() => Index.Read(Map);
        internal MapPointOfInterest Add(SystemMapData system, string guid)
        {
            var poi = new MapPointOfInterest { guid = guid, system = system };
            system.pointsOfInterest.Add(poi);
            return poi;
        }
    }

    [Fact]
    public void ExactSingleSiteRemovalIsAcceptedAndEverythingElseRefused()
    {
        // Exactly the owned site leaves; an unrelated POI stays.
        var exact = new MapBook();
        var site = exact.Add(exact.Host, "site-1");
        exact.Add(exact.Other, "foreign");
        var before = exact.Read();
        Assert.False(WorldNativeResourceSites.VerifyRemoveDelta(before, exact.Read(), site, exact.Host)); // nothing removed yet
        Assert.True(exact.Host.pointsOfInterest.Remove(site));
        Assert.True(WorldNativeResourceSites.VerifyRemoveDelta(before, exact.Read(), site, exact.Host));

        // An unrelated POI removed alongside the site is not a clean delta.
        var extra = new MapBook();
        var extraSite = extra.Add(extra.Host, "site-1");
        var foreign = extra.Add(extra.Other, "foreign");
        var extraBefore = extra.Read();
        extra.Host.pointsOfInterest.Remove(extraSite);
        extra.Other.pointsOfInterest.Remove(foreign);
        Assert.False(WorldNativeResourceSites.VerifyRemoveDelta(extraBefore, extra.Read(), extraSite, extra.Host));

        // Adding anything alongside the removal is refused.
        var added = new MapBook();
        var addedSite = added.Add(added.Host, "site-1");
        var addedBefore = added.Read();
        added.Host.pointsOfInterest.Remove(addedSite);
        added.Add(added.Host, "added");
        Assert.False(WorldNativeResourceSites.VerifyRemoveDelta(addedBefore, added.Read(), addedSite, added.Host));

        // A claimed identity that was never present is refused.
        var absent = new MapBook();
        absent.Add(absent.Host, "site-1");
        var absentSnapshot = absent.Read();
        Assert.False(WorldNativeResourceSites.VerifyRemoveDelta(absentSnapshot, absentSnapshot, new object(), absent.Host));
    }
}
