using System;
using System.Linq;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

public sealed class PocketSystemDissolveTests
{
    /// <summary>Combined harness: authored systems plus authored sites, optionally a combat-site inventory gate.</summary>
    private sealed class Harness : IDisposable
    {
        internal readonly LifecycleHub Hub;
        internal readonly FakePocketSystemNative Native;
        internal readonly FakeResourceSiteNative SiteNative;
        internal readonly WorldDefinitionRegistry Combat;
        internal readonly PocketSystemRegistry Systems;
        internal readonly PocketSystemCoordinator Coordinator;
        internal readonly ResourceSiteRegistry Sites;
        internal readonly ResourceSiteCoordinator SiteCoordinator;
        internal readonly FakeWormholePairs WormholeNative = new();
        internal readonly WormholePairRegistry Wormholes;
        internal readonly WormholePairCoordinator WormholeCoordinator;
        internal readonly WorldCreationCoordinator? Creation;
        internal readonly WorldContentService Service;
        internal IWorldProvider Provider = null!;
        internal Guid Session;
        internal Harness(bool withCombatGate = false)
        {
            Hub = new LifecycleHub((_, error) => throw error);
            Native = new FakePocketSystemNative();
            SiteNative = new FakeResourceSiteNative();
            var plugin = new object();
            StoryHostAuthenticator auth = (occurrence, caller) =>
                ReferenceEquals(occurrence, plugin) ? new StoryHostPlugin("author.a", caller) : null;
            Combat = new WorldDefinitionRegistry(auth, Hub.CheckThread);
            Systems = new PocketSystemRegistry(auth, Hub.CheckThread);
            Coordinator = new PocketSystemCoordinator(Hub, Systems, Native, () => true, _ => true, _ => { });
            Sites = new ResourceSiteRegistry(auth, Hub.CheckThread);
            SiteCoordinator = new ResourceSiteCoordinator(Hub, Sites, SiteNative, _ => true, _ => { });
            Wormholes = new WormholePairRegistry(auth, Hub.CheckThread);
            WormholeCoordinator = new WormholePairCoordinator(Hub, Wormholes, WormholeNative, _ => true, _ => { });
            WorldAuthoringGate gate = null!;
            if (withCombatGate)
            {
                Creation = new WorldCreationCoordinator(null!, Hub.CheckThread);
                gate = new WorldAuthoringGate(Combat, Creation, _ => true);
            }
            Service = new WorldContentService(Hub, Combat, gate, () => true, null, null, null, null, Systems, Coordinator, Sites, SiteCoordinator,
                null, null, null, Wormholes, WormholeCoordinator);
            Provider = Service.AcquireProvider(plugin)!;
        }
        internal void BeginGameplay()
        {
            Session = Hub.Begin(SessionOrigin.NewGame, null);
            Hub.PlayerReady(Session);
            Hub.GameplayInitialized(Session);
        }
        internal IPocketSystem CreatePocket(string key = "k1")
        {
            Assert.Equal(WorldStatus.Succeeded, Provider.RegisterPocketSystem(new PocketSystemDefinition("pocket", 1, "The Hollow")));
            BeginGameplay();
            return Provider.CreatePocketSystem("pocket", key, "anchor")!;
        }
        public void Dispose()
        {
            Provider?.Dispose();
            Coordinator.Dispose(); SiteCoordinator.Dispose(); WormholeCoordinator.Dispose();
            Service.Dispose(); Combat.Dispose(); Systems.Dispose(); Sites.Dispose(); Wormholes.Dispose(); Hub.Dispose();
        }
    }

    [Fact]
    public void DissolveRefusesWhileThePocketIsStillAWormholeEndpoint()
    {
        using var harness = new Harness();
        Assert.Equal(WorldStatus.Succeeded, harness.Provider.RegisterPocketSystem(new PocketSystemDefinition("pocket", 1, "The Hollow")));
        Assert.Equal(WorldStatus.Succeeded, harness.Provider.RegisterWormholePair(new WormholePairDefinition("rift", 1, "Unstable Rift")));
        harness.BeginGameplay();
        var pocket = harness.Provider.CreatePocketSystem("pocket", "k1", "anchor")!;
        var hook = harness.Provider.CreateWormholePair("rift", "default", pocket.SystemId!, "other-system");
        Assert.NotNull(hook);
        // Orphaning the pocket-side wormhole POI is refused, mirroring the combat-site refusal.
        Assert.Equal(WorldContentStatus.Rejected, pocket.Dissolve().Status);
        Assert.Equal(WorldContentStatus.Rejected, pocket.LastAction.Status);
    }

