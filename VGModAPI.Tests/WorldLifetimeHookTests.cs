using System;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using VGModAPI.Patches;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldLifetimeHookTests : IDisposable
{
    public void Dispose() => WorldLifetimePatches.Host = null;

    [Fact]
    public void ConcreteHostRefusesOwnedBodiesBeforeAndAfterTeardown()
    {
        var hub = new LifecycleHub((_, error) => throw new Exception("Unexpected fault", error));
        using var host = new WorldLifetimeHookHost(typeof(Source.Galaxy.MapElement).Assembly, hub);
        WorldLifetimePatches.Host = host;
        hub.Begin(SessionOrigin.NewGame, null);
        var owned = new Source.Galaxy.MapPointOfInterest { guid = WorldObjectIdentity.ReservedPrefix + "unknown" };
        var vanilla = new Source.Galaxy.MapPointOfInterest { guid = "vanilla" };
        Assert.False(WorldLifetimePatches.Ambient.Prefix(owned));
        Assert.False(WorldLifetimePatches.Remove.Prefix(owned));
        Assert.True(WorldLifetimePatches.Ambient.Prefix(vanilla));
        Assert.True(WorldLifetimePatches.Remove.Prefix(vanilla));
        owned.guid = "stripped";
        host.Dispose();
        Assert.False(WorldLifetimePatches.Ambient.Prefix(owned));
        Assert.False(WorldLifetimePatches.Remove.Prefix(owned));
        Assert.True(WorldLifetimePatches.Ambient.Prefix(vanilla));
        Assert.Equal(0, owned.NameReads);
    }
}
