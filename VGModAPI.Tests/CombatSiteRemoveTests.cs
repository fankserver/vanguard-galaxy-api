using System;
using System.Collections;
using System.IO;
using Source.Galaxy;
using Source.Player;
using Source.Util;
using VGModAPI;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>
/// Owned combat sites get the same direct teardown as the other authored kinds: a verified native
/// POI removal, then the poi key is dropped so save data records intentional absence instead
/// of reconstructing a failure. Refusals leave the key and the native POI untouched.
/// </summary>
[Collection("game-double")]
public sealed class CombatSiteRemoveTests
{
    private sealed class Harness : IDisposable
    {
        private const string FactionId = "world.remove.test";
        internal readonly LifecycleHub Hub;
        internal readonly WorldDefinitionRegistry Combat;
        internal readonly WorldCreationCoordinator Creation;
        internal readonly WorldContentService Service;
        internal readonly IWorldProvider Provider;
        internal readonly GalaxyMapData Map = new();
        internal readonly SystemMapData Host = new() { guid = "system" };
        internal readonly GameAdapter Game;
        internal Guid Session;

        internal Harness()
        {
            Hub = new LifecycleHub((_, error) => throw error);
            var sector = new SectorMapData { guid = "sector" };
            Map.TestSectors.Add(sector);
            sector.TestSystems.Add(Host);
            Game = new GameAdapter(Hub, new GameBindings(typeof(GamePlayer).Assembly), _ => { });
            var plugin = new object();
            StoryHostAuthenticator auth = (instance, caller) => ReferenceEquals(instance, plugin) ? new StoryHostPlugin("author.a", caller) : null;
            Combat = new WorldDefinitionRegistry(auth, Hub.CheckThread);
            Creation = new WorldCreationCoordinator(new WorldNativeAttachment(Game), Hub.CheckThread);
            Service = new WorldContentService(Hub, Combat, new WorldAuthoringGate(Combat, Creation, _ => true), () => true);
            Provider = Service.AcquireProvider(plugin)!;
            Faction.allFactions[FactionId] = new Faction();
            Register();
        }
        internal CombatSiteDefinition Definition => new("PoiX", 1, "Contested Site", FactionId, 2);
        private void Register() => Assert.Equal(WorldContentStatus.Succeeded, Provider.RegisterCombatSite(Definition).Status);

        internal void BeginGameplay()
        {
            var request = Game.BeginLoad(new SaveGameFile(Path.Combine(Path.GetTempPath(), "combat-remove.save")));
            IEnumerator Load() { GamePlayer.current = new GamePlayer { map = Map }; Game.PlayerReconstructed(); yield break; }
            var routine = Game.ObserveLoad(Load());
            Game.EndLoadRequest(request, null);
            while (routine.MoveNext()) { }
            Game.GameplayCompleted(request.Id, new GameplayManager(true), null);
            Session = request.Id;
            Creation.Reset(Session);
            Assert.True(Creation.TryRestore(Session, () => Array.Empty<WorldSnapshotInstance>()));
        }

        public void Dispose()
        {
            Faction.allFactions.Remove(FactionId);
            GamePlayer.current = null;
            Provider?.Dispose(); Service.Dispose(); Combat.Dispose(); Hub.Dispose();
        }
    }