    [Fact]
    public void DissolveRemovesThePocketItsSaveRowAndFreesTheKeyForAFreshInstance()
    {
        using var harness = new Harness();
        var pocket = harness.CreatePocket();
        Assert.True(pocket.SetEntranceOpen(true).Succeeded);
        var result = pocket.Dissolve();
        Assert.True(result.Succeeded);
        // The native pocket is gone and no save row remains to reconstruct it.
        Assert.Empty(harness.Native.Systems);
        Assert.Empty(harness.Coordinator.CaptureRows());
        // The object is terminal: Dissolved state, no native identity, actions refused with a retained result.
        Assert.Equal(ReconstructionStatus.Dissolved, pocket.State.Status);
        Assert.Null(pocket.SystemId); Assert.Null(pocket.EntranceGatePoiId); Assert.Null(pocket.PocketGatePoiId);
        Assert.Equal(WorldContentStatus.Rejected, pocket.SetEntranceOpen(true).Status);
        Assert.Equal(WorldContentStatus.Rejected, pocket.Dissolve().Status);
        Assert.Equal(WorldContentStatus.Rejected, pocket.LastAction.Status);
        // The occurrence is no longer obtainable; the key authors a FRESH pocket with fresh native identity.
        Assert.Null(harness.Provider.GetPocketSystem("pocket", "k1"));
        Assert.Empty(harness.Provider.GetPocketSystems("pocket"));
        var fresh = harness.Provider.CreatePocketSystem("pocket", "k1", "anchor")!;
        Assert.NotSame(pocket, fresh);
        Assert.Equal("sys-2", fresh.SystemId);
        Assert.Equal(ReconstructionStatus.Reconstructed, fresh.State.Status);
        Assert.Equal(ReconstructionStatus.Dissolved, pocket.State.Status);
    }

    [Fact]
    public void PlayerInsideThePocketRefusesDissolutionAndRetainsTheInstance()
    {
        using var harness = new Harness();
        var pocket = harness.CreatePocket();
        harness.Native.PlayerInside = true;
        var refused = pocket.Dissolve();
        Assert.Equal(WorldContentStatus.Rejected, refused.Status);
        Assert.Contains("player", refused.Detail, StringComparison.OrdinalIgnoreCase);
        // Nothing was removed; the occurrence stays live and actionable.
        Assert.Single(harness.Native.Systems);
        Assert.Single(harness.Coordinator.CaptureRows());
        Assert.Equal(ReconstructionStatus.Reconstructed, pocket.State.Status);
        Assert.True(pocket.SetEntranceOpen(false).Succeeded);
        // Once the consumer relocated the player, the same object dissolves.
        harness.Native.PlayerInside = false;
        Assert.True(pocket.Dissolve().Succeeded);
    }

    [Fact]
    public void NativelyAbsentPocketRefusesDissolutionWithoutDroppingTheRow()
    {
        using var harness = new Harness();
        var pocket = harness.CreatePocket();
        harness.Native.Systems.Clear();
        Assert.Equal(WorldContentStatus.Rejected, pocket.Dissolve().Status);
        Assert.Single(harness.Coordinator.CaptureRows());
    }

    [Fact]
    public void UnverifiedNativeRemovalIsRejectedAndRetainsTheRow()
    {
        using var harness = new Harness();
        var pocket = harness.CreatePocket();
        harness.Native.FailDissolve = true;
        Assert.Equal(WorldContentStatus.Rejected, pocket.Dissolve().Status);
        Assert.Single(harness.Coordinator.CaptureRows());
        Assert.Equal(ReconstructionStatus.Reconstructed, pocket.State.Status);
    }

    [Fact]
    public void NativeFaultIsReportedAsUnavailableAndRetainsTheRow()
    {
        using var harness = new Harness();
        var pocket = harness.CreatePocket();
        harness.Native.ThrowOnDissolve = true;
        Assert.Equal(WorldContentStatus.Unavailable, pocket.Dissolve().Status);
        Assert.Single(harness.Coordinator.CaptureRows());
        harness.Native.ThrowOnDissolve = false;
        Assert.True(pocket.Dissolve().Succeeded);
    }

    [Fact]
    public void FailedCreationDissolvesWithoutANativeRemovalAndFreesTheKey()
    {
        using var harness = new Harness();
        Assert.Equal(WorldStatus.Succeeded, harness.Provider.RegisterPocketSystem(new PocketSystemDefinition("pocket", 1, "The Hollow")));
        harness.BeginGameplay();
        harness.Native.FailCreate = true;
        var failed = harness.Provider.CreatePocketSystem("pocket", "k1", "anchor")!;
        Assert.NotNull(failed);
        Assert.True(failed.Dissolve().Succeeded);
        Assert.Equal(0, harness.Native.DissolveCalls); // nothing native was ever created
        Assert.Empty(harness.Coordinator.CaptureRows());
        harness.Native.FailCreate = false;
        var fresh = harness.Provider.CreatePocketSystem("pocket", "k1", "anchor")!;
        Assert.Equal(ReconstructionStatus.Reconstructed, fresh.State.Status);
    }

    [Fact]
    public void ReplacedSessionRefusesDissolveWithGameEnded()
    {
        using var harness = new Harness();
        var pocket = harness.CreatePocket();
        harness.Hub.Invalidate("session replaced by test");
        harness.BeginGameplay();
        Assert.Equal(WorldContentStatus.GameEnded, pocket.Dissolve().Status);
    }

