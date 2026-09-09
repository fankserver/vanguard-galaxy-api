using System;
using System.Collections;
using System.IO;
using System.Reflection;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldNativeTravelCaptureTests
{
    [Fact]
    public async System.Threading.Tasks.Task NativeRequestFirstLegAndSynchronousWaypointHandoffKeepSuccessorState()
    {
        var oldPlayer = Source.Player.GamePlayer.current;
        var singleton = typeof(Behaviour.Util.Singleton<Behaviour.Managers.TravelManager>).GetField("instance", BindingFlags.NonPublic | BindingFlags.Static)!;
        var oldTravel = singleton.GetValue(null);
        var loaderField = typeof(Behaviour.Util.PersistentSingleton<Behaviour.Bootstrap.SceneLoader>).GetField("instance", BindingFlags.NonPublic | BindingFlags.Static)!;
        var oldLoader = loaderField.GetValue(null);
        var loader = new Behaviour.Bootstrap.SceneLoader(); loaderField.SetValue(null, loader);
        try
        {
            var hub = new LifecycleHub((_, error) => throw error);
            var host = new WorldLifetimeHookHost(typeof(Source.Galaxy.MapElement).Assembly, hub);
            var manager = new Behaviour.Managers.TravelManager(); singleton.SetValue(null, manager);
            var player = new Source.Player.GamePlayer(); Source.Player.GamePlayer.current = player;
            hub.Begin(SessionOrigin.NewGame, null);
            var first = new Source.Galaxy.MapPointOfInterest { guid = "first" };
            var second = new Source.Galaxy.MapPointOfInterest { guid = "second" };
            player.waypoints.Add(first); player.waypoints.Add(second);
            var pendingUnload = new System.Threading.Tasks.TaskCompletionSource<bool>();
            System.Threading.Tasks.Task<bool>? unloadResult = null;
            int obsoleteTailWrites = 0, prepSteps = 0; IEnumerator? successor = null, background = null;
            IEnumerator Preparation()
            {
                prepSteps++; yield return null; prepSteps++;
            }
            IEnumerator Second()
            {
                manager.localTarget = second; yield return null;
            }
            IEnumerator Arrive()
            {
                player.currentPointOfInterest = first; player.waypoints.RemoveAt(0);
                var handoff = host.BeginWaypoint(manager); Assert.NotNull(handoff);
                successor = host.WrapLeg(manager, second, Second()); Assert.True(successor.MoveNext());
                host.EndWaypoint(handoff!); yield break;
            }
            IEnumerator First()
            {
                manager.localTarget = first;
                host.RequireSceneTransition(manager);
                manager.loadingNextScene = true;
                Assert.True(host.CaptureSceneUnload(manager, "Combat")!.GetAwaiter().GetResult());
                Assert.False(manager.loadingNextScene);
                loader.Unload = scene => { Assert.Equal("Combat", scene); return pendingUnload.Task; };
                manager.loadingNextScene = true;
                var context = System.Threading.SynchronizationContext.Current;
                try { System.Threading.SynchronizationContext.SetSynchronizationContext(null); unloadResult = host.CaptureSceneUnload(manager, "Combat"); }
                finally { System.Threading.SynchronizationContext.SetSynchronizationContext(context); }
                manager.localTarget = second;
                Assert.Throws<InvalidDataException>(() => host.RequireSceneTransition(manager));
                manager.localTarget = first;
                background = host.WrapChild(manager, Preparation()); Assert.True(background.MoveNext());
                yield return Arrive();
                obsoleteTailWrites++;
            }
            var request = host.BeginRoute(manager, second); Assert.NotNull(request);
            var predecessorCleanup = host.BeginCancellation(manager);
            Assert.Throws<InvalidDataException>(() => host.BeginRoute(manager, second));
            host.EndCancellation(predecessorCleanup, true);
            var root = host.WrapLeg(manager, first, First());
            Assert.True(root.MoveNext()); host.CompleteRoute(request!, true);
            var refusedCancellation = host.BeginCancellation(manager); host.EndCancellation(refusedCancellation, false);
            Assert.Throws<InvalidDataException>(() => host.RequireSceneTransition(manager));
            var child = Assert.IsType<WorldTravelLegEnumerator>(root.Current);
            Assert.False(child.MoveNext()); Assert.False(root.MoveNext());
            Assert.Equal(0, obsoleteTailWrites); Assert.Same(second, manager.localTarget);
            Assert.Throws<InvalidDataException>(() => background!.MoveNext()); Assert.Equal(1, prepSteps);
            ((IDisposable)background!).Dispose();
            Assert.NotNull(successor); Assert.False(successor!.MoveNext());
            ((IDisposable)successor).Dispose(); child.Dispose(); ((IDisposable)root).Dispose();
            var cancellation = host.BeginCancellation(manager); host.EndCancellation(cancellation, true);
            Assert.Null(host.Travel.CurrentLeg);
            var owned = new Source.Galaxy.MapPointOfInterest { guid = WorldObjectIdentity.ReservedPrefix + "unknown" };
            Assert.Throws<InvalidDataException>(() => host.WrapLeg(manager, owned, Second()));
            host.Dispose();
            pendingUnload.SetResult(true);
            Assert.NotNull(unloadResult); Assert.False(await unloadResult!);
            Assert.True(manager.loadingNextScene);
        }
        finally { Source.Player.GamePlayer.current = oldPlayer; singleton.SetValue(null, oldTravel); loaderField.SetValue(null, oldLoader); }
    }
}