    [Fact]
    public void RemoveRemovesTheNativePoiAndTheKeyAndFreesTheKeyForAFreshInstance()
    {
        using var h = new Harness();
        h.BeginGameplay();
        var site = h.Provider.CreateCombatSite("PoiX", "encounter", "system", 10, 20);
        Assert.NotNull(site);
        Assert.True(site!.State.Reconstructed);
        Assert.Single(h.Host.pointsOfInterest);
        Assert.Single(h.Service.CaptureCombatKeys());

        Assert.True(site.Remove().Succeeded);
        // The native POI and the save key are both gone: load reconstructs nothing.
        Assert.Empty(h.Host.pointsOfInterest);
        Assert.Empty(h.Service.CaptureCombatKeys());
        Assert.Equal(ReconstructionStatus.Removed, site.State.Status);
        Assert.Null(site.PoiId);
        Assert.Null(h.Provider.GetCombatSite("PoiX", "encounter"));
        Assert.Empty(h.Provider.GetCombatSites("PoiX"));
        // The object is terminal and the freed key authors a fresh poi.
        Assert.Equal(WorldContentStatus.Rejected, site.Remove().Status);
        var fresh = h.Provider.CreateCombatSite("PoiX", "encounter", "system", 10, 20);
        Assert.NotNull(fresh);
        Assert.NotSame(site, fresh);
        Assert.Equal(ReconstructionStatus.Reconstructed, fresh!.State.Status);
        Assert.Single(h.Host.pointsOfInterest);
    }

    [Fact]
    public void CanRemoveReportsPlayerInsideWhileAtTheSiteAndRemoveIsPlain()
    {
        using var h = new Harness();
        h.BeginGameplay();
        var site = h.Provider.CreateCombatSite("PoiX", "encounter", "system", 10, 20)!;
        GamePlayer.current!.currentPointOfInterest = (MapPointOfInterest)h.Host.pointsOfInterest[0];
        // Readiness reports the occupancy; Remove itself is plain and proceeds regardless.
        Assert.Equal(RemovalStatus.PlayerInside, site.CanRemove());
        Assert.True(site.Remove().Succeeded);
        Assert.Equal(ReconstructionStatus.Removed, site.State.Status);
        Assert.Empty(h.Host.pointsOfInterest);
        Assert.Empty(h.Service.CaptureCombatKeys());
    }

    [Fact]
    public void RequestRemovalDefersThenCompletesAtACleanupWindow()
    {
        using var h = new Harness();
        h.BeginGameplay();
        var site = h.Provider.CreateCombatSite("PoiX", "encounter", "system", 10, 20)!;
        // Player at the site: readiness is PlayerInside, so a deferred removal is queued and waits.
        GamePlayer.current!.currentPointOfInterest = (MapPointOfInterest)h.Host.pointsOfInterest[0];
        Assert.Equal(WorldContentStatus.Succeeded, site.RequestRemoval().Status); // queued
        Assert.Single(h.Host.pointsOfInterest); // still present; the window won't act while occupied
        Assert.Equal(ReconstructionStatus.Reconstructed, site.State.Status);
        Assert.True(site.CanRemove() == RemovalStatus.PlayerInside);
        // The player leaves; the next cleanup window completes the deferred removal.
        GamePlayer.current.currentPointOfInterest = null;
        Assert.True(site.CanRemove() == RemovalStatus.Ready);
        h.Service.MaintainPocketSystems(h.Session);
        Assert.Equal(ReconstructionStatus.Removed, site.State.Status);
        Assert.Contains("completed at a cleanup window", site.LastAction.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(h.Host.pointsOfInterest);
        Assert.Empty(h.Service.CaptureCombatKeys());
    }

    [Fact]
    public void NativelyAbsentSiteRefusesRemovalWithoutDroppingTheKey()
    {
        using var h = new Harness();
        h.BeginGameplay();
        var site = h.Provider.CreateCombatSite("PoiX", "encounter", "system", 10, 20)!;
        h.Host.pointsOfInterest.Clear();
        Assert.Equal(WorldContentStatus.Rejected, site.Remove().Status);
        Assert.Single(h.Service.CaptureCombatKeys());
    }

    [Fact]
    public void ReplacedSessionRefusesRemoveWithGameEnded()
    {
        using var h = new Harness();
        h.BeginGameplay();
        var site = h.Provider.CreateCombatSite("PoiX", "encounter", "system", 10, 20)!;
        h.Hub.Invalidate("session replaced by test");
        h.BeginGameplay();
        Assert.Equal(WorldContentStatus.GameEnded, site.Remove().Status);
        // The replacement save was never touched: the old native POI is still present.
        Assert.Single(h.Host.pointsOfInterest);
    }
}
