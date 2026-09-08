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
    public void NativeRequestFirstLegAndSynchronousWaypointHandoffKeepSuccessorState()
    {
        var oldPlayer = Source.Player.GamePlayer.current;
        var singleton = typeof(Behaviour.Util.Singleton<Behaviour.Managers.TravelManager>).GetField("instance", BindingFlags.NonPublic | BindingFlags.Static)!;
        var oldTravel = singleton.GetValue(null);
        try
        {
            var hub = new LifecycleHub((_, error) => throw error);
            using var host = new WorldLifetimeHookHost(typeof(Source.Galaxy.MapElement).Assembly, hub);
            var manager = new Behaviour.Managers.TravelManager(); singleton.SetValue(null, manager);
            var player = new Source.Player.GamePlayer(); Source.Player.GamePlayer.current = player;
            hub.Begin(SessionOrigin.NewGame, null);
            var first = new Source.Galaxy.MapPointOfInterest { guid = "first" };
            var second = new Source.Galaxy.MapPointOfInterest { guid = "second" };
            player.waypoints.Add(first); player.waypoints.Add(second);
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
                background = host.WrapChild(manager, Preparation()); Assert.True(background.MoveNext());
                yield return Arrive();
                obsoleteTailWrites++;
            }
            var request = host.BeginRoute(manager, second); Assert.NotNull(request);
            var root = host.WrapLeg(manager, first, First());
            Assert.True(root.MoveNext()); host.CompleteRoute(request!, true);
            var child = Assert.IsType<WorldTravelLegEnumerator>(root.Current);
            Assert.False(child.MoveNext()); Assert.False(root.MoveNext());
            Assert.Equal(0, obsoleteTailWrites); Assert.Same(second, manager.localTarget);
            Assert.Throws<InvalidDataException>(() => background!.MoveNext()); Assert.Equal(1, prepSteps);
            ((IDisposable)background!).Dispose();
            Assert.NotNull(successor); Assert.False(successor!.MoveNext());
            ((IDisposable)successor).Dispose(); child.Dispose(); ((IDisposable)root).Dispose();
            var owned = new Source.Galaxy.MapPointOfInterest { guid = WorldObjectIdentity.ReservedPrefix + "unknown" };
            Assert.Throws<InvalidDataException>(() => host.WrapLeg(manager, owned, Second()));
        }
        finally { Source.Player.GamePlayer.current = oldPlayer; singleton.SetValue(null, oldTravel); }
    }
}
