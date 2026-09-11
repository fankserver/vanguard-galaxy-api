using System;
using System.IO;
using System.Linq;
using VGModAPI;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>
/// Keyed combat sites carry the SAME occurrence contract as the other authored kinds: persisted
/// author-local keys (schema-4 kind-4 rows), honest enumeration from persisted state rather than a
/// handle cache, a once-per-session settled report, and cross-kind key-collision refusal.
/// </summary>
public sealed class CombatSiteParityTests
{
    // ---- persistence envelope -------------------------------------------------------------

    [Fact]
    public void CombatKeyRowsRoundTripBesideEveryOtherKind()
    {
        var system = new PocketSystemOccurrence("author.a", "pocket", "k1", 1, "sys", "gate-in", "gate-out", true);
        var wormhole = new WormholePairOccurrence("author.a", "rift", "k2", 1, "a", "b", "wa", "wb", false);
        var combat = new CombatSiteKeyRow("author.a", "PoiX", "encounter", Guid.NewGuid());
        var bytes = PocketSystemStateCodec.Encode(new[] { system }, Array.Empty<ResourceSiteOccurrence>(),
            Array.Empty<MooredShipOccurrence>(), new[] { wormhole }, new[] { combat });
        var decoded = PocketSystemStateCodec.DecodeAll(bytes);
        Assert.Single(decoded.Systems); Assert.Single(decoded.Wormholes);
        var row = Assert.Single(decoded.CombatKeys);
        Assert.Equal("author.a", row.Owner); Assert.Equal("PoiX", row.LocalId);
        Assert.Equal("encounter", row.OccurrenceKey); Assert.Equal(combat.InstanceId, row.InstanceId);
    }

    [Fact]
    public void CombatKeyRowsShareTheCrossKindKeySpace()
    {
        // The envelope keys occurrences per (owner, local, key) across ALL kinds; a combat key that
        // collides with a site row is refused at save time, exactly like every other kind pair.
        var site = new ResourceSiteOccurrence("author.a", "wreck", "k", 1, ResourceSiteKind.SalvageSite, "sys", "poi");
        var combat = new CombatSiteKeyRow("author.a", "wreck", "k", Guid.NewGuid());
        Assert.Throws<InvalidDataException>(() => PocketSystemStateCodec.Encode(
            Array.Empty<PocketSystemOccurrence>(), new[] { site }, Array.Empty<MooredShipOccurrence>(),
            Array.Empty<WormholePairOccurrence>(), new[] { combat }));
    }

    [Fact]
    public void OlderSchemasRefuseCombatKeyRowsAndMalformedRowsNeverDecode()
    {
        // A hand-built schema-3 payload carrying kind 4 must refuse: the kind did not exist yet, so
        // accepting it would invent meaning for bytes an older writer could not have produced.
        var valid = PocketSystemStateCodec.Encode(Array.Empty<PocketSystemOccurrence>(),
            Array.Empty<ResourceSiteOccurrence>(), Array.Empty<MooredShipOccurrence>(),
            Array.Empty<WormholePairOccurrence>(), new[] { new CombatSiteKeyRow("author.a", "PoiX", "k", Guid.NewGuid()) });
        var downgraded = (byte[])valid.Clone();
        BitConverter.GetBytes(3).CopyTo(downgraded, 4); // version slot follows the magic
        Assert.Throws<InvalidDataException>(() => PocketSystemStateCodec.DecodeAll(downgraded));
        // Truncated identity refuses instead of decoding a short guid.
        var truncated = valid.Take(valid.Length - 8).ToArray();
        Assert.Throws<InvalidDataException>(() => PocketSystemStateCodec.DecodeAll(truncated));
        // Full valid payload still decodes after both tamper checks (the clone protected it).
        Assert.Single(PocketSystemStateCodec.DecodeAll(valid).CombatKeys);
    }

    // ---- service state: enumeration, settled report, cross-kind guard ---------------------

