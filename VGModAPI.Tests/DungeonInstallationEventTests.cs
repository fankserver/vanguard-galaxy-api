using System;
using System.Collections.Generic;
using System.IO;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class DungeonInstallationEventTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vg-installations-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private static LifecycleHub Hub(Action<string, Exception>? report = null)
    {
        var hub = new LifecycleHub(report ?? ((_, _) => { }));
        hub.SetCapability("session-lifecycle", true, "Bound"); hub.SetCapability("save-outcomes", true, "Bound");
        return hub;
    }
    private static Guid Ready(LifecycleHub hub)
    { var id = hub.Begin(SessionOrigin.NewGame, null); hub.PlayerReady(id); hub.GameplayInitialized(id); return id; }
    private static void Extract(LifecycleHub hub, Guid session, string poi = "station-a")
    { using var scope = hub.EnterServiceDispatch(); hub.Installations.ExtractionStarted(session, id => id == poi); }
    private static IDungeonProvider Provider(LifecycleHub hub, ISaveDataRegistration? registration = null)
    {
        var registry = new DungeonDefinitionRegistry(_ => true, _ => true, _ => true);
        var service = new DungeonContentService(hub, registry, null, null, (_, _) => { });
        return service.AcquireProvider("consumer", registration);
    }

    [Fact]
    public void InstallationSubscribedBeforeCreationSurvivesReloadWithoutConsumerRebinding()
    {
        using var hub = Hub(); using var provider = Provider(hub);
        var station = provider.GetInstallation("station-a"); var count = 0;
        Assert.Same(station, provider.GetInstallation("station-a"));
        station.ExtractionStarted += () => count++;
        var id = Ready(hub);
        Extract(hub, id, "unrelated"); hub.Installations.Tick(); Assert.Equal(0, count);
        Extract(hub, id); hub.Installations.Tick(); Assert.Equal(1, count);
        Extract(hub, id);
        var next = Ready(hub); hub.Installations.Tick(); Assert.Equal(1, count);
        Extract(hub, id); hub.Installations.Tick(); Assert.Equal(1, count);
        Extract(hub, next); hub.Installations.Tick(); Assert.Equal(2, count);
    }

    [Fact]
    public void ExtractionHandlerCanMutateRealCustomSaveDataWithoutAnyConsumerScheduling()
    {
        using var hub = Hub();
        using var storage = new PersistenceService(hub, new GenerationStore(_root), p => p, _ => new string('a', 64));
        using var registration = storage.Register(new PersistenceProvider("consumer", 1, () => new byte[] { 1 }, (_, _) => { }, b => b.Length == 1)).Registration!;
        using var provider = Provider(hub, registration);
        var ran = 0;
        provider.GetInstallation("station-a").ExtractionStarted += () =>
        { Assert.True(registration.CanMutate); Assert.False(hub.IsDispatchingCallbacks); ran++; };
        var id = Ready(hub);
        using (hub.EnterServiceDispatch())
        {
            Assert.False(registration.CanMutate);
            Extract(hub, id); hub.Installations.Tick(); Assert.Equal(0, ran);
        }
        var save = Guid.NewGuid(); var nested = Guid.NewGuid();
        hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveStarted, hub.CurrentSession, save, "slot"));
        hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveStarted, hub.CurrentSession, nested, "slot"));
        hub.Installations.Tick(); Assert.Equal(0, ran);
        hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveSkipped, hub.CurrentSession, nested, "slot"));
        hub.Installations.Tick(); Assert.Equal(0, ran);
        hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveSkipped, hub.CurrentSession, save, "slot"));
        hub.Installations.Tick(); Assert.Equal(1, ran);
    }

    [Fact]
    public void DisposingCustomSaveDataBeforeProviderClosesDeliveryWithoutThrowing()
    {
        var diagnostics = 0;
        using var hub = Hub((_, _) => diagnostics++);
        using var storage = new PersistenceService(hub, new GenerationStore(_root), p => p, _ => new string('a', 64));
        var registration = storage.Register(new PersistenceProvider("consumer", 1, () => new byte[] { 1 }, (_, _) => { }, b => b.Length == 1)).Registration!;
        using var provider = Provider(hub, registration);
        provider.GetInstallation("station-a").ExtractionStarted += () => Assert.Fail("Disposed save data");
        var id = Ready(hub); Extract(hub, id);
        registration.Dispose();
        hub.Installations.Tick(); hub.Installations.Tick();
        Assert.Equal(1, diagnostics);
        provider.Dispose(); hub.Installations.Tick();
        Assert.Equal(1, diagnostics);
    }

    [Fact]
    public void BlockedProviderRetainsReactionWithoutBlockingAnotherProvider()
    {
        using var hub = Hub(); var id = Ready(hub); var gate = new Registration(id); var order = new List<int>();
        using var one = hub.Installations.Get("one", "station-a", gate);
        using var two = hub.Installations.Get("two", "station-a", null);
        one.ExtractionStarted += () => order.Add(1); two.ExtractionStarted += () => order.Add(2);
        Extract(hub, id); hub.Installations.Tick(); Assert.Equal(new[] { 2 }, order);
        gate.Open = true; hub.Installations.Tick(); Assert.Equal(new[] { 2, 1 }, order);
    }

    [Fact]
    public void HandlersAreIsolatedAndReentrantExtractionsWaitUntilNextBoundary()
    {
        var faults = 0; using var hub = Hub((_, _) => { faults++; throw new Exception("logger"); });
        using var provider = Provider(hub); var station = provider.GetInstallation("station-a"); var id = Ready(hub);
        var order = new List<int>(); var nested = false;
        station.ExtractionStarted += () =>
        {
            order.Add(1);
            if (!nested) { nested = true; Extract(hub, id); hub.Installations.Tick(); }
            throw new Exception("consumer");
        };
        station.ExtractionStarted += () => order.Add(2);
        Extract(hub, id); hub.Installations.Tick(); Assert.Equal(new[] { 1, 2 }, order); Assert.Equal(1, faults);
        hub.Installations.Tick(); Assert.Equal(new[] { 1, 2, 1, 2 }, order); Assert.Equal(2, faults);
    }

    [Fact]
    public void UnsubscriptionAndProviderDisposalSuppressAlreadyPendingHandlersWithoutReplay()
    {
        using var hub = Hub(); var provider = Provider(hub); var station = provider.GetInstallation("station-a");
        var id = Ready(hub); var count = 0; Action handler = () => count++;
        station.ExtractionStarted += handler; Extract(hub, id);
        station.ExtractionStarted -= handler;
        station.ExtractionStarted += handler;
        hub.Installations.Tick(); Assert.Equal(0, count);
        Extract(hub, id); provider.Dispose(); hub.Installations.Tick(); Assert.Equal(0, count);
        Assert.Throws<ObjectDisposedException>(() => station.ExtractionStarted += handler);
    }

    [Fact]
    public void SessionReplacementDuringFirstHandlerSuppressesRemainingOldSessionWork()
    {
        using var hub = Hub(); using var provider = Provider(hub); var station = provider.GetInstallation("station-a");
        var id = Ready(hub); var count = 0;
        station.ExtractionStarted += () => Ready(hub);
        station.ExtractionStarted += () => count++;
        Extract(hub, id); hub.Installations.Tick(); Assert.Equal(0, count);
    }

    [Fact]
    public void FaultingIdentityLookupDoesNotSuppressAnotherInstallationsReaction()
    {
        var reports = new List<string>(); using var hub = Hub((owner, _) => reports.Add(owner));
        using var bad = hub.Installations.Get("bad-owner", "bad-id", null);
        using var good = hub.Installations.Get("good-owner", "station-a", null);
        bad.ExtractionStarted += () => Assert.Fail("Failed identity"); var count = 0;
        good.ExtractionStarted += () => count++;
        var id = Ready(hub);
        hub.Installations.ExtractionStarted(id, poi => poi == "bad-id" ? throw new InvalidOperationException("lookup") : true);
        hub.Installations.Tick();
        Assert.Equal(new[] { "bad-owner" }, reports); Assert.Equal(1, count);
    }

    [Fact]
    public void SessionReplacementCannotClearAnUnfinishedNativeSaveBarrier()
    {
        using var hub = Hub(); using var provider = Provider(hub); var count = 0;
        provider.GetInstallation("station-a").ExtractionStarted += () => count++;
        Ready(hub); var originalSession = hub.CurrentSession; var operation = Guid.NewGuid();
        hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveStarted, originalSession, operation, "slot"));
        var next = Ready(hub); Extract(hub, next);
        hub.Installations.Tick(); Assert.Equal(0, count);
        hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveSkipped, originalSession, operation, "slot"));
        hub.Installations.Tick(); Assert.Equal(1, count);
    }

    [Fact]
    public void UnavailableObservationIsDiagnosedOnceAndDoesNotGrantPermission()
    {
        var reports = 0; using var hub = Hub((_, _) => reports++); using var provider = Provider(hub);
        var count = 0; provider.GetInstallation("station-a").ExtractionStarted += () => count++;
        var id = Ready(hub); Extract(hub, id);
        hub.SetCapability("save-outcomes", false, "Fault");
        hub.Installations.Tick(); hub.Installations.Tick();
        Assert.Equal(1, reports); Assert.Equal(0, count);
        hub.SetCapability("save-outcomes", true, "Recovered"); hub.Installations.Tick(); Assert.Equal(1, count);
    }

    [Fact]
    public void AccessIsThreadBoundAndApiShutdownClearsPendingReactions()
    {
        using var hub = Hub(); using var provider = Provider(hub); var station = provider.GetInstallation("station-a");
        station.ExtractionStarted += () => Assert.Fail("Stopped"); var id = Ready(hub); Extract(hub, id);
        Assert.IsType<InvalidOperationException>(ServiceNotificationTests.OnWorker(() => _ = station.PoiId));
        hub.Dispose(); hub.Installations.Tick();
    }

    private sealed class Registration : ISaveDataRegistration
    {
        private readonly Guid _session;
        internal bool Open;
        internal Registration(Guid session) { _session = session; }
        public SaveDataState State => Open ? new(SaveDataStateKind.Ready, _session) : new(SaveDataStateKind.Blocked, _session, SaveDataBlockReason.CaptureFailed);
        public bool CanRead => Open;
        public bool CanMutate => Open;
        public event Action<SaveDataState>? StateChanged { add { } remove { } }
        public void Dispose() { }
    }
}
