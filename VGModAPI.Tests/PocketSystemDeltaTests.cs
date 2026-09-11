using Source.Galaxy;
using Source.Galaxy.POI;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>
/// Models the membership growth the native SandboxWorld.AddSideContentSystemToSystem produces (+1 pocket
/// system, + anchor-side entrance jump gate, + pocket-side jump gate) and verifies WorldNativePocketSystems's
/// structural delta check accepts exactly that growth and rejects anything else. This is the P0 regression
/// test: the old SameMembership check never matched after a genuine, successful creation.
/// </summary>
public sealed class PocketSystemDeltaTests
{
    private static WorldMapIndex.Snapshot Read()
    {
        var index = new WorldMapIndex(typeof(GalaxyMapData).Assembly);
        var map = new GalaxyMapData();
        var sector = new SectorMapData { guid = "sector" };
        var anchor = new SystemMapData { guid = "anchor" };
        map.TestSectors.Add(sector);
        sector.TestSystems.Add(anchor);
        return index.Read(map);
    }
    private sealed class MapBook
    {
        internal readonly WorldMapIndex Index;
        internal GalaxyMapData Map = new();
        internal SectorMapData? Sector;
        internal SystemMapData Anchor = new() { guid = "anchor" };
        internal MapBook()
        {
            Index = new WorldMapIndex(typeof(GalaxyMapData).Assembly);
            Map.TestSectors.Add((Sector = new SectorMapData { guid = "sector" }));
            Sector.TestSystems.Add(Anchor);
        }
        internal WorldMapIndex.Snapshot Read() => Index.Read(Map);
    }
    private static bool Exact(SystemMapData created, SystemMapData anchor,
        WorldMapIndex.Snapshot before, WorldMapIndex.Snapshot after)
        => WorldNativePocketSystems.VerifyPocketDelta(before, after, created, anchor,
            o => o is JumpGate, o => ((MapElement)o!).system);

    [Fact]
    public void ExactNativeMembershipGrowthIsAccepted()
    {
        var book = new MapBook();
        var before = book.Read();
        // The exact growth a successful AddSideContentSystemToSystem produces.
        var created = new SystemMapData { guid = "sys-p1" };
        book.Sector!.TestSystems.Add(created);
        var entrance = new JumpGate { guid = "en-1", system = book.Anchor };
        var pocketGate = new JumpGate { guid = "pk-1", system = created };
        book.Anchor.pointsOfInterest.Add(entrance);
        created.pointsOfInterest.Add(pocketGate);
        var after = book.Read();
        Assert.True(Exact(created, book.Anchor, before, after));
    }

    [Fact]
    public void AnExtraForeignSystemIsRejected()
    {
        var book = new MapBook();
        var before = book.Read();
        var created = new SystemMapData { guid = "sys-p1" };
        book.Sector!.TestSystems.Add(created);
        var pocketGate = new JumpGate { guid = "pk-1", system = created };
        created.pointsOfInterest.Add(pocketGate);
        book.Sector.TestSystems.Add(new SystemMapData { guid = "foreign" });   // beyond the pocket
        var after = book.Read();
        Assert.False(Exact(created, book.Anchor, before, after));
    }

    [Fact]
    public void AGateMisParentedAwayFromCreatedOrAnchorIsRejected()
    {
        var book = new MapBook();
        var before = book.Read();
        var created = new SystemMapData { guid = "sys-p1" };
        book.Sector!.TestSystems.Add(created);
        // Both gates parented to the anchor: the pocket gate did not land in the created system.
        var entrance = new JumpGate { guid = "en-1", system = book.Anchor };
        var pocketGate = new JumpGate { guid = "pk-1", system = book.Anchor };
        book.Anchor.pointsOfInterest.Add(entrance);
        book.Anchor.pointsOfInterest.Add(pocketGate);
        var after = book.Read();
        Assert.False(Exact(created, book.Anchor, before, after));
    }

    [Fact]
    public void ANonGateNewPoiIsRejected()
    {
        var book = new MapBook();
        var before = book.Read();
        var created = new SystemMapData { guid = "sys-p1" };
        book.Sector!.TestSystems.Add(created);
        var entrance = new JumpGate { guid = "en-1", system = book.Anchor };
        book.Anchor.pointsOfInterest.Add(entrance);
        book.Anchor.pointsOfInterest.Add(new MapPointOfInterest { guid = "station", system = book.Anchor });   // not a jump gate
        var after = book.Read();
        Assert.False(Exact(created, book.Anchor, before, after));
    }
}
