using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>Fake authored-site native seam modelling created/resolvable identities.</summary>
internal sealed class FakeResourceSiteNative : IResourceSiteNative
{
    internal readonly Dictionary<string, (string SystemId, ResourceSiteKind Kind)> Created = new(StringComparer.Ordinal);
    internal readonly HashSet<string> Hidden = new(StringComparer.Ordinal);
    internal bool RefuseCreation;
    internal int Ambiguous;
    internal int Creations;
    internal ResourceSiteRemoveOutcome RemoveOutcome = ResourceSiteRemoveOutcome.Removed;
    internal bool ThrowOnRemove;
    internal int RemoveCalls;
    private int _next;
    public string? CreateSite(Guid session, string systemId, float x, float y, ResourceSiteDeclaration declaration)
    {
        Creations++;
        if (RefuseCreation || systemId == "missing") return null;
        var id = "site-" + ++_next;
        Created[id] = (systemId, declaration.Kind);
        return id;
    }
    public string? ResolveSite(Guid session, string systemId, string poiId, ResourceSiteKind kind)
        => !Hidden.Contains(poiId) && Created.TryGetValue(poiId, out var row) && row.SystemId == systemId && row.Kind == kind ? poiId : null;
    public ResourceSiteRemoveOutcome RemoveSite(Guid session, string systemId, string poiId, ResourceSiteKind kind)
    {
        RemoveCalls++;
        if (ThrowOnRemove) throw new InvalidOperationException("fake remove fault");
        if (!Created.TryGetValue(poiId, out var row) || row.SystemId != systemId || row.Kind != kind)
            return ResourceSiteRemoveOutcome.Missing;
        if (RemoveOutcome != ResourceSiteRemoveOutcome.Removed) return RemoveOutcome;
        Created.Remove(poiId);
        return ResourceSiteRemoveOutcome.Removed;
    }
    public int AmbiguousCount(Guid session, string poiId) => Ambiguous > 0 ? Ambiguous : Created.ContainsKey(poiId) ? 1 : 0;
    public void BeginPass(Guid session) { }
    public void EndPass() { }
}

public sealed class ResourceSiteTests
{
    private sealed class Harness : IDisposable
    {
        internal readonly LifecycleHub Hub;
        internal readonly FakeResourceSiteNative Native;
        internal readonly WorldDefinitionRegistry Combat;
        internal readonly ResourceSiteRegistry Sites;
        internal readonly ResourceSiteCoordinator Coordinator;
        internal readonly WorldContentService Service;
        internal IWorldProvider Provider = null!;
        internal Guid Session;
        internal bool PersistenceReady = true;
        internal Harness(Func<bool>? canAuthor = null)
        {
            Hub = new LifecycleHub((_, error) => throw error);
            Native = new FakeResourceSiteNative();
            var plugin = new object();
            StoryHostAuthenticator auth = (instance, caller) => ReferenceEquals(instance, plugin) ? new StoryHostPlugin("author.a", caller) : null;
            Combat = new WorldDefinitionRegistry(auth, Hub.CheckThread);
            Sites = new ResourceSiteRegistry(auth, Hub.CheckThread);
            Coordinator = new ResourceSiteCoordinator(Hub, Sites, Native, _ => PersistenceReady, _ => { });
            Service = new WorldContentService(Hub, Combat, null!, canAuthor ?? (() => true), null, null, null, null, null, null, Sites, Coordinator);
            Provider = Service.AcquireProvider(plugin)!;
        }
        internal void BeginGameplay()
        {
            Session = Hub.Begin(SessionOrigin.NewGame, null);
            Hub.PlayerReady(Session); Hub.GameplayInitialized(Session);
        }
        public void Dispose() { Provider?.Dispose(); Coordinator.Dispose(); Service.Dispose(); Combat.Dispose(); Sites.Dispose(); Hub.Dispose(); }
    }
    private static ResourceSiteDefinition Salvage(string local = "wreck", int revision = 1, bool withStation = false)
        => ResourceSiteDefinition.Salvage(local, revision, "Failed Refuge", 8, "Monsoon", "Fanatics", withStation, ResourceSiteHazard.DamageInRadius, scatterAsteroids: true);

