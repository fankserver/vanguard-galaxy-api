using System;
using LightJson;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>Placement-mode declaration: OffMap is the default, Visible is an explicit opt-in, and the
/// declared placement reaches the native seam and survives the handle's Definition reconstruction.</summary>
public sealed class PocketSystemPlacementTests
{
    private sealed class Harness : IDisposable
    {
        internal readonly LifecycleHub Hub;
        internal readonly FakePocketSystemNative Native;
        internal readonly WorldDefinitionRegistry Combat;
        internal readonly PocketSystemRegistry Systems;
        internal readonly PocketSystemCoordinator Coordinator;
        internal readonly WorldContentService Service;
        internal IWorldProvider Provider = null!;
        internal Guid Session;
        private readonly bool _disposeProvider;
        internal Harness()
        {
            Hub = new LifecycleHub((_, error) => throw error);
            Native = new FakePocketSystemNative();
            var plugin = new object();
            StoryHostAuthenticator auth = (occurrence, caller) =>
                ReferenceEquals(occurrence, plugin) ? new StoryHostPlugin("author.a", caller) : null;
            Combat = new WorldDefinitionRegistry(auth, Hub.CheckThread);
            Systems = new PocketSystemRegistry(auth, Hub.CheckThread);
            Coordinator = new PocketSystemCoordinator(Hub, Systems, Native, () => true, _ => true, _ => { });
            Service = new WorldContentService(Hub, Combat, null!, () => true, null, null, null, null, Systems, Coordinator);
            Provider = Service.AcquireProvider(plugin)!;
            _disposeProvider = Provider != null;
        }
        internal void BeginGameplay()
        {
            Session = Hub.Begin(SessionOrigin.NewGame, null);
            Hub.PlayerReady(Session);
            Hub.GameplayInitialized(Session);
        }
        public void Dispose()
        {
            if (_disposeProvider) Provider.Dispose();
            Coordinator.Dispose();
            Service.Dispose();
            Combat.Dispose();
            Systems.Dispose();
            Hub.Dispose();
        }
    }

    [Fact]
    public void PlacementDefaultsToOffMap()
    {
        var definition = new PocketSystemDefinition("p", 1, "Pocket");
        Assert.Equal(PocketSystemPlacement.OffMap, definition.Placement);
        // The explicit form is the way to opt into a visible, on-map pocket.
        Assert.Equal(PocketSystemPlacement.Visible, new PocketSystemDefinition("p", 1, "Pocket", PocketSystemPlacement.Visible).Placement);
    }

    [Fact]
    public void OffMapPlacementIsForwardedToTheNativeSeam()
    {
        using var harness = new Harness();
        Assert.Equal(WorldStatus.Succeeded, harness.Provider.RegisterPocketSystem(new PocketSystemDefinition("p", 1, "Pocket")));
        harness.BeginGameplay();
        var pocket = harness.Provider.CreatePocketSystem("p", "k1", "anchor")!;
        Assert.NotNull(pocket);
        Assert.Equal(PocketSystemPlacement.OffMap, Assert.Single(harness.Native.CreatedPlacements));
        Assert.Equal(PocketSystemPlacement.OffMap, pocket.Definition.Placement);
    }

    [Fact]
    public void VisiblePlacementIsForwardedToTheNativeSeamAndRetainedOnTheHandle()
    {
        using var harness = new Harness();
        Assert.Equal(WorldStatus.Succeeded, harness.Provider.RegisterPocketSystem(
            new PocketSystemDefinition("p", 1, "Pocket", PocketSystemPlacement.Visible)));
        harness.BeginGameplay();
        var pocket = harness.Provider.CreatePocketSystem("p", "k1", "anchor")!;
        Assert.NotNull(pocket);
        Assert.Equal(PocketSystemPlacement.Visible, Assert.Single(harness.Native.CreatedPlacements));
        // The handle's Definition carries the declared placement (not just the fallback default).
        Assert.Equal(PocketSystemPlacement.Visible, pocket.Definition.Placement);
        // Both modes still author a single enclosed gate pair (same occurrence contract).
        Assert.Equal(ReconstructionStatus.Reconstructed, pocket.State.Status);
        Assert.NotNull(pocket.SystemId);
        Assert.NotNull(pocket.EntranceGatePoiId);
        Assert.NotNull(pocket.PocketGatePoiId);
    }

