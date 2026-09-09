using System;
using System.IO;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;
public sealed class WorldProfileHookTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ProfileMutationRefusesOwnedWorkWithoutPoisoningReplacement(bool replace, bool changed)
    {
        var hub = new LifecycleHub((_, error) => throw error); var guard = new WorldLifetimeGuard(); int refused = 0; bool contentChanged = false;
        using var host = new WorldLifetimeHookHost(typeof(Source.Galaxy.MapElement).Assembly, hub, guard,
            generationFailure: _ => refused++, stateProfile: _ => { if (contentChanged) throw new InvalidDataException("Profile changed."); });
        var session = hub.Begin(SessionOrigin.NewGame, null);
        var identity = new WorldObjectIdentity(new ContentDeclaration("author.a", "PoiX", PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent), Guid.NewGuid());
        var poi = new Source.Galaxy.MapPointOfInterest { guid = identity.NativeId };
        guard.Track(session, poi, identity); guard.Ready(session);
        host.RequireContentMutation(new Source.Galaxy.MapPointOfInterest { guid = "vanilla" });
        Assert.True(host.AllowUse(poi));
        if (replace) { session = hub.Begin(SessionOrigin.NewGame, null); guard.Ready(session); }
        contentChanged = changed;
        if (changed) Assert.Throws<InvalidDataException>(() => host.AllowUse(poi));
        else Assert.Throws<InvalidDataException>(() => host.RequireContentMutation(poi));
        Assert.Equal(replace ? 0 : 1, refused);
        Assert.False(host.AllowUse(poi));
        Assert.True(host.AllowUse(new Source.Galaxy.MapPointOfInterest { guid = "vanilla" }));
    }
}
