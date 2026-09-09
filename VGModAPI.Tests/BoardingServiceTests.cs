using System;
using System.Collections.Generic;
using System.Threading;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class BoardingServiceTests
{
    private static Guid Ready(LifecycleHub hub)
    {
        var id = hub.Begin(SessionOrigin.SaveLoad, "save"); hub.PlayerReady(id); hub.GameplayInitialized(id); return id;
    }
    private static BoardingTargetSnapshot Target(Guid session, Guid? generation = null, long revision = 1) =>
        new(new BoardingHandle(session, generation ?? Guid.NewGuid()), revision, BoardingEncounterKind.Ship, "Ship", "Faction", "Ship", BoardingAvailability.Available, null);
    [Fact]
    public void TypedHandlersAreScopedIsolatedRemovableAndHealthGated()
    {
        using var hub = new LifecycleHub((_, _) => { });
        hub.SetCapability("boarding-observation", true, "Test bindings.");
        using var engine = new BoardingService(hub, (_, _) => { });
        IBoardingService service = engine;
        var target = Target(Ready(hub)); var scopes = new List<bool>();
        Action<BoardingEvent> handlers = _ => throw new InvalidOperationException("Expected fault.");
        handlers += _ => scopes.Add(hub.IsDispatchingCallbacks && service.IsDispatchingCallbacks);
        service.Changed += handlers;
        Assert.True(engine.Observe(BoardingEventKind.TargetAvailable, target));
        Assert.True(Assert.Single(scopes));
        service.Changed -= handlers;
        Assert.True(engine.Observe(BoardingEventKind.TargetChanged, Target(target.Handle.SessionId, target.Handle.Generation, 2)));
        Assert.Single(scopes);
        hub.SetCapability("boarding-observation", false, "Fault.", ServiceUnavailableReason.ObserverFault);
        Assert.Null(service.SessionId); Assert.Empty(service.GetTargets()); Assert.Null(service.GetTarget(target.Handle));
        Assert.False(engine.Observe(BoardingEventKind.TargetChanged, target));
        engine.Dispose(); Assert.Equal(ServiceUnavailableReason.ObserverFault, service.Availability.Reason);
        Assert.Throws<ObjectDisposedException>(() => service.Changed += handlers);
        Assert.Null(typeof(ModApi).GetProperty("Boarding"));
        Assert.Null(typeof(ModApi).Assembly.GetType("VGModAPI.IBoardingEvents"));
    }
    [Fact]
    public void QueryDoesNotReplayAndInvalidationRejectsOldHandles()
    {
        using var hub = new LifecycleHub((_, _) => { }); hub.SetCapability("boarding-observation", true, "Test bindings."); using var service = new BoardingService(hub, (_, _) => { });
        var target = Target(Ready(hub));
        Assert.True(service.Observe(BoardingEventKind.TargetAvailable, target));
        var calls = 0; using var subscription = service.Subscribe("mod", _ => calls++);
        Assert.Same(target, service.GetTarget(target.Handle)); Assert.Equal(0, calls);
        hub.Invalidate("unload"); Assert.Empty(service.GetTargets());
        Assert.False(service.Observe(BoardingEventKind.TargetChanged, target));
        Ready(hub); Assert.Null(service.GetTarget(target.Handle));
        Assert.False(service.Observe(BoardingEventKind.TargetAvailable, target));
    }
    [Fact]
    public void DispatchContainsFailuresAndSkipsDisposedCallbacks()
    {
        using var hub = new LifecycleHub((_, _) => { }); var faults = 0;
        hub.SetCapability("boarding-observation", true, "Test bindings."); using var service = new BoardingService(hub, (_, _) => faults++);
        var target = Target(Ready(hub)); var seen = new List<long>(); IDisposable? victim = null;
        using var first = service.Subscribe("first", message =>
        {
            Assert.True(service.IsDispatchingCallbacks); victim?.Dispose();
            if (message.Sequence == 1) service.Observe(BoardingEventKind.TargetChanged, Target(target.Handle.SessionId, target.Handle.Generation, 2));
        });
        victim = service.Subscribe("victim", _ => Assert.Fail("Disposed callback ran"));
        using var bad = service.Subscribe("bad", _ => throw new InvalidOperationException());
        using var last = service.Subscribe("last", message => seen.Add(message.Sequence));
        service.Observe(BoardingEventKind.TargetAvailable, target);
        Assert.Equal(new long[] { 1, 2 }, seen); Assert.Equal(2, faults); Assert.False(service.IsDispatchingCallbacks);
    }
    [Fact]
    public void ReentrantSessionReplacementStopsOldEventDelivery()
    {
        using var hub = new LifecycleHub((_, _) => { }); hub.SetCapability("boarding-observation", true, "Test bindings."); using var service = new BoardingService(hub, (_, _) => { });
        var target = Target(Ready(hub)); var seen = 0;
        using var first = service.Subscribe("replace", _ => Ready(hub));
        using var last = service.Subscribe("last", _ => seen++);
        service.Observe(BoardingEventKind.TargetAvailable, target);
        Assert.Equal(0, seen); Assert.Empty(service.GetTargets());
    }
    [Fact]
    public void RetirementRemovesQueriesButPreservesEventSnapshot()
    {
        using var hub = new LifecycleHub((_, _) => { }); hub.SetCapability("boarding-observation", true, "Test bindings."); using var service = new BoardingService(hub, (_, _) => { });
        var target = Target(Ready(hub)); service.Observe(BoardingEventKind.TargetAvailable, target);
        BoardingEvent? receipt = null; using var sub = service.Subscribe("observer", message => receipt = message);
        service.Observe(BoardingEventKind.Retired, target);
        Assert.Null(service.GetTarget(target.Handle)); Assert.Same(target, receipt!.Target);
        Assert.False(service.Observe(BoardingEventKind.TargetAvailable, target));
    }
    [Fact]
    public void SnapshotCopiesCollectionsAndRejectsInvalidNumbers()
    {
        var target = new BoardingHandle(Guid.NewGuid(), Guid.NewGuid());
        var crew = new Dictionary<string, int> { ["Marine"] = 2 };
        var rooms = new List<BoardingCompartmentSnapshot> { new(0, "Airlock", "Friendly", false, false, 2, 0) };
        BoardingOperationSnapshot Build(float? integrity) => new(new BoardingHandle(target.SessionId, Guid.NewGuid()), target, 1,
            BoardingPhase.Active, false, true, integrity, 100, null, crew, rooms, null);
        var snapshot = Build(50); crew["Marine"] = 9; rooms.Clear();
        Assert.Equal(2, snapshot.AssignedCrew["Marine"]); Assert.Single(snapshot.Compartments); Assert.Null(snapshot.ActivePods);
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(float.PositiveInfinity));
    }
    [Fact]
    public void NestedNativeTypeSpellingMatchesMetadata()
    {
        Assert.Equal("System.Environment/SpecialFolder", VGModAPI.Runtime.NativeTypeName.Canonical(typeof(Environment.SpecialFolder)));
    }
    [Fact]
    public void ForeignThreadAccessIsRejected()
    {
        using var hub = new LifecycleHub((_, _) => { }); hub.SetCapability("boarding-observation", true, "Test bindings."); using var service = new BoardingService(hub, (_, _) => { });
        Exception? error = null;
        var thread = new Thread(() => error = Record.Exception(() => service.GetTargets()));
        thread.Start(); thread.Join();
        Assert.IsType<InvalidOperationException>(error);
    }
}