    private sealed class Harness : IDisposable
    {
        internal readonly LifecycleHub Hub;
        internal readonly FakeResourceSiteNative Native;
        internal readonly WorldDefinitionRegistry Combat;
        internal readonly ResourceSiteRegistry Sites;
        internal readonly ResourceSiteCoordinator Coordinator;
        internal readonly WorldContentService Service;
        internal IWorldProvider Provider;
        internal Guid Session;
        internal Harness()
        {
            Hub = new LifecycleHub((_, error) => throw error);
            Native = new FakeResourceSiteNative();
            var plugin = new object();
            StoryHostAuthenticator auth = (instance, caller) => ReferenceEquals(instance, plugin) ? new StoryHostPlugin("author.a", caller) : null;
            Combat = new WorldDefinitionRegistry(auth, Hub.CheckThread);
            Sites = new ResourceSiteRegistry(auth, Hub.CheckThread);
            Coordinator = new ResourceSiteCoordinator(Hub, Sites, Native, _ => true, _ => { });
            // A REAL authoring gate: keyed combat handles resolve through it, unlike authored sites.
            var game = new GameAdapter(Hub, new GameBindings(typeof(Source.Player.GamePlayer).Assembly), _ => { });
            var creation = new WorldCreationCoordinator(new WorldNativeAttachment(game), Hub.CheckThread, new WorldLifetimeGuard());
            Service = new WorldContentService(Hub, Combat, new WorldAuthoringGate(Combat, creation, _ => true), () => true, null, null, null, null, null, null, Sites, Coordinator);
            Provider = Service.AcquireProvider(plugin)!;
        }
        internal void BeginGameplay()
        {
            Session = Hub.Begin(SessionOrigin.NewGame, null);
            Hub.PlayerReady(Session); Hub.GameplayInitialized(Session);
        }
        public void Dispose() { Provider?.Dispose(); Coordinator.Dispose(); Service.Dispose(); Combat.Dispose(); Sites.Dispose(); Hub.Dispose(); }
    }

    [Fact]
    public void EnumerationComesFromPersistedKeysScopedToTheOwnerAndSession()
    {
        using var h = new Harness();
        h.BeginGameplay();
        var mine = new CombatSiteKeyRow("author.a", "PoiX", "encounter", Guid.NewGuid());
        var other = new CombatSiteKeyRow("other.owner", "PoiX", "foreign", Guid.NewGuid());
        h.Service.RestoreCombatKeys(h.Session, new[] { mine, other });
        // Enumeration answers from the persisted key rows - the source of truth - never a handle cache.
        var sites = h.Provider.GetCombatSites("PoiX");
        var handle = Assert.Single(sites);
        Assert.False(handle.State.Reconstructed);                                // honest: keyed, not in world
        Assert.Same(handle, Assert.Single(h.Provider.GetCombatSites("PoiX")));   // same key = same object
        // GetCombatSite keeps its existing semantics - null while no native site backs the key -
        // while enumeration reports the persisted key with its honest un-reconstructed state.
        Assert.Null(h.Provider.GetCombatSite("PoiX", "encounter"));
        Assert.Empty(h.Provider.GetCombatSites("SomethingElse"));
        // Captured rows survive a save capture untouched, both rows included.
        Assert.Equal(2, h.Service.CaptureCombatKeys().Length);
    }

    [Fact]
    public void RestoreOutsideTheCurrentSessionRefusesInsteadOfAdoptingRows()
    {
        using var h = new Harness();
        h.BeginGameplay();
        Assert.Throws<InvalidDataException>(() => h.Service.RestoreCombatKeys(Guid.NewGuid(),
            new[] { new CombatSiteKeyRow("author.a", "PoiX", "k", Guid.NewGuid()) }));
        Assert.Empty(h.Service.CaptureCombatKeys());
    }

