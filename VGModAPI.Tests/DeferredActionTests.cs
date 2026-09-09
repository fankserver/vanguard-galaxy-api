using System;
using System.Collections.Generic;
using System.IO;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class DeferredActionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vg-actions-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private static LifecycleHub Bound(Action<string, Exception>? report = null)
    {
        var hub = new LifecycleHub(report ?? ((_, _) => { }));
        hub.SetCapability("session-lifecycle", true, "Bound");
        hub.SetCapability("save-outcomes", true, "Bound");
        return hub;
    }
    private static Guid Ready(LifecycleHub hub)
    {
        var id = hub.Begin(SessionOrigin.NewGame, null);
        hub.PlayerReady(id); hub.GameplayInitialized(id); return id;
    }
    private PersistenceService Storage(LifecycleHub hub) => new(hub, new GenerationStore(_root), p => p, _ => new string('a', 64));
    private static ISaveDataRegistration Register(PersistenceService storage) => storage.Register(new PersistenceProvider(
        "consumer", 1, () => new byte[] { 1 }, (_, _) => { }, b => b.Length == 1)).Registration!;

    [Fact]
    public void ObservationCannotMutateButAdjacentReactionRunsWithRealRegistrationGateOpen()
    {
        using var hub = Bound();
        using var storage = Storage(hub);
        using var registration = Register(storage);
        var id = Ready(hub);
        using var events = new ServiceNotifications<int>(hub.CheckThread, hub.ReportSubscriberFailure, hub.EnterServiceDispatch);
        var order = new List<string>();
        events.Add(_ =>
        {
            Assert.False(registration.CanMutate);
            hub.Actions.Defer("consumer", id, () =>
            {
                Assert.True(registration.CanMutate); Assert.False(hub.IsDispatchingCallbacks); order.Add("action");
            }, outcome => { Assert.Equal(DeferredActionOutcome.Executed, outcome); Assert.True(hub.IsDispatchingCallbacks); order.Add("complete"); }, registration);
            hub.Actions.Tick();
            order.Add("observer");
        });
        events.Publish(1);
        Assert.Equal(new[] { "observer" }, order);
        hub.Actions.Tick();
        Assert.Equal(new[] { "observer", "action", "complete" }, order);
    }

    [Fact]
    public void HoldsAcrossNestedSaveOperationsAndRunsAfterAllTerminalNotifications()
    {
        using var hub = Bound();
        using var storage = Storage(hub); using var registration = Register(storage);
        var id = Ready(hub); var first = Guid.NewGuid(); var second = Guid.NewGuid(); var ran = 0;
        hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveStarted, hub.CurrentSession, first, "slot"));
        hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveStarted, hub.CurrentSession, second, "slot"));
        hub.Actions.Defer("consumer", id, () => { Assert.True(registration.CanMutate); ran++; }, _ => { }, registration);
        hub.Actions.Defer("stateless", id, () => ran++, _ => { });
        hub.Actions.Tick(); Assert.Equal(0, ran);
        hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveSkipped, hub.CurrentSession, second, "slot"));
        hub.Actions.Tick(); Assert.Equal(0, ran);
        hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveSkipped, hub.CurrentSession, first, "slot"));
        Assert.Equal(0, ran); hub.Actions.Tick(); Assert.Equal(2, ran);
    }

    [Fact]
    public void DurableRegistrationBlockIsReportedRatherThanExecuting()
    {
        using var hub = Bound(); using var storage = Storage(hub); var registration = Register(storage);
        var id = Ready(hub); var outcomes = new List<DeferredActionOutcome>();
        hub.Actions.Defer("consumer", id, () => Assert.Fail("must not run"), outcomes.Add, registration);
        registration.Dispose(); hub.Actions.Tick();
        Assert.Equal(new[] { DeferredActionOutcome.SaveDataBlocked }, outcomes);
    }

    [Fact]
    public void ReplacementDropsOldWorkAndDoesNotBindStaleEventToNewSession()
    {
        using var hub = Bound(); var id = Ready(hub); var outcomes = new List<DeferredActionOutcome>();
        hub.Actions.Defer("consumer", id, () => Assert.Fail("stale"), outcomes.Add);
        var next = Ready(hub);
        Assert.Equal(new[] { DeferredActionOutcome.SessionEnded }, outcomes);
        hub.Actions.Defer("consumer", id, () => Assert.Fail("stale event"), outcomes.Add);
        hub.Actions.Defer("consumer", next, () => { }, outcomes.Add);
        hub.Actions.Tick();
        Assert.Equal(new[] { DeferredActionOutcome.SessionEnded, DeferredActionOutcome.SessionEnded, DeferredActionOutcome.Executed }, outcomes);
    }

    [Fact]
    public void PreservesOwnerOrderWhileWaitingDoesNotBlockAnotherConsumer()
    {
        using var hub = Bound(); var id = Ready(hub); var ready = false; var order = new List<int>();
        hub.Actions.Defer("one", id, () => order.Add(1), _ => { }, canRun: () => ready);
        hub.Actions.Defer("one", id, () => order.Add(2), _ => { });
        hub.Actions.Defer("two", id, () => order.Add(3), _ => { });
        hub.Actions.Tick(); Assert.Equal(new[] { 3 }, order);
        ready = true; hub.Actions.Tick(); Assert.Equal(new[] { 3, 1, 2 }, order);
    }

    [Fact]
    public void FaultsAreIsolatedAndActionOrCompletionEnqueuesWaitUntilNextTick()
    {
        var reports = 0;
        using var hub = Bound((_, _) => { reports++; throw new Exception("logger"); }); var id = Ready(hub);
        var order = new List<int>(); var outcomes = new List<DeferredActionOutcome>();
        hub.Actions.Defer("one", id, () =>
        {
            order.Add(1); hub.Actions.Defer("one", id, () => order.Add(3), outcomes.Add);
            hub.Actions.Tick(); throw new Exception("action");
        }, outcome =>
        {
            outcomes.Add(outcome); hub.Actions.Defer("one", id, () => order.Add(4), outcomes.Add);
            throw new Exception("completion");
        });
        hub.Actions.Defer("one", id, () => order.Add(2), outcomes.Add);
        hub.Actions.Tick(); Assert.Equal(new[] { 1, 2 }, order); Assert.Equal(2, reports);
        hub.Actions.Tick(); Assert.Equal(new[] { 1, 2, 3, 4 }, order);
        Assert.Equal(new[] { DeferredActionOutcome.Faulted, DeferredActionOutcome.Executed, DeferredActionOutcome.Executed, DeferredActionOutcome.Executed }, outcomes);
    }

    [Fact]
    public void CancellationAndShutdownCompleteExactlyOnceAndCapacityRefusalIsSynchronous()
    {
        using var hub = Bound(); var id = Ready(hub); var outcomes = new List<DeferredActionOutcome>();
        var handle = hub.Actions.Defer("one", id, () => Assert.Fail("cancelled"), outcomes.Add);
        handle.Dispose(); handle.Dispose();
        Assert.Equal(new[] { DeferredActionOutcome.Cancelled }, outcomes);
        for (var i = 0; i < DeferredActionService.Capacity; i++) hub.Actions.Defer("one", id, () => { }, _ => { });
        hub.Actions.Defer("two", id, () => Assert.Fail("full"), outcomes.Add);
        Assert.Equal(DeferredActionOutcome.QueueFull, outcomes[1]);
        hub.Actions.Dispose();
        hub.Actions.Defer("one", id, () => Assert.Fail("stopped"), outcomes.Add);
        Assert.Equal(DeferredActionOutcome.Unavailable, outcomes[2]);
    }

    [Fact]
    public void PredicatesAreObservationalAndSessionChangesInsideThemPreventAction()
    {
        using var hub = Bound(); var id = Ready(hub); var outcomes = new List<DeferredActionOutcome>();
        hub.Actions.Defer("one", id, () => Assert.Fail("invalidated"), outcomes.Add, canRun: () =>
        { Assert.True(hub.IsDispatchingCallbacks); hub.Invalidate("menu"); return true; });
        hub.Actions.Tick(); Assert.Equal(new[] { DeferredActionOutcome.SessionEnded }, outcomes);
    }

    [Fact]
    public void BatchIsBoundedAndWrongThreadAccessRejected()
    {
        using var hub = Bound(); var id = Ready(hub); var count = 0;
        for (var i = 0; i < DeferredActionService.BatchSize + 1; i++) hub.Actions.Defer("one", id, () => count++, _ => { });
        hub.Actions.Tick(); Assert.Equal(DeferredActionService.BatchSize, count);
        hub.Actions.Tick(); Assert.Equal(DeferredActionService.BatchSize + 1, count);
        Assert.IsType<InvalidOperationException>(ServiceNotificationTests.OnWorker(() => hub.Actions.Defer("one", id, () => { }, _ => { })));
    }
}
