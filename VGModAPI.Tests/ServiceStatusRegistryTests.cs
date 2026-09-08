using System;
using System.Collections.Generic;
using System.Linq;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class ServiceStatusRegistryTests
{
    private static LifecycleHub Bound()
    {
        var hub = new LifecycleHub((_, _) => { });
        foreach (var name in new[] { "session-lifecycle", "save-outcomes", "save-data", "mission-transitions", "mission-continuity", "native-travel" })
            hub.SetCapability(name, true, "Bound.");
        return hub;
    }

    [Fact]
    public void DependencyFailureIsCoherentBeforeEveryCallbackAndSaveObservationRemainsIndependent()
    {
        using var hub = Bound();
        var session = hub.Services.Get("session-lifecycle");
        var save = hub.Services.Get("save-data");
        var mission = hub.Services.Get("mission-transitions");
        var identity = hub.Services.Get("mission-continuity");
        var states = new List<bool>();
        foreach (var view in new[] { session, save, mission, identity })
            view.AvailabilityChanged += _ => states.Add(!session.Availability.IsAvailable && !save.Availability.IsAvailable &&
                !mission.Availability.IsAvailable && !identity.Availability.IsAvailable && hub.IsDispatchingCallbacks);
        hub.SetCapability("session-lifecycle", false, "Unsupported.", ServiceUnavailableReason.UnsupportedGame);
        Assert.Equal(new[] { true, true, true, true }, states);
        Assert.Equal(ServiceUnavailableReason.DependencyUnavailable, identity.Availability.Reason);
        Assert.False(hub.Capabilities.Single(c => c.Name == "native-travel").Available);
        Assert.True(hub.Capabilities.Single(c => c.Name == "save-outcomes").Available);
        Assert.False(hub.IsDispatchingCallbacks);
    }

    [Fact]
    public void NoReplayUnchangedStateSuppressionAndReasonDetailChanges()
    {
        using var hub = Bound();
        var view = hub.Services.Get("native-travel");
        Assert.Same(view, hub.Services.Get("native-travel"));
        var events = new List<ServiceAvailability>();
        view.AvailabilityChanged += events.Add;
        Assert.Empty(events);
        Assert.True(view.Availability.IsAvailable);
        hub.Services.Refresh();
        hub.SetCapability("native-travel", true, "Bound.");
        Assert.Empty(events);
        hub.SetCapability("native-travel", false, "First.");
        hub.SetCapability("native-travel", false, "First.");
        hub.SetCapability("native-travel", false, "Second.");
        hub.SetCapability("native-travel", false, "Second.", ServiceUnavailableReason.ObserverFault);
        Assert.Equal(3, events.Count);
        Assert.Equal("Second.", events[1].Detail);
        Assert.Equal(ServiceUnavailableReason.ObserverFault, events[2].Reason);
    }

    private sealed class FaultLatch { internal volatile bool Failed; }

    [Fact]
    public void WorkerFaultIsReadableImmediatelyButOnlyMainThreadReconciliationNotifies()
    {
        using var hub = Bound();
        var latch = new FaultLatch();
        hub.Services.WatchFault("session-lifecycle", () => latch.Failed);
        hub.Services.WatchFault("save-outcomes", () => latch.Failed);
        var save = hub.Services.Get("save-data");
        var notices = new List<ServiceAvailability>();
        save.AvailabilityChanged += notices.Add;
        Assert.Null(ServiceNotificationTests.OnWorker(() => latch.Failed = true));
        Assert.Empty(notices);
        Assert.False(save.Availability.IsAvailable);
        Assert.False(hub.Capabilities.Single(c => c.Name == "save-outcomes").Available);
        Assert.Empty(notices);
        hub.Services.Refresh(); hub.Services.Refresh();
        Assert.Single(notices);
        Assert.Equal(ServiceUnavailableReason.DependencyUnavailable, notices[0].Reason);
    }

    [Fact]
    public void ReentrantChangesQueueWithoutAnIntermediateDependencyView()
    {
        using var hub = Bound();
        var primary = hub.Services.Get("session-lifecycle");
        var dependent = hub.Services.Get("save-data");
        var seen = new List<string>();
        primary.AvailabilityChanged += state =>
        {
            seen.Add("primary:" + state.IsAvailable);
            if (!state.IsAvailable) hub.SetCapability("session-lifecycle", true, "Bound.");
        };
        dependent.AvailabilityChanged += state => seen.Add("dependent:" + state.IsAvailable + ":current=" + dependent.Availability.IsAvailable);
        hub.SetCapability("session-lifecycle", false, "Failure.");
        Assert.Equal(new[] { "primary:False", "dependent:False:current=True", "primary:True", "dependent:True:current=True" }, seen);
    }

    [Fact]
    public void StoppingClosesGatesBeforePublishingAndRetainedViewsRemainReadable()
    {
        var hub = Bound();
        hub.Begin(SessionOrigin.NewGame, null);
        var view = hub.Services.Get("mission-transitions");
        var notices = new List<ServiceAvailability>();
        var phases = new List<SessionPhase?>();
        Action<ServiceAvailability> callback = state => { notices.Add(state); phases.Add(hub.CurrentSession?.Phase); };
        view.AvailabilityChanged += callback;
        hub.Services.BeginStop();
        Assert.Empty(notices);
        Assert.Equal(ServiceUnavailableReason.ApiStopped, view.Availability.Reason);
        hub.Dispose();
        Assert.Single(notices);
        Assert.Equal(new SessionPhase?[] { SessionPhase.Invalidated }, phases);
        Assert.Equal(ServiceUnavailableReason.ApiStopped, notices[0].Reason);
        view.AvailabilityChanged -= callback;
        hub.Dispose();
        Assert.Equal(ServiceUnavailableReason.ApiStopped, view.Availability.Reason);
        Assert.Throws<ObjectDisposedException>(() => view.AvailabilityChanged += callback);
        Assert.All(hub.Capabilities, capability => Assert.False(capability.Available));
    }

    [Fact]
    public void DisposalInsideNotificationStillDeliversQueuedStopBeforeClearingHandlers()
    {
        using var hub = Bound();
        var primary = hub.Services.Get("session-lifecycle");
        var dependent = hub.Services.Get("save-data");
        var stops = new List<string>();
        primary.AvailabilityChanged += state =>
        {
            if (state.Reason != ServiceUnavailableReason.ApiStopped) hub.Services.Dispose();
            else stops.Add("primary");
        };
        dependent.AvailabilityChanged += state => { if (state.Reason == ServiceUnavailableReason.ApiStopped) stops.Add("dependent"); };
        hub.SetCapability("session-lifecycle", false, "Failure.");
        Assert.Equal(new[] { "primary", "dependent" }, stops);
        Assert.False(hub.IsDispatchingCallbacks);
    }

    [Fact]
    public void ShutdownCallbacksCannotCreateANewSessionOrPublishOrdinaryEvents()
    {
        using var hub = Bound();
        hub.Begin(SessionOrigin.NewGame, null);
        var events = new List<LifecycleEvent>();
        Exception? restart = null;
        hub.Subscribe("shutdown", fact =>
        {
            events.Add(fact);
            restart = Record.Exception(() => hub.Begin(SessionOrigin.NewGame, null));
            hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveStarted, hub.CurrentSession));
        });
        hub.Dispose();
        Assert.IsType<ObjectDisposedException>(restart);
        Assert.Equal(LifecycleEventKind.SessionInvalidated, Assert.Single(events).Kind);
        Assert.Equal(SessionPhase.Invalidated, hub.CurrentSession!.Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReentrantLifecycleDisposalDrainsTerminalInvalidationBeforeServiceShutdown(bool duringInvalidation)
    {
        using var hub = Bound();
        var seen = new List<string>();
        var status = hub.Services.Get("session-lifecycle");
        status.AvailabilityChanged += state => seen.Add("status:" + state.Reason);
        hub.Subscribe("first", fact =>
        {
            seen.Add("first:" + fact.Kind);
            if (duringInvalidation && fact.Kind != LifecycleEventKind.SessionInvalidated) return;
            if (!duringInvalidation && fact.Kind == LifecycleEventKind.SessionStarting)
                hub.PlayerReady(fact.Session!.Id); // Queued ordinary work must not survive shutdown.
            hub.Dispose();
        });
        hub.Subscribe("second", fact => seen.Add("second:" + fact.Kind));
        hub.Begin(SessionOrigin.NewGame, null);
        if (duringInvalidation)
        {
            seen.Clear();
            hub.Dispose();
        }
        Assert.Equal(duringInvalidation
            ? new[] { "first:SessionInvalidated", "second:SessionInvalidated", "status:ApiStopped" }
            : new[] { "first:SessionStarting", "first:SessionInvalidated", "second:SessionInvalidated", "status:ApiStopped" }, seen);
        Assert.Equal(SessionPhase.Invalidated, hub.CurrentSession!.Phase);
        Assert.False(hub.IsDispatchingCallbacks);
        Assert.Throws<ObjectDisposedException>(() => hub.Subscribe("late", _ => { }));
        Assert.Throws<ObjectDisposedException>(() => hub.Begin(SessionOrigin.NewGame, null));
    }

    [Fact]
    public void UnknownFeaturesFailClosedAndAllStatusAccessRequiresTheMainThread()
    {
        using var hub = Bound();
        var view = hub.Services.Get("unbound");
        Assert.False(view.Availability.IsAvailable);
        foreach (Action action in new Action[] { () => _ = view.Availability, () => hub.Services.Refresh(), () => _ = hub.Capabilities })
            Assert.IsType<InvalidOperationException>(ServiceNotificationTests.OnWorker(action));
        Assert.Throws<ArgumentException>(() => hub.SetCapability("bad", false, "", ServiceUnavailableReason.None));
    }
}