    [Fact]
    public void DefinitionValidationEnforcesKindPayloads()
    {
        Assert.Throws<ArgumentException>(() => ResourceSiteDefinition.Salvage("a", 1, "n", 8, " ", "Fanatics"));
        Assert.Throws<ArgumentException>(() => ResourceSiteDefinition.Salvage("a", 1, "n", 8, "Monsoon", " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => ResourceSiteDefinition.Salvage("a", 1, "n", 4, "Monsoon", "Fanatics", withStation: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => ResourceSiteDefinition.MiningField("a", 1, "n", 8, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ResourceSiteDefinition.MiningField("a", 1, "n", 8, 65));
        Assert.Equal(6, ResourceSiteDefinition.MiningField("singers-field", 1, "Singer's Field", 8, 6).AsteroidCount);
    }

    [Fact]
    public void RegistrationIsPreSessionAndCreationIsKeyedToOneInstance()
    {
        using var h = new Harness();
        Assert.Equal(WorldStatus.Succeeded, h.Provider.RegisterResourceSite(Salvage()));
        Assert.Equal(WorldStatus.DuplicateDefinition, h.Provider.RegisterResourceSite(Salvage()));
        h.BeginGameplay();
        Assert.Equal(WorldStatus.NotReady, h.Provider.RegisterResourceSite(ResourceSiteDefinition.MiningField("late", 1, "n", 8, 6)));
        var site = h.Provider.CreateResourceSite("wreck", "act2-refuge", "pocket-system", 10, 4);
        Assert.NotNull(site);
        Assert.True(site!.State.Reconstructed);
        Assert.NotNull(site.PoiId);
        Assert.Equal(WorldContentStatus.Succeeded, site.LastAction.Status);
        Assert.Equal("Monsoon", site.Definition.WreckShipId);
        // Same key = same object instance, one native creation.
        Assert.Same(site, h.Provider.CreateResourceSite("wreck", "act2-refuge", "pocket-system", 10, 4));
        Assert.Same(site, h.Provider.GetResourceSite("wreck", "act2-refuge"));
        Assert.Equal(1, h.Native.Creations);
        Assert.Null(h.Provider.GetResourceSite("wreck", "other"));
        Assert.Single(h.Provider.GetResourceSites("wreck"));
    }

    [Fact]
    public void RefusedCreationIsTypedAndDoesNotRetryImplicitly()
    {
        using var h = new Harness();
        Assert.Equal(WorldStatus.Succeeded, h.Provider.RegisterResourceSite(Salvage()));
        h.BeginGameplay();
        h.Native.RefuseCreation = true;
        var site = h.Provider.CreateResourceSite("wreck", "k", "system", 0, 0);
        Assert.NotNull(site);
        Assert.Equal(WorldContentStatus.Rejected, site!.LastAction.Status);
        Assert.False(site.State.Reconstructed);
        h.Native.RefuseCreation = false;
        // A failed key stays failed for the session; creation is not silently retried.
        Assert.Equal(WorldContentStatus.Rejected, h.Provider.CreateResourceSite("wreck", "k", "system", 0, 0)!.LastAction.Status);
        Assert.Equal(1, h.Native.Creations); // the failed key is retained; creation is never re-invoked implicitly
    }

    [Fact]
    public void RestoredRowsReconstructAndSettleWithActualOutcomes()
    {
        using var h = new Harness();
        Assert.Equal(WorldStatus.Succeeded, h.Provider.RegisterResourceSite(Salvage()));
        Assert.Equal(WorldStatus.Succeeded, h.Provider.RegisterResourceSite(ResourceSiteDefinition.MiningField("field", 1, "Singer's", 8, 6)));
        h.BeginGameplay();
        var wreck = h.Provider.CreateResourceSite("wreck", "a", "system", 0, 0)!;
        var field = h.Provider.CreateResourceSite("field", "b", "system", 5, 5)!;
        var rows = h.Coordinator.CaptureRows();
        Assert.Equal(2, rows.Length);

        using var restored = new Harness();
        Assert.Equal(WorldStatus.Succeeded, restored.Provider.RegisterResourceSite(Salvage()));
        Assert.Equal(WorldStatus.Succeeded, restored.Provider.RegisterResourceSite(ResourceSiteDefinition.MiningField("field", 1, "Singer's", 8, 6)));
        ResourceSitesSettledEvent? settled = null;
        restored.Provider.ResourceSiteReconstructionSettled += e => settled = e;
        restored.BeginGameplay();
        foreach (var pair in h.Native.Created) restored.Native.Created[pair.Key] = pair.Value;
        restored.Native.Hidden.Add(field.PoiId!); // one occurrence not yet natively present
        var bytes = PocketSystemStateCodec.Encode(Array.Empty<PocketSystemOccurrence>(), rows);
        var decoded = PocketSystemStateCodec.DecodeAll(bytes);
        restored.Coordinator.RestoreRows(restored.Session, decoded.Sites);
        restored.Service.MaintainPocketSystems(restored.Session);
        Assert.NotNull(settled);
        Assert.Equal(wreck.PoiId, Assert.Single(settled!.Reconstructed).PoiId);
        var failure = Assert.Single(settled.Failures);
        Assert.Equal(ReconstructionFailureReason.NativeMissing, failure.Reason);
        Assert.Equal("b", failure.Occurrence.OccurrenceKey);
        // Convergence: the missing native surfaces later and the object transitions with a Changed event.
        int changes = 0;
        failure.Occurrence.Changed += _ => changes++;
        restored.Native.Hidden.Clear();
        restored.Service.MaintainPocketSystems(restored.Session);
        Assert.True(failure.Occurrence.State.Reconstructed);
        Assert.Equal(1, changes);
        // Settlement is once per session.
        settled = null;
        restored.Service.MaintainPocketSystems(restored.Session);
        Assert.Null(settled);
    }

    [Fact]
    public void RevisionMigrationAndMissingDefinitionAreTypedFailures()
    {
        using var h = new Harness();
        Assert.Equal(WorldStatus.Succeeded, h.Provider.RegisterResourceSite(Salvage()));
        h.BeginGameplay();
        var site = h.Provider.CreateResourceSite("wreck", "k", "system", 0, 0)!;
        var rows = h.Coordinator.CaptureRows();

        using var migrated = new Harness();
        Assert.Equal(WorldStatus.Succeeded, migrated.Provider.RegisterResourceSite(Salvage(revision: 2), Salvage(revision: 1)));
        migrated.BeginGameplay();
        foreach (var pair in h.Native.Created) migrated.Native.Created[pair.Key] = pair.Value;
        migrated.Coordinator.RestoreRows(migrated.Session, rows);
        var upgraded = migrated.Provider.GetResourceSite("wreck", "k")!;
        Assert.True(upgraded.State.Reconstructed);
        Assert.Equal(2, upgraded.Definition.Revision);

        using var mismatched = new Harness();
        Assert.Equal(WorldStatus.Succeeded, mismatched.Provider.RegisterResourceSite(Salvage(revision: 3)));
        mismatched.BeginGameplay();
        mismatched.Coordinator.RestoreRows(mismatched.Session, rows);
        Assert.Equal(ReconstructionFailureReason.RevisionMismatch, mismatched.Provider.GetResourceSite("wreck", "k")!.State.Reason);
    }

    [Fact]
    public void StaleSessionObjectsFreezeAndAmbiguityRefuses()
    {
        using var h = new Harness();
        Assert.Equal(WorldStatus.Succeeded, h.Provider.RegisterResourceSite(Salvage()));
        h.BeginGameplay();
        var site = h.Provider.CreateResourceSite("wreck", "k", "system", 0, 0)!;
        Assert.True(site.State.Reconstructed);
        h.Native.Ambiguous = 2;
        h.Service.MaintainPocketSystems(h.Session);
        Assert.Equal(ReconstructionFailureReason.AmbiguousIdentity, site.State.Reason);
        h.Native.Ambiguous = 0;
        // Session replacement freezes the old object at its last state.
        var frozen = site.State;
        var next = h.Hub.Begin(SessionOrigin.NewGame, null);
        h.Hub.PlayerReady(next); h.Hub.GameplayInitialized(next);
        h.Service.MaintainPocketSystems(next);
        Assert.Equal(frozen.Status, site.State.Status);
        Assert.Null(h.Provider.GetResourceSite("wreck", "k"));
    }

    [Fact]
    public void CrossKindKeyCollisionsAreRefusedAtCreationNotAtSaveTime()
    {
        using var hub = new LifecycleHub((_, error) => throw error);
        var native = new FakeResourceSiteNative();
        var systemsNative = new FakePocketSystemNative();
        var plugin = new object();
        StoryHostAuthenticator auth = (instance, caller) => ReferenceEquals(instance, plugin) ? new StoryHostPlugin("author.a", caller) : null;
        using var combat = new WorldDefinitionRegistry(auth, hub.CheckThread);
        using var systems = new PocketSystemRegistry(auth, hub.CheckThread);
        using var sites = new ResourceSiteRegistry(auth, hub.CheckThread);
        using var systemCoordinator = new PocketSystemCoordinator(hub, systems, systemsNative, () => true, _ => true, _ => { });
        using var siteCoordinator = new ResourceSiteCoordinator(hub, sites, native, _ => true, _ => { });
        using var service = new WorldContentService(hub, combat, null!, () => true, null, null, null, null, systems, systemCoordinator, sites, siteCoordinator);
        using var provider = service.AcquireProvider(plugin)!;
        Assert.Equal(WorldStatus.Succeeded, provider.RegisterPocketSystem(new PocketSystemDefinition("outpost", 1, "Pocket")));
        Assert.Equal(WorldStatus.Succeeded, provider.RegisterResourceSite(ResourceSiteDefinition.MiningField("outpost", 1, "Field", 8, 6)));
        var session = hub.Begin(SessionOrigin.NewGame, null);
        hub.PlayerReady(session); hub.GameplayInitialized(session);
        Assert.NotNull(provider.CreatePocketSystem("outpost", "k", "anchor"));
        // The same (local, key) under the other kind must refuse instead of poisoning the shared envelope.
        Assert.Null(provider.CreateResourceSite("outpost", "k", "system", 0, 0));
        Assert.NotNull(provider.CreateResourceSite("outpost", "other", "system", 0, 0));
        Assert.Null(provider.CreatePocketSystem("outpost", "other", "anchor"));
        // The combined capture stays encodable.
        _ = PocketSystemStateCodec.Encode(systemCoordinator.CaptureRows(), siteCoordinator.CaptureRows());
    }

    [Fact]
    public void DefinitionFallbackHonorsTheRetainedRowKind()
    {
        using var h = new Harness();
        Assert.Equal(WorldStatus.Succeeded, h.Provider.RegisterResourceSite(Salvage()));
        h.BeginGameplay();
        _ = h.Provider.CreateResourceSite("wreck", "k", "system", 0, 0)!;
        var rows = h.Coordinator.CaptureRows();
        // A restored harness with NO live definition still reports the retained salvage kind.
        using var bare = new Harness();
        bare.BeginGameplay();
        foreach (var pair in h.Native.Created) bare.Native.Created[pair.Key] = pair.Value;
        bare.Coordinator.RestoreRows(bare.Session, rows);
        var site = bare.Provider.GetResourceSite("wreck", "k")!;
        Assert.Equal(ResourceSiteKind.SalvageSite, site.Definition.Kind);
        Assert.Equal(ReconstructionFailureReason.MissingDefinition, site.State.Reason);
    }

    [Fact]
    public void CodecRoundTripsMixedKindsAndReadsLegacySchema()
    {
        var systems = new[] { new PocketSystemOccurrence("o", "sys", "k1", 1, "system-1", "gate-a", "gate-b", declaredOpen: true) };
        var sites = new[]
        {
            new ResourceSiteOccurrence("o", "wreck", "k2", 3, ResourceSiteKind.SalvageSite, "system-1", "poi-1"),
            new ResourceSiteOccurrence("o", "field", "k3", 1, ResourceSiteKind.MiningField, "system-1", "poi-2")
        };
        var decoded = PocketSystemStateCodec.DecodeAll(PocketSystemStateCodec.Encode(systems, sites));
        Assert.Single(decoded.Systems);
        Assert.Equal(2, decoded.Sites.Length);
        Assert.Equal(ResourceSiteKind.MiningField, decoded.Sites[1].Kind);
        Assert.Equal("poi-1", decoded.Sites[0].PoiId);
        Assert.True(decoded.Systems[0].DeclaredOpen);
        // Schema-1 payloads (all pocket-system rows) remain readable.
        var legacy = LegacyEncode(systems);
        var fromLegacy = PocketSystemStateCodec.DecodeAll(legacy);
        Assert.Single(fromLegacy.Systems); Assert.Empty(fromLegacy.Sites);
        Assert.Throws<InvalidDataException>(() => PocketSystemStateCodec.DecodeAll(legacy.Concat(new byte[] { 1 }).ToArray()));
        // Duplicate keys across kinds are refused.
        Assert.Throws<InvalidDataException>(() => PocketSystemStateCodec.Encode(systems,
            new[] { new ResourceSiteOccurrence("o", "sys", "k1", 1, ResourceSiteKind.SalvageSite, "s", "p") }));
    }

    private static byte[] LegacyEncode(PocketSystemOccurrence[] rows)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, new System.Text.UTF8Encoding(false, true), true);
        writer.Write(0x32534756); writer.Write(1); writer.Write(rows.Length);
        void Text(string value) { var bytes = System.Text.Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
        foreach (var row in rows)
        {
            Text(row.Owner); Text(row.LocalId); Text(row.OccurrenceKey); writer.Write(row.Revision);
            Text(row.SystemId); Text(row.EntranceGateId); Text(row.PocketGateId); writer.Write(row.DeclaredOpen);
        }
        writer.Flush(); return stream.ToArray();
    }

    // ---- direct remove -------------------------------------------------------------------

    [Fact]
    public void RemoveRemovesTheSiteItsSaveRowAndFreesTheKeyForAFreshInstance()
    {
        using var h = new Harness();
        Assert.Equal(WorldStatus.Succeeded, h.Provider.RegisterResourceSite(Salvage()));
        h.BeginGameplay();
        var site = h.Provider.CreateResourceSite("wreck", "k", "system", 0, 0)!;
        Assert.True(site.State.Reconstructed);
        Assert.Equal(WorldContentStatus.Succeeded, site.Remove().Status);
        // No save row remains, so load reconstructs nothing rather than reporting a failure.
        Assert.Empty(h.Coordinator.CaptureRows());
        Assert.Null(h.Provider.GetResourceSite("wreck", "k"));
        Assert.Empty(h.Provider.GetResourceSites("wreck"));
        // The object is terminal: Removed state, repeated remove refused with a retained result.
        Assert.Equal(ReconstructionStatus.Removed, site.State.Status);
        Assert.Null(site.PoiId);
        Assert.Equal(WorldContentStatus.Rejected, site.Remove().Status);
        Assert.Equal(WorldContentStatus.Rejected, site.LastAction.Status);
        // The freed key authors a FRESH site with fresh native identity.
        var fresh = h.Provider.CreateResourceSite("wreck", "k", "system", 0, 0)!;
        Assert.NotSame(site, fresh);
        Assert.Equal(ReconstructionStatus.Reconstructed, fresh.State.Status);
        Assert.Equal(2, h.Native.Creations);
    }

    [Theory]
    [InlineData((int)ResourceSiteRemoveOutcome.PlayerInside)]
    [InlineData((int)ResourceSiteRemoveOutcome.BoardingActive)]
    [InlineData((int)ResourceSiteRemoveOutcome.InteriorPersisted)]
    [InlineData((int)ResourceSiteRemoveOutcome.Missing)]
    [InlineData((int)ResourceSiteRemoveOutcome.Failed)]
    public void TypedRefusalsRetainTheSiteAndItsSaveRow(int outcomeValue)
    {
        var outcome = (ResourceSiteRemoveOutcome)outcomeValue;
        using var h = new Harness();
        Assert.Equal(WorldStatus.Succeeded, h.Provider.RegisterResourceSite(Salvage(withStation: true)));
        h.BeginGameplay();
        var site = h.Provider.CreateResourceSite("wreck", "k", "system", 0, 0)!;
        h.Native.RemoveOutcome = outcome;
        var refused = site.Remove();
        Assert.Equal(WorldContentStatus.Rejected, refused.Status);
        Assert.False(string.IsNullOrWhiteSpace(refused.Detail));
        // Nothing was dropped or removed; the occurrence stays live and actionable.
        Assert.Single(h.Coordinator.CaptureRows());
        Assert.Same(site, h.Provider.GetResourceSite("wreck", "k"));
        Assert.True(site.State.Reconstructed);
        h.Native.RemoveOutcome = ResourceSiteRemoveOutcome.Removed;
        Assert.True(site.Remove().Succeeded);
    }

    [Fact]
    public void NativeFaultIsReportedAsUnavailableAndRetainsTheRow()
    {
        using var h = new Harness();
        Assert.Equal(WorldStatus.Succeeded, h.Provider.RegisterResourceSite(Salvage()));
        h.BeginGameplay();
        var site = h.Provider.CreateResourceSite("wreck", "k", "system", 0, 0)!;
        h.Native.ThrowOnRemove = true;
        Assert.Equal(WorldContentStatus.Unavailable, site.Remove().Status);
        Assert.Single(h.Coordinator.CaptureRows());
        h.Native.ThrowOnRemove = false;
        Assert.True(site.Remove().Succeeded);
    }

    [Fact]
    public void FailedCreationRemovesWithoutNativeRemovalAndFreesTheKey()
    {
        using var h = new Harness();
        Assert.Equal(WorldStatus.Succeeded, h.Provider.RegisterResourceSite(Salvage()));
        h.BeginGameplay();
        h.Native.RefuseCreation = true;
        var failed = h.Provider.CreateResourceSite("wreck", "k", "system", 0, 0)!;
        Assert.Equal(WorldContentStatus.Rejected, failed.LastAction.Status);
        Assert.True(failed.Remove().Succeeded);
        Assert.Equal(0, h.Native.RemoveCalls); // nothing native was ever created
        Assert.Empty(h.Coordinator.CaptureRows());
        h.Native.RefuseCreation = false;
        Assert.True(h.Provider.CreateResourceSite("wreck", "k", "system", 0, 0)!.State.Reconstructed);
    }

    [Fact]
    public void KeepEnterableHoldRefusesRemoveUntilReleased()
    {
        using var h = new Harness();
        Assert.Equal(WorldStatus.Succeeded, h.Provider.RegisterResourceSite(Salvage(withStation: true)));
        h.BeginGameplay();
        var site = h.Provider.CreateResourceSite("wreck", "k", "system", 0, 0)!;
        h.Hub.Installations.Aegis.SetAvailable(true);
        var installation = h.Hub.Installations.Get("author.a", site.PoiId!, null);
        using var keep = installation.KeepEnterable();
        var refused = site.Remove();
        Assert.Equal(WorldContentStatus.Rejected, refused.Status);
        Assert.Contains("enterable", refused.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Single(h.Coordinator.CaptureRows());
        keep.Dispose();
        Assert.True(site.Remove().Succeeded);
    }

    [Fact]
    public void ReplacedSessionRefusesRemoveWithGameEnded()
    {
        using var h = new Harness();
        Assert.Equal(WorldStatus.Succeeded, h.Provider.RegisterResourceSite(Salvage()));
        h.BeginGameplay();
        var site = h.Provider.CreateResourceSite("wreck", "k", "system", 0, 0)!;
        h.Hub.Invalidate("session replaced by test");
        h.BeginGameplay();
        Assert.Equal(WorldContentStatus.GameEnded, site.Remove().Status);
        Assert.Equal(0, h.Native.RemoveCalls); // the replacement save was never touched
    }

    [Fact]
    public void RemoveDropsAnAttachedDungeonOccurrenceOnlyAfterVerifiedRemoval()
    {
        using var h = new Harness();
        Assert.Equal(WorldStatus.Succeeded, h.Provider.RegisterResourceSite(Salvage(withStation: true)));
        h.BeginGameplay();
        var site = h.Provider.CreateResourceSite("wreck", "k", "system", 0, 0)!;
        var occurrence = Guid.NewGuid();
        var resolved = new List<string>(); var dropped = new List<Guid>();
        h.Coordinator.AttachDungeonOccurrencePrune(
            poiId => { resolved.Add(poiId); return occurrence; },
            id => { dropped.Add(id); return true; });
        int changes = 0; site.Changed += _ => changes++;
        var poiId = site.PoiId;
        Assert.True(site.Remove().Succeeded);
        // The location is resolved (before removal) and the row dropped (after) exactly once.
        Assert.Equal(poiId, Assert.Single(resolved));
        Assert.Equal(occurrence, Assert.Single(dropped));
        Assert.Empty(h.Coordinator.CaptureRows());
        Assert.Equal(1, changes);
    }

    [Fact]
    public void AttachedDungeonOccurrenceIsNotDroppedWhenTheNativeRemovalIsRefused()
    {
        using var h = new Harness();
        Assert.Equal(WorldStatus.Succeeded, h.Provider.RegisterResourceSite(Salvage(withStation: true)));
        h.BeginGameplay();
        var site = h.Provider.CreateResourceSite("wreck", "k", "system", 0, 0)!;
        var dropped = new List<Guid>();
        h.Coordinator.AttachDungeonOccurrencePrune(_ => Guid.NewGuid(), id => { dropped.Add(id); return true; });
        h.Native.RemoveOutcome = ResourceSiteRemoveOutcome.BoardingActive;
        Assert.Equal(WorldContentStatus.Rejected, site.Remove().Status);
        Assert.Empty(dropped);
        Assert.Single(h.Coordinator.CaptureRows());
    }

    [Fact]
    public void UnreadableAttachedDungeonStateRefusesRemoveWithoutTouchingTheSite()
    {
        using var h = new Harness();
        Assert.Equal(WorldStatus.Succeeded, h.Provider.RegisterResourceSite(Salvage(withStation: true)));
        h.BeginGameplay();
        var site = h.Provider.CreateResourceSite("wreck", "k", "system", 0, 0)!;
        h.Coordinator.AttachDungeonOccurrencePrune(_ => throw new InvalidOperationException("read fault"), _ => true);
        Assert.Equal(WorldContentStatus.Unavailable, site.Remove().Status);
        Assert.Equal(0, h.Native.RemoveCalls); // nothing native was touched
        Assert.Single(h.Coordinator.CaptureRows());
    }

    [Fact]
    public void FailedDungeonRowDropStillRemovesTheSite()
    {
        using var h = new Harness();
        Assert.Equal(WorldStatus.Succeeded, h.Provider.RegisterResourceSite(Salvage(withStation: true)));
        h.BeginGameplay();
        var site = h.Provider.CreateResourceSite("wreck", "k", "system", 0, 0)!;
        var dropped = new List<Guid>();
        h.Coordinator.AttachDungeonOccurrencePrune(_ => Guid.NewGuid(), id => { dropped.Add(id); return false; });
        Assert.True(site.Remove().Succeeded);
        Assert.Single(dropped); // the drop was attempted
        Assert.Empty(h.Coordinator.CaptureRows());
        Assert.Equal(ReconstructionStatus.Removed, site.State.Status);
    }

    [Fact]
    public void RemoveRefusesWhenWorldAuthoringIsUnavailable()
    {
        bool canAuthor = true;
        using var h = new Harness(canAuthor: () => canAuthor);
        Assert.Equal(WorldStatus.Succeeded, h.Provider.RegisterResourceSite(Salvage()));
        h.BeginGameplay();
        var site = h.Provider.CreateResourceSite("wreck", "k", "system", 0, 0)!;
        canAuthor = false;
        var refused = site.Remove();
        Assert.Equal(WorldContentStatus.Unavailable, refused.Status);
        Assert.Equal(0, h.Native.RemoveCalls); // no native mutation while authoring is unavailable
        Assert.Single(h.Coordinator.CaptureRows());
        canAuthor = true;
        Assert.True(site.Remove().Succeeded);
    }
}