    [Fact]
    public void SettledReportFiresOncePerSessionWithHonestPerOccurrenceStates()
    {
        using var h = new Harness();
        h.BeginGameplay();
        h.Service.RestoreCombatKeys(h.Session, new[] { new CombatSiteKeyRow("author.a", "PoiX", "encounter", Guid.NewGuid()) });
        int reports = 0; CombatSitesSettledEvent? seen = null;
        h.Provider.CombatSiteReconstructionSettled += settled => { reports++; seen = settled; };
        h.Service.MaintainPocketSystems(h.Session);
        Assert.Equal(1, reports);
        Assert.NotNull(seen);
        Assert.Equal(h.Session, seen!.SessionId);
        // In this world no native combat POI backs the key, so the report is a FAILURE with the
        // occurrence object attached - actual outcomes, never a success invented from the key row.
        Assert.Empty(seen.Reconstructed);
        var failure = Assert.Single(seen.Failures);
        Assert.False(failure.Occurrence.State.Reconstructed);
        // Once per session: a second maintenance pass reports nothing again.
        h.Service.MaintainPocketSystems(h.Session);
        Assert.Equal(1, reports);
    }

    [Fact]
    public void CombatKeysRefuseCrossKindKeyCollisionsAtCreation()
    {
        using var h = new Harness();
        Assert.Equal(WorldStatus.Succeeded, h.Provider.RegisterResourceSite(
            ResourceSiteDefinition.Salvage("wreck", 1, "Failed Refuge", 8, "Monsoon", "Fanatics")));
        h.BeginGameplay();
        // A combat key already claims this (local, key); the authored-site creation must refuse it.
        h.Service.RestoreCombatKeys(h.Session, new[] { new CombatSiteKeyRow("author.a", "wreck", "act2-refuge", Guid.NewGuid()) });
        Assert.Null(h.Provider.CreateResourceSite("wreck", "act2-refuge", "pocket-system", 10, 4));
        // A different key is untouched by the guard.
        Assert.NotNull(h.Provider.CreateResourceSite("wreck", "other-key", "pocket-system", 10, 4));
    }

    [Fact]
    public void CombatKeysBlockWormholeCreationUnderTheSameKey()
    {
        // The review caught this guard missing: without it the collision surfaced only at save time
        // as an encode refusal - exactly the failure the creation-edge guard exists to prevent.
        using var h = new WormholeHarness();
        h.Provider.RegisterWormholePair(new WormholePairDefinition("rift", 1, "Rift"));
        h.BeginGameplay();
        h.Service.RestoreCombatKeys(h.Session, new[] { new CombatSiteKeyRow("author.a", "rift", "k", Guid.NewGuid()) });
        Assert.Null(h.Provider.CreateWormholePair("rift", "k", "a", "b"));
        Assert.NotNull(h.Provider.CreateWormholePair("rift", "other", "a", "b"));
    }

    private sealed class WormholeHarness : IDisposable
    {
        internal readonly LifecycleHub Hub;
        internal readonly WorldDefinitionRegistry Combat;
        internal readonly WormholePairRegistry Wormholes;
        internal readonly WormholePairCoordinator Coordinator;
        internal readonly WorldContentService Service;
        internal readonly IWorldProvider Provider;
        internal Guid Session;
        internal WormholeHarness()
        {
            Hub = new LifecycleHub((_, error) => throw error);
            var plugin = new object();
            StoryHostAuthenticator auth = (instance, caller) => ReferenceEquals(instance, plugin) ? new StoryHostPlugin("author.a", caller) : null;
            Combat = new WorldDefinitionRegistry(auth, Hub.CheckThread);
            Wormholes = new WormholePairRegistry(auth, Hub.CheckThread);
            Coordinator = new WormholePairCoordinator(Hub, Wormholes, new FakeWormholePairs(), _ => true, _ => { });
            Service = new WorldContentService(Hub, Combat, null!, () => true,
                wormholeDefinitions: Wormholes, wormholeCoordinator: Coordinator);
            Provider = Service.AcquireProvider(plugin)!;
        }
        internal void BeginGameplay()
        {
            Session = Hub.Begin(SessionOrigin.NewGame, null);
            Hub.PlayerReady(Session); Hub.GameplayInitialized(Session);
        }
        public void Dispose() { Provider?.Dispose(); Coordinator.Dispose(); Service.Dispose(); Combat.Dispose(); Wormholes.Dispose(); Hub.Dispose(); }
    }
}
