using System;
using System.IO;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldLifetimeGuardTests
{
    private static WorldObjectIdentity Identity() => new(new ContentDeclaration("author.a", "PoiX", PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent), Guid.NewGuid());

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
