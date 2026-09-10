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
        internal void BeginSaveLoad()
        {
            Session = Hub.Begin(SessionOrigin.SaveLoad, "save");
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

    [Fact]
    public void CreateRequiresGameplayInitializedAndReconcilesTheSameKeyWithoutDuplicating()
    {
        using var harness = new Harness();
        Register(harness);
        // Before the gameplay boundary creation is refused.
        var pre = harness.Provider.CreateAuthoredSystem(Guid.NewGuid(), "sysA", "k1", "anchor");
        Assert.Equal(WorldStatus.NotReady, pre.Status);
        harness.BeginGameplay();
        var first = harness.Provider.CreateAuthoredSystem(harness.Session, "sysA", "k1", "anchor");
        Assert.True(first.Succeeded);
        Assert.Equal("sys-1", first.SystemId);
        Assert.Equal("en-1", first.EntranceGatePoiId);
        Assert.Equal("pk-1", first.PocketGatePoiId);
        Assert.Equal(1, harness.Native.NextId);
        // Re-declaring the same key reconciles to the owned occurrence; no duplicate native system.
        var second = harness.Provider.CreateAuthoredSystem(harness.Session, "sysA", "k1", "anchor");
        Assert.True(second.Succeeded);
        Assert.Equal("sys-1", second.SystemId);
        Assert.Equal(1, harness.Native.NextId);
        // A different key is a distinct owned occurrence.
        var other = harness.Provider.CreateAuthoredSystem(harness.Session, "sysA", "k2", "anchor");
        Assert.True(other.Succeeded);
        Assert.Equal("sys-2", other.SystemId);
        Assert.Equal(2, harness.Native.NextId);
    }

    [Fact]
    public void ForeignOrAmbiguousNativeIdentityIsNeverAdopted()
    {
        using var harness = new Harness();
        Register(harness);
        harness.BeginGameplay();
        var first = harness.Provider.CreateAuthoredSystem(harness.Session, "sysA", "k1", "anchor");
        Assert.True(first.Succeeded);
        // Tamper with the native gate identity so the owned occurrence no longer matches: rejected, not adopted.
        harness.Native.Systems["sys-1"] = ("en-999", "pk-999");
        var state = harness.Provider.GetAuthoredSystemReconstructionState(new AuthoredSystemReference("author.a", "sysA", "k1"));
        Assert.Equal(AuthoredSystemReconstructionStatus.Failed, state.Status);
        Assert.Equal(AuthoredSystemFailureReason.AmbiguousIdentity, state.Reason);
        var recreated = harness.Provider.CreateAuthoredSystem(harness.Session, "sysA", "k1", "anchor");
        Assert.Equal(WorldStatus.Rejected, recreated.Status);
        // Re-used different key still gets a brand-new owned system.
        var other = harness.Provider.CreateAuthoredSystem(harness.Session, "sysA", "kcopy", "anchor");
        Assert.True(other.Succeeded);
        Assert.Equal("sys-2", other.SystemId);
    }

    [Fact]
    public void EntranceOpenIsDeclarativeAndConvergesBothPeersWithHiddenRepair()
    {
        using var harness = new Harness();
        Register(harness);
        harness.BeginGameplay();
        var created = harness.Provider.CreateAuthoredSystem(harness.Session, "sysA", "k1", "anchor");
        Assert.True(created.Succeeded);
        Assert.False(harness.Native.IsOpen(harness.Session, "en-1", "pk-1"));
        var reference = new AuthoredSystemReference("author.a", "sysA", "k1");
        Assert.Equal(WorldStatus.Succeeded, harness.Provider.SetAuthoredSystemEntranceOpen(harness.Session, reference, true));
        Assert.True(harness.Native.IsOpen(harness.Session, "en-1", "pk-1"));
        // Declared state persists across reconcile: a native revert is repaired and re-opened together.
        harness.Native.Open["sys-1"] = false;   // simulate an out-of-band hidden/closed revert
        Assert.False(harness.Native.IsOpen(harness.Session, "en-1", "pk-1"));
        harness.Coordinator.Reconcile(harness.Session);
        Assert.True(harness.Native.IsOpen(harness.Session, "en-1", "pk-1"));
        // Declared close is honoured too.
        Assert.Equal(WorldStatus.Succeeded, harness.Provider.SetAuthoredSystemEntranceOpen(harness.Session, reference, false));
        Assert.False(harness.Native.IsOpen(harness.Session, "en-1", "pk-1"));
    }

    [Fact]
    public void NoFreePositionIsATypedFailureAndSettlesAsNativeMissing()
    {
        using var harness = new Harness();
        Register(harness);
        harness.BeginGameplay();
        harness.Native.FailCreate = true;
        var rejected = harness.Provider.CreateAuthoredSystem(harness.Session, "sysA", "k1", "anchor");
        Assert.Equal(WorldStatus.Rejected, rejected.Status);
        var state = harness.Provider.GetAuthoredSystemReconstructionState(new AuthoredSystemReference("author.a", "sysA", "k1"));
        Assert.Equal(AuthoredSystemReconstructionStatus.Pending, state.Status);
        AuthoredSystemFailuresCapture? captured = null;
        harness.Provider.AuthoredSystemReconstructionSettled += e => captured = new AuthoredSystemFailuresCapture(e);
        harness.Coordinator.Reconcile(harness.Session);
        Assert.NotNull(captured);
        Assert.Single(captured!.Event.Failures);
        Assert.Equal(AuthoredSystemFailureReason.NativeMissing, captured.Event.Failures[0].Reason);
    }
    private sealed class AuthoredSystemFailuresCapture
    {
        internal ReconstructionSettledEvent Event;
        internal AuthoredSystemFailuresCapture(ReconstructionSettledEvent e) => Event = e;
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
            var state = missing.Provider.GetAuthoredSystemReconstructionState(new AuthoredSystemReference("author.a", "ghost", "k"));
            Assert.Equal(AuthoredSystemFailureReason.MissingDefinition, state.Reason);
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
            var state = mismatch.Provider.GetAuthoredSystemReconstructionState(new AuthoredSystemReference("author.a", "sysA", "k"));
            Assert.Equal(AuthoredSystemFailureReason.RevisionMismatch, state.Reason);
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
            var state = native.Provider.GetAuthoredSystemReconstructionState(new AuthoredSystemReference("author.a", "sysA", "k"));
            Assert.Equal(AuthoredSystemReconstructionStatus.Pending, state.Status);
            AuthoredSystemFailuresCapture? captured = null;
            native.Provider.AuthoredSystemReconstructionSettled += e => captured = new AuthoredSystemFailuresCapture(e);
            native.Coordinator.Reconcile(native.Session);
            Assert.NotNull(captured);
            Assert.Equal(AuthoredSystemFailureReason.NativeMissing, captured!.Event.Failures.Single().Reason);
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
            var state = ambiguous.Provider.GetAuthoredSystemReconstructionState(new AuthoredSystemReference("author.a", "sysA", "k"));
            Assert.Equal(AuthoredSystemFailureReason.AmbiguousIdentity, state.Reason);
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
            var state = persisted.Provider.GetAuthoredSystemReconstructionState(new AuthoredSystemReference("author.a", "sysA", "k"));
            Assert.Equal(AuthoredSystemFailureReason.PersistenceUnavailable, state.Reason);
            // PersistenceUnavailable is genuinely reportable, not query-only: it reaches the settled event.
            AuthoredSystemFailuresCapture? capturedPersisted = null;
            persisted.Provider.AuthoredSystemReconstructionSettled += e => capturedPersisted = new AuthoredSystemFailuresCapture(e);
            persisted.Coordinator.Reconcile(persisted.Session);
            Assert.NotNull(capturedPersisted);
            Assert.Equal(AuthoredSystemFailureReason.PersistenceUnavailable, capturedPersisted!.Event.Failures.Single().Reason);
        }
    }

    [Fact]
    public void ReconstructionSettledReportsReconstructedOutcomesOnce()
    {
        using var harness = new Harness();
        Register(harness);
        harness.BeginGameplay();
        var created = harness.Provider.CreateAuthoredSystem(harness.Session, "sysA", "k1", "anchor");
        Assert.True(created.Succeeded);
        int events = 0;
        harness.Provider.AuthoredSystemReconstructionSettled += e => events++;
        harness.Coordinator.Reconcile(harness.Session);
        harness.Coordinator.Reconcile(harness.Session);   // second pass does not re-emit
        Assert.Equal(1, events);
    }

    [Fact]
    public void CrossSaveGenerationIsolationDoesNotBleedOldNativeIdentity()
    {
        using var harness = new Harness();
        Register(harness);
        harness.BeginGameplay();
        var first = harness.Provider.CreateAuthoredSystem(harness.Session, "sysA", "k1", "anchor");
        Assert.True(first.Succeeded);
        Assert.Equal("sys-1", first.SystemId);
        // A brand-new game: the session starts empty (no persisted occurrences) and creates afresh.
        harness.BeginGameplay();
        var fresh = harness.Provider.CreateAuthoredSystem(harness.Session, "sysA", "k1", "anchor");
        Assert.True(fresh.Succeeded);
        Assert.Equal("sys-2", fresh.SystemId);
        Assert.Equal(2, harness.Native.NextId);
    }

    [Fact]
    public void NativeFaultsFailOpenWithoutBreakingTheFacade()
    {
        using var harness = new Harness();
        Register(harness);
        harness.BeginGameplay();
        harness.Native.ThrowOnCreate = true;
        var created = harness.Provider.CreateAuthoredSystem(harness.Session, "sysA", "k1", "anchor");
        Assert.Equal(WorldStatus.Unavailable, created.Status);
        harness.Native.ThrowOnCreate = false;
        var ok = harness.Provider.CreateAuthoredSystem(harness.Session, "sysA", "k1", "anchor");
        Assert.True(ok.Succeeded);
        harness.Native.ThrowOnApply = true;
        Assert.Equal(WorldStatus.Unavailable, harness.Provider.SetAuthoredSystemEntranceOpen(harness.Session,
            new AuthoredSystemReference("author.a", "sysA", "k1"), true));
        // Query still works after a failed apply.
        Assert.Equal(AuthoredSystemReconstructionStatus.Reconstructed,
            harness.Provider.GetAuthoredSystemReconstructionState(new AuthoredSystemReference("author.a", "sysA", "k1")).Status);
    }

    [Fact]
    public void DisposedAndWrongProviderArgumentAreRejectedSafely()
    {
        using var harness = new Harness();
        Register(harness);
        harness.BeginGameplay();
        harness.Provider.Dispose(); harness.Provider.Dispose();   // idempotent
        Assert.Equal(WorldStatus.UnknownProvider, harness.Provider.RegisterAuthoredSystem(new AuthoredSystemDefinition("x", 1, "Pocket")));
        Assert.Equal(WorldStatus.UnknownProvider, harness.Provider.CreateAuthoredSystem(harness.Session, "sysA", "k1", "anchor").Status);
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
            try { _ = harness.Provider.GetAuthoredSystemReconstructionState(new AuthoredSystemReference("author.a", "sysA", "k1")); }
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
            var state = migrate.Provider.GetAuthoredSystemReconstructionState(new AuthoredSystemReference("author.a", "sysA", "k1"));
            Assert.Equal(AuthoredSystemReconstructionStatus.Reconstructed, state.Status);
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
            var state = mismatch.Provider.GetAuthoredSystemReconstructionState(new AuthoredSystemReference("author.a", "sysA", "k1"));
            Assert.Equal(AuthoredSystemFailureReason.RevisionMismatch, state.Reason);
        }
    }

    [Fact]
    public void FailedGateApplyDoesNotCommitDeclaredOpen()
    {
        using var harness = new Harness();
        Register(harness);
        harness.BeginGameplay();
        var created = harness.Provider.CreateAuthoredSystem(harness.Session, "sysA", "k1", "anchor");
        Assert.True(created.Succeeded);
        harness.Native.ThrowOnApply = true;
        var reference = new AuthoredSystemReference("author.a", "sysA", "k1");
        Assert.Equal(WorldStatus.Unavailable, harness.Provider.SetAuthoredSystemEntranceOpen(harness.Session, reference, true));
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
            var r = harness.Provider.CreateAuthoredSystem(harness.Session, "sysA", "k" + i, "anchor");
            Assert.True(r.Succeeded);
        }
        var overflow = harness.Provider.CreateAuthoredSystem(harness.Session, "sysA", "k-over", "anchor");
        Assert.Equal(WorldStatus.Rejected, overflow.Status);
    }
}
