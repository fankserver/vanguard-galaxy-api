using System;
using System.Linq;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class TravelLegTrackerTests
{
    private static readonly TravelLegTracker.Place Origin = new("system-a", "station-a");
    private static readonly TravelLegTracker.Place Target = new("system-b", "gate-b");

    [Fact]
    public void InitialPlacementIsNotArrivalAndRequiresReadiness()
    {
        var tracker = new TravelLegTracker(); var session = Guid.NewGuid(); tracker.Reset(session);
        tracker.PlaceInitially(session, Origin, 1, false); Assert.Empty(tracker.Drain());
        tracker.PlaceInitially(session, Origin, 2, true);
        var fact = Assert.Single(tracker.Drain()); Assert.Equal(TravelLegTracker.Kind.InitialPlacement, fact.Transition);
        Assert.Null(fact.Operation); Assert.Same(Origin, tracker.Current);
        tracker.PlaceInitially(session, Origin, 3, true); Assert.Empty(tracker.Drain());
    }

    [Fact]
    public void RequestAndIteratorExistenceCannotProveDepartureOrArrival()
    {
        var tracker = new TravelLegTracker(); var session = Guid.NewGuid(); tracker.Reset(session);
        tracker.PlaceInitially(session, Origin, 10, true); tracker.Drain();
        var leg = tracker.Request(session, Target)!;
        tracker.Arrive(leg, Target, 12, true);
        Assert.Equal(TravelLegTracker.Kind.Requested, Assert.Single(tracker.Drain()).Transition);
        Assert.Same(Origin, tracker.Current);
        tracker.Depart(leg, 15); tracker.Depart(leg, 16);
        var departed = Assert.Single(tracker.Drain()); Assert.Equal(5d, departed.DwellSeconds); Assert.Null(tracker.Current);
        tracker.Arrive(leg, Target, 20, false); Assert.Empty(tracker.Drain());
        tracker.Arrive(leg, Target, 21, true); tracker.Arrive(leg, Target, 22, true);
        Assert.Equal(TravelLegTracker.Kind.Arrived, Assert.Single(tracker.Drain()).Transition);
    }

    [Fact]
    public void ActualAlternativeDestinationIsNotReplacedWithRequestedDestination()
    {
        var tracker = new TravelLegTracker(); var session = Guid.NewGuid(); tracker.Reset(session);
        var leg = tracker.Request(session, Target)!; tracker.Depart(leg, 1);
        var actual = new TravelLegTracker.Place("sandbox", "replacement-gate");
        tracker.Arrive(leg, actual, 2, true);
        Assert.Same(Target, leg.Requested); Assert.Same(actual, tracker.Current);
        Assert.Same(actual, tracker.Drain().Last().Location);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SupersededLegCannotCompleteOrCancelReplacement(bool departed)
    {
        var tracker = new TravelLegTracker(); var session = Guid.NewGuid(); tracker.Reset(session);
        tracker.PlaceInitially(session, Origin, 0, true);
        var old = tracker.Request(session, Target)!;
        if (departed) tracker.Depart(old, 1);
        tracker.Drain(); var next = tracker.Request(session, Origin)!;
        var replacement = tracker.Drain();
        Assert.Equal(new[] { TravelLegTracker.Kind.Cancelled, TravelLegTracker.Kind.Requested }, replacement.Select(f => f.Transition));
        Assert.Same(departed ? null : Origin, tracker.Current);
        Assert.Same(departed ? null : Origin, replacement[0].Location);
        Assert.Same(departed ? null : Origin, next.Origin);
        tracker.Arrive(old, Target, 2, true); tracker.Depart(old, 2); tracker.Cancel(old); Assert.Empty(tracker.Drain());
        tracker.Depart(next, 3); tracker.Arrive(next, Origin, 4, true);
        Assert.All(tracker.Drain(), f => Assert.Equal(next.Id, f.Operation)); Assert.NotEqual(old.Id, next.Id);
    }

    [Fact]
    public void ReplacementSessionDiscardsQueuedAndLateEvidence()
    {
        var tracker = new TravelLegTracker(); var oldSession = Guid.NewGuid(); tracker.Reset(oldSession);
        var old = tracker.Request(oldSession, Target)!; tracker.Depart(old, 1);
        var current = Guid.NewGuid(); tracker.Reset(current);
        tracker.Arrive(old, Target, 2, true); tracker.Cancel(old); tracker.PlaceInitially(oldSession, Origin, 2, true);
        Assert.Null(tracker.Request(oldSession, Target)); Assert.Empty(tracker.Drain());
        tracker.PlaceInitially(current, Origin, 3, true); Assert.Equal(current, Assert.Single(tracker.Drain()).Session);
        tracker.Reset(null); Assert.Null(tracker.Current); Assert.Null(tracker.Request(current, Target));
    }

    [Fact]
    public void RapidChainedLegsKeepTheirOwnArrivalAndDwellBoundaries()
    {
        var tracker = new TravelLegTracker(); var session = Guid.NewGuid(); tracker.Reset(session);
        tracker.PlaceInitially(session, Origin, 1, true); tracker.Drain();
        for (var i = 0; i < 3; i++)
        {
            var leg = tracker.Request(session, Target)!; tracker.Depart(leg, 2); tracker.Arrive(leg, Target, 2, true);
        }
        var facts = tracker.Drain(); Assert.Equal(9, facts.Length);
        Assert.Equal(3, facts.Select(f => f.Operation).Distinct().Count());
        Assert.Equal(new[] { 1d, 0d, 0d }, facts.Where(f => f.Transition == TravelLegTracker.Kind.Departed).Select(f => f.DwellSeconds!.Value));
    }

    [Fact]
    public void InterruptedTravelCanRecoverVerifiedPlacementWithoutFabricatingALeg()
    {
        var tracker = new TravelLegTracker(); var session = Guid.NewGuid(); tracker.Reset(session);
        tracker.PlaceInitially(session, Origin, 1, true); tracker.Drain();
        var leg = tracker.Request(session, Target)!; tracker.Depart(leg, 2);
        tracker.RecoverPlacement(session, Target, 3, true); Assert.Null(tracker.Current);
        tracker.Cancel(leg); Assert.Null(tracker.Drain().Last().Location);
        tracker.RecoverPlacement(session, Target, 3, false); Assert.Null(tracker.Current);
        tracker.RecoverPlacement(Guid.NewGuid(), Target, 3, true); Assert.Null(tracker.Current);
        tracker.RecoverPlacement(session, Target, 4, true);
        var recovered = Assert.Single(tracker.Drain()); Assert.Equal(TravelLegTracker.Kind.RecoveredPlacement, recovered.Transition);
        Assert.Null(recovered.Operation); Assert.Same(Target, tracker.Current);
        tracker.RecoverPlacement(session, Target, 5, true); tracker.Arrive(leg, Target, 5, true); Assert.Empty(tracker.Drain());
        var next = tracker.Request(session, Origin)!; Assert.Same(Target, next.Origin); tracker.Depart(next, 6);
        Assert.Equal(2d, tracker.Drain().Last().DwellSeconds);
    }

    [Fact]
    public void PendingRequestDoesNotAdoptUnattributedInitialReadiness()
    {
        var tracker = new TravelLegTracker(); var session = Guid.NewGuid(); tracker.Reset(session);
        var leg = tracker.Request(session, Target)!; tracker.Drain();
        tracker.PlaceInitially(session, Origin, 1, true); Assert.Empty(tracker.Drain()); Assert.Null(leg.Origin);
        tracker.Cancel(leg); tracker.Drain();
        tracker.PlaceInitially(session, Origin, 2, true); Assert.Equal(TravelLegTracker.Kind.InitialPlacement, Assert.Single(tracker.Drain()).Transition);
    }

    [Fact]
    public void ClockRollbackDoesNotInventNegativeDwell()
    {
        var tracker = new TravelLegTracker(); var session = Guid.NewGuid(); tracker.Reset(session);
        tracker.PlaceInitially(session, Origin, 10, true); tracker.Drain();
        var leg = tracker.Request(session, Target)!; tracker.Depart(leg, 5);
        Assert.Null(tracker.Drain().Last().DwellSeconds);
    }
}
