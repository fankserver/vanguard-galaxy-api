using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>Fake authored-site native seam modelling created/resolvable identities.</summary>
internal sealed class FakeAuthoredSiteNative : IAuthoredSiteNative
{
    internal readonly Dictionary<string, (string SystemId, AuthoredSiteKind Kind)> Created = new(StringComparer.Ordinal);
    internal readonly HashSet<string> Hidden = new(StringComparer.Ordinal);
    internal bool RefuseCreation;
    internal int Ambiguous;
    internal int Creations;
    private int _next;
    public string? CreateSite(Guid session, string systemId, float x, float y, AuthoredSiteDeclaration declaration)
    {
        Creations++;
        if (RefuseCreation || systemId == "missing") return null;
        var id = "site-" + ++_next;
        Created[id] = (systemId, declaration.Kind);
        return id;
    }
    public string? ResolveSite(Guid session, string systemId, string poiId, AuthoredSiteKind kind)
        => !Hidden.Contains(poiId) && Created.TryGetValue(poiId, out var row) && row.SystemId == systemId && row.Kind == kind ? poiId : null;
    public int AmbiguousCount(Guid session, string poiId) => Ambiguous > 0 ? Ambiguous : Created.ContainsKey(poiId) ? 1 : 0;
    public void BeginPass(Guid session) { }
    public void EndPass() { }
}

