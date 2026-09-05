using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class LifecycleHubTests
{
    private static LifecycleHub Hub() => new((_, _) => { });

    [Fact]
    public void SessionProgressesOnceWithImmutableSnapshots()
    {
        using var hub = Hub();
        var events = new List<LifecycleEvent>();
        using var subscription = hub.Subscribe("test", events.Add);
        var id = hub.Begin(SessionOrigin.SaveLoad, "/save/a.save");
        var initial = hub.CurrentSession;
        hub.PlayerReady(id); hub.PlayerReady(id);
        hub.GameplayInitialized(id); hub.GameplayInitialized(id);
        Assert.Equal(new[] { LifecycleEventKind.SessionStarting, LifecycleEventKind.PlayerReady, LifecycleEventKind.GameplayInitialized }, events.Select(e => e.Kind));
        Assert.All(events, e => Assert.Equal(id, e.Session!.Id));
        Assert.Equal(SessionPhase.Starting, initial!.Phase);
        Assert.Equal(SessionPhase.GameplayInitialized, hub.CurrentSession!.Phase);
    }

    [Fact]
    public void ReplacementRejectsStaleReadyAndFailureSignals()
    {
        using var hub = Hub();
        var events = new List<LifecycleEvent>();
        hub.Subscribe("test", events.Add);
        var a = hub.Begin(SessionOrigin.SaveLoad, "a");
        var b = hub.Begin(SessionOrigin.SaveLoad, "b");
        hub.PlayerReady(a); hub.GameplayInitialized(a); hub.Fail(a, "old");
        Assert.Equal(b, hub.CurrentSession!.Id);
        Assert.Equal(SessionPhase.Starting, hub.CurrentSession.Phase);
        Assert.Equal(new[] { LifecycleEventKind.SessionStarting, LifecycleEventKind.SessionInvalidated, LifecycleEventKind.SessionStarting }, events.Select(e => e.Kind));
    }

    [Fact]
    public void FailureIsTerminalUntilAnotherAttempt()
    {
        using var hub = Hub();
        var events = new List<LifecycleEvent>(); hub.Subscribe("test", events.Add);
        var id = hub.Begin(SessionOrigin.NewGame, null);
        hub.Fail(id, "creation failed"); hub.Fail(id, "again"); hub.PlayerReady(id); hub.GameplayInitialized(id);
        Assert.Equal(SessionPhase.Failed, hub.CurrentSession!.Phase);
        Assert.Single(events, e => e.Kind == LifecycleEventKind.SessionStartFailed);
        var next = hub.Begin(SessionOrigin.NewGame, null);
        Assert.NotEqual(id, next);
    }

    [Fact]
    public void GameplayCannotBecomeReadyBeforePlayerAndCanFailDuringInitialization()
    {
        using var hub = Hub();
        var id = hub.Begin(SessionOrigin.NewGame, null);
        hub.GameplayInitialized(id);
        Assert.Equal(SessionPhase.Starting, hub.CurrentSession!.Phase);
        hub.PlayerReady(id); hub.Fail(id, "gameplay failed");
        Assert.Equal(SessionPhase.Failed, hub.CurrentSession.Phase);
    }

    [Fact]
    public void MenuInvalidatesExactlyOnceAndRejectsDelayedCallbacks()
    {
        using var hub = Hub(); var events = new List<LifecycleEvent>(); hub.Subscribe("test", events.Add);
        var id = hub.Begin(SessionOrigin.SaveLoad, "a"); hub.PlayerReady(id);
        hub.Invalidate("menu"); hub.Invalidate("menu"); hub.GameplayInitialized(id); hub.Fail(id, "late");
        Assert.Single(events, e => e.Kind == LifecycleEventKind.SessionInvalidated);
        Assert.Equal(SessionPhase.Invalidated, hub.CurrentSession!.Phase);
    }

    [Fact]
    public void SubscriberAndLoggerExceptionsDoNotBlockOtherSubscribers()
    {
        var owners = new List<string>();
        using var hub = new LifecycleHub((owner, _) => { owners.Add(owner); throw new Exception("logger"); });
        hub.Subscribe("broken", _ => throw new Exception("subscriber"));
        int received = 0; hub.Subscribe("healthy", _ => received++);
        hub.Begin(SessionOrigin.NewGame, null);
        Assert.Equal(1, received); Assert.Equal(new[] { "broken" }, owners);
    }

    [Fact]
    public void DisposalAndNewSubscriptionDuringDeliveryHaveDefinedBehavior()
    {
        using var hub = Hub(); var received = new List<string>();
        IDisposable? second = null; IDisposable? late = null;
        hub.Subscribe("first", _ => { received.Add("first"); second!.Dispose(); late ??= hub.Subscribe("late", _ => received.Add("late")); });
        second = hub.Subscribe("second", _ => received.Add("second"));
        var id = hub.Begin(SessionOrigin.NewGame, null);
        Assert.Equal(new[] { "first" }, received);
        hub.PlayerReady(id);
        Assert.Equal(new[] { "first", "first", "late" }, received);
    }

    [Fact]
    public void ReentrantEventsAreQueuedAndDoNotOverwriteNewSession()
    {
        using var hub = Hub(); var seen = new List<LifecycleEvent>(); bool replaced = false; Guid latest = default;
        hub.Subscribe("replace", e =>
        {
            if (e.Kind == LifecycleEventKind.SessionStarting && !replaced)
            { replaced = true; latest = hub.Begin(SessionOrigin.NewGame, null); }
        });
        hub.Subscribe("record", seen.Add);
        var first = hub.Begin(SessionOrigin.SaveLoad, "a");
        Assert.Equal(latest, hub.CurrentSession!.Id);
        Assert.Equal(new[] { LifecycleEventKind.SessionStarting, LifecycleEventKind.SessionInvalidated, LifecycleEventKind.SessionStarting }, seen.Select(e => e.Kind));
        Assert.Equal(first, seen[0].Session!.Id); Assert.Equal(latest, seen[2].Session!.Id);
    }

    [Fact]
    public void LateSubscribersQueryWithoutReplayAndShutdownStopsDelivery()
    {
        var hub = Hub(); var id = hub.Begin(SessionOrigin.NewGame, null);
        int count = 0; var subscription = hub.Subscribe("late", _ => count++);
        Assert.Equal(id, hub.CurrentSession!.Id); Assert.Equal(0, count);
        hub.Dispose(); subscription.Dispose(); hub.PlayerReady(id);
        Assert.Equal(0, count);
        Assert.Throws<ObjectDisposedException>(() => hub.Subscribe("late", _ => { }));
    }

    [Fact]
    public void AccessFromWrongThreadIsRejected()
    {
        using var hub = Hub(); Exception? error = null;
        var thread = new Thread(() => { try { _ = hub.CurrentSession; } catch (Exception ex) { error = ex; } });
        thread.Start(); thread.Join(); Assert.IsType<InvalidOperationException>(error);
    }

    [Fact]
    public void CapabilitiesAreSnapshotCopiesAndNeverRuntimeQualified()
    {
        using var hub = Hub(); hub.SetCapability("session", true, "bound");
        var before = hub.Capabilities; hub.SetCapability("session", false, "fault");
        Assert.True(before[0].Available); Assert.False(hub.Capabilities[0].Available);
        Assert.False(before[0].RuntimeQualified);
    }
}
