using System;
using System.Reflection;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldActorOriginTests
{
    [Fact]
    public void ActorsRetainSpawnManagerAndNestedVanillaSpawnDoesNotInheritOwnership()
    {
        var oldPlayer = Source.Player.GamePlayer.current;
        var singleton = typeof(Behaviour.Util.Singleton<Behaviour.Managers.TravelManager>).GetField("instance", BindingFlags.NonPublic | BindingFlags.Static)!;
        var oldTravel = singleton.GetValue(null);
        try
        {
            var hub = new LifecycleHub((_, error) => throw error); var guard = new WorldLifetimeGuard();
            using var host = new WorldLifetimeHookHost(typeof(Source.Galaxy.MapElement).Assembly, hub, guard);
            var session = hub.Begin(SessionOrigin.NewGame, null);
            var identity = new WorldObjectIdentity(new ContentDeclaration("author.a", "PoiX", PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent), Guid.NewGuid());
            var poi = new Source.Galaxy.MapPointOfInterest { guid = identity.NativeId };
            var manager = new Behaviour.Managers.TestPoiManager { poi = poi };
            var travel = new Behaviour.Managers.TravelManager { localPoiManager = manager, localTarget = poi };
            singleton.SetValue(null, travel); Source.Player.GamePlayer.current = new Source.Player.GamePlayer { currentPointOfInterest = poi };
            guard.Track(session, poi, identity); guard.Ready(session);
            var actor = new UnityEngine.Object(); var vanilla = new UnityEngine.Object();
            using (host.BeginSpawn(manager))
            {
                host.CaptureActor(actor);
                using (host.BeginSpawn(new Behaviour.Managers.TestPoiManager { poi = new Source.Galaxy.MapPointOfInterest { guid = "vanilla" } }))
                    host.CaptureActor(vanilla);
            }
            Assert.True(host.AllowActor(actor));
            manager.poi = new Source.Galaxy.MapPointOfInterest { guid = "rebound" };
            Assert.False(host.AllowActor(actor)); Assert.True(host.AllowActor(vanilla));
            manager.poi = poi;
            typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(actor, IntPtr.Zero);
            Assert.False(host.AllowActor(actor));
            host.Dispose(); Assert.False(host.AllowActor(actor)); Assert.True(host.AllowActor(vanilla));
        }
        finally { Source.Player.GamePlayer.current = oldPlayer; singleton.SetValue(null, oldTravel); }
    }
}
