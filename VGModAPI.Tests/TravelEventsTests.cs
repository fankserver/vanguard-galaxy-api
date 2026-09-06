using System;
using System.Collections.Generic;
using System.Threading;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class TravelEventsTests
{
    private static readonly TravelLocation Location = new("system", "poi", "System", "POI");
    private static void Arrival(TravelEvents hub, Guid session) => hub.Emit(session, Guid.NewGuid(), TravelTransitionKind.Arrived, TravelMode.InSystem, null, Location, Location, 1);

    [Fact]
    public void SubscriberAndDiagnosticFaultsAreIsolatedUnderDispatchGuard()
    {
        TravelEvents? hub = null;
        var loggerGuard = false; var consumerGuard = false;
        using var events = hub = new TravelEvents((_, _) => { loggerGuard = hub!.IsDispatchingCallbacks; throw new Exception("logger"); });
        var session = Guid.NewGuid(); events.SetSession(session); var seen = 0;
        using var broken = events.Subscribe("broken", _ => throw new Exception("consumer"));
        using var healthy = events.Subscribe("healthy", _ => { consumerGuard = events.IsDispatchingCallbacks; seen++; });
        Arrival(events, session); Assert.Equal(1, seen); Assert.True(loggerGuard); Assert.True(consumerGuard);
        Assert.False(events.IsDispatchingCallbacks); Assert.Same(Location, events.CurrentLocation);
    }

    [Fact]
    public void ReentrantDeliveryIsQueuedAndNewSubscribersStartWithNextEvent()
    {
        using var hub = new TravelEvents((_, error) => throw error); var session = Guid.NewGuid(); hub.SetSession(session);
        var order = new List<string>(); IDisposable? late = null;
        using var first = hub.Subscribe("first", e =>
        {
            order.Add("first" + e.Sequence);
            if (e.Sequence == 1) { late = hub.Subscribe("late", next => order.Add("late" + next.Sequence)); Arrival(hub, session); }
        });
        using var second = hub.Subscribe("second", e => order.Add("second" + e.Sequence));
        Arrival(hub, session);
        Assert.Equal(new[] { "first1", "second1", "first2", "second2", "late2" }, order); late?.Dispose();
    }

    [Fact]
    public void ReentrantSessionReplacementDropsQueuedAndRemainingOldDelivery()
    {
        using var hub = new TravelEvents((_, _) => { }); var old = Guid.NewGuid(); var next = Guid.NewGuid(); hub.SetSession(old);
        var delivered = new List<Guid>();
        using var first = hub.Subscribe("first", e =>
        {
            if (e.SessionId == old) { Arrival(hub, old); hub.SetSession(next); Arrival(hub, next); }
        });
        using var second = hub.Subscribe("second", e => delivered.Add(e.SessionId));
        Arrival(hub, old); Assert.Equal(new[] { next }, delivered);
        Arrival(hub, old); Assert.Single(delivered); Assert.Equal(next, hub.SessionId);
    }

    [Fact]
    public void DisposalBeforeTurnAndDuringCallbackSuppressesFurtherDelivery()
    {
        using var hub = new TravelEvents((_, _) => { }); var session = Guid.NewGuid(); hub.SetSession(session);
        IDisposable? later = null; var called = false; var disposalGuard = false;
        using var first = hub.Subscribe("first", _ => { later!.Dispose(); hub.Dispose(); disposalGuard = hub.IsDispatchingCallbacks; });
        later = hub.Subscribe("later", _ => called = true);
        Arrival(hub, session); Assert.False(called); Assert.True(disposalGuard); Assert.Null(hub.CurrentLocation); Assert.Null(hub.SessionId);
        Assert.False(hub.IsDispatchingCallbacks);
        Assert.Throws<ObjectDisposedException>(() => hub.Subscribe("late", _ => { }));
    }

    [Fact]
    public void QueryDoesNotReplayAndDepartureClearsVerifiedLocation()
    {
        using var hub = new TravelEvents((_, _) => { }); var session = Guid.NewGuid(); hub.SetSession(session);
        Arrival(hub, session); var called = 0;
        using var late = hub.Subscribe("late", _ => called++); Assert.Equal(0, called); Assert.Same(Location, hub.CurrentLocation);
        hub.Emit(session, Guid.NewGuid(), TravelTransitionKind.Departed, TravelMode.InSystem, Location, Location, null, 2, 1);
        Assert.Null(hub.CurrentLocation); Assert.Equal(1, called);
    }

    [Fact]
    public void ForeignThreadCannotReadOrMutateService()
    {
        using var hub = new TravelEvents((_, _) => { });
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Assert.Throws<InvalidOperationException>(() => hub.CurrentLocation);
                Assert.Throws<InvalidOperationException>(() => hub.Subscribe("foreign", _ => { }));
                Assert.Throws<InvalidOperationException>(() => hub.SetSession(Guid.NewGuid()));
            }
            catch (Exception error) { failure = error; }
        });
        thread.Start(); thread.Join(); Assert.Null(failure);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void InvalidClocksCannotBecomePublicFacts(double time)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TravelTransition(Guid.NewGuid(), Guid.NewGuid(), 1,
            TravelTransitionKind.Arrived, TravelMode.InSystem, null, Location, Location, time, null));
    }

    [Fact]
    public void FinalRouteCompletionRequiresActualLocationEvenForEmptySpace()
    {
        var session = Guid.NewGuid(); var operation = Guid.NewGuid();
        Assert.Throws<ArgumentNullException>(() => new TravelTransition(session, operation, 1,
            TravelTransitionKind.RouteCompleted, TravelMode.InSystem, null, Location, null, 1, null));
        var emptySpace = new TravelLocation("system", null, null, null);
        var fact = new TravelTransition(session, operation, 1, TravelTransitionKind.RouteCompleted,
            TravelMode.InSystem, null, Location, emptySpace, 1, null);
        Assert.Same(emptySpace, fact.ActualLocation); Assert.Null(emptySpace.SystemName);
    }

    [Fact]
    public void PublicFactsRequirePlacementVersusOperationIdentityAndActualLocation()
    {
        Assert.Throws<ArgumentException>(() => new TravelTransition(Guid.NewGuid(), Guid.NewGuid(), 1,
            TravelTransitionKind.InitialPlacement, TravelMode.Unknown, null, null, Location, 1, null));
        Assert.Throws<ArgumentException>(() => new TravelTransition(Guid.NewGuid(), null, 1,
            TravelTransitionKind.Arrived, TravelMode.InSystem, null, Location, Location, 1, null));
        Assert.Throws<ArgumentNullException>(() => new TravelTransition(Guid.NewGuid(), Guid.NewGuid(), 1,
            TravelTransitionKind.Arrived, TravelMode.InSystem, null, Location, null, 1, null));
    }
}
