using System;
using System.Reflection;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldActorOriginTests
{
    [Fact]
    public void FailedCaptureRemainsOwnedAndDeniedAfterScopeEnds()
    {
        var origins = new WorldActorOrigins(); var actor = new object(); bool ready = false;
        using (origins.Enter(() => ready))
            Assert.Throws<System.IO.InvalidDataException>(() => origins.Capture(actor));
        ready = true;
        Assert.True(origins.Known(actor)); Assert.False(origins.Allow(actor));
        var changedScopeActor = new object(); IDisposable? nested = null;
        using (origins.Enter(() => { nested = origins.Enter(null); return true; }))
            Assert.Throws<System.IO.InvalidDataException>(() => origins.Capture(changedScopeActor));
        nested!.Dispose();
        Assert.True(origins.Known(changedScopeActor)); Assert.False(origins.Allow(changedScopeActor));
    }

    [Fact]
    public void ActorsRetainSpawnManagerAndNestedVanillaSpawnDoesNotInheritOwnership()
    {
        var oldHost = VGModAPI.Patches.WorldLifetimePatches.Host;
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
            var actor = new UnityEngine.Object(); var vanilla = new UnityEngine.Object(); var destroyed = new UnityEngine.Object();
            using (host.BeginSpawn(manager))
            {
                host.CaptureActor(actor); host.CaptureActor(destroyed);
                using (host.BeginSpawn(new Behaviour.Managers.TestPoiManager { poi = new Source.Galaxy.MapPointOfInterest { guid = "vanilla" } }))
                    host.CaptureActor(vanilla);
            }
            VGModAPI.Patches.WorldLifetimePatches.Host = host;
            VGModAPI.Patches.WorldLifetimePatches.ActorMutation.Prefix(actor);
            Assert.True(host.AllowActor(actor)); Assert.True(host.AllowActor(destroyed));
            typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(destroyed, IntPtr.Zero);
            Assert.False(host.AllowActor(destroyed));
            manager.poi = new Source.Galaxy.MapPointOfInterest { guid = "rebound" };
            Assert.False(host.AllowActor(actor)); Assert.True(host.AllowActor(vanilla));
            Assert.Throws<System.IO.InvalidDataException>(() => VGModAPI.Patches.WorldLifetimePatches.ActorMutation.Prefix(actor));
            VGModAPI.Patches.WorldLifetimePatches.ActorMutation.Prefix(vanilla);
            manager.poi = poi;
            typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(actor, IntPtr.Zero);
            Assert.False(host.AllowActor(actor));
            host.Dispose(); Assert.False(host.AllowActor(actor)); Assert.True(host.AllowActor(vanilla));
        }
        finally { VGModAPI.Patches.WorldLifetimePatches.Host = oldHost; Source.Player.GamePlayer.current = oldPlayer; singleton.SetValue(null, oldTravel); }
    }
}
