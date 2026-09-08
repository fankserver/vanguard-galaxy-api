using System;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using VGModAPI.Patches;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldLifetimeHookTests : IDisposable
{
    public void Dispose() => WorldLifetimePatches.Host = null;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AwakeRefusalPersistsEvenWhileManagerPoiIsNull(bool localTarget)
    {
        var oldPlayer = Source.Player.GamePlayer.current;
        var singleton = typeof(Behaviour.Util.Singleton<Behaviour.Managers.TravelManager>).GetField("instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var oldTravel = singleton.GetValue(null);
        try
        {
            var hub = new LifecycleHub((_, error) => throw error);
            using var host = new WorldLifetimeHookHost(typeof(Source.Galaxy.MapElement).Assembly, hub);
            hub.Begin(SessionOrigin.NewGame, null);
            var owned = new Source.Galaxy.MapPointOfInterest { guid = WorldObjectIdentity.ReservedPrefix + "unknown" };
            var vanilla = new Source.Galaxy.MapPointOfInterest { guid = "vanilla" };
            Source.Player.GamePlayer.current = new Source.Player.GamePlayer { currentPointOfInterest = localTarget ? vanilla : owned };
            singleton.SetValue(null, new Behaviour.Managers.TravelManager { localTarget = localTarget ? owned : vanilla });
            var manager = new Behaviour.Managers.TestPoiManager();
            Assert.False(host.AllowManagerAwake(manager));
            Assert.Null(manager.poi); Assert.False(host.AllowManager(manager));
            Source.Player.GamePlayer.current.currentPointOfInterest = vanilla;
            singleton.SetValue(null, null);
            Assert.False(host.AllowManagerAwake(manager));
            Assert.False(host.CaptureManager(manager)());
            WorldLifetimePatches.Host = host;
            Assert.False(WorldLifetimePatches.Arrival.Prefix(manager));
            Assert.Throws<System.IO.InvalidDataException>(() => WorldLifetimePatches.Spawn.Prefix(manager));
            Assert.True(host.AllowManagerAwake(new Behaviour.Managers.TestPoiManager()));
        }
        finally { Source.Player.GamePlayer.current = oldPlayer; singleton.SetValue(null, oldTravel); }
    }

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
        Assert.False(WorldLifetimePatches.Active.Prefix(owned));
        bool result = true;
        Assert.False(WorldLifetimePatches.Route.Prefix(owned, ref result)); Assert.False(result);
        result = true;
        Assert.False(WorldLifetimePatches.CanTravel.Prefix(owned, ref result)); Assert.False(result);
        Assert.True(WorldLifetimePatches.Route.Prefix(vanilla, ref result));
        Assert.True(WorldLifetimePatches.Active.Prefix(vanilla));
        var manager = new Behaviour.Managers.TestPoiManager { poi = owned };
        Assert.False(WorldLifetimePatches.Arrival.Prefix(manager));
        Assert.Throws<System.IO.InvalidDataException>(() => WorldLifetimePatches.Spawn.Prefix(manager));
        manager.poi = vanilla;
        Assert.True(WorldLifetimePatches.Arrival.Prefix(manager)); WorldLifetimePatches.Spawn.Prefix(manager);
        owned.guid = "stripped";
        host.Dispose();
        Assert.False(WorldLifetimePatches.Ambient.Prefix(owned));
        Assert.False(WorldLifetimePatches.Remove.Prefix(owned));
        Assert.True(WorldLifetimePatches.Ambient.Prefix(vanilla));
        Assert.Equal(0, owned.NameReads);
    }
}