    [Fact]
    public void DefininitionsWithDifferentPlacementAreDistinctAndRevisionCheckedByIdentity()
    {
        using var harness = new Harness();
        // A Visible declaration under a local id used before as OffMap is still keyed by (owner, local id):
        // registering the same local id again is a duplicate even when placement differs.
        Assert.Equal(WorldStatus.Succeeded, harness.Provider.RegisterPocketSystem(new PocketSystemDefinition("p", 1, "Pocket")));
        Assert.Equal(WorldStatus.DuplicateDefinition, harness.Provider.RegisterPocketSystem(
            new PocketSystemDefinition("p", 1, "Pocket", PocketSystemPlacement.Visible)));
    }

    [Fact]
    public void OwnerFactionDefaultsToUnknownAndSurvivesHandleReconstruction()
    {
        using var harness = new Harness();
        Assert.Equal(WorldStatus.Succeeded, harness.Provider.RegisterPocketSystem(new PocketSystemDefinition("p", 1, "Pocket")));
        harness.BeginGameplay();
        var pocket = harness.Provider.CreatePocketSystem("p", "k1", "anchor")!;
        // No owner specified => unknown (null) forwarded to the native seam, and retained as null on the handle.
        Assert.Null(Assert.Single(harness.Native.CreatedFactionIds));
        Assert.Null(pocket.Definition.FactionId);
    }

    [Fact]
    public void FreshPocketGatesAreSealedHiddenNotVisible()
    {
        using var harness = new Harness();
        Assert.Equal(WorldStatus.Succeeded, harness.Provider.RegisterPocketSystem(new PocketSystemDefinition("p", 1, "Pocket")));
        harness.BeginGameplay();
        var pocket = harness.Provider.CreatePocketSystem("p", "k1", "anchor")!;
        // A freshly authored pocket must present sealed gates (closed AND hidden) so the map draws no
        // phantom red gate line for a pocket the consumer reaches another way (e.g. a wormhole).
        Assert.False(harness.Native.IsOpen(harness.Session, pocket.EntranceGatePoiId!, pocket.PocketGatePoiId!));
        Assert.True(harness.Native.IsSealed(harness.Session, pocket.EntranceGatePoiId!, pocket.PocketGatePoiId!));
    }

    [Fact]
    public void OpeningAPocketUnsealsAndClosagainReseals()
    {
        using var harness = new Harness();
        Assert.Equal(WorldStatus.Succeeded, harness.Provider.RegisterPocketSystem(new PocketSystemDefinition("p", 1, "Pocket")));
        harness.BeginGameplay();
        var pocket = harness.Provider.CreatePocketSystem("p", "k1", "anchor")!;
        Assert.True(pocket.SetEntranceOpen(true).Succeeded);
        Assert.True(harness.Native.IsOpen(harness.Session, pocket.EntranceGatePoiId!, pocket.PocketGatePoiId!));
        Assert.False(harness.Native.IsSealed(harness.Session, pocket.EntranceGatePoiId!, pocket.PocketGatePoiId!));
        Assert.True(pocket.SetEntranceOpen(false).Succeeded);
        Assert.True(harness.Native.IsSealed(harness.Session, pocket.EntranceGatePoiId!, pocket.PocketGatePoiId!));
    }

    [Fact]
    public void ReconcileRepairsAClosedButStillVisiblePocketGate()
    {
        using var harness = new Harness();
        Assert.Equal(WorldStatus.Succeeded, harness.Provider.RegisterPocketSystem(new PocketSystemDefinition("p", 1, "Pocket")));
        harness.BeginGameplay();
        var pocket = harness.Provider.CreatePocketSystem("p", "k1", "anchor")!;
        // Simulate a pocket authored before gates were hidden at create: closed but visible (not sealed).
        var sid = Assert.Single(harness.Native.Systems.Keys);
        harness.Native.Hidden[sid] = false;
        Assert.False(harness.Native.IsSealed(harness.Session, pocket.EntranceGatePoiId!, pocket.PocketGatePoiId!));
        // A reconciliation pass must re-apply the declared closed state, sealing the pair again.
        harness.Coordinator.Reconcile(harness.Session);
        Assert.True(harness.Native.IsSealed(harness.Session, pocket.EntranceGatePoiId!, pocket.PocketGatePoiId!));
    }

