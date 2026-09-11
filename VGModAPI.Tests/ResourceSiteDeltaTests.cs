using Source.Galaxy;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>
/// Models the membership growth a successful authored-site SetupPOI+Add produces (+1 POI parented to
/// the host system, nothing else) and verifies the structural delta check accepts exactly that growth
/// and rejects removals, extra growth, foreign parents and adopted natives.
/// </summary>
public sealed class ResourceSiteDeltaTests
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
        internal Source.Galaxy.POI.Combat Add(SystemMapData system, string guid)
        {
            var poi = new Source.Galaxy.POI.Combat { guid = guid, system = system };
            system.pointsOfInterest.Add(poi);
            return poi;
        }
    }

    [Fact]
    public void ExactSingleSiteGrowthIsAccepted()
    {
        var book = new MapBook();
        var before = book.Read();
        var created = book.Add(book.Host, "site-1");
        Assert.True(WorldNativeResourceSites.VerifySiteDelta(before, book.Read(), created, book.Host));
    }

    [Fact]
    public void NoOpAdoptedForeignAndExtraGrowthAreRejected()
    {
        var book = new MapBook();
        var before = book.Read();
        // No-op: nothing was created.
        Assert.False(WorldNativeResourceSites.VerifySiteDelta(before, book.Read(), new object(), book.Host));
        // Adoption: an existing native cannot be claimed as the creation.
        var preExisting = book.Add(book.Host, "pre");
        var mid = book.Read();
        Assert.False(WorldNativeResourceSites.VerifySiteDelta(mid, mid, preExisting, book.Host));
        // Extra growth: two new POIs is not a site creation.
        var first = book.Add(book.Host, "a");
        book.Add(book.Host, "b");
        Assert.False(WorldNativeResourceSites.VerifySiteDelta(mid, book.Read(), first, book.Host));
    }

    [Fact]
    public void RemovalsAndForeignSystemMembershipAreRejected()
    {
        var book = new MapBook();
        var stale = book.Add(book.Host, "stale");
        var before = book.Read();
        // A removal alongside the creation is not a clean delta.
        Assert.True(book.Host.pointsOfInterest.Remove(stale));
        var created = book.Add(book.Host, "site-1");
        Assert.False(WorldNativeResourceSites.VerifySiteDelta(before, book.Read(), created, book.Host));
        // A system-set change alongside the creation is rejected too.
        var book2 = new MapBook();
        var before2 = book2.Read();
        var created2 = book2.Add(book2.Host, "site-2");
        var sector = book2.Map.TestSectors[0];
        sector.TestSystems.Add(new SystemMapData { guid = "sneaked" });
        Assert.False(WorldNativeResourceSites.VerifySiteDelta(before2, book2.Read(), created2, book2.Host));
    }
}
