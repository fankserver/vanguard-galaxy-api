using System;
using System.Collections;
using System.Collections.Generic;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class NavigationServiceTests
{
    [Fact]
    public void DirectedJumpCountsDistinguishMissingDisconnectedAndStaleSessions()
    {
        using var hub = new LifecycleHub((_, _) => { }); hub.SetCapability("navigation", true, "ready");
        var session = hub.Begin(SessionOrigin.NewGame, null); hub.PlayerReady(session);
        var map = new NavigationMap(new Dictionary<string, string[]> { ["a"] = new[] { "b" }, ["b"] = Array.Empty<string>(), ["c"] = Array.Empty<string>() }, Array.Empty<NavigationStation>(), () => true);
        var api = new NavigationService(hub, _ => map, (_, _, _) => NavigationStatus.Succeeded, (_, _) => null);
        var all = api.GetJumpCounts(session, "a");
        Assert.Equal(NavigationStatus.Succeeded, all.Status); Assert.Equal(2, all.Hops.Count);
        Assert.Equal(1, all.Hops["b"]); Assert.False(all.Hops.ContainsKey("c"));
        Assert.Equal(1, api.GetJumpCount(session, "a", "b").Hops);
        Assert.Equal(0, api.GetJumpCount(session, "a", "a").Hops);
        Assert.Equal(NavigationStatus.Disconnected, api.GetJumpCount(session, "b", "a").Status);
        Assert.Equal(NavigationStatus.Disconnected, api.GetJumpCount(session, "a", "c").Status);
        Assert.Equal(NavigationStatus.Missing, api.GetJumpCount(session, "a", "missing").Status);
        hub.Begin(SessionOrigin.NewGame, null);
        Assert.Equal(NavigationStatus.NotReady, api.GetJumpCount(session, "a", "b").Status);
        Assert.Equal(NavigationStatus.NotReady, api.FocusPoi(session, "station"));
    }
    [Fact]
    public void OwnedDestinationsUseProviderAndInstanceIdentityAndRecheckRestoration()
    {
        using var hub = new LifecycleHub((_, _) => { }); hub.SetCapability("navigation", true, "ready");
        var session = hub.Begin(SessionOrigin.NewGame, null); hub.PlayerReady(session);
        bool restored = true; var ids = new List<string>(); Func<bool>? ongoing = null;
        var api = new NavigationService(hub, _ => null, (_, id, current) => { ids.Add(id); ongoing = current; return NavigationStatus.Succeeded; }, (_, _) => restored);
        var instance = Guid.NewGuid();
        Assert.Equal(NavigationStatus.Succeeded, api.FocusCombatSite(session, new CombatSiteReference("owner.a", "port", instance)));
        Assert.Equal(NavigationStatus.Succeeded, api.FocusCombatSite(session, new CombatSiteReference("owner.b", "port", instance)));
        Assert.NotEqual(ids[0], ids[1]);
        Assert.Equal(NavigationStatus.Rejected, api.FocusPoi(session, ids[0]));
        restored = false; Assert.False(ongoing!());
        Assert.Equal(NavigationStatus.Missing, api.FocusCombatSite(session, new CombatSiteReference("owner.a", "port", instance)));
        Assert.Equal(2, ids.Count);
    }
    [Fact]
    public void StationsAreVisitedOnlyByDefaultAndDoNotExposeMutableCollections()
    {
        using var hub = new LifecycleHub((_, _) => { }); hub.SetCapability("navigation", true, "ready");
        var session = hub.Begin(SessionOrigin.NewGame, null); hub.PlayerReady(session);
        var source = new[] { new NavigationStation("visited", "a", "Port", true), new NavigationStation("new", "a", null, false) };
        var map = new NavigationMap(new Dictionary<string, string[]>(), source, () => true);
        var api = new NavigationService(hub, _ => map, (_, _, _) => NavigationStatus.Succeeded, (_, _) => null);
        Assert.Single(api.GetStations(session).Stations);
        var all = api.GetStations(session, false); Assert.Equal(2, all.Stations.Count);
        source[0] = source[1]; Assert.Equal("visited", all.Stations[0].Id);
    }
    [Fact]
    public void FocusStopsBeforeAdvancingStaleWorkAndDoesNotClearAnotherTarget()
    {
        object target = new(), other = new(); object? focused = target; bool current = true; int advances = 0;
        IEnumerator Native() { while (true) { advances++; yield return null; } }
        using var routine = new NavigationFocusRoutine(Native(), target, () => current, () => focused, () => focused = null, () => { });
        Assert.True(routine.MoveNext()); focused = other; current = false;
        Assert.False(routine.MoveNext()); Assert.Equal(1, advances); Assert.Same(other, focused);
    }
    [Fact]
    public void SuspendedWaitIsPolledThroughTheCurrentnessGuard()
    {
        object target = new(); object? focused = null; bool valid = true; int polls = 0;
        IEnumerator Wait() { while (true) { polls++; yield return null; } }
        IEnumerator Native() { focused = target; yield return Wait(); throw new Exception("must not resume"); }
        using var routine = new NavigationFocusRoutine(Native(), target, () => valid, () => focused, () => focused = null, () => { });
        Assert.True(routine.MoveNext()); Assert.Null(routine.Current);
        Assert.True(routine.MoveNext()); Assert.Equal(1, polls); Assert.Null(routine.Current);
        valid = false; Assert.False(routine.MoveNext()); Assert.Equal(1, polls); Assert.Null(focused);
    }
    [Fact]
    public void FirstAdvanceFailureClearsTheAssignedTarget()
    {
        object target = new(); object? focused = null;
        IEnumerator Native() { focused = target; if (focused != null) throw new InvalidOperationException("tab failed"); yield return null; }
        using var routine = new NavigationFocusRoutine(Native(), target, () => true, () => focused, () => focused = null, () => { });
        Assert.Throws<InvalidOperationException>(() => routine.MoveNext()); Assert.Null(focused);
    }
    [Fact]
    public void CancellingOwnedFocusClearsOnlyItsTarget()
    {
        object target = new(); object? focused = null;
        IEnumerator Native() { focused = target; yield return null; }
        var routine = new NavigationFocusRoutine(Native(), target, () => true, () => focused, () => focused = null, () => { });
        Assert.True(routine.MoveNext()); routine.Dispose(); Assert.Null(focused);
    }
}
