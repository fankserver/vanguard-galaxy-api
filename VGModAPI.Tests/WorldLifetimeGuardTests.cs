using System;
using System.IO;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldLifetimeGuardTests
{
    private static WorldObjectIdentity Identity() => new(new ContentDeclaration("author.a", "PoiX", PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent), Guid.NewGuid());

    [Fact]
    public void PreparedTrackingDoesNotAdmitBeforeCommitAndCannotOverwriteReplacementInventory()
    {
        var guard = new WorldLifetimeGuard(); var session = Guid.NewGuid(); guard.Start(session); guard.Ready(session);
        var identity = Identity(); var poi = new object();
        var prepared = guard.PrepareTracking(session, new[] { (poi, identity) });
        Assert.False(guard.AllowAmbient(session, poi, identity.NativeId));
        Assert.False(guard.AllowNativeRemoval(poi, "stripped"));
        prepared.Commit(); Assert.True(guard.AllowAmbient(session, poi, identity.NativeId));
        Assert.Throws<InvalidDataException>(() => prepared.Commit());
        var discarded = new object(); var discardedId = Identity();
        var stale = guard.PrepareTracking(session, new[] { (discarded, discardedId) });
        var successor = new object(); var successorId = Identity(); guard.Track(session, successor, successorId);
        Assert.False(stale.Current); Assert.Throws<InvalidDataException>(() => stale.Commit());
        Assert.True(guard.AllowAmbient(session, successor, successorId.NativeId));
        Assert.False(guard.AllowAmbient(session, discarded, discardedId.NativeId));
    }

    [Fact]
    public void ProviderReadinessLossLatchesButStaleFailureCannotRevokeReplacementReadiness()
    {
        var guard = new WorldLifetimeGuard(); var session = Guid.NewGuid(); guard.Start(session);
        var identity = Identity(); var poi = new object(); guard.Track(session, poi, identity);
        bool available = true;
        guard.Ready(session, () => available);
        Assert.True(guard.AllowAmbient(session, poi, identity.NativeId));
        available = false; Assert.False(guard.AllowAmbient(session, poi, identity.NativeId));
        available = true; Assert.False(guard.AllowAmbient(session, poi, identity.NativeId));
        bool replace = true; Func<bool>? readiness = null;
        readiness = () =>
        {
            if (!replace) return true;
            replace = false; guard.Ready(session, readiness); return false;
        };
        guard.Ready(session, readiness);
        Assert.False(guard.AllowAmbient(session, poi, identity.NativeId));
        Assert.True(guard.AllowAmbient(session, poi, identity.NativeId));
    }

    [Fact]
    public void OwnedUpdatesWaitForScopedReadinessAndNativeCleanupCannotDeletePersistentSites()
    {
        var guard = new WorldLifetimeGuard(); var session = Guid.NewGuid(); guard.Start(session);
        var identity = Identity(); var poi = new object(); guard.Track(session, poi, identity);
        Assert.False(guard.AllowAmbient(session, poi, identity.NativeId));
        guard.Ready(session); Assert.True(guard.AllowAmbient(session, poi, identity.NativeId));
        Assert.False(guard.AllowNativeRemoval(poi, identity.NativeId));
        Assert.False(guard.AllowAmbient(session, poi, "stripped"));
        Assert.False(guard.AllowNativeRemoval(poi, "stripped"));
        Assert.True(guard.AllowAmbient(session, new object(), "vanilla"));
        Assert.True(guard.AllowNativeRemoval(new object(), "vanilla"));
    }

    [Fact]
    public void StaleObjectsAndUntrackedReservedObjectsRemainQuarantined()
    {
        var guard = new WorldLifetimeGuard(); var session = Guid.NewGuid(); guard.Start(session);
        var identity = Identity(); var poi = new object(); guard.Track(session, poi, identity); guard.Ready(session);
        var untracked = new object(); Assert.False(guard.AllowAmbient(session, untracked, identity.NativeId));
        Assert.False(guard.AllowAmbient(session, untracked, "stripped"));
        var next = Guid.NewGuid(); guard.Start(next); guard.Ready(next);
        Assert.False(guard.AllowAmbient(next, poi, identity.NativeId));
        Assert.Throws<InvalidDataException>(() => guard.Track(next, poi, identity));
        Assert.Throws<InvalidDataException>(() => guard.Ready(session));
        guard.Stop(); Assert.False(guard.AllowAmbient(next, poi, identity.NativeId));
        Assert.Throws<InvalidOperationException>(() => guard.Start(Guid.NewGuid()));
    }

    [Fact]
    public void DuplicateNativeIdentityDoesNotReplaceTheOriginalObject()
    {
        var guard = new WorldLifetimeGuard(); var session = Guid.NewGuid(); guard.Start(session);
        var identity = Identity(); var first = new object(); guard.Track(session, first, identity);
        var duplicate = new object();
        Assert.Throws<InvalidDataException>(() => guard.Track(session, duplicate, identity));
        guard.Ready(session); Assert.True(guard.AllowAmbient(session, first, identity.NativeId));
        Assert.False(guard.AllowAmbient(session, duplicate, identity.NativeId));
        Assert.False(guard.AllowNativeRemoval(duplicate, "stripped"));
    }
}