    [Fact]
    public void SectorNameIsForwardedToTheNativeSeamSoAClusterIsNamed()
    {
        using var harness = new Harness();
        Assert.Equal(WorldStatus.Succeeded, harness.Provider.RegisterPocketSystem(
            new PocketSystemDefinition("p", 1, "Cluster Entry", PocketSystemPlacement.OffMap, factionId: null, sectorName: "Wormhole Cluster")));
        harness.BeginGameplay();
        var pocket = harness.Provider.CreatePocketSystem("p", "k1", "anchor")!;
        // The OffMap pocket allocates a remote subsector; its declared name must reach the native seam so
        // the cluster is named instead of getting a procedural subsector name.
        Assert.Equal("Wormhole Cluster", Assert.Single(harness.Native.CreatedSectorNames));
        Assert.Equal("Wormhole Cluster", pocket.Definition.SectorName);
    }

    [Fact]
    public void OwnSectorPlacementIsAPlacementThatStaysOnTheMap()
    {
        using var harness = new Harness();
        Assert.Equal(WorldStatus.Succeeded, harness.Provider.RegisterPocketSystem(
            new PocketSystemDefinition("p", 1, "Pocket", PocketSystemPlacement.OwnSector)));
        harness.BeginGameplay();
        var pocket = harness.Provider.CreatePocketSystem("p", "k1", "anchor")!;
        Assert.Equal(PocketSystemPlacement.OwnSector, Assert.Single(harness.Native.CreatedPlacements));
        Assert.Equal(PocketSystemPlacement.OwnSector, pocket.Definition.Placement);
    }

    [Fact]
    public void SettledMapBoundsDescribeTheBandTheGalaxyMapCanShow()
    {
        Assert.True(SettledMapBounds.Contains(0f, 0f));
        Assert.True(SettledMapBounds.Contains(SettledMapBounds.MinX, SettledMapBounds.MaxY));
        Assert.False(SettledMapBounds.Contains(SettledMapBounds.MinX - 0.1f, 0f));
        Assert.False(SettledMapBounds.Contains(0f, SettledMapBounds.MaxY + 0.1f));
        // An off-map allocation well outside the band is exactly what makes a sector invisible.
        Assert.False(SettledMapBounds.Contains(200f, 200f));
        Assert.True(SettledMapBounds.MinSeparation > 0f);
    }

    [Fact]
    public void StaticNameIsForwardedToTheNativeSeamAndRetainedOnTheHandle()
    {
        using var harness = new Harness();
        Assert.Equal(WorldStatus.Succeeded, harness.Provider.RegisterPocketSystem(
            new PocketSystemDefinition("p", 1, "Cluster Entry")));
        harness.BeginGameplay();
        var pocket = harness.Provider.CreatePocketSystem("p", "k1", "anchor")!;
        // The static Name (requirement 6) is written onto the authored system at create time.
        Assert.Equal("Cluster Entry", Assert.Single(harness.Native.CreatedNames));
        Assert.Equal("Cluster Entry", pocket.Definition.Name);
    }

    [Fact]
    public void OwnerFactionIsForwardedToTheNativeSeamAndRetainedOnTheHandle()
    {
        using var harness = new Harness();
        Assert.Equal(WorldStatus.Succeeded, harness.Provider.RegisterPocketSystem(
            new PocketSystemDefinition("p", 1, "Pocket", PocketSystemPlacement.OffMap, "Marauders")));
        harness.BeginGameplay();
        var pocket = harness.Provider.CreatePocketSystem("p", "k1", "anchor")!;
        Assert.Equal("Marauders", Assert.Single(harness.Native.CreatedFactionIds));
        Assert.Equal("Marauders", pocket.Definition.FactionId);
    }
}