    [Fact]
    public void ResourceSitesInsideTheDissolvedPocketAreDroppedAndTheirObjectsAreTerminal()
    {
        using var harness = new Harness();
        Assert.Equal(WorldStatus.Succeeded, harness.Provider.RegisterPocketSystem(new PocketSystemDefinition("pocket", 1, "The Hollow")));
        Assert.Equal(WorldStatus.Succeeded, harness.Provider.RegisterResourceSite(
            ResourceSiteDefinition.MiningField("field", 1, "Singer's Field", 8, 6)));
        harness.BeginGameplay();
        var pocket = harness.Provider.CreatePocketSystem("pocket", "k1", "anchor")!;
        var inside = harness.Provider.CreateResourceSite("field", "in-pocket", pocket.SystemId!, 1f, 2f)!;
        var outside = harness.Provider.CreateResourceSite("field", "elsewhere", "other-system", 3f, 4f)!;
        bool insideChanged = false;
        inside.Changed += _ => insideChanged = true;
        Assert.True(pocket.Dissolve().Succeeded);
        // The contained site row is dropped (intentionally absent, not a reconstruction failure)...
        Assert.Equal("elsewhere", Assert.Single(harness.SiteCoordinator.CaptureRows()).OccurrenceKey);
        Assert.Null(harness.Provider.GetResourceSite("field", "in-pocket"));
        // ...its object is terminal and announced the transition; the unrelated site is untouched.
        Assert.Equal(ReconstructionStatus.Dissolved, inside.State.Status);
        Assert.True(insideChanged);
        Assert.Same(outside, harness.Provider.GetResourceSite("field", "elsewhere"));
    }

    [Fact]
    public void CombatSitesInsideThePocketRefuseDissolutionUntilTheInventoryCanProveAbsence()
    {
        using var harness = new Harness(withCombatGate: true);
        var pocket = harness.CreatePocket();
        // The inventory cannot answer yet: fail closed with NotReady rather than guessing.
        var notReady = pocket.Dissolve();
        Assert.Equal(WorldContentStatus.NotReady, notReady.Status);
        Assert.Single(harness.Coordinator.CaptureRows());
        // A retained combat-site record inside the pocket refuses dissolution outright.
        harness.Creation!.Reset(harness.Session);
        var identity = new WorldObjectIdentity(new ContentDeclaration("author.a", "PoiX", PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent), Guid.NewGuid());
        var row = new WorldSnapshotInstance(new object(), identity, pocket.SystemId!,
            new WorldSavedDefinition(identity.Owner, new WorldCombatDefinition(identity.LocalId, 1, "Site", "player", 1)));
        Assert.True(harness.Creation.TryRestore(harness.Session, () => new[] { row }));
        var refused = pocket.Dissolve();
        Assert.Equal(WorldContentStatus.Rejected, refused.Status);
        Assert.Contains("combat", refused.Detail, StringComparison.OrdinalIgnoreCase);
        // Without combat sites in the pocket the same object dissolves.
        harness.Creation.Reset(harness.Session);
        Assert.True(harness.Creation.TryRestore(harness.Session, Array.Empty<WorldSnapshotInstance>));
        Assert.True(pocket.Dissolve().Succeeded);
    }

    [Fact]
    public void DissolveDeltaVerificationAcceptsOnlyTheExactRemoval()
    {
        var index = new WorldMapIndex(typeof(Source.Galaxy.GalaxyMapData).Assembly);
        var map = new Source.Galaxy.GalaxyMapData();
        var sector = new Source.Galaxy.SectorMapData { guid = "sector" };
        var anchor = new Source.Galaxy.SystemMapData { guid = "anchor" };
        var pocket = new Source.Galaxy.SystemMapData { guid = "sys-p1" };
        map.TestSectors.Add(sector); sector.TestSystems.Add(anchor); sector.TestSystems.Add(pocket);
        var entrance = new Source.Galaxy.POI.JumpGate { guid = "en-1", system = anchor };
        var peer = new Source.Galaxy.POI.JumpGate { guid = "pk-1", system = pocket };
        var other = new Source.Galaxy.POI.JumpGate { guid = "other", system = anchor };
        anchor.pointsOfInterest.Add(entrance); anchor.pointsOfInterest.Add(other); pocket.pointsOfInterest.Add(peer);
        var before = index.Read(map);
        static object? ParentOf(object poi) => ((Source.Galaxy.MapElement)poi).system;
        bool Verify(WorldMapIndex.Snapshot after) => WorldNativePocketSystems.VerifyDissolveDelta(before, after, pocket, entrance, ParentOf);
        // Removing too little is refused: the pocket and entrance are still present.
        Assert.False(Verify(index.Read(map)));
        anchor.pointsOfInterest.Remove(entrance);
        Assert.False(Verify(index.Read(map))); // pocket system still present
        sector.TestSystems.Remove(pocket);
        var exact = index.Read(map);
        Assert.True(Verify(exact));
        // Removing an unrelated survivor or adding anything is refused.
        anchor.pointsOfInterest.Remove(other);
        Assert.False(Verify(index.Read(map)));
        anchor.pointsOfInterest.Add(other);
        anchor.pointsOfInterest.Add(new Source.Galaxy.POI.JumpGate { guid = "added", system = anchor });
        Assert.False(Verify(index.Read(map)));
    }
}
