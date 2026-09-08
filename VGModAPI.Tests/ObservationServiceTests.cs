using System;
using System.Collections.Generic;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class ObservationServiceTests
{
    private static LifecycleHub Bound(Action<string, Exception>? report = null)
    {
        var hub = new LifecycleHub(report ?? ((_, _) => { }));
        hub.SetCapability("session-lifecycle", true, "Bound.");
        hub.SetCapability("save-outcomes", true, "Bound.");
        hub.SetCapability("native-travel", true, "Bound.");
        return hub;
    }

    [Fact]
    public void PublicAndInternalHandlersKeepRegistrationOrderAndMulticastFailureIsolation()
    {
        var failures = new List<string>();
        using var hub = Bound((owner, _) => failures.Add(owner));
        ILifecycleService service = hub;
        var seen = new List<string>();
        hub.Subscribe("legacy-first", _ => seen.Add("legacy-first"));
        Action<LifecycleEvent> handlers = _ => { seen.Add("throwing"); throw new Exception(); };
        handlers += _ => seen.Add("modern");
        service.Changed += handlers;
        hub.Subscribe("legacy-last", _ => seen.Add("legacy-last"));
        Assert.Empty(seen);
        hub.Begin(SessionOrigin.NewGame, null);
        Assert.Equal(new[] { "legacy-first", "throwing", "modern", "legacy-last" }, seen);
        Assert.Single(failures);
        Assert.Equal(typeof(ObservationServiceTests).Assembly.GetName().Name, failures[0]);
        seen.Clear();
        service.Changed -= handlers;
        hub.PlayerReady(hub.CurrentSession!.Id);
        Assert.Equal(new[] { "legacy-first", "legacy-last" }, seen);
    }

    [Fact]
    public void LifecycleIsDirectAndSeparatesTrackingFromSaveOutcomeHealth()
    {
        using var hub = Bound();
        ILifecycleService service = hub;
        Assert.Same(hub, service);
        var facts = new List<LifecycleEventKind>();
        service.Changed += fact => facts.Add(fact.Kind);
        hub.Begin(SessionOrigin.NewGame, null);
        Assert.NotNull(service.CurrentSession);
        hub.SetCapability("session-lifecycle", false, "Tracking fault.", ServiceUnavailableReason.ObserverFault);
        Assert.Null(service.CurrentSession);
        facts.Clear();
        hub.Publish(new LifecycleEvent(LifecycleEventKind.PlayerReady, hub.CurrentSession));
        hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveSkipped, hub.CurrentSession, Guid.NewGuid()));
        Assert.Equal(new[] { LifecycleEventKind.SaveSkipped }, facts);
        hub.Invalidate("Tracking stopped.");
        Assert.Equal(LifecycleEventKind.SessionInvalidated, facts[1]);
        Assert.Equal(SessionPhase.Invalidated, service.CurrentSession!.Phase);
        foreach (var name in new[] { "ILifecycleApi", "ILifecycleDispatchState" })
            Assert.Null(typeof(ModApi).Assembly.GetType("VGModAPI." + name));
        Assert.Null(typeof(ModApi).GetProperty("Current"));
    }

    [Fact]
    public void TravelNotificationsCloseMutationDispatchGateAndRejectStaleSessions()
    {
        using var hub = Bound();
        using var source = new TravelEvents((_, _) => { });
        using var service = new TravelServiceView(hub, source);
        var id = hub.Begin(SessionOrigin.NewGame, null);
        source.SetSession(id);
        var scopes = new List<bool>();
        service.Transitioned += _ => scopes.Add(hub.IsDispatchingCallbacks && service.IsDispatchingCallbacks);
        source.Emit(id, null, TravelTransitionKind.InitialPlacement, TravelMode.InSystem, null, null, new TravelLocation("system", null, null, null), 1);
        Assert.Equal(new[] { true }, scopes);
        Assert.False(hub.IsDispatchingCallbacks);
        Assert.Equal(id, service.SessionId);
        hub.Begin(SessionOrigin.NewGame, null);
        Assert.Null(service.SessionId);
        Assert.Null(service.CurrentLocation);
        source.Emit(id, null, TravelTransitionKind.InitialPlacement, TravelMode.InSystem, null, null, new TravelLocation("system", null, null, null), 2);
        Assert.Single(scopes);
    }

    [Fact]
    public void UnavailableTravelHasNoPlacementAndDisposedSourceAcceptsOnlyInertObservationRegistration()
    {
        using var hub = Bound();
        using var source = new TravelEvents((_, _) => { });
        using var service = new TravelServiceView(hub, source);
        var id = hub.Begin(SessionOrigin.NewGame, null);
        source.SetSession(id);
        hub.SetCapability("native-travel", false, "Fault.", ServiceUnavailableReason.ObserverFault);
        source.Dispose();
        Action<TravelTransition> callback = _ => throw new Exception();
        service.Transitioned += callback;
        service.Transitioned -= callback;
        Assert.Null(service.SessionId);
        Assert.Null(service.CurrentLocation);
        Assert.False(service.Availability.IsAvailable);
        hub.Dispose();
        Assert.Throws<ObjectDisposedException>(() => service.Transitioned += callback);
    }

    [Fact]
    public void LifecycleDeliversTerminalInvalidationEvenAfterAvailabilityCloses()
    {
        using var hub = Bound();
        ILifecycleService service = hub;
        hub.Begin(SessionOrigin.NewGame, null);
        var facts = new List<LifecycleEvent>();
        service.Changed += facts.Add;
        hub.Dispose();
        Assert.Equal(LifecycleEventKind.SessionInvalidated, Assert.Single(facts).Kind);
        Assert.Equal(SessionPhase.Invalidated, service.CurrentSession!.Phase);
        Assert.Equal(ServiceUnavailableReason.ApiStopped, service.SessionTracking.Availability.Reason);
    }

    [Fact]
    public void AvailableBindingsCannotBeComposedWithMissingSources()
    {
        using var hub = Bound();
        Assert.Throws<ArgumentException>(() => new TravelServiceView(hub, null));
        Assert.Throws<ArgumentException>(() => new StationServiceView(hub, null));
    }

    [Fact]
    public void MissingSourcesRemainUnavailableAndForeignThreadQueriesFail()
    {
        using var hub = new LifecycleHub((_, _) => { });
        using var missions = new MissionTransitions(hub);
        using var travel = new TravelServiceView(hub, null);
        using var station = new StationServiceView(hub, null);
        Assert.False(missions.Availability.IsAvailable);
        Assert.False(missions.IdentityContinuity.Availability.IsAvailable);
        Assert.Null(travel.SessionId);
        Assert.Null(station.SessionId);
        Assert.IsType<InvalidOperationException>(ServiceNotificationTests.OnWorker(() => _ = travel.CurrentLocation));
        Assert.IsType<InvalidOperationException>(ServiceNotificationTests.OnWorker(() => _ = station.IsDispatchingCallbacks));
    }
}