public sealed class AuthoredSiteTests
{
    private sealed class Harness : IDisposable
    {
        internal readonly LifecycleHub Hub;
        internal readonly FakeAuthoredSiteNative Native;
        internal readonly WorldDefinitionRegistry Combat;
        internal readonly AuthoredSiteRegistry Sites;
        internal readonly AuthoredSiteCoordinator Coordinator;
        internal readonly WorldContentService Service;
        internal IWorldProvider Provider = null!;
        internal Guid Session;
        internal bool PersistenceReady = true;
        internal Harness(Func<bool>? canAuthor = null)
        {
            Hub = new LifecycleHub((_, error) => throw error);
            Native = new FakeAuthoredSiteNative();
            var plugin = new object();
            StoryHostAuthenticator auth = (instance, caller) => ReferenceEquals(instance, plugin) ? new StoryHostPlugin("author.a", caller) : null;
            Combat = new WorldDefinitionRegistry(auth, Hub.CheckThread);
            Sites = new AuthoredSiteRegistry(auth, Hub.CheckThread);
            Coordinator = new AuthoredSiteCoordinator(Hub, Sites, Native, _ => PersistenceReady, _ => { });
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
    private static AuthoredSiteDefinition Salvage(string local = "wreck", int revision = 1, bool withStation = false)
        => AuthoredSiteDefinition.Salvage(local, revision, "Failed Refuge", 8, "Monsoon", "Fanatics", withStation, AuthoredSiteHazard.DamageInRadius, scatterAsteroids: true);

    [Fact]
    public void DefinitionValidationEnforcesKindPayloads()
    {
        Assert.Throws<ArgumentException>(() => AuthoredSiteDefinition.Salvage("a", 1, "n", 8, " ", "Fanatics"));
        Assert.Throws<ArgumentException>(() => AuthoredSiteDefinition.Salvage("a", 1, "n", 8, "Monsoon", " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => AuthoredSiteDefinition.Salvage("a", 1, "n", 4, "Monsoon", "Fanatics", withStation: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => AuthoredSiteDefinition.MiningField("a", 1, "n", 8, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => AuthoredSiteDefinition.MiningField("a", 1, "n", 8, 65));
        Assert.Equal(6, AuthoredSiteDefinition.MiningField("singers-field", 1, "Singer's Field", 8, 6).AsteroidCount);
    }

    [Fact]
    public void RegistrationIsPreSessionAndCreationIsKeyedToOneInstance()
    {
        using var h = new Harness();
        Assert.Equal(WorldStatus.Succeeded, h.Provider.RegisterAuthoredSite(Salvage()));
        Assert.Equal(WorldStatus.DuplicateDefinition, h.Provider.RegisterAuthoredSite(Salvage()));
        h.BeginGameplay();
        Assert.Equal(WorldStatus.NotReady, h.Provider.RegisterAuthoredSite(AuthoredSiteDefinition.MiningField("late", 1, "n", 8, 6)));
        var site = h.Provider.CreateAuthoredSite("wreck", "act2-refuge", "pocket-system", 10, 4);
        Assert.NotNull(site);
        Assert.True(site!.State.Reconstructed);
        Assert.NotNull(site.PoiId);
        Assert.Equal(AuthoredActionStatus.Succeeded, site.LastAction.Status);
        Assert.Equal("Monsoon", site.Definition.WreckShipId);
        // Same key = same object instance, one native creation.
        Assert.Same(site, h.Provider.CreateAuthoredSite("wreck", "act2-refuge", "pocket-system", 10, 4));
        Assert.Same(site, h.Provider.GetAuthoredSite("wreck", "act2-refuge"));
        Assert.Equal(1, h.Native.Creations);
        Assert.Null(h.Provider.GetAuthoredSite("wreck", "other"));
        Assert.Single(h.Provider.GetAuthoredSites("wreck"));
    }

    [Fact]
    public void RefusedCreationIsTypedAndDoesNotRetryImplicitly()
    {
        using var h = new Harness();
        Assert.Equal(WorldStatus.Succeeded, h.Provider.RegisterAuthoredSite(Salvage()));
        h.BeginGameplay();
        h.Native.RefuseCreation = true;
        var site = h.Provider.CreateAuthoredSite("wreck", "k", "system", 0, 0);
        Assert.NotNull(site);
        Assert.Equal(AuthoredActionStatus.Rejected, site!.LastAction.Status);
        Assert.False(site.State.Reconstructed);
        h.Native.RefuseCreation = false;
        // A failed key stays failed for the session; creation is not silently retried.
        Assert.Equal(AuthoredActionStatus.Rejected, h.Provider.CreateAuthoredSite("wreck", "k", "system", 0, 0)!.LastAction.Status);
        Assert.Equal(1, h.Native.Creations); // the failed key is retained; creation is never re-invoked implicitly
    }

    [Fact]
    public void RestoredRowsReconstructAndSettleWithActualOutcomes()
    {
        using var h = new Harness();
        Assert.Equal(WorldStatus.Succeeded, h.Provider.RegisterAuthoredSite(Salvage()));
        Assert.Equal(WorldStatus.Succeeded, h.Provider.RegisterAuthoredSite(AuthoredSiteDefinition.MiningField("field", 1, "Singer's", 8, 6)));
        h.BeginGameplay();
        var wreck = h.Provider.CreateAuthoredSite("wreck", "a", "system", 0, 0)!;
        var field = h.Provider.CreateAuthoredSite("field", "b", "system", 5, 5)!;
        var rows = h.Coordinator.CaptureRows();
        Assert.Equal(2, rows.Length);

        using var restored = new Harness();
        Assert.Equal(WorldStatus.Succeeded, restored.Provider.RegisterAuthoredSite(Salvage()));
        Assert.Equal(WorldStatus.Succeeded, restored.Provider.RegisterAuthoredSite(AuthoredSiteDefinition.MiningField("field", 1, "Singer's", 8, 6)));
        AuthoredSitesSettledEvent? settled = null;
        restored.Provider.AuthoredSiteReconstructionSettled += e => settled = e;
        restored.BeginGameplay();
        foreach (var pair in h.Native.Created) restored.Native.Created[pair.Key] = pair.Value;
        restored.Native.Hidden.Add(field.PoiId!); // one occurrence not yet natively present
        var bytes = AuthoredSystemStateCodec.Encode(Array.Empty<AuthoredSystemOccurrence>(), rows);
        var decoded = AuthoredSystemStateCodec.DecodeAll(bytes);
        restored.Coordinator.RestoreRows(restored.Session, decoded.Sites);
        restored.Service.MaintainAuthoredSystems(restored.Session);
        Assert.NotNull(settled);
        Assert.Equal(wreck.PoiId, Assert.Single(settled!.Reconstructed).PoiId);
        var failure = Assert.Single(settled.Failures);
        Assert.Equal(AuthoredSystemFailureReason.NativeMissing, failure.Reason);
        Assert.Equal("b", failure.Occurrence.OccurrenceKey);
        // Convergence: the missing native surfaces later and the object transitions with a Changed event.
        int changes = 0;
        failure.Occurrence.Changed += _ => changes++;
        restored.Native.Hidden.Clear();
        restored.Service.MaintainAuthoredSystems(restored.Session);
        Assert.True(failure.Occurrence.State.Reconstructed);
        Assert.Equal(1, changes);
        // Settlement is once per session.
        settled = null;
        restored.Service.MaintainAuthoredSystems(restored.Session);
        Assert.Null(settled);
    }

    [Fact]
    public void RevisionMigrationAndMissingDefinitionAreTypedFailures()
    {
        using var h = new Harness();
        Assert.Equal(WorldStatus.Succeeded, h.Provider.RegisterAuthoredSite(Salvage()));
        h.BeginGameplay();
        var site = h.Provider.CreateAuthoredSite("wreck", "k", "system", 0, 0)!;
        var rows = h.Coordinator.CaptureRows();

        using var migrated = new Harness();
        Assert.Equal(WorldStatus.Succeeded, migrated.Provider.RegisterAuthoredSite(Salvage(revision: 2), Salvage(revision: 1)));
        migrated.BeginGameplay();
        foreach (var pair in h.Native.Created) migrated.Native.Created[pair.Key] = pair.Value;
        migrated.Coordinator.RestoreRows(migrated.Session, rows);
        var upgraded = migrated.Provider.GetAuthoredSite("wreck", "k")!;
        Assert.True(upgraded.State.Reconstructed);
        Assert.Equal(2, upgraded.Definition.Revision);

        using var mismatched = new Harness();
        Assert.Equal(WorldStatus.Succeeded, mismatched.Provider.RegisterAuthoredSite(Salvage(revision: 3)));
        mismatched.BeginGameplay();
        mismatched.Coordinator.RestoreRows(mismatched.Session, rows);
        Assert.Equal(AuthoredSystemFailureReason.RevisionMismatch, mismatched.Provider.GetAuthoredSite("wreck", "k")!.State.Reason);
    }

    [Fact]
    public void StaleSessionObjectsFreezeAndAmbiguityRefuses()
    {
        using var h = new Harness();
        Assert.Equal(WorldStatus.Succeeded, h.Provider.RegisterAuthoredSite(Salvage()));
        h.BeginGameplay();
        var site = h.Provider.CreateAuthoredSite("wreck", "k", "system", 0, 0)!;
        Assert.True(site.State.Reconstructed);
        h.Native.Ambiguous = 2;
        h.Service.MaintainAuthoredSystems(h.Session);
        Assert.Equal(AuthoredSystemFailureReason.AmbiguousIdentity, site.State.Reason);
        h.Native.Ambiguous = 0;
        // Session replacement freezes the old object at its last state.
        var frozen = site.State;
        var next = h.Hub.Begin(SessionOrigin.NewGame, null);
        h.Hub.PlayerReady(next); h.Hub.GameplayInitialized(next);
        h.Service.MaintainAuthoredSystems(next);
        Assert.Equal(frozen.Status, site.State.Status);
        Assert.Null(h.Provider.GetAuthoredSite("wreck", "k"));
    }

    [Fact]
    public void CrossKindKeyCollisionsAreRefusedAtCreationNotAtSaveTime()
    {
        using var hub = new LifecycleHub((_, error) => throw error);
        var native = new FakeAuthoredSiteNative();
        var systemsNative = new FakeAuthoredNative();
        var plugin = new object();
        StoryHostAuthenticator auth = (instance, caller) => ReferenceEquals(instance, plugin) ? new StoryHostPlugin("author.a", caller) : null;
        using var combat = new WorldDefinitionRegistry(auth, hub.CheckThread);
        using var systems = new AuthoredSystemRegistry(auth, hub.CheckThread);
        using var sites = new AuthoredSiteRegistry(auth, hub.CheckThread);
        using var systemCoordinator = new AuthoredSystemCoordinator(hub, systems, systemsNative, () => true, _ => true, _ => { });
        using var siteCoordinator = new AuthoredSiteCoordinator(hub, sites, native, _ => true, _ => { });
        using var service = new WorldContentService(hub, combat, null!, () => true, null, null, null, null, systems, systemCoordinator, sites, siteCoordinator);
        using var provider = service.AcquireProvider(plugin)!;
        Assert.Equal(WorldStatus.Succeeded, provider.RegisterAuthoredSystem(new AuthoredSystemDefinition("outpost", 1, "Pocket")));
        Assert.Equal(WorldStatus.Succeeded, provider.RegisterAuthoredSite(AuthoredSiteDefinition.MiningField("outpost", 1, "Field", 8, 6)));
        var session = hub.Begin(SessionOrigin.NewGame, null);
        hub.PlayerReady(session); hub.GameplayInitialized(session);
        Assert.NotNull(provider.CreateAuthoredSystem("outpost", "k", "anchor"));
        // The same (local, key) under the other kind must refuse instead of poisoning the shared envelope.
        Assert.Null(provider.CreateAuthoredSite("outpost", "k", "system", 0, 0));
        Assert.NotNull(provider.CreateAuthoredSite("outpost", "other", "system", 0, 0));
        Assert.Null(provider.CreateAuthoredSystem("outpost", "other", "anchor"));
        // The combined capture stays encodable.
        _ = AuthoredSystemStateCodec.Encode(systemCoordinator.CaptureRows(), siteCoordinator.CaptureRows());
    }

    [Fact]
    public void DefinitionFallbackHonorsTheRetainedRowKind()
    {
        using var h = new Harness();
        Assert.Equal(WorldStatus.Succeeded, h.Provider.RegisterAuthoredSite(Salvage()));
        h.BeginGameplay();
        _ = h.Provider.CreateAuthoredSite("wreck", "k", "system", 0, 0)!;
        var rows = h.Coordinator.CaptureRows();
        // A restored harness with NO live definition still reports the retained salvage kind.
        using var bare = new Harness();
        bare.BeginGameplay();
        foreach (var pair in h.Native.Created) bare.Native.Created[pair.Key] = pair.Value;
        bare.Coordinator.RestoreRows(bare.Session, rows);
        var site = bare.Provider.GetAuthoredSite("wreck", "k")!;
        Assert.Equal(AuthoredSiteKind.SalvageSite, site.Definition.Kind);
        Assert.Equal(AuthoredSystemFailureReason.MissingDefinition, site.State.Reason);
    }

    [Fact]
    public void CodecRoundTripsMixedKindsAndReadsLegacySchema()
    {
        var systems = new[] { new AuthoredSystemOccurrence("o", "sys", "k1", 1, "system-1", "gate-a", "gate-b", declaredOpen: true) };
        var sites = new[]
        {
            new AuthoredSiteOccurrence("o", "wreck", "k2", 3, AuthoredSiteKind.SalvageSite, "system-1", "poi-1"),
            new AuthoredSiteOccurrence("o", "field", "k3", 1, AuthoredSiteKind.MiningField, "system-1", "poi-2")
        };
        var decoded = AuthoredSystemStateCodec.DecodeAll(AuthoredSystemStateCodec.Encode(systems, sites));
        Assert.Single(decoded.Systems);
        Assert.Equal(2, decoded.Sites.Length);
        Assert.Equal(AuthoredSiteKind.MiningField, decoded.Sites[1].Kind);
        Assert.Equal("poi-1", decoded.Sites[0].PoiId);
        Assert.True(decoded.Systems[0].DeclaredOpen);
        // Schema-1 payloads (all pocket-system rows) remain readable.
        var legacy = LegacyEncode(systems);
        var fromLegacy = AuthoredSystemStateCodec.DecodeAll(legacy);
        Assert.Single(fromLegacy.Systems); Assert.Empty(fromLegacy.Sites);
        Assert.Throws<InvalidDataException>(() => AuthoredSystemStateCodec.DecodeAll(legacy.Concat(new byte[] { 1 }).ToArray()));
        // Duplicate keys across kinds are refused.
        Assert.Throws<InvalidDataException>(() => AuthoredSystemStateCodec.Encode(systems,
            new[] { new AuthoredSiteOccurrence("o", "sys", "k1", 1, AuthoredSiteKind.SalvageSite, "s", "p") }));
    }

    private static byte[] LegacyEncode(AuthoredSystemOccurrence[] rows)
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
}
