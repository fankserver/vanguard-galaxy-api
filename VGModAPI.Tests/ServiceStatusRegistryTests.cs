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
        var view = hub.Services.Get("mission-transitions");
        var notices = new List<ServiceAvailability>();
        Action<ServiceAvailability> callback = notices.Add;
        view.AvailabilityChanged += callback;
        hub.Services.BeginStop();
        Assert.Empty(notices);
        Assert.Equal(ServiceUnavailableReason.ApiStopped, view.Availability.Reason);
        hub.Dispose();
        Assert.Single(notices);
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
