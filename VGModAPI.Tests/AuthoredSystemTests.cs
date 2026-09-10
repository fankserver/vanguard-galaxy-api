using System;
using System.Collections.Generic;
using System.Linq;
using LightJson;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

public sealed class AuthoredSystemTests
{
    private sealed class Harness : IDisposable
    {
        internal readonly LifecycleHub Hub;
        internal readonly FakeAuthoredNative Native;
        internal readonly WorldDefinitionRegistry Combat;
        internal readonly AuthoredSystemRegistry Systems;
        internal readonly AuthoredSystemCoordinator Coordinator;
        internal readonly WorldContentService Service;
        internal IWorldProvider Provider = null!;
        internal Guid Session;
        private readonly bool _disposeProvider;
        internal Harness(Func<bool>? canAuthor = null, Func<Guid, bool>? persistenceReady = null, bool extensionAvailable = true)
        {
            Hub = new LifecycleHub((_, error) => throw error);
            Native = new FakeAuthoredNative();
            var plugin = new object();
            StoryHostAuthenticator auth = (instance, caller) =>
                ReferenceEquals(instance, plugin) && extensionAvailable ? new StoryHostPlugin("author.a", caller) : null;
            Combat = new WorldDefinitionRegistry(auth, Hub.CheckThread);
            Systems = new AuthoredSystemRegistry(auth, Hub.CheckThread);
            Coordinator = new AuthoredSystemCoordinator(Hub, Systems, Native,
                canAuthor ?? (() => true), persistenceReady ?? (_ => true), _ => { });
            Service = new WorldContentService(Hub, Combat, null!, canAuthor ?? (() => true), null, null, null, null, Systems, Coordinator);
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
    public void RegistrationIsPreSessionImmutableRevisionedAndKeyed()
    {
        using var harness = new Harness();
        Assert.Equal(WorldStatus.Succeeded, harness.Provider.RegisterAuthoredSystem(new AuthoredSystemDefinition("sysA", 1, "Pocket")));
        Assert.Equal(WorldStatus.DuplicateDefinition, harness.Provider.RegisterAuthoredSystem(new AuthoredSystemDefinition("sysA", 1, "Pocket")));
        Assert.Equal(WorldStatus.InvalidDefinition, harness.Provider.RegisterAuthoredSystem(new AuthoredSystemDefinition("sysBad", 0, "Pocket")));
        Assert.Equal(WorldStatus.InvalidDefinition, harness.Provider.RegisterAuthoredSystem(new AuthoredSystemDefinition("sysBad", 1, "  ")));
        Assert.Equal(WorldStatus.Succeeded, harness.Provider.RegisterAuthoredSystem(
            new AuthoredSystemDefinition("mig", 2, "New"), new AuthoredSystemDefinition("mig", 1, "Old")));
        Assert.Equal(WorldStatus.InvalidDefinition, harness.Provider.RegisterAuthoredSystem(
            new AuthoredSystemDefinition("mig2", 2, "New"), new AuthoredSystemDefinition("mig2", 3, "Down")));
        harness.BeginGameplay();
        Assert.Equal(WorldStatus.NotReady, harness.Provider.RegisterAuthoredSystem(new AuthoredSystemDefinition("late", 1, "Pocket")));
    }
    private static void Register(Harness h) =>
        Assert.Equal(WorldStatus.Succeeded, h.Provider.RegisterAuthoredSystem(new AuthoredSystemDefinition("sysA", 1, "Pocket")));
    private static IAuthoredSystem Create(Harness h, string key) => h.Provider.CreateAuthoredSystem("sysA", key, "anchor")!;

    [Fact]
    public void CreateReturnsOwnedObjectAndSameKeyReconcilesToTheSameInstance()
    {
        using var harness = new Harness();
        Register(harness);
        // Before the gameplay boundary creation is refused (no current actionable session).
        Assert.Null(harness.Provider.CreateAuthoredSystem("sysA", "k1", "anchor"));
        harness.BeginGameplay();
        var first = Create(harness, "k1");
        Assert.NotNull(first);
        Assert.Equal(AuthoredSystemReconstructionStatus.Reconstructed, first.State.Status);
        Assert.Equal("sys-1", first.SystemId);
        Assert.Equal("en-1", first.EntranceGatePoiId);
        Assert.Equal("pk-1", first.PocketGatePoiId);
        Assert.Equal(1, harness.Native.NextId);
        // Re-declaring the same key returns the SAME object instance (no duplicate native system).
        var second = Create(harness, "k1");
        Assert.Same(first, second);
        Assert.Equal(1, harness.Native.NextId);
        // Re-obtaining by key also returns the same object.
        Assert.Same(first, harness.Provider.GetAuthoredSystem("sysA", "k1"));
        Assert.Same(first, Assert.Single(harness.Provider.GetAuthoredSystems("sysA")));
        // A different key is a distinct owned occurrence object.
        var other = Create(harness, "k2");
        Assert.NotSame(first, other);
        Assert.Equal("sys-2", other.SystemId);
        Assert.Equal(2, harness.Native.NextId);
    }

    [Fact]
    public void ForeignOrAmbiguousNativeIdentityIsNeverAdopted()
    {
        using var harness = new Harness();
        Register(harness);
        harness.BeginGameplay();
        var first = Create(harness, "k1");
        Assert.Equal("sys-1", first.SystemId);
        // Tamper with the native gate identity so the owned occurrence no longer matches: rejected, not adopted.
        harness.Native.Systems["sys-1"] = ("en-999", "pk-999");
        harness.Service.MaintainAuthoredSystems(harness.Session);   // reconcile + object refresh
        Assert.Equal(AuthoredSystemReconstructionStatus.Failed, first.State.Status);
        Assert.Equal(AuthoredSystemFailureReason.AmbiguousIdentity, first.State.Reason);
        // Re-declaring the same key still reconciles to the SAME owned occurrence object.
        Assert.Same(first, Create(harness, "k1"));
        // A different key still gets a brand-new owned system.
        var other = Create(harness, "kcopy");
        Assert.Equal("sys-2", other.SystemId);
    }

    [Fact]
    public void EntranceOpenIsDeclarativeAndConvergesBothPeersWithHiddenRepair()
    {
        using var harness = new Harness();
        Register(harness);
        harness.BeginGameplay();
        var created = Create(harness, "k1");
        Assert.False(harness.Native.IsOpen(harness.Session, "en-1", "pk-1"));
        Assert.Equal(AuthoredActionStatus.Succeeded, created.SetEntranceOpen(true).Status);
        Assert.Equal(AuthoredActionStatus.Succeeded, created.LastAction.Status);
        Assert.True(harness.Native.IsOpen(harness.Session, "en-1", "pk-1"));
        // Declared state persists across reconcile: a native revert is repaired and re-opened together.
        harness.Native.Open["sys-1"] = false;   // simulate an out-of-band hidden/closed revert
        Assert.False(harness.Native.IsOpen(harness.Session, "en-1", "pk-1"));
        harness.Coordinator.Reconcile(harness.Session);
        Assert.True(harness.Native.IsOpen(harness.Session, "en-1", "pk-1"));
        // Declared close is honoured too.
        Assert.Equal(AuthoredActionStatus.Succeeded, created.SetEntranceOpen(false).Status);
        Assert.False(harness.Native.IsOpen(harness.Session, "en-1", "pk-1"));
    }

    [Fact]
    public void NoFreePositionIsATypedFailureAndSettlesAsNativeMissing()
    {
        using var harness = new Harness();
        Register(harness);
        harness.BeginGameplay();
        harness.Native.FailCreate = true;
        var rejected = harness.Provider.CreateAuthoredSystem("sysA", "k1", "anchor");
        Assert.NotNull(rejected);
        Assert.Equal(AuthoredSystemReconstructionStatus.Pending, rejected!.State.Status);
        AuthoredSystemsSettledEvent? captured = null;
        harness.Provider.AuthoredSystemReconstructionSettled += e => captured = e;
        harness.Coordinator.Reconcile(harness.Session);
        Assert.NotNull(captured);
        Assert.Single(captured!.Failures);
        Assert.Equal(AuthoredSystemFailureReason.NativeMissing, captured.Failures[0].Reason);
        Assert.NotNull(captured.Failures[0].Occurrence);
    }

    [Fact]
    public void EveryTypedFailureReasonIsReportedOncePerSession()
    {
        // MissingDefinition
        using (var missing = new Harness())
        {
            missing.BeginGameplay();
            missing.Coordinator.RestoreRows(missing.Session, new[]
            {
                new AuthoredSystemOccurrence("author.a", "ghost", "k", 1, "sys-1", "en-1", "pk-1", false)
            });
            var occurrence = missing.Provider.GetAuthoredSystem("ghost", "k");
            Assert.NotNull(occurrence);
            Assert.Equal(AuthoredSystemFailureReason.MissingDefinition, occurrence!.State.Reason);
        }
        // RevisionMismatch
        using (var mismatch = new Harness())
        {
            Register(mismatch); // rev 1
            mismatch.BeginGameplay();
            mismatch.Coordinator.RestoreRows(mismatch.Session, new[]
            {
                new AuthoredSystemOccurrence("author.a", "sysA", "k", 2, "sys-1", "en-1", "pk-1", false)
            });
            var occurrence = mismatch.Provider.GetAuthoredSystem("sysA", "k");
            Assert.Equal(AuthoredSystemFailureReason.RevisionMismatch, occurrence!.State.Reason);
        }
        // NativeMissing
        using (var native = new Harness())
        {
            Register(native);
            native.BeginGameplay();
            native.Coordinator.RestoreRows(native.Session, new[]
            {
                new AuthoredSystemOccurrence("author.a", "sysA", "k", 1, "sys-9", "en-9", "pk-9", false)
            });
            var occurrence = native.Provider.GetAuthoredSystem("sysA", "k");
            Assert.Equal(AuthoredSystemReconstructionStatus.Pending, occurrence!.State.Status);
            AuthoredSystemsSettledEvent? captured = null;
            native.Provider.AuthoredSystemReconstructionSettled += e => captured = e;
            native.Coordinator.Reconcile(native.Session);
            Assert.NotNull(captured);
            Assert.Equal(AuthoredSystemFailureReason.NativeMissing, captured!.Failures.Single().Reason);
        }
        // AmbiguousIdentity
        using (var ambiguous = new Harness())
        {
            Register(ambiguous);
            ambiguous.BeginGameplay();
            ambiguous.Coordinator.RestoreRows(ambiguous.Session, new[]
            {
                new AuthoredSystemOccurrence("author.a", "sysA", "k", 1, "sys-1", "en-1", "pk-1", false)
            });
            ambiguous.Native.Systems["sys-1"] = ("en-1", "pk-9");   // entry differs from owned gate
            var occurrence = ambiguous.Provider.GetAuthoredSystem("sysA", "k");
            Assert.Equal(AuthoredSystemFailureReason.AmbiguousIdentity, occurrence!.State.Reason);
        }
        // PersistenceUnavailable
        using (var persisted = new Harness(persistenceReady: _ => false))
        {
            Register(persisted);
            persisted.BeginGameplay();
            persisted.Coordinator.RestoreRows(persisted.Session, new[]
            {
                new AuthoredSystemOccurrence("author.a", "sysA", "k", 1, "sys-1", "en-1", "pk-1", false)
            });
            var occurrence = persisted.Provider.GetAuthoredSystem("sysA", "k");
            Assert.Equal(AuthoredSystemFailureReason.PersistenceUnavailable, occurrence!.State.Reason);
            // PersistenceUnavailable is genuinely reportable, not query-only: it reaches the settled event.
            AuthoredSystemsSettledEvent? capturedPersisted = null;
            persisted.Provider.AuthoredSystemReconstructionSettled += e => capturedPersisted = e;
            persisted.Coordinator.Reconcile(persisted.Session);
            Assert.NotNull(capturedPersisted);
            Assert.Equal(AuthoredSystemFailureReason.PersistenceUnavailable, capturedPersisted!.Failures.Single().Reason);
        }
    }

    [Fact]
    public void ReconstructionSettledReportsReconstructedOutcomesOnce()
    {
        using var harness = new Harness();
        Register(harness);
        harness.BeginGameplay();
        var created = Create(harness, "k1");
        Assert.Equal(AuthoredSystemReconstructionStatus.Reconstructed, created.State.Status);
        int events = 0;
        AuthoredSystemsSettledEvent? last = null;
        harness.Provider.AuthoredSystemReconstructionSettled += e => { events++; last = e; };
        harness.Service.MaintainAuthoredSystems(harness.Session);
        harness.Service.MaintainAuthoredSystems(harness.Session);   // second pass does not re-emit
        Assert.Equal(1, events);
        Assert.NotNull(last);
        Assert.Same(created, Assert.Single(last!.Reconstructed));
        Assert.Empty(last.Failures);
    }

    [Fact]
    public void AFaultySettledSubscriberDoesNotStarveSiblingSubscribers()
    {
        using var harness = new Harness();
        Register(harness);
        harness.BeginGameplay();
        Create(harness, "k1");
        AuthoredSystemsSettledEvent? second = null;
        harness.Provider.AuthoredSystemReconstructionSettled += _ => throw new InvalidOperationException("consumer bug");
        harness.Provider.AuthoredSystemReconstructionSettled += e => second = e;
        harness.Service.MaintainAuthoredSystems(harness.Session);
        Assert.NotNull(second); // The faulty sibling handler was isolated; delivery still happened.
    }

    [Fact]
    public void OccurrenceChangedFiresForItsOwnStateTransition()
    {
        using var harness = new Harness();
        Register(harness);
        harness.BeginGameplay();
        // A restored occurrence whose native pocket only surfaces later (Pending → Reconstructed on reconcile).
        harness.Coordinator.RestoreRows(harness.Session, new[]
        {
            new AuthoredSystemOccurrence("author.a", "sysA", "k1", 1, "sys-7", "en-7", "pk-7", false)
        });
        var occ = harness.Provider.GetAuthoredSystem("sysA", "k1");
        Assert.NotNull(occ);
        Assert.Equal(AuthoredSystemReconstructionStatus.Pending, occ!.State.Status);
        int changes = 0;
        occ.Changed += _ => changes++;
        harness.Native.Systems["sys-7"] = ("en-7", "pk-7");   // pocket becomes natively present
        harness.Service.MaintainAuthoredSystems(harness.Session);
        Assert.Equal(1, changes);
        Assert.Equal(AuthoredSystemReconstructionStatus.Reconstructed, occ.State.Status);
    }

    [Fact]
    public void StaleOccurrenceAfterSessionReplacementFailsActionsWithGameEndedAndDoesNotBleedNativeIdentity()
    {
        using var harness = new Harness();
        Register(harness);
        harness.BeginGameplay();
        var first = Create(harness, "k1");
        Assert.Equal("sys-1", first.SystemId);
        // A brand-new game: the session starts empty; the old object must refuse to act on the replacement save.
        harness.BeginGameplay();
        Assert.Equal(AuthoredActionStatus.GameEnded, first.SetEntranceOpen(true).Status);
        Assert.Null(harness.Provider.GetAuthoredSystem("sysA", "k1"));   // no replayed occurrence in the fresh save
        var fresh = Create(harness, "k1");
        Assert.Equal("sys-2", fresh.SystemId);
        Assert.NotSame(first, fresh);
        Assert.Equal(2, harness.Native.NextId);
    }

    [Fact]
    public void NativeFaultsFailOpenWithoutBreakingTheFacade()
    {
        using var harness = new Harness();
        Register(harness);
        harness.BeginGameplay();
        harness.Native.ThrowOnCreate = true;
        Assert.Null(harness.Provider.CreateAuthoredSystem("sysA", "k1", "anchor"));
        harness.Native.ThrowOnCreate = false;
        var ok = Create(harness, "k1");
        Assert.Equal(AuthoredSystemReconstructionStatus.Reconstructed, ok.State.Status);
        harness.Native.ThrowOnApply = true;
        Assert.Equal(AuthoredActionStatus.Unavailable, ok.SetEntranceOpen(true).Status);
        // State is still readable after a failed apply.
        Assert.Equal(AuthoredSystemReconstructionStatus.Reconstructed, ok.State.Status);
    }

    [Fact]
    public void DisposedAndWrongProviderArgumentAreRejectedSafely()
    {
        using var harness = new Harness();
        Register(harness);
        harness.BeginGameplay();
        harness.Provider.Dispose(); harness.Provider.Dispose();   // idempotent
        Assert.Equal(WorldStatus.UnknownProvider, harness.Provider.RegisterAuthoredSystem(new AuthoredSystemDefinition("x", 1, "Pocket")));
        Assert.Null(harness.Provider.CreateAuthoredSystem("sysA", "k1", "anchor"));
    }

    [Fact]
    public void WrongThreadAccessIsRejected()
    {
        using var harness = new Harness();
        Register(harness);
        harness.BeginGameplay();
        Exception? caught = null;
        var thread = new System.Threading.Thread(() =>
        {
            try { _ = harness.Provider.GetAuthoredSystems("sysA"); }
            catch (Exception error) { caught = error; }
        });
        thread.Start(); thread.Join();
        Assert.IsType<InvalidOperationException>(caught);
    }

    [Fact]
    public void EmptyAuthoredCapturePreservesCombatOwnerAndOmitsAuthoredOwner()
    {
        // Driving the full recorder with and without an authored capture proves the authored owner is a
        // distinct envelope key: combat + definitions owner bytes are byte-identical, and the authored key
        // only exists when an authored capture is wired (no-authored saves are byte-identical to combat-only).
        var json = new WorldJsonInspection(typeof(JsonObject).Assembly);
        var instances = Array.Empty<WorldSnapshotInstance>();

        var plain = new WorldSnapshotRecorder(json);
        var plainRoot = EmptySnapshotRoot();
        var token = plain.Begin(1, instances);
        Assert.True(plain.Complete(token, 1, instances, plainRoot));
        var plainStore = plain.ForStore(plainRoot);

        var authored = new WorldSnapshotRecorder(json, () => AuthoredSystemStateCodec.Encode(Array.Empty<AuthoredSystemOccurrence>()));
        var authoredRoot = EmptySnapshotRoot();
        var token2 = authored.Begin(1, instances);
        Assert.True(authored.Complete(token2, 1, instances, authoredRoot));
        var authoredStore = authored.ForStore(authoredRoot);

        Assert.True(plainStore[WorldStateCodec.Owner].SequenceEqual(authoredStore[WorldStateCodec.Owner]));
        Assert.True(plainStore[WorldDefinitionCodec.Owner].SequenceEqual(authoredStore[WorldDefinitionCodec.Owner]));
        Assert.False(plainStore.ContainsKey(AuthoredSystemStateCodec.Owner));
        Assert.True(authoredStore.ContainsKey(AuthoredSystemStateCodec.Owner));
    }
    private static JsonObject EmptySnapshotRoot() => new()
    {
        ["Player"] = new(new JsonObject { ["map"] = new(new JsonObject { ["sectors"] = new(new List<JsonValue>()) }) }),
        ["Version"] = new("0.8.2.3")
    };

    [Fact]
    public void PreviousRevisionRowsMigrateUpOnReconcileAndMismatchWithoutPreviousFails()
    {
        // Migration success: rev2 declared with previous rev1; a retained row stamped rev1 reconstructs
        // after migrating up, and the migrated revision is what the next capture persists.
        using (var migrate = new Harness())
        {
            Assert.Equal(WorldStatus.Succeeded, migrate.Provider.RegisterAuthoredSystem(
                new AuthoredSystemDefinition("sysA", 2, "New"), new AuthoredSystemDefinition("sysA", 1, "Old")));
            migrate.BeginGameplay();
            migrate.Coordinator.RestoreRows(migrate.Session, new[]
            {
                new AuthoredSystemOccurrence("author.a", "sysA", "k1", 1, "sys-1", "en-1", "pk-1", false)
            });
            migrate.Native.Systems["sys-1"] = ("en-1", "pk-1");   // owned pocket is present natively
            var occurrence = migrate.Provider.GetAuthoredSystem("sysA", "k1");
            Assert.NotNull(occurrence);
            Assert.Equal(AuthoredSystemReconstructionStatus.Reconstructed, occurrence!.State.Status);
            byte[] encoded = migrate.Coordinator.CaptureBytes();
            Assert.Equal(2, AuthoredSystemStateCodec.Decode(encoded).Single(o => o.OccurrenceKey == "k1").Revision);
        }
        // Mismatch with no previous declared: a retained older revision is not silently adopted.
        using (var mismatch = new Harness())
        {
            Assert.Equal(WorldStatus.Succeeded, mismatch.Provider.RegisterAuthoredSystem(new AuthoredSystemDefinition("sysA", 2, "New")));
            mismatch.BeginGameplay();
            mismatch.Coordinator.RestoreRows(mismatch.Session, new[]
            {
                new AuthoredSystemOccurrence("author.a", "sysA", "k1", 1, "sys-1", "en-1", "pk-1", false)
            });
            var occurrence = mismatch.Provider.GetAuthoredSystem("sysA", "k1");
            Assert.Equal(AuthoredSystemFailureReason.RevisionMismatch, occurrence!.State.Reason);
        }
    }

    [Fact]
    public void FailedGateApplyDoesNotCommitDeclaredOpen()
    {
        using var harness = new Harness();
        Register(harness);
        harness.BeginGameplay();
        var created = Create(harness, "k1");
        Assert.Equal(AuthoredSystemReconstructionStatus.Reconstructed, created.State.Status);
        harness.Native.ThrowOnApply = true;
        Assert.Equal(AuthoredActionStatus.Unavailable, created.SetEntranceOpen(true).Status);
        harness.Native.ThrowOnApply = false;
        // DeclaredOpen was not committed on failure, so reconciliation must not force the gate open.
        harness.Coordinator.Reconcile(harness.Session);
        Assert.False(harness.Native.IsOpen(harness.Session, "en-1", "pk-1"));
    }

    [Fact]
    public void CreateFailsAtTheOwnedEnvelopeBoundInsteadOfAtSaveTime()
    {
        using var harness = new Harness();
        Register(harness);
        harness.BeginGameplay();
        for (int i = 1; i <= 1024; i++)
        {
            var r = harness.Provider.CreateAuthoredSystem("sysA", "k" + i, "anchor");
            Assert.NotNull(r);
        }
        var overflow = harness.Provider.CreateAuthoredSystem("sysA", "k-over", "anchor");
        Assert.Null(overflow);
    }
}
